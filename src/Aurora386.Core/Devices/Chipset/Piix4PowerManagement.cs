using System.Buffers.Binary;
using Aurora386.Core.Devices.Pci;
using Aurora386.Core.IO;

namespace Aurora386.Core.Devices.Chipset;

/// <summary>
/// The 1997 Intel 82371AB PIIX4 function 3 power-management register block.
/// Firmware assigns PMBA at configuration offset 40h and enables its 64 ports
/// with PMREGMISC bit 0, independently of PCI command I/O enable. The timer
/// follows emulated CPU cycles. This minimal block supplies register storage
/// and timer overflow status, but no SCI, SMI, suspend actions or ACPI tables.
/// </summary>
public sealed class Piix4PowerManagement : IIoDwordDevice
{
    public const int WindowLength = 64;

    private const int BaseAddressOffset = 0x40;
    private const uint BaseAddressMask = 0xFFC0;
    private const int DecodeEnableOffset = 0x80;
    private const int StatusOffset = 0x00;
    private const int EnableOffset = 0x02;
    private const int ControlOffset = 0x04;
    private const int TimerOffset = 0x08;
    private const ushort TimerOverflowStatus = 1;
    private const ushort StatusMask = 0x8D31; // Resume, override, RTC, button, global, bus master, timer.
    private const ushort EnableMask = 0x0521; // RTC, button, global and timer interrupt enables.
    private const ushort ControlMask = 0x1C07; // Suspend type, global release, bus-master reload, SCI.
    private const uint TimerFrequencyHz = 3_579_545;
    private const uint TimerMask = 0x00FF_FFFF;
    private const int SnapshotLength = 22;

    private readonly Func<long> _cpuCycles;
    private readonly Func<int> _cpuFrequencyHz;
    private readonly IoBus _io;
    private ushort? _mappedBase;
    private long _lastCpuCycles;
    private uint _timerRemainder;
    private int _timerCpuFrequency;
    private ulong _lastTimerTicks;
    private ushort _status;
    private ushort _enable;
    private ushort _control;

    public Piix4PowerManagement(
        PciFunction function,
        IoBus io,
        ushort ioAddress,
        Func<long> cpuCycles,
        Func<int> cpuFrequencyHz)
    {
        Function = function;
        _io = io;
        _cpuCycles = cpuCycles;
        _cpuFrequencyHz = cpuFrequencyHz;
        Function.Define(BaseAddressOffset, (ioAddress & BaseAddressMask) | 1, 4, BaseAddressMask);
        Function.Define(DecodeEnableOffset, 0, 1, 1);
        Function.ConfigurationChanged += UpdateMapping;
        Reset();
    }

    public PciFunction Function { get; }

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
        _lastCpuCycles = _cpuCycles();
        _timerRemainder = 0;
        _timerCpuFrequency = ReadCpuFrequency();
        _lastTimerTicks = 0;
        UpdateMapping();
    }

    public byte ReadByte(ushort port)
    {
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
        UpdateTimer();
        int offset = Offset(port);
        int shift = (offset & 1) * 8;
        ushort written = (ushort)(value << shift);
        int registerOffset = offset & ~1;
        if (registerOffset == StatusOffset)
        {
            // W1C must use only the written lane, never the untouched status byte.
            _status &= (ushort)~(written & StatusMask);
            return;
        }

        ushort combined = (ushort)((ReadRegister(registerOffset) & ~(0xFF << shift)) | written);
        switch (registerOffset)
        {
            case EnableOffset:
                _enable = (ushort)(combined & EnableMask);
                break;
            case ControlOffset:
                _control = (ushort)(combined & ControlMask);
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

    /// <summary>Captures register storage and timer phase; PCI configuration is captured by the bus.</summary>
    internal byte[] Capture()
    {
        UpdateTimer();
        byte[] state = new byte[SnapshotLength];
        BinaryPrimitives.WriteUInt64LittleEndian(state, _lastTimerTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(8), _timerRemainder);
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(12), _timerCpuFrequency);
        BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(16), _status);
        BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(18), _enable);
        BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(20), _control);
        return state;
    }

    /// <summary>Validates the complete local snapshot before restoring any register.</summary>
    internal static void ValidateSnapshot(ReadOnlySpan<byte> state)
    {
        if (state.Length != SnapshotLength)
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
    }

    /// <summary>Restores local state after the container validates all device sections.</summary>
    internal void Restore(ReadOnlySpan<byte> state)
    {
        ValidateSnapshot(state);
        // CPU.Cycles is not restored by the CPU snapshot; rebase the saved phase.
        _lastCpuCycles = _cpuCycles();
        _lastTimerTicks = BinaryPrimitives.ReadUInt64LittleEndian(state);
        _timerRemainder = BinaryPrimitives.ReadUInt32LittleEndian(state[8..]);
        _timerCpuFrequency = BinaryPrimitives.ReadInt32LittleEndian(state[12..]);
        _status = BinaryPrimitives.ReadUInt16LittleEndian(state[16..]);
        _enable = BinaryPrimitives.ReadUInt16LittleEndian(state[18..]);
        _control = BinaryPrimitives.ReadUInt16LittleEndian(state[20..]);
        UpdateMapping();
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
            _ => 0,
        };
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
