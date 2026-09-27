using Aurora386.Core.Cpu;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// Behaviour the SingleStepTests 80386 real-mode vectors -- captures from a
/// real 386EX -- showed the emulator getting wrong, stated here in the words
/// of the instruction set reference so that it cannot come back.
/// </summary>
public class RealModeVectorFindingsTests
{
    private const uint CodeBase = 0x1000;

    private static Cpu Run(int steps, params byte[] code)
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus);

        foreach (SegReg segment in Enum.GetValues<SegReg>())
        {
            cpu.State.LoadSegmentRealMode(segment, 0);
        }

        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;
        code.CopyTo(bus.GetRamSpan(CodeBase, code.Length));

        for (int i = 0; i < steps; i++)
        {
            cpu.Step();
        }

        return cpu;
    }

    /// <summary>
    /// SAHF loads SF, ZF, AF, PF and CF from AH; bits 3 and 5 of EFLAGS stay
    /// 0 and bit 1 stays 1 whatever AH holds. The 386EX turns AH = FE into a
    /// low flags byte of D6.
    /// </summary>
    [Fact]
    public void SahfLoadsOnlyTheFiveFlagsFromAh()
    {
        // MOV AH,FE ; SAHF
        Cpu cpu = Run(2, 0xB4, 0xFE, 0x9E);

        Assert.Equal(0xD6u, cpu.Flags.Value & 0xFF);
    }
}
