using Aurora86.Core.Devices.Pci;
using Aurora86.Core.Memory;

namespace Aurora86.Core.Devices.Usb;

/// <summary>
/// Intel ICH9's EHCI 1.0 host controller (PCI 8086:293A, class 0C0320): the
/// USB 2.0 high-speed companion every EHCI-aware OS drives through a
/// memory-mapped register file -- Linux's <c>ehci-hcd</c>, Windows's
/// <c>usbehci.sys</c> -- fetched from guest physical memory exactly as
/// <see cref="UhciController"/> fetches its own frame list, except EHCI's
/// schedule is built from queue heads (QH) and queue element transfer
/// descriptors (qTD) rather than UHCI's flat transfer descriptors: see
/// <c>EhciController.Schedule.cs</c> for that engine and
/// <c>EhciController.Ports.cs</c> for CONFIGFLAG/PORTSC port routing to the
/// UHCI companion this device always ships next to on real ICH silicon.
///
/// This emulation only ever attaches a high-speed device directly to an
/// EHCI port (plan decision 3, <c>plans/usb2-ehci.md</c>): low- and
/// full-speed devices stay on the companion UHCI controller, so there is no
/// split-transaction state machine here, and the periodic schedule never
/// needs an iTD, siTD or FSTN -- a periodic-list or async-list pointer of
/// that type is a malformed guest schedule and raises a host system error,
/// exactly like an out-of-range descriptor address.
///
/// Every guest command still runs to completion synchronously within the
/// microframe that reaches it, exactly as <see cref="UhciController"/> and
/// <c>AhciController</c> already do; there is no queued command outstanding
/// across ticks and no bus-time bandwidth accounting (documented gap shared
/// with UHCI, see <c>docs/USB.md</c>).
/// </summary>
public sealed partial class EhciController : IMemoryDevice
{
    // --- PCI identity (plan decision 1) -----------------------------------------

    private const ushort VendorId = 0x8086; // Intel
    private const ushort DeviceId = 0x293A; // ICH9 EHCI controller
    private const uint ClassCode = 0x0C0320; // serial bus controller (0C), USB (03), EHCI programming interface (20h)
    private const byte InterruptPinIntA = 1;
    private const byte SerialBusReleaseNumber = 0x20; // SBRN: USB 2.0
    private const byte FrameLengthAdjustmentDefault = 0x20; // FLADJ's power-on value; stored only, no functional effect here

    /// <summary>Memory space (bit 1) and bus master (bit 2); there is no I/O BAR to gate with bit 0.</summary>
    private const ushort CommandWritableMask = 0x6;

    // --- BAR0 geometry ---------------------------------------------------------------

    private const int Bar0BarIndex = 0;

    /// <summary>
    /// 4 KB (owner decision, superseding the plan's original 1 KB): matches
    /// QEMU's <c>ehci-pci</c> (its <c>MMIO_SIZE</c>) and, more fundamentally,
    /// is required by this emulator's own bus -- <see cref="PhysicalMemoryBus.TryMapDevice"/>
    /// only ever claims whole 4 KB pages, so a legal 1 KB placement such as
    /// FEBFFC00h would decode nothing at all. Every offset past the
    /// operational block (past <see cref="RegPortStatusBase"/>'s last port)
    /// reads 0, not FFh: it is still inside the mapped page, just reserved.
    /// </summary>
    private const uint Bar0Length = 0x1000;

    // --- capability registers (offsets 00h-1Fh, fixed, never written) ----------

    /// <summary>CAPLENGTH (byte 0) = 20h, reserved byte 1 = 0, HCIVERSION (bytes 2-3) = 0100h: one dword.</summary>
    private const uint CapabilityLengthAndVersion = 0x0100_0020;

    private const uint OperationalBase = 0x20; // CAPLENGTH's value: where the operational registers start

    /// <summary>
    /// HCSPARAMS (offset 04h): N_PORTS=2, PPC=0 (ports are always powered,
    /// see <c>EhciController.Ports.cs</c>'s PORTSC.PP), Port Routing Rules=0,
    /// N_PCC=2, N_CC=1, Port Indicators=0, Debug Port Number=0. Bit layout
    /// per EHCI 1.0 section 2.2.2 (cross-checked against Linux's
    /// ehci_def.h, whose HCS_* macros the field shifts below mirror).
    /// </summary>
    private const uint HcsParamsValue =
        2u                    // N_PORTS, bits 3:0
        | (2u << 8)           // N_PCC, bits 11:8
        | (1u << 12);         // N_CC, bits 15:12

    /// <summary>
    /// HCCPARAMS (offset 08h): 64-bit addressing=0, programmable frame list=0
    /// (fixed 1024-entry periodic list), asynchronous park=0, Isochronous
    /// Scheduling Threshold (IST, bits 7:4) =1, EECP (bits 15:8) =0 -- no
    /// USBLEGSUP capability, so there is no BIOS/OS handoff to emulate
    /// (plan decision 4, legal per EHCI 1.0 section 2.2.4).
    /// </summary>
    private const uint HccParamsValue = 1u << 4;

    // --- USBCMD (op+00h) ---------------------------------------------------------

    private const uint CommandRun = 1u << 0;                          // RS
    private const uint CommandHostControllerReset = 1u << 1;          // HCRESET
    private const uint CommandPeriodicScheduleEnable = 1u << 4;       // PSE
    private const uint CommandAsyncScheduleEnable = 1u << 5;          // ASE
    private const uint CommandInterruptOnAsyncAdvanceDoorbell = 1u << 6; // IAAD
    private const uint CommandInterruptThresholdMask = 0xFFu << 16;   // ITC, bits 23:16: stored, no scheduling effect (see class summary)
    private const uint CommandStorableMask = CommandRun | CommandPeriodicScheduleEnable
        | CommandAsyncScheduleEnable | CommandInterruptThresholdMask;
    private const uint CommandResetDefault = 0x08u << 16; // USBCMD's power-on ITC value (EHCI 1.0 table 2-9)

    // --- USBSTS (op+04h) ----------------------------------------------------------

    private const uint StatusUsbInterrupt = 1u << 0;             // USBINT
    private const uint StatusUsbErrorInterrupt = 1u << 1;        // USBERRINT
    private const uint StatusPortChangeDetect = 1u << 2;         // PCD
    private const uint StatusFrameListRollover = 1u << 3;        // FLR
    private const uint StatusHostSystemError = 1u << 4;          // HSE
    private const uint StatusInterruptOnAsyncAdvance = 1u << 5;  // IAA
    private const uint StatusWriteOneToClearMask = StatusUsbInterrupt | StatusUsbErrorInterrupt
        | StatusPortChangeDetect | StatusFrameListRollover | StatusHostSystemError | StatusInterruptOnAsyncAdvance;
    private const uint StatusReclamation = 1u << 13;              // RECL, RO: bandwidth reclamation is not modelled, always reads 0
    private const uint StatusPeriodicScheduleStatus = 1u << 14;   // PSS, RO: mirrors PSE while running
    private const uint StatusAsyncScheduleStatus = 1u << 15;      // ASS, RO: mirrors ASE while running
    private const uint StatusHalted = 1u << 12;                   // HCHalted, RO: mirrors !RS

    // --- USBINTR (op+08h) ----------------------------------------------------------

    private const uint InterruptEnableMask = StatusWriteOneToClearMask; // the six enable bits mirror USBSTS bits 0-5 one for one

    // --- FRINDEX (op+0Ch) ------------------------------------------------------------

    private const int FrameIndexBits = 14;
    private const ushort FrameIndexMask = (1 << FrameIndexBits) - 1; // 3FFFh
    private const ushort FrameListRolloverBit = 1 << 13;             // the periodic list's own 1024-entry period, in microframes
    private const double MicrosecondsPerMicroframe = 125.0;
    private const int MicroframesPerFrameShift = 3; // 8 microframes per 1 ms frame
    private const int MicroframesPerFrameMask = 7;

    // --- PERIODICLISTBASE (op+14h) / ASYNCLISTADDR (op+18h) alignment -----------

    private const uint PeriodicListBaseMask = 0xFFFF_F000u; // 4 KB aligned, 1024 dword entries
    private const uint AsyncListAddressMask = 0xFFFF_FFE0u; // 32-byte aligned queue heads

    // --- operational register offsets, relative to OperationalBase ---------------

    private const uint RegUsbCommand = 0x00;
    private const uint RegUsbStatus = 0x04;
    private const uint RegUsbInterruptEnable = 0x08;
    private const uint RegFrameIndex = 0x0C;
    private const uint RegControlDataStructureSegment = 0x10;
    private const uint RegPeriodicListBase = 0x14;
    private const uint RegAsyncListAddress = 0x18;
    private const uint RegConfigFlag = 0x40;
    private const uint RegPortStatusBase = 0x44;
    private const uint PortRegisterStride = 4;

    private readonly IDmaMemory _dma;
    private readonly PciInterruptRouter _router;
    private readonly PciMemoryBarWindow _barWindow;

    private uint _command;
    private uint _status;
    private uint _interruptEnable;
    private ushort _frameIndex;
    private uint _controlDataStructureSegment; // CTRLDSSEGMENT: stored only, since HCCPARAMS reports no 64-bit addressing capability
    private uint _periodicListBase;
    private uint _asyncListAddress;
    private double _elapsedMicroseconds;

    public PciFunction Function { get; }

    public EhciController(PciBus pci, PhysicalMemoryBus memory, ushort functionAddress)
    {
        _router = pci.InterruptRouter ?? throw new InvalidOperationException("EHCI requires PCI interrupt routing.");
        if (PciInterruptRouter.Link(functionAddress, InterruptPinIntA) < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(functionAddress));
        }

        // The register file itself (this class's own C# fields, reached through the
        // IMemoryDevice methods below) is not bus-mastered: it is the CPU's own MMIO
        // access to BAR0, routed here by PciMemoryBarWindow exactly like any other
        // memory-mapped device. Everything the schedule itself reads or writes --
        // queue heads, qTDs, payload bytes -- is a genuine bus-master access and goes
        // through this instead, exactly as AhciController's own command engine does:
        // IDmaMemory does not apply A20 masking (a bus master never sees the CPU's own
        // address-wrap quirk) and raises Written for anything that changes RAM.
        _dma = pci.DmaMemory ?? throw new InvalidOperationException("EHCI requires the chipset's bus-master DMA memory.");

        Function = new PciFunction(functionAddress, VendorId, DeviceId, ClassCode, commandMask: CommandWritableMask);
        Function.DefineBar(Bar0BarIndex, Bar0Length, io: false);
        Function.Define(0x3C, 0, 1, 0xFF); // Interrupt Line bookkeeping only
        Function.Define(0x3D, InterruptPinIntA, 1); // INTA wired to the motherboard router
        Function.Define(0x60, SerialBusReleaseNumber, 1); // SBRN, read-only
        Function.Define(0x61, FrameLengthAdjustmentDefault, 1, 0xFF); // FLADJ, guest-writable scratch
        pci.Add(Function);

        _barWindow = new PciMemoryBarWindow(Function, Bar0BarIndex, Bar0Length, memory, this);

        Reset();
    }

    /// <summary>
    /// The full HCRESET handshake: every operational register (and every
    /// port's software-visible state) returns to its power-on value while
    /// PCI configuration and attached devices are untouched, exactly as
    /// <see cref="UhciController.Reset"/> does for its own frame schedule.
    /// </summary>
    public void Reset()
    {
        _command = CommandResetDefault;
        _status = StatusHalted;
        _interruptEnable = 0;
        _frameIndex = 0;
        _controlDataStructureSegment = 0;
        _periodicListBase = 0;
        _asyncListAddress = 0;
        _elapsedMicroseconds = 0;
        ResetPorts();
        UpdateInterrupt();
    }

    private bool InRam(uint address, int length) => address <= _dma.RamSize && (uint)length <= _dma.RamSize - address;

    /// <summary>
    /// A host system error (HSE): an out-of-range descriptor address, or a
    /// periodic/async list pointer naming a structure type this emulation
    /// never places there (an iTD, siTD or FSTN -- plan decision 5). Halts
    /// the controller exactly as <see cref="UhciController.Fatal"/> does.
    /// </summary>
    private void Fatal()
    {
        _status |= StatusHostSystemError | StatusHalted;
        _command &= ~CommandRun;
        UpdateInterrupt();
    }

    /// <summary>USBINT is asserted at the PCI line exactly when a pending status bit has its USBINTR enable set.</summary>
    private void UpdateInterrupt() => _router.SetInterrupt(Function.Address, InterruptPinIntA, (_status & _interruptEnable & StatusWriteOneToClearMask) != 0);

    public void Advance(double microseconds)
    {
        AdvancePorts(microseconds);

        if ((_command & CommandRun) == 0)
        {
            return;
        }

        _elapsedMicroseconds += microseconds;
        while (_elapsedMicroseconds >= MicrosecondsPerMicroframe && (_command & CommandRun) != 0)
        {
            _elapsedMicroseconds -= MicrosecondsPerMicroframe;
            AdvanceOneMicroframe();
        }
    }

    private void AdvanceOneMicroframe()
    {
        ushort previousFrameIndex = _frameIndex;
        _frameIndex = (ushort)((_frameIndex + 1) & FrameIndexMask);

        // With the fixed 1024-entry frame list, FLR is defined by bit 13 of FRINDEX
        // toggling (EHCI 1.0 section 2.3.2), not only by the 14-bit counter wrapping to
        // zero: that bit flips twice per full 16384-microframe period, at the wrap and
        // again halfway through it.
        if (((_frameIndex ^ previousFrameIndex) & FrameListRolloverBit) != 0)
        {
            _status |= StatusFrameListRollover;
        }

        if (Function.BusMasterEnabled)
        {
            if ((_command & CommandPeriodicScheduleEnable) != 0)
            {
                ExecutePeriodicMicroframe();
            }
            if ((_command & CommandRun) != 0 && (_command & CommandAsyncScheduleEnable) != 0)
            {
                ExecuteAsyncSchedule();
            }
        }

        UpdateInterrupt();
    }

    // --- IMemoryDevice: BAR0 ------------------------------------------------------

    public byte ReadByte(uint offset)
    {
        uint whole = ReadDword(offset & ~3u);
        return (byte)(whole >> (8 * (int)(offset & 3)));
    }

    /// <summary>
    /// Merges a byte write into the current dword and routes it through
    /// <see cref="WriteDwordInLanes"/> so a write-one-to-clear register
    /// (USBSTS, PORTSC's change bits) only clears the byte lane the guest
    /// actually touched -- see <c>AhciController.WriteByte</c> for the same
    /// reasoning, shared verbatim here.
    /// </summary>
    public void WriteByte(uint offset, byte value)
    {
        uint aligned = offset & ~3u;
        int lane = (int)(offset & 3);
        uint laneMask = 0xFFu << (8 * lane);
        uint whole = ReadDword(aligned);
        whole = (whole & ~laneMask) | ((uint)value << (8 * lane));
        WriteDwordInLanes(aligned, whole, laneMask);
    }

    public uint ReadDword(uint offset)
    {
        if (offset >= Bar0Length)
        {
            return uint.MaxValue;
        }
        if (offset < OperationalBase)
        {
            return ReadCapabilityRegister(offset);
        }
        return ReadOperationalRegister(offset - OperationalBase);
    }

    public void WriteDword(uint offset, uint value) => WriteDwordInLanes(offset, value, 0xFFFF_FFFFu);

    private void WriteDwordInLanes(uint offset, uint value, uint writeMask)
    {
        if (offset < Bar0Length && offset >= OperationalBase)
        {
            WriteOperationalRegister(offset - OperationalBase, value, writeMask);
        }
        UpdateInterrupt();
    }

    private uint ReadCapabilityRegister(uint offset) => offset switch
    {
        0x00 => CapabilityLengthAndVersion,
        0x04 => HcsParamsValue,
        0x08 => HccParamsValue,
        _ => 0, // HCSP-PORTROUTE and reserved space: Port Routing Rules is 0, so this field is never consulted
    };

    private uint ReadOperationalRegister(uint opOffset)
    {
        if (opOffset >= RegPortStatusBase)
        {
            int port = (int)((opOffset - RegPortStatusBase) / PortRegisterStride);
            return port < PortCount ? ReadPortStatus(port) : 0;
        }
        return opOffset switch
        {
            RegUsbCommand => _command,
            RegUsbStatus => ReadUsbStatus(),
            RegUsbInterruptEnable => _interruptEnable,
            RegFrameIndex => _frameIndex,
            RegControlDataStructureSegment => _controlDataStructureSegment,
            RegPeriodicListBase => _periodicListBase,
            RegAsyncListAddress => _asyncListAddress,
            RegConfigFlag => ReadConfigFlag(),
            _ => 0,
        };
    }

    private void WriteOperationalRegister(uint opOffset, uint value, uint writeMask)
    {
        if (opOffset >= RegPortStatusBase)
        {
            int port = (int)((opOffset - RegPortStatusBase) / PortRegisterStride);
            if (port < PortCount)
            {
                WritePortStatus(port, value, writeMask);
            }
            return;
        }

        switch (opOffset)
        {
            case RegUsbCommand:
                WriteUsbCommand(value, writeMask);
                break;
            case RegUsbStatus:
                _status &= ~(value & writeMask & StatusWriteOneToClearMask);
                break;
            case RegUsbInterruptEnable:
                _interruptEnable = (_interruptEnable & ~writeMask) | (value & writeMask & InterruptEnableMask);
                break;
            case RegFrameIndex:
                if ((_command & CommandRun) == 0)
                {
                    _frameIndex = (ushort)((_frameIndex & ~writeMask & FrameIndexMask) | (value & writeMask & FrameIndexMask));
                }
                break;
            case RegControlDataStructureSegment:
                _controlDataStructureSegment = (_controlDataStructureSegment & ~writeMask) | (value & writeMask);
                break;
            case RegPeriodicListBase:
                _periodicListBase = (_periodicListBase & ~writeMask) | (value & writeMask & PeriodicListBaseMask);
                break;
            case RegAsyncListAddress:
                _asyncListAddress = (_asyncListAddress & ~writeMask) | (value & writeMask & AsyncListAddressMask);
                break;
            case RegConfigFlag:
                WriteConfigFlag(value, writeMask);
                break;
        }
    }

    private uint ReadUsbStatus()
    {
        uint value = _status;
        if ((_command & CommandRun) != 0)
        {
            value |= (_command & CommandPeriodicScheduleEnable) != 0 ? StatusPeriodicScheduleStatus : 0;
            value |= (_command & CommandAsyncScheduleEnable) != 0 ? StatusAsyncScheduleStatus : 0;
        }
        return value;
    }

    /// <summary>
    /// HCRESET takes priority over every other bit in the same write, self
    /// clears instantly (the reset completes synchronously), and the RS
    /// transition gates HCHalted. IAAD is honoured synchronously too: since
    /// the async schedule always runs to completion within the microframe
    /// that reaches it (see the class summary), there is no "in progress"
    /// state for the doorbell to await, so ringing it immediately raises IAA.
    /// </summary>
    private void WriteUsbCommand(uint value, uint writeMask)
    {
        if ((writeMask & CommandHostControllerReset) != 0 && (value & CommandHostControllerReset) != 0)
        {
            Reset();
            return;
        }

        _command = (_command & ~writeMask & ~CommandInterruptOnAsyncAdvanceDoorbell)
            | (value & writeMask & CommandStorableMask);

        bool run = (_command & CommandRun) != 0;
        _status = run ? (_status & ~StatusHalted) : (_status | StatusHalted);
        if (!run)
        {
            _elapsedMicroseconds = 0;
        }

        if ((writeMask & CommandInterruptOnAsyncAdvanceDoorbell) != 0 && (value & CommandInterruptOnAsyncAdvanceDoorbell) != 0)
        {
            _status |= StatusInterruptOnAsyncAdvance;
        }
    }

    // --- snapshot ------------------------------------------------------------------

    public const int SnapshotVersion = 1;

    public byte[] Capture()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(_command);
        writer.Write(_status);
        writer.Write(_interruptEnable);
        writer.Write(_frameIndex);
        writer.Write(_controlDataStructureSegment);
        writer.Write(_periodicListBase);
        writer.Write(_asyncListAddress);
        writer.Write(_elapsedMicroseconds);
        CapturePorts(writer);
        return stream.ToArray();
    }

    public void Restore(byte[] payload, int version)
    {
        if (version != SnapshotVersion)
        {
            throw new InvalidDataException("Invalid EHCI snapshot version.");
        }

        using var reader = new BinaryReader(new MemoryStream(payload));
        uint command = reader.ReadUInt32();
        uint status = reader.ReadUInt32();
        uint interruptEnable = reader.ReadUInt32();
        ushort frameIndex = reader.ReadUInt16();
        uint controlDataStructureSegment = reader.ReadUInt32();
        uint periodicListBase = reader.ReadUInt32();
        uint asyncListAddress = reader.ReadUInt32();
        double elapsedMicroseconds = reader.ReadDouble();
        if (!double.IsFinite(elapsedMicroseconds) || elapsedMicroseconds < 0)
        {
            throw new InvalidDataException("Invalid EHCI snapshot.");
        }
        RestorePorts(reader);
        if (reader.BaseStream.Position != reader.BaseStream.Length)
        {
            throw new InvalidDataException("Invalid EHCI snapshot.");
        }

        _command = command;
        _status = status;
        _interruptEnable = interruptEnable;
        _frameIndex = frameIndex;
        _controlDataStructureSegment = controlDataStructureSegment;
        _periodicListBase = periodicListBase;
        _asyncListAddress = asyncListAddress;
        _elapsedMicroseconds = elapsedMicroseconds;
        UpdateInterrupt();
    }
}
