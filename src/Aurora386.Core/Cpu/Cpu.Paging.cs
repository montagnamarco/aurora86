// Paging: the two-level walk from a linear address to a physical one, the
// TLB that caches it, the accessed and dirty bits, and the faults --
// including write protection at ring 0, which the 486 honours when CR0.WP
// is set and the 386 did not. Only on when CR0.PG is set, which for the
// guests here means Windows in enhanced mode.
//
// CR4.PSE (the Pentium's Page Size Extension) lets a page directory entry
// map 4 MB directly instead of pointing at a page table -- PDE bit 7 (PS)
// says so, and the physical frame is then PDE[31:22] with the low 22 bits
// of the linear address underneath it rather than a table walk producing
// them. The TLB below still caches translations at 4 KB granularity (see
// TranslateLargePage's remarks) rather than gaining a second, page-sized
// cache the way the P5 itself has separate 4 KB and 4 MB TLBs -- an
// observably identical result, at the cost of one TLB entry per 4 KB of a
// large page instead of one entry for the whole 4 MB, which this emulator's
// 1024-entry TLB can afford.
//
// CR4.PGE (P6's Page Global Enable) and CR4.PAE (the same generation's
// Physical Address Extension) are P3.1 additions on top of that:
//
// - PGE marks a leaf entry (PTE, or a large PDE) "global" through its own
//   bit 8 (G). A global translation survives MOV CR3 and a task switch's
//   CR3 load -- the whole point being that a monitor's own mappings, the
//   same in every address space, need not be walked afresh on every context
//   switch -- but not CR0.PG, CR4.PSE/PAE/PGE changing (those still drop
//   everything, as they always did) and not INVLPG (which drops an entry
//   whatever its G bit, since the operating system is asking for that exact
//   translation to go). See FlushTlbForCr3Load.
// - PAE replaces the two-level, 32-bit-entry walk with a three-level,
//   64-bit-entry one: CR3 names a 32-byte, 4-entry page-directory-pointer
//   table instead of a page directory, and PDE/PTE grow to 64 bits with
//   room for a 36-bit physical frame (see TranslatePae). The four PDPT
//   entries are loaded into the internal registers real hardware also
//   keeps them in -- <see cref="Pdpte"/> -- at MOV CR3 and a few other
//   defined points (see LoadPdptRegisters), and never re-read from memory
//   between those points. This emulator's own memory bus stays 32 bits
//   wide throughout (plans/specs/p6.md, coordinator decision): a PAE entry
//   keeps whatever bits 35:32 the guest wrote, but a translation whose
//   physical address needs any of them reads as an open bus (0xFF) and
//   drops the write, via <see cref="OpenBusPhysicalAddress"/>, rather than
//   silently aliasing some unrelated low address.

using System.Diagnostics;

namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>
    /// Linear -> physical translation through the 386's page directory.
    ///
    /// Two levels: the top 10 bits index the directory, the next 10 index the
    /// page table, and the low 12 are the offset within the 4 KB page. Both
    /// levels carry permission bits, and the effective permission is the
    /// stricter of the two -- a writable page inside a read-only directory
    /// entry is not writable. With CR4.PSE set and the directory entry's PS
    /// bit on, there is no second level at all: see
    /// <see cref="TranslateLargePage"/>. With CR4.PAE set this method is not
    /// reached at all: see <see cref="TranslatePae"/> and
    /// <see cref="TranslateWhenPaging"/>.
    /// </summary>
    /// <param name="asSupervisor">
    /// True when the PROCESSOR is making this access on its own behalf -- a
    /// descriptor, an interrupt gate, a task state segment field, the I/O
    /// permission bitmap. Those are supervisor accesses whatever ring the
    /// running code is in, and saying so as an ARGUMENT is deliberate: the
    /// alternative, temporarily writing ring 0 into the processor's state,
    /// cannot express it in virtual 8086 mode, where the privilege is not a
    /// stored field but a consequence of the mode. See TranslateSupervisor.
    /// </param>
    /// <param name="execute">
    /// True for an instruction fetch. The legacy two-level format has no
    /// execute-disable bit, so every page it maps is executable; the flag is
    /// passed only so a cached translation filled by this walk answers the
    /// same question the PAE and long-mode walks ask of it.
    /// </param>
    private uint TranslatePaged(uint linear, bool write, bool asSupervisor = false, bool execute = false)
    {
        uint page = linear & 0xFFFFF000;
        uint offset = linear & 0xFFF;

        // The TLB caches the translation and the permissions that came with
        // it, which is what makes paging affordable: without it every access
        // would cost two extra memory reads, and in enhanced mode every
        // access comes through here. This is true whether the entry came
        // from an ordinary page table or from a 4 MB PDE: both fill the same
        // 4 KB-granular slot, and TlbEntry.LargePageMask only matters to
        // InvalidatePage.
        ref TlbEntry entry = ref _tlb[(page >> 12) & (TlbSize - 1)];
        if (entry.Valid && entry.Page == page)
        {
            CheckPagePermissions(linear, write, entry.Writable, entry.User, asSupervisor, execute, entry.Executable);

            // The dirty bit is not cached: a page that has only been read may
            // be written next, and the operating system has to be told.
            if (write && !entry.Dirty)
            {
                MarkDirty(entry.TableEntryAddress);
                entry.Dirty = true;
            }

            return entry.Frame | offset;
        }

        uint directoryIndex = linear >> 22;
        uint tableIndex = (linear >> 12) & 0x3FF;

        uint directoryAddress = (State.Cr3 & 0xFFFFF000) + directoryIndex * 4;
        uint directoryEntry = _memory.ReadDword(directoryAddress);
        if ((directoryEntry & PagePresent) == 0)
            throw new PageFaultException(linear, write, present: false,
                                         user: !asSupervisor && State.Cpl == 3);

        // PS is only a page-size bit when CR4.PSE says the processor honours
        // it at all; with PSE off it is simply an ordinary reserved/ignored
        // bit of a 4 KB PDE, and ordinary 4 KB pages are what every directory
        // entry describes (test case 1 of plans/specs/pse-vme.md).
        if ((State.Cr4 & CpuState.Cr4Pse) != 0 && (directoryEntry & PageSize) != 0)
            return TranslateLargePage(linear, write, page, offset, directoryAddress, directoryEntry, ref entry, asSupervisor);

        uint tableEntryAddress = (directoryEntry & 0xFFFFF000) + tableIndex * 4;
        uint tableEntry = _memory.ReadDword(tableEntryAddress);
        if ((tableEntry & PagePresent) == 0)
            throw new PageFaultException(linear, write, present: false,
                                         user: !asSupervisor && State.Cpl == 3);

        // Permissions are the AND of the two levels, not just the page's own.
        bool writable = (directoryEntry & PageWritable) != 0 && (tableEntry & PageWritable) != 0;
        bool user = (directoryEntry & PageUser) != 0 && (tableEntry & PageUser) != 0;

        CheckPagePermissions(linear, write, writable, user, asSupervisor);

        // The accessed bit is set on both levels, because the processor
        // walked both. Operating systems read it to work out which pages are
        // worth keeping in memory; the dirty bit, set only on the page
        // itself, tells them which ones must be written to swap first.
        if ((directoryEntry & PageAccessed) == 0)
            _memory.WriteDword(directoryAddress, directoryEntry | PageAccessed);

        uint updated = tableEntry | PageAccessed | (write ? PageDirty : 0);
        if (updated != tableEntry)
            _memory.WriteDword(tableEntryAddress, updated);

        entry.Valid = true;
        entry.Page = page;
        entry.Frame = tableEntry & 0xFFFFF000;
        entry.Writable = writable;
        entry.User = user;
        entry.Dirty = (updated & PageDirty) != 0;
        entry.Executable = true;
        entry.TableEntryAddress = tableEntryAddress;
        entry.LargePageMask = 0;

        // G only means anything once CR4.PGE says so; with PGE off this is
        // always false, whatever the entry's own bit 8 says, which is what
        // makes MOV CR3 flush unconditionally in that case (see
        // FlushTlbForCr3Load) exactly as if the flag did not exist.
        entry.Global = (State.Cr4 & CpuState.Cr4Pge) != 0 && (tableEntry & PageGlobal) != 0;

        return entry.Frame | offset;
    }

    /// <summary>
    /// The 4 MB path CR4.PSE opens up: a page directory entry with PS set is
    /// the whole translation, with no page table underneath it at all.
    ///
    /// Frame = PDE[31:22] concatenated with the low 22 bits of the linear
    /// address (SDM Vol.3A §4.3/Table 4-4 for the equivalent P6+ case;
    /// R. Collins "Understanding 4M Page Size Extensions" for the P5 itself,
    /// cited in plans/specs/pse-vme.md). Rights come from the PDE alone --
    /// there is no second level to AND against -- and CR0.WP applies to that
    /// single set of rights exactly as it does to a 4 KB page's combined
    /// ones. A and D are both maintained in the PDE, since it is the only
    /// entry there is.
    ///
    /// The result is filled into the same 4 KB-granular TLB slot an ordinary
    /// page would use (see the module comment for why), tagged with a 4 MB
    /// <see cref="TlbEntry.LargePageMask"/> so <see cref="InvalidatePage"/>
    /// can find every other slot the same 4 MB page may have filled.
    /// </summary>
    private uint TranslateLargePage(uint linear, bool write, uint page, uint offset,
                                     uint directoryAddress, uint directoryEntry, ref TlbEntry entry,
                                     bool asSupervisor)
    {
        // PDE[21:13]: on a 4 KB PDE these nine bits are the low bits of the
        // page-table frame number; a 4 MB PDE has no page table, so they are
        // simply unused and must be zero. The SDM calls a nonzero value here
        // a reserved-bit violation and folds it into the ordinary #PF with
        // the reserved-bit indication set (error code bit 3) on the
        // processors that document it explicitly (PAE and long-mode
        // paging); Collins does not cite a P5 error-code dump for this exact
        // case, so which bit pattern the very first PSE silicon produced is
        // *uncertain* here -- this uses the same bit 3 later processors are
        // documented to set, as the least surprising choice absent a trace,
        // and the P entry present alongside it (SDM: RSVD faults are always
        // reported as P=1, since the entry was found -- it was merely
        // malformed) and the usual W/R and U/S bits.
        if ((directoryEntry & LargePageReservedMask) != 0)
            throw new PageFaultException(linear, write, present: true, user: !asSupervisor && State.Cpl == 3, reserved: true);

        bool writable = (directoryEntry & PageWritable) != 0;
        bool user = (directoryEntry & PageUser) != 0;

        CheckPagePermissions(linear, write, writable, user, asSupervisor);

        uint updated = directoryEntry | PageAccessed | (write ? PageDirty : 0);
        if (updated != directoryEntry)
            _memory.WriteDword(directoryAddress, updated);

        uint frame = (directoryEntry & 0xFFC00000) | (linear & 0x003FF000);

        entry.Valid = true;
        entry.Page = page;
        entry.Frame = frame;
        entry.Writable = writable;
        entry.User = user;
        entry.Dirty = (updated & PageDirty) != 0;
        // The dirty bit lives in the PDE itself here, so the lazy dirty-bit
        // write in TranslatePaged's cache-hit path -- which always targets
        // TableEntryAddress -- reaches the right dword without change.
        entry.Executable = true;
        entry.TableEntryAddress = directoryAddress;
        entry.LargePageMask = LegacyLargePageMask;
        entry.Global = (State.Cr4 & CpuState.Cr4Pge) != 0 && (directoryEntry & PageGlobal) != 0;
        _largeEntriesCached = true;

        return frame | offset;
    }

    /// <summary>
    /// Raises a page fault when the access is not allowed by the page's own
    /// permission bits, as opposed to the page simply not being there.
    ///
    /// WHO IS ASKING is the argument rather than a reading of the processor's
    /// state, because the two differ for the accesses the processor makes on
    /// its own behalf. See TranslateSupervisor for why that distinction
    /// cannot be expressed by writing ring 0 into the state in V86 mode.
    /// </summary>
    private void CheckPagePermissions(uint linear, bool write, bool writable, bool user,
                                      bool asSupervisor = false, bool execute = false, bool executable = true)
    {
        bool userMode = !asSupervisor && State.Cpl == 3;
        bool instructionFetch = execute && InstructionFetchReported;

        // Supervisor-only pages are invisible to ring 3. This is the whole
        // basis of keeping the kernel safe from applications.
        if (userMode && !user)
            throw new PageFaultException(linear, write, present: true, user: true, instructionFetch: instructionFetch);

        // A page whose tables set the execute-disable bit (XD, bit 63, with
        // EFER.NXE on) may be read and written but never fetched from, at
        // any privilege (SDM Vol.3A §4.6). The bit exists only in the PAE
        // and long-mode formats; the legacy walk marks every page executable.
        if (execute && !executable)
            throw new PageFaultException(linear, write, present: true, user: userMode, instructionFetch: true);

        // Read-only pages cannot be written from user code. Supervisor code
        // may write them on a 386 whatever the bit says -- the 386 has no
        // write-protect control at all. The 486 added one, CR0.WP (bit 16),
        // and with it set the kernel is held to the same rule as everyone
        // else. Honouring the bit costs nothing when it is clear, which is how
        // every 386-era system leaves it, and it is what the hardware suite
        // this processor is checked against expects.
        bool writeProtect = (State.Cr0 & (1u << 16)) != 0;
        if (write && !writable && (userMode || writeProtect))
            throw new PageFaultException(linear, write, present: true, user: userMode);
    }

    /// <summary>
    /// Whether a page fault's error code reports instruction fetches (bit 4,
    /// I/D): only when execute-disable is in force, that is PAE or long-mode
    /// paging with EFER.NXE set (SDM Vol.3A §4.7; there is no SMEP here).
    /// </summary>
    private bool InstructionFetchReported =>
        (State.Cr4 & CpuState.Cr4Pae) != 0 && (State.Efer & CpuState.EferNoExecuteEnable) != 0;

    /// <summary>
    /// A page fault from the PAE or long-mode walk, whose error code carries
    /// the instruction-fetch bit whenever <see cref="InstructionFetchReported"/>
    /// says so -- for a missing page and a reserved bit as much as for a
    /// protection violation, since the bit describes the access, not the
    /// cause.
    /// </summary>
    private PageFaultException WalkFault(uint linear, bool write, bool present, bool asSupervisor,
                                         bool execute, bool reserved = false)
    {
        return new PageFaultException(
            linear,
            write,
            present,
            user: !asSupervisor && State.Cpl == 3,
            reserved: reserved,
            instructionFetch: execute && InstructionFetchReported);
    }

    private void MarkDirty(uint tableEntryAddress)
    {
        uint tableEntry = _memory.ReadDword(tableEntryAddress);
        _memory.WriteDword(tableEntryAddress, tableEntry | PageDirty);
    }

    /// <summary>
    /// Empties the TLB. Loading CR3 does this on real hardware, and so does
    /// any change that could invalidate a cached translation: the processor
    /// has no way of knowing that the operating system has rewritten a page
    /// table behind its back, so the operating system has to say so.
    ///
    /// This is the unconditional flush CR0.PG and CR4.PSE/PAE/PGE changing
    /// still use (Cpu.TwoByte.cs's WriteCr0/WriteCr4): every cached
    /// translation is dropped, global or not, because those changes alter
    /// what a linear address means or how its permissions are decided, not
    /// merely which address space is current. A CR3 reload is different --
    /// see <see cref="FlushTlbForCr3Load"/>.
    /// </summary>
    public void FlushTlb()
    {
        for (int i = 0; i < _tlb.Length; i++) _tlb[i].Valid = false;
        _largeEntriesCached = false;
        InvalidateFetchPage();
    }

    /// <summary>
    /// What MOV CR3 and a task switch's CR3 load do to the TLB, as opposed to
    /// the unconditional <see cref="FlushTlb"/> a CR0.PG or CR4.PSE/PAE/PGE
    /// change still uses.
    ///
    /// With CR4.PGE clear this is the same full flush. With it set, a page
    /// marked global in its leaf entry (PGE is what gives that bit meaning
    /// at all -- see <see cref="TranslatePaged"/>) survives: PGE exists
    /// precisely so a monitor's own mappings, present identically in every
    /// address space, need not be walked afresh on every context switch
    /// (SDM Vol.3A §4.10.2.4, plans/specs/p6.md item 5). INVLPG still drops
    /// a global entry outright (<see cref="InvalidatePage"/>) -- only a CR3
    /// reload treats G specially.
    ///
    /// Exposed (rather than kept private, the way the rest of this file's
    /// internals are) for the same reason <see cref="FlushTlb"/> already is:
    /// it is exactly what the MOV-to-CR3 instruction handler and the task
    /// switch's CR3 load call, in Cpu.TwoByte.cs and Cpu.Tasks.cs, and a test
    /// can exercise the flush rule this way without needing to encode and
    /// step an instruction under active paging to get there.
    ///
    /// The instruction-fetch cache is dropped unconditionally either way: it
    /// does not record whether the page it last translated was global, and
    /// guessing wrong would run the wrong code from the wrong address space.
    /// </summary>
    public void FlushTlbForCr3Load()
    {
        if ((State.Cr4 & CpuState.Cr4Pge) == 0)
        {
            FlushTlb();
            return;
        }

        bool anyLargeSurvives = false;
        for (int i = 0; i < _tlb.Length; i++)
        {
            ref TlbEntry candidate = ref _tlb[i];
            if (!candidate.Valid) continue;

            if (candidate.Global)
            {
                if (candidate.LargePageMask != 0) anyLargeSurvives = true;
            }
            else
            {
                candidate.Valid = false;
            }
        }

        _largeEntriesCached = anyLargeSurvives;
        InvalidateFetchPage();
    }

    /// <summary>
    /// Drops one page's cached translation, as the INVLPG instruction does.
    ///
    /// A large page (4 MB under CR4.PSE, 2 MB under CR4.PAE) fills its 4 KB-
    /// granular TLB slots from many different linear addresses, each hashing
    /// to a different slot, so INVLPG of any one address in the range must
    /// drop every slot that came from the same large page: the SDM (vol. 3A
    /// 4.10.2.3) requires exactly that of a processor that caches a large
    /// page as smaller entries. The slots are found by their linear address,
    /// never by reading the page tables again: the guest has usually
    /// rewritten the entry before it invalidates, and a sweep that asked
    /// whether it was still large left the old large-page translation cached
    /// after the page was unmapped or split into a page table.
    ///
    /// Whatever G says, the entry goes: only a CR3 reload treats a global
    /// entry specially (<see cref="FlushTlbForCr3Load"/>), never INVLPG.
    /// </summary>
    public void InvalidatePage(uint linear)
    {
        uint page = linear & 0xFFFFF000;
        ref TlbEntry entry = ref _tlb[(page >> 12) & (TlbSize - 1)];

        if (entry.Valid && entry.Page == page)
        {
            entry.Valid = false;
        }

        if (_largeEntriesCached)
        {
            InvalidateLargePage(linear);
        }

        InvalidateFetchPage();
    }

    /// <summary>
    /// Drops every cached slot that came from the same large page as
    /// <paramref name="linear"/> -- a 4 MB one under CR4.PSE, a 2 MB one
    /// under CR4.PAE -- using whichever mask that particular cached entry
    /// was filled with, so 4 MB and 2 MB entries are swept correctly even
    /// mixed in the same TLB. Only reached when a large translation has been
    /// cached since the last flush, so INVLPG on a machine that uses only
    /// 4 KB pages never pays for the sweep.
    /// </summary>
    private void InvalidateLargePage(uint linear)
    {
        for (int i = 0; i < _tlb.Length; i++)
        {
            ref TlbEntry candidate = ref _tlb[i];
            if (!candidate.Valid || candidate.LargePageMask == 0) continue;

            if ((candidate.Page & candidate.LargePageMask) == (linear & candidate.LargePageMask))
            {
                candidate.Valid = false;
            }
        }
    }

    /// <summary>
    /// Whether any slot has been filled from a large page since the TLB was
    /// last flushed; see <see cref="InvalidatePage"/>.
    /// </summary>
    private bool _largeEntriesCached;

    /// <summary>
    /// A direct-mapped translation cache. Real processors use a small
    /// associative one; direct-mapped is simpler and gets the same
    /// observable behaviour, since a TLB is only ever allowed to hold
    /// translations the page tables would have produced anyway.
    /// </summary>
    private struct TlbEntry
    {
        public bool Valid;
        public uint Page;

        /// <summary>
        /// The 4 KB-aligned physical address this slot translates to (low 12
        /// bits always zero; the caller ORs the linear address's own low 12
        /// bits in on every return). Set to
        /// <see cref="OpenBusPhysicalAddress"/> instead of the real frame
        /// when a PAE translation's physical address needed bits 35:32 --
        /// see that constant's remarks.
        /// </summary>
        public uint Frame;
        public bool Writable;
        public bool User;
        public bool Dirty;

        /// <summary>
        /// Whether code may be fetched from the page: false when any level
        /// of a PAE or long-mode walk had XD (bit 63) set with EFER.NXE on.
        /// EFER.NXE changing flushes the TLB (ModelSpecificRegisters'
        /// TryWriteEfer), so a cached value never outlives the rule it was
        /// computed under.
        /// </summary>
        public bool Executable;

        /// <summary>
        /// Whether PGE (CR4 bit 7) makes this translation survive a CR3
        /// reload: the leaf entry's own G bit, already ANDed with CR4.PGE
        /// at fill time (TranslatePaged, TranslateLargePage, TranslatePae),
        /// so nothing else has to re-check CR4 to use this flag. Read only
        /// by <see cref="FlushTlbForCr3Load"/>; INVLPG ignores it.
        /// </summary>
        public bool Global;

        /// <summary>
        /// Zero for an ordinary 4 KB translation. Otherwise the mask that
        /// finds every other TLB slot the same large page may have filled --
        /// <see cref="LegacyLargePageMask"/> for a 4 MB CR4.PSE page,
        /// <see cref="PaeLargePageMask"/> for a 2 MB CR4.PAE one -- which is
        /// all <see cref="InvalidatePage"/> needs to sweep either kind
        /// correctly, including both cached in the same TLB at once (an
        /// unreachable case today, since toggling CR4.PAE always flushes
        /// first, but the mask makes it correct anyway rather than by
        /// accident of ordering).
        /// </summary>
        public uint LargePageMask;

        public uint TableEntryAddress;
    }

    // 1024 entries: enhanced-mode Windows works across the monitor's pages
    // high in the address space, the applications' and the display's, and
    // with 64 entries the useful translations kept evicting one another.
    // Coincidentally exactly the number of 4 KB pages in a 4 MB page: a single
    // large page, once fully touched, occupies every slot, which is why INVLPG
    // of a large page sweeps the whole table.
    private const int TlbSize = 1024;
    private readonly TlbEntry[] _tlb = new TlbEntry[TlbSize];

    private const uint PagePresent = 1u << 0;
    private const uint PageWritable = 1u << 1;
    private const uint PageUser = 1u << 2;
    private const uint PageAccessed = 1u << 5;
    private const uint PageDirty = 1u << 6;

    /// <summary>PDE bit 7 (PS): with CR4.PSE set, this entry maps 4 MB directly instead of pointing at a page table.</summary>
    private const uint PageSize = 1u << 7;

    /// <summary>
    /// Bit 8 of a leaf entry (PTE, or a PDE with PS set): global, once
    /// CR4.PGE says the bit means anything at all (P6 and later; SDM Vol.3A
    /// §4.10.2.4). A non-leaf 4 KB PDE has the same bit position, where it is
    /// simply ignored -- there is nothing there for it to mark global.
    /// </summary>
    private const uint PageGlobal = 1u << 8;

    /// <summary>PDE[21:13]: unused by a 4 MB page (there is no page-table frame number to hold), and reserved-must-be-zero when PS is set.</summary>
    private const uint PseReservedMask = 0x003FE000;

    /// <summary>PDE bit 12 of a 4 MB page: the PAT index bit where PAT exists, reserved (must be 0) where it does not (SDM vol. 3A table 4-4).</summary>
    private const uint LargePagePatBit = 1u << 12;

    /// <summary>The mask <see cref="TlbEntry.LargePageMask"/> and <see cref="InvalidateLargePage"/> use for a CR4.PSE 4 MB page: PDE[31:22], the top 10 bits.</summary>
    private const uint LegacyLargePageMask = 0xFFC00000;

    /// <summary>
    /// The bits of a 4 MB PDE that must be zero on this processor: PDE[21:13]
    /// always, and bit 12 too unless the processor has PAT.
    /// </summary>
    internal uint LargePageReservedMask => State.Features.Has(CpuFeature.Pat)
        ? PseReservedMask
        : PseReservedMask | LargePagePatBit;

    // ------------------------------------------------------------------
    // CR4.PAE: three-level, 64-bit-entry paging (SDM Vol.3A §4.4).
    //
    // CR3[31:5] names a 32-byte, 4-entry page-directory-pointer table
    // instead of a page directory; linear[31:30] picks one of those four
    // entries, linear[29:21] then picks one of 512 page-directory entries
    // (each covering 2 MB), and -- unless that PDE's PS bit says the 2 MB
    // page is the whole translation -- linear[20:12] picks one of 512 page-
    // table entries covering 4 KB each. Every entry from here down is a
    // 64-bit qword: P, R/W, U/S, PWT, PCD, A, D (leaf), PS (PDE) or PAT
    // (PTE), G (leaf), a 36-bit frame in bits 35:12, and bits 63:36 reserved
    // (there is no NX on a P6). A 2 MB PDE additionally reserves bits 20:13,
    // the part of a 4 KB PDE's own bits that would otherwise be a page-table
    // frame number but has nothing to point at once PS says there is no
    // second level.
    // ------------------------------------------------------------------

    private const ulong PaePresent = 1UL << 0;
    private const ulong PaeReadWrite = 1UL << 1;
    private const ulong PaeUserSupervisor = 1UL << 2;
    // Bits 3 (PWT) and 4 (PCD) exist only as storage, exactly as the legacy
    // format's own equivalents do (no cache model here to configure), so
    // this file never names them.
    private const ulong PaeAccessed = 1UL << 5;
    private const ulong PaeDirty = 1UL << 6;

    /// <summary>PDE bit 7: this entry maps a 2 MB page directly, whatever CR4.PSE says -- PAE's PS is not gated by it (plans/specs/p6.md item 6). Shares its position with a 4 KB PTE's PAT bit, which is only ever read where the caller already knows it is holding a PTE rather than a PDE.</summary>
    private const ulong PaePageSize = 1UL << 7;

    /// <summary>Bit 8 of a PAE leaf entry: global, under CR4.PGE, exactly like the legacy format's <see cref="PageGlobal"/>.</summary>
    private const ulong PaeGlobal = 1UL << 8;

    /// <summary>Bit 12 of a 2 MB PDE: the PAT index bit, in the space a 4 KB PDE would use for the low bits of its page-table frame number.</summary>
    private const ulong PaeLargePagePatBit = 1UL << 12;

    /// <summary>Bit 7 of a 4 KB PTE: the PAT index bit where PAT exists, reserved (must be 0) where it does not (SDM Vol.3A Table 4-8) -- the 4 KB counterpart of <see cref="PaeLargePagePatBit"/>, sharing its position with a PDE's own PS bit (<see cref="PaePageSize"/>), which is why this name is only ever read where the caller already knows the entry in hand is a PTE.</summary>
    private const ulong PaePatBit = 1UL << 7;

    /// <summary>
    /// Every reserved bit of a present, non-2-MB PAE PTE: bits 63:36 always,
    /// plus bit 7 (PAT) too on a processor without <see cref="CpuFeature.Pat"/>
    /// -- mirrors <see cref="LargePageReservedMask"/>'s legacy-format
    /// equivalent (coordinator decision U3).
    /// </summary>
    private ulong PaePteReservedMask => PaeEntryReservedHighMask
        | (State.Features.Has(CpuFeature.Pat) ? 0UL : PaePatBit);

    /// <summary>
    /// Every reserved bit of a present 2 MB PAE PDE: bits 63:36 and 20:13
    /// always, plus bit 12 (PAT) too on a processor without
    /// <see cref="CpuFeature.Pat"/> -- mirrors <see cref="LargePageReservedMask"/>'s
    /// legacy-format equivalent (coordinator decision U3).
    /// </summary>
    private ulong Pae2MbPdeReservedMask => PaeEntryReservedHighMask | Pae2MbReservedMask
        | (State.Features.Has(CpuFeature.Pat) ? 0UL : PaeLargePagePatBit);

    /// <summary>
    /// Bit 63 of a PAE or long-mode PDE/PTE (and a long-mode PML4E/PDPTE):
    /// XD, execute-disable, meaningful only while EFER.NXE is set and
    /// reserved otherwise (SDM Vol.3A Tables 4-9 to 4-11 and 4-15 to 4-20).
    /// A PAE PDPT register never has it.
    /// </summary>
    private const ulong ExecuteDisableBit = 1UL << 63;

    /// <summary>
    /// The high reserved bits of a PAE PDE or PTE: 63:36 (this processor's
    /// physical address width is 36 bits, CPUID 80000008h), less bit 63
    /// once EFER.NXE makes it the XD bit.
    /// </summary>
    private ulong PaeEntryReservedHighMask => (State.Efer & CpuState.EferNoExecuteEnable) != 0
        ? PaeReservedHighMask & ~ExecuteDisableBit
        : PaeReservedHighMask;

    /// <summary>Bits 35:12: a 4 KB-aligned 36-bit physical address, whether it names the next table down or (a PTE's, or a 2 MB PDE's) own frame.</summary>
    private const ulong PaeAddressMask4K = ((1UL << 36) - 1) & ~0xFFFUL;

    /// <summary>Bits 35:21: a 2 MB-aligned frame, for a PDE with PS set.</summary>
    private const ulong PaeAddressMask2M = ((1UL << 36) - 1) & ~((1UL << 21) - 1);

    /// <summary>PDE[20:13] of a 2 MB page: no page-table frame number to hold once PS says there is no second level, so reserved-must-be-zero (plans/specs/p6.md item 6).</summary>
    private const ulong Pae2MbReservedMask = ((1UL << 21) - 1) & ~((1UL << 13) - 1);

    /// <summary>Bits 63:36 of any PAE PDE or PTE: reserved-must-be-zero on a processor with no NX bit.</summary>
    private const ulong PaeReservedHighMask = ~((1UL << 36) - 1);

    /// <summary>Bits 2:1 and 8:5 of a PDPTE: reserved-must-be-zero (a PDPTE has no R/W, U/S, A or D of its own in this, the only paging mode this processor gives PAE -- IA-32e mode is P5.2's).</summary>
    private const ulong PdpteReservedLowMask = (0x3UL << 1) | (0xFUL << 5);

    /// <summary>Every reserved bit of a present PDPTE: bits 2:1, 8:5 and 63:36 (SDM Vol.3A §4.4, plans/specs/p6.md item 6).</summary>
    private const ulong PdpteReservedMask = PdpteReservedLowMask | PaeReservedHighMask;

    /// <summary>The mask <see cref="TlbEntry.LargePageMask"/> and <see cref="InvalidateLargePage"/> use for a CR4.PAE 2 MB page: linear/physical bits 35:21.</summary>
    private const uint PaeLargePageMask = 0xFFE00000;

    /// <summary>
    /// A physical address guaranteed unmapped on every machine this emulator
    /// configures (Machine.cs: installed RAM plus the video and PCI MMIO
    /// windows never come close to this), and safely below the BIOS ROM's
    /// reset-vector alias at the very top of the 32-bit space
    /// (Machine.Post.cs maps that alias from 0xFFFF0000). PhysicalMemoryBus
    /// already treats an address like that as a bus with nothing connected
    /// to it -- reads 0xFF, drops writes -- which is exactly the behaviour a
    /// PAE translation needs once its physical address needs bits 35:32 this
    /// 32-bit-bus emulator has nowhere to send (plans/specs/p6.md's
    /// coordinator decision): routing through this address gets it for
    /// free, rather than truncating to the low 32 bits and silently
    /// aliasing whatever unrelated page happens to sit there.
    ///
    /// This has to stay true of whatever RAM and MMIO a guest is actually
    /// configured with, and below the ROM alias's own start -- both asserted
    /// in <see cref="AssertOpenBusPhysicalAddressIsSafe"/>, cheaply, only on
    /// the rare path that reaches it (coordinator decision U6).
    /// </summary>
    private const uint OpenBusPhysicalAddress = 0xFFFE0000;

    /// <summary>Whether a 36-bit physical address needs a bit this emulator's 32-bit bus has no room for.</summary>
    private static bool IsOpenBusPhysicalAddress(ulong physical) => (physical >> 32) != 0;

    /// <summary>
    /// Debug-only check that <see cref="OpenBusPhysicalAddress"/> is still
    /// what its own remarks promise: outside every byte of installed RAM,
    /// and below 0xFFFF0000, where Machine.Post.cs maps the BIOS ROM's
    /// reset-vector alias. Neither bound can move without this constant
    /// having to move with it -- landing inside RAM would silently alias a
    /// real page instead of reading open bus, and landing inside the ROM
    /// alias would read BIOS bytes instead. Called only from the few call
    /// sites that actually take the open-bus branch, so it costs nothing on
    /// the path every ordinary translation takes, and nothing at all in a
    /// release build (coordinator decision U6).
    /// </summary>
    [Conditional("DEBUG")]
    private void AssertOpenBusPhysicalAddressIsSafe()
    {
        Debug.Assert(OpenBusPhysicalAddress >= _memory.RamSize,
            "OpenBusPhysicalAddress must stay outside installed RAM.");
        Debug.Assert(OpenBusPhysicalAddress < 0xFFFF0000,
            "OpenBusPhysicalAddress must stay below the BIOS ROM's reset-vector alias.");
    }

    /// <summary>
    /// Reads one PAE paging-structure entry (a PDE or a PTE) at the physical
    /// address the level above named for it -- itself built from that
    /// level's own frame field, so a stray high bit the guest wrote there
    /// can make the *address of the next table down* need bits 35:32 this
    /// emulator's 32-bit bus has nowhere to send, exactly as
    /// <see cref="IsOpenBusPhysicalAddress"/> already covers for a leaf
    /// entry's own data frame. Rather than truncating to the low 32 bits and
    /// reading whatever unrelated table happens to sit there, this reads
    /// back as all ones: P set, and every reserved bit set too (63:36
    /// always, and PS's own besides), so the walk always ends in #PF with
    /// the reserved-bit indication set, and the caller never reaches the
    /// point of writing A or D back through the alias (coordinator decision
    /// D3).
    /// </summary>
    private ulong ReadPagingStructureEntry(ulong structureAddress)
    {
        if (!IsOpenBusPhysicalAddress(structureAddress)) return ReadPhysicalQword((uint)structureAddress);

        AssertOpenBusPhysicalAddressIsSafe();
        return ulong.MaxValue;
    }

    // ReadPhysicalQword/WritePhysicalQword (a page-table-structure entry's
    // address is always a plain 32-bit one by construction here: the four
    // PDPT registers, and every PDE and PTE this emulator's guests ever
    // write, describe tables that live in the emulator's actual, well under
    // 4 GB, RAM, so only the final data frame a PTE or 2 MB PDE names is
    // ever checked against OpenBusPhysicalAddress -- see TranslatePae) are
    // Cpu.Pentium.cs's own, added for CMPXCHG8B's split-access path; reused
    // here rather than duplicated, since both are the same "one qword, two
    // dword bus accesses, kept whole for MMIO" operation.

    /// <summary>
    /// The four PDPT registers CR4.PAE gives this processor (SDM Vol.3A
    /// §4.4.1), indexed by linear[31:30]. Loaded by
    /// <see cref="LoadPdptRegisters"/> at MOV CR3, at CR0.PG's 0-to-1
    /// transition with PAE already set, at CR4.PAE's 0-to-1 transition with
    /// paging already on, and at a task switch's CR3 load (Cpu.TwoByte.cs,
    /// Cpu.Tasks.cs) -- never re-read from memory between those points,
    /// exactly as hardware keeps them in registers rather than walking the
    /// table on every translation. Exposed so the machine snapshot can carry
    /// them (a running PAE guest restored without them would translate
    /// through whatever these four qwords happened to hold before, almost
    /// certainly not the guest's real page tables).
    /// </summary>
    public readonly ulong[] Pdpte = new ulong[4];

    /// <summary>
    /// Loads the four PDPT registers from the 32-byte page-directory-pointer
    /// table at <paramref name="cr3"/>'s bits 31:5 -- the value MOV CR3 is
    /// about to store, or the current CR3 when CR0.PG or CR4.PAE is what
    /// changed instead (SDM Vol.3A §4.4.1).
    ///
    /// Every present entry (bit 0 set) has its reserved bits checked right
    /// here (<see cref="PdpteReservedMask"/>): hardware raises #GP(0) at the
    /// instruction that caused the reload, not a page fault deferred until
    /// the entry is actually used, so the check has to happen before
    /// anything commits. All four entries are read into a local buffer
    /// first and copied into <see cref="Pdpte"/> only once none of them
    /// faults, so a bad fourth entry cannot leave the first three loaded
    /// while CR3 (or CR0/CR4) is left exactly as it was -- which is the
    /// point of raising #GP(0) here rather than quietly loading whatever
    /// could be read. A not-present entry is stored unchecked: an absent
    /// PDPTE faults with #PF only when a linear address that would use it is
    /// actually translated (<see cref="TranslatePae"/>).
    /// </summary>
    private void LoadPdptRegisters(uint cr3)
    {
        uint pdptBase = cr3 & 0xFFFFFFE0;
        Span<ulong> loaded = stackalloc ulong[4];

        for (int i = 0; i < 4; i++)
        {
            ulong pdpte = ReadPhysicalQword(pdptBase + (uint)(i * 8));
            if ((pdpte & PaePresent) != 0 && (pdpte & PdpteReservedMask) != 0)
                throw new CpuException(13, 0);
            loaded[i] = pdpte;
        }

        for (int i = 0; i < 4; i++) Pdpte[i] = loaded[i];
    }

    /// <summary>
    /// Linear -> physical translation through PAE's three levels, in place
    /// of <see cref="TranslatePaged"/> whenever CR4.PAE is set. Shares the
    /// same TLB (the two modes never run at once without an intervening
    /// full flush -- CR4.PAE changing always flushes, see Cpu.TwoByte.cs's
    /// WriteCr4) and the same permission and dirty-bit machinery
    /// (<see cref="CheckPagePermissions"/>, <see cref="MarkDirty"/> -- D
    /// lives in bit 6 of the low dword in both formats, so the lazy dirty
    /// write on a cache hit needs no PAE-specific version at all).
    /// </summary>
    private uint TranslatePae(uint linear, bool write, bool asSupervisor = false, bool execute = false)
    {
        uint page = linear & 0xFFFFF000;
        uint offset = linear & 0xFFF;

        ref TlbEntry entry = ref _tlb[(page >> 12) & (TlbSize - 1)];
        if (entry.Valid && entry.Page == page)
        {
            CheckPagePermissions(linear, write, entry.Writable, entry.User, asSupervisor, execute, entry.Executable);

            if (write && !entry.Dirty)
            {
                MarkDirty(entry.TableEntryAddress);
                entry.Dirty = true;
            }

            return entry.Frame | offset;
        }

        // The PDPTE is never re-read from memory: real hardware keeps the
        // four of them in internal registers between reloads, and so does
        // this emulator (Pdpte, filled by LoadPdptRegisters). A reserved bit
        // in a present one was already turned into #GP(0) back when it was
        // loaded; only presence is checked on every translation.
        ulong pdpte = Pdpte[(linear >> 30) & 0x3];
        if ((pdpte & PaePresent) == 0)
            throw WalkFault(linear, write, present: false, asSupervisor, execute);

        uint directoryIndex = (linear >> 21) & 0x1FF;
        ulong pdeAddress64 = (pdpte & PaeAddressMask4K) + directoryIndex * 8;
        ulong pde = ReadPagingStructureEntry(pdeAddress64);
        if ((pde & PaePresent) == 0)
            throw WalkFault(linear, write, present: false, asSupervisor, execute);

        if ((pde & PaePageSize) != 0)
            return TranslatePae2MbPage(linear, write, page, offset, (uint)pdeAddress64, pde, ref entry, asSupervisor, execute);

        if ((pde & PaeEntryReservedHighMask) != 0)
            throw WalkFault(linear, write, present: true, asSupervisor, execute, reserved: true);

        uint tableIndex = (linear >> 12) & 0x1FF;
        ulong pteAddress64 = (pde & PaeAddressMask4K) + tableIndex * 8;
        ulong pte = ReadPagingStructureEntry(pteAddress64);
        if ((pte & PaePresent) == 0)
            throw WalkFault(linear, write, present: false, asSupervisor, execute);
        if ((pte & PaePteReservedMask) != 0)
            throw WalkFault(linear, write, present: true, asSupervisor, execute, reserved: true);

        // Reaching here means PS was clear, so pde is not the open-bus
        // sentinel (that reads back with every bit, PS included, set) --
        // pdeAddress64 is safe to narrow for the write below.
        uint pdeAddress = (uint)pdeAddress64;
        uint pteAddress = (uint)pteAddress64;

        // As in the legacy walk, permissions are the AND of the two levels
        // that exist here (the PDPTE contributes none at all: on this
        // processor it carries no R/W, U/S, A or D -- plans/specs/p6.md item
        // 6's confirmation that rights are the PDE/PTE AND alone).
        bool writable = (pde & PaeReadWrite) != 0 && (pte & PaeReadWrite) != 0;
        bool user = (pde & PaeUserSupervisor) != 0 && (pte & PaeUserSupervisor) != 0;
        bool executable = IsExecutable(pde) && IsExecutable(pte);

        CheckPagePermissions(linear, write, writable, user, asSupervisor, execute, executable);

        // The PDE's own A bit records that the walk reached it, exactly as
        // the legacy directory entry's does.
        if ((pde & PaeAccessed) == 0)
            _memory.WriteDword(pdeAddress, (uint)(pde | PaeAccessed));

        ulong updatedPte = pte | PaeAccessed | (write ? PaeDirty : 0);
        if (updatedPte != pte)
        {
            // A and D are both in the low dword (bits 5 and 6); the high
            // dword (the address extension and the reserved bits above it)
            // never changes here, so only the low one needs writing back --
            // the same single-dword rewrite TranslatePaged's own dirty-bit
            // update already relies on for the legacy 32-bit format. Real
            // hardware performs a locked read-modify-write of the whole
            // qword; leaving the high dword exactly as it was produces the
            // identical memory image without needing to reread or rewrite
            // it.
            _memory.WriteDword(pteAddress, (uint)updatedPte);
        }

        ulong frame64 = pte & PaeAddressMask4K;
        bool openBus = IsOpenBusPhysicalAddress(frame64);
        if (openBus) AssertOpenBusPhysicalAddressIsSafe();

        entry.Valid = true;
        entry.Page = page;
        entry.Frame = openBus ? OpenBusPhysicalAddress : (uint)frame64;
        entry.Writable = writable;
        entry.User = user;
        entry.Dirty = (updatedPte & PaeDirty) != 0;
        entry.Executable = executable;
        entry.TableEntryAddress = pteAddress;
        entry.LargePageMask = 0;
        entry.Global = (State.Cr4 & CpuState.Cr4Pge) != 0 && (pte & PaeGlobal) != 0;

        return entry.Frame | offset;
    }

    /// <summary>
    /// The 2 MB path a PAE PDE's own PS bit opens up -- unlike the legacy
    /// format's 4 MB pages, not gated by CR4.PSE at all (plans/specs/p6.md
    /// item 6): the PDE is the whole translation, exactly as
    /// <see cref="TranslateLargePage"/> is for a 4 MB page, but with a 64-bit
    /// entry, a 2 MB-aligned frame in bits 35:21, and PAT (rather than the
    /// low bits of a page-table frame number) at bit 12.
    /// </summary>
    private uint TranslatePae2MbPage(uint linear, bool write, uint page, uint offset,
                                      uint pdeAddress, ulong pde, ref TlbEntry entry,
                                      bool asSupervisor, bool execute)
    {
        // PDE[20:13]: no page-table frame number to hold once PS says there
        // is no second level, so reserved-must-be-zero, folded into the same
        // #PF-with-RSVD as bits 63:36 -- and, on a processor without PAT,
        // bit 12 too (plans/specs/p6.md item 6; coordinator decision U3).
        if ((pde & Pae2MbPdeReservedMask) != 0)
            throw WalkFault(linear, write, present: true, asSupervisor, execute, reserved: true);

        bool writable = (pde & PaeReadWrite) != 0;
        bool user = (pde & PaeUserSupervisor) != 0;
        bool executable = IsExecutable(pde);

        CheckPagePermissions(linear, write, writable, user, asSupervisor, execute, executable);

        ulong updated = pde | PaeAccessed | (write ? PaeDirty : 0);
        if (updated != pde)
        {
            // Only the low dword changes (A and D are bits 5 and 6); see
            // TranslatePae's own dirty-bit update for why that is enough.
            _memory.WriteDword(pdeAddress, (uint)updated);
        }

        // The page-aligned (low 12 bits zero) part of the frame: the PDE's
        // own bits 35:21 plus the linear address's bits 20:12, which is the
        // part of the 2 MB page's own offset a 4 KB-granular TLB slot still
        // needs to distinguish -- exactly as TranslateLargePage folds
        // linear[21:12] into its own 4 MB frame.
        ulong frame64 = (pde & PaeAddressMask2M) | (linear & 0x1FF000UL);
        bool openBus = IsOpenBusPhysicalAddress(frame64);
        if (openBus) AssertOpenBusPhysicalAddressIsSafe();

        entry.Valid = true;
        entry.Page = page;
        entry.Frame = openBus ? OpenBusPhysicalAddress : (uint)frame64;
        entry.Writable = writable;
        entry.User = user;
        entry.Dirty = (updated & PaeDirty) != 0;
        entry.Executable = executable;
        entry.TableEntryAddress = pdeAddress;
        entry.LargePageMask = PaeLargePageMask;
        entry.Global = (State.Cr4 & CpuState.Cr4Pge) != 0 && (pde & PaeGlobal) != 0;
        _largeEntriesCached = true;

        return entry.Frame | offset;
    }

    /// <summary>
    /// PeekLinear's PAE walk: the same by-hand, non-faulting inspection
    /// PeekLinear's legacy path does (Cpu.cs), through PAE's three levels
    /// instead. Absent at any level, or a malformed entry, reads as 0xFF --
    /// exactly PeekLinear's contract for a translation that would fault a
    /// real access -- and so does a translation whose own physical address
    /// needs bits this emulator's bus does not have, the same rule
    /// <see cref="TranslatePae"/> applies to an actual access.
    /// </summary>
    private byte PeekLinearPae(uint linear)
    {
        ulong pdpte = Pdpte[(linear >> 30) & 0x3];
        if ((pdpte & PaePresent) == 0) return 0xFF;

        ulong pdeAddress64 = (pdpte & PaeAddressMask4K) + ((linear >> 21) & 0x1FFu) * 8;
        ulong pde = ReadPagingStructureEntry(pdeAddress64);
        if ((pde & PaePresent) == 0) return 0xFF;

        if ((pde & PaePageSize) != 0)
        {
            if ((pde & Pae2MbPdeReservedMask) != 0) return 0xFF;

            ulong physical = (pde & PaeAddressMask2M) | (linear & 0x1FFFFFu);
            return IsOpenBusPhysicalAddress(physical) ? (byte)0xFF : _memory.ReadByte((uint)physical);
        }

        if ((pde & PaeEntryReservedHighMask) != 0) return 0xFF;

        ulong pteAddress64 = (pde & PaeAddressMask4K) + ((linear >> 12) & 0x1FFu) * 8;
        ulong pte = ReadPagingStructureEntry(pteAddress64);
        if ((pte & PaePresent) == 0) return 0xFF;
        if ((pte & PaePteReservedMask) != 0) return 0xFF;

        ulong finalPhysical = (pte & PaeAddressMask4K) | (linear & 0xFFFu);
        return IsOpenBusPhysicalAddress(finalPhysical) ? (byte)0xFF : _memory.ReadByte((uint)finalPhysical);
    }

    /// <summary>
    /// Whether an entry of a PAE or long-mode walk allows instruction
    /// fetches: XD (bit 63) counts only while EFER.NXE is set; otherwise the
    /// bit is reserved and a walk that met it has already faulted.
    /// </summary>
    private bool IsExecutable(ulong entry)
    {
        bool executeDisableEnabled = (State.Efer & CpuState.EferNoExecuteEnable) != 0;
        return !executeDisableEnabled || (entry & ExecuteDisableBit) == 0;
    }

    /// <summary>
    /// The single dispatch point between the three paging modes, used
    /// everywhere a linear address needs translating once
    /// <see cref="CpuState.PagingEnabled"/> already says paging is on:
    /// <see cref="Cpu.TranslateLinear"/>, the split-access helpers and the
    /// instruction-fetch cache (Cpu.cs). Long mode (EFER.LMA) comes first,
    /// since it keeps CR4.PAE set; CR4.PAE and CR0.PG changing always flush
    /// the TLB (Cpu.TwoByte.cs), so nothing here needs to worry about two
    /// walks disagreeing about an already-cached slot.
    /// </summary>
    /// <param name="execute">True for an instruction fetch; see <see cref="CheckPagePermissions"/>.</param>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private uint TranslateWhenPaging(uint linear, bool write, bool asSupervisor = false, bool execute = false)
    {
        if (State.LongModeActive)
        {
            return TranslateLongMode(linear, write, asSupervisor, execute);
        }

        if ((State.Cr4 & CpuState.Cr4Pae) != 0)
        {
            return TranslatePae(linear, write, asSupervisor, execute);
        }

        return TranslatePaged(linear, write, asSupervisor, execute);
    }
}

/// <summary>
/// A page fault (exception 14). It carries the address that caused it, which
/// the handler goes on to read from CR2, and the reason, which the processor
/// pushes as an error code.
/// </summary>
public sealed class PageFaultException(uint linearAddress, bool write, bool present, bool user = false, bool reserved = false,
                                        bool instructionFetch = false)
    : Exception($"Page fault at 0x{linearAddress:X8}")
{
    public uint LinearAddress { get; } = linearAddress;
    public bool Write { get; } = write;
    public bool Present { get; } = present;
    public bool User { get; } = user;

    /// <summary>
    /// Set when a reserved bit was found 1 in a paging-structure entry: a
    /// 4 MB PDE's PDE[21:13] under CR4.PSE (<see cref="Cpu.TranslateLargePage"/>),
    /// or under CR4.PAE any PDE/PTE's bits 63:36, or a 2 MB PDE's bits 20:13
    /// (<see cref="Cpu.TranslatePae"/>, <see cref="Cpu.TranslatePae2MbPage"/>).
    /// </summary>
    public bool Reserved { get; } = reserved;

    /// <summary>
    /// Set when the access was an instruction fetch and execute-disable is in
    /// force (PAE or long-mode paging with EFER.NXE): error code bit 4, I/D.
    /// </summary>
    public bool InstructionFetch { get; } = instructionFetch;

    /// <summary>
    /// The error code pushed onto the handler's stack: bit 0 says the page was
    /// present (so this is a protection violation rather than a missing page),
    /// bit 1 that the access was a write, bit 2 that it came from user code,
    /// bit 3 that a reserved bit was set in a paging-structure entry, bit 4
    /// that it was an instruction fetch under execute-disable.
    /// </summary>
    public uint ErrorCode =>
        (Present ? 1u : 0u) | (Write ? 2u : 0u) | (User ? 4u : 0u) | (Reserved ? 8u : 0u)
        | (InstructionFetch ? 16u : 0u);
}
