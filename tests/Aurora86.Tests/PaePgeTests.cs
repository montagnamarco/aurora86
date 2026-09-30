using Aurora86.Core;
using Aurora86.Core.Cpu;
using Aurora86.Core.Memory;

namespace Aurora86.Tests;

/// <summary>
/// Work package P3.1's paging half: CR4.PGE (Page Global Enable) and CR4.PAE
/// (Physical Address Extension), against plans/specs/p6.md items 5 and 6 and
/// the coordinator's decisions there -- notably that this emulator's memory
/// bus stays 32 bits wide, so a PAE translation whose physical address needs
/// bits 35:32 reads as an open bus (0xFF) rather than reaching real memory.
///
/// Group 1 exercises PGE against ordinary (non-PAE) two-level paging, since
/// PGE's own rule -- a global leaf entry survives a CR3 reload -- has nothing
/// to do with how many levels the walk has. <see cref="Cpu.FlushTlbForCr3Load"/>
/// stands in for the MOV-to-CR3 instruction handler and the task switch's own
/// CR3 load, the same way LargePageTests and PagingTests call
/// <c>Cpu.FlushTlb</c> directly instead of encoding an instruction, per that
/// method's own remarks in Cpu.Paging.cs.
///
/// Group 2 exercises the four PDPT registers CR4.PAE gives the processor,
/// through the real MOV CR3 (and MOV CR0) instructions -- their reload is an
/// instruction-level side effect (a reserved PDPTE bit is #GP(0) *at that
/// instruction*, not deferred to a later access), so it has to run through
/// the interpreter to be tested at all. Neither instruction needs paging
/// active to reach it, so there is no need to identity-map the code fetch.
///
/// Group 3 exercises PAE's own three-level walk: 4 KB and 2 MB pages, the
/// 64-bit PDE/PTE reserved-bit checks, A/D, permissions and the open-bus
/// rule -- built by hand and driven through <c>ReadMemoryByte</c>/
/// <c>WriteMemoryByte</c>, exactly as PagingTests and LargePageTests build
/// the legacy format's tables, with <see cref="Cpu.Pdpte"/> poked directly
/// in place of running MOV CR3 to load it (that loading path is Group 2's).
/// </summary>
public class PaePgeTests
{
    // === shared bit names =====================================================

    private const uint Present = 1u << 0;
    private const uint Writable = 1u << 1;
    private const uint User = 1u << 2;
    private const uint GlobalBit = 1u << 8;

    private const ulong PaeP = 1UL << 0;
    private const ulong PaeRw = 1UL << 1;
    private const ulong PaeUs = 1UL << 2;
    private const ulong PaeA = 1UL << 5;
    private const ulong PaeD = 1UL << 6;
    private const ulong PaePs = 1UL << 7;
    private const ulong PaeG = 1UL << 8;

    private static void WriteQword(PhysicalMemoryBus bus, uint address, ulong value)
    {
        bus.WriteDword(address, (uint)value);
        bus.WriteDword(address + 4, (uint)(value >> 32));
    }

    private static ulong ReadQword(PhysicalMemoryBus bus, uint address) =>
        bus.ReadDword(address) | ((ulong)bus.ReadDword(address + 4) << 32);

    // === Group 1: CR4.PGE, against ordinary two-level paging ==================

    private const uint DirectoryAddress = 0x20000;
    private const uint GlobalTableAddress = 0x21000;
    private const uint OrdinaryTableAddress = 0x22000;

    /// <summary>
    /// Two 4 KB pages in different directory slots, one PTE marked global
    /// (bit 8) and one not, with CR4.PGE already on.
    /// </summary>
    private static Machine TwoPageSetup(uint globalLinear, uint globalFrame,
                                         uint ordinaryLinear, uint ordinaryFrame)
    {
        var machine = new Machine();
        machine.CpuModel = CpuModel.Pentium;   // resets the CPU; must come first
        machine.RunInstructions(200);

        machine.Memory.WriteDword(DirectoryAddress + (globalLinear >> 22) * 4,
                                   GlobalTableAddress | Present | Writable | User);
        machine.Memory.WriteDword(GlobalTableAddress + ((globalLinear >> 12) & 0x3FF) * 4,
                                   (globalFrame & 0xFFFFF000) | Present | Writable | User | GlobalBit);

        machine.Memory.WriteDword(DirectoryAddress + (ordinaryLinear >> 22) * 4,
                                   OrdinaryTableAddress | Present | Writable | User);
        machine.Memory.WriteDword(OrdinaryTableAddress + ((ordinaryLinear >> 12) & 0x3FF) * 4,
                                   (ordinaryFrame & 0xFFFFF000) | Present | Writable | User);   // no global bit

        machine.Cpu.State.Cr3 = DirectoryAddress;
        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;
        machine.Cpu.State.Cr4 |= CpuState.Cr4Pge;
        machine.Cpu.State.Features = machine.Cpu.State.Features.With(CpuFeature.Pge, CpuFeature.Pae);

        machine.Cpu.State.Segments[(int)SegReg.Ds].Base = 0;
        machine.Cpu.State.Segments[(int)SegReg.Ds].Limit = 0xFFFFFFFF;

        return machine;
    }

    [Fact]
    public void ReloadingCr3WithPgeSetKeepsAGlobalEntryAndDropsAnOrdinaryOne()
    {
        const uint GlobalLinear = 0x00400000;
        // A different table index (bits 21:12), not just a different
        // directory slot: the direct-mapped TLB's 1024 slots exactly match
        // the 1024 table entries a directory slot has, so the slot a linear
        // address hashes to is its table index alone -- 0x00800000's is 0,
        // the same as 0x00400000's, which would make the second access below
        // evict the first from the very same slot instead of exercising two
        // independently cached entries.
        const uint OrdinaryLinear = 0x00800000 + 0x1000;
        const uint GlobalFrame = 0x00330000;
        const uint OrdinaryFrame = 0x00340000;
        const uint NewGlobalFrame = 0x00350000;
        const uint NewOrdinaryFrame = 0x00360000;

        using Machine machine = TwoPageSetup(GlobalLinear, GlobalFrame, OrdinaryLinear, OrdinaryFrame);

        // Cache both translations.
        machine.Cpu.WriteMemoryByte(SegReg.Ds, GlobalLinear, 0xAA);
        machine.Cpu.WriteMemoryByte(SegReg.Ds, OrdinaryLinear, 0xBB);

        // Remap both pages elsewhere, then reload CR3 the way MOV CR3 (and a
        // task switch's CR3 load) does with PGE set.
        machine.Memory.WriteDword(GlobalTableAddress + ((GlobalLinear >> 12) & 0x3FF) * 4,
                                   NewGlobalFrame | Present | Writable | User | GlobalBit);
        machine.Memory.WriteDword(OrdinaryTableAddress + ((OrdinaryLinear >> 12) & 0x3FF) * 4,
                                   NewOrdinaryFrame | Present | Writable | User);
        machine.Cpu.FlushTlbForCr3Load();

        // The global entry survived: the next access still uses the OLD,
        // cached translation -- stale, but exactly what PGE is for.
        machine.Cpu.WriteMemoryByte(SegReg.Ds, GlobalLinear, 0xCC);
        Assert.Equal(0xCC, machine.Memory.ReadByte(GlobalFrame));
        Assert.NotEqual(0xCC, machine.Memory.ReadByte(NewGlobalFrame));

        // The ordinary entry did not: the next access walks the tables again.
        machine.Cpu.WriteMemoryByte(SegReg.Ds, OrdinaryLinear, 0xDD);
        Assert.Equal(0xDD, machine.Memory.ReadByte(NewOrdinaryFrame));
        Assert.NotEqual(0xDD, machine.Memory.ReadByte(OrdinaryFrame));
    }

    [Fact]
    public void InvlpgDropsAGlobalEntryEvenThoughACr3ReloadWouldHaveKeptIt()
    {
        const uint Linear = 0x00400000;
        const uint FirstFrame = 0x00330000;
        const uint SecondFrame = 0x00350000;

        using Machine machine = TwoPageSetup(Linear, FirstFrame, 0x00800000, 0x00340000);

        machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear, 0xAA);
        Assert.Equal(0xAA, machine.Memory.ReadByte(FirstFrame));

        machine.Memory.WriteDword(GlobalTableAddress + ((Linear >> 12) & 0x3FF) * 4,
                                   SecondFrame | Present | Writable | User | GlobalBit);
        machine.Cpu.InvalidatePage(Linear);

        // INVLPG does not consult G at all: the entry is gone regardless.
        machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear, 0xBB);
        Assert.Equal(0xBB, machine.Memory.ReadByte(SecondFrame));
        Assert.NotEqual(0xBB, machine.Memory.ReadByte(FirstFrame));
    }

    [Fact]
    public void TheUnconditionalFlushCr0AndCr4ChangesUseDropsAGlobalEntryToo()
    {
        // CR0.PG and CR4.PSE/PAE/PGE changing still call the plain,
        // unconditional FlushTlb (Cpu.TwoByte.cs) -- unlike a CR3 reload,
        // that flush was never taught about G at all, so a global entry
        // cannot survive it either.
        const uint Linear = 0x00400000;
        const uint FirstFrame = 0x00330000;
        const uint SecondFrame = 0x00350000;

        using Machine machine = TwoPageSetup(Linear, FirstFrame, 0x00800000, 0x00340000);

        machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear, 0xAA);
        machine.Memory.WriteDword(GlobalTableAddress + ((Linear >> 12) & 0x3FF) * 4,
                                   SecondFrame | Present | Writable | User | GlobalBit);
        machine.Cpu.FlushTlb();

        machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear, 0xBB);
        Assert.Equal(0xBB, machine.Memory.ReadByte(SecondFrame));
        Assert.NotEqual(0xBB, machine.Memory.ReadByte(FirstFrame));
    }

    // === Group 2: the four PDPT registers, through MOV CR3 / MOV CR0 =========

    private const uint CodeBase = 0x1000;

    /// <summary>A bare CPU in real mode, with paging off. See CpuFeatureTests.Setup, which this mirrors.</summary>
    private static (Cpu cpu, PhysicalMemoryBus bus) Setup(params byte[] code)
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus);
        foreach (SegReg s in Enum.GetValues<SegReg>())
            cpu.State.LoadSegmentRealMode(s, 0);
        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;
        code.CopyTo(bus.GetRamSpan(CodeBase, code.Length));

        cpu.State.SelectModel(CpuModel.Pentium);
        cpu.State.Features = cpu.State.Features.With(CpuFeature.Pae, CpuFeature.Pge);

        return (cpu, bus);
    }

    /// <summary>The vector and error code of the first exception a step raises. See PentiumInstructionTests.FaultVector, which this extends with the error code.</summary>
    private static (byte Vector, uint ErrorCode) Fault(Cpu cpu, Action step)
    {
        byte vector = 0xFF;
        uint errorCode = 0;
        cpu.ProcessorException += (v, ec, _, _) => { if (vector == 0xFF) { vector = v; errorCode = ec; } };
        step();
        return (vector, errorCode);
    }

    private const uint PdptAddress = 0x3000;

    /// <summary>
    /// The page a bare <see cref="Setup"/> CPU's real-mode interrupt delivery
    /// actually writes to: SS.Big is false for a real-mode-loaded segment
    /// (<c>LoadSegmentRealMode</c>), so PUSH16 uses the 16-bit SP alias, and
    /// FLAGS/CS/IP land at <c>Esp-2</c>, <c>Esp-4</c>, <c>Esp-6</c> -- one
    /// page *below* Setup's own <c>Esp = 0x8000</c>, not the page Esp itself
    /// starts in.
    /// </summary>
    private const uint StackPageLinear = 0x7000;

    /// <summary>
    /// Identity-maps <see cref="CodeBase"/>'s own page, and
    /// <see cref="StackPageLinear"/>'s, directly through the Pdpte registers
    /// -- never through <c>LoadPdptRegisters</c>, which is the very thing
    /// several of these tests exercise -- so the fetch of the MOV CR3/CR0/CR4
    /// instruction under test succeeds once CR0.PG and CR4.PAE are both
    /// already set before it runs (fix D1: PAE paging is active, and MOV CR3
    /// loads PDPTEs at all, only once CR0.PG and CR4.PAE are both set), and
    /// so does delivering whatever fault that instruction itself may then
    /// raise: real-mode delivery still pushes three words onto this bare
    /// harness's stack, and an unmapped stack page would turn the fault this
    /// test is checking for into a page fault instead, then a double fault,
    /// then a reset -- which clobbers CR0/CR3/CR4 wholesale and makes every
    /// assertion about them meaningless. Reuses <see cref="PdAddress"/> and
    /// <see cref="PtAddress"/>, Group 3's own PAE table addresses -- there is
    /// no conflict, since Group 2's tests never build Group 3's own tables.
    /// </summary>
    private static void IdentityMapCodePage(Cpu cpu, PhysicalMemoryBus bus)
    {
        WriteQword(bus, PtAddress + ((CodeBase >> 12) & 0x1FF) * 8, (CodeBase & 0xFFFFF000) | PaeP);
        WriteQword(bus, PtAddress + ((StackPageLinear >> 12) & 0x1FF) * 8, (StackPageLinear & 0xFFFFF000) | PaeP | PaeRw);
        WriteQword(bus, PdAddress + ((CodeBase >> 21) & 0x1FF) * 8, PtAddress | PaeP);
        cpu.Pdpte[0] = PdAddress | PaeP;
    }

    [Fact]
    public void MovCr3LoadsTheFourPdptRegistersWhenPaeIsSet()
    {
        var (cpu, bus) = Setup(0x0F, 0x22, 0xD8);   // mov cr3, eax
        using (bus)
        {
            IdentityMapCodePage(cpu, bus);
            cpu.State.Cr4 |= CpuState.Cr4Pae;
            cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;

            ulong[] entries = new ulong[4];
            for (int i = 0; i < 4; i++)
            {
                entries[i] = (0x00123000u + (uint)(i * 0x1000)) | PaeP;
                WriteQword(bus, PdptAddress + (uint)(i * 8), entries[i]);
            }

            cpu.State.Eax = PdptAddress;
            cpu.Step();

            Assert.Equal(PdptAddress, cpu.State.Cr3);
            for (int i = 0; i < 4; i++) Assert.Equal(entries[i], cpu.Pdpte[i]);
        }
    }

    [Fact]
    public void MovCr3FaultsWithGpZeroOnAReservedPdpteBitAndLeavesCr3Unchanged()
    {
        var (cpu, bus) = Setup(0x0F, 0x22, 0xD8);   // mov cr3, eax
        using (bus)
        {
            IdentityMapCodePage(cpu, bus);
            cpu.State.Cr4 |= CpuState.Cr4Pae;
            // Paging only, not protection: the point under test is D1's own
            // rule (CR0.PG = 1 and CR4.PAE = 1), and staying in real mode
            // keeps the fault this test expects deliverable through the
            // plain real-mode IVT rather than needing a GDT/IDT this bare
            // harness never sets up.
            cpu.State.Cr0 |= CpuState.Cr0Paging;
            cpu.State.Cr3 = 0x00099000;   // the "old" CR3, well clear of PdptAddress

            WriteQword(bus, PdptAddress + 0, 0x00123000u | PaeP);
            // Present, with bit 5 -- one of the reserved 8:5 -- also set.
            WriteQword(bus, PdptAddress + 8, (0x00124000u | PaeP) | (1UL << 5));
            WriteQword(bus, PdptAddress + 16, 0);
            WriteQword(bus, PdptAddress + 24, 0);

            cpu.State.Eax = PdptAddress;
            (byte vector, uint errorCode) = Fault(cpu, () => cpu.Step());

            Assert.Equal(13, vector);
            Assert.Equal(0u, errorCode);
            Assert.Equal(0x00099000u, cpu.State.Cr3);
        }
    }

    [Fact]
    public void MovCr3StoresANotPresentPdpteUncheckedAndStillUpdatesCr3()
    {
        var (cpu, bus) = Setup(0x0F, 0x22, 0xD8);   // mov cr3, eax
        using (bus)
        {
            IdentityMapCodePage(cpu, bus);
            cpu.State.Cr4 |= CpuState.Cr4Pae;
            cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;

            // Not present (bit 0 clear), but garbage reserved bits set --
            // must not fault, and must be stored exactly as written: it only
            // faults if a linear address that would use it is translated.
            ulong notPresentGarbage = (1UL << 5) | (1UL << 40);
            WriteQword(bus, PdptAddress + 0, notPresentGarbage);
            WriteQword(bus, PdptAddress + 8, 0);
            WriteQword(bus, PdptAddress + 16, 0);
            WriteQword(bus, PdptAddress + 24, 0);

            cpu.State.Eax = PdptAddress;
            cpu.Step();

            Assert.Equal(PdptAddress, cpu.State.Cr3);
            Assert.Equal(notPresentGarbage, cpu.Pdpte[0]);
        }
    }

    [Fact]
    public void MovCr3WithPaeSetButPagingOffJustStoresCr3EvenOverAMalformedPdpte()
    {
        // Fix D1: MOV CR3 loads PDPTEs (and may #GP) only once PAE paging is
        // actually active -- CR0.PG = 1 *and* CR4.PAE = 1. With PG = 0 there
        // is no paging at all, PAE included, so CR3 is simply stored,
        // whatever CR4.PAE says and whatever memory happens to hold at the
        // address it names.
        var (cpu, bus) = Setup(0x0F, 0x22, 0xD8);   // mov cr3, eax
        using (bus)
        {
            cpu.State.Cr4 |= CpuState.Cr4Pae;   // PAE is set...
            // ...but CR0.PG is not: Setup()'s CPU is real mode, paging off.

            // The IVT's own first dword (real mode's interrupt 0 vector):
            // bit 1 of it -- one of a PDPTE's own reserved bits 2:1 -- is
            // set, which would be #GP(0) if this were ever read as a PDPTE.
            // It never is.
            bus.WriteDword(0, 0xF000FF53);

            cpu.State.Eax = 0;   // CR3 = 0: the PDPT "would" be read from address 0
            cpu.Step();

            Assert.Equal(0u, cpu.State.Cr3);
            for (int i = 0; i < 4; i++) Assert.Equal(0ul, cpu.Pdpte[i]);   // never touched
        }
    }

    [Fact]
    public void Cr0PagingZeroToOneWithPaeAlreadySetReloadsThePdptRegisters()
    {
        // The fetch of this very instruction happens before CR0.PG takes
        // effect (paging is still off while it is being read), so no
        // identity-mapped code page is needed to reach this test at all.
        var (cpu, bus) = Setup(0x0F, 0x22, 0xC0);   // mov cr0, eax
        using (bus)
        {
            cpu.State.Cr4 |= CpuState.Cr4Pae;
            cpu.State.Cr3 = PdptAddress;
            // Fix U5: PG = 1 without PE = 1 is #GP(0). Set PE directly first
            // so the write under test is only about PG.
            cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable;

            ulong entry = 0x00321000u | PaeP;
            WriteQword(bus, PdptAddress + 0, entry);
            WriteQword(bus, PdptAddress + 8, 0);
            WriteQword(bus, PdptAddress + 16, 0);
            WriteQword(bus, PdptAddress + 24, 0);

            cpu.State.Eax = cpu.State.Cr0 | CpuState.Cr0Paging;
            cpu.Step();

            Assert.NotEqual(0u, cpu.State.Cr0 & CpuState.Cr0Paging);
            Assert.Equal(entry, cpu.Pdpte[0]);
        }
    }

    [Fact]
    public void MovCr0SettingPagingWithoutProtectionFaultsAndLeavesCr0Unchanged()
    {
        // Fix U5 (SDM "MOV--Move to/from Control Registers"): PG = 1 while
        // PE is (or would be left) 0 is #GP(0), CR0 exactly as it was.
        var (cpu, bus) = Setup(0x0F, 0x22, 0xC0);   // mov cr0, eax
        using (bus)
        {
            uint originalCr0 = cpu.State.Cr0;
            cpu.State.Eax = originalCr0 | CpuState.Cr0Paging;   // PG alone, PE still clear

            byte vector = Fault(cpu, () => cpu.Step()).Vector;

            Assert.Equal(13, vector);
            Assert.Equal(originalCr0, cpu.State.Cr0);
        }
    }

    [Fact]
    public void MovCr4PaeZeroToOneWithPagingAlreadyOnReloadsThePdptRegistersThroughARealInstruction()
    {
        // Fix U1 (the SDM's own narrow rule, exercised through a real
        // instruction this time instead of Cpu.Paging.cs's helper): CR4.PAE's
        // 0-to-1 transition, with CR0.PG already set, reloads the four PDPT
        // registers from the current CR3.
        var (cpu, bus) = Setup(0x0F, 0x22, 0xE0);   // mov cr4, eax
        using (bus)
        {
            // A legacy (non-PAE) identity map for the code page: PAE is
            // still 0 while this very instruction is fetched, so the fetch
            // itself walks the ordinary two-level format.
            //
            // Directory index 8 (linear 32 MB), not 0 (linear 0): the fetch
            // marks whichever directory entry it uses Accessed (bit 5) --
            // and CR3's own low 32 bytes are about to be reinterpreted as
            // the four PDPT registers once PAE turns on, where bit 5 is one
            // of a PDPTE's own reserved bits. Directory index 0 or 1 would
            // land the Accessed write inside PDPTE 0 itself and turn this
            // test's own fetch into a spurious #GP(0) -- a hazard specific
            // to reusing one CR3 across the two formats, which real
            // software avoids by loading a fresh, PAE-shaped CR3 instead of
            // reinterpreting an old legacy directory's bytes. Index 8 sits
            // well outside the 4 PDPTEs' 32-byte footprint (indices 0-7), so
            // marking it Accessed touches nothing this test reads back.
            const uint FetchLinear = 8u << 22;
            bus.WriteDword(PdAddress + 8 * 4, PtAddress | 1);
            bus.WriteDword(PtAddress + ((FetchLinear >> 12) & 0x3FF) * 4, CodeBase | 1);   // -> physical CodeBase, where Setup() put the bytes

            // Untouched by the fetch above (which only ever marks directory
            // index 8 Accessed): a legitimate PDPTE 0 the reload should pick
            // up unchanged.
            ulong prewrittenPdpte0 = 0x00123000u | PaeP;
            WriteQword(bus, PdAddress + 0, prewrittenPdpte0);   // untouched by the fetch: see above

            cpu.State.Cr3 = PdAddress;
            cpu.State.Cr0 |= CpuState.Cr0Paging;   // paging only -- see the D1-fix tests' own remark
            // A real-mode segment's own limit (0xFFFF) would reject an EIP
            // this far out on its own terms, well before paging enters into
            // it at all -- raised here, directly, the same way Group 1 and
            // Group 3's own setups widen Ds's limit for a flat 32-bit access.
            cpu.State.Segments[(int)SegReg.Cs].Limit = 0xFFFFFFFF;
            cpu.State.Eip = FetchLinear;

            cpu.State.Eax = CpuState.Cr4Pae;
            cpu.Step();

            Assert.Equal(FetchLinear + 3, cpu.State.Eip);   // the instruction actually executed, no fault
            Assert.NotEqual(0u, cpu.State.Cr4 & CpuState.Cr4Pae);
            Assert.Equal(prewrittenPdpte0, cpu.Pdpte[0]);
        }
    }

    [Fact]
    public void TogglingCr4PgeUnderPaeReloadsThePdptRegistersAndFaultsOnABadOne()
    {
        // Fix U1 (the wider rule): with PAE paging already active, a CR4
        // write that changes PGE -- not PAE itself -- still reloads the
        // PDPT registers, and a bad one is #GP(0) with CR4 left unchanged.
        var (cpu, bus) = Setup(0x0F, 0x22, 0xE0);   // mov cr4, eax
        using (bus)
        {
            IdentityMapCodePage(cpu, bus);

            cpu.State.Cr4 = CpuState.Cr4Pae;
            cpu.State.Cr3 = PdptAddress;
            cpu.State.Cr0 |= CpuState.Cr0Paging;   // paging only -- see the D1-fix tests' own remark

            // The reload re-reads from CR3: present, with bit 5 -- one of
            // the reserved 8:5 -- also set.
            WriteQword(bus, PdptAddress + 0, (0x00099000u | PaeP) | (1UL << 5));
            WriteQword(bus, PdptAddress + 8, 0);
            WriteQword(bus, PdptAddress + 16, 0);
            WriteQword(bus, PdptAddress + 24, 0);

            cpu.State.Eax = CpuState.Cr4Pae | CpuState.Cr4Pge;
            (byte vector, uint errorCode) = Fault(cpu, () => cpu.Step());

            Assert.Equal(13, vector);
            Assert.Equal(0u, errorCode);
            Assert.Equal(CpuState.Cr4Pae, cpu.State.Cr4);
        }
    }

    [Fact]
    public void ABadPdpteOnAWriteCr0ThatTogglesCacheDisableFaultsAndLeavesCr0Unchanged()
    {
        // Fix U1's wider rule again, this time from CR0's side: CD (bit 30)
        // is not PG, but it changes what a cached translation's write
        // protection means exactly as PG and WP already do, so it reloads
        // the PDPT registers too while PAE paging is active -- and a bad
        // one is #GP(0) with CR0 left unchanged (fix U1's third test: "a bad
        // PDPTE leaves CR0/CR4 unchanged").
        var (cpu, bus) = Setup(0x0F, 0x22, 0xC0);   // mov cr0, eax
        using (bus)
        {
            IdentityMapCodePage(cpu, bus);

            cpu.State.Cr4 |= CpuState.Cr4Pae;
            cpu.State.Cr3 = PdptAddress;
            cpu.State.Cr0 |= CpuState.Cr0Paging;   // paging only -- see the D1-fix tests' own remark
            uint originalCr0 = cpu.State.Cr0;

            WriteQword(bus, PdptAddress + 0, (0x00099000u | PaeP) | (1UL << 5));
            WriteQword(bus, PdptAddress + 8, 0);
            WriteQword(bus, PdptAddress + 16, 0);
            WriteQword(bus, PdptAddress + 24, 0);

            // PE is added only to the *attempted* write (satisfying fix U5's
            // own PG-without-PE check, which looks at the value being
            // written): the current, committed CR0 stays PE-clear, so if the
            // reload below faults as expected, the CPU is still in real
            // mode -- and its plain IVT, not a GDT/IDT this bare harness
            // never builds -- when the fault is delivered.
            cpu.State.Eax = originalCr0 | CpuState.Cr0CacheDisable | CpuState.Cr0ProtectionEnable;
            (byte vector, uint errorCode) = Fault(cpu, () => cpu.Step());

            Assert.Equal(13, vector);
            Assert.Equal(0u, errorCode);
            Assert.Equal(originalCr0, cpu.State.Cr0);
        }
    }

    [Fact]
    public void TracingUnderPaeReadsThroughTheThreeLevelWalkWithoutTouchingItsAOrDBits()
    {
        // Fix D2: TraceByte must go through PeekLinear (which dispatches to
        // PeekLinearPae under CR4.PAE), not TranslatePaged directly -- side-
        // effect free, and not blind to PAE's own three levels.
        //
        // Page A ends with a one-byte NOP; the real fetch touches only that
        // page. The trace's own 8-byte peek (RecordForTrace/TraceByte)
        // spills over into page B, which the real fetch never reaches at
        // all -- proving the peek walked page B through PAE (rather than
        // faulting, or worse, misreading CR3 as a legacy directory) and did
        // so without marking it accessed.
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus);
        using (bus)
        {
            const uint PageALinear = 0x00400000;
            const uint NopLinear = PageALinear + 0xFFF;      // the last byte of page A
            const uint PageBLinear = PageALinear + 0x1000;   // only the trace's peek reaches here
            const uint FrameA = 0x00330000;
            const uint FrameB = 0x00340000;

            WriteQword(bus, PdAddress + ((PageALinear >> 21) & 0x1FF) * 8, PtAddress | PaeP | PaeRw | PaeUs);
            WriteQword(bus, PtAddress + ((PageALinear >> 12) & 0x1FF) * 8, FrameA | PaeP | PaeRw | PaeUs);
            WriteQword(bus, PtAddress + ((PageBLinear >> 12) & 0x1FF) * 8, FrameB | PaeP | PaeRw | PaeUs);
            bus.WriteByte(FrameA + 0xFFF, 0x90);   // NOP: the only byte the real fetch needs

            cpu.State.SelectModel(CpuModel.Pentium);
            cpu.State.Features = cpu.State.Features.With(CpuFeature.Pae, CpuFeature.Pge);
            cpu.Pdpte[0] = PdAddress | PaeP;
            cpu.State.Cr4 |= CpuState.Cr4Pae;
            cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;

            cpu.State.Segments[(int)SegReg.Cs].Base = 0;
            cpu.State.Segments[(int)SegReg.Cs].Limit = 0xFFFFFFFF;
            cpu.State.Segments[(int)SegReg.Cs].Big = true;
            cpu.State.Eip = NopLinear;

            cpu.Trace = new Aurora86.Core.Diagnostics.InstructionTrace { Enabled = true };

            cpu.Step();

            Assert.Equal(NopLinear + 1, cpu.State.Eip);   // the real fetch executed the NOP

            uint pteAAddress = PtAddress + ((PageALinear >> 12) & 0x1FF) * 8;
            uint pteBAddress = PtAddress + ((PageBLinear >> 12) & 0x1FF) * 8;
            Assert.Equal(PaeA, ReadQword(bus, pteAAddress) & PaeA);   // page A: touched by the real fetch
            Assert.Equal(0ul, ReadQword(bus, pteBAddress) & PaeA);    // page B: the trace's own peek reached here, harmlessly
        }
    }

    // === Group 3: PAE's own three-level walk ==================================

    private const uint PdAddress = 0x30000;   // one page directory: 512 * 8 bytes
    private const uint PtAddress = 0x31000;   // one page table: 512 * 8 bytes

    /// <summary>
    /// A machine with CR4.PAE on and one 4 KB page mapped through all three
    /// levels: linear <paramref name="linear"/> (assumed to fall inside
    /// PDPTE 0, i.e. below 1 GB) lands on <paramref name="frame"/>.
    /// <see cref="Cpu.Pdpte"/> is poked directly rather than loaded through
    /// MOV CR3 -- that loading path is Group 2's own concern.
    /// </summary>
    private static Machine PagedPae(uint linear, ulong frame,
                                     ulong pdeFlags = PaeP | PaeRw | PaeUs,
                                     ulong pteFlags = PaeP | PaeRw | PaeUs)
    {
        var machine = new Machine();
        machine.CpuModel = CpuModel.Pentium;
        machine.RunInstructions(200);

        machine.Cpu.Pdpte[0] = PdAddress | PaeP;
        WriteQword(machine.Memory, PdAddress + ((linear >> 21) & 0x1FF) * 8, PtAddress | pdeFlags);
        WriteQword(machine.Memory, PtAddress + ((linear >> 12) & 0x1FF) * 8, frame | pteFlags);

        machine.Cpu.State.Cr4 |= CpuState.Cr4Pae;
        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;
        machine.Cpu.State.Features = machine.Cpu.State.Features.With(CpuFeature.Pae, CpuFeature.Pge);

        machine.Cpu.State.Segments[(int)SegReg.Ds].Base = 0;
        machine.Cpu.State.Segments[(int)SegReg.Ds].Limit = 0xFFFFFFFF;

        return machine;
    }

    /// <summary>The same, but PDE.PS = 1: <paramref name="linear"/>'s whole 2 MB page maps directly to <paramref name="frame"/>, no page table at all.</summary>
    private static Machine PagedPae2Mb(uint linear, ulong frame,
                                        ulong pdeFlags = PaeP | PaeRw | PaeUs | PaePs)
    {
        var machine = new Machine();
        machine.CpuModel = CpuModel.Pentium;
        machine.RunInstructions(200);

        machine.Cpu.Pdpte[0] = PdAddress | PaeP;
        WriteQword(machine.Memory, PdAddress + ((linear >> 21) & 0x1FF) * 8, frame | pdeFlags);

        machine.Cpu.State.Cr4 |= CpuState.Cr4Pae;
        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;
        machine.Cpu.State.Features = machine.Cpu.State.Features.With(CpuFeature.Pae, CpuFeature.Pge);

        machine.Cpu.State.Segments[(int)SegReg.Ds].Base = 0;
        machine.Cpu.State.Segments[(int)SegReg.Ds].Limit = 0xFFFFFFFF;

        return machine;
    }

    [Fact]
    public void A4KPaeTranslationReachesTheRightPhysicalBytes()
    {
        const uint Linear = 0x00400000;
        const ulong Frame = 0x00330000;

        using Machine machine = PagedPae(Linear, Frame);

        machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear + 0x24, 0x5A);
        Assert.Equal(0x5A, machine.Memory.ReadByte((uint)Frame + 0x24));
    }

    [Fact]
    public void A2MbPaeTranslationReachesTheRightPhysicalBytes()
    {
        const uint Linear = 0x00400000;
        const ulong Frame = 0x00600000;   // 2 MB-aligned

        using Machine machine = PagedPae2Mb(Linear, Frame);

        machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear + 0x123456, 0x77);
        Assert.Equal(0x77, machine.Memory.ReadByte((uint)Frame + 0x123456));
    }

    [Fact]
    public void AccessAndWriteSetAccessedAndDirtyInTheSixtyFourBitPte()
    {
        const uint Linear = 0x00400000;
        const ulong Frame = 0x00330000;
        uint pteAddress = PtAddress + ((Linear >> 12) & 0x1FF) * 8;

        using Machine machine = PagedPae(Linear, Frame);

        Assert.Equal(0ul, ReadQword(machine.Memory, pteAddress) & PaeA);

        machine.Cpu.ReadMemoryByte(SegReg.Ds, Linear);
        Assert.Equal(PaeA, ReadQword(machine.Memory, pteAddress) & PaeA);
        Assert.Equal(0ul, ReadQword(machine.Memory, pteAddress) & PaeD);

        // Second access hits the TLB; the dirty write still lands (the same
        // lazy-dirty machinery TranslatePaged's own hit path uses).
        machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear, 0x11);
        Assert.Equal(PaeD, ReadQword(machine.Memory, pteAddress) & PaeD);

        // The high dword -- the address extension and the reserved bits --
        // is untouched by either update.
        Assert.Equal(0u, (uint)(ReadQword(machine.Memory, pteAddress) >> 32));
    }

    [Fact]
    public void AWriteThroughATwoMegabytePaePageSetsAccessedAndDirtyInThePde()
    {
        const uint Linear = 0x00400000;
        const ulong Frame = 0x00600000;
        uint pdeAddress = PdAddress + ((Linear >> 21) & 0x1FF) * 8;

        using Machine machine = PagedPae2Mb(Linear, Frame);

        Assert.Equal(0ul, ReadQword(machine.Memory, pdeAddress) & (PaeA | PaeD));

        machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear, 0x11);

        Assert.Equal(PaeA | PaeD, ReadQword(machine.Memory, pdeAddress) & (PaeA | PaeD));
    }

    [Fact]
    public void AUserWriteToAReadOnlyPaePageFaultsWithTheRightErrorCode()
    {
        const uint Linear = 0x00400000;
        const ulong Frame = 0x00330000;

        using Machine machine = PagedPae(Linear, Frame, pteFlags: PaeP | PaeUs);   // no R/W
        machine.Cpu.State.Cpl = 3;

        var fault = Assert.Throws<PageFaultException>(
            () => machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear, 0x33));

        Assert.True(fault.Present);
        Assert.True(fault.Write);
        Assert.True(fault.User);
        Assert.Equal(1u | 2u | 4u, fault.ErrorCode);
    }

    [Fact]
    public void ANotPresentPdpteFaultsAsNotPresentOnUse()
    {
        var machine = new Machine();
        machine.CpuModel = CpuModel.Pentium;
        machine.RunInstructions(200);

        // Pdpte[0] is left at its reset value of zero: not present.
        machine.Cpu.State.Cr4 |= CpuState.Cr4Pae;
        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;
        machine.Cpu.State.Segments[(int)SegReg.Ds].Base = 0;
        machine.Cpu.State.Segments[(int)SegReg.Ds].Limit = 0xFFFFFFFF;

        using (machine)
        {
            var fault = Assert.Throws<PageFaultException>(
                () => machine.Cpu.ReadMemoryByte(SegReg.Ds, 0x1000));

            Assert.False(fault.Present);
            Assert.Equal(0x1000u, fault.LinearAddress);
        }
    }

    [Fact]
    public void ReservedHighBitsInAPaePteFaultWithTheReservedBitErrorCode()
    {
        const uint Linear = 0x00400000;
        const ulong Frame = 0x00330000;
        uint pteAddress = PtAddress + ((Linear >> 12) & 0x1FF) * 8;

        using Machine machine = PagedPae(Linear, Frame);

        ulong original = ReadQword(machine.Memory, pteAddress);
        WriteQword(machine.Memory, pteAddress, original | (1UL << 40));   // inside bits 63:36

        var fault = Assert.Throws<PageFaultException>(
            () => machine.Cpu.ReadMemoryByte(SegReg.Ds, Linear));

        Assert.True(fault.Present);
        Assert.True(fault.Reserved);
        Assert.Equal(Linear, fault.LinearAddress);
        Assert.Equal(1u | 8u, fault.ErrorCode);
    }

    [Fact]
    public void ReservedBitsInATwoMegabytePaePdeFaultWithTheReservedBitErrorCode()
    {
        const uint Linear = 0x00400000;
        const ulong Frame = 0x00600000;

        using Machine machine = PagedPae2Mb(Linear, Frame,
            pdeFlags: PaeP | PaeRw | PaeUs | PaePs | (1UL << 13));   // inside bits 20:13

        var fault = Assert.Throws<PageFaultException>(
            () => machine.Cpu.ReadMemoryByte(SegReg.Ds, Linear));

        Assert.True(fault.Present);
        Assert.True(fault.Reserved);
        Assert.Equal(1u | 8u, fault.ErrorCode);
    }

    [Fact]
    public void APaePteWithThePatBitSetFaultsRsvdOnAProcessorWithoutPat()
    {
        // Fix U3: without CpuFeature.Pat, PTE bit 7 -- the PAT index bit
        // where PAT exists -- is reserved, mirroring LargePageReservedMask's
        // legacy-format equivalent. PagedPae's own Features.With(Pae, Pge)
        // never adds Pat, so this processor has none.
        const uint Linear = 0x00400000;
        const ulong Frame = 0x00330000;

        using Machine machine = PagedPae(Linear, Frame, pteFlags: PaeP | PaeRw | PaeUs | (1UL << 7));

        var fault = Assert.Throws<PageFaultException>(
            () => machine.Cpu.ReadMemoryByte(SegReg.Ds, Linear));

        Assert.True(fault.Present);
        Assert.True(fault.Reserved);
        Assert.Equal(1u | 8u, fault.ErrorCode);
    }

    [Fact]
    public void ATwoMegabytePaePdeWithThePatBitSetFaultsRsvdOnAProcessorWithoutPat()
    {
        // Fix U3's other half: a 2 MB PDE's bit 12 is the same PAT index
        // bit, reserved on the same processor.
        const uint Linear = 0x00400000;
        const ulong Frame = 0x00600000;

        using Machine machine = PagedPae2Mb(Linear, Frame,
            pdeFlags: PaeP | PaeRw | PaeUs | PaePs | (1UL << 12));

        var fault = Assert.Throws<PageFaultException>(
            () => machine.Cpu.ReadMemoryByte(SegReg.Ds, Linear));

        Assert.True(fault.Present);
        Assert.True(fault.Reserved);
        Assert.Equal(1u | 8u, fault.ErrorCode);
    }

    [Fact]
    public void APteWhoseFrameNeedsBitThirtyTwoReadsAsOpenBus()
    {
        const uint Linear = 0x00400000;
        const ulong Frame = 0x00000001_00230000UL;   // bit 32 set; low 32 bits alias physical 0x00230000

        using Machine machine = PagedPae(Linear, Frame);

        // Fresh RAM under the low-32 alias, so a wrongly-truncated address
        // would read back as this sentinel instead of 0xFF.
        machine.Memory.WriteByte(0x00230000, 0x99);

        Assert.Equal(0xFF, machine.Cpu.ReadMemoryByte(SegReg.Ds, Linear));

        // A write through it is dropped, not redirected to the low-32 alias.
        machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear, 0x42);
        Assert.Equal(0x99, machine.Memory.ReadByte(0x00230000));
    }

    [Fact]
    public void APdpteWhoseOwnAddressNeedsBitThirtyTwoIsOpenBusNotAWronglyAliasedRead()
    {
        // Fix D3: it is not only a leaf entry's own data frame that can need
        // a bit this emulator's 32-bit bus has no room for -- so can the
        // *address of the next table down* that a PDPTE (or a PDE) names.
        // 0x1_0003_0001: present, bit 32 set, low 32 bits alias physical
        // 0x00030000 (masking off the low 12 bits leaves 0x1_00030000).
        // Read as open bus (all ones), the "PDE" that comes back has P and
        // PS both set (a 2 MB page) and every reserved bit of one set too,
        // so the walk ends in #PF with RSVD -- and physical 0x00030000,
        // which a wrongly-truncated address would have read and written
        // instead, is never touched at all.
        const uint Linear = 0x00400000;

        var machine = new Machine();
        machine.CpuModel = CpuModel.Pentium;
        machine.RunInstructions(200);

        machine.Cpu.Pdpte[0] = 0x1_0003_0001UL;
        machine.Memory.WriteByte(0x00030000, 0x99);

        machine.Cpu.State.Cr4 |= CpuState.Cr4Pae;
        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;
        machine.Cpu.State.Segments[(int)SegReg.Ds].Base = 0;
        machine.Cpu.State.Segments[(int)SegReg.Ds].Limit = 0xFFFFFFFF;

        using (machine)
        {
            var fault = Assert.Throws<PageFaultException>(
                () => machine.Cpu.ReadMemoryByte(SegReg.Ds, Linear));

            Assert.True(fault.Present);
            Assert.True(fault.Reserved);
            Assert.Equal(1u | 8u, fault.ErrorCode);
            Assert.Equal(0x99, machine.Memory.ReadByte(0x00030000));
        }
    }

    [Fact]
    public void InvlpgOfAnyAddressInATwoMegabytePaePageDropsEveryCachedSlot()
    {
        const uint Base = 0x00400000;   // 2 MB-aligned
        const ulong FirstFrame = 0x00600000;
        const ulong SecondFrame = 0x00800000;

        // Subpages 5 and 9, four apart -- not congruent mod the 1024-entry
        // TLB, so they hash to different slots; see LargePageTests' own
        // equivalent test for the same reasoning against a 4 MB page.
        const uint Touched = Base + 0x005000;
        const uint Invalidated = Base + 0x009000;

        using Machine machine = PagedPae2Mb(Base, FirstFrame);

        machine.Cpu.WriteMemoryByte(SegReg.Ds, Touched, 0xAA);
        Assert.Equal(0xAA, machine.Memory.ReadByte((uint)FirstFrame + 0x005000));

        WriteQword(machine.Memory, PdAddress + ((Base >> 21) & 0x1FF) * 8,
                   SecondFrame | PaeP | PaeRw | PaeUs | PaePs);
        machine.Cpu.InvalidatePage(Invalidated);

        machine.Cpu.WriteMemoryByte(SegReg.Ds, Touched, 0xBB);
        Assert.Equal(0xBB, machine.Memory.ReadByte((uint)SecondFrame + 0x005000));
        Assert.Equal(0xAA, machine.Memory.ReadByte((uint)FirstFrame + 0x005000));   // the old frame is untouched
    }

    [Fact]
    public void AGlobalPaeEntrySurvivesTheCr3ReloadFlushJustLikeTheLegacyFormat()
    {
        const uint Linear = 0x00400000;
        const ulong FirstFrame = 0x00330000;
        const ulong SecondFrame = 0x00350000;

        using Machine machine = PagedPae(Linear, FirstFrame,
            pteFlags: PaeP | PaeRw | PaeUs | PaeG);
        machine.Cpu.State.Cr4 |= CpuState.Cr4Pge;

        machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear, 0xAA);

        WriteQword(machine.Memory, PtAddress + ((Linear >> 12) & 0x1FF) * 8,
                   SecondFrame | PaeP | PaeRw | PaeUs | PaeG);
        machine.Cpu.FlushTlbForCr3Load();

        machine.Cpu.WriteMemoryByte(SegReg.Ds, Linear, 0xBB);
        Assert.Equal(0xBB, machine.Memory.ReadByte((uint)FirstFrame));
        Assert.NotEqual(0xBB, machine.Memory.ReadByte((uint)SecondFrame));
    }
}
