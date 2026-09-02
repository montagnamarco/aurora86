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

    private readonly uint _addressableBytes;
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

    public PhysicalMemoryBus(uint ramSize = 16 * 1024 * 1024, uint addressableBytes = 16 * 1024 * 1024)
    {
        if (ramSize == 0 || (ramSize & PageMask) != 0)
            throw new ArgumentException("RAM size must be a multiple of 4 KB.", nameof(ramSize));
        if (addressableBytes < ramSize)
            throw new ArgumentException("The addressable space cannot be smaller than the installed RAM.", nameof(addressableBytes));

        _ramSize = ramSize;
        _addressableBytes = addressableBytes;
        _pageCount = (int)(addressableBytes >> PageShift);

        // Unmanaged, zeroed allocation: guest RAM must not be relocated or
        // scanned by the GC, and a real machine comes up with memory cleared.
        _ram = (byte*)NativeMemory.AllocZeroed(ramSize);

        _pageRam = new byte*[_pageCount];
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
    /// Maps a ROM: the contents are copied into the underlying RAM and the
    /// pages are marked read-only. Reads therefore keep taking the fast path,
    /// which matters because a ROM is read constantly and written never, and
    /// writes are discarded without complaint.
    /// </summary>
    public void MapRom(uint physicalBase, ReadOnlySpan<byte> content)
    {
        uint length = AlignUp((uint)content.Length);
        ValidateRange(physicalBase, length);

        if (physicalBase + length > _ramSize)
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
        if (physicalBase + length > _ramSize)
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(uint address, byte value)
    {
        address = Translate(address);
        if (address >= _addressableBytes) return;

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
        if (physicalBase + (uint)length > _ramSize)
            throw new ArgumentOutOfRangeException(nameof(length), "The range extends past the installed RAM.");
        return new Span<byte>(_ram + physicalBase, length);
    }

    private static uint AlignUp(uint value) => (value + PageMask) & ~PageMask;

    private void ValidateRange(uint physicalBase, uint length)
    {
        if ((physicalBase & PageMask) != 0)
            throw new ArgumentException("The base address must be 4 KB aligned.", nameof(physicalBase));
        if (length == 0 || (length & PageMask) != 0)
            throw new ArgumentException("The length must be a multiple of 4 KB.", nameof(length));
        if (physicalBase + length > _addressableBytes)
            throw new ArgumentOutOfRangeException(nameof(length), "The range extends past the addressable space.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeMemory.Free(_ram);
    }
}
