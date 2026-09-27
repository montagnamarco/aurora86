// The model-specific register space RDMSR/WRMSR read and write. Named and
// scoped the way cpu-models.md §2 describes the Pentium's own set: sources
// are rcollins.org's "p5msr" (the index list, and 03h/0Ah/0Fh being
// deliberately trapped by microcode) and Intel SDM Vol.3 §17's footnote on
// TSC write semantics. Work package P3.1 (plans/specs/p6.md, source for
// every family-six constant and rule below) adds the Pentium Pro/Pentium
// II's own set alongside it: one class, holding both families' storage, so
// that the single <see cref="Cpu.Msrs"/> field a snapshot already reaches
// through does not have to change shape. Which set answers RDMSR/WRMSR is
// decided at call time from <see cref="Cpu.State"/>'s own model -- the P5
// set stays exactly the size and behaviour it always was for every model
// that carries it, and the family-six set is new storage alongside it that
// a Pentium/Pentium MMX simply never reaches.
//
// AGENTS.md asks that every MSR index, bit and field carry a named constant
// and a comment; that is the organising rule for everything below.

namespace Aurora386.Core.Cpu;

/// <summary>
/// The model-specific registers, for both families this emulator carries:
/// the Pentium's (P5) five registers, unchanged since work package P2.1, and
/// the Pentium Pro/Pentium II's (family six) larger set added by P3.1. A
/// model selects which set answers RDMSR/WRMSR/WRMSR-to-the-TSC by testing
/// <see cref="Cpu.State"/>'s own <see cref="CpuState.Model"/> -- see
/// <see cref="IsFamilySix"/> -- exactly the same test
/// <see cref="CpuFeatures.ImplementedFor"/> used to apply before family six
/// had registers of its own to answer with.
///
/// Owned by a <see cref="Cpu"/> instance rather than static: the TSC MSR
/// (0x10) is not separate storage at all, it reads and writes the same
/// counter <see cref="Cpu.Tsc"/> and RDTSC do, so this class needs the owning
/// Cpu to reach it.
/// </summary>
public sealed class ModelSpecificRegisters
{
    private readonly Cpu _cpu;

    public ModelSpecificRegisters(Cpu cpu) => _cpu = cpu;

    /// <summary>
    /// Whether <see cref="Cpu.State"/>'s current model answers from the
    /// family-six set rather than the P5 one. The catalog's only family-six
    /// models are Deschutes (Pentium II) and later (plans/specs/p6.md's
    /// coordinator decision), which is exactly what <see cref="CpuModel"/>'s
    /// own ordering already encodes.
    /// </summary>
    private bool IsFamilySix => _cpu.State.Model >= CpuModel.PentiumII;

    // ==== the Pentium (P5) set, unchanged since work package P2.1 =========
    //
    // The two machine-check registers. cpu-models.md §2: "both may be
    // modeled as inert read/write scratch -- no MCE logic needed at P2.1".
    // No #MC is ever raised (CR4.MCE is settable but there is no
    // machine-check hardware behind it), so nothing here is ever read
    // except by software probing for the MSRs' mere presence.

    /// <summary>P5_MC_ADDR: the address of the last machine-check event. Inert scratch (see the type summary).</summary>
    public ulong McAddr { get; set; }

    /// <summary>P5_MC_TYPE: the type of the last machine-check event. Inert scratch.</summary>
    public ulong McType { get; set; }

    /// <summary>
    /// CESR: Control and Event Select Register, which on real silicon picks
    /// what <see cref="Counter0"/> and <see cref="Counter1"/> each count.
    /// Modelled as inert storage: Aurora counts no performance events, so
    /// RDMSR/WRMSR round-trip whatever was written and nothing more.
    /// </summary>
    public ulong Cesr { get; set; }

    private ulong _counter0;
    private ulong _counter1;

    /// <summary>The two on-chip performance counters are 40 bits wide on real P5 silicon (rcollins.org "p5msr"); writes are masked to that width even though nothing increments them.</summary>
    private const ulong CounterMask = (1UL << 40) - 1;

    /// <summary>PerfCtr0 (MSR 0x12). See <see cref="CounterMask"/>.</summary>
    public ulong Counter0
    {
        get => _counter0;
        set => _counter0 = value & CounterMask;
    }

    /// <summary>PerfCtr1 (MSR 0x13).</summary>
    public ulong Counter1
    {
        get => _counter1;
        set => _counter1 = value & CounterMask;
    }

    // The index each MSR sits at, named the way rcollins.org's "p5msr" and
    // the SDM's own MSR table name them.
    public const uint P5McAddr = 0x00;
    public const uint P5McType = 0x01;
    public const uint TimeStampCounter = 0x10;
    public const uint CesrIndex = 0x11;
    public const uint Ctr0Index = 0x12;
    public const uint Ctr1Index = 0x13;

    // ==== the family-six set, added by work package P3.1 ==================
    // Every index, bit and reset value below is cited in plans/specs/p6.md
    // item 10, which is itself the summary of the hardware-researcher's
    // report (Intel SDM Vol.3's MSR appendix, the Pentium Pro BIOS Writer's
    // Guide for the MTRR count, Intel's "Runtime Microcode Update" paper for
    // the BIOS_SIGN_ID idiom).

    /// <summary>PLATFORM_ID (17h): read-only. Real silicon reports voltage/bus-ratio identification a BIOS uses to pick a microcode file; Aurora has no such SKU data to report, so RDMSR always answers 0 and WRMSR is #GP(0) like every other read-only MSR here.</summary>
    public const uint PlatformIdIndex = 0x17;

    /// <summary>
    /// APIC_BASE (1Bh): work package P4.4's LocalApic reads this through
    /// <see cref="ApicBaseChanged"/> to know where its MMIO page belongs and
    /// whether it is hardware-enabled. Reset value FEE00900h -- BSP flag
    /// (bit 8) and the APIC global-enable bit (bit 11) both set, the default
    /// power-on state of the first processor in the system.
    ///
    /// Coordinator ruling (independent review, item 13): this MSR exists
    /// from the P6 family on, never on the P5 set -- a Pentium MMX's on-die
    /// APIC is real (the catalog's silicon still carries <see
    /// cref="CpuFeature.Apic"/>) but fixed at FEE00000h and always
    /// hardware-enabled, with no MSR to move or disable it; the only way to
    /// quiet it is SVR's own software-enable bit. RDMSR/WRMSR here therefore
    /// still dispatch on <see cref="IsFamilySix"/> alone, exactly as every
    /// other family-six-only register in this file does -- P5's own switch
    /// (<see cref="TryReadP5"/>/<see cref="TryWriteP5"/>) never answers
    /// index 1Bh, and a P5-model guest takes <c>#GP(0)</c> reading or
    /// writing it, matching real silicon.
    /// </summary>
    public const uint ApicBaseIndex = 0x1B;
    private const ulong ApicBaseResetValue = 0xFEE00900UL;

    /// <summary>Bit 8: BSP, the first processor in the system. Software-settable, but Aurora models one CPU so nothing reads it back except the guest itself.</summary>
    private const ulong ApicBaseBspBit = 1UL << 8;

    /// <summary>Bit 10: EXTD, x2APIC mode. Not modelled on any silicon this emulator carries -- writing it is <c>#GP(0)</c> (plans/specs/apic.md §1.1).</summary>
    private const ulong ApicBaseExtdBit = 1UL << 10;

    /// <summary>Bit 11: the hardware-enable bit. Clearing it disables the unit entirely, MMIO page included; setting it again re-enables it at its reset register values (coordinator decision A7).</summary>
    private const ulong ApicBaseEnableBit = 1UL << 11;

    /// <summary>Bits 35:12: the page the LAPIC's MMIO registers are mapped at, the same 36-bit physical width the MTRR fields above use.</summary>
    private const ulong ApicBaseAddressMask = 0xFFFFFF000UL;

    /// <summary>Bits a write actually changes: BSP (bit 8) is tolerated but read-only, so it is deliberately not in this mask -- see <see cref="TryWriteApicBase"/>.</summary>
    private const ulong ApicBaseWritableMask = ApicBaseAddressMask | ApicBaseEnableBit;

    /// <summary>EBL_CR_POWERON (2Ah): inert storage, no cited reset value -- BIOS-defined on real hardware, zero here.</summary>
    public const uint EblCrPoweronIndex = 0x2A;

    /// <summary>BIOS_UPDT_TRIG (79h): the microcode-update trigger. WRMSR is accepted (and discarded -- Aurora has no microcode to load); RDMSR is #GP(0) (plans/specs/p6.md item 10 marks this "uncertain", but a write-only MSR is what every cited source describes and there is nothing to read back regardless).</summary>
    public const uint BiosUpdtTrigIndex = 0x79;

    /// <summary>BIOS_SIGN_ID (8Bh): the microcode-revision idiom. WRMSR stores whatever is written (the BIOS writes 0 by convention); CPUID -- any function -- then sets the high 32 bits to the revision, which is always 0 here (<see cref="NoteCpuIdExecuted"/>, hooked from Cpu.Identification.cs).</summary>
    public const uint BiosSignIdIndex = 0x8B;
    private const uint BiosSignIdRevision = 0;

    /// <summary>PERFCTR0 (C1h): the 40-bit counter RDPMC(0) reads (Cpu.P6.cs). Inert -- Aurora counts no performance events -- so it round-trips whatever was written, masked to the same 40 bits <see cref="Counter0"/> is.</summary>
    public const uint PerfCtr0Index = 0xC1;

    /// <summary>PERFCTR1 (C2h): as above, for RDPMC(1).</summary>
    public const uint PerfCtr1Index = 0xC2;

    /// <summary>SYSENTER_CS (174h): the flat code selector SYSENTER/SYSEXIT synthesise their caches from (Cpu.P6.cs).</summary>
    public const uint SysenterCsIndex = 0x174;

    /// <summary>SYSENTER_ESP (175h): the stack pointer SYSENTER loads.</summary>
    public const uint SysenterEspIndex = 0x175;

    /// <summary>SYSENTER_EIP (176h): the instruction pointer SYSENTER loads.</summary>
    public const uint SysenterEipIndex = 0x176;

    /// <summary>PERFEVTSEL0 (186h): inert storage -- picks what PERFCTR0 counts on real silicon; Aurora counts nothing.</summary>
    public const uint PerfEvtSel0Index = 0x186;

    /// <summary>PERFEVTSEL1 (187h): as above, for PERFCTR1.</summary>
    public const uint PerfEvtSel1Index = 0x187;

    /// <summary>MCG_CAP (179h): read-only. Bits 7:0 are the bank count (5, the Pentium Pro's own number); bit 8 is MCG_CTL_P, which says MCG_CTL (17Bh) exists at all -- it does, here, as inert storage.</summary>
    public const uint McgCapIndex = 0x179;
    private const ulong McgCapBankCount = 5;
    private const ulong McgCapControlPresent = 1UL << 8;
    private const ulong McgCapValue = McgCapBankCount | McgCapControlPresent;

    /// <summary>MCG_STATUS (17Ah): inert storage, zero-initialised. No machine-check hardware ever sets RIPV/EIPV/MCIP here.</summary>
    public const uint McgStatusIndex = 0x17A;

    /// <summary>MCG_CTL (17Bh): inert storage -- present because MCG_CAP says so (see <see cref="McgCapControlPresent"/>).</summary>
    public const uint McgCtlIndex = 0x17B;

    /// <summary>The first of the five machine-check banks' four registers (CTL, STATUS, ADDR, MISC), 400h-403h; the next four banks follow at 404h, 408h, 40Ch, 410h, up to 413h. All twenty are inert storage, zero-initialised, gated on <see cref="CpuFeature.Mca"/> exactly like the MCG_* registers above.</summary>
    public const uint McBank0Index = 0x400;
    private const int McBankCount = 5;
    private const int McRegistersPerBank = 4;
    private const uint McHighestIndex = McBank0Index + McBankCount * McRegistersPerBank - 1;   // 413h

    /// <summary>DEBUGCTLMSR (1D9h): inert storage. The last-branch-record indices it would otherwise gate are not modelled (plans/specs/p6.md item 10 marks this "uncertain"), so the whole register is simply held.</summary>
    public const uint DebugCtlMsrIndex = 0x1D9;

    /// <summary>MTRRcap (FEh): read-only. VCNT (bits 7:0) is 8 -- the Pentium Pro BIOS Writer's Guide's variable-range count; FIX (bit 8) says the fixed-range registers exist; WC (bit 10) says the write-combining type is a valid MTRR encoding.</summary>
    public const uint MtrrCapIndex = 0xFE;
    private const ulong MtrrCapVariableCount = 8;
    private const ulong MtrrCapFixedSupported = 1UL << 8;
    private const ulong MtrrCapWriteCombining = 1UL << 10;
    private const ulong MtrrCapValue = MtrrCapVariableCount | MtrrCapFixedSupported | MtrrCapWriteCombining;

    /// <summary>MTRRdefType (2FFh): type (bits 2:0, the MTRR type set, <see cref="IsValidMtrrType"/>), FE (bit 10, fixed MTRRs enabled), E (bit 11, MTRRs enabled as a whole). Every other bit is reserved and #GP(0) if set; reset 0.</summary>
    public const uint MtrrDefTypeIndex = 0x2FF;
    private const uint MtrrDefTypeTypeMask = 0x7;
    private const uint MtrrDefTypeFixedEnable = 1u << 10;
    private const uint MtrrDefTypeEnable = 1u << 11;
    private const uint MtrrDefTypeWritableMask = MtrrDefTypeTypeMask | MtrrDefTypeFixedEnable | MtrrDefTypeEnable;

    /// <summary>MTRRphysBase0 (200h): the first of eight variable-range base/mask pairs, 200h-20Fh (base at 200h+2i, mask at 201h+2i). Base holds a type (bits 2:0) and the range's physical address (bits 35:12); mask holds V (bit 11, the pair is in use) and the address mask (bits 35:12). Every other bit is reserved and #GP(0) if set.</summary>
    public const uint MtrrPhysBase0Index = 0x200;
    private const int MtrrVariableCount = 8;
    private const uint MtrrHighestVariableIndex = MtrrPhysBase0Index + (uint)MtrrVariableCount * 2 - 1;   // 20Fh
    private const ulong MtrrPhysBaseTypeMask = 0x7;
    private const ulong MtrrPhysAddressMask = 0xFFFFFF000UL;   // bits 35:12
    private const ulong MtrrPhysBaseWritableMask = MtrrPhysBaseTypeMask | MtrrPhysAddressMask;
    private const ulong MtrrPhysMaskValidBit = 1UL << 11;
    private const ulong MtrrPhysMaskWritableMask = MtrrPhysMaskValidBit | MtrrPhysAddressMask;

    /// <summary>The eleven fixed-range MTRRs: MTRRfix64K_00000 (250h), MTRRfix16K_80000/A0000 (258h-259h), MTRRfix4K_C0000..F8000 (268h-26Fh). Each register packs eight one-byte range types; every byte is checked against the MTRR type set (<see cref="IsValidMtrrType"/>) before any of the eight is stored.</summary>
    public const uint MtrrFix64KIndex = 0x250;
    public const uint MtrrFix16KFirstIndex = 0x258;
    public const uint MtrrFix16KLastIndex = 0x259;
    public const uint MtrrFix4KFirstIndex = 0x268;
    public const uint MtrrFix4KLastIndex = 0x26F;

    /// <summary>PAT (277h): eight 3-bit memory types, one per PAT-index byte, reset 0007040600070406h -- the power-on default from Intel SDM Vol.3 table 11-11 (WB, WT, UC-, UC repeated twice). Every byte is checked against the same valid set fixed-range MTRRs use.</summary>
    public const uint PatIndex = 0x277;
    private const ulong PatResetValue = 0x0007040600070406UL;

    /// <summary>The eight memory types a PAT or MTRR byte/field may hold: UC, WC, WT, WP, WB and UC- (plans/specs/p6.md items 7-8). 2 and 3 are reserved encodings and anything above 7 does not fit the field at all; both are #GP(0) on WRMSR.</summary>
    /// <summary>
    /// The memory types an MTRR may hold: UC (0), WC (1), WT (4), WP (5) and
    /// WB (6). UC- (7) exists only as a PAT entry; in an MTRR it is reserved
    /// and #GP(0) like 2 and 3 (SDM vol. 3, MTRR memory types).
    /// </summary>
    private static bool IsValidMtrrType(byte type) => type is 0 or 1 or 4 or 5 or 6;

    /// <summary>The memory types a PAT entry may hold: the MTRR set plus UC- (7).</summary>
    private static bool IsValidPatType(byte type) => type is 0 or 1 or 4 or 5 or 6 or 7;

    private ulong _apicBase;
    private ulong _eblCrPoweron;
    private ulong _biosSignId;
    private ulong _perfCtr0;
    private ulong _perfCtr1;
    private ulong _sysenterCs;
    private ulong _sysenterEsp;
    private ulong _sysenterEip;
    private ulong _perfEvtSel0;
    private ulong _perfEvtSel1;
    private ulong _mcgStatus;
    private ulong _mcgCtl;
    private readonly ulong[] _mcBanks = new ulong[McBankCount * McRegistersPerBank];
    private ulong _debugCtlMsr;
    private uint _mtrrDefType;
    private readonly ulong[] _mtrrVariableBase = new ulong[MtrrVariableCount];
    private readonly ulong[] _mtrrVariableMask = new ulong[MtrrVariableCount];
    private readonly ulong[] _mtrrFixed = new ulong[1 + 2 + 8];   // 250h; 258h-259h; 268h-26Fh
    private ulong _pat = PatResetValue;

    /// <summary>PERFCTR0/1, masked the same way the P5's own counters are: RDPMC (Cpu.P6.cs) reads these directly by index.</summary>
    public ulong PerfCounter0
    {
        get => _perfCtr0;
        set => _perfCtr0 = value & CounterMask;
    }

    public ulong PerfCounter1
    {
        get => _perfCtr1;
        set => _perfCtr1 = value & CounterMask;
    }

    /// <summary>SYSENTER_CS, as SYSENTER/SYSEXIT read it (Cpu.P6.cs). Only the low 16 bits are architecturally meaningful; the rest are simply held.</summary>
    public ulong SysenterCs
    {
        get => _sysenterCs;
        set => _sysenterCs = value;
    }

    public ulong SysenterEsp
    {
        get => _sysenterEsp;
        set => _sysenterEsp = value;
    }

    public ulong SysenterEip
    {
        get => _sysenterEip;
        set => _sysenterEip = value;
    }

    /// <summary>
    /// The BIOS_SIGN_ID idiom's other half: called from every CPUID,
    /// whichever MSR set is active, because the microcode that implements
    /// this on real silicon does not check which family it is running on
    /// either -- it simply always updates the one MSR. See
    /// <see cref="BiosSignIdIndex"/>.
    /// </summary>
    public void NoteCpuIdExecuted()
        => _biosSignId = (_biosSignId & 0xFFFFFFFFUL) | ((ulong)BiosSignIdRevision << 32);

    /// <summary>
    /// Raised after a validated write to APIC_BASE, with the new raw value:
    /// <c>Devices.Apic.LocalApic</c> subscribes to move or unmap its MMIO
    /// page and to notice the hardware-enable bit changing. Also raised from
    /// <see cref="Reset"/>, so a CPU reset re-synchronises the device the
    /// same way a fresh machine's construction does.
    /// </summary>
    public event Action<ulong>? ApicBaseChanged;

    /// <summary>
    /// The raw APIC_BASE value, for a device that needs to read where the
    /// LAPIC belongs without going through the CPUID-gated <see
    /// cref="TryRead"/> -- <c>Devices.Apic.LocalApic</c> reads this once at
    /// construction, before it has subscribed to <see cref="ApicBaseChanged"/>.
    /// </summary>
    public ulong ApicBaseRaw => _apicBase;

    /// <summary>
    /// RDMSR. Returns false for any index this processor's own set does not
    /// carry, which is the caller's cue to raise <c>#GP(0)</c> rather than
    /// answer with an arbitrary value.
    /// </summary>
    public bool TryRead(uint index, out ulong value)
    {
        if (IsFamilySix)
        {
            return TryReadFamilySix(index, out value);
        }

        return TryReadP5(index, out value);
    }

    /// <summary>WRMSR. Same "false means #GP(0)" contract as <see cref="TryRead"/>.</summary>
    public bool TryWrite(uint index, ulong value)
    {
        if (IsFamilySix)
        {
            return TryWriteFamilySix(index, value);
        }

        return TryWriteP5(index, value);
    }

    /// <summary>
    /// APIC_BASE, family six only (see the constant's own summary): a 386,
    /// 486 or any P5 (Pentium, Pentium MMX) has never heard of this MSR and
    /// takes <c>#GP(0)</c> exactly like an unknown index.
    /// </summary>
    private bool TryReadApicBase(out ulong value)
    {
        if (!_cpu.State.Features.Has(CpuFeature.Apic))
        {
            value = 0;
            return false;
        }

        value = _apicBase;
        return true;
    }

    /// <summary>
    /// See <see cref="TryReadApicBase"/> for the feature gate. EXTD (x2APIC)
    /// is not modelled on any silicon here, so setting it is <c>#GP(0)</c>
    /// like any other reserved bit (SDM's own rule for a bit the processor
    /// does not implement); the address and enable bits round-trip. BSP
    /// (bit 8) is read-only (independent review, "Low" item): the bit is
    /// tolerated in the write -- real hardware does not fault over it -- but
    /// the stored value keeps whatever it already was, since Aurora models
    /// one CPU and nothing here ever needs a guest's write to move it.
    /// </summary>
    private bool TryWriteApicBase(ulong value)
    {
        if (!_cpu.State.Features.Has(CpuFeature.Apic)) return false;
        if ((value & ApicBaseExtdBit) != 0) return false;
        if ((value & ~(ApicBaseWritableMask | ApicBaseBspBit)) != 0) return false;

        _apicBase = (value & ApicBaseWritableMask) | (_apicBase & ApicBaseBspBit);
        ApicBaseChanged?.Invoke(_apicBase);
        return true;
    }

    // ---- the P5 set, exactly as it has always been ------------------------

    private bool TryReadP5(uint index, out ulong value)
    {
        switch (index)
        {
            case P5McAddr:
                value = McAddr;
                return true;

            case P5McType:
                value = McType;
                return true;

            case TimeStampCounter:
                value = _cpu.Tsc;
                return true;

            case CesrIndex:
                value = Cesr;
                return true;

            case Ctr0Index:
                value = Counter0;
                return true;

            case Ctr1Index:
                value = Counter1;
                return true;

            default:
                value = 0;
                return false;
        }
    }

    private bool TryWriteP5(uint index, ulong value)
    {
        switch (index)
        {
            case P5McAddr:
                McAddr = value;
                return true;

            case P5McType:
                McType = value;
                return true;

            case TimeStampCounter:
                // Verified (cpu-models.md §2): a P5 writes only the low 32
                // bits of the TSC through this MSR and clears the high 32,
                // unlike later cores that accept a full 64-bit write.
                _cpu.WriteTsc((uint)value);
                return true;

            case CesrIndex:
                Cesr = value;
                return true;

            case Ctr0Index:
                Counter0 = value;
                return true;

            case Ctr1Index:
                Counter1 = value;
                return true;

            default:
                return false;
        }
    }

    // ---- the family-six set, added by work package P3.1 --------------------

    private bool TryReadFamilySix(uint index, out ulong value)
    {
        if (index is >= McBank0Index and <= McHighestIndex && _cpu.State.Features.Has(CpuFeature.Mca))
        {
            value = _mcBanks[index - McBank0Index];
            return true;
        }

        if (index is >= MtrrPhysBase0Index and <= MtrrHighestVariableIndex
            && _cpu.State.Features.Has(CpuFeature.Mtrr))
        {
            int pairIndex = (int)(index - MtrrPhysBase0Index) / 2;
            bool isMask = ((index - MtrrPhysBase0Index) & 1) != 0;
            value = isMask ? _mtrrVariableMask[pairIndex] : _mtrrVariableBase[pairIndex];
            return true;
        }

        if (TryFixedMtrrSlot(index, out int fixedSlot) && _cpu.State.Features.Has(CpuFeature.Mtrr))
        {
            value = _mtrrFixed[fixedSlot];
            return true;
        }

        switch (index)
        {
            // The time-stamp counter is as readable here as on the Pentium:
            // it is the same counter RDTSC reads (SDM, P6 MSR table, 10h).
            case TimeStampCounter:
                value = _cpu.Tsc;
                return true;

            // The P6 keeps the Pentium's two machine-check address and type
            // registers at 0h and 1h (SDM, P6 MSR table).
            case P5McAddr:
                value = McAddr;
                return true;

            case P5McType:
                value = McType;
                return true;

            case PlatformIdIndex:
                value = 0;
                return true;

            case ApicBaseIndex:
                return TryReadApicBase(out value);

            case EblCrPoweronIndex:
                value = _eblCrPoweron;
                return true;

            case BiosUpdtTrigIndex:
                // Write-only (plans/specs/p6.md item 10).
                value = 0;
                return false;

            case BiosSignIdIndex:
                value = _biosSignId;
                return true;

            case PerfCtr0Index:
                value = PerfCounter0;
                return true;

            case PerfCtr1Index:
                value = PerfCounter1;
                return true;

            case SysenterCsIndex:
                value = SysenterCs;
                return true;

            case SysenterEspIndex:
                value = SysenterEsp;
                return true;

            case SysenterEipIndex:
                value = SysenterEip;
                return true;

            case PerfEvtSel0Index:
                value = _perfEvtSel0;
                return true;

            case PerfEvtSel1Index:
                value = _perfEvtSel1;
                return true;

            case McgCapIndex:
            {
                bool mcaAdvertised = _cpu.State.Features.Has(CpuFeature.Mca);
                value = mcaAdvertised ? McgCapValue : 0;
                return mcaAdvertised;
            }

            case McgStatusIndex:
            {
                bool mcaAdvertised = _cpu.State.Features.Has(CpuFeature.Mca);
                value = mcaAdvertised ? _mcgStatus : 0;
                return mcaAdvertised;
            }

            case McgCtlIndex:
            {
                bool mcaAdvertised = _cpu.State.Features.Has(CpuFeature.Mca);
                value = mcaAdvertised ? _mcgCtl : 0;
                return mcaAdvertised;
            }

            case DebugCtlMsrIndex:
                value = _debugCtlMsr;
                return true;

            case MtrrCapIndex:
            {
                bool mtrrAdvertised = _cpu.State.Features.Has(CpuFeature.Mtrr);
                value = mtrrAdvertised ? MtrrCapValue : 0;
                return mtrrAdvertised;
            }

            case MtrrDefTypeIndex:
            {
                bool mtrrAdvertised = _cpu.State.Features.Has(CpuFeature.Mtrr);
                value = mtrrAdvertised ? _mtrrDefType : 0;
                return mtrrAdvertised;
            }

            case PatIndex:
            {
                bool patAdvertised = _cpu.State.Features.Has(CpuFeature.Pat);
                value = patAdvertised ? _pat : 0;
                return patAdvertised;
            }

            default:
                value = 0;
                return false;
        }
    }

    /// <summary>
    /// What WRMSR stores in a P6 performance counter: only the low 32 bits are
    /// written and bits 39:32 copy bit 31 (SDM, P6 performance monitoring),
    /// so that a profiler can load a negative count and wait for the overflow.
    /// </summary>
    private static ulong SignExtendedCounterWrite(ulong value)
    {
        const ulong bit31 = 1UL << 31;
        const ulong bits39To32 = 0xFFUL << 32;

        ulong low = value & 0xFFFFFFFF;

        return (low & bit31) != 0 ? low | bits39To32 : low;
    }

    private bool TryWriteFamilySix(uint index, ulong value)
    {
        if (index is >= McBank0Index and <= McHighestIndex)
        {
            if (!_cpu.State.Features.Has(CpuFeature.Mca)) return false;
            _mcBanks[index - McBank0Index] = value;
            return true;
        }

        if (index is >= MtrrPhysBase0Index and <= MtrrHighestVariableIndex)
        {
            return TryWriteMtrrVariable(index, value);
        }

        if (TryFixedMtrrSlot(index, out int fixedSlot))
        {
            return TryWriteFixedMtrr(fixedSlot, value);
        }

        switch (index)
        {
            case PlatformIdIndex:
                return false;   // read-only

            case ApicBaseIndex:
                return TryWriteApicBase(value);

            case EblCrPoweronIndex:
                _eblCrPoweron = value;
                return true;

            case BiosUpdtTrigIndex:
                // Accepted and discarded: there is no microcode to load.
                return true;

            case BiosSignIdIndex:
                _biosSignId = value;
                return true;

            case P5McAddr:
                McAddr = value;
                return true;

            case P5McType:
                McType = value;
                return true;

            case PerfCtr0Index:
                PerfCounter0 = SignExtendedCounterWrite(value);
                return true;

            case PerfCtr1Index:
                PerfCounter1 = SignExtendedCounterWrite(value);
                return true;

            case SysenterCsIndex:
                SysenterCs = value;
                return true;

            case SysenterEspIndex:
                SysenterEsp = value;
                return true;

            case SysenterEipIndex:
                SysenterEip = value;
                return true;

            case PerfEvtSel0Index:
                _perfEvtSel0 = value;
                return true;

            case PerfEvtSel1Index:
                _perfEvtSel1 = value;
                return true;

            case McgCapIndex:
                return false;   // read-only

            case McgStatusIndex:
                if (!_cpu.State.Features.Has(CpuFeature.Mca)) return false;
                _mcgStatus = value;
                return true;

            case McgCtlIndex:
                if (!_cpu.State.Features.Has(CpuFeature.Mca)) return false;
                _mcgCtl = value;
                return true;

            case DebugCtlMsrIndex:
                _debugCtlMsr = value;
                return true;

            case MtrrCapIndex:
                return false;   // read-only

            case MtrrDefTypeIndex:
                return TryWriteMtrrDefType(value);

            case PatIndex:
                return TryWritePat(value);

            case TimeStampCounter:
                // Unlike the P5 rule (TryWriteP5), a family-six-or-later
                // processor accepts a full 64-bit write (SDM Vol.3 §17's
                // footnote; plans/specs/p6.md item 10).
                _cpu.WriteTscFull(value);
                return true;

            default:
                return false;
        }
    }

    private bool TryWriteMtrrVariable(uint index, ulong value)
    {
        if (!_cpu.State.Features.Has(CpuFeature.Mtrr)) return false;

        int pairIndex = (int)(index - MtrrPhysBase0Index) / 2;
        bool isMask = ((index - MtrrPhysBase0Index) & 1) != 0;

        if (isMask)
        {
            if ((value & ~MtrrPhysMaskWritableMask) != 0) return false;
            _mtrrVariableMask[pairIndex] = value;
            return true;
        }

        if ((value & ~MtrrPhysBaseWritableMask) != 0) return false;
        if (!IsValidMtrrType((byte)(value & MtrrPhysBaseTypeMask))) return false;
        _mtrrVariableBase[pairIndex] = value;
        return true;
    }

    /// <summary>
    /// Maps 250h, 258h-259h and 268h-26Fh onto <see cref="_mtrrFixed"/>'s
    /// eleven slots, in that order; every other index is not a fixed-range
    /// MTRR at all.
    /// </summary>
    private static bool TryFixedMtrrSlot(uint index, out int slot)
    {
        if (index == MtrrFix64KIndex) { slot = 0; return true; }
        if (index is >= MtrrFix16KFirstIndex and <= MtrrFix16KLastIndex)
        {
            slot = 1 + (int)(index - MtrrFix16KFirstIndex);
            return true;
        }

        if (index is >= MtrrFix4KFirstIndex and <= MtrrFix4KLastIndex)
        {
            slot = 3 + (int)(index - MtrrFix4KFirstIndex);
            return true;
        }

        slot = 0;
        return false;
    }

    private bool TryWriteFixedMtrr(int slot, ulong value)
    {
        if (!_cpu.State.Features.Has(CpuFeature.Mtrr)) return false;

        for (int byteIndex = 0; byteIndex < 8; byteIndex++)
        {
            byte type = (byte)(value >> (byteIndex * 8));
            if (!IsValidMtrrType(type)) return false;
        }

        _mtrrFixed[slot] = value;
        return true;
    }

    private bool TryWriteMtrrDefType(ulong value)
    {
        if (!_cpu.State.Features.Has(CpuFeature.Mtrr)) return false;
        if (value > uint.MaxValue) return false;

        uint word = (uint)value;
        if ((word & ~MtrrDefTypeWritableMask) != 0) return false;
        if (!IsValidMtrrType((byte)(word & MtrrDefTypeTypeMask))) return false;

        _mtrrDefType = word;
        return true;
    }

    private bool TryWritePat(ulong value)
    {
        if (!_cpu.State.Features.Has(CpuFeature.Pat)) return false;

        for (int byteIndex = 0; byteIndex < 8; byteIndex++)
        {
            byte type = (byte)(value >> (byteIndex * 8));
            if (!IsValidPatType(type)) return false;
        }

        _pat = value;
        return true;
    }

    // ---- machine snapshots --------------------------------------------------

    /// <summary>
    /// The size <see cref="WriteSnapshot"/> produces for a P5 model: five
    /// 64-bit registers. The TSC is not among them; it belongs to the
    /// processor's cycle count. Frozen -- a family-six model's own, larger
    /// payload is <see cref="FamilySixSnapshotLength"/>, which the CPU-level
    /// snapshot version has to select between (see the coordinator note on
    /// Cpu.SnapshotVersion).
    /// </summary>
    public const int SnapshotLength = 5 * 8;

    /// <summary>
    /// The size <see cref="WriteSnapshot"/> produces for a family-six model:
    /// APIC_BASE, EBL_CR_POWERON, BIOS_SIGN_ID, PERFCTR0/1, PERFEVTSEL0/1,
    /// the three SYSENTER MSRs, MCG_STATUS, MCG_CTL, the twenty MC0-4 bank
    /// registers, DEBUGCTLMSR, MTRRdefType, the sixteen variable-range
    /// base/mask registers, the eleven fixed-range registers and PAT -- 62
    /// 64-bit fields in total, listed in the order <see cref="WriteSnapshot"/>
    /// writes them.
    /// </summary>
    public const int FamilySixSnapshotLength = 62 * 8;

    /// <summary>
    /// The power-on and RESET values: the event select and both counters
    /// are zero (SDM vol. 3, table 9-1), and the machine-check registers,
    /// which record nothing here, are zero too. The family-six fields reset
    /// the same way, except APIC_BASE (<see cref="ApicBaseResetValue"/>) and
    /// PAT (<see cref="PatResetValue"/>), which have a cited non-zero
    /// power-on value.
    /// </summary>
    public void Reset()
    {
        McAddr = 0;
        McType = 0;
        Cesr = 0;
        Counter0 = 0;
        Counter1 = 0;

        _apicBase = ApicBaseResetValue;
        ApicBaseChanged?.Invoke(_apicBase);
        _eblCrPoweron = 0;
        _biosSignId = 0;
        PerfCounter0 = 0;
        PerfCounter1 = 0;
        SysenterCs = 0;
        SysenterEsp = 0;
        SysenterEip = 0;
        _perfEvtSel0 = 0;
        _perfEvtSel1 = 0;
        _mcgStatus = 0;
        _mcgCtl = 0;
        Array.Clear(_mcBanks);
        _debugCtlMsr = 0;
        _mtrrDefType = 0;
        Array.Clear(_mtrrVariableBase);
        Array.Clear(_mtrrVariableMask);
        Array.Clear(_mtrrFixed);
        _pat = PatResetValue;
    }

    /// <summary>
    /// The version of the snapshot section this set writes: 1 for the
    /// Pentium's layout (<see cref="SnapshotLength"/> bytes), 2 for family
    /// six (<see cref="FamilySixSnapshotLength"/> bytes). The machine
    /// snapshot stores it as the version of its "MSR " section, which keeps
    /// each version at a fixed length it can check before restoring.
    /// </summary>
    public int SnapshotVersion => IsFamilySix ? 2 : 1;

    /// <summary>
    /// Reads a section of either version. The registers are reset first, so
    /// a file taken on the other family leaves this model's set at its
    /// power-on values rather than half restored.
    /// </summary>
    public void ReadSnapshot(BinaryReader reader, int version)
    {
        Reset();

        if (version == 2)
        {
            ReadFamilySixSnapshot(reader);
            return;
        }

        McAddr = reader.ReadUInt64();
        McType = reader.ReadUInt64();
        Cesr = reader.ReadUInt64();
        Counter0 = reader.ReadUInt64();
        Counter1 = reader.ReadUInt64();
    }

    /// <summary>Writes the active set's stored registers for a machine snapshot; see <see cref="SnapshotVersion"/>.</summary>
    public void WriteSnapshot(BinaryWriter writer)
    {
        if (IsFamilySix)
        {
            WriteFamilySixSnapshot(writer);
            return;
        }

        writer.Write(McAddr);
        writer.Write(McType);
        writer.Write(Cesr);
        writer.Write(Counter0);
        writer.Write(Counter1);
    }

    private void WriteFamilySixSnapshot(BinaryWriter writer)
    {
        writer.Write(_apicBase);
        writer.Write(_eblCrPoweron);
        writer.Write(_biosSignId);
        writer.Write(_perfCtr0);
        writer.Write(_perfCtr1);
        writer.Write(_perfEvtSel0);
        writer.Write(_perfEvtSel1);
        writer.Write(_sysenterCs);
        writer.Write(_sysenterEsp);
        writer.Write(_sysenterEip);
        writer.Write(_mcgStatus);
        writer.Write(_mcgCtl);

        foreach (ulong bank in _mcBanks)
        {
            writer.Write(bank);
        }

        writer.Write(_debugCtlMsr);
        writer.Write((ulong)_mtrrDefType);

        foreach (ulong baseRegister in _mtrrVariableBase)
        {
            writer.Write(baseRegister);
        }

        foreach (ulong maskRegister in _mtrrVariableMask)
        {
            writer.Write(maskRegister);
        }

        foreach (ulong fixedRegister in _mtrrFixed)
        {
            writer.Write(fixedRegister);
        }

        writer.Write(_pat);
    }

    private void ReadFamilySixSnapshot(BinaryReader reader)
    {
        _apicBase = reader.ReadUInt64();
        _eblCrPoweron = reader.ReadUInt64();
        _biosSignId = reader.ReadUInt64();
        PerfCounter0 = reader.ReadUInt64();
        PerfCounter1 = reader.ReadUInt64();
        _perfEvtSel0 = reader.ReadUInt64();
        _perfEvtSel1 = reader.ReadUInt64();
        SysenterCs = reader.ReadUInt64();
        SysenterEsp = reader.ReadUInt64();
        SysenterEip = reader.ReadUInt64();
        _mcgStatus = reader.ReadUInt64();
        _mcgCtl = reader.ReadUInt64();

        for (int i = 0; i < _mcBanks.Length; i++)
        {
            _mcBanks[i] = reader.ReadUInt64();
        }

        _debugCtlMsr = reader.ReadUInt64();
        _mtrrDefType = (uint)reader.ReadUInt64();

        for (int i = 0; i < _mtrrVariableBase.Length; i++)
        {
            _mtrrVariableBase[i] = reader.ReadUInt64();
        }

        for (int i = 0; i < _mtrrVariableMask.Length; i++)
        {
            _mtrrVariableMask[i] = reader.ReadUInt64();
        }

        for (int i = 0; i < _mtrrFixed.Length; i++)
        {
            _mtrrFixed[i] = reader.ReadUInt64();
        }

        _pat = reader.ReadUInt64();
    }
}
