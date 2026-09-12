using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Core.Firmware;

/// <summary>
/// Advanced Power Management 1.2: the interface through which an operating
/// system tells the BIOS it is idle, asks about batteries, and asks for the
/// power to go off.
///
/// There is no battery here and nothing to save power on, so most of what
/// the specification describes is answered with "fine" or "none". The one
/// call that does something is the one that sets the power state to off:
/// that is what turns "It is now safe to turn off your computer" into the
/// machine actually stopping, and Windows 95 will not offer the former
/// without an APM BIOS to give the latter.
///
/// The interface is reached three ways -- INT 15h AH=53h from real mode,
/// and a far call into a 16-bit or 32-bit code segment with the function
/// in AX once connected -- and all three end up here.
/// </summary>
public sealed class ApmBios
{
    private const byte ErrorNotConnected = 0x03;
    private const byte ErrorAlreadyConnected = 0x02;
    private const byte ErrorInvalidDevice = 0x09;
    private const byte ErrorInvalidState = 0x0A;
    private const byte ErrorNoEvents = 0x80;
    private const byte ErrorNotSupported = 0x86;

    private readonly Machine _machine;

    public enum Connection { None, RealMode, Protected16, Protected32 }

    public Connection Connected { get; private set; }

    /// <summary>The version the driver said it speaks, after 530Eh; 1.0 until then, as the specification requires.</summary>
    public (int Major, int Minor) DriverVersion { get; private set; } = (1, 0);

    public bool Enabled { get; private set; } = true;
    public bool Engaged { get; private set; } = true;

    public ApmBios(Machine machine) => _machine = machine;

    public void Handle(CpuNs.Cpu cpu, bool fromEntryPoint)
    {
        HandleFunction(cpu);
        // The protected entry returns with RETF, so carry belongs in the live
        // flags. Only INT 15h has a saved FLAGS word to update before IRET.
        if (!fromEntryPoint)
        {
            cpu.SetCarryInStackedFlags(cpu.Flags.Carry);
        }
    }

    private void HandleFunction(CpuNs.Cpu cpu)
    {
        byte function = cpu.State.GetReg8(0);   // AL
        ushort device = cpu.State.Bx;

        switch (function)
        {
            case 0x00:
                // Installation check. AH.AL is the version, BX spells "PM",
                // CX says which interfaces exist: bit 0 the 16-bit protected
                // one, bit 1 the 32-bit one. Bit 2 would mean CPU idle slows
                // the clock, which it does not.
                if (device != 0) { Fail(cpu, ErrorInvalidDevice); return; }
                cpu.State.Ax = 0x0102;
                cpu.State.Bx = 0x504D;
                cpu.State.Cx = 0x0003;
                Succeed(cpu);
                return;

            case 0x01:
                Connect(cpu, device, Connection.RealMode);
                return;

            case 0x02:
                // 16-bit protected mode: code segment, entry offset, data segment.
                if (!Connect(cpu, device, Connection.Protected16)) return;
                cpu.State.Ax = 0xF000;
                cpu.State.Bx = BiosRom.Entry.AdvancedPowerManagement;
                cpu.State.Cx = 0xF000;
                cpu.State.Si = 0xFFFF;             // code segment length
                cpu.State.Di = 0xFFFF;             // data segment length
                return;

            case 0x03:
                // 32-bit protected mode: the same entry, plus a 16-bit code
                // segment the BIOS might call into itself, and the lengths.
                if (!Connect(cpu, device, Connection.Protected32)) return;
                cpu.State.Ax = 0xF000;             // 32-bit code segment base, in paragraphs
                cpu.State.Ebx = BiosRom.Entry.AdvancedPowerManagement;
                cpu.State.Cx = 0xF000;             // 16-bit code segment
                cpu.State.Dx = 0xF000;             // data segment
                cpu.State.Esi = 0xFFFF_FFFF;       // 32-bit and 16-bit code segment lengths
                cpu.State.Di = 0xFFFF;             // data segment length
                return;

            case 0x04:
                if (Connected == Connection.None) { Fail(cpu, ErrorNotConnected); return; }
                Connected = Connection.None;
                DriverVersion = (1, 0);
                Succeed(cpu);
                return;

            case 0x05:   // CPU idle
            case 0x06:   // CPU busy
                // Idle would be the moment to sleep the host thread; the
                // machine's own HLT handling already does that when the
                // guest halts, and a driver that calls this in its idle loop
                // gets nothing worse than a fast return.
                Succeed(cpu);
                return;

            case 0x07:
                SetPowerState(cpu, device, cpu.State.Cx);
                return;

            case 0x08:   // enable/disable power management
                Enabled = cpu.State.Cx == 1;
                Succeed(cpu);
                return;

            case 0x09:   // restore BIOS defaults
                Succeed(cpu);
                return;

            case 0x0A:
                // Power status: on AC, no battery. FFh means "unknown" for
                // the battery fields, which is the honest answer when there
                // is no battery to ask.
                cpu.State.Bx = 0x01FF;             // AC line on; battery status unknown
                cpu.State.Cx = 0xFFFF;             // battery flag, percentage unknown
                cpu.State.Dx = 0xFFFF;             // remaining time unknown
                Succeed(cpu);
                return;

            case 0x0B:
                // Nothing ever happens: no lid, no battery, no resume.
                Fail(cpu, ErrorNoEvents);
                return;

            case 0x0C:   // get power state: everything is always on
                cpu.State.Cx = 0x0000;
                Succeed(cpu);
                return;

            case 0x0D:   // enable/disable device power management
                Succeed(cpu);
                return;

            case 0x0E:
            {
                // The driver announces the version it speaks, and the BIOS
                // answers with the one both will use: the lower of the two.
                int major = Math.Min((int)cpu.State.GetReg8(5), 1);   // CH
                int minor = major == 1 ? Math.Min((int)cpu.State.GetReg8(1), 2) : 0;   // CL
                DriverVersion = (major, minor);
                cpu.State.Ax = (ushort)((major << 8) | minor);
                Succeed(cpu);
                return;
            }

            case 0x0F:   // engage/disengage
                Engaged = cpu.State.Cx == 1;
                Succeed(cpu);
                return;

            case 0x10:   // capabilities: no batteries, no wake sources
                cpu.State.SetReg8(3, 0);           // BL: batteries
                cpu.State.Cx = 0x0000;
                Succeed(cpu);
                return;

            default:
                Fail(cpu, ErrorNotSupported);
                return;
        }
    }

    private bool Connect(CpuNs.Cpu cpu, ushort device, Connection kind)
    {
        if (device != 0) { Fail(cpu, ErrorInvalidDevice); return false; }
        if (Connected != Connection.None) { Fail(cpu, ErrorAlreadyConnected); return false; }
        Connected = kind;
        Succeed(cpu);
        return true;
    }

    private void SetPowerState(CpuNs.Cpu cpu, ushort device, ushort state)
    {
        // Device 0001h is "all devices", the one an operating system uses
        // to shut down; individual devices have nothing to switch. State
        // 0003h is off; standby and suspend (1, 2) are accepted and do
        // nothing, since there is nothing to wake up from.
        if (device != 0x0001 && (device & 0xFF00) == 0) { Fail(cpu, ErrorInvalidDevice); return; }
        if (state > 3) { Fail(cpu, ErrorInvalidState); return; }

        Succeed(cpu);
        if (device == 0x0001 && state == 3) _machine.RequestPowerOff();
    }

    private static void Succeed(CpuNs.Cpu cpu) => cpu.Flags.Carry = false;

    private static void Fail(CpuNs.Cpu cpu, byte error)
    {
        cpu.State.SetReg8(4, error);
        cpu.Flags.Carry = true;
    }
}
