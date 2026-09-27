using Aurora386.Core.Cpu;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// Work package F1.1: a <see cref="CpuFeature"/> bitmask per model instead of
/// scattered "Model >= Intel486" comparisons, and CPUID reading the same
/// per-model table (<c>CpuModelCatalog</c>, in Cpu.Identification.cs) that
/// RESET and CR0 now read too. What a guest actually sees is always
/// <c>silicon ∩ CpuFeatures.Implemented</c> -- a Pentium's real CX8 does not
/// become CPUID-visible until CMPXCHG8B itself does, which is why most of
/// these tests are really testing the intersection, not the silicon table.
/// </summary>
public class CpuFeatureTests
{
    private const uint CodeBase = 0x1000;

    /// <summary>A bare CPU in real mode, exactly as CpuRealModeTests sets one up.</summary>
    private static (Cpu cpu, PhysicalMemoryBus bus) Setup(params byte[] code)
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus);
        foreach (SegReg s in Enum.GetValues<SegReg>())
            cpu.State.LoadSegmentRealMode(s, 0);
        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;
        code.CopyTo(bus.GetRamSpan(CodeBase, code.Length));
        return (cpu, bus);
    }

    /// <summary>Runs CPUID (0F A2) once with EAX already loaded, and returns the four registers.</summary>
    private static (uint Eax, uint Ebx, uint Ecx, uint Edx) Cpuid(CpuModel model, uint eax, CpuFeatures? features = null)
    {
        var (cpu, bus) = Setup(0x0F, 0xA2);
        using (bus)
        {
            cpu.State.SelectModel(model);
            if (features is { } custom) cpu.State.Features = custom;
            cpu.State.Eax = eax;

            cpu.Step();

            return (cpu.State.Eax, cpu.State.Ebx, cpu.State.Ecx, cpu.State.Edx);
        }
    }

    // --- the 386 has none of this -----------------------------------------

    [Fact]
    public void OnA386CpuidIsAnInvalidOpcode()
    {
        var (cpu, bus) = Setup(0x0F, 0xA2);
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Intel386);
            cpu.State.Eax = 0;

            byte faultVector = 0xFF;
            cpu.ProcessorException += (vector, _, _, _) => faultVector = vector;

            cpu.Step();

            Assert.Equal(6, faultVector);
        }
    }

    [Fact]
    public void OnA386EflagsIdentificationBitDoesNotToggle()
    {
        // 66 9D = POPFD: the only way in real mode to reach bit 21 of
        // EFLAGS at all, since the 16-bit POPF cannot touch it.
        var (cpu, bus) = Setup(0x66, 0x9D);
        using (bus)
        {
            cpu.State.SelectModel(CpuModel.Intel386);
            uint before = cpu.Flags.Value;
            uint attempted = before ^ (1u << 21);
            bus.WriteDword(cpu.State.Esp, attempted);

            cpu.Step();

            Assert.Equal(before & (1u << 21), cpu.Flags.Value & (1u << 21));
        }
    }

    [Fact]
    public void BswapFaultsOnA386AndRunsOnA486()
    {
        // 66 0F C8 = bswap eax (the 0x66 prefix asks for the 32-bit form;
        // real mode defaults to 16-bit operands).
        var (cpu386, bus386) = Setup(0x66, 0x0F, 0xC8);
        using (bus386)
        {
            cpu386.State.SelectModel(CpuModel.Intel386);
            byte faultVector = 0xFF;
            cpu386.ProcessorException += (vector, _, _, _) => faultVector = vector;

            cpu386.Step();

            Assert.Equal(6, faultVector);
        }

        var (cpu486, bus486) = Setup(0x66, 0x0F, 0xC8);
        using (bus486)
        {
            cpu486.State.Eax = 0x11223344;

            cpu486.Step();

            Assert.Equal(0x44332211u, cpu486.State.Eax);
        }
    }

    // --- the 486 answers exactly as it always has ---------------------------

    [Fact]
    public void A486LeafZeroNamesTheMakerAndTheMaxLeaf()
    {
        var (eax, ebx, ecx, edx) = Cpuid(CpuModel.Intel486, 0);

        string vendor = System.Text.Encoding.ASCII.GetString(
        [
            .. BitConverter.GetBytes(ebx),
            .. BitConverter.GetBytes(edx),
            .. BitConverter.GetBytes(ecx),
        ]);

        Assert.Equal("GenuineIntel", vendor);
        Assert.Equal(1u, eax);
    }

    [Fact]
    public void A486LeafOneIsTheOldSignatureWithOnlyFpu()
    {
        var (eax, _, ecx, edx) = Cpuid(CpuModel.Intel486, 1);

        Assert.Equal(0x0480u, eax);
        Assert.Equal(1u, edx & 1);   // bit 0: floating point on chip
        Assert.Equal(0u, edx & ~1u);
        Assert.Equal(0u, ecx);
    }

    // --- a later model's silicon is real, but Aurora holds most of it back --

    [Fact]
    public void PentiumLeafOneIsFamilyFiveWithTheP2_1FeaturesAdvertised()
    {
        // Signature and family/model/stepping from the InstLatX64 P54C dump
        // cited in Cpu.Identification.cs (000052C, 166 MHz Pentium): family
        // 5, model 2, stepping 0xC.
        var (eax, _, ecx, edx) = Cpuid(CpuModel.Pentium, 1);

        Assert.Equal(0x052Cu, eax);
        Assert.Equal(5u, (eax >> 8) & 0xF);
        Assert.Equal(2u, (eax >> 4) & 0xF);
        Assert.Equal(0xCu, eax & 0xF);

        // VME is real on this silicon (see the catalog entry) and, since
        // work package P2.3, is in CpuFeatures.Implemented too -- CR4.VME/
        // PVI, EFLAGS.VIF/VIP and V86's virtualized CLI/STI/PUSHF/POPF/IRET/
        // INT n are all real now -- so bit 1 is set. Work package P2.1
        // (Pentium system registers and instructions) turned on DE(2),
        // TSC(4), MSR(5), MCE(7) and CX8(8), alongside the FPU(0) bit every
        // model already had; P2.2 turned on PSE(3) -- CR4.PSE and 4 MB pages
        // are real now.
        // 1(FPU) + 2(VME) + 4(DE) + 8(PSE) + 16(TSC) + 32(MSR) + 128(MCE) + 256(CX8) = 0x1BF.
        Assert.Equal(0x000001BFu, edx);
        Assert.Equal(0u, ecx);
    }

    /// <summary>
    /// Work package P2.5: MMX is real on the Pentium MMX's silicon and, since
    /// Cpu.Mmx.cs now executes its instructions, in CpuFeatures.Implemented
    /// too -- so leaf 1 EDX bit 23 appears for that model. The plain Pentium
    /// (P54C) predates MMX in silicon and must not show the bit even though
    /// Implemented now carries it.
    /// </summary>
    [Fact]
    public void OnlyThePentiumMmxModelClaimsMmxNotThePlainPentium()
    {
        var (_, _, _, mmxEdx) = Cpuid(CpuModel.PentiumMmx, 1);
        Assert.Equal(1u << 23, mmxEdx & (1u << 23));

        var (_, _, _, plainEdx) = Cpuid(CpuModel.Pentium, 1);
        Assert.Equal(0u, plainEdx & (1u << 23));
    }

    /// <summary>
    /// Work package P3.1: family six now claims Msr, Cmov, Sep, Mtrr, Mca,
    /// Pat and Fxsr alongside the P54C set (VME, DE, PSE, TSC, MSR, MCE,
    /// CX8) once CpuFeatures.ImplementedFor stops holding Msr back and
    /// Implemented adds the rest. The P3.1 paging review then added PAE and
    /// PGE too (Cpu.Paging.cs's three-level walk, FlushTlbForCr3Load's
    /// global-entry rule), so those now move as well. APIC and PSE-36 stay
    /// off -- neither is in Implemented (no LAPIC device modelled, and
    /// nothing here treats a 4 MB PDE's bits 20:13 as an address extension)
    /// -- even though Deschutes' own silicon carries them.
    ///
    /// 1(FPU)+2(VME)+4(DE)+8(PSE)+16(TSC)+32(MSR)+128(MCE)+256(CX8) = 0x1BF,
    /// as the plain Pentium already shows, plus 64(PAE)+2048(SEP)+4096(MTRR)+
    /// 8192(PGE)+16384(MCA)+32768(CMOV)+65536(PAT)+8388608(MMX)+
    /// 16777216(FXSR) = 0x0181F9FF.
    /// </summary>
    [Fact]
    public void PentiumIiLeafOneAddsPaeAndPgeAlongsideTheP3_1FeaturesButNotApicOrPse36()
    {
        var (eax, _, ecx, edx) = Cpuid(CpuModel.PentiumII, 1);

        Assert.Equal(0x0651u, eax);
        Assert.Equal(0x0181F9FFu, edx);
        Assert.Equal(0u, ecx);

        // Named individually too, so a future regression names the bit that
        // moved rather than just the combined hex value.
        Assert.Equal(1u << 6, edx & (1u << 6));    // PAE: Cpu.Paging.cs's PAE walk
        Assert.Equal(0u, edx & (1u << 9));         // APIC: no LAPIC device modelled
        Assert.Equal(1u << 13, edx & (1u << 13));  // PGE: FlushTlbForCr3Load's global-entry rule
        Assert.Equal(0u, edx & (1u << 17));        // PSE-36: still out of scope
    }

    [Fact]
    public void ACustomFeatureSetAdvertisingCx8ShowsEdxBitEight()
    {
        // Features is publicly settable precisely so a test can describe a
        // processor that implements a feature no shipped model combination
        // covers yet, without waiting for a whole new CpuModel entry.
        CpuFeatures custom = new CpuFeatures().With(
            CpuFeature.Fpu, CpuFeature.Cx8, CpuFeature.Isa486, CpuFeature.Identification);

        var (_, _, _, edx) = Cpuid(CpuModel.Pentium, 1, custom);

        Assert.Equal(1u << 8, edx & (1u << 8));
    }

    [Fact]
    public void Pentium4BrandStringIsReadableThroughTheExtendedLeaves()
    {
        var leaf2 = Cpuid(CpuModel.Pentium4, 0x80000002);
        var leaf3 = Cpuid(CpuModel.Pentium4, 0x80000003);
        var leaf4 = Cpuid(CpuModel.Pentium4, 0x80000004);

        byte[] bytes =
        [
            .. BitConverter.GetBytes(leaf2.Eax), .. BitConverter.GetBytes(leaf2.Ebx),
            .. BitConverter.GetBytes(leaf2.Ecx), .. BitConverter.GetBytes(leaf2.Edx),
            .. BitConverter.GetBytes(leaf3.Eax), .. BitConverter.GetBytes(leaf3.Ebx),
            .. BitConverter.GetBytes(leaf3.Ecx), .. BitConverter.GetBytes(leaf3.Edx),
            .. BitConverter.GetBytes(leaf4.Eax), .. BitConverter.GetBytes(leaf4.Ebx),
            .. BitConverter.GetBytes(leaf4.Ecx), .. BitConverter.GetBytes(leaf4.Edx),
        ];

        // 48 bytes, right-justified: 14 leading spaces then the visible
        // name, then a null terminator -- exactly what the cited InstLatX64
        // dump for a 2.40 GHz Northwood holds (see Cpu.Identification.cs).
        string brand = System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0');
        Assert.Equal("              Intel(R) Pentium(R) 4 CPU 2.40GHz", brand);
    }

    // --- leaves the model does not have fall back to the highest basic one --

    [Fact]
    public void OutOfRangeStandardLeavesReturnTheHighestBasicLeaf()
    {
        // Pentium II's own maximum standard leaf is 2 (see the catalog).
        var atMax = Cpuid(CpuModel.PentiumII, 2);
        var wayBeyond = Cpuid(CpuModel.PentiumII, 0x40000000);

        Assert.Equal(atMax, wayBeyond);
    }

    [Fact]
    public void AModelWithoutExtendedLeavesAnswersWithTheHighestBasicLeaf()
    {
        // Pentium III predates extended CPUID (AP-485: it arrived with the
        // Pentium 4), so even 0x80000000 itself falls back to the highest
        // *basic* leaf -- Pentium III's own maximum is 3.
        var basic = Cpuid(CpuModel.PentiumIII, 3);
        var extended = Cpuid(CpuModel.PentiumIII, 0x80000000);

        Assert.Equal(basic, extended);
    }
}
