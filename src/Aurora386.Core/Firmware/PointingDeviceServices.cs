using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Devices.Input;
using Aurora386.Core.Devices.Interrupts;
using Aurora386.Core.Devices.Timing;
using Aurora386.Core.Devices.System;
using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Core.Firmware;

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
    private readonly Machine _machine;

    public PointingDeviceServices(Machine machine) => _machine = machine;

    /// <summary>The far routine installed with C207h; 0000:0000 when none.</summary>
    public ushort HandlerSegment { get; private set; }
    public ushort HandlerOffset { get; private set; }

    /// <summary>Whether C200h has switched packet delivery on.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Whether C205h has run; enabling before it is refused.</summary>
    public bool Initialized { get; private set; }

    public byte SampleRate { get; private set; } = 100;
    public byte Resolution { get; private set; } = 2;
    public bool Scaling2To1 { get; private set; }

    private readonly byte[] _packet = new byte[3];
    private int _received;

    private const byte Success = 0x00, InvalidFunction = 0x01, InvalidInput = 0x02, NoHandler = 0x05;

    public void Handle(CpuNs.Cpu cpu)
    {
        byte subFunction = cpu.State.GetReg8(0);   // AL
        byte bh = cpu.State.GetReg8(7);

        switch (subFunction)
        {
            case 0x00:   // enable (BH=1) or disable (BH=0) the device
                if (bh > 1) { Fail(cpu, InvalidInput); return; }
                if (bh == 1 && (HandlerSegment | HandlerOffset) == 0) { Fail(cpu, NoHandler); return; }
                SetEnabled(bh == 1);
                Succeed(cpu);
                return;

            case 0x01:   // reset: BH = device id, BL = AAh
                SetEnabled(false);
                _received = 0;
                SampleRate = 100; Resolution = 2; Scaling2To1 = false;
                cpu.State.Bx = 0x00AA;
                Succeed(cpu);
                return;

            case 0x02:   // sample rate, BH = 0..6 for 10,20,40,60,80,100,200 per second
                if (bh > 6) { Fail(cpu, InvalidInput); return; }
                SampleRate = new byte[] { 10, 20, 40, 60, 80, 100, 200 }[bh];
                Succeed(cpu);
                return;

            case 0x03:   // resolution, BH = 0..3 for 1,2,4,8 counts per millimetre
                if (bh > 3) { Fail(cpu, InvalidInput); return; }
                Resolution = bh;
                Succeed(cpu);
                return;

            case 0x04:   // device type: BH = id, 00h for a standard mouse
                cpu.State.SetReg8(7, 0x00);
                Succeed(cpu);
                return;

            case 0x05:   // initialise: BH = data package size, 1..8 bytes
                if (bh is < 1 or > 8) { Fail(cpu, InvalidInput); return; }
                Initialized = true;
                SetEnabled(false);
                _received = 0;
                Succeed(cpu);
                return;

            case 0x06:   // extended commands
                switch (bh)
                {
                    case 0x00:   // status: BL flags, CL resolution, DL sample rate
                        cpu.State.SetReg8(3, (byte)((Scaling2To1 ? 0x10 : 0) | (Enabled ? 0x20 : 0)));
                        cpu.State.SetReg8(1, Resolution);
                        cpu.State.SetReg8(2, SampleRate);
                        Succeed(cpu);
                        return;
                    case 0x01: Scaling2To1 = false; Succeed(cpu); return;
                    case 0x02: Scaling2To1 = true; Succeed(cpu); return;
                    default: Fail(cpu, InvalidInput); return;
                }

            case 0x07:   // install the far handler at ES:BX; 0000:0000 removes it
                HandlerSegment = cpu.State.Es.Selector;
                HandlerOffset = cpu.State.Bx;
                if ((HandlerSegment | HandlerOffset) == 0) SetEnabled(false);

                // Mirrored into the EBDA where the PS/2 BIOS kept it (offset
                // 22h), for software that peeks there.
                uint ebda = (uint)Machine.ExtendedBiosDataSegment << 4;
                _machine.Memory.WriteWord(ebda + 0x22, HandlerOffset);
                _machine.Memory.WriteWord(ebda + 0x24, HandlerSegment);
                Succeed(cpu);
                return;

            default:
                Fail(cpu, InvalidFunction);
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
        io.WriteByte(0x64, 0xA8);
        io.WriteByte(0x64, 0x60);
        byte commandByte = (byte)(controller.CommandByte & ~0x20);
        io.WriteByte(0x60, enabled ? (byte)(commandByte | 0x02) : (byte)(commandByte & ~0x02));

        io.WriteByte(0x64, 0xD4);
        io.WriteByte(0x60, enabled ? (byte)0xF4 : (byte)0xF5);           // mouse: reporting on / off
        AwaitAcknowledge();

        // The interrupt itself, at the controllers. The PS/2 BIOS opens IRQ 12
        // on the slave -- and the cascade line on the master, without which
        // nothing from the slave arrives -- when the device is enabled, and
        // closes it again on disable. A DOS extender that masked the line
        // before Windows started would otherwise leave the driver installed,
        // enabled, and deaf.
        byte slaveMask = io.ReadByte(0xA1);
        io.WriteByte(0xA1, enabled ? (byte)(slaveMask & ~0x10) : (byte)(slaveMask | 0x10));
        if (enabled) io.WriteByte(0x21, (byte)(io.ReadByte(0x21) & ~0x04));
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
        for (int attempt = 0; attempt < 8; attempt++)
        {
            _machine.Keyboard.PollMouse();
            byte status = io.ReadByte(0x64);
            if ((status & 0x21) == 0x21)                  // output full, and it is mouse data
            {
                if (io.ReadByte(0x60) == 0xFA) return;
                continue;
            }
            if (_machine.Ps2Mouse.TryDequeue(out byte queued))
            {
                if (queued == 0xFA) return;
                continue;
            }
            return;
        }
    }

    /// <summary>
    /// One byte has come in on IRQ 12; the ROM entry has read it into AH and
    /// sent the EOI. Three make a packet, and a packet goes to the handler.
    /// </summary>
    /// <summary>Bytes seen on IRQ 12 and packets handed to the guest, for diagnostics.</summary>
    public long BytesReceived { get; private set; }
    public long PacketsDelivered { get; private set; }
    public long PacketsDropped { get; private set; }

    public void OnInterrupt(CpuNs.Cpu cpu)
    {
        byte value = cpu.State.GetReg8(4);   // AH
        BytesReceived++;

        if (!Enabled || (HandlerSegment | HandlerOffset) == 0) return;

        // The first byte of a packet always has bit 3 set; a byte without it
        // in first position is a leftover from a packet that lost sync, and
        // waiting for a proper header is how the real BIOS resynchronised.
        if (_received == 0 && (value & 0x08) == 0) return;

        _packet[_received++] = value;
        if (_received < 3) return;
        _received = 0;

        // The handler address is a real-mode far pointer, and the ROM entry
        // that brought us here is real-mode code: from real mode proper, or
        // from a virtual 8086 task when a monitor such as WIN386 reflected
        // IRQ 12 into it. Anywhere else there is nothing sensible to call.
        if (cpu.State.ProtectedMode && !cpu.State.Virtual8086Mode) { PacketsDropped++; return; }

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
        Push(cpu, 0xF000);
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
        cpu.State.SetReg8(4, Success);
        cpu.SetCarryInStackedFlags(false);
    }

    private static void Fail(CpuNs.Cpu cpu, byte code)
    {
        cpu.State.SetReg8(4, code);
        cpu.SetCarryInStackedFlags(true);
    }
}
