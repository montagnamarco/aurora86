using Aurora86.Core.Cpu;
using Aurora86.Core.Memory;

namespace Aurora86.Tests;

/// <summary>
/// What the P3.1 review found wrong in the family-six instructions and MSRs,
/// stated in register terms so that it stays fixed.
/// </summary>
public class P6ReviewFindingsTests
{
    private const uint CodeBase = 0x1000;

    private static (Cpu Cpu, PhysicalMemoryBus Bus) Setup(CpuModel model, params byte[] code)
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
        code.CopyTo(bus.GetRamSpan(CodeBase, code.Length));

        return (cpu, bus);
    }

    private static byte StepAndReturnVector(Cpu cpu)
    {
        byte vector = 0xFF;
        cpu.ProcessorException += (number, _, _, _) => vector = vector == 0xFF ? number : vector;
        cpu.Step();
        return vector;
    }

    /// <summary>RDMSR 10h on a Pentium II returns the TSC in EDX:EAX, the same value RDTSC reads.</summary>
    [Fact]
    public void RdmsrOfTheTimeStampCounterWorksOnFamilySix()
    {
        (Cpu cpu, _) = Setup(CpuModel.PentiumII, 0x0F, 0x32);   // RDMSR
        cpu.WriteTscFull(0x0000_0123_4567_89AB);
        cpu.State.Ecx = 0x10;
        ulong expected = cpu.Tsc;

        byte vector = StepAndReturnVector(cpu);

        Assert.Equal(0xFF, vector);
        ulong read = cpu.State.Eax | ((ulong)cpu.State.Edx << 32);
        Assert.Equal(expected, read);
    }

    /// <summary>
    /// WRMSR to PERFCTR0 (C1h) writes the low 32 bits and copies bit 31 into
    /// bits 39:32: EDX:EAX = 0:FFFFFFF0 reads back FF_FFFFFFF0, and EDX = FFh,
    /// EAX = 0 reads back 0.
    /// </summary>
    [Fact]
    public void PerformanceCounterWritesSignExtendBit31()
    {
        (Cpu cpu, _) = Setup(CpuModel.PentiumII);

        Assert.True(cpu.Msrs.TryWrite(0xC1, 0x0000_0000_FFFF_FFF0));
        Assert.Equal(0xFF_FFFF_FFF0ul, cpu.Msrs.PerfCounter0);

        Assert.True(cpu.Msrs.TryWrite(0xC1, 0x0000_00FF_0000_0000));
        Assert.Equal(0ul, cpu.Msrs.PerfCounter0);
    }

    /// <summary>
    /// FXSAVE [5E00] with DS limit FFFF: the image ends at 5FFF, inside the
    /// limit, but at FFF0 it would run past it. #GP, and nothing written.
    /// FXSAVE [FFF0] = 0F AE 06 F0 FF.
    /// </summary>
    [Fact]
    public void FxsavePastTheSegmentLimitFaultsBeforeWritingAnything()
    {
        (Cpu cpu, PhysicalMemoryBus bus) = Setup(CpuModel.PentiumII, 0x0F, 0xAE, 0x06, 0xF0, 0xFF);
        bus.WriteDword(0xFFF0, 0x5A5A5A5A);

        byte vector = StepAndReturnVector(cpu);

        Assert.Equal(13, vector);
        Assert.Equal(0x5A5A5A5Au, bus.ReadDword(0xFFF0));
    }

    /// <summary>
    /// FXSAVE leaves bytes 464-511 of the image alone: they belong to
    /// software (SDM FXSAVE). FXSAVE [5000] = 0F AE 06 00 50.
    /// </summary>
    [Fact]
    public void FxsaveLeavesTheSoftwareAreaAlone()
    {
        (Cpu cpu, PhysicalMemoryBus bus) = Setup(CpuModel.PentiumII, 0x0F, 0xAE, 0x06, 0x00, 0x50);
        bus.WriteDword(0x5000 + 464, 0x12345678);
        bus.WriteDword(0x5000 + 508, 0x9ABCDEF0);

        cpu.Step();

        Assert.Equal(0x12345678u, bus.ReadDword(0x5000 + 464));
        Assert.Equal(0x9ABCDEF0u, bus.ReadDword(0x5000 + 508));
    }

    /// <summary>
    /// The Pentium with MMX has RDPMC, reading its own CTR0/CTR1 (MSR 12h,
    /// 13h): with CTR1 = 12_3456_789A, RDPMC with ECX = 1 in real mode
    /// returns EDX = 12, EAX = 3456789A.
    /// </summary>
    [Fact]
    public void ThePentiumMmxReadsItsOwnCountersWithRdpmc()
    {
        (Cpu cpu, _) = Setup(CpuModel.PentiumMmx, 0x0F, 0x33);   // RDPMC
        cpu.Msrs.Counter1 = 0x12_3456_789A;
        cpu.State.Ecx = 1;

        byte vector = StepAndReturnVector(cpu);

        Assert.Equal(0xFF, vector);
        Assert.Equal(0x3456789Au, cpu.State.Eax);
        Assert.Equal(0x12u, cpu.State.Edx);
    }

    /// <summary>The plain Pentium (P54C) has no RDPMC: #UD.</summary>
    [Fact]
    public void ThePlainPentiumHasNoRdpmc()
    {
        (Cpu cpu, _) = Setup(CpuModel.Pentium, 0x0F, 0x33);

        Assert.Equal(6, StepAndReturnVector(cpu));
    }
}
