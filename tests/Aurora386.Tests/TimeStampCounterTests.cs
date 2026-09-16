using Aurora386.Core.Cpu;
using Aurora386.Core.IO;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// RDTSC (0F 31) and the Pentium identification that goes with it.
///
/// Why this matters beyond one instruction: every bootloader written after
/// the middle nineties uses RDTSC to measure a short interval, because it is
/// far cheaper than programming the interval timer. On a processor without
/// it the instruction raises #UD, and early in a boot there is no handler
/// installed, so the fault becomes a triple fault and the machine resets.
/// The visible symptom is a loader that appears to restart for ever, which
/// is a long way from its cause -- that is exactly how this was found.
/// </summary>
public class TimeStampCounterTests
{
    private const uint CodeBase = 0x1000;

    private static (Cpu cpu, PhysicalMemoryBus bus) Setup(CpuModel model, params byte[] code)
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus, new IoBus());
        cpu.State.SelectModel(model);
        foreach (SegReg s in Enum.GetValues<SegReg>()) cpu.State.LoadSegmentRealMode(s, 0);
        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;
        code.CopyTo(bus.GetRamSpan(CodeBase, code.Length));
        return (cpu, bus);
    }

    [Fact]
    public void RdtscOnAPentiumReturnsTheCounterInEdxEax()
    {
        var (cpu, _) = Setup(CpuModel.Pentium, 0x0F, 0x31);

        cpu.Step();

        // The instruction just executed is itself counted, so the value is
        // not zero; what matters is that both halves were written and the
        // high half is the top 32 bits of the same counter.
        ulong reported = ((ulong)cpu.State.Edx << 32) | cpu.State.Eax;
        Assert.Equal((ulong)cpu.Cycles, reported);
        Assert.True(reported > 0, "the counter must include the read itself");
    }

    [Fact]
    public void TheCounterAdvancesAndNeverGoesBackwards()
    {
        // RDTSC ; RDTSC -- two reads in a row must not report time going back.
        var (cpu, _) = Setup(CpuModel.Pentium, 0x0F, 0x31, 0x0F, 0x31);

        cpu.Step();
        ulong first = ((ulong)cpu.State.Edx << 32) | cpu.State.Eax;
        cpu.Step();
        ulong second = ((ulong)cpu.State.Edx << 32) | cpu.State.Eax;

        Assert.True(second > first,
            $"the counter must advance: first {first}, second {second}");
    }

    [Fact]
    public void RdtscOnA486RaisesInvalidOpcode()
    {
        // The counterpart, and the behaviour that sent the bootloader into a
        // reset loop. A 486 has no time stamp counter, and saying so through
        // a fault is what lets software detect the absence.
        var (cpu, bus) = Setup(CpuModel.Intel486, 0x0F, 0x31);

        // Point the invalid-opcode vector somewhere recognisable so the fault
        // is observed as a fault rather than as a reset.
        bus.WriteWord(6 * 4, 0x0500);
        bus.WriteWord(6 * 4 + 2, 0x0000);

        cpu.Step();

        Assert.Equal(0x0500u, cpu.State.Eip);
    }

    [Fact]
    public void RdtscOnA386AlsoRaisesInvalidOpcode()
    {
        var (cpu, bus) = Setup(CpuModel.Intel386, 0x0F, 0x31);
        bus.WriteWord(6 * 4, 0x0500);
        bus.WriteWord(6 * 4 + 2, 0x0000);

        cpu.Step();

        Assert.Equal(0x0500u, cpu.State.Eip);
    }

    [Fact]
    public void CpuidAnnouncesTheTimeStampCounterOnAPentium()
    {
        // MOV EAX,1 ; CPUID. Bit 4 of EDX is the time stamp counter, and a
        // guest that trusts it must not then fault on RDTSC: the claim and
        // the instruction have to agree.
        var (cpu, _) = Setup(CpuModel.Pentium, 0x66, 0xB8, 0x01, 0x00, 0x00, 0x00, 0x0F, 0xA2);

        cpu.Step();
        cpu.Step();

        Assert.Equal(0x0520u, cpu.State.Eax);            // family 5, model 2
        Assert.Equal(0x10u, cpu.State.Edx & 0x10u);      // bit 4: TSC present
        Assert.Equal(0x01u, cpu.State.Edx & 0x01u);      // bit 0: FPU on chip

        // Bit 5 is the machine specific registers, which we do not implement
        // and therefore must not claim.
        Assert.Equal(0u, cpu.State.Edx & 0x20u);
    }

    [Fact]
    public void CpuidDoesNotAnnounceTheCounterOnA486()
    {
        var (cpu, _) = Setup(CpuModel.Intel486, 0x66, 0xB8, 0x01, 0x00, 0x00, 0x00, 0x0F, 0xA2);

        cpu.Step();
        cpu.Step();

        Assert.Equal(0x0480u, cpu.State.Eax);            // family 4, model 8
        Assert.Equal(0u, cpu.State.Edx & 0x10u);         // no TSC claimed
    }

    [Fact]
    public void EdxCarriesTheProcessorSignatureOutOfReset()
    {
        // Firmware reads EDX before any instruction of its own has run, to
        // learn which chip it woke up on. The value must match what CPUID
        // reports later, or the two identifications contradict each other.
        foreach ((CpuModel model, uint signature) in new[]
                 {
                     (CpuModel.Intel386, 0x0308u),
                     (CpuModel.Intel486, 0x0480u),
                     (CpuModel.Pentium, 0x0520u),
                 })
        {
            using var bus = new PhysicalMemoryBus();
            var cpu = new Cpu(bus, new IoBus());
            cpu.State.SelectModel(model);
            cpu.State.Reset();

            Assert.Equal(signature, cpu.State.Edx);
        }
    }
}
