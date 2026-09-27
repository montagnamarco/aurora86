using Aurora386.Core.Cpu;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// CMPXCHG8B's operand is one quadword, checked as one: against the segment
/// limit as a whole, without wrapping inside a 16-bit segment.
/// </summary>
public class Cmpxchg8bOperandTests
{
    private const uint CodeBase = 0x1000;

    /// <summary>
    /// In real mode, with DS limit FFFF, CMPXCHG8B [SI] at SI = FFFC runs past
    /// the limit and raises #GP(13). It must not split into FFFC-FFFF and
    /// 0000-0003, and memory at both ends stays untouched.
    /// </summary>
    [Fact]
    public void AQuadwordPastTheSegmentLimitFaultsInsteadOfWrapping()
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus);
        cpu.State.SelectModel(CpuModel.Pentium);

        foreach (SegReg segment in Enum.GetValues<SegReg>())
        {
            cpu.State.LoadSegmentRealModeReset(segment, 0);
        }

        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;
        cpu.State.Esi = 0xFFFC;

        // EDX:EAX equals the wrapped quadword FFFC..0003, so an
        // implementation that wrapped would compare equal and store ECX:EBX.
        // CMPXCHG8B [SI] = 0F C7 0C
        byte[] program = [0x0F, 0xC7, 0x0C];
        program.CopyTo(bus.GetRamSpan(CodeBase, program.Length));

        bus.WriteDword(0xFFFC, 0x11111111);
        bus.WriteDword(0x0000, 0x22222222);
        cpu.State.Eax = 0x11111111;
        cpu.State.Edx = 0x22222222;
        cpu.State.Ebx = 0xAAAAAAAA;
        cpu.State.Ecx = 0xBBBBBBBB;

        byte vector = 0xFF;
        cpu.ProcessorException += (number, _, _, _) => vector = number;
        cpu.Step();

        Assert.Equal(13, vector);
        Assert.Equal(0x11111111u, bus.ReadDword(0xFFFC));
        Assert.Equal(0x22222222u, bus.ReadDword(0x0000));
    }
}
