namespace Aurora386.Core.Cpu;

/// <summary>
/// One capability a processor model can have, named the way CPUID itself
/// names it (Intel SDM Vol.2A, table "Feature Information Returned in the
/// ECX/EDX Registers"). Two entries at the end are not CPUID bits at all:
///
/// <list type="bullet">
/// <item><see cref="Isa486"/> is BSWAP, XADD, CMPXCHG, INVD/WBINVD, INVLPG
/// and the four CR0 bits (WP, AM, NW, CD) the 486 added, plus CR0.ET being
/// hardwired rather than software-set. There is no CPUID bit for any of
/// this -- CPUID itself arrived partway through the 486's life -- so
/// software that wants to know detects it the old way, by executing one of
/// the new instructions and seeing whether it faults.</item>
/// <item><see cref="Identification"/> is whether EFLAGS.ID may move, which
/// is how software decides CPUID exists at all before daring to execute it
/// (a 386, or an early 486 stepping, would fault on CPUID itself; letting
/// the flag move without the instruction being there would be answering a
/// question the processor cannot back up).</item>
/// </list>
///
/// The numeric value is the bit position in <see cref="CpuFeatures"/>, not
/// a CPUID bit position -- those live in the table inside
/// Cpu.Identification.cs, because the same capability sits at different
/// leaf/register/bit coordinates for the legacy leaf-1 bits, the extended
/// leaf and leaf 7.
/// </summary>
public enum CpuFeature
{
    Fpu, Vme, De, Pse, Tsc, Msr, Pae, Mce, Cx8, Apic, Sep, Mtrr, Pge, Mca, Cmov, Pat, Pse36,
    Clfsh, Mmx, Fxsr, Sse, Sse2, Sse3, Pclmulqdq, Ssse3, Fma, Cx16, Sse41, Sse42, Movbe, Popcnt,
    Aes, Xsave, Avx, F16c, Fsgsbase, Bmi1, Avx2, Bmi2, Erms, Syscall, Nx, LongMode, LahfLm, Lzcnt,
    Prefetchw,

    /// <summary>The 486's own new instructions and CR0 bits. See the type summary.</summary>
    Isa486,

    /// <summary>EFLAGS.ID is writable, i.e. CPUID itself exists. See the type summary.</summary>
    Identification,

    /// <summary>
    /// Whether CR4 exists as a register at all. Not a CPUID bit either: CR4
    /// arrived with the Pentium (P5), and naming it in a MOV to/from a
    /// control register on an earlier processor is <c>#UD</c>, exactly like
    /// naming the perpetually reserved CR1/CR5/CR6/CR7 on any model (Intel
    /// SDM Vol.3 Ch.2, "Control Registers"; cpu-models.md §2). Software does
    /// not probe for it directly the way it probes for <see cref="Isa486"/>
    /// -- it infers CR4 from the family/model CPUID itself already reports,
    /// or from one of the bits CR4 controls (VME, DE, ...) failing to hold
    /// -- but Aurora still needs a feature bit to gate the MOV CRn encoding.
    /// </summary>
    ControlRegister4,

    /// <summary>
    /// The test registers, reached with MOV r32,TRn and MOV TRn,r32 (0F 24, 0F
    /// 26). The 386 has TR6 and TR7 for its TLB test hardware and the 486 adds
    /// TR3-TR5 for the cache; the Pentium removed them all and moved its test
    /// functions into model-specific registers, so there both encodings are
    /// #UD. CPU probes of the period tell a 486 from a Pentium this way.
    /// </summary>
    TestRegisters,

    /// <summary>
    /// Not a CPUID bit: whether RDPMC and the two on-chip performance
    /// counters behind PERFCTR0/1 (MSR C1h/C2h) exist at all. Real silicon
    /// answers this from the family/model, the same way software tells
    /// <see cref="Isa486"/> apart -- there is no CPUID feature bit for "this
    /// chip has performance-monitoring counters", because every P6-or-later
    /// part does. Gates RDPMC (Cpu.P6.cs) and CR4.PCE (CpuState.Cr4Writable).
    /// </summary>
    Pmc,

    /// <summary>
    /// Not a CPUID bit either: the reserved multi-byte NOP encodings (0F 18
    /// with a memory operand, 0F 19-0F 1F) that Intel documents from the
    /// Pentium Pro onward (plans/specs/p6.md, coordinator decision). Earlier
    /// silicon has nothing behind these bytes, so without this feature they
    /// fall through to an ordinary #UD exactly like any other unrecognised
    /// two-byte opcode.
    /// </summary>
    HintNops,
}

/// <summary>
/// A set of <see cref="CpuFeature"/> values, packed one bit per feature.
///
/// Two sets matter everywhere a feature is asked about. A model's "silicon"
/// set is what the real chip has, taken from CPUID dumps and the SDM. The
/// emulator's <see cref="Implemented"/> set is what Aurora actually does
/// something about -- there is no MTRR hardware behind CR4/MTRR MSRs yet,
/// no LAPIC behind the APIC bit, and so on. What CPUID reports, and what
/// gates an instruction, is always the intersection of the two
/// (<see cref="CpuState.Features"/>): a real Pentium has VME and CX8, but
/// Aurora only starts answering "yes" to a guest's probe once the matching
/// subsystem exists. This is also why turning a feature on for real, in a
/// later work package, is a one-line change to <see cref="Implemented"/>
/// and nothing else -- the per-model tables already say which silicon
/// carries the bit.
/// </summary>
public readonly struct CpuFeatures : IEquatable<CpuFeatures>
{
    private readonly ulong _bits;

    private CpuFeatures(ulong bits) => _bits = bits;

    public static readonly CpuFeatures None = default;

    /// <summary>
    /// What this emulator actually executes, independent of which processor
    /// it is pretending to be. Selecting a Pentium advertises FPU, DE, TSC,
    /// MSR, MCE, CX8 and CR4 today (see the remarks below) -- the rest of
    /// its silicon (VME, PSE, ...) stays off until the work package that
    /// implements it adds the matching bit here. Every later CPU work
    /// package extends this set, never the per-model tables -- widening
    /// what a model *could* show is not the same change as widening what
    /// Aurora *can do*.
    /// </summary>
    /// <remarks>
    /// Work package P2.1 (Pentium system registers and instructions) added
    /// <see cref="CpuFeature.Tsc"/>, <see cref="CpuFeature.Msr"/>,
    /// <see cref="CpuFeature.Cx8"/>, <see cref="CpuFeature.Mce"/>,
    /// <see cref="CpuFeature.De"/> and <see cref="CpuFeature.ControlRegister4"/>:
    /// RDTSC, RDMSR/WRMSR and the P5 MSR set, CMPXCHG8B, CR4.MCE (settable,
    /// never actually raises #MC -- there is no machine-check hardware
    /// behind it) and CR4.DE (the DR4/DR5 aliasing rule) now all exist.
    /// Work package P2.2 added <see cref="CpuFeature.Pse"/>: CR4.PSE is now
    /// writable on every model whose silicon has it, and a page directory
    /// entry with PS set maps 4 MB directly (Cpu.Paging.cs's
    /// TranslateLargePage). PAE and PGE stay off deliberately: the CR4 bits
    /// that would enable them are real on later silicon but nothing in
    /// Aurora acts on global pages yet, and PAE's 3-level paging mode is a
    /// separate work package of its own (cpu-models.md §1's "should not
    /// advertise" list and §2's own writable-CR4-bit note).
    /// Work package P2.3 added <see cref="CpuFeature.Vme"/>: CR4.VME and
    /// CR4.PVI are now writable on every model whose silicon has them (see
    /// <see cref="CpuState.Cr4Writable"/>), EFLAGS.VIF and .VIP now exist and
    /// move under the rules Cpu.VirtualMode.cs implements, and V86's
    /// CLI/STI/PUSHF/POPF/IRET/INT n follow SDM Vol.3 Table 20-2 instead of
    /// the flat "trap below IOPL 3, real flag at IOPL 3" rule Aurora used
    /// before this bit existed.
    /// Work package P2.5 added <see cref="CpuFeature.Mmx"/>: the eight packed
    /// integer instructions' opcodes (Cpu.Mmx.cs) now execute instead of
    /// faulting, aliased onto the x87 register file exactly as the Pentium
    /// MMX's silicon does (Fpu/X87Unit.cs's MMX section).
    /// Work package P3.1 (Pentium Pro / Pentium II) added
    /// <see cref="CpuFeature.Cmov"/>, <see cref="CpuFeature.Sep"/>,
    /// <see cref="CpuFeature.Fxsr"/>, <see cref="CpuFeature.Mtrr"/>,
    /// <see cref="CpuFeature.Pat"/>, <see cref="CpuFeature.Mca"/>,
    /// <see cref="CpuFeature.Pmc"/> and <see cref="CpuFeature.HintNops"/>:
    /// CMOVcc/FCMOVcc/FCOMI family, SYSENTER/SYSEXIT, FXSAVE/FXRSTOR, the
    /// family-six MSR set (MTRRs, PAT, the inert MCA bank registers), RDPMC
    /// and the reserved multi-byte NOP space all now execute instead of
    /// faulting on the models whose silicon carries them (Cpu.P6.cs,
    /// ModelSpecificRegisters.cs). PAE, PGE and PSE-36 stayed off at the
    /// time -- the CR4/paging work behind them was a separate work
    /// package's scope (plans/specs/p6.md's coordinator decisions) -- and so
    /// did APIC and SSE, which still have no device or register file behind
    /// them.
    /// The P3.1 paging review added <see cref="CpuFeature.Pae"/> and
    /// <see cref="CpuFeature.Pge"/>: CR4.PAE's three-level, 64-bit-entry walk
    /// (Cpu.Paging.cs's TranslatePae) and CR4.PGE's global leaf entries
    /// (FlushTlbForCr3Load) both execute now, so both bits move on every
    /// model whose silicon carries them -- PentiumII and later, per the
    /// catalog (Cpu.Identification.cs). PSE-36 stays off: nothing here acts
    /// on a 4 MB PDE's own bits 20:13 as an address extension.
    /// Work package P4.1 (SSE) added <see cref="CpuFeature.Sse"/>: XMM0-7 and
    /// MXCSR are now real architectural state (Cpu/Sse/SseUnit.cs), the
    /// whole SSE instruction set executes (Cpu.Sse.cs), FXSAVE/FXRSTOR carry
    /// that state for real (Cpu.P6.cs) instead of the placeholder a part
    /// without SSE still shows, and CR4.OSXMMEXCPT is writable
    /// (<see cref="CpuState.Cr4Osxmmexcpt"/>) -- so PentiumIII and later now show SSE
    /// to a guest exactly as their silicon does.
    /// Work package P4.2 (SSE2) added <see cref="CpuFeature.Sse2"/> and
    /// <see cref="CpuFeature.Clfsh"/>: the packed/scalar double-precision
    /// arithmetic (Cpu/Sse/DoubleFloat.cs, Cpu.Sse2*.cs), every MMX integer
    /// instruction widened to the 128-bit XMM registers, and CLFLUSH/LFENCE/
    /// MFENCE/MOVNTI all now execute instead of faulting, on the Pentium 4
    /// and every later model this emulator carries (the models whose
    /// silicon carries the two bits, per the catalog in
    /// Cpu.Identification.cs) -- and MXCSR_MASK's own DAZ bit is now real,
    /// on those same models, for single precision too
    /// (<see cref="Sse.SseUnit.MxcsrMaskFor"/>).
    /// Work package P4.3 adds SSE3's horizontal arithmetic, duplicate loads,
    /// LDDQU and x87 FISTTP. Core2 is the first catalog model with SSE3;
    /// the catalog's Northwood Pentium 4 does not advertise Prescott's bit.
    /// Work package P4.4 adds <see cref="CpuFeature.Apic"/>: every model
    /// whose silicon carries an on-die local APIC (PentiumMmx onward, per
    /// the catalog in Cpu.Identification.cs) now shows leaf 1 EDX bit 9,
    /// answers RDMSR/WRMSR on IA32_APIC_BASE (MSR 1Bh, both the P5 and the
    /// family-six set -- <see cref="ModelSpecificRegisters"/>), and has a
    /// working LAPIC device (<c>Devices.Apic.LocalApic</c>) behind that MSR.
    /// A model without this bit in its silicon (386, 486, plain Pentium)
    /// is unaffected: its <c>Devices.Apic.LocalApic</c> wrapper always finds
    /// the APIC "absent" and forwards the 8259 unchanged (coordinator
    /// decision A1, plans/specs/apic.md).
    /// </remarks>
    public static readonly CpuFeatures Implemented =
        None.With(CpuFeature.Fpu, CpuFeature.Isa486, CpuFeature.Identification,
            CpuFeature.Tsc, CpuFeature.Msr, CpuFeature.Cx8, CpuFeature.Mce, CpuFeature.De,
            CpuFeature.ControlRegister4, CpuFeature.Pse, CpuFeature.Vme, CpuFeature.TestRegisters,
            CpuFeature.Mmx, CpuFeature.Cmov, CpuFeature.Sep, CpuFeature.Fxsr, CpuFeature.Mtrr,
            CpuFeature.Pat, CpuFeature.Mca, CpuFeature.Pmc, CpuFeature.HintNops,
            CpuFeature.Pae, CpuFeature.Pge, CpuFeature.Sse, CpuFeature.Sse2, CpuFeature.Clfsh,
            CpuFeature.Sse3, CpuFeature.Apic);

    /// <summary>
    /// What Aurora implements for one model. Before work package P3.1 this
    /// held <see cref="CpuFeature.Msr"/> back from every family-six-or-later
    /// model: only the Pentium's own MSR set existed, and a family-6
    /// processor that claimed MSR support would have been asked for
    /// registers it does not have -- software for those families reads
    /// BIOS_SIGN_ID (8Bh) and PLATFORM_ID (17h) without checking anything
    /// first, and would take a #GP -- and would clear the high half of the
    /// TSC on a WRMSR, which is a P5 rule only.
    ///
    /// P3.1 gives family six its own MSR set (<see cref="ModelSpecificRegisters"/>
    /// selects it from <see cref="CpuState.Model"/>), so there is nothing left
    /// to hold back: every model simply shows <see cref="Implemented"/>
    /// intersected with its own silicon. The method stays, rather than being
    /// replaced by the bare property, as the one place a future family's own
    /// carve-out would go.
    /// </summary>
    public static CpuFeatures ImplementedFor(CpuModel model) => Implemented;

    public bool Has(CpuFeature feature) => (_bits & Mask(feature)) != 0;

    public CpuFeatures With(CpuFeature feature) => new(_bits | Mask(feature));

    public CpuFeatures With(params ReadOnlySpan<CpuFeature> features)
    {
        ulong bits = _bits;
        foreach (CpuFeature feature in features) bits |= Mask(feature);
        return new CpuFeatures(bits);
    }

    public CpuFeatures Without(CpuFeature feature) => new(_bits & ~Mask(feature));

    /// <summary>
    /// What a model actually shows: the silicon's own bits, held down to
    /// the ones this emulator implements. See <see cref="Implemented"/>.
    /// </summary>
    public CpuFeatures Intersect(CpuFeatures other) => new(_bits & other._bits);

    public CpuFeatures Union(CpuFeatures other) => new(_bits | other._bits);

    private static ulong Mask(CpuFeature feature) => 1UL << (int)feature;

    public bool Equals(CpuFeatures other) => _bits == other._bits;
    public override bool Equals(object? obj) => obj is CpuFeatures other && Equals(other);
    public override int GetHashCode() => _bits.GetHashCode();
    public static bool operator ==(CpuFeatures left, CpuFeatures right) => left.Equals(right);
    public static bool operator !=(CpuFeatures left, CpuFeatures right) => !left.Equals(right);
}
