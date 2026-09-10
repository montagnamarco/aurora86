using Aurora386.Core;
using Aurora386.Core.Cpu;

namespace Aurora386.Tests;

/// <summary>
/// The BIOS copy used by EMM386 before it turns paging on. Failure here is
/// reported by the driver as insufficient memory, however much RAM is fitted.
/// </summary>
public class ExtendedMemoryMoveTests
{
    private static Machine Setup(uint source, uint destination, ushort words, uint limit = 0xFFFF)
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        var cpu = machine.Cpu;
        cpu.State.Halted = false;
        cpu.SetSegment(SegReg.Cs, 0);
        cpu.SetSegment(SegReg.Ss, 0);
        cpu.SetSegment(SegReg.Es, 0x300);
        cpu.State.Esp = 0x7000;
        cpu.State.Eip = 0x2000;
        cpu.State.Esi = 0xCAFE0100; // the BIOS pointer uses SI, not ESI
        cpu.State.Cx = words;
        cpu.State.Ax = 0x875A;
        cpu.Flags.Carry = true;
        machine.Memory.WriteWord(0x2000, 0x15CD); // INT 15h
        void Descriptor(uint address, uint start)
        {
            machine.Memory.WriteWord(address, (ushort)limit);
            machine.Memory.WriteWord(address + 2, (ushort)start);
            machine.Memory.WriteByte(address + 4, (byte)(start >> 16));
            machine.Memory.WriteByte(address + 5, 0x93);
            machine.Memory.WriteByte(address + 6, (byte)(limit >> 16));
            machine.Memory.WriteByte(address + 7, (byte)(start >> 24));
        }
        Descriptor(0x3110, source);
        Descriptor(0x3118, destination);
        return machine;
    }

    private static void Invoke(Machine machine)
    {
        machine.Cpu.Step(); // INT
        machine.Cpu.Step(); // native service
        machine.Cpu.Step(); // IRET
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopiesEmmPageTableAndRestoresA20(bool a20)
    {
        using var machine = Setup(0x17AD0, 0x118000, 0x800, 0x1000);
        var memory = machine.Memory;
        memory.A20Enabled = true;
        for (uint i = 0; i < 4096; i++) memory.WriteByte(0x17AD0 + i, (byte)(i * 7));
        memory.WriteByte(0x119000, 0xA5);
        memory.A20Enabled = a20;
        Invoke(machine);
        Assert.Equal(a20, memory.A20Enabled);
        Assert.False(machine.Cpu.Flags.Carry);
        Assert.Equal(0x005A, machine.Cpu.State.Ax);
        Assert.Equal(0xCAFE0100u, machine.Cpu.State.Esi);
        Assert.Equal(0x800, machine.Cpu.State.Cx);
        memory.A20Enabled = true;
        for (uint i = 0; i < 4096; i++) Assert.Equal((byte)(i * 7), memory.ReadByte(0x118000 + i));
        Assert.Equal(0xA5, memory.ReadByte(0x119000));
    }

    [Fact]
    public void ShortDescriptorFailsBeforeWriting()
    {
        using var machine = Setup(0x8000, 0x9000, 2, 2);
        machine.Memory.WriteWord(0x8000, 0x1234);
        machine.Memory.WriteWord(0x9000, 0xABCD);
        Invoke(machine);
        Assert.True(machine.Cpu.Flags.Carry);
        Assert.Equal(0x025A, machine.Cpu.State.Ax);
        Assert.Equal(0xABCD, machine.Memory.ReadWord(0x9000));
        Assert.False(machine.Memory.A20Enabled);
    }

    [Fact]
    public void OverlapCopiesWordsInForwardOrder()
    {
        using var machine = Setup(0x8000, 0x8002, 3);
        machine.Memory.WriteWord(0x8000, 0x1234);
        machine.Memory.WriteWord(0x8002, 0x5678);
        Invoke(machine);
        Assert.False(machine.Cpu.Flags.Carry);
        for (uint i = 0; i < 8; i += 2) Assert.Equal(0x1234, machine.Memory.ReadWord(0x8000 + i));
    }

    [Fact]
    public void ZeroWordsDoesNotTouchDestination()
    {
        using var machine = Setup(0x8000, 0x9000, 0);
        machine.Memory.WriteWord(0x9000, 0xABCD);
        Invoke(machine);
        Assert.False(machine.Cpu.Flags.Carry);
        Assert.Equal(0xABCD, machine.Memory.ReadWord(0x9000));
    }
}
