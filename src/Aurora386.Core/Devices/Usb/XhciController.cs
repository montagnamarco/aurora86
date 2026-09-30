using Aurora386.Core.Devices.Pci;
using Aurora386.Core.Memory;

namespace Aurora386.Core.Devices.Usb;

/// <summary>
/// A qemu-xhci-compatible USB 3.0 host controller (PCI 1B36:000D, class
/// 0C0330, revision 01): the xHCI 1.2 register file Linux's <c>xhci-hcd</c>
/// and Windows 8's <c>usbxhci.sys</c> bind to on class code alone, since no
/// USB 3.0 vendor identity was ever assigned here (coordinator decision 1,
/// <c>plans/usb3-xhci.md</c>). This first stage only exposes the register
/// skeleton -- capability, operational and runtime registers, the doorbell
/// array and PCI identity/BAR -- so a driver can probe and reset the
/// controller; <c>XhciController.Ports.cs</c> adds the four root ports and
/// <c>XhciController.Events.cs</c> the interrupter-0 event ring and
/// interrupt delivery. Command ring and transfer ring execution (the
/// doorbell-driven engine plan decision 6 describes) is a later stage:
/// <see cref="RingDoorbell"/> is deliberately empty here.
///
/// Only INTx (INTA) is wired, one interrupter and one event-ring segment are
/// modelled (coordinator decision 3), and there is no USB Legacy Support
/// capability, matching qemu-xhci's own extended capability list (decision
/// 7). Every offset this class does not otherwise claim, within BAR0, reads
/// 0 and discards writes; everything past BAR0's own length reads all ones,
/// exactly like <see cref="EhciController"/>'s own reserved space.
/// </summary>
public sealed partial class XhciController : IMemoryDevice
{
    // --- PCI identity (plan decision 1) -----------------------------------------

    private const ushort VendorId = 0x1B36; // Red Hat (qemu-xhci's own vendor)
    private const ushort DeviceId = 0x000D;
    private const uint ClassCode = 0x0C0330; // serial bus controller (0C), USB (03), xHCI programming interface (30h)
    private const byte RevisionId = 0x01;
    private const byte InterruptPinIntA = 1;

    /// <summary>Memory space (bit 1) and bus master (bit 2); there is no I/O BAR to gate with bit 0.</summary>
    private const ushort CommandWritableMask = 0x6;

    // --- BAR0 geometry: a 64-bit, 16 KB memory BAR --------------------------------

    private const int Bar0BarIndex = 0;
    private const int Bar0UpperDwordBarIndex = 1; // PCI 2.1's own upper-dword slot for a 64-bit BAR pair, not a second real BAR
    private const uint Bar0Length = 0x4000; // 16 KB (plan's "Costanti": BAR0 16 KB, a 64 bit)

    /// <summary>
    /// BAR0's fixed low bits: bit 0 = 0 (memory space), bits 2:1 = 10b (a
    /// 64-bit BAR, PCI 2.2 section 6.2.5.1), bit 3 = 0 (not prefetchable --
    /// the register file has read side effects, most visibly the doorbells).
    /// <see cref="PciFunction.DefineBar"/> only ever spells out a 32-bit
    /// memory or I/O BAR, so this class calls <see cref="PciFunction.Define"/>
    /// directly for both dwords of the pair instead, exactly the way a plain
    /// 32-bit BAR's own fixed bit and address mask are built.
    /// </summary>
    private const uint Bar0TypeAndPrefetch = 0x4;

    // --- capability registers (offsets 00h-1Fh) and the one extended capability (20h-2Fh), fixed, never written ---

    /// <summary>CAPLENGTH (byte 0) = 40h, reserved byte 1 = 0, HCIVERSION (bytes 2-3) = 0100h: one dword.</summary>
    private const uint CapabilityLengthAndVersion = 0x0100_0040;

    private const uint OperationalBase = 0x40; // CAPLENGTH's value: where the operational registers start

    private const int MaxDeviceSlots = 8;
    private const int MaxInterrupters = 1;

    /// <summary>HCSPARAMS1 (offset 04h): MaxPorts (31:24), MaxIntrs (18:8), MaxSlots (7:0).</summary>
    private const uint HcsParams1Value = ((uint)PortCount << 24) | ((uint)MaxInterrupters << 8) | MaxDeviceSlots;

    /// <summary>HCSPARAMS2 (offset 08h): IST=15 (bits 3:0), ERST Max=0 -- one segment only (coordinator decision 3), no scratchpad buffers.</summary>
    private const uint HcsParams2Value = 0x0000000F;

    private const uint HcsParams3Value = 0; // offset 0Ch: no exit latencies modelled

    /// <summary>HCCPARAMS1 (offset 10h): AC64=1 (bit 0), CSZ=0 (32-byte contexts, bit 2), xECP=8 dwords (bits 31:16) -- the Supported Protocol capability at 20h.</summary>
    private const uint HccParams1Value = 0x00080001;

    private const uint DoorbellOffsetValue = 0x2000; // DBOFF, offset 14h
    private const uint RuntimeOffsetValue = 0x1000; // RTSOFF, offset 18h
    private const uint HccParams2Value = 0; // offset 1Ch: no ESE/CTC/LEC/CIC modelled

    // --- Supported Protocol capability (the only extended capability, at 20h) ------

    /// <summary>
    /// dword0: Cap ID=02h (Supported Protocol), Next=0 -- this is the only
    /// extended capability, unlike QEMU's two-capability layout (USB 2.0 at
    /// 20h chained to a USB 3.0 one at 30h): the coordinator's stage-one
    /// scope is USB 2.0 speeds only (decision 4), so there is nothing to
    /// chain to and Next stays 0 rather than pointing at a capability that
    /// does not exist yet.
    /// </summary>
    private const uint SupportedProtocolDword0 = 0x02000002;

    private const uint SupportedProtocolDword1 = 0x20425355; // "USB " (ASCII name string, little-endian)
    private const uint SupportedProtocolDword2 = (PortCount << 8) | 1; // Compatible Port Count=4 (15:8), Compatible Port Offset=1 (7:0)
    private const uint SupportedProtocolDword3 = 0;

    private const uint ExtendedCapabilityBase = 0x20;

    // --- USBCMD (op+00h) -----------------------------------------------------------

    private const uint CommandRunStop = 1u << 0; // RS
    private const uint CommandHostControllerReset = 1u << 1; // HCRST
    private const uint CommandInterrupterEnable = 1u << 2; // INTE
    private const uint CommandHostSystemErrorEnable = 1u << 3; // HSEE
    private const uint CommandStorableMask = CommandRunStop | CommandInterrupterEnable | CommandHostSystemErrorEnable;

    // --- USBSTS (op+04h) -----------------------------------------------------------

    private const uint StatusHostControllerHalted = 1u << 0; // HCH: 1 whenever RS=0
    private const uint StatusHostSystemError = 1u << 2; // HSE
    private const uint StatusEventInterrupt = 1u << 3; // EINT
    private const uint StatusPortChangeDetect = 1u << 4; // PCD
    private const uint StatusControllerNotReady = 1u << 11; // CNR: never set, so it always reads 0
    private const uint StatusHostControllerError = 1u << 12; // HCE
    private const uint StatusWriteOneToClearMask = StatusHostSystemError | StatusEventInterrupt | StatusPortChangeDetect | StatusHostControllerError;

    // --- PAGESIZE (op+08h) -----------------------------------------------------------

    private const uint PageSizeValue = 1; // bit 0 set: 4 KB pages (2^(12+0))

    // --- DNCTRL (op+14h) --------------------------------------------------------------

    private const uint DeviceNotificationControlMask = 0xFFFF; // bits 15:0 (N0-N15); stored only, no notification TRB is ever generated

    // --- CRCR (op+18h/1Ch) -------------------------------------------------------------

    private const uint CrcrRingCycleState = 1u << 0; // RCS, RW
    private const uint CrcrPointerMask = 0xFFFF_FFC0u; // bits 31:6; bits 5:4 reserved
    private const uint CrcrLowWritableMask = CrcrRingCycleState | CrcrPointerMask;

    /// <summary>
    /// CRR (bit 3): set once doorbell 0 has started the command ring and
    /// clear again after CS/CA, HCRST or USBCMD.RS=0 stop it (xHCI 1.2
    /// section 5.4.5); see <c>WriteCommandRingControlLow</c>.
    /// </summary>
    private const uint CrcrCommandRingRunning = 1u << 3;

    // --- DCBAAP (op+28h/2Ch) -----------------------------------------------------------

    private const uint DcbaapPointerMask = 0xFFFF_FFC0u; // 64-byte aligned (plan's "Costanti")

    // --- CONFIG (op+30h) -----------------------------------------------------------------

    private const uint ConfigMaxSlotsEnabledMask = 0xFF;

    // --- operational register offsets, relative to OperationalBase -------------------

    private const uint RegUsbCommand = 0x00;
    private const uint RegUsbStatus = 0x04;
    private const uint RegPageSize = 0x08;
    private const uint RegDeviceNotificationControl = 0x14;
    private const uint RegCommandRingControlLow = 0x18;
    private const uint RegCommandRingControlHigh = 0x1C;
    private const uint RegDcbaapLow = 0x28;
    private const uint RegDcbaapHigh = 0x2C;
    private const uint RegConfig = 0x30;

    // --- runtime register offsets, relative to RuntimeOffsetValue ---------------------

    private const uint RegMicroframeIndex = 0x00;
    private const uint InterrupterRegisterSetBase = 0x20; // interrupter 0 (the only one) starts here, at RTSOFF+20h = 1020h

    private const int MicroframeIndexBits = 14;
    private const ushort MicroframeIndexMask = (1 << MicroframeIndexBits) - 1; // 3FFFh
    private const double MicrosecondsPerMicroframe = 125.0;

    // --- doorbells (DoorbellOffsetValue, one dword per doorbell) -----------------------

    private const int DoorbellCount = MaxDeviceSlots + 1; // doorbell 0 = command ring, 1..MaxDeviceSlots = device slots

    private readonly IDmaMemory _dma;
    private readonly PciInterruptRouter _router;
    private readonly PciMemoryBarWindow _barWindow;

    private uint _command;
    private uint _status;
    private uint _deviceNotificationControl;
    private uint _crcrLow;
    private uint _crcrHigh;
    private uint _dcbaapLow;
    private uint _dcbaapHigh;
    private uint _config;
    private ushort _microframeIndex;
    private double _elapsedMicroseconds;
    private readonly uint[] _doorbells = new uint[DoorbellCount];

    public PciFunction Function { get; }

    public XhciController(PciBus pci, PhysicalMemoryBus memory, ushort functionAddress)
    {
        _router = pci.InterruptRouter ?? throw new InvalidOperationException("xHCI requires PCI interrupt routing.");
        if (PciInterruptRouter.Link(functionAddress, InterruptPinIntA) < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(functionAddress));
        }

        // Every payload byte a command, transfer or event TRB moves goes through the
        // chipset's bus-master DMA memory, exactly as EhciController's own schedule
        // does: see IDmaMemory's own summary for the A20 and RAM-bound reasoning.
        _dma = pci.DmaMemory ?? throw new InvalidOperationException("xHCI requires the chipset's bus-master DMA memory.");

        Function = new PciFunction(functionAddress, VendorId, DeviceId, ClassCode, commandMask: CommandWritableMask);
        Function.Define(0x08, RevisionId, 1); // revision ID, the low byte of the class-code dword
        Function.Define(0x3C, 0, 1, 0xFF); // Interrupt Line bookkeeping only
        Function.Define(0x3D, InterruptPinIntA, 1); // INTA wired to the motherboard router

        uint bar0Mask = ~(Bar0Length - 1) & 0xFFFF_FFF0u;
        Function.Define(0x10 + Bar0BarIndex * 4, Bar0TypeAndPrefetch, 4, bar0Mask);
        Function.Define(0x10 + Bar0UpperDwordBarIndex * 4, 0, 4, 0xFFFF_FFFFu); // the 64-bit BAR's upper dword: fully writable, this emulator's own bus only ever places the base below it
        pci.Add(Function);

        _barWindow = new PciMemoryBarWindow(Function, Bar0BarIndex, Bar0Length, memory, this);

        Reset();
    }

    /// <summary>
    /// The full HCRST handshake: every operational, runtime and doorbell
    /// register (and every port's software-visible state) returns to its
    /// power-on value while PCI configuration and attached devices are
    /// untouched, exactly as <see cref="EhciController.Reset"/> does for its
    /// own registers.
    /// </summary>
    public void Reset()
    {
        _command = 0;
        _status = StatusHostControllerHalted;
        _deviceNotificationControl = 0;
        _crcrLow = 0;
        _crcrHigh = 0;
        _commandRingRunning = false;
        _commandRingAbortHighPending = false;
        ArmCommandRing();
        _dcbaapLow = 0;
        _dcbaapHigh = 0;
        _config = 0;
        _microframeIndex = 0;
        _elapsedMicroseconds = 0;
        Array.Clear(_doorbells);
        Array.Clear(_slotEnabled);
        ResetPorts();
        ResetEvents();
        ResetTransfers();
        RecomputeInterruptLine();
    }

    /// <summary>
    /// Doorbell 0, target 0 runs the command ring
    /// (<c>XhciController.Commands.cs</c>); doorbells 1..<see cref="MaxDeviceSlots"/>,
    /// target 1..31, run that slot's endpoint transfer ring
    /// (<c>XhciController.Transfers.cs</c>) -- the doorbell target field,
    /// bits 7:0 of the value just written (plan's "Costanti"; the stream ID
    /// in bits 31:16 is discarded, since this subset has no streams).
    /// </summary>
    private void RingDoorbell(int slot, uint value)
    {
        byte target = (byte)value;
        if (slot == 0)
        {
            if (target == 0)
            {
                RunCommandRing();
            }
            return;
        }
        if (target is >= 1 and <= MaxEndpointContextIndex)
        {
            RunTransferRing(slot, target);
        }
    }

    public void Advance(double microseconds)
    {
        AdvancePorts(microseconds);

        if ((_command & CommandRunStop) == 0)
        {
            return;
        }

        AdvanceTransferRings(microseconds); // interrupt endpoints poll on their own interval, not only on a doorbell

        _elapsedMicroseconds += microseconds;
        while (_elapsedMicroseconds >= MicrosecondsPerMicroframe)
        {
            _elapsedMicroseconds -= MicrosecondsPerMicroframe;
            _microframeIndex = (ushort)((_microframeIndex + 1) & MicroframeIndexMask);
        }
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
    /// (USBSTS, PORTSC's change bits, IMAN.IP, ERDP.EHB) only clears the
    /// byte lane the guest actually touched -- see
    /// <c>EhciController.WriteByte</c> for the same reasoning, shared
    /// verbatim here.
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
        if (offset < RuntimeOffsetValue)
        {
            return ReadOperationalRegister(offset - OperationalBase);
        }
        if (offset < DoorbellOffsetValue)
        {
            return ReadRuntimeRegister(offset - RuntimeOffsetValue);
        }
        return ReadDoorbellRegister(offset - DoorbellOffsetValue);
    }

    public void WriteDword(uint offset, uint value) => WriteDwordInLanes(offset, value, 0xFFFF_FFFFu);

    private void WriteDwordInLanes(uint offset, uint value, uint writeMask)
    {
        if (offset < Bar0Length && offset >= OperationalBase)
        {
            if (offset < RuntimeOffsetValue)
            {
                WriteOperationalRegister(offset - OperationalBase, value, writeMask);
            }
            else if (offset < DoorbellOffsetValue)
            {
                WriteRuntimeRegister(offset - RuntimeOffsetValue, value, writeMask);
            }
            else
            {
                WriteDoorbellRegister(offset - DoorbellOffsetValue, value, writeMask);
            }
        }
        RecomputeInterruptLine();
    }

    private uint ReadCapabilityRegister(uint offset) => offset switch
    {
        0x00 => CapabilityLengthAndVersion,
        0x04 => HcsParams1Value,
        0x08 => HcsParams2Value,
        0x0C => HcsParams3Value,
        0x10 => HccParams1Value,
        0x14 => DoorbellOffsetValue,
        0x18 => RuntimeOffsetValue,
        0x1C => HccParams2Value,
        ExtendedCapabilityBase + 0x00 => SupportedProtocolDword0,
        ExtendedCapabilityBase + 0x04 => SupportedProtocolDword1,
        ExtendedCapabilityBase + 0x08 => SupportedProtocolDword2,
        ExtendedCapabilityBase + 0x0C => SupportedProtocolDword3,
        _ => 0, // reserved capability space, and the rest of a single extended capability's own reserved dwords
    };

    private uint ReadOperationalRegister(uint opOffset)
    {
        if (opOffset >= PortRegisterSetBase)
        {
            // Each port's set is PORTSC at +0 then PORTPMSC, PORTLI and
            // PORTHLPMC, which read 0 here (no link power management).
            int port = (int)((opOffset - PortRegisterSetBase) / PortRegisterStride);
            bool isPortStatus = (opOffset - PortRegisterSetBase) % PortRegisterStride == 0;
            return port < PortCount && isPortStatus ? ReadPortStatus(port) : 0;
        }
        return opOffset switch
        {
            RegUsbCommand => _command,
            RegUsbStatus => ReadUsbStatus(),
            RegPageSize => PageSizeValue,
            RegDeviceNotificationControl => _deviceNotificationControl,
            RegCommandRingControlLow => _commandRingRunning ? _crcrLow | CrcrCommandRingRunning : _crcrLow,
            RegCommandRingControlHigh => _crcrHigh,
            RegDcbaapLow => _dcbaapLow,
            RegDcbaapHigh => _dcbaapHigh,
            RegConfig => _config,
            _ => 0,
        };
    }

    private void WriteOperationalRegister(uint opOffset, uint value, uint writeMask)
    {
        if (opOffset >= PortRegisterSetBase)
        {
            int port = (int)((opOffset - PortRegisterSetBase) / PortRegisterStride);
            bool isPortStatus = (opOffset - PortRegisterSetBase) % PortRegisterStride == 0;
            if (port < PortCount && isPortStatus)
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
            case RegDeviceNotificationControl:
                _deviceNotificationControl = (_deviceNotificationControl & ~writeMask) | (value & writeMask & DeviceNotificationControlMask);
                break;
            case RegCommandRingControlLow:
                // See WriteCommandRingControlLow (XhciController.Commands.cs):
                // the pointer loads only while the ring is stopped, and CS/CA
                // stop a running ring with a Command Ring Stopped event.
                WriteCommandRingControlLow(value, writeMask);
                break;
            case RegCommandRingControlHigh:
                WriteCommandRingControlHigh(value, writeMask);
                break;
            case RegDcbaapLow:
                _dcbaapLow = (_dcbaapLow & ~writeMask) | (value & writeMask & DcbaapPointerMask);
                break;
            case RegDcbaapHigh:
                _dcbaapHigh = (_dcbaapHigh & ~writeMask) | (value & writeMask);
                break;
            case RegConfig:
                _config = (_config & ~writeMask) | (value & writeMask & ConfigMaxSlotsEnabledMask);
                break;
        }
    }

    private uint ReadUsbStatus() => _status; // HCH already lives in _status, toggled by WriteUsbCommand; CNR is never set

    /// <summary>
    /// HCRST takes priority over every other bit in the same write and self
    /// clears instantly (the reset completes synchronously, exactly like
    /// <see cref="EhciController"/>'s own HCRESET); the RS transition gates
    /// HCH.
    /// </summary>
    private void WriteUsbCommand(uint value, uint writeMask)
    {
        if ((writeMask & CommandHostControllerReset) != 0 && (value & CommandHostControllerReset) != 0)
        {
            Reset();
            return;
        }

        _command = (_command & ~writeMask) | (value & writeMask & CommandStorableMask);

        bool run = (_command & CommandRunStop) != 0;
        _status = run ? (_status & ~StatusHostControllerHalted) : (_status | StatusHostControllerHalted);
        if (!run)
        {
            _elapsedMicroseconds = 0;
            // Halting the controller stops the command ring (xHCI 1.2
            // 5.4.5: CRR clears once RS is 0); software re-arms it with a
            // fresh CRCR write before setting RS again.
            _commandRingRunning = false;
        }
    }

    private uint ReadRuntimeRegister(uint rtOffset)
    {
        if (rtOffset == RegMicroframeIndex)
        {
            return _microframeIndex;
        }
        if (rtOffset >= InterrupterRegisterSetBase)
        {
            return ReadInterrupterRegister(rtOffset - InterrupterRegisterSetBase);
        }
        return 0;
    }

    private void WriteRuntimeRegister(uint rtOffset, uint value, uint writeMask)
    {
        if (rtOffset >= InterrupterRegisterSetBase)
        {
            WriteInterrupterRegister(rtOffset - InterrupterRegisterSetBase, value, writeMask);
        }
        // MFINDEX itself is read-only (it only ever moves through Advance).
    }

    private uint ReadDoorbellRegister(uint dbOffset)
    {
        int index = (int)(dbOffset / 4);
        return (uint)index < DoorbellCount && dbOffset % 4 == 0 ? _doorbells[index] : 0;
    }

    private void WriteDoorbellRegister(uint dbOffset, uint value, uint writeMask)
    {
        int index = (int)(dbOffset / 4);
        if ((uint)index >= DoorbellCount || dbOffset % 4 != 0)
        {
            return;
        }
        _doorbells[index] = (_doorbells[index] & ~writeMask) | (value & writeMask);
        RingDoorbell(index, _doorbells[index]);
    }

    // --- snapshot ------------------------------------------------------------------

    /// <summary>
    /// Bumped from 1 once command and transfer processing (stages 3-4) added
    /// their own persisted state (<see cref="_slotEnabled"/> and the
    /// transfer-ring bookkeeping in <c>XhciController.Transfers.cs</c>):
    /// version 1 was never committed, so there is no old format to keep
    /// reading.
    /// </summary>
    public const int SnapshotVersion = 2;

    public byte[] Capture()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(_command);
        writer.Write(_status);
        writer.Write(_deviceNotificationControl);
        writer.Write(_crcrLow);
        writer.Write(_crcrHigh);
        writer.Write(_dcbaapLow);
        writer.Write(_dcbaapHigh);
        writer.Write(_config);
        writer.Write(_microframeIndex);
        writer.Write(_elapsedMicroseconds);
        writer.Write(_doorbells.Length);
        foreach (uint doorbell in _doorbells)
        {
            writer.Write(doorbell);
        }
        CapturePorts(writer);
        CaptureEvents(writer);
        CaptureCommands(writer);
        CaptureTransfers(writer);
        return stream.ToArray();
    }

    public void Restore(byte[] payload, int version)
    {
        if (version != SnapshotVersion)
        {
            throw new InvalidDataException("Invalid xHCI snapshot version.");
        }

        using var reader = new BinaryReader(new MemoryStream(payload));
        uint command = reader.ReadUInt32();
        uint status = reader.ReadUInt32();
        uint deviceNotificationControl = reader.ReadUInt32();
        uint crcrLow = reader.ReadUInt32();
        uint crcrHigh = reader.ReadUInt32();
        uint dcbaapLow = reader.ReadUInt32();
        uint dcbaapHigh = reader.ReadUInt32();
        uint config = reader.ReadUInt32();
        ushort microframeIndex = reader.ReadUInt16();
        double elapsedMicroseconds = reader.ReadDouble();
        if (!double.IsFinite(elapsedMicroseconds) || elapsedMicroseconds < 0)
        {
            throw new InvalidDataException("Invalid xHCI snapshot.");
        }
        int doorbellCount = reader.ReadInt32();
        if (doorbellCount != DoorbellCount)
        {
            throw new InvalidDataException("Invalid xHCI snapshot doorbell count.");
        }
        var doorbells = new uint[DoorbellCount];
        for (int i = 0; i < DoorbellCount; i++)
        {
            doorbells[i] = reader.ReadUInt32();
        }
        RestorePorts(reader);
        RestoreEvents(reader);
        RestoreCommands(reader);
        RestoreTransfers(reader);
        if (reader.BaseStream.Position != reader.BaseStream.Length)
        {
            throw new InvalidDataException("Invalid xHCI snapshot.");
        }

        _command = command;
        _status = status;
        _deviceNotificationControl = deviceNotificationControl;
        _crcrLow = crcrLow;
        _crcrHigh = crcrHigh;
        _dcbaapLow = dcbaapLow;
        _dcbaapHigh = dcbaapHigh;
        _config = config;
        _microframeIndex = microframeIndex;
        _elapsedMicroseconds = elapsedMicroseconds;
        doorbells.CopyTo(_doorbells, 0);
        RecomputeInterruptLine();
    }
}
