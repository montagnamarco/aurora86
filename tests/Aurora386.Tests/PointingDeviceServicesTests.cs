using Aurora386.Core;
using Aurora386.Core.Bios;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices;

namespace Aurora386.Tests;

/// <summary>
/// INT 15h AH=C2h, the BIOS pointing-device interface that MOUSE.DRV and
/// MOUSE.COM use to reach a PS/2 mouse.
/// </summary>
public class PointingDeviceServicesTests
{
    private static Machine Started()
    {
        var machine = new Machine();
        machine.RunInstructions(2000);
        machine.MouseInterface = MouseInterface.Ps2;
        return machine;
    }

    /// <summary>INT 15h with AH=C2h and AL=<paramref name="sub"/>; returns AH.</summary>
    private static byte Call(Machine machine, byte sub, byte bh = 0)
    {
        machine.Cpu.State.Ax = (ushort)(0xC200 | sub);
        machine.Cpu.State.SetReg8(7, bh);
        // A flags image on the stack, as an INT would leave, for the carry.
        machine.Cpu.State.Sp = 0x7000;
        machine.Cpu.SetSegment(SegReg.Ss, 0x0000);
        machine.Memory.WriteWord(0x7004, 0x0202);
        machine.PointingDevice.Handle(machine.Cpu);
        return machine.Cpu.State.GetReg8(4);
    }

    private static bool Carry(Machine machine) => (machine.Memory.ReadWord(0x7004) & 1) != 0;

    [Fact]
    public void EnablingWithoutAHandlerIsRefusedWithCodeFive()
    {
        using Machine machine = Started();
        Assert.Equal(0x05, Call(machine, 0x00, bh: 1));
        Assert.True(Carry(machine));
        Assert.False(machine.PointingDevice.Enabled);
    }

    [Fact]
    public void ResetReportsAStandardMouse()
    {
        using Machine machine = Started();
        Assert.Equal(0x00, Call(machine, 0x01));
        Assert.False(Carry(machine));
        Assert.Equal((ushort)0x00AA, machine.Cpu.State.Bx);
    }

    [Fact]
    public void EnablingSwitchesTheMouseOnThroughTheController()
    {
        using Machine machine = Started();
        machine.Cpu.SetSegment(SegReg.Es, 0x0900);
        machine.Cpu.State.Bx = 0x0000;
        Assert.Equal(0x00, Call(machine, 0x07));            // install a handler
        Assert.Equal(0x00, Call(machine, 0x05, bh: 3));     // initialise, 3-byte packets
        Assert.Equal(0x00, Call(machine, 0x00, bh: 1));     // enable

        Assert.True(machine.PointingDevice.Enabled);
        Assert.True(machine.Ps2Mouse.Reporting);
        Assert.Equal(0x02, machine.Keyboard.CommandByte & 0x22);   // aux IRQ on, aux port on
    }

    [Fact]
    public void ACompletePacketReachesTheHandlerWithFourWordsOnTheStack()
    {
        using Machine machine = Started();

        // The handler: stores three of the words it was given at 0900:0080
        // and returns. The BIOS pushes status, X, Y, Z in that order and then
        // far-calls, so after PUSH BP they sit at [bp+0Ch], [bp+0Ah], [bp+8]
        // and [bp+6] -- the layout every PS/2 mouse driver hard-codes.
        byte[] handler =
        {
            0x55, 0x8B, 0xEC,                 // push bp; mov bp,sp
            0x8B, 0x46, 0x0C, 0x2E, 0xA3, 0x80, 0x00,   // mov ax,[bp+0Ch] (status); mov cs:[80h],ax
            0x8B, 0x46, 0x0A, 0x2E, 0xA3, 0x82, 0x00,   // X
            0x8B, 0x46, 0x08, 0x2E, 0xA3, 0x84, 0x00,   // Y
            0x5D, 0xCB,                       // pop bp; retf
        };
        for (int i = 0; i < handler.Length; i++) machine.Memory.WriteByte(0x9000 + (uint)i, handler[i]);

        machine.Cpu.SetSegment(SegReg.Es, 0x0900);
        machine.Cpu.State.Bx = 0x0000;
        Call(machine, 0x07);
        Call(machine, 0x05, bh: 3);
        Call(machine, 0x00, bh: 1);

        // Park the CPU in a HLT loop with interrupts on, IRQ 12 unmasked.
        machine.Memory.WriteByte(0x8000, 0xF4);   // HLT
        machine.Memory.WriteByte(0x8001, 0xEB);   // JMP -3
        machine.Memory.WriteByte(0x8002, 0xFD);
        machine.Cpu.SetSegment(SegReg.Cs, 0x0800);
        machine.Cpu.State.Eip = 0;
        machine.Cpu.SetSegment(SegReg.Ss, 0x0000);
        machine.Cpu.State.Sp = 0x7000;
        machine.Cpu.Flags.Interrupt = true;
        machine.Io.WriteByte(0xA1, (byte)(machine.Io.ReadByte(0xA1) & ~0x10));   // IRQ 12
        machine.Io.WriteByte(0x21, (byte)(machine.Io.ReadByte(0x21) & ~0x04));   // cascade

        machine.RunInstructions(20_000);   // lets the enable acknowledgement drain

        machine.Mouse.SetButton(MouseButtons.Left, true);
        machine.Mouse.Move(5, -3);
        machine.RunInstructions(200_000);

        ushort status = machine.Memory.ReadWord(0x9080);
        ushort x = machine.Memory.ReadWord(0x9082);
        ushort y = machine.Memory.ReadWord(0x9084);

        Assert.Equal(0x09, status & 0x0F);     // left button, bit 3 set
        Assert.Equal(5, x);
        Assert.Equal(3, y);                    // up is positive on the wire
    }
}
