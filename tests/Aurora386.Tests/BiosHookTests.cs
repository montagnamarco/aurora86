using Aurora386.Core;
using Xunit;

namespace Aurora386.Tests;

/// <summary>
/// The two bytes that call a native BIOS handler are an undefined opcode on
/// the real processor, and only the ROM may use them as anything else.
/// </summary>
public class BiosHookTests
{
    [Fact]
    public void TheHookOpcodeOutsideTheRomIsAnUndefinedOpcode()
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);

        var raised = new List<byte>();
        machine.Cpu.ProcessorException += (vector, _, _, _) => raised.Add(vector);

        // 0F FF 55 in ordinary memory, as Windows' kernel executes it.
        uint code = 0x20100;
        machine.Memory.WriteByte(code + 0, 0x0F);
        machine.Memory.WriteByte(code + 1, 0xFF);
        machine.Memory.WriteByte(code + 2, 0x55);
        machine.Cpu.SetSegment(Aurora386.Core.Cpu.SegReg.Cs, 0x2000);
        machine.Cpu.State.Eip = 0x0100;
        machine.Cpu.SetSegment(Aurora386.Core.Cpu.SegReg.Ss, 0x3000);
        machine.Cpu.State.Esp = 0xFFFE;
        machine.Cpu.State.Halted = false;
        machine.RunInstructions(1);

        Assert.Contains((byte)6, raised);
        // The byte after the opcode was not consumed as a handler number: the
        // return address on the stack points back at the instruction itself.
        Assert.Equal(0x0100, machine.Memory.ReadWord(0x30000 + 0xFFF8));
    }

    [Fact]
    public void TheRomStillReachesItsHandlers()
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);

        uint code = 0x20100;
        machine.Memory.WriteByte(code + 0, 0xB8);   // MOV AX,0F00h
        machine.Memory.WriteWord(code + 1, 0x0F00);
        machine.Memory.WriteByte(code + 3, 0xCD);   // INT 10h
        machine.Memory.WriteByte(code + 4, 0x10);
        machine.Memory.WriteByte(code + 5, 0xF4);   // HLT
        machine.Cpu.SetSegment(Aurora386.Core.Cpu.SegReg.Cs, 0x2000);
        machine.Cpu.State.Eip = 0x0100;
        machine.Cpu.SetSegment(Aurora386.Core.Cpu.SegReg.Ss, 0x3000);
        machine.Cpu.State.Esp = 0xFFFE;
        machine.Cpu.State.Halted = false;
        for (int i = 0; i < 100 && !machine.Cpu.State.Halted; i++) machine.RunInstructions(1);

        Assert.True(machine.Cpu.State.Halted);
        Assert.Equal(0x03, machine.Cpu.State.GetReg8(0));   // the current mode came back
    }
}
