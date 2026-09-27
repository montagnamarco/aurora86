using System.Buffers.Binary;
using Aurora386.Core.Devices.Interrupts;
using Aurora386.Core.Devices.Pci;
using Aurora386.Core.IO;

namespace Aurora386.Core.Devices.Chipset;

/// <summary>
/// The 1997 Intel 82371AB PIIX4 function 3 power-management register block.
/// Firmware assigns PMBA at configuration offset 40h and enables its 64 ports
/// with PMREGMISC bit 0, independently of PCI command I/O enable. The timer
/// follows emulated CPU cycles.
///
/// From work package P4.5 on this block also carries the ACPI runtime a
/// PIIX4-era BIOS builds tables for (plans/specs/acpi.md §4): the GPE0 pair
/// at PMBA+0Ch, the fixed SMI_CMD port B2h that toggles PM1_CNT.SCI_EN in
/// place of a real SMI (there is no SMM here to trap through instead), the
/// SCI itself on IRQ 9 -- recomputed after every register write that could
/// change it -- and the SLP_EN write-only strobe that powers the machine off
/// for the one sleep state (\_S5) the DSDT defines.
/// </summary>
public sealed class Piix4PowerManagement : IIoDwordDevice
{
    public const int WindowLength = 64;

    /// <summary>
    /// The board's own fixed PMBA choice (plans/specs/acpi.md's own
    /// "Repository facts" section): named here, rather than left as a
    /// literal at <c>Machine</c>'s own construction call, so the ACPI tables
    /// <c>Machine.BuildBiosImage</c> builds from the same board can read it
    /// back rather than keeping an independent copy of "B000h" that could
    /// silently drift from the device's own port.
    /// </summary>
    public const ushort DefaultPmBase = 0xB000;

    /// <summary>
    /// ACPI 1.0b §4.7.2.1: the fixed port an ACPI OS writes to move the
    /// platform in or out of ACPI mode. A real PIIX4 traps this through SMM;
    /// this machine has none, so the write acts on PM1_CNT.SCI_EN directly.
    /// Registered once, unconditionally, in the constructor -- which only
    /// runs on the i430TX board to begin with (<c>Machine.Piix4Power</c> is
    /// null on i430FX), so no separate chipset check is needed here.
    /// </summary>
    public const ushort SmiCommandPort = 0xB2;

    private const byte AcpiEnableCommand = 0xF1;
    private const byte AcpiDisableCommand = 0xF0;

    /// <summary>The legacy ISA line the SCI shares with nothing else on this board; POST marks it level-triggered in the ELCR (decision C4).</summary>
    private const int SciIrq = 9;

    private const int BaseAddressOffset = 0x40;
    private const uint BaseAddressMask = 0xFFC0;
    private const int DecodeEnableOffset = 0x80;
    private const int StatusOffset = 0x00;
    private const int EnableOffset = 0x02;
    private const int ControlOffset = 0x04;
    private const int TimerOffset = 0x08;

    /// <summary>GPE0_STS, ACPI 1.0b §4.7.1.6: two bytes at PMBA+0Ch. Nothing in this machine ever wires a general-purpose event to it, so it stays zero forever (test case 13); kept as ordinary W1C storage rather than a hardcoded constant so a future GPE source has somewhere to land.</summary>
    private const int GpeStatusOffset = 0x0C;

    /// <summary>GPE0_EN, two bytes at PMBA+0Eh: ordinary read/write storage. It can never contribute a set SCI term (GPE0_STS never sets a bit), but an ACPI OS still expects to read back whatever it wrote.</summary>
    private const int GpeEnableOffset = 0x0E;

    private const ushort TimerOverflowStatus = 1;
    private const ushort StatusMask = 0x8D31; // Resume, override, RTC, button, global, bus master, timer.
    private const ushort EnableMask = 0x0521; // RTC, button, global and timer interrupt enables.
    private const ushort ControlMask = 0x1C07; // Suspend type, global release, bus-master reload, SCI.

    /// <summary>Nothing wires a GPE0 input in this codebase (see <see cref="GpeStatusOffset"/>'s own note): every status bit is always clear, so the write mask that would let a guest set one is empty.</summary>
    private const ushort GpeStatusMask = 0x0000;

    /// <summary>GPE0_EN has no wired input either, but it is otherwise unconstrained storage -- an ACPI OS is entitled to see back exactly what it wrote.</summary>
    private const ushort GpeEnableMask = 0xFFFF;

    /// <summary>PM1_CNT bit 0 (ACPI 1.0b §4.7.3.2.1): SCI_EN. Set by an ACPI OS, or by SMI_CMD's own ACPI-enable command in this SMM-less machine.</summary>
    private const ushort SciEnableBit = 0x0001;

    /// <summary>
    /// PM1_CNT bit 13, SLP_EN: a write-only strobe that always reads 0
    /// (§4.7.3.2.1). <see cref="ControlMask"/> deliberately excludes it from
    /// <see cref="_control"/>'s own persisted bits -- the strobe is caught on
    /// the way in, acted on once, and never stored.
    /// </summary>
    private const ushort SleepEnableBit = 0x2000;

    private const int SleepTypeShift = 10;      // PM1_CNT bits 12:10, SLP_TYPx.
    private const ushort SleepTypeMask = 0x0007;

    /// <summary>The only sleep state \_S5 in the DSDT ever names (decision C6): SLP_TYP 0 means "power off", nothing else is defined.</summary>
    private const int SleepTypeS5 = 0;

    /// <summary>PM1_STS bit 8, PWRBTN_STS: already part of <see cref="StatusMask"/>, named again here for <see cref="RaisePowerButton"/>.</summary>
    private const ushort PowerButtonStatusBit = 0x0100;

    private const uint TimerFrequencyHz = 3_579_545;
    private const uint TimerMask = 0x00FF_FFFF;
    private const int SnapshotLengthVersion1 = 22;
    private const int SnapshotLengthVersion2 = SnapshotLengthVersion1 + 2 + 2;   // GPE0 STS and EN.

    private readonly Func<long> _cpuCycles;
    private readonly Func<int> _cpuFrequencyHz;
    private readonly InterruptController _interrupts;
    private readonly Action _requestPowerOff;
    private readonly IoBus _io;
    private ushort? _mappedBase;
    private long _lastCpuCycles;
    private uint _timerRemainder;
    private int _timerCpuFrequency;
    private ulong _lastTimerTicks;
    private ushort _status;
    private ushort _enable;
    private ushort _control;
    private ushort _gpeStatus;
    private ushort _gpeEnable;

    /// <summary>Whether the SCI line is currently asserted, so <see cref="UpdateSci"/> only calls <see cref="InterruptController.RaiseIrq"/>/<see cref="InterruptController.LowerIrq"/> on an actual change of a level line, exactly as every other level-triggered device in this codebase does.</summary>
    private bool _sciAsserted;

    public Piix4PowerManagement(
        PciFunction function,
        IoBus io,
        ushort ioAddress,
        InterruptController interrupts,
        Action requestPowerOff,
        Func<long> cpuCycles,
        Func<int> cpuFrequencyHz)
    {
        Function = function;
        _io = io;
        _interrupts = interrupts;
        _requestPowerOff = requestPowerOff;
        _cpuCycles = cpuCycles;
        _cpuFrequencyHz = cpuFrequencyHz;
        Function.Define(BaseAddressOffset, (ioAddress & BaseAddressMask) | 1, 4, BaseAddressMask);
        Function.Define(DecodeEnableOffset, 0, 1, 1);
        Function.ConfigurationChanged += UpdateMapping;
        _io.Register(SmiCommandPort, this);
        Reset();
    }

    public PciFunction Function { get; }

    /// <summary>PM1_CNT.SCI_EN: true once an ACPI-aware guest (or SMI_CMD's own F1h command) has taken charge of the machine's power state.</summary>
    public bool SciEnabled => (_control & SciEnableBit) != 0;

    /// <summary>
    /// Enables the PM I/O decode at PMBA -- PMREGMISC bit 0 (Intel 82371AB
    /// §7.1), reset to 0 on real silicon and off again after every <see
    /// cref="Devices.Pci.PciBus.Reset"/> here too. <c>Machine.RunPost</c>
    /// calls this once, after that reset, the same way a real BIOS enables
    /// every one of the chipset's own fixed hardware register blocks during
    /// POST: an ACPI OS trusts the FADT's PM1a_EVT_BLK and its neighbours to
    /// already answer, rather than negotiating this decode through PCI
    /// configuration space itself the way an ordinary BAR is sized and
    /// enabled.
    /// </summary>
    public void EnablePmDecode() => Function.Write(DecodeEnableOffset, 1, 1);

    /// <summary>The 24-bit, 3.579545 MHz counter, advancing only with emulated cycles.</summary>
    public uint TimerValue
    {
        get
        {
            UpdateTimer();
            return (uint)(_lastTimerTicks & TimerMask);
        }
    }

    /// <summary>Resets the local registers and timer after the machine resets its CPU clock.</summary>
    public void Reset()
    {
        _status = 0;
        _enable = 0;
        _control = 0;
        _gpeStatus = 0;
        _gpeEnable = 0;
        _lastCpuCycles = _cpuCycles();
        _timerRemainder = 0;
        _timerCpuFrequency = ReadCpuFrequency();
        _lastTimerTicks = 0;
        UpdateMapping();

        // SCI_EN is 0 again, so the SCI line must drop with it -- POST must
        // not leave IRQ 9 asserted from before a reboot.
        UpdateSci();
    }

    public byte ReadByte(ushort port)
    {
        if (port == SmiCommandPort)
        {
            // Write-only in practice: with no SMM to answer through, nothing
            // here defines a meaningful read-back for this port.
            return 0;
        }

        int offset = Offset(port);
        if (offset >= TimerOffset && offset < TimerOffset + 4)
        {
            return (byte)(TimerValue >> ((offset - TimerOffset) * 8));
        }

        UpdateTimer();
        return (byte)(ReadRegister(offset & ~1) >> ((offset & 1) * 8));
    }

    public ushort ReadWord(ushort port)
    {
        int offset = Offset(port);
        if (offset == TimerOffset || offset == TimerOffset + 2)
        {
            return (ushort)(TimerValue >> ((offset - TimerOffset) * 8));
        }

        return (ushort)(ReadByte(port) | (ReadByte((ushort)(port + 1)) << 8));
    }

    public uint ReadDword(ushort port)
    {
        if (Offset(port) == TimerOffset)
        {
            return TimerValue;
        }

        return (uint)ReadWord(port) | ((uint)ReadWord((ushort)(port + 2)) << 16);
    }

    public void WriteByte(ushort port, byte value)
    {
        if (port == SmiCommandPort)
        {
            // ACPI 1.0b §4.7.2.1: SMI_CMD moves the platform in or out of
            // ACPI mode. A real PIIX4 traps this through SMM and has the SMI
            // handler set SCI_EN itself; this machine has no SMM, so the
            // write acts on PM1_CNT.SCI_EN directly. Any other command byte
            // (there is no SMM here to define one) is simply ignored.
            if (value == AcpiEnableCommand)
            {
                _control |= SciEnableBit;
            }
            else if (value == AcpiDisableCommand)
            {
                _control &= unchecked((ushort)~SciEnableBit);
            }

            UpdateSci();
            return;
        }

        UpdateTimer();
        int offset = Offset(port);
        int shift = (offset & 1) * 8;
        ushort written = (ushort)(value << shift);
        int registerOffset = offset & ~1;
        if (registerOffset == StatusOffset)
        {
            // W1C must use only the written lane, never the untouched status byte.
            _status &= (ushort)~(written & StatusMask);
            UpdateSci();
            return;
        }

        if (registerOffset == GpeStatusOffset)
        {
            // Same W1C shape as PM1_STS above; GpeStatusMask is empty today
            // (test case 13), so this can never actually clear a set bit.
            _gpeStatus &= (ushort)~(written & GpeStatusMask);
            UpdateSci();
            return;
        }

        ushort combined = (ushort)((ReadRegister(registerOffset) & ~(0xFF << shift)) | written);
        switch (registerOffset)
        {
            case EnableOffset:
                _enable = (ushort)(combined & EnableMask);
                UpdateSci();
                break;
            case ControlOffset:
                // The SLP_EN strobe (bit 13) has to be read out of the
                // would-be combined value before ControlMask drops it --
                // that mask is what keeps it from ever being persisted, so
                // this is the only moment the write is visible at all.
                bool sleepEnableRequested = (combined & SleepEnableBit) != 0;
                _control = (ushort)(combined & ControlMask);
                if (sleepEnableRequested)
                {
                    HandleSleepEnable();
                }

                UpdateSci();
                break;
            case GpeEnableOffset:
                _gpeEnable = (ushort)(combined & GpeEnableMask);
                UpdateSci();
                break;
        }
    }

    public void WriteWord(ushort port, ushort value)
    {
        WriteByte(port, (byte)value);
        WriteByte((ushort)(port + 1), (byte)(value >> 8));
    }

    public void WriteDword(ushort port, uint value)
    {
        WriteWord(port, (ushort)value);
        WriteWord((ushort)(port + 2), (ushort)(value >> 16));
    }

    /// <summary>
    /// The newest section version this build writes: version 1 is the block
    /// as it stood before this package (22 bytes, no GPE0); version 2 adds
    /// GPE0 STS/EN (26 bytes). <see cref="MachineSnapshot"/> keeps reading
    /// version 1 files, restoring GPE0 at its own reset value for them.
    /// </summary>
    public const int SnapshotVersion = 2;

    /// <summary>Captures register storage and timer phase; PCI configuration is captured by the bus.</summary>
    internal byte[] Capture()
    {
        UpdateTimer();
        byte[] state = new byte[SnapshotLengthVersion2];
        BinaryPrimitives.WriteUInt64LittleEndian(state, _lastTimerTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(8), _timerRemainder);
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(12), _timerCpuFrequency);
        BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(16), _status);
        BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(18), _enable);
        BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(20), _control);
        BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(22), _gpeStatus);
        BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(24), _gpeEnable);
        return state;
    }

    /// <summary>Validates the complete local snapshot before restoring any register.</summary>
    internal static void ValidateSnapshot(ReadOnlySpan<byte> state, int version)
    {
        int expectedLength = version >= 2 ? SnapshotLengthVersion2 : SnapshotLengthVersion1;
        if (state.Length != expectedLength)
        {
            throw new InvalidDataException("Invalid PIIX4 power-management state length.");
        }

        ulong ticks = BinaryPrimitives.ReadUInt64LittleEndian(state);
        uint remainder = BinaryPrimitives.ReadUInt32LittleEndian(state[8..]);
        int frequency = BinaryPrimitives.ReadInt32LittleEndian(state[12..]);
        ushort status = BinaryPrimitives.ReadUInt16LittleEndian(state[16..]);
        ushort enable = BinaryPrimitives.ReadUInt16LittleEndian(state[18..]);
        ushort control = BinaryPrimitives.ReadUInt16LittleEndian(state[20..]);
        if (ticks > TimerMask || frequency <= 0 || remainder >= frequency ||
            (status & ~StatusMask) != 0 ||
            (enable & ~EnableMask) != 0 || (control & ~ControlMask) != 0)
        {
            throw new InvalidDataException("Invalid PIIX4 power-management register state.");
        }

        if (version >= 2)
        {
            ushort gpeStatus = BinaryPrimitives.ReadUInt16LittleEndian(state[22..]);
            ushort gpeEnable = BinaryPrimitives.ReadUInt16LittleEndian(state[24..]);
            if ((gpeStatus & ~GpeStatusMask) != 0 || (gpeEnable & ~GpeEnableMask) != 0)
            {
                throw new InvalidDataException("Invalid PIIX4 power-management register state.");
            }
        }
    }

    /// <summary>Restores local state after the container validates all device sections.</summary>
    internal void Restore(ReadOnlySpan<byte> state, int version)
    {
        ValidateSnapshot(state, version);
        // CPU.Cycles is not restored by the CPU snapshot; rebase the saved phase.
        _lastCpuCycles = _cpuCycles();
        _lastTimerTicks = BinaryPrimitives.ReadUInt64LittleEndian(state);
        _timerRemainder = BinaryPrimitives.ReadUInt32LittleEndian(state[8..]);
        _timerCpuFrequency = BinaryPrimitives.ReadInt32LittleEndian(state[12..]);
        _status = BinaryPrimitives.ReadUInt16LittleEndian(state[16..]);
        _enable = BinaryPrimitives.ReadUInt16LittleEndian(state[18..]);
        _control = BinaryPrimitives.ReadUInt16LittleEndian(state[20..]);

        // A file from before GPE0 existed in this section (version 1)
        // restores it at its own reset value, the same tolerance every
        // other section added after the container's own versioning gets.
        _gpeStatus = version >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(state[22..]) : (ushort)0;
        _gpeEnable = version >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(state[24..]) : (ushort)0;
        UpdateMapping();
        UpdateSci();
    }

    /// <summary>Returns the register offset only while the PM window is decoded.</summary>
    private int Offset(ushort port)
    {
        return _mappedBase is { } address ? port - address : -1;
    }

    /// <summary>Reads implemented register storage; unimplemented PM registers return zero.</summary>
    private ushort ReadRegister(int offset)
    {
        return offset switch
        {
            StatusOffset => _status,
            EnableOffset => _enable,
            ControlOffset => _control,
            GpeStatusOffset => _gpeStatus,
            GpeEnableOffset => _gpeEnable,
            _ => 0,
        };
    }

    /// <summary>
    /// Sets PWRBTN_STS (PM1_STS bit 8) and lets <see cref="UpdateSci"/> raise
    /// the SCI if PM1_EN.PWRBTN_EN and SCI_EN both agree -- the ACPI-mode
    /// half of <c>Machine.PressPowerButton</c> (decision C5).
    /// </summary>
    internal void RaisePowerButton()
    {
        _status |= PowerButtonStatusBit;
        UpdateSci();
    }

    /// <summary>
    /// PM1_CNT §4.7.3.2.1: SLP_EN with SLP_TYP = 0 asks for \_S5, the only
    /// sleep state this DSDT ever names (decision C6) -- every other SLP_TYP
    /// value the guest could still write (there being no \_S1/\_S3/\_S4 in
    /// the namespace for an ACPI-aware OS to have asked for one) is accepted
    /// and simply does nothing, since nothing here defines what it would mean.
    /// </summary>
    private void HandleSleepEnable()
    {
        int sleepType = (_control >> SleepTypeShift) & SleepTypeMask;
        if (sleepType == SleepTypeS5)
        {
            _requestPowerOff();
        }
    }

    /// <summary>
    /// Recomputes the SCI as a level line and raises or lowers IRQ 9 only on
    /// an actual change, exactly as <c>InterruptController</c>'s other
    /// level-triggered callers do. Runtime §4: the SCI is
    /// <c>(PM1_STS &amp; PM1_EN) | (GPE0_STS &amp; GPE0_EN)</c>, asserted only
    /// while SCI_EN is set -- an ACPI OS not yet in charge must never see
    /// this legacy-mode line move on its own.
    /// </summary>
    private void UpdateSci()
    {
        bool active = SciEnabled &&
            (((_status & _enable) != 0) || ((_gpeStatus & _gpeEnable) != 0));

        if (active == _sciAsserted) return;

        _sciAsserted = active;
        if (active)
        {
            _interrupts.RaiseIrq(SciIrq);
        }
        else
        {
            _interrupts.LowerIrq(SciIrq);
        }
    }

    /// <summary>Samples the crystal counter without losing precision in a long-running machine.</summary>
    private void UpdateTimer()
    {
        long currentCycles = _cpuCycles();
        long elapsed = Math.Max(0, currentCycles - _lastCpuCycles);
        int frequency = ReadCpuFrequency();
        if (frequency != _timerCpuFrequency)
        {
            // Models keep a fixed clock. If an embedding changes it, retain the
            // fractional tick to within 1/frequency tick and use the new rate
            // for cycles since the previous sample; callers should sample first.
            _timerRemainder = (uint)((ulong)_timerRemainder * (uint)frequency /
                (uint)_timerCpuFrequency);
        }

        UInt128 scaledCycles = (UInt128)(ulong)elapsed * TimerFrequencyHz + _timerRemainder;
        UInt128 ticks = _lastTimerTicks + scaledCycles / (uint)frequency;
        // Intel 82371AB section 7.2.4 sets status at both transitions of bit 23.
        if ((ticks >> 23) != (_lastTimerTicks >> 23))
        {
            _status |= TimerOverflowStatus;
        }

        _lastTimerTicks = (ulong)(ticks & TimerMask);
        _timerRemainder = (uint)(scaledCycles % (uint)frequency);
        _timerCpuFrequency = frequency;
        _lastCpuCycles = currentCycles;

        // TMR_STS can only ever change here, lazily, on the next access to
        // any PM register (test case 10) -- there is no ticking clock event
        // of its own, so the SCI has to be re-checked from this same spot.
        UpdateSci();
    }

    /// <summary>Rejects an invalid host-provided clock before attempting timer division.</summary>
    private int ReadCpuFrequency()
    {
        int frequency = _cpuFrequencyHz();
        if (frequency <= 0)
        {
            throw new InvalidOperationException("The emulated CPU frequency must be positive.");
        }

        return frequency;
    }

    /// <summary>Moves the chipset-specific PMBA decode without overwriting another port owner.</summary>
    private void UpdateMapping()
    {
        if (_mappedBase is { } oldAddress)
        {
            _io.Unregister(oldAddress, WindowLength, this);
        }

        _mappedBase = null;
        if ((Function.Read(DecodeEnableOffset, 1) & 1) == 0)
        {
            return;
        }

        ushort address = (ushort)(Function.Read(BaseAddressOffset, 4) & BaseAddressMask);
        if (!_io.IsFree(address, WindowLength, this))
        {
            return;
        }

        _mappedBase = address;
        _io.Register(address, WindowLength, this);
    }
}
