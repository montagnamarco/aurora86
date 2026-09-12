using Aurora386.Core;
using Aurora386.Core.Cpu;

namespace Aurora386.Tests;

/// <summary>An empty diskette drive remains installed and describes its capacity.</summary>
public sealed class EmptyFloppyTests
{
    private static Machine Ask(byte function)
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Cpu.State.Halted = false;
        machine.Cpu.SetSegment(SegReg.Cs, 0);
        machine.Cpu.SetSegment(SegReg.Ss, 0);
        machine.Cpu.State.Esp = 0x7000;
        machine.Cpu.State.Eip = 0x2000;
        machine.Cpu.State.Ax = (ushort)(function << 8);
        machine.Cpu.State.Dx = 0;
        machine.Memory.WriteByte(0x2000, 0xCD);
        machine.Memory.WriteByte(0x2001, 0x13);
        machine.Cpu.Step();
        machine.Cpu.Step();
        machine.Cpu.Step();
        return machine;
    }

    [Fact]
    public void EmptyDriveParametersAgreeWithTheCmosThreeAndHalfInchDrive()
    {
        using Machine machine = Ask(0x08);
        Assert.False(machine.Cpu.Flags.Carry);
        Assert.Equal(4, machine.Cpu.State.GetReg8(3));
        Assert.Equal(0x4F12, machine.Cpu.State.Cx);
        Assert.Equal(0x0101, machine.Cpu.State.Dx);
        machine.Io.WriteByte(0x70, 0x10);
        Assert.Equal(0x40, machine.Io.ReadByte(0x71));
    }

    [Fact]
    public void EmptyDriveStillReportsItsChangeLine()
    {
        using Machine machine = Ask(0x15);
        Assert.False(machine.Cpu.Flags.Carry);
        Assert.Equal(2, machine.Cpu.State.GetReg8(4));
    }
}
