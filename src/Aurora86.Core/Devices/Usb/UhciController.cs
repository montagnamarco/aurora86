using Aurora86.Core.Devices.Pci;
using Aurora86.Core.IO;
using Aurora86.Core.Memory;

namespace Aurora86.Core.Devices.Usb;

/// <summary>
/// UHCI's software-owned frame schedule, fetched from physical guest RAM.
/// A frame contains TDs and queue heads; each transaction writes its result
/// back to the same descriptor the guest driver submitted. Ports expose
/// attach/reset/enable separately so address-zero enumeration is unambiguous.
/// Register and bit names below follow the Intel UHCI 1.1 Design Guide.
///
/// Only these two root ports are wired to this controller directly; a
/// <see cref="UsbHubDevice"/> plugged into either one extends the tree
/// further down, and <see cref="ExecuteTd"/> reaches a device behind it
/// through <see cref="IUsbDevice.Find"/> without needing to know a hub is
/// there at all.
/// </summary>
public sealed class UhciController : IIoDwordDevice
{
    // Number of root hub ports this controller exposes. Real UHCI hardware
    // (and this emulation) always has exactly two.
    private const int PortCount = 2;

    // I/O register offsets from the BAR, all word registers except SOFMOD
    // (a byte register that still lives on its own word slot).
    private const int RegisterCommand = 0x00;           // USBCMD
    private const int RegisterStatus = 0x02;             // USBSTS
    private const int RegisterInterruptEnable = 0x04;    // USBINTR
    private const int RegisterFrameNumber = 0x06;        // FRNUM
    private const int RegisterFrameBaseLow = 0x08;       // FRBASEADD bits 15:0
    private const int RegisterFrameBaseHigh = 0x0A;      // FRBASEADD bits 31:16
    private const int RegisterStartOfFrameModify = 0x0C; // SOFMOD
    private const int RegisterPort0 = 0x10;              // PORTSC1
    private const int RegisterPort1 = 0x12;              // PORTSC2
    private const int PortRegisterStride = RegisterPort1 - RegisterPort0;

    // USBCMD bits.
    private const ushort CommandRun = 1 << 0;                 // RS
    private const ushort CommandHostControllerReset = 1 << 1; // HCRESET
    private const ushort CommandGlobalReset = 1 << 2;         // GRESET
    private const ushort CommandEnterGlobalSuspend = 1 << 3;  // EGSM
    private const ushort CommandForceGlobalResume = 1 << 4;   // FGR
    private const ushort CommandSoftwareDebug = 1 << 5;       // SWDBG
    private const ushort CommandRegisterByteMask = 0xFF;      // USBCMD is byte-wide; the upper byte always reads zero

    // USBSTS bits.
    private const ushort StatusUsbInterrupt = 1 << 0;    // USBINT
    private const ushort StatusErrorInterrupt = 1 << 1;  // ERROR
    private const ushort StatusResumeDetect = 1 << 2;    // RESUME
    private const ushort StatusHostSystemError = 1 << 3; // HSE
    private const ushort StatusProcessError = 1 << 4;    // HCPE
    private const ushort StatusHalted = 1 << 5;          // HCHALTED
    private const ushort StatusWriteOneToClearMask = StatusUsbInterrupt | StatusErrorInterrupt
        | StatusResumeDetect | StatusHostSystemError | StatusProcessError;

    // USBINTR bits.
    private const ushort InterruptEnableTimeoutCrc = 1 << 0;  // Timeout/CRC Interrupt Enable
    private const ushort InterruptEnableResume = 1 << 1;      // Resume Interrupt Enable
    private const ushort InterruptEnableComplete = 1 << 2;    // Interrupt On Complete Enable
    private const ushort InterruptEnableShortPacket = 1 << 3; // Short Packet Interrupt Enable

    private const ushort FrameNumberMask = 0x7FF;             // FRNUM is 11 bits
    private const int FrameListSize = 1024;                   // entries the frame list schedules, and this walk's traversal budget
    private const uint FrameListIndexMask = FrameListSize - 1;
    private const uint FrameBaseLowerHalfMask = 0x0000FFFFu;
    private const uint FrameBaseUpperHalfMask = 0xFFFF0000u;
    private const uint FrameBaseLowWritableMask = 0xF000;     // the frame list is 4 KB aligned; bits 11:0 are hardwired zero
    private const int FrameBaseHighShift = 16;
    private const byte StartOfFrameMask = 0x7F;               // SOFMOD is 7 bits
    private const byte DefaultStartOfFrameValue = 64;         // (11936 + 64) / 12 = 1000 microseconds, the nominal 1 ms frame
    private const double SofModBaseClockCount = 11936;        // SOFMOD's formula: (11936 + SOF) 12 MHz clocks per frame
    private const double ClocksPerMicrosecond = 12.0;

    // PORTSC bits.
    private const ushort PortConnectStatus = 1 << 0;         // CCS
    private const ushort PortConnectStatusChange = 1 << 1;   // CSC, write-one-to-clear
    private const ushort PortEnabled = 1 << 2;                // PE
    private const ushort PortEnableChange = 1 << 3;           // PEC, write-one-to-clear
    private const ushort PortLineStatusDPlus = 1 << 4;        // a full-speed device's idle pull-up
    private const ushort PortLineStatusDMinus = 1 << 5;       // a low-speed device's idle pull-up
    private const ushort PortResumeDetect = 1 << 6;           // RD
    private const ushort PortAlwaysOne = 1 << 7;              // reserved, always reads 1
    private const ushort PortLowSpeedDeviceAttached = 1 << 8; // LSDA
    private const ushort PortReset = 1 << 9;                  // PR
    private const ushort PortSuspend = 1 << 12;               // SUSP
    private const ushort PortWritableMask = PortEnabled | PortResumeDetect | PortReset | PortSuspend;
    private const ushort PortChangeMask = PortConnectStatusChange | PortEnableChange;
    private const ushort PortActiveMask = PortEnabled | PortReset | PortSuspend;

    // TD Link Pointer bits (also used for a Queue Head's two link fields).
    // TDs and queue heads are 16-byte aligned, so bits 3:0 carry these flags.
    private const uint LinkTerminate = 1u << 0;     // T
    private const uint LinkQueueHead = 1u << 1;     // Q
    private const uint LinkVerticalFirst = 1u << 2; // Vf, depth-first traversal
    private const uint LinkAddressMask = 0xFFFFFFF0u;

    // TD Token bits.
    private const byte PidSetup = 0x2D;
    private const byte PidIn = 0x69;
    private const byte PidOut = 0xE1;
    private const int TokenAddressShift = 8;
    private const uint TokenAddressMask = 0x7F;
    private const int TokenEndpointShift = 15;
    private const uint TokenEndpointMask = 0x0F;
    private const uint TokenDataToggleBit = 1u << 19; // D
    private const int TokenMaxLengthShift = 21;
    private const uint TokenMaxLengthMask = 0x7FF;    // 11 bits, encoded as length - 1; all-ones means a zero-length packet
    private const int MaxTransferLength = 2048;       // the largest length TokenMaxLengthMask can encode, plus headroom

    // TD Control and Status bits.
    private const uint TdControlActive = 1u << 23;
    private const uint TdControlStalled = 1u << 22;
    private const uint TdControlDataBufferError = 1u << 21;
    private const uint TdControlBabbleDetected = 1u << 20;
    private const uint TdControlNakReceived = 1u << 19;
    private const uint TdControlCrcTimeoutError = 1u << 18;
    private const uint TdControlBitstuffError = 1u << 17;
    private const uint TdControlReservedBit16 = 1u << 16; // always reads zero; part of the status byte cleared as a whole
    private const uint TdControlStatusBitsMask = TdControlReservedBit16 | TdControlBitstuffError | TdControlCrcTimeoutError
        | TdControlNakReceived | TdControlBabbleDetected | TdControlDataBufferError | TdControlStalled; // bits 16-22, cleared before a fresh result
    private const uint TdControlIoc = 1u << 24;        // Interrupt On Complete
    private const uint TdControlLowSpeed = 1u << 26;   // LS, the addressed device is low speed
    private const int TdErrorCounterShift = 27;
    private const uint TdErrorCounterMask = 0x3;       // C_ERR, the two-bit retry counter
    private const uint TdControlShortPacketDetect = 1u << 29; // SPD
    private const uint TdActualLengthMask = 0x7FF;     // bits 10:0, encoded as length - 1

    private const byte PciInterruptPinA = 1;

    private readonly PhysicalMemoryBus _memory;
    private readonly PciInterruptRouter _router;
    private readonly PciIoBarWindow _ioWindow;
    private readonly IUsbDevice?[] _devices = new IUsbDevice?[PortCount];
    private readonly ushort[] _ports = [PortAlwaysOne, PortAlwaysOne];
    private ushort _command;
    private ushort _status = StatusHalted;
    private ushort _interruptEnable;
    private ushort _frame;
    private uint _frameBase;
    private byte _sof = DefaultStartOfFrameValue;
    private double _elapsed;
    private bool _ioc;
    private bool _shortPacket;

    public PciFunction Function { get; }

    public UhciController(
        PhysicalMemoryBus memory,
        IoBus io,
        PciBus bus,
        ushort functionAddress = 0x10,
        ushort ioAddress = 0xC100,
        ushort deviceId = 0x7020)
    {
        _memory = memory;
        _router = bus.InterruptRouter ?? throw new ArgumentException("USB requires PCI routing.", nameof(bus));
        Function = new PciFunction(functionAddress, 0x8086, deviceId, 0x0C0300, commandMask: 5);
        Function.DefineBar(4, 32, true, ioAddress);
        Function.Define(4, 1, 2, 5);          // USBCMD's I/O space bit reads 1 at power-on; bus master starts off
        Function.Define(0x3C, 0xFF, 1, 0xFF); // interrupt line, unassigned until firmware picks one
        Function.Define(0x3D, PciInterruptPinA, 1);
        Function.Define(0x60, 0x10, 1); // USB 1.0 serial bus release number
        bus.Add(Function);

        // BAR4, the only one this function defines: 32 bytes of I/O space,
        // moved and gated by command bit 0 exactly like any other PCI I/O
        // BAR. See PciIoBarWindow for the shared relocation logic, including
        // never decoding the transient all-ones sizing probe.
        _ioWindow = new PciIoBarWindow(Function, 4, 32, io, this);
    }

    public void Attach(int port, IUsbDevice? device)
    {
        if ((uint)port >= PortCount)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        if (ReferenceEquals(_devices[port], device))
        {
            return;
        }

        bool wasEnabled = (_ports[port] & PortEnabled) != 0;
        _devices[port] = device;
        device?.Reset();

        // A device change always looks like a fresh connect/disconnect to
        // software; one that was enabled also loses that enable, since a
        // real port disables itself the moment the wire changes.
        _ports[port] = (ushort)(PortPresence(port) | PortConnectStatusChange | (wasEnabled ? PortEnableChange : 0));

        // While the controller is suspended (EGSM), a connect or disconnect
        // is the wake event: it sets Resume Detect and asks the host to
        // force a global resume, exactly as a real root hub would.
        if ((_command & CommandEnterGlobalSuspend) != 0)
        {
            _status |= StatusResumeDetect;
            _command |= CommandForceGlobalResume;
            UpdateInterrupt();
        }
    }

    private ushort PortPresence(int port) => _devices[port] is { } device
        ? (ushort)(PortConnectStatus | PortAlwaysOne | (device.LowSpeed ? (ushort)(PortLowSpeedDeviceAttached | PortLineStatusDMinus) : PortLineStatusDPlus))
        : PortAlwaysOne;

    public void Reset()
    {
        _command = _interruptEnable = _frame = 0;
        _status = StatusHalted;
        _frameBase = 0;
        _sof = DefaultStartOfFrameValue;
        _elapsed = 0;
        _ioc = _shortPacket = false;
        for (int i = 0; i < PortCount; i++)
        {
            _devices[i]?.Reset();
            _ports[i] = (ushort)(PortPresence(i) | (_devices[i] is null ? 0 : PortConnectStatusChange));
        }
        UpdateInterrupt();
    }

    private int Offset(ushort port) => _ioWindow.MappedBase is { } address ? port - address : -1;

    private ushort ReadRegister(int offset) => offset switch
    {
        RegisterCommand => _command,
        RegisterStatus => _status,
        RegisterInterruptEnable => _interruptEnable,
        RegisterFrameNumber => _frame,
        RegisterFrameBaseLow => (ushort)_frameBase,
        RegisterFrameBaseHigh => (ushort)(_frameBase >> FrameBaseHighShift),
        RegisterStartOfFrameModify => _sof,
        RegisterPort0 => _ports[0],
        RegisterPort1 => _ports[1],
        _ => 0,
    };

    public byte ReadByte(ushort port)
    {
        int offset = Offset(port);
        return (byte)(ReadRegister(offset & ~1) >> ((offset & 1) * 8));
    }

    public ushort ReadWord(ushort port) => ReadRegister(Offset(port));

    public uint ReadDword(ushort port) => (uint)ReadWord(port) | ((uint)ReadWord((ushort)(port + 2)) << 16);

    public void WriteByte(ushort port, byte value)
    {
        int offset = Offset(port);
        if (offset is RegisterFrameNumber or RegisterFrameNumber + 1 or >= RegisterPort0)
        {
            return; // FRNUM/PORTSC require word writes
        }

        int shift = (offset & 1) * 8;
        if ((offset & ~1) == RegisterStatus)
        {
            // USBSTS is write-one-to-clear; merging a byte write through
            // ReadRegister would re-arm whatever the other byte already
            // read back as 1, so route it through WriteWord unmixed.
            WriteWord((ushort)(port & ~1), (ushort)(value << shift));
            return;
        }

        ushort combined = (ushort)((ReadRegister(offset & ~1) & ~(0xFF << shift)) | value << shift);
        WriteWord((ushort)(port & ~1), combined);
    }

    public void WriteDword(ushort port, uint value)
    {
        WriteWord(port, (ushort)value);
        WriteWord((ushort)(port + 2), (ushort)(value >> 16));
    }

    public void WriteWord(ushort port, ushort value)
    {
        switch (Offset(port))
        {
            case RegisterCommand:
                if ((value & (CommandHostControllerReset | CommandGlobalReset)) != 0)
                {
                    Reset();
                    _command = (ushort)(value & CommandGlobalReset);
                    break;
                }
                _command = (ushort)(value & (CommandRegisterByteMask & ~(CommandHostControllerReset | CommandGlobalReset)));
                _status = (ushort)((_status & ~StatusHalted) | ((_command & CommandRun) == 0 ? StatusHalted : 0));
                if ((_command & CommandRun) == 0)
                {
                    _elapsed = 0;
                }
                break;

            case RegisterStatus:
                _status &= (ushort)~(value & StatusWriteOneToClearMask);
                if ((value & StatusUsbInterrupt) != 0)
                {
                    _ioc = _shortPacket = false;
                }
                break;

            case RegisterInterruptEnable:
                _interruptEnable = (ushort)(value & (InterruptEnableTimeoutCrc | InterruptEnableResume
                    | InterruptEnableComplete | InterruptEnableShortPacket));
                break;

            case RegisterFrameNumber:
                if ((_command & CommandRun) == 0)
                {
                    _frame = (ushort)(value & FrameNumberMask);
                }
                break;

            case RegisterFrameBaseLow:
                _frameBase = (_frameBase & FrameBaseUpperHalfMask) | (uint)(value & FrameBaseLowWritableMask);
                break;

            case RegisterFrameBaseHigh:
                _frameBase = (_frameBase & FrameBaseLowerHalfMask) | (uint)value << FrameBaseHighShift;
                break;

            case RegisterStartOfFrameModify:
                _sof = (byte)(value & StartOfFrameMask);
                break;

            case RegisterPort0:
            case RegisterPort1:
                int index = (Offset(port) - RegisterPort0) / PortRegisterStride;
                ushort old = _ports[index];
                ushort control = (ushort)(value & PortWritableMask);
                if (_devices[index] is null || (control & PortReset) != 0)
                {
                    // Nothing is plugged in, or reset is being asserted:
                    // either way the port cannot read as enabled.
                    control &= unchecked((ushort)~PortEnabled);
                }
                if ((control & PortReset) != 0 && (old & PortReset) == 0)
                {
                    _devices[index]?.Reset();
                }
                _ports[index] = (ushort)(PortPresence(index) | control | (old & PortChangeMask & ~value));
                break;
        }
        UpdateInterrupt();
    }

    private void UpdateInterrupt() => _router.SetInterrupt(Function.Address, PciInterruptPinA,
        (_status & (StatusHostSystemError | StatusProcessError)) != 0
        || ((_status & StatusErrorInterrupt) != 0 && (_interruptEnable & InterruptEnableTimeoutCrc) != 0)
        || ((_status & StatusResumeDetect) != 0 && (_interruptEnable & InterruptEnableResume) != 0)
        || (_ioc && (_interruptEnable & InterruptEnableComplete) != 0)
        || (_shortPacket && (_interruptEnable & InterruptEnableShortPacket) != 0));

    public void Advance(double microseconds)
    {
        foreach (var device in _devices)
        {
            device?.Advance(microseconds);
        }

        if ((_command & (CommandRun | CommandGlobalReset | CommandEnterGlobalSuspend)) != CommandRun)
        {
            return;
        }

        _elapsed += microseconds;
        double duration = (SofModBaseClockCount + _sof) / ClocksPerMicrosecond;
        while (_elapsed >= duration && (_command & CommandRun) != 0)
        {
            _elapsed -= duration;
            if (Function.BusMasterEnabled)
            {
                ExecuteFrame();
            }
            _frame = (ushort)((_frame + 1) & FrameNumberMask);
            UpdateInterrupt();
        }
    }

    private bool InRam(uint address, int length) => address <= _memory.RamSize && (uint)length <= _memory.RamSize - address;

    private void Fatal(bool busError = false)
    {
        _status |= busError ? (ushort)(StatusHostSystemError | StatusHalted) : (ushort)(StatusProcessError | StatusHalted);
        _command &= unchecked((ushort)~CommandRun);
    }

    private void ExecuteFrame()
    {
        uint entry = _frameBase + (_frame & FrameListIndexMask) * 4;
        if (!InRam(entry, 4))
        {
            Fatal(true);
            return;
        }

        uint link = _memory.ReadDword(entry);
        uint horizontal = LinkTerminate;
        uint queue = 0;

        // Guest lists may be circular for bandwidth reclamation. A bounded
        // walk represents the finite frame budget and never hangs the host.
        for (int count = 0; count < FrameListSize && (link & LinkTerminate) == 0; count++)
        {
            uint address = link & LinkAddressMask;
            if (!InRam(address, 16))
            {
                Fatal(true);
                return;
            }

            if ((link & LinkQueueHead) != 0)
            {
                queue = address;
                horizontal = _memory.ReadDword(queue);
                link = _memory.ReadDword(queue + 4);
                if ((link & LinkTerminate) != 0)
                {
                    // The queue's element pointer is empty; move on
                    // through the frame list via the queue head itself.
                    link = horizontal;
                    queue = 0;
                }
                continue;
            }

            uint next = _memory.ReadDword(address);
            bool advance = ExecuteTd(address, queue != 0);
            if ((_command & CommandRun) == 0)
            {
                return;
            }

            if (queue != 0)
            {
                if (advance)
                {
                    _memory.WriteDword(queue + 4, next);
                }
                if (!advance || (next & (LinkTerminate | LinkVerticalFirst)) != LinkVerticalFirst)
                {
                    // Either the TD did not complete, or the next entry
                    // does not continue the same queue depth-first: fall
                    // back to the queue head's breadth-first successor.
                    link = horizontal;
                    queue = 0;
                }
                else
                {
                    link = next;
                }
            }
            else
            {
                link = next;
            }

            if ((_command & CommandSoftwareDebug) != 0)
            {
                // Software debug mode executes one element and halts.
                _command &= unchecked((ushort)~CommandRun);
                _status |= StatusHalted;
                return;
            }
        }
    }

    private bool ExecuteTd(uint address, bool queued)
    {
        uint control = _memory.ReadDword(address + 4);
        if ((control & TdControlActive) == 0)
        {
            if ((control & TdControlIoc) != 0)
            {
                _ioc = true;
                _status |= StatusUsbInterrupt;
            }
            return false;
        }

        uint token = _memory.ReadDword(address + 8);
        uint buffer = _memory.ReadDword(address + 12);
        byte pid = (byte)token;
        byte deviceAddress = (byte)((token >> TokenAddressShift) & TokenAddressMask);
        byte endpoint = (byte)((token >> TokenEndpointShift) & TokenEndpointMask);
        int length = (int)(((token >> TokenMaxLengthShift) + 1) & TokenMaxLengthMask);
        if (pid is not (PidIn or PidOut or PidSetup))
        {
            Fatal();
            return false;
        }
        if (!InRam(buffer, length))
        {
            Fatal(true);
            return false;
        }

        Span<byte> packet = stackalloc byte[MaxTransferLength];
        packet = packet[..length];
        if (pid != PidIn)
        {
            for (int i = 0; i < length; i++)
            {
                packet[i] = _memory.ReadByte(buffer + (uint)i);
            }
        }

        // A device several tiers behind a hub is found the same way one
        // plugged directly into a root port is: IUsbDevice.Find walks the
        // tree by address, one hop per hub, and a disabled/reset/suspended
        // root port forwards nothing, exactly like a disabled downstream
        // hub port. Real UHCI hardware precedes a low-speed transaction
        // with a PRE PID so the preamble reaches a low-speed device behind
        // a full-speed hub at the electrical level; this emulation has no
        // wire to precondition, so honouring the TD's LS bit against
        // whatever Find() returns -- at any depth -- has the same effect.
        bool lowSpeed = (control & TdControlLowSpeed) != 0;
        IUsbDevice? device = null;
        for (int i = 0; i < PortCount; i++)
        {
            if ((_ports[i] & PortActiveMask) != PortEnabled)
            {
                continue;
            }
            if (_devices[i]?.Find(deviceAddress) is { } found && found.LowSpeed == lowSpeed)
            {
                device = found;
            }
        }

        var result = device?.Transfer(pid, endpoint, (token & TokenDataToggleBit) != 0, packet)
            ?? new UsbTransactionResult(UsbHandshake.Timeout);
        uint updated = control & ~TdControlStatusBitsMask;
        bool advance = false;
        switch (result.Handshake)
        {
            case UsbHandshake.Ack:
                if (result.Length < 0 || result.Length > length)
                {
                    updated = (updated & ~TdControlActive) | TdControlStalled | TdControlBabbleDetected;
                    _status |= StatusErrorInterrupt;
                    break;
                }
                if (pid == PidIn)
                {
                    for (int i = 0; i < result.Length; i++)
                    {
                        _memory.WriteByte(buffer + (uint)i, packet[i]);
                    }
                }
                updated = (updated & ~(TdControlActive | TdActualLengthMask)) | ((uint)(result.Length - 1) & TdActualLengthMask);
                advance = true;
                if (queued && pid == PidIn && result.Length < length && (control & TdControlShortPacketDetect) != 0)
                {
                    _shortPacket = true;
                    _status |= StatusUsbInterrupt;
                    advance = false;
                }
                break;

            case UsbHandshake.Nak:
                updated |= TdControlNakReceived;
                break;

            case UsbHandshake.Stall:
                updated = (updated & ~TdControlActive) | TdControlStalled;
                _status |= StatusErrorInterrupt;
                break;

            case UsbHandshake.Timeout:
                updated |= TdControlCrcTimeoutError;
                uint retries = (control >> TdErrorCounterShift) & TdErrorCounterMask;
                if (retries != 0)
                {
                    updated = (updated & ~(TdErrorCounterMask << TdErrorCounterShift)) | ((retries - 1) << TdErrorCounterShift);
                    if (retries == 1)
                    {
                        updated = (updated & ~TdControlActive) | TdControlStalled;
                        _status |= StatusErrorInterrupt;
                    }
                }
                break;
        }

        if ((updated & TdControlActive) == 0 && (control & TdControlIoc) != 0)
        {
            _ioc = true;
            _status |= StatusUsbInterrupt;
        }
        _memory.WriteDword(address + 4, updated);
        return advance;
    }

    public byte[] Capture()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(_command);
        writer.Write(_status);
        writer.Write(_interruptEnable);
        writer.Write(_frame);
        writer.Write(_frameBase);
        writer.Write(_sof);
        writer.Write(_elapsed);
        writer.Write(_ioc);
        writer.Write(_shortPacket);
        for (int i = 0; i < PortCount; i++)
        {
            writer.Write(_ports[i]);
            writer.Write(_devices[i]?.GetType().FullName ?? "");
            byte[] state = _devices[i]?.Capture() ?? [];
            writer.Write(state.Length);
            writer.Write(state);
        }
        return stream.ToArray();
    }

    public void Restore(ReadOnlySpan<byte> state)
    {
        if (state.IsEmpty)
        {
            Reset();
            return;
        }

        using var reader = new BinaryReader(new MemoryStream(state.ToArray()));
        ushort command = reader.ReadUInt16();
        ushort status = reader.ReadUInt16();
        ushort interrupts = reader.ReadUInt16();
        ushort frame = reader.ReadUInt16();
        uint frameBase = reader.ReadUInt32();
        byte sof = reader.ReadByte();
        double elapsed = reader.ReadDouble();
        bool ioc = reader.ReadBoolean();
        bool shortPacket = reader.ReadBoolean();
        var ports = new ushort[PortCount];
        var blocks = new byte[PortCount][];
        for (int i = 0; i < PortCount; i++)
        {
            ports[i] = reader.ReadUInt16();
            if (reader.ReadString() != (_devices[i]?.GetType().FullName ?? ""))
            {
                throw new InvalidDataException("USB snapshot device topology differs from the machine.");
            }
            int length = reader.ReadInt32();
            if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            {
                throw new InvalidDataException("Invalid USB snapshot payload.");
            }
            blocks[i] = reader.ReadBytes(length);
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length || !double.IsFinite(elapsed) || elapsed < 0)
        {
            throw new InvalidDataException("Invalid USB controller snapshot.");
        }

        _command = command;
        _status = status;
        _interruptEnable = interrupts;
        _frame = frame;
        _frameBase = frameBase;
        _sof = sof;
        _elapsed = elapsed;
        _ioc = ioc;
        _shortPacket = shortPacket;
        for (int i = 0; i < PortCount; i++)
        {
            _ports[i] = ports[i];
            _devices[i]?.Restore(blocks[i]);
        }
        UpdateInterrupt();
    }
}
