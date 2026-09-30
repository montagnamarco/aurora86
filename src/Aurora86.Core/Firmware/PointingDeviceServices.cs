using Aurora86.Core.Devices.Storage;
using Aurora86.Core.Devices.Input;
using Aurora86.Core.Devices.Interrupts;
using Aurora86.Core.Devices.Timing;
using Aurora86.Core.Devices.System;
using CpuNs = Aurora86.Core.Cpu;

namespace Aurora86.Core.Firmware;

/// <summary>
/// INT 15h, AH=C2h: the BIOS pointing-device interface for the PS/2 mouse.
///
/// The PS/2 mouse has no interrupt of its own that software can simply hook:
/// its bytes arrive through the keyboard controller on IRQ 12, three to a
/// packet, and somebody has to collect them. On the PS/2 the BIOS did, and
/// offered this interface on top: a driver enables the device, installs a
/// far routine with C207h, and from then on the BIOS calls that routine once
/// per complete packet with four words on the stack -- status, X, Y, Z. This
/// is how MOUSE.DRV in Windows 3.x and the Microsoft MOUSE.COM use the PS/2
/// port; without it they look for a serial mouse instead.
///
/// Results follow the convention of the rest of INT 15h: AH holds a status
/// code and carry is set on any error. The codes that matter are 00h success,
/// 01h invalid function, 02h invalid input, 03h interface error and 05h no
/// far-call routine installed.
/// </summary>
public sealed class PointingDeviceServices
{
    // Subfunctions of INT 15h AH=C2h, passed in AL.
    private const byte SubfunctionEnable = 0x00;
    private const byte SubfunctionReset = 0x01;
    private const byte SubfunctionSetSampleRate = 0x02;
    private const byte SubfunctionSetResolution = 0x03;
    private const byte SubfunctionGetDeviceType = 0x04;
    private const byte SubfunctionInitialize = 0x05;
    private const byte SubfunctionExtended = 0x06;
    private const byte SubfunctionSetHandler = 0x07;

    // Commands of subfunction 06h, passed in BH.
    private const byte ExtendedGetStatus = 0x00;
    private const byte ExtendedScalingOneToOne = 0x01;
    private const byte ExtendedScalingTwoToOne = 0x02;

    // Status codes returned in AH.
    private const byte Success = 0x00;
    private const byte InvalidFunction = 0x01;
    private const byte InvalidInput = 0x02;
    private const byte NoHandler = 0x05;

    /// <summary>Sample rates, in reports per second, that BH values 0-6 select.</summary>
    private static readonly byte[] SampleRates = [10, 20, 40, 60, 80, 100, 200];

    private const byte DefaultSampleRate = 100;

    /// <summary>Resolution 2 is four counts per millimetre, the mouse's power-up setting.</summary>
    private const byte DefaultResolution = 2;

    private const byte MaximumResolution = 3;
    private const byte MinimumPackageSize = 1;
    private const byte MaximumPackageSize = 8;

    /// <summary>What reset returns in BX: device id 00h in BH, and the self-test pass code AAh in BL.</summary>
    private const ushort ResetResult = 0x00AA;

    /// <summary>The device id of a standard PS/2 mouse.</summary>
    private const byte StandardMouseId = 0x00;

    // Status flags that extended status returns in BL.
    private const byte StatusScalingTwoToOne = 0x10;
    private const byte StatusEnabled = 0x20;

    // Where the PS/2 BIOS kept the handler pointer inside the EBDA.
    private const uint EbdaHandlerOffset = 0x22;
    private const uint EbdaHandlerSegment = 0x24;

    // The 8042 keyboard controller: data and command/status ports, the
    // commands used here, and the bits they touch.
    private const ushort ControllerData = 0x60;
    private const ushort ControllerCommand = 0x64;
    private const byte CommandEnableAuxiliary = 0xA8;
    private const byte CommandWriteCommandByte = 0x60;
    private const byte CommandWriteToAuxiliary = 0xD4;
    private const byte CommandByteAuxiliaryInterrupt = 0x02;
    private const byte CommandByteAuxiliaryDisabled = 0x20;
    private const byte StatusOutputFull = 0x01;
    private const byte StatusAuxiliaryData = 0x20;

    // Mouse commands sent through the auxiliary port.
    private const byte MouseEnableReporting = 0xF4;
    private const byte MouseDisableReporting = 0xF5;

    /// <summary>How many reply bytes the acknowledgement drain may read before giving up.</summary>
    private const int AcknowledgeAttempts = 16;

    // The 8259 mask registers and the lines the mouse needs open.
    private const ushort MasterMaskPort = 0x21;
    private const ushort SlaveMaskPort = 0xA1;
    private const byte SlaveIrq12Bit = 0x10;
    private const byte MasterCascadeBit = 0x04;

    private const int PacketLength = 3;

    /// <summary>Bit 3 of a packet's first byte is always set; it is how a lost packet is resynchronised.</summary>
    private const byte PacketHeaderMarker = 0x08;

    /// <summary>The segment of the BIOS ROM, where the callback return stub lives.</summary>
    private const ushort BiosSegment = 0xF000;

    private readonly Machine _machine;

    public PointingDeviceServices(Machine machine) => _machine = machine;

    /// <summary>The far routine installed with C207h; 0000:0000 when none.</summary>
    public ushort HandlerSegment { get; private set; }
    public ushort HandlerOffset { get; private set; }

    /// <summary>Whether C200h has switched packet delivery on.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Whether C205h has run; enabling before it is refused.</summary>
    public bool Initialized { get; private set; }

    public byte SampleRate { get; private set; } = DefaultSampleRate;
    public byte Resolution { get; private set; } = DefaultResolution;
    public bool Scaling2To1 { get; private set; }

    /// <summary>Bytes seen on IRQ 12 and packets handed to the guest, for diagnostics.</summary>
    public long BytesReceived { get; private set; }
    public long PacketsDelivered { get; private set; }
    public long PacketsDropped { get; private set; }

    private readonly byte[] _packet = new byte[PacketLength];
    private int _received;

    private bool HasHandler => (HandlerSegment | HandlerOffset) != 0;

    public void Handle(CpuNs.Cpu cpu)
    {
        byte subFunction = cpu.State.GetReg8((int)CpuNs.Reg8.Al);
        byte bh = cpu.State.GetReg8((int)CpuNs.Reg8.Bh);

        switch (subFunction)
        {
            case SubfunctionEnable:   // enable (BH=1) or disable (BH=0) the device
                if (bh > 1)
                {
                    Fail(cpu, InvalidInput);
                    return;
                }

                if (bh == 1 && !HasHandler)
                {
                    Fail(cpu, NoHandler);
                    return;
                }

                SetEnabled(bh == 1);
                Succeed(cpu);
                return;

            case SubfunctionReset:
                SetEnabled(false);
                _received = 0;
                SampleRate = DefaultSampleRate;
                Resolution = DefaultResolution;
                Scaling2To1 = false;
                cpu.State.Bx = ResetResult;
                Succeed(cpu);
                return;

            case SubfunctionSetSampleRate:
                if (bh >= SampleRates.Length)
                {
                    Fail(cpu, InvalidInput);
                    return;
                }

                SampleRate = SampleRates[bh];
                Succeed(cpu);
                return;

            case SubfunctionSetResolution:   // BH = 0..3 for 1, 2, 4, 8 counts per millimetre
                if (bh > MaximumResolution)
                {
                    Fail(cpu, InvalidInput);
                    return;
                }

                Resolution = bh;
                Succeed(cpu);
                return;

            case SubfunctionGetDeviceType:
                cpu.State.SetReg8((int)CpuNs.Reg8.Bh, StandardMouseId);
                Succeed(cpu);
                return;

            case SubfunctionInitialize:   // BH = data package size in bytes
                if (bh is < MinimumPackageSize or > MaximumPackageSize)
                {
                    Fail(cpu, InvalidInput);
                    return;
                }

                Initialized = true;
                SetEnabled(false);
                _received = 0;
                Succeed(cpu);
                return;

            case SubfunctionExtended:
                HandleExtended(cpu, bh);
                return;

            case SubfunctionSetHandler:   // install the far handler at ES:BX; 0000:0000 removes it
                HandlerSegment = cpu.State.Es.Selector;
                HandlerOffset = cpu.State.Bx;
                if (!HasHandler)
                {
                    SetEnabled(false);
                }

                // Mirrored into the EBDA where the PS/2 BIOS kept it, for
                // software that peeks there.
                uint ebda = (uint)Machine.ExtendedBiosDataSegment << 4;
                _machine.Memory.WriteWord(ebda + EbdaHandlerOffset, HandlerOffset);
                _machine.Memory.WriteWord(ebda + EbdaHandlerSegment, HandlerSegment);
                Succeed(cpu);
                return;

            default:
                Fail(cpu, InvalidFunction);
                return;
        }
    }

    /// <summary>Subfunction 06h: status (BL flags, CL resolution, DL sample rate) and the scaling commands.</summary>
    private void HandleExtended(CpuNs.Cpu cpu, byte command)
    {
        switch (command)
        {
            case ExtendedGetStatus:
                byte flags = 0;
                if (Scaling2To1)
                {
                    flags |= StatusScalingTwoToOne;
                }

                if (Enabled)
                {
                    flags |= StatusEnabled;
                }

                cpu.State.SetReg8((int)CpuNs.Reg8.Bl, flags);
                cpu.State.SetReg8((int)CpuNs.Reg8.Cl, Resolution);
                cpu.State.SetReg8((int)CpuNs.Reg8.Dl, SampleRate);
                Succeed(cpu);
                return;

            case ExtendedScalingOneToOne:
                Scaling2To1 = false;
                Succeed(cpu);
                return;

            case ExtendedScalingTwoToOne:
                Scaling2To1 = true;
                Succeed(cpu);
                return;

            default:
                Fail(cpu, InvalidInput);
                return;
        }
    }

    /// <summary>
    /// Turns packet delivery on or off, and programs the hardware to match:
    /// the auxiliary port and its interrupt on the 8042, and reporting on
    /// the mouse itself, exactly as MOUSE.COM would have done by hand.
    /// </summary>
    private void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        _received = 0;

        Devices.Input.Keyboard8042 controller = _machine.Keyboard;
        IO.IoBus io = _machine.Io;

        // The auxiliary port is opened either way, so that the mouse's
        // acknowledgement can be read back here rather than turn up later as
        // a stray byte on IRQ 12.
        io.WriteByte(ControllerCommand, CommandEnableAuxiliary);
        io.WriteByte(ControllerCommand, CommandWriteCommandByte);
        byte commandByte = (byte)(controller.CommandByte & ~CommandByteAuxiliaryDisabled);
        byte newCommandByte = enabled
            ? (byte)(commandByte | CommandByteAuxiliaryInterrupt)
            : (byte)(commandByte & ~CommandByteAuxiliaryInterrupt);
        io.WriteByte(ControllerData, newCommandByte);

        io.WriteByte(ControllerCommand, CommandWriteToAuxiliary);
        io.WriteByte(ControllerData, enabled ? MouseEnableReporting : MouseDisableReporting);
        AwaitAcknowledge();

        // The interrupt itself, at the controllers, and only ever opened --
        // never closed.
        //
        // A real PS/2 BIOS leaves IRQ 12 unmasked from POST onwards, and that
        // detail matters more than it looks. Windows' MOUSE.DRV installs its
        // own IRQ 12 handler like this: read the mask, remember it, set the
        // bit while it works, then put back what it remembered. If the BIOS
        // opens the line only when C200h enables the device, the driver has
        // already photographed a mask with IRQ 12 closed, and faithfully
        // restores that -- leaving a mouse that is enabled, has a handler,
        // and is deaf. Opening at POST and never closing keeps the snapshot
        // honest whenever the driver takes it.
        if (enabled)
        {
            OpenMouseInterrupt(_machine);
        }
    }

    /// <summary>
    /// Unmasks IRQ 12 on the slave controller and the cascade line on the
    /// master, without which nothing from the slave arrives at the CPU.
    /// </summary>
    public static void OpenMouseInterrupt(Machine machine)
    {
        IO.IoBus io = machine.Io;
        io.WriteByte(SlaveMaskPort, (byte)(io.ReadByte(SlaveMaskPort) & ~SlaveIrq12Bit));
        io.WriteByte(MasterMaskPort, (byte)(io.ReadByte(MasterMaskPort) & ~MasterCascadeBit));
    }

    /// <summary>
    /// Reads the mouse's FAh reply to a command synchronously, as the real
    /// BIOS does while it waits for the command to complete. Left in the
    /// controller it would be delivered on IRQ 12 and mistaken for the first
    /// byte of a packet -- FAh even has bit 3 set, like a packet header.
    /// </summary>
    private void AwaitAcknowledge()
    {
        IO.IoBus io = _machine.Io;
        const byte AuxiliaryByteWaiting = StatusOutputFull | StatusAuxiliaryData;

        // Drain the whole reply, not just its first byte. A reset answers
        // FAh AAh 00h, and an acknowledgement byte left in the queue would
        // later surface on IRQ 12 and be mistaken for the header of a packet.
        for (int attempt = 0; attempt < AcknowledgeAttempts; attempt++)
        {
            _machine.Keyboard.PollMouse();
            byte status = io.ReadByte(ControllerCommand);
            if ((status & AuxiliaryByteWaiting) == AuxiliaryByteWaiting)
            {
                io.ReadByte(ControllerData);   // consume the mouse byte
                continue;
            }

            if (_machine.Ps2Mouse.TryDequeue(out _))
            {
                continue;
            }

            break;
        }
    }

    /// <summary>
    /// One byte has come in on IRQ 12; the ROM entry has read it into AH and
    /// sent the EOI. Three make a packet, and a packet goes to the handler.
    /// </summary>
    public void OnInterrupt(CpuNs.Cpu cpu)
    {
        byte value = cpu.State.GetReg8((int)CpuNs.Reg8.Ah);
        BytesReceived++;

        if (!Enabled || !HasHandler)
        {
            return;
        }

        // The first byte of a packet always has bit 3 set; a byte without it
        // in first position is a leftover from a packet that lost sync, and
        // waiting for a proper header is how the real BIOS resynchronised.
        if (_received == 0 && (value & PacketHeaderMarker) == 0)
        {
            return;
        }

        _packet[_received] = value;
        _received++;
        if (_received < PacketLength)
        {
            return;
        }

        _received = 0;

        // The handler address is a real-mode far pointer, and the ROM entry
        // that brought us here is real-mode code: from real mode proper, or
        // from a virtual 8086 task when a monitor such as WIN386 reflected
        // IRQ 12 into it. Anywhere else there is nothing sensible to call.
        if (cpu.State.ProtectedMode && !cpu.State.Virtual8086Mode)
        {
            PacketsDropped++;
            return;
        }

        PacketsDelivered++;
        CallHandler(cpu, _packet[0], _packet[1], _packet[2]);
    }

    /// <summary>
    /// Redirects the guest into its handler as the BIOS far call would:
    /// status, X, Y and Z pushed as words, then the return address of a stub
    /// that discards them and returns to the interrupt entry, which finishes
    /// with POP AX and IRET.
    /// </summary>
    private void CallHandler(CpuNs.Cpu cpu, byte status, byte deltaX, byte deltaY)
    {
        Push(cpu, cpu.State.Cs.Selector);
        Push(cpu, (ushort)cpu.State.Eip);
        Push(cpu, status);
        Push(cpu, deltaX);
        Push(cpu, deltaY);
        Push(cpu, 0);                         // Z: no wheel on this mouse
        Push(cpu, BiosSegment);
        Push(cpu, BiosRom.Entry.PointingCallbackReturn);

        cpu.SetSegment(CpuNs.SegReg.Cs, HandlerSegment);
        cpu.State.Eip = HandlerOffset;
    }

    private static void Push(CpuNs.Cpu cpu, ushort value)
    {
        ushort sp = (ushort)(cpu.State.Sp - 2);
        cpu.State.Sp = sp;
        cpu.WriteMemoryWord(CpuNs.SegReg.Ss, sp, value);
    }

    private static void Succeed(CpuNs.Cpu cpu)
    {
        cpu.State.SetReg8((int)CpuNs.Reg8.Ah, Success);
        cpu.SetCarryInStackedFlags(false);
    }

    private static void Fail(CpuNs.Cpu cpu, byte code)
    {
        cpu.State.SetReg8((int)CpuNs.Reg8.Ah, code);
        cpu.SetCarryInStackedFlags(true);
    }
}
