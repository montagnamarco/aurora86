namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    /// <summary>
    /// Linear -> physical translation through the 386's page directory.
    ///
    /// Two levels: the top 10 bits index the directory, the next 10 index the
    /// page table, and the low 12 are the offset within the 4 KB page.
    ///
    /// There is no TLB yet, so every access costs two extra memory reads. That
    /// is acceptable while paging is off the hot path (in real mode this is
    /// never called at all), but a TLB will have to arrive alongside full
    /// protected mode support: Windows 3.1 in enhanced mode comes through here
    /// on every single memory access.
    /// </summary>
    private uint TranslatePaged(uint linear, bool write)
    {
        uint directoryIndex = linear >> 22;
        uint tableIndex = (linear >> 12) & 0x3FF;
        uint offset = linear & 0xFFF;

        uint directoryEntry = _memory.ReadDword((State.Cr3 & 0xFFFFF000) + directoryIndex * 4);
        if ((directoryEntry & PagePresent) == 0)
            throw new PageFaultException(linear, write, present: false);

        uint tableEntry = _memory.ReadDword((directoryEntry & 0xFFFFF000) + tableIndex * 4);
        if ((tableEntry & PagePresent) == 0)
            throw new PageFaultException(linear, write, present: false);

        // The accessed and dirty bits have to be written back the way real
        // hardware does: operating systems read them to decide which pages are
        // worth evicting and which ones must be written out to swap first.
        uint updated = tableEntry | PageAccessed | (write ? PageDirty : 0);
        if (updated != tableEntry)
            _memory.WriteDword((directoryEntry & 0xFFFFF000) + tableIndex * 4, updated);

        return (tableEntry & 0xFFFFF000) | offset;
    }

    private const uint PagePresent = 1u << 0;
    private const uint PageWritable = 1u << 1;
    private const uint PageUser = 1u << 2;
    private const uint PageAccessed = 1u << 5;
    private const uint PageDirty = 1u << 6;
}

/// <summary>
/// A page fault (exception 14). It carries the address that caused it, which
/// the handler will go on to read from CR2.
/// </summary>
public sealed class PageFaultException(uint linearAddress, bool write, bool present)
    : Exception($"Page fault at 0x{linearAddress:X8}")
{
    public uint LinearAddress { get; } = linearAddress;
    public bool Write { get; } = write;
    public bool Present { get; } = present;
}
