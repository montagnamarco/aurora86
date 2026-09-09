// Paging: the two-level walk from a linear address to a physical one, the
// TLB that caches it, the accessed and dirty bits, and the faults --
// including write protection at ring 0, which the 486 honours when CR0.WP
// is set and the 386 did not. Only on when CR0.PG is set, which for the
// guests here means Windows in enhanced mode.

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
    /// entry is not writable.
    /// </summary>
    private uint TranslatePaged(uint linear, bool write)
    {
        uint page = linear & 0xFFFFF000;
        uint offset = linear & 0xFFF;

        // The TLB caches the translation and the permissions that came with
        // it, which is what makes paging affordable: without it every access
        // would cost two extra memory reads, and in enhanced mode every
        // access comes through here.
        ref TlbEntry entry = ref _tlb[(page >> 12) & (TlbSize - 1)];
        if (entry.Valid && entry.Page == page)
        {
            CheckPagePermissions(linear, write, entry.Writable, entry.User);

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
            throw new PageFaultException(linear, write, present: false, user: State.Cpl == 3);

        uint tableEntryAddress = (directoryEntry & 0xFFFFF000) + tableIndex * 4;
        uint tableEntry = _memory.ReadDword(tableEntryAddress);
        if ((tableEntry & PagePresent) == 0)
            throw new PageFaultException(linear, write, present: false, user: State.Cpl == 3);

        // Permissions are the AND of the two levels, not just the page's own.
        bool writable = (directoryEntry & PageWritable) != 0 && (tableEntry & PageWritable) != 0;
        bool user = (directoryEntry & PageUser) != 0 && (tableEntry & PageUser) != 0;

        CheckPagePermissions(linear, write, writable, user);

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
        entry.TableEntryAddress = tableEntryAddress;

        return entry.Frame | offset;
    }

    /// <summary>
    /// Raises a page fault when the access is not allowed by the page's own
    /// permission bits, as opposed to the page simply not being there.
    /// </summary>
    private void CheckPagePermissions(uint linear, bool write, bool writable, bool user)
    {
        bool userMode = State.Cpl == 3;

        // Supervisor-only pages are invisible to ring 3. This is the whole
        // basis of keeping the kernel safe from applications.
        if (userMode && !user)
            throw new PageFaultException(linear, write, present: true, user: true);

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
    /// </summary>
    public void FlushTlb()
    {
        for (int i = 0; i < _tlb.Length; i++) _tlb[i].Valid = false;
        InvalidateFetchPage();
    }

    /// <summary>
    /// Drops one page's cached translation, as the INVLPG instruction does.
    /// </summary>
    public void InvalidatePage(uint linear)
    {
        uint page = linear & 0xFFFFF000;
        ref TlbEntry entry = ref _tlb[(page >> 12) & (TlbSize - 1)];
        if (entry.Valid && entry.Page == page) entry.Valid = false;
        InvalidateFetchPage();
    }

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
        public uint Frame;
        public bool Writable;
        public bool User;
        public bool Dirty;
        public uint TableEntryAddress;
    }

    // 1024 entries: enhanced-mode Windows works across the monitor's pages
    // high in the address space, the applications' and the display's, and
    // with 64 entries the useful translations kept evicting one another.
    private const int TlbSize = 1024;
    private readonly TlbEntry[] _tlb = new TlbEntry[TlbSize];

    private const uint PagePresent = 1u << 0;
    private const uint PageWritable = 1u << 1;
    private const uint PageUser = 1u << 2;
    private const uint PageAccessed = 1u << 5;
    private const uint PageDirty = 1u << 6;
}

/// <summary>
/// A page fault (exception 14). It carries the address that caused it, which
/// the handler goes on to read from CR2, and the reason, which the processor
/// pushes as an error code.
/// </summary>
public sealed class PageFaultException(uint linearAddress, bool write, bool present, bool user = false)
    : Exception($"Page fault at 0x{linearAddress:X8}")
{
    public uint LinearAddress { get; } = linearAddress;
    public bool Write { get; } = write;
    public bool Present { get; } = present;
    public bool User { get; } = user;

    /// <summary>
    /// The error code pushed onto the handler's stack: bit 0 says the page was
    /// present (so this is a protection violation rather than a missing page),
    /// bit 1 that the access was a write, bit 2 that it came from user code.
    /// </summary>
    public uint ErrorCode =>
        (Present ? 1u : 0u) | (Write ? 2u : 0u) | (User ? 4u : 0u);
}
