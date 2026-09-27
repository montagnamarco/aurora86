using Aurora386.Core.Cpu;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// What differs between the models once the Pentium arrives: the test
/// registers go away, and the MSR space is the Pentium's own.
/// </summary>
public class PentiumModelGatingTests
{
    private const uint CodeBase = 0x1000;

    /// <summary>Runs MOV EAX,TR6 (0F 24 F0) in real mode and returns the vector raised, or FF.</summary>
    private static byte ReadTestRegister(CpuModel model)
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus);
        cpu.State.SelectModel(model);

        foreach (SegReg segment in Enum.GetValues<SegReg>())
        {
            cpu.State.LoadSegmentRealModeReset(segment, 0);
        }

        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;

        byte[] program = [0x0F, 0x24, 0xF0];
        program.CopyTo(bus.GetRamSpan(CodeBase, program.Length));

        byte vector = 0xFF;
        cpu.ProcessorException += (number, _, _, _) => vector = number;
        cpu.Step();

        return vector;
    }

    /// <summary>The 486 keeps TR6 and TR7: MOV EAX,TR6 runs.</summary>
    [Fact]
    public void The486StillHasItsTestRegisters()
    {
        Assert.Equal(0xFF, ReadTestRegister(CpuModel.Intel486));
    }

    /// <summary>The Pentium removed the test registers: MOV EAX,TR6 is #UD (vector 6).</summary>
    [Fact]
    public void ThePentiumHasNoTestRegisters()
    {
        Assert.Equal(6, ReadTestRegister(CpuModel.Pentium));
    }

    /// <summary>
    /// Work package P3.1 gives family six its own MSR set
    /// (ModelSpecificRegisters), so every model whose silicon has MSR now
    /// claims it (CPUID leaf 1 EDX bit 5) -- before P3.1 a family-6 model
    /// held the bit back for want of registers of its own to answer with;
    /// see CpuFeatures.ImplementedFor's own history for the P5-only period
    /// this test used to document.
    /// </summary>
    [Fact]
    public void BothThePentiumAndPentiumIiClaimModelSpecificRegisters()
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus);

        cpu.State.SelectModel(CpuModel.Pentium);
        Assert.True(cpu.State.Features.Has(CpuFeature.Msr));

        cpu.State.SelectModel(CpuModel.PentiumII);
        Assert.True(cpu.State.Features.Has(CpuFeature.Msr));
        Assert.True(cpu.State.Features.Has(CpuFeature.Tsc));
    }
}
