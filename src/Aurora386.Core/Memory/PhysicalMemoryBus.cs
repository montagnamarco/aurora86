using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aurora386.Core.Memory;

/// <summary>
/// The 386's physical memory bus. The address space is divided into 4 KB
/// pages; each page is either plain RAM (a non-null pointer, taking the fast
/// path with no indirection at all) or a mapped device served by a handler.
///
/// The 386 addresses 32 bits, but a page table covering the full 4 GB would
/// need a million entries. Since the emulated machine has 16 MB of RAM and
/// nothing is mapped above it, the table covers only the first 16 MB;
/// everything higher reads 0xFF and swallows writes, exactly like a bus with
/// nothing connected to it.
/// </summary>
public sealed unsafe class PhysicalMemoryBus : IDisposable
{
    public const int PageShift = 12;
    public const int PageSize = 1 << PageShift;   // 4096
    public const uint PageMask = PageSize - 1;

    private readonly ulong _addressableBytes;
    private readonly int _pageCount;

    /// <summary>Pointer to the page's backing RAM, or null for MMIO and unmapped pages.</summary>
    private readonly byte*[] _pageRam;

    /// <summary>The device serving this page, when it is not plain RAM.</summary>
    private readonly IMemoryDevice?[] _pageDevice;

    /// <summary>The device's physical base, used to turn an address into a device-relative offset.</summary>
    private readonly uint[] _pageDeviceBase;

    /// <summary>Read-only pages (ROM): writes are silently discarded.</summary>
    private readonly bool[] _pageReadOnly;

    private readonly byte* _ram;
    private readonly uint _ramSize;
    private bool _disposed;

    /// <summary>
    /// Aliasing of the top of the address space onto the BIOS ROM.
    ///
    /// After reset the 386 fetches from 0xFFFFFFF0, right up against the 4 GB
    /// boundary, while the ROM actually sits in the last stretch of the first
    /// megabyte. On a real board it is chipset decoding that makes those two
    /// coincide. Here we get the same effect by translating addresses that
    /// land in the top megabyte of the 32-bit space, which avoids having to
    /// allocate a page table spanning 4 GB just to serve a handful of bytes.
    /// </summary>
    private uint _highAliasStart;
    private uint _highAliasTarget;
    private bool _highAliasEnabled;

    /// <summary>
    /// State of the A20 address line.
    ///
    /// The 8086 had only 20 address lines, so a segment:offset pair such as
    /// FFFF:0010 did not reach 0x100000: it wrapped around to 0x00000, and a
    /// surprising amount of software came to depend on that wrap. When the AT
    /// added a twenty-first line it deliberately left it disabled at power-on
    /// so as not to break that software, and ever since, turning it on has
    /// been the operating system's job.
    ///
    /// With A20 disabled, the 64 KB just above the first megabyte -- the High
    /// Memory Area -- becomes unreachable, and that is exactly the space
    /// DOS 6.22 relocates itself into with DOS=HIGH.
    ///
    /// A real PC therefore powers up with A20 <b>disabled</b>; enabling it is
    /// left to the operating system or to the extended memory manager.
    /// </summary>
    private bool _a20Enabled;

    /// <summary>Mask applied to every address: it clears bit 20 while A20 is disabled.</summary>
    private uint _addressMask = ~(1u << 20);

    /// <summary>
    /// Enables or disables the A20 line. Flipping it changes the meaning of
    /// every address above the first megabyte, so it must only ever be driven
    /// by the keyboard controller or by port 0x92, just as on real hardware.
    /// </summary>
    public bool A20Enabled
    {
        get => _a20Enabled;
        set
        {
            _a20Enabled = value;
            // Note carefully what the 8086 wrap actually does: it forces
            // BIT 20 to zero and leaves every other bit alone. It does not
            // truncate the address to 20 bits. Truncating would be a much
            // easier mask to write and is a classic emulator bug, because it
            // silently makes every byte of extended memory above 2 MB
            // unreachable while the low megabyte keeps working perfectly --
            // so the machine boots and then fails in bewildering ways much
            // later, once a memory manager starts using high RAM.
            _addressMask = value ? 0xFFFFFFFF : ~(1u << 20);
        }
    }

    public uint RamSize => _ramSize;

    /// <summary>
    /// Allocates the installed RAM and a separate table for decoded physical
    /// addresses. PCI apertures can reach the 4 GB boundary without adding
    /// that address space to guest RAM; the exclusive endpoint needs 33 bits.
    /// </summary>
    public PhysicalMemoryBus(uint ramSize = 16 * 1024 * 1024,
        ulong addressableBytes = 16 * 1024 * 1024)
    {
        if (ramSize == 0 || (ramSize & PageMask) != 0)
            throw new ArgumentException("RAM size must be a multiple of 4 KB.", nameof(ramSize));
        if (addressableBytes < ramSize)
            throw new ArgumentException("The addressable space cannot be smaller than the installed RAM.", nameof(addressableBytes));
        if ((addressableBytes & PageMask) != 0 || addressableBytes > (1UL << 32))
        {
            throw new ArgumentOutOfRangeException(nameof(addressableBytes),
                "The addressable space must be 4 KB aligned and no larger than 4 GB.");
        }

        _ramSize = ramSize;
        _addressableBytes = addressableBytes;
        _pageCount = (int)(addressableBytes >> PageShift);

        // Unmanaged, zeroed allocation: guest RAM must not be relocated or
        // scanned by the GC, and a real machine comes up with memory cleared.
        _ram = (byte*)NativeMemory.AllocZeroed(ramSize);

        _pageRam = new byte*[_pageCount];
        _pageWatchedRam = new byte*[_pageCount];
        _pageDevice = new IMemoryDevice?[_pageCount];
        _pageDeviceBase = new uint[_pageCount];
        _pageReadOnly = new bool[_pageCount];

        // By default every page within the installed RAM points straight at it.
        for (int i = 0; i < _pageCount; i++)
        {
            uint physical = (uint)i << PageShift;
            _pageRam[i] = physical < ramSize ? _ram + physical : null;
        }
    }

    /// <summary>
    /// Maps a device over a physical range, displacing whatever RAM was there
    /// -- the VGA framebuffer covering 0xA0000-0xBFFFF being the obvious case.
    /// </summary>
    public void MapDevice(uint physicalBase, uint length, IMemoryDevice device)
    {
        ValidateRange(physicalBase, length);
        int first = (int)(physicalBase >> PageShift);
        int count = (int)(length >> PageShift);

        for (int i = 0; i < count; i++)
        {
            int page = first + i;
            _pageRam[page] = null;              // forces the slow path through the handler
            _pageDevice[page] = device;
            _pageDeviceBase[page] = physicalBase;
            _pageReadOnly[page] = false;
        }
    }

    /// <summary>
    /// Leaves a range without a responding device: reads return FFh and
    /// writes disappear. The PC's unused option-ROM sockets are holes in
    /// its address space, even when more RAM is installed above one MB.
    /// </summary>
    public void Unmap(uint physicalBase, uint length)
    {
        ValidateRange(physicalBase, length);
        int first = (int)(physicalBase >> PageShift);
        int count = (int)(length >> PageShift);
        for (int page = first; page < first + count; page++)
        {
            _pageRam[page] = null;
            _pageWatchedRam[page] = null;
            _pageDevice[page] = null;
            _pageDeviceBase[page] = 0;
            _pageReadOnly[page] = false;
        }
    }

    /// <summary>
    /// Maps a ROM: the contents are copied into the underlying RAM and the
    /// pages are marked read-only. Reads therefore keep taking the fast path,
    /// which matters because a ROM is read constantly and written never, and
    /// Copies the whole of installed RAM out, for a snapshot.
    ///
    /// The page table is deliberately not part of this: it is rebuilt from the
    /// devices when a snapshot is restored, and a captured table would hold
    /// pointers into memory that no longer exists.
    /// </summary>
    public byte[] CaptureRam()
    {
        var copy = new byte[_ramSize];
        new ReadOnlySpan<byte>(_ram, (int)_ramSize).CopyTo(copy);
        return copy;
    }

    /// <summary>
    /// Wipes everything above the first megabyte, which is what the POST's
    /// count of extended memory does on a real machine: it writes a pattern
    /// to every location and reads it back, and nothing that was there
    /// survives. Software relies on that. HIMEM leaves a VDISK header at the
    /// one-megabyte mark while it is installed, and on the next boot refuses
    /// to install again if it still finds it -- a warm reset that kept the
    /// memory intact would leave the machine without extended memory.
    /// </summary>
    public void ClearExtendedMemory()
    {
        if (_ramSize > 0x100000)
            NativeMemory.Clear(_ram + 0x100000, (nuint)(_ramSize - 0x100000));
    }

    /// <summary>
    /// Puts back RAM captured by <see cref="CaptureRam"/>. The ROM pages are
    /// left alone: the firmware belongs to the emulator running now, not to
    /// the one that took the snapshot, and a snapshot restored into a newer
    /// build would otherwise quietly bring the old BIOS back with it.
    /// </summary>
    public void RestoreRam(ReadOnlySpan<byte> content)
    {
        if (content.Length != _ramSize)
            throw new ArgumentException("The snapshot was taken from a machine with different RAM.",
                                        nameof(content));

        for (int page = 0; page < _pageCount && ((uint)page << PageShift) < _ramSize; page++)
        {
            if (_pageReadOnly[page]) continue;
            uint at = (uint)page << PageShift;
            content.Slice((int)at, PageSize).CopyTo(new Span<byte>(_ram + at, PageSize));
        }
    }

    /// <summary>
    /// writes are discarded without complaint.
    /// </summary>
    public void MapRom(uint physicalBase, ReadOnlySpan<byte> content)
    {
        uint length = AlignUp((uint)content.Length);
        ValidateRange(physicalBase, length);

        if ((ulong)physicalBase + length > _ramSize)
            throw new ArgumentException("A ROM must lie within the installed RAM.", nameof(physicalBase));

        content.CopyTo(new Span<byte>(_ram + physicalBase, content.Length));

        int first = (int)(physicalBase >> PageShift);
        int count = (int)(length >> PageShift);
        for (int i = 0; i < count; i++)
        {
            int page = first + i;
            _pageRam[page] = _ram + ((uint)page << PageShift);
            _pageDevice[page] = null;
            _pageReadOnly[page] = true;
        }
    }

    /// <summary>
    /// Writes a word into a mapped ROM, bypassing the read-only marking.
    ///
    /// The guest cannot do this and must not be able to: a ROM that the
    /// running program can change is not a ROM. But the emulator is standing
    /// in for the machine's manufacturer, and there are a few bytes in the
    /// BIOS image that a real board would have had in a socket or a latch
    /// rather than in mask ROM -- a vector a resident service needs to reach,
    /// a value fixed when the board was built. Writing them from outside is
    /// the honest way to model that, and much simpler than mapping a second
    /// device for eight bytes.
    /// </summary>
    public void WriteRomWord(uint address, ushort value)
    {
        if ((ulong)address + sizeof(ushort) > _ramSize)
            throw new ArgumentOutOfRangeException(nameof(address));

        _ram[address] = (byte)value;
        _ram[address + 1] = (byte)(value >> 8);
    }

    /// <summary>
    /// Makes addresses from <paramref name="aliasStart"/> to the top of the
    /// 32-bit space be served starting at <paramref name="target"/>. This is
    /// what puts the ROM under the 386's reset vector.
    /// </summary>
    public void MapHighAlias(uint aliasStart, uint target)
    {
        _highAliasStart = aliasStart;
        _highAliasTarget = target;
        _highAliasEnabled = true;
    }

    /// <summary>
    /// Translates a physical address before every access.
    ///
    /// The order of these two steps matters, and it is not the order one
    /// would naturally write. The high alias -- the ROM window near the 4 GB
    /// boundary that the 386 starts fetching from after reset -- is decoded by
    /// the chipset <b>before</b> A20 gets a say. Those addresses have bit 20
    /// set, so if A20 masking were applied first, no PC could ever boot with
    /// the line disabled, which is precisely the state a real PC powers up in.
    ///
    /// A20 masking therefore only applies to addresses the running program
    /// actually generates, never to the reset-vector aliasing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Translate(uint address)
    {
        // High alias: decoded first, and never masked.
        if (_highAliasEnabled && address >= _highAliasStart)
            return _highAliasTarget + (address - _highAliasStart);

        return address & _addressMask;
    }

    /// <summary>Restores a range to ordinary writable RAM.</summary>
    public void MapRam(uint physicalBase, uint length)
    {
        ValidateRange(physicalBase, length);
        if ((ulong)physicalBase + length > _ramSize)
            throw new ArgumentException("The range extends past the installed RAM.", nameof(physicalBase));

        int first = (int)(physicalBase >> PageShift);
        int count = (int)(length >> PageShift);
        for (int i = 0; i < count; i++)
        {
            int page = first + i;
            _pageRam[page] = _ram + ((uint)page << PageShift);
            _pageDevice[page] = null;
            _pageReadOnly[page] = false;
        }
    }

    // ------------------------------------------------------------------ reads

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte(uint address)
    {
        address = Translate(address);
        if (address >= _addressableBytes) return 0xFF;

        byte* page = _pageRam[address >> PageShift];
        if (page is not null)
            return page[address & PageMask];

        return ReadByteSlow(address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadWord(uint address)
    {
        address = Translate(address);

        // Fast path: the access does not straddle a page boundary and the
        // page is RAM. Unaligned accesses are perfectly legal on x86, so we
        // cannot assume anything and must check that both bytes fall inside
        // the same page.
        if (address < _addressableBytes && (address & PageMask) <= PageMask - 1)
        {
            byte* page = _pageRam[address >> PageShift];
            if (page is not null)
                return Unsafe.ReadUnaligned<ushort>(page + (address & PageMask));
        }
        return ReadWordSlow(address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadDword(uint address)
    {
        address = Translate(address);

        if (address < _addressableBytes && (address & PageMask) <= PageMask - 3)
        {
            byte* page = _pageRam[address >> PageShift];
            if (page is not null)
                return Unsafe.ReadUnaligned<uint>(page + (address & PageMask));
        }
        return ReadDwordSlow(address);
    }

    private byte ReadByteSlow(uint address)
    {
        int index = (int)(address >> PageShift);

        // A RAM page set aside for a read watch: report the read if it is
        // inside a range, and serve the byte from where the page really is.
        byte* watched = _pageWatchedRam[index];
        if (watched is not null)
        {
            if (IsReadWatched(address)) ReadWatched?.Invoke(address);
            return watched[address & PageMask];
        }

        IMemoryDevice? device = _pageDevice[index];
        return device is null ? (byte)0xFF : device.ReadByte(address - _pageDeviceBase[index]);
    }

    /// <summary>
    /// Reads a byte from an <b>already translated</b> address. Used only by
    /// the slow paths, which received their address from Translate() and must
    /// not run it through translation a second time.
    /// </summary>
    private byte ReadByteTranslated(uint address)
    {
        if (address >= _addressableBytes) return 0xFF;

        byte* page = _pageRam[address >> PageShift];
        if (page is not null) return page[address & PageMask];

        return ReadByteSlow(address);
    }

    private void WriteByteTranslated(uint address, byte value)
    {
        if (address >= _addressableBytes) return;

        uint index = address >> PageShift;
        byte* page = _pageRam[index];
        if (page is not null)
        {
            if (_pageReadOnly[index]) return;
            page[address & PageMask] = value;
            return;
        }
        WriteByteSlow(address, value);
    }

    private ushort ReadWordSlow(uint address)
    {
        int index = (int)(address >> PageShift);
        // An access that lies entirely within one MMIO page must be served by
        // a single call: some registers have side effects on read -- advancing
        // an index, clearing a latch -- and splitting the access into two byte
        // reads would trigger them twice.
        if (address < _addressableBytes && (address & PageMask) <= PageMask - 1)
        {
            IMemoryDevice? device = _pageDevice[index];
            if (device is not null)
                return device.ReadWord(address - _pageDeviceBase[index]);
        }
        return (ushort)(ReadByteTranslated(address)
                      | (ReadByteTranslated(address + 1) << 8));
    }

    private uint ReadDwordSlow(uint address)
    {
        int index = (int)(address >> PageShift);
        if (address < _addressableBytes && (address & PageMask) <= PageMask - 3)
        {
            IMemoryDevice? device = _pageDevice[index];
            if (device is not null)
                return device.ReadDword(address - _pageDeviceBase[index]);
        }
        return (uint)(ReadWordSlow(address) | (ReadWordSlow(address + 2) << 16));
    }

    // ----------------------------------------------------------------- writes

    /// <summary>Diagnostics: a single physical address to report writes to, or uint.MaxValue for none.</summary>
    public uint WatchAddress = uint.MaxValue;

    /// <summary>How many bytes from <see cref="WatchAddress"/> are watched; one by default.</summary>
    public uint WatchLength = 1;
    public event Action<uint, byte>? Watched;

    /// <summary>
    /// Extra watched ranges, for a debugger that wants several at once.
    ///
    /// The single range above is kept as it is because it costs one
    /// subtraction and one comparison on the write path, and that path runs
    /// tens of millions of times a second. This list is consulted only when
    /// it is not empty, so a machine with no watchpoints pays one null-ish
    /// test; the moment somebody sets one, every write pays for the walk,
    /// which is the honest price of watching memory in an interpreter.
    /// </summary>
    private (uint Start, uint Length)[] _watchRanges = [];

    /// <summary>Replaces the set of extra watched ranges.</summary>
    public void SetWatchRanges(IEnumerable<(uint Start, uint Length)> ranges)
        => _watchRanges = ranges.Where(r => r.Length > 0).ToArray();

    /// <summary>True when any extra range covers this address.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool InWatchRanges(uint address)
    {
        (uint Start, uint Length)[] ranges = _watchRanges;
        for (int i = 0; i < ranges.Length; i++)
            if (address - ranges[i].Start < ranges[i].Length) return true;
        return false;
    }

    /// <summary>True when this address is watched at all, by either mechanism.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsWatched(uint address, uint extra)
        => address - WatchAddress < WatchLength + extra
        || (_watchRanges.Length > 0 && InWatchRanges(address));

    /// <summary>
    /// Ranges watched for reads, kept apart from the write ranges because
    /// watching a byte for reads and for writes are different questions:
    /// "who overwrote this vector" and "who consults this table" rarely
    /// have the same answer, and a list that mixed them would report both.
    /// </summary>
    private (uint Start, uint Length)[] _readWatchRanges = [];

    /// <summary>
    /// RAM pointers set aside for pages under a read watch. A watched page
    /// is taken out of the fast table so that every access to it falls into
    /// the slow path, which is the one place that can afford to look.
    /// </summary>
    private readonly byte*[] _pageWatchedRam;

    /// <summary>
    /// Replaces the set of ranges watched for reads.
    ///
    /// Reads are the hottest path in the interpreter, and the first version
    /// of this checked the ranges on every one of them -- an array length
    /// and a comparison, the same the write path pays, and surely cheap.
    /// Measured, it cost between four and seven per cent of the whole
    /// machine, for a feature used once a month. So the fast path is left
    /// exactly as it was, and a watched page is instead unmapped from it:
    /// accesses to that page take the slow path, which checks the ranges
    /// and serves the byte from the pointer set aside. Nothing is paid
    /// anywhere else, which is the only acceptable price on this path.
    /// </summary>
    public void SetReadWatchRanges(IEnumerable<(uint Start, uint Length)> ranges)
    {
        // Give every page back first, then take out the ones now covered.
        for (int i = 0; i < _pageCount; i++)
        {
            if (_pageWatchedRam[i] is null) continue;
            _pageRam[i] = _pageWatchedRam[i];
            _pageWatchedRam[i] = null;
        }

        _readWatchRanges = ranges.Where(r => r.Length > 0).ToArray();

        foreach ((uint start, uint length) in _readWatchRanges)
        {
            long first = start >> PageShift;
            long last = (start + length - 1) >> PageShift;
            for (long page = first; page <= last && page < _pageCount; page++)
            {
                if (_pageRam[page] is null) continue;       // a device, or nothing: already slow
                _pageWatchedRam[page] = _pageRam[page];
                _pageRam[page] = null;
            }
        }
    }

    /// <summary>
    /// Raised for every byte read inside a read-watched range, on the
    /// emulation thread and inside the read itself.
    ///
    /// Instruction fetches come this way too. The processor's fetch cache
    /// keeps only the page translation and still reads each byte through
    /// the bus, so a watch on a page of code reports the processor running
    /// it, byte by byte, as well as anything reading it as data. That makes
    /// such a watch noisy -- and it also means "who executes this" is a
    /// question this can answer, which no other watch can.
    /// </summary>
    public event Action<uint>? ReadWatched;

    private bool IsReadWatched(uint address)
    {
        (uint Start, uint Length)[] ranges = _readWatchRanges;
        for (int i = 0; i < ranges.Length; i++)
            if (address - ranges[i].Start < ranges[i].Length) return true;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(uint address, byte value)
    {
        address = Translate(address);
        if (address >= _addressableBytes) return;
        if (IsWatched(address, 0)) Watched?.Invoke(address, value);

        uint index = address >> PageShift;
        byte* page = _pageRam[index];
        if (page is not null)
        {
            if (_pageReadOnly[index]) return;   // write to ROM: discarded
            page[address & PageMask] = value;
            return;
        }
        WriteByteSlow(address, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteWord(uint address, ushort value)
    {
        address = Translate(address);

        // Inside a watched range the write goes byte by byte, so that it is
        // reported like any other.
        if (IsWatched(address, 1))
        {
            WriteByte(address, (byte)value);
            WriteByte(address + 1, (byte)(value >> 8));
            return;
        }

        if (address < _addressableBytes && (address & PageMask) <= PageMask - 1)
        {
            uint index = address >> PageShift;
            byte* page = _pageRam[index];
            if (page is not null)
            {
                if (_pageReadOnly[index]) return;
                Unsafe.WriteUnaligned(page + (address & PageMask), value);
                return;
            }
        }
        WriteWordSlow(address, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteDword(uint address, uint value)
    {
        address = Translate(address);

        if (IsWatched(address, 3))
        {
            for (uint i = 0; i < 4; i++) WriteByte(address + i, (byte)(value >> (int)(8 * i)));
            return;
        }

        if (address < _addressableBytes && (address & PageMask) <= PageMask - 3)
        {
            uint index = address >> PageShift;
            byte* page = _pageRam[index];
            if (page is not null)
            {
                if (_pageReadOnly[index]) return;
                Unsafe.WriteUnaligned(page + (address & PageMask), value);
                return;
            }
        }
        WriteDwordSlow(address, value);
    }

    private void WriteByteSlow(uint address, byte value)
    {
        int index = (int)(address >> PageShift);

        // A page set aside for a read watch is still RAM, and still ROM if
        // it was ROM: the watch changes which path an access takes, not
        // what the page is.
        byte* watched = _pageWatchedRam[index];
        if (watched is not null)
        {
            if (!_pageReadOnly[index]) watched[address & PageMask] = value;
            return;
        }

        _pageDevice[index]?.WriteByte(address - _pageDeviceBase[index], value);
    }

    private void WriteWordSlow(uint address, ushort value)
    {
        int index = (int)(address >> PageShift);
        if (address < _addressableBytes && (address & PageMask) <= PageMask - 1)
        {
            IMemoryDevice? device = _pageDevice[index];
            if (device is not null)
            {
                device.WriteWord(address - _pageDeviceBase[index], value);
                return;
            }
        }
        WriteByteTranslated(address, (byte)value);
        WriteByteTranslated(address + 1, (byte)(value >> 8));
    }

    private void WriteDwordSlow(uint address, uint value)
    {
        int index = (int)(address >> PageShift);
        if (address < _addressableBytes && (address & PageMask) <= PageMask - 3)
        {
            IMemoryDevice? device = _pageDevice[index];
            if (device is not null)
            {
                device.WriteDword(address - _pageDeviceBase[index], value);
                return;
            }
        }
        WriteWordSlow(address, (ushort)value);
        WriteWordSlow(address + 2, (ushort)(value >> 16));
    }

    // ---------------------------------------------------------------- utilities

    /// <summary>
    /// Direct access to RAM for DMA and for loading disk images, neither of
    /// which should go through the page-mapping logic.
    /// </summary>
    public Span<byte> GetRamSpan(uint physicalBase, int length)
    {
        // Direct host pointers must be bounded by installed RAM, even when
        // the bus can decode PCI windows all the way up to 4 GB.
        if (length < 0 || (ulong)physicalBase + (uint)length > _ramSize)
        {
            throw new ArgumentOutOfRangeException(nameof(length),
                "The range extends past the installed RAM.");
        }

        return new Span<byte>(_ram + physicalBase, length);
    }

    private static uint AlignUp(uint value) => (value + PageMask) & ~PageMask;

    private void ValidateRange(uint physicalBase, uint length)
    {
        if ((physicalBase & PageMask) != 0)
            throw new ArgumentException("The base address must be 4 KB aligned.", nameof(physicalBase));
        if (length == 0 || (length & PageMask) != 0)
            throw new ArgumentException("The length must be a multiple of 4 KB.", nameof(length));
        // The final PCI aperture can end exactly at 4 GB. Add in 64 bits
        // before touching any page entry, so an overflowing range cannot
        // wrap to zero and partially replace a valid mapping.
        if ((ulong)physicalBase + length > _addressableBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(length),
                "The range extends past the addressable space.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeMemory.Free(_ram);
    }
}
