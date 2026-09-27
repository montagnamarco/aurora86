// CPUID, and the per-model identity it answers from: the family/model/
// stepping signature, which standard and extended leaves exist, the brand
// string where the silicon has one, and the feature bits real hardware
// reports at leaf 1, leaf 7 and the extended leaf. Sources for every
// number here are cited on the field that uses it and in the work
// package's report; the general rules are plans/specs/cpu-models.md §1.

namespace Aurora386.Core.Cpu;

/// <summary>
/// Which register of a CPUID leaf a feature bit lives in.
/// </summary>
internal enum CpuidRegister { Eax, Ebx, Ecx, Edx }

/// <summary>
/// A processor model's CPUID identity, independent of what Aurora actually
/// implements: the signature RESET leaves in EDX and CPUID leaf 1 repeats
/// in EAX, which standard and extended leaves the silicon answers at all,
/// the leaf-2 cache descriptors where a cited dump gives real ones, the
/// brand string where the model has one, and the feature bits the silicon
/// carries before <see cref="CpuFeatures.Implemented"/> holds any of them
/// back.
/// </summary>
internal sealed class CpuModelIdentity
{
    /// <summary>EAX of CPUID leaf 1: family/model/stepping, packed the way RESET leaves in EDX.</summary>
    public required uint Signature { get; init; }

    /// <summary>Highest standard leaf (EAX with the top bit clear) this silicon answers.</summary>
    public required byte MaxStandardLeaf { get; init; }

    /// <summary>
    /// Highest extended leaf (0x80000000 and up) this silicon answers, or 0
    /// for a model that predates extended CPUID entirely (every 386/486/P5/P6
    /// model here: AP-485 confirms Intel introduced 0x80000000+ with the
    /// Pentium 4).
    /// </summary>
    public required uint MaxExtendedLeaf { get; init; }

    /// <summary>The silicon's own feature bits, before intersecting with what Aurora implements.</summary>
    public required CpuFeatures Silicon { get; init; }

    /// <summary>EAX,EBX,ECX,EDX of CPUID leaf 2, or null for the "AL=1, no descriptors" default.</summary>
    public uint[]? Leaf2 { get; init; }

    /// <summary>
    /// The 48-byte brand string (leaves 0x80000002-4) as twelve dwords, in
    /// leaf order and EAX,EBX,ECX,EDX order within each leaf -- i.e. exactly
    /// the layout CPUID itself fills the registers in. Null for models that
    /// predate the brand string (everything before the Pentium 4).
    /// </summary>
    public uint[]? BrandString { get; init; }
}

/// <summary>
/// The per-model table CPUID and RESET both read from. Every signature and
/// brand string here is copied from a named, dated CPUID dump rather than
/// invented -- see the comment on each entry -- because this is exactly the
/// data a guest operating system fingerprints the host by, and a plausible
/// but wrong value is worse than an honestly incomplete one.
/// </summary>
internal static class CpuModelCatalog
{
    private static readonly Dictionary<CpuModel, CpuModelIdentity> Models = new()
    {
        // The 386 predates CPUID by a full processor generation; RESET's
        // EDX signature is the only identification it ever gives, and even
        // that is a convention later chips continue rather than something
        // a 386 program can read via any documented instruction.
        [CpuModel.Intel386] = new CpuModelIdentity
        {
            Signature = 0x0308,
            MaxStandardLeaf = 0,
            MaxExtendedLeaf = 0,
            Silicon = CpuFeatures.None.With(CpuFeature.TestRegisters),
        },

        // Family 4, model 8: an Intel486 DX4. Unchanged from the value this
        // code already used before this work package, and pinned down again
        // here so Reset() and CPUID read it from the same place.
        [CpuModel.Intel486] = new CpuModelIdentity
        {
            Signature = 0x0480,
            MaxStandardLeaf = 1,
            MaxExtendedLeaf = 0,
            Silicon = new CpuFeatures().With(CpuFeature.Fpu, CpuFeature.Isa486, CpuFeature.Identification,
                CpuFeature.TestRegisters),
        },

        // Pentium (P54C), 166 MHz, stepping 0xC. Source: InstLatX64 CPUID
        // dump "GenuineIntel000052C_P54C_CPUID.txt" -- leaf 0 EAX=1 (no leaf
        // 2 on this SKU), leaf 1 EAX=0000052Ch, EDX=000001BFh (FPU, VME, DE,
        // PSE, TSC, MSR, MCE, CX8; no on-die APIC on this part, no PAE -- P5
        // never has PAE), ECX=0. Matches cpu-models.md's own P54C example
        // (5/2/12) exactly.
        [CpuModel.Pentium] = new CpuModelIdentity
        {
            Signature = 0x052C,
            MaxStandardLeaf = 1,
            MaxExtendedLeaf = 0,
            Silicon = new CpuFeatures().With(
                CpuFeature.Fpu, CpuFeature.Vme, CpuFeature.De, CpuFeature.Pse, CpuFeature.Tsc,
                CpuFeature.Msr, CpuFeature.Mce, CpuFeature.Cx8, CpuFeature.Isa486, CpuFeature.Identification,
                CpuFeature.ControlRegister4),
        },

        // Pentium MMX (P55C), 200 MHz mobile (Tillamook), stepping 0xA3.
        // Source: InstLatX64 "GenuineIntel0000543_P55C_CPUID.txt" -- leaf 0
        // EAX=1 (this SKU has no leaf 2 either, though cpu-models.md notes
        // some later P55C steppings do), leaf 1 EAX=00000543h,
        // EDX=008003BFh: everything the P54C has, plus MMX(23) and, on this
        // particular part, on-die APIC(9) -- SKU-dependent on real silicon,
        // but load-bearing here since work package P4.4: Aurora's local
        // APIC (Devices.Apic.LocalApic) is now a real device behind this
        // bit, fixed at FEE00000h and always hardware-enabled on this
        // model (there is no APIC_BASE MSR on the P5 set to move or
        // disable it -- plans/specs/apic.md, coordinator decision A11).
        [CpuModel.PentiumMmx] = new CpuModelIdentity
        {
            Signature = 0x0543,
            MaxStandardLeaf = 1,
            MaxExtendedLeaf = 0,
            Silicon = new CpuFeatures().With(
                CpuFeature.Fpu, CpuFeature.Vme, CpuFeature.De, CpuFeature.Pse, CpuFeature.Tsc,
                CpuFeature.Msr, CpuFeature.Mce, CpuFeature.Cx8, CpuFeature.Apic, CpuFeature.Mmx,
                CpuFeature.Isa486, CpuFeature.Identification, CpuFeature.ControlRegister4,
                // RDPMC and CR4.PCE arrived together in the Pentium Pro and the
                // Pentium with MMX (SDM RDPMC); on the P55C they read CTR0/CTR1.
                CpuFeature.Pmc),
        },

        // Pentium II (Deschutes), 333 MHz, stepping 1. Source: InstLatX64
        // "GenuineIntel0000651_P2_Descuthes_CPUID.txt" -- leaf 0 EAX=2, leaf
        // 1 EAX=00000651h, EDX=0183FBFFh (P54C set plus PAE, SEP, MTRR, PGE,
        // MCA, CMOV, PAT, PSE-36, FXSR -- exactly cpu-models.md's PII row),
        // ECX=0, leaf 2 = 03020101-00000000-00000000-0C040843 (used
        // verbatim below; Aurora does not otherwise model cache
        // descriptors, so this is the one case where a cited dump answers
        // leaf 2 instead of the AL=1 default).
        [CpuModel.PentiumII] = new CpuModelIdentity
        {
            Signature = 0x0651,
            MaxStandardLeaf = 2,
            MaxExtendedLeaf = 0,
            Silicon = new CpuFeatures().With(
                CpuFeature.Fpu, CpuFeature.Vme, CpuFeature.De, CpuFeature.Pse, CpuFeature.Tsc,
                CpuFeature.Msr, CpuFeature.Pae, CpuFeature.Mce, CpuFeature.Cx8, CpuFeature.Apic,
                CpuFeature.Sep, CpuFeature.Mtrr, CpuFeature.Pge, CpuFeature.Mca, CpuFeature.Cmov,
                CpuFeature.Pat, CpuFeature.Pse36, CpuFeature.Mmx, CpuFeature.Fxsr,
                CpuFeature.Isa486, CpuFeature.Identification, CpuFeature.ControlRegister4,
                // Neither is a CPUID bit (see CpuFeature.Pmc/HintNops): every
                // family-six model carries the on-chip performance counters
                // RDPMC reads and the reserved multi-byte NOP encodings, from
                // Deschutes onward (work package P3.1, plans/specs/p6.md).
                CpuFeature.Pmc, CpuFeature.HintNops),
            Leaf2 = [0x03020101, 0x00000000, 0x00000000, 0x0C040843],
        },

        // Pentium III (Coppermine), "Pentium IIIE" 733 MHz, stepping 3
        // (marked cB0). Source: InstLatX64 "GenuineIntel0000683_P3_
        // Coppermine_CPUID.txt" -- leaf 0 EAX=3, leaf 1 EAX=00000683h,
        // EDX=0387FBFFh (PII set plus PSN(18) and SSE(25)), ECX=0, leaf 2 =
        // 03020101-00000000-00000000-0C040882, leaf 3 (the serial number
        // this PSN bit promises) deliberately not copied from the dump:
        // cpu-models.md's "not to advertise yet" list keeps PSN off until
        // there is a serial-number MSR to back it, so leaf 3 always answers
        // zero regardless of what a real chip's fused serial happens to be.
        //
        // NOTE: this specific stepping does *not* show CLFSH(19), DS(21),
        // ACPI(22) or SS(27), although cpu-models.md's summary row lists
        // them as arriving with Coppermine; those bits were phased in
        // across Coppermine's stepping range and I could not find a cited
        // dump for the later stepping (~0xA) the spec's example names. Not
        // load-bearing today -- none of the four are in
        // CpuFeatures.Implemented -- but flagged here for whoever adds
        // CLFSH.
        [CpuModel.PentiumIII] = new CpuModelIdentity
        {
            Signature = 0x0683,
            MaxStandardLeaf = 3,
            MaxExtendedLeaf = 0,
            Silicon = new CpuFeatures().With(
                CpuFeature.Fpu, CpuFeature.Vme, CpuFeature.De, CpuFeature.Pse, CpuFeature.Tsc,
                CpuFeature.Msr, CpuFeature.Pae, CpuFeature.Mce, CpuFeature.Cx8, CpuFeature.Apic,
                CpuFeature.Sep, CpuFeature.Mtrr, CpuFeature.Pge, CpuFeature.Mca, CpuFeature.Cmov,
                CpuFeature.Pat, CpuFeature.Pse36, CpuFeature.Mmx, CpuFeature.Fxsr, CpuFeature.Sse,
                CpuFeature.Isa486, CpuFeature.Identification, CpuFeature.ControlRegister4,
                CpuFeature.Pmc, CpuFeature.HintNops),
            Leaf2 = [0x03020101, 0x00000000, 0x00000000, 0x0C040882],
        },

        // Pentium 4 (Northwood), 2.40 GHz, stepping 7 (C1). Source:
        // InstLatX64 "GenuineIntel0000F27_P4_Nortwood_CPUID.txt" -- leaf 0
        // EAX=2, leaf 1 EAX=00000F27h, EDX=BFEBFBFFh (every legacy bit plus
        // SEP, CLFSH, MMX, FXSR, SSE, SSE2; no PSN on this non-Xeon SKU),
        // ECX=00004400h (CNXT-ID and xTPR, neither in CpuFeatures), leaf 2 =
        // 665B5101-00000000-00000000-007B7040 (used verbatim below), leaf
        // 0x80000000=80000004h (brand string only -- AP-485: extended
        // CPUID starts with the Pentium 4, so PII/PIII have none), leaf
        // 0x80000001 all zero (no EM64T on Northwood). The brand string
        // (leaves 0x80000002-4) is copied byte for byte from the dump and
        // decodes to 14 leading spaces + "Intel(R) Pentium(R) 4 CPU
        // 2.40GHz" + a null, 48 bytes exactly -- the right-justified
        // padding AP-485 documents for the Pentium 4's brand strings.
        [CpuModel.Pentium4] = new CpuModelIdentity
        {
            Signature = 0x0F27,
            MaxStandardLeaf = 2,
            MaxExtendedLeaf = 0x80000004,
            Silicon = new CpuFeatures().With(
                CpuFeature.Fpu, CpuFeature.Vme, CpuFeature.De, CpuFeature.Pse, CpuFeature.Tsc,
                CpuFeature.Msr, CpuFeature.Pae, CpuFeature.Mce, CpuFeature.Cx8, CpuFeature.Apic,
                CpuFeature.Sep, CpuFeature.Mtrr, CpuFeature.Pge, CpuFeature.Mca, CpuFeature.Cmov,
                CpuFeature.Pat, CpuFeature.Pse36, CpuFeature.Clfsh, CpuFeature.Mmx, CpuFeature.Fxsr,
                CpuFeature.Sse, CpuFeature.Sse2, CpuFeature.Isa486, CpuFeature.Identification,
                CpuFeature.ControlRegister4, CpuFeature.Pmc, CpuFeature.HintNops),
            Leaf2 = [0x665B5101, 0x00000000, 0x00000000, 0x007B7040],
            BrandString =
            [
                0x20202020, 0x20202020, 0x20202020, 0x6E492020,
                0x286C6574, 0x50202952, 0x69746E65, 0x52286D75,
                0x20342029, 0x20555043, 0x30342E32, 0x007A4847,
            ],
        },

        // Core 2 Extreme X6800 (Conroe), 2.93 GHz, stepping 6 (B2) -- the
        // exact stepping cpu-models.md's own Core 2 example names (6/15/6).
        // Source: InstLatX64 "GenuineIntel00006F6_Conroe_CPUID.txt" -- leaf
        // 0 EAX=0Ah (the real chip answers up to leaf 0xA; Aurora only gives
        // leaf 4 real meaning below leaf 7, so MaxStandardLeaf is pinned to
        // 4 here and leaves 5/6/8/9/0xA fall through to the generic
        // "unimplemented standard leaf" zero answer rather than the real
        // MONITOR/thermal/cache-topology data), leaf 1 EAX=000006F6h,
        // EDX=BFEBFBFFh (same legacy+SSE2 set as Northwood), ECX=0000E3BDh:
        // of the bits CpuFeatures names, SSE3(0), SSSE3(9) and CX16(13) are
        // set; MONITOR/DS-CPL/VMX/EST/TM2/xTPR/PDCM are real on this chip
        // too but have no CpuFeature member because Aurora has nothing to
        // gate them on. Leaf 2 copied verbatim below. Leaf 0x80000000
        // =80000008h; leaf 0x80000001 EDX=20100000h (LongMode(29), NX(20);
        // SYSCALL(11) reads 0 here because Intel only sets that bit while
        // the querying code is actually running in 64-bit mode, and this
        // dump was taken from 32-bit code even though the OS is x64),
        // ECX=00000001h (LahfLm). Leaf 0x80000008 EAX=00003024h (36-bit
        // physical, 48-bit linear) -- not used directly: Aurora derives
        // these two fields from advertised PAE/PSE-36/LongMode instead of
        // the raw dump, so they stay correct as those bits turn on one at a
        // time in later work packages.
        [CpuModel.Core2] = new CpuModelIdentity
        {
            Signature = 0x06F6,
            MaxStandardLeaf = 4,
            MaxExtendedLeaf = 0x80000008,
            Silicon = new CpuFeatures().With(
                CpuFeature.Fpu, CpuFeature.Vme, CpuFeature.De, CpuFeature.Pse, CpuFeature.Tsc,
                CpuFeature.Msr, CpuFeature.Pae, CpuFeature.Mce, CpuFeature.Cx8, CpuFeature.Apic,
                CpuFeature.Sep, CpuFeature.Mtrr, CpuFeature.Pge, CpuFeature.Mca, CpuFeature.Cmov,
                CpuFeature.Pat, CpuFeature.Pse36, CpuFeature.Clfsh, CpuFeature.Mmx, CpuFeature.Fxsr,
                CpuFeature.Sse, CpuFeature.Sse2, CpuFeature.Sse3, CpuFeature.Ssse3, CpuFeature.Cx16,
                CpuFeature.Syscall, CpuFeature.Nx, CpuFeature.LongMode, CpuFeature.LahfLm,
                CpuFeature.Isa486, CpuFeature.Identification, CpuFeature.ControlRegister4,
                CpuFeature.Pmc, CpuFeature.HintNops),
            Leaf2 = [0x05B0B101, 0x005657F0, 0x00000000, 0x2CB43049],
            BrandString =
            [
                0x65746E49, 0x2952286C, 0x726F4320, 0x4D542865,
                0x43203229, 0x20205550, 0x20202020, 0x58202020,
                0x30303836, 0x20402020, 0x33392E32, 0x007A4847,
            ],
        },

        // Core i7-4770 (Haswell-DT), 3.40 GHz, stepping 3 (C0) -- again the
        // exact family/model/stepping cpu-models.md's Haswell example names
        // (6/60/3). Source: InstLatX64 "GenuineIntel00306C3_Haswell_
        // CPUID.txt" -- leaf 0 EAX=0Dh (Aurora special-cases leaves 2, 4, 7
        // and 0xD only, so MaxStandardLeaf is the real 0xD and leaves
        // 3/5/6/8/9/0xA/0xB/0xC fall through to the generic zero answer),
        // leaf 1 EDX=BFEBFBFFh (same legacy set as Northwood/Conroe),
        // ECX=7FFAFBFFh: of the CpuFeatures bits, SSE3, PCLMULQDQ, SSSE3,
        // FMA, CX16, SSE4.1, SSE4.2, MOVBE, POPCNT, AES, XSAVE, AVX and
        // F16C are all set (RDRAND(30) is real too but is on the
        // "never advertise" list in cpu-models.md and so is not in
        // CpuFeatures at all). Leaf 2 copied verbatim below. Leaf 7 subleaf
        // 0 EBX=000027ABh: FSGSBASE(0), BMI1(3), AVX2(5), BMI2(8), ERMS(9)
        // all set; HLE(4) is *not* set, confirming cpu-models.md's note
        // that RTM/HLE are disabled by microcode (erratum HSW136) even
        // though the silicon has the transactional-memory hardware. Leaf
        // 0x80000001 ECX=00000021h (LahfLm(0), Lzcnt(5)), EDX=2C100000h
        // (LongMode(29), NX(20)).
        //
        // DISCREPANCY WORTH FLAGGING: cpu-models.md's summary row lists
        // "+ ABM/LZCNT, PREFETCHW" for Haswell's extended bits, but this
        // dump's leaf 0x80000001 ECX has bit 8 (PREFETCHW) clear -- 0x21 is
        // only bits 0 and 5. Mainstream Haswell client parts appear not to
        // set PREFETCHW (it shows up starting with Broadwell in other
        // dumps I did not fetch for this work package); Prefetchw is
        // therefore left off this table's silicon set rather than
        // asserted from the spec's summary text. None of this is
        // load-bearing today since Implemented has neither bit, but a
        // later work package should re-check against its own citation
        // before turning PREFETCHW on for Haswell.
        [CpuModel.Haswell] = new CpuModelIdentity
        {
            Signature = 0x306C3,
            MaxStandardLeaf = 0x0D,
            MaxExtendedLeaf = 0x80000008,
            Silicon = new CpuFeatures().With(
                CpuFeature.Fpu, CpuFeature.Vme, CpuFeature.De, CpuFeature.Pse, CpuFeature.Tsc,
                CpuFeature.Msr, CpuFeature.Pae, CpuFeature.Mce, CpuFeature.Cx8, CpuFeature.Apic,
                CpuFeature.Sep, CpuFeature.Mtrr, CpuFeature.Pge, CpuFeature.Mca, CpuFeature.Cmov,
                CpuFeature.Pat, CpuFeature.Pse36, CpuFeature.Clfsh, CpuFeature.Mmx, CpuFeature.Fxsr,
                CpuFeature.Sse, CpuFeature.Sse2, CpuFeature.Sse3, CpuFeature.Pclmulqdq, CpuFeature.Ssse3,
                CpuFeature.Fma, CpuFeature.Cx16, CpuFeature.Sse41, CpuFeature.Sse42, CpuFeature.Movbe,
                CpuFeature.Popcnt, CpuFeature.Aes, CpuFeature.Xsave, CpuFeature.Avx, CpuFeature.F16c,
                CpuFeature.Fsgsbase, CpuFeature.Bmi1, CpuFeature.Avx2, CpuFeature.Bmi2, CpuFeature.Erms,
                CpuFeature.Syscall, CpuFeature.Nx, CpuFeature.LongMode, CpuFeature.LahfLm, CpuFeature.Lzcnt,
                CpuFeature.Isa486, CpuFeature.Identification, CpuFeature.ControlRegister4,
                CpuFeature.Pmc, CpuFeature.HintNops),
            Leaf2 = [0x76036301, 0x00F0B5FF, 0x00000000, 0x00C10000],
            BrandString =
            [
                0x65746E49, 0x2952286C, 0x726F4320, 0x4D542865,
                0x37692029, 0x3737342D, 0x50432030, 0x20402055,
                0x30342E33, 0x007A4847, 0x00000000, 0x00000000,
            ],
        },
    };

    public static CpuModelIdentity Get(CpuModel model) => Models[model];
}

public sealed partial class Cpu
{
    /// <summary>
    /// Which CPUID-visible feature lives at which leaf/register/bit. The
    /// same table answers leaf 1, leaf 7 subleaf 0 and the extended leaf --
    /// a feature that moved leaf between generations (there is none among
    /// the ones Aurora names yet) would simply get two entries.
    /// </summary>
    private static readonly (CpuFeature Feature, uint Leaf, CpuidRegister Register, int Bit)[] FeatureBits =
    [
        // Leaf 1, EDX.
        (CpuFeature.Fpu, 1, CpuidRegister.Edx, 0),
        (CpuFeature.Vme, 1, CpuidRegister.Edx, 1),
        (CpuFeature.De, 1, CpuidRegister.Edx, 2),
        (CpuFeature.Pse, 1, CpuidRegister.Edx, 3),
        (CpuFeature.Tsc, 1, CpuidRegister.Edx, 4),
        (CpuFeature.Msr, 1, CpuidRegister.Edx, 5),
        (CpuFeature.Pae, 1, CpuidRegister.Edx, 6),
        (CpuFeature.Mce, 1, CpuidRegister.Edx, 7),
        (CpuFeature.Cx8, 1, CpuidRegister.Edx, 8),
        (CpuFeature.Apic, 1, CpuidRegister.Edx, 9),
        (CpuFeature.Sep, 1, CpuidRegister.Edx, 11),
        (CpuFeature.Mtrr, 1, CpuidRegister.Edx, 12),
        (CpuFeature.Pge, 1, CpuidRegister.Edx, 13),
        (CpuFeature.Mca, 1, CpuidRegister.Edx, 14),
        (CpuFeature.Cmov, 1, CpuidRegister.Edx, 15),
        (CpuFeature.Pat, 1, CpuidRegister.Edx, 16),
        (CpuFeature.Pse36, 1, CpuidRegister.Edx, 17),
        (CpuFeature.Clfsh, 1, CpuidRegister.Edx, 19),
        (CpuFeature.Mmx, 1, CpuidRegister.Edx, 23),
        (CpuFeature.Fxsr, 1, CpuidRegister.Edx, 24),
        (CpuFeature.Sse, 1, CpuidRegister.Edx, 25),
        (CpuFeature.Sse2, 1, CpuidRegister.Edx, 26),

        // Leaf 1, ECX.
        (CpuFeature.Sse3, 1, CpuidRegister.Ecx, 0),
        (CpuFeature.Pclmulqdq, 1, CpuidRegister.Ecx, 1),
        (CpuFeature.Ssse3, 1, CpuidRegister.Ecx, 9),
        (CpuFeature.Fma, 1, CpuidRegister.Ecx, 12),
        (CpuFeature.Cx16, 1, CpuidRegister.Ecx, 13),
        (CpuFeature.Sse41, 1, CpuidRegister.Ecx, 19),
        (CpuFeature.Sse42, 1, CpuidRegister.Ecx, 20),
        (CpuFeature.Movbe, 1, CpuidRegister.Ecx, 22),
        (CpuFeature.Popcnt, 1, CpuidRegister.Ecx, 23),
        (CpuFeature.Aes, 1, CpuidRegister.Ecx, 25),
        (CpuFeature.Xsave, 1, CpuidRegister.Ecx, 26),
        (CpuFeature.Avx, 1, CpuidRegister.Ecx, 28),
        (CpuFeature.F16c, 1, CpuidRegister.Ecx, 29),

        // Leaf 7, subleaf 0, EBX.
        (CpuFeature.Fsgsbase, 7, CpuidRegister.Ebx, 0),
        (CpuFeature.Bmi1, 7, CpuidRegister.Ebx, 3),
        (CpuFeature.Avx2, 7, CpuidRegister.Ebx, 5),
        (CpuFeature.Bmi2, 7, CpuidRegister.Ebx, 8),
        (CpuFeature.Erms, 7, CpuidRegister.Ebx, 9),

        // Extended leaf 0x80000001, EDX.
        (CpuFeature.Syscall, 0x80000001, CpuidRegister.Edx, 11),
        (CpuFeature.Nx, 0x80000001, CpuidRegister.Edx, 20),
        (CpuFeature.LongMode, 0x80000001, CpuidRegister.Edx, 29),

        // Extended leaf 0x80000001, ECX.
        (CpuFeature.LahfLm, 0x80000001, CpuidRegister.Ecx, 0),
        (CpuFeature.Lzcnt, 0x80000001, CpuidRegister.Ecx, 5),
        (CpuFeature.Prefetchw, 0x80000001, CpuidRegister.Ecx, 8),
    ];

    private static uint FeatureRegister(CpuFeatures features, uint leaf, CpuidRegister register)
    {
        uint value = 0;
        foreach (var entry in FeatureBits)
            if (entry.Leaf == leaf && entry.Register == register && features.Has(entry.Feature))
                value |= 1u << entry.Bit;
        return value;
    }

    /// <summary>Leaf 1 EDX bit 9: the local APIC. SDM 10.4.3's own rule.</summary>
    private const int ApicEdxBit = 9;

    /// <summary>Bit 11 of IA32_APIC_BASE: the hardware-enable bit this bit's own value follows.</summary>
    private const ulong ApicBaseHardwareEnableBit = 1UL << 11;

    /// <summary>
    /// Leaf 1 EDX, with bit 9 additionally following IA32_APIC_BASE's own
    /// hardware-enable bit (SDM 10.4.3: "the CPUID feature flag for the
    /// APIC is also set to 0" while it is clear) -- work package P4.4's
    /// independent review, item "Low". A model whose silicon has no local
    /// APIC at all never sets the bit regardless of the MSR (<see
    /// cref="FeatureRegister"/> already answers 0 for it); on the P5 set,
    /// where APIC_BASE is not a real MSR at all, this MSR-backed storage
    /// stays at its power-on value with the enable bit set forever -- the
    /// unit is always hardware-enabled there (coordinator decision A11)
    /// -- so this check is a no-op for that family and only ever clears the
    /// bit on a family-six model that has actually disabled its APIC.
    /// </summary>
    private uint Leaf1EdxWithApicBit(CpuFeatures features)
    {
        uint edx = FeatureRegister(features, 1, CpuidRegister.Edx);
        if ((Msrs.ApicBaseRaw & ApicBaseHardwareEnableBit) == 0)
        {
            edx &= ~(1u << ApicEdxBit);
        }

        return edx;
    }

    /// <summary>
    /// CPUID: the processor says what it is.
    ///
    /// Before it, software identified the processor by the differences
    /// between the models -- which flags a shift left alone, whether an
    /// instruction existed -- a collection of tricks that got less reliable
    /// with each generation. CPUID replaced all of it with a question, and
    /// from Windows 95 onwards everything asks.
    ///
    /// What it answers is always <see cref="CpuState.Features"/> -- the
    /// model's silicon feature set already narrowed to what Aurora
    /// implements -- never the model's raw silicon set. See
    /// CpuFeatures.Implemented for why: a Pentium's CX8 does not become
    /// real to a guest until CMPXCHG8B itself does.
    /// </summary>
    private void ExecuteCpuId()
    {
        // The BIOS_SIGN_ID idiom (Intel's "Runtime Microcode Update" paper):
        // firmware writes 0 to MSR 8Bh, executes CPUID, then reads the
        // microcode revision back out of the same MSR's high half. Aurora
        // has no microcode to update, so the revision is always 0 -- but the
        // hook has to fire on every CPUID regardless of which MSR set is
        // selected, since a family-five Cpu shares this one MSR-set instance
        // even though its own RDMSR/WRMSR never expose the index.
        Msrs.NoteCpuIdExecuted();

        CpuModelIdentity identity = CpuModelCatalog.Get(State.Model);
        uint eax = State.Eax;

        if ((eax & 0x80000000u) != 0)
        {
            AnswerExtendedLeaf(identity, eax);
            return;
        }

        // A basic leaf above the maximum returns the data of the highest
        // basic leaf: real Intel silicon does this because the microcode
        // that decodes EAX simply has nothing else behind the leaves it
        // does not implement, and the 486 code this replaces already
        // relied on it (leaf 1 answers for any nonzero EAX on a 486, since
        // its own maximum is 1).
        AnswerStandardLeaf(identity, Math.Min(eax, identity.MaxStandardLeaf));
    }

    private void AnswerStandardLeaf(CpuModelIdentity identity, uint leaf)
    {
        switch (leaf)
        {
            case 0:
                State.Eax = identity.MaxStandardLeaf;
                // EBX, EDX, ECX spells "GenuineIntel" when the twelve bytes
                // are laid out in that sequence -- this is what the silicon
                // does, not an arbitrary register order.
                State.Ebx = 0x756E6547;        // "Genu"
                State.Edx = 0x49656E69;        // "ineI"
                State.Ecx = 0x6C65746E;        // "ntel"
                return;

            case 1:
                State.Eax = identity.Signature;
                State.Ebx = LeafOneEbx(State.Features);
                State.Ecx = FeatureRegister(State.Features, 1, CpuidRegister.Ecx);
                State.Edx = Leaf1EdxWithApicBit(State.Features);
                return;

            case 2:
                WriteLeaf2(identity);
                return;

            case 3:
                // The Pentium III processor serial number. cpu-models.md's
                // "not to advertise yet" list keeps PSN off until there is a
                // serial-number MSR to back it -- and even then, matching
                // real BIOS behaviour, disabled by default -- so this
                // leaf is always zero regardless of what a real chip's
                // fused serial happens to hold.
                State.Eax = State.Ebx = State.Ecx = State.Edx = 0;
                return;

            case 4:
                // No cache hierarchy is modelled. A null cache-type
                // descriptor (all zero) is what real software reads as
                // "no more caches to enumerate", so subleaf 0 answering
                // this way is a legitimate description of a processor with
                // no caching, not a stub.
                State.Eax = State.Ebx = State.Ecx = State.Edx = 0;
                return;

            case 7:
                State.Eax = 0;   // no subleaves beyond 0
                State.Ebx = FeatureRegister(State.Features, 7, CpuidRegister.Ebx);
                State.Ecx = 0;
                State.Edx = 0;
                return;

            case 0x0D:
                // XSAVE area enumeration: zero until Xsave is advertised,
                // because without it there is no save area to describe.
                State.Eax = State.Ebx = State.Ecx = State.Edx = 0;
                return;

            default:
                // A standard leaf between 1 and this model's maximum that
                // Aurora otherwise has no data for (MONITOR/MWAIT topology,
                // thermal management, performance counters, ...). Zero is
                // the same "nothing here" answer leaf 4 gives.
                State.Eax = State.Ebx = State.Ecx = State.Edx = 0;
                return;
        }
    }

    private void WriteLeaf2(CpuModelIdentity identity)
    {
        if (identity.Leaf2 is { } leaf2)
        {
            State.Eax = leaf2[0];
            State.Ebx = leaf2[1];
            State.Ecx = leaf2[2];
            State.Edx = leaf2[3];
            return;
        }

        // AL=1 says one iteration of leaf 2 is all there ever is, and the
        // null descriptor bytes above it say nothing is cached -- the same
        // "no cache modelled" answer leaf 4 gives, for a model with no
        // cited leaf-2 dump to copy instead.
        State.Eax = 0x00000001;
        State.Ebx = 0;
        State.Ecx = 0;
        State.Edx = 0;
    }

    /// <summary>
    /// Brand index 0 (no brand string lookup table entry), logical
    /// processor count and initial APIC ID both 0 (no multiprocessing
    /// modelled), and the CLFLUSH line size field -- 8, meaning 64 bytes --
    /// present only once CLFSH itself is advertised, since the field is
    /// meaningless without the instruction it describes.
    /// </summary>
    private static uint LeafOneEbx(CpuFeatures features)
        => features.Has(CpuFeature.Clfsh) ? 0x00000800u : 0u;

    private void AnswerExtendedLeaf(CpuModelIdentity identity, uint eax)
    {
        if (identity.MaxExtendedLeaf == 0 || eax > identity.MaxExtendedLeaf)
        {
            // Either this silicon predates extended CPUID entirely (AP-485:
            // Intel introduced 0x80000000+ with the Pentium 4, so every
            // 386/486/P5/P6 model here has none), or the request is past
            // what this model does implement. Either way, real silicon
            // answers with whatever the highest *basic* leaf holds --
            // there is no separate "extended, but unimplemented" answer to
            // fall back to underneath the basic one.
            AnswerStandardLeaf(identity, identity.MaxStandardLeaf);
            return;
        }

        switch (eax)
        {
            case 0x80000000:
                State.Eax = identity.MaxExtendedLeaf;
                State.Ebx = State.Ecx = State.Edx = 0;
                return;

            case 0x80000001:
                State.Eax = State.Ebx = 0;
                State.Ecx = FeatureRegister(State.Features, 0x80000001, CpuidRegister.Ecx);
                State.Edx = FeatureRegister(State.Features, 0x80000001, CpuidRegister.Edx);
                return;

            case 0x80000002:
            case 0x80000003:
            case 0x80000004:
            {
                // identity.BrandString is guaranteed non-null here: every
                // model whose MaxExtendedLeaf reaches 0x80000004 has one
                // (see the catalog above).
                uint[] brand = identity.BrandString!;
                int chunk = (int)(eax - 0x80000002) * 4;
                State.Eax = brand[chunk + 0];
                State.Ebx = brand[chunk + 1];
                State.Ecx = brand[chunk + 2];
                State.Edx = brand[chunk + 3];
                return;
            }

            case 0x80000008:
            {
                // Address sizes are computed from what is actually
                // advertised, not copied from a real chip's dump: PAE or
                // PSE-36 is what makes 36 physical address bits real to a
                // guest, and long mode is what makes the upper 16 of 64
                // linear bits real, so the two answers stay correct as
                // those bits turn on one at a time in later work packages
                // instead of jumping straight to a real chip's numbers.
                byte physical = State.Features.Has(CpuFeature.Pae) || State.Features.Has(CpuFeature.Pse36)
                    ? (byte)36 : (byte)32;
                byte linear = State.Features.Has(CpuFeature.LongMode) ? (byte)48 : (byte)32;
                State.Eax = (uint)physical | ((uint)linear << 8);
                State.Ebx = State.Ecx = State.Edx = 0;
                return;
            }

            default:
                // In range but otherwise unimplemented (0x80000005-7 on the
                // models that have them): the same "nothing here" zero
                // answer as an unimplemented standard leaf.
                State.Eax = State.Ebx = State.Ecx = State.Edx = 0;
                return;
        }
    }
}
