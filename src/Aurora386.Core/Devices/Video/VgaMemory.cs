using Aurora386.Core.Memory;

namespace Aurora386.Core.Devices.Video;

/// <summary>
/// Video memory, mapped between 0xA0000 and 0xBFFFF.
///
/// The region is split into three windows with quite different uses:
///   0xA0000-0xAFFFF  graphics modes
///   0xB0000-0xB7FFF  monochrome text (MDA/Hercules)
///   0xB8000-0xBFFFF  colour text (CGA/EGA/VGA)
///
/// <para><b>The four planes.</b> Physically the VGA has four 64 KB memory
/// chips, all wired to the same address lines. A CPU address does not select
/// one byte: it selects one byte <i>in each of the four planes at the same
/// time</i>. What happens to those four bytes on a read or a write is decided
/// entirely by the Sequencer and Graphics Controller registers, and that is
/// the whole trick of 16-colour VGA programming.</para>
///
/// <para>Why do it this way at all? A 16-colour pixel needs four bits. Packing
/// two pixels into a byte -- the obvious layout -- would mean the CPU has to
/// read-modify-write to change one pixel, and a 640x480 screen would be 150 KB
/// that all has to cross an 8-bit bus. The planar layout instead makes one CPU
/// byte cover eight horizontally adjacent pixels in one bit position of their
/// colour. A solid fill then costs one write per eight pixels, with all four
/// planes updated in parallel by the hardware, and 640x480 fits in four 37.5 KB
/// planes that the CPU addresses through a single 64 KB window.</para>
///
/// <para><b>The latches.</b> Every CPU read from the graphics window loads all
/// four planes into four internal 8-bit latch registers, regardless of what the
/// CPU actually receives back. The latches then act as the "background" for the
/// next write: bits the Bit Mask protects come from the latches, not from
/// memory. This is why the classic VGA read-modify-write idiom is a dummy read
/// followed by a write -- the read is not for the CPU's benefit, it is to load
/// the latches. Getting the latches wrong produces the single most confusing
/// class of VGA bug: pixels that come back as whatever was last read from a
/// completely different address.</para>
///
/// Every write marks the affected row as dirty. Reconverting the whole screen
/// each frame would be almost entirely wasted work: a DOS prompt typically
/// changes a handful of cells between one frame and the next.
/// </summary>
public sealed class VgaMemory : IMemoryDevice
{
    public const uint Base = 0xA0000;
    public const uint Size = 0x20000;    // 128 KB visible through the window

    /// <summary>Offset of the colour text window within this device.</summary>
    public const uint TextWindowOffset = 0x18000;   // 0xB8000 - 0xA0000

    /// <summary>Bytes in one plane. 64 KB each, four of them, 256 KB total.</summary>
    public const int PlaneSize = 64 * 1024;

    /// <summary>
    /// The four planes, laid out end to end: plane N occupies
    /// [N * PlaneSize, (N+1) * PlaneSize).
    ///
    /// Text modes and mode 13h also live in here. Text uses plane 0 for
    /// characters and plane 1 for attributes on real hardware, but since the
    /// CPU sees them interleaved through the odd/even mapping we keep the
    /// simple interleaved layout in plane 0's space -- it is what every other
    /// part of this emulator already assumes, and it costs nothing.
    /// </summary>
    private readonly byte[] _vram = new byte[4 * PlaneSize];

    /// <summary>
    /// The four latch registers, one per plane. Loaded on every CPU read from
    /// the graphics window; consumed by every planar write.
    /// </summary>
    private readonly byte[] _latches = new byte[4];

    /// <summary>
    /// Dirty tracking, in scan-line-ish granularity. 512 slots covers a text
    /// row count (25/43/50) as well as a 480-line graphics mode when rows are
    /// bucketed; see <see cref="MarkDirty"/>.
    /// </summary>
    private readonly bool[] _dirtyRows = new bool[512];

    private bool _anyDirty = true;

    public VgaRegisters Registers { get; }

    public VgaMemory(VgaRegisters registers) => Registers = registers;

    /// <summary>The latch contents, for tests and for the debugger.</summary>
    public ReadOnlySpan<byte> Latches => _latches;

    /// <summary>
    /// True when the CPU window at 0xA0000 currently behaves as four bit
    /// planes. Text modes and mode 13h both bypass the planar path: text
    /// because it lives in the 0xB8000 window, mode 13h because the sequencer
    /// chains the planes into one linear space.
    /// </summary>
    private bool PlanarWindow(uint offset)
        => offset < 0x10000
        && Registers.EffectiveMode.Layout == VgaMemoryLayout.Planar4;

    /// <summary>
    /// Mode 13h's chained window. Consecutive CPU addresses walk plane 0, 1,
    /// 2, 3, 0, ... but since we store mode 13h pixels contiguously in plane
    /// 0's region, the chaining is invisible and a plain linear access is
    /// correct. Keeping this as its own branch documents the difference
    /// instead of hiding it behind an accident of layout.
    /// </summary>
    private bool LinearWindow(uint offset)
        => offset < 0x10000
        && Registers.EffectiveMode.Layout == VgaMemoryLayout.Linear8;

    public byte ReadByte(uint offset)
    {
        if (offset >= Size) return 0xFF;

        if (PlanarWindow(offset))
        {
            // Every read loads the latches, whatever the read mode. This
            // happens before the value the CPU gets is decided.
            LoadLatches(offset);

            return Registers.ReadMode == 0
                ? ReadMode0()
                : ReadMode1();
        }

        return _vram[offset];
    }

    private void LoadLatches(uint offset)
    {
        for (int plane = 0; plane < 4; plane++)
            _latches[plane] = _vram[plane * PlaneSize + offset];
    }

    /// <summary>
    /// Read mode 0: the CPU simply gets one plane back, chosen by Read Map
    /// Select. To read a whole pixel's colour you have to do this four times,
    /// once per plane -- which is why reading pixels on a planar VGA is so
    /// much slower than writing them.
    /// </summary>
    private byte ReadMode0() => _latches[Registers.Graphics(VgaRegisters.GcReadMapSelect) & 3];

    /// <summary>
    /// Read mode 1, "colour compare": each returned bit answers the question
    /// "is the pixel at this position equal to the Color Compare value?" for
    /// all eight pixels at once. Planes listed in Color Don't Care are excluded
    /// from the comparison, so you can ask about a subset of the bits.
    ///
    /// This exists because the alternative -- four reads plus bit shuffling per
    /// pixel -- makes operations like flood fill and transparent blitting
    /// unusably slow. One read here answers eight pixels.
    /// </summary>
    private byte ReadMode1()
    {
        int compare = Registers.Graphics(VgaRegisters.GcColorCompare) & 0x0F;
        int dontCare = Registers.Graphics(VgaRegisters.GcColorDontCare) & 0x0F;

        byte result = 0xFF;
        for (int plane = 0; plane < 4; plane++)
        {
            // A plane whose Don't Care bit is clear takes no part in the test.
            if ((dontCare & (1 << plane)) == 0) continue;

            // Where the plane's bit already matches the wanted bit, the pixel
            // is still a candidate; elsewhere it is ruled out. Expanding the
            // single compare bit to all eight positions turns the per-pixel
            // test into one XOR.
            byte wanted = (compare & (1 << plane)) != 0 ? (byte)0xFF : (byte)0x00;
            result &= (byte)~(_latches[plane] ^ wanted);
        }
        return result;
    }

    public void WriteByte(uint offset, byte value)
    {
        if (offset >= Size) return;

        if (PlanarWindow(offset))
        {
            WritePlanar(offset, value);
            return;
        }

        if (LinearWindow(offset))
        {
            // Mode 13h honours the Map Mask too on real hardware, but the BIOS
            // sets it to 0x0F and essentially every program leaves it there.
            if (_vram[offset] == value) return;
            _vram[offset] = value;
            MarkDirty(offset);
            return;
        }

        if (_vram[offset] == value) return;    // no visible change, so no dirty row

        _vram[offset] = value;
        MarkDirty(offset);
    }

    /// <summary>
    /// The planar write path: one CPU byte, up to four planes updated.
    ///
    /// Four registers decide the outcome, and they apply in this order:
    ///   1. the <b>write mode</b> (Graphics 0x05 bits 0-1) picks the source of
    ///      each plane's eight bits;
    ///   2. the <b>raster op</b> (Graphics 0x03 bits 3-4) combines that source
    ///      with the latch -- AND, OR, XOR or plain replace;
    ///   3. the <b>bit mask</b> (Graphics 0x08) decides, bit by bit, whether
    ///      the result or the untouched latch content survives;
    ///   4. the <b>map mask</b> (Sequencer 0x02) decides which planes are
    ///      written to memory at all.
    ///
    /// Steps 3 and 4 are easy to confuse. The bit mask works <i>horizontally</i>
    /// -- which of the eight pixels this byte covers may change. The map mask
    /// works <i>vertically through the planes</i> -- which bits of those pixels'
    /// colour may change. Together they let a program set, say, only the blue
    /// bit of the middle two pixels of a byte.
    /// </summary>
    private void WritePlanar(uint offset, byte value)
    {
        byte mapMask = Registers.MapMask;
        byte bitMask = Registers.Graphics(VgaRegisters.GcBitMask);
        int writeMode = Registers.WriteMode;

        byte dataRotate = Registers.Graphics(VgaRegisters.GcDataRotate);
        int rotate = dataRotate & 0x07;
        int rasterOp = (dataRotate >> 3) & 0x03;

        byte setReset = (byte)(Registers.Graphics(VgaRegisters.GcSetReset) & 0x0F);
        byte enableSetReset = (byte)(Registers.Graphics(VgaRegisters.GcEnableSetReset) & 0x0F);

        // Write mode 3 uses the CPU byte as the bit mask itself, ANDed with the
        // register. See the per-mode comments below.
        byte effectiveBitMask = bitMask;

        // Mode 0 and mode 3 rotate the CPU data first. The rotate exists so
        // that a program shifting a bitmap across a byte boundary does not have
        // to do the shifting itself -- the hardware was faster than an 8086.
        byte rotated = (byte)((value >> rotate) | (value << (8 - rotate)));
        if (rotate == 0) rotated = value;

        if (writeMode == 3)
        {
            // Write mode 3 is the "paint with one colour through a stencil"
            // mode, and it is the reason a fast VGA line or text routine is
            // short. Set/Reset supplies the colour for all four planes, and the
            // CPU byte is not data at all: it is a stencil ANDed with the Bit
            // Mask register, so one write paints an arbitrary subset of eight
            // pixels in a preset colour. Drawing a font glyph becomes: set the
            // colour once, then write one glyph row byte per scanline.
            effectiveBitMask = (byte)(bitMask & rotated);
        }

        for (int plane = 0; plane < 4; plane++)
        {
            if ((mapMask & (1 << plane)) == 0) continue;   // this plane is not selected

            byte source = writeMode switch
            {
                // Write mode 0: the general-purpose one. Each plane takes
                // either the CPU byte or eight copies of that plane's Set/Reset
                // bit, depending on Enable Set/Reset. Enabling Set/Reset for
                // all four planes is how you write a solid colour without the
                // CPU having to compute per-plane bit patterns.
                0 => (enableSetReset & (1 << plane)) != 0
                        ? Expand(setReset, plane)
                        : rotated,

                // Write mode 1: a pure latch copy. The CPU byte is ignored
                // entirely and all four latches go straight back to memory.
                // This is the fastest possible screen-to-screen copy on a VGA:
                // read a byte (loading the latches), write it back somewhere
                // else, and eight pixels of all four planes move at one byte
                // per eight pixels. Note that mode 1 also bypasses the raster
                // op and the bit mask on real hardware.
                1 => _latches[plane],

                // Write mode 2: the CPU byte's low four bits are one pixel's
                // colour, replicated across the whole byte per plane. Combined
                // with a bit mask that selects a single pixel, this is the
                // canonical "plot one pixel in colour N" write.
                2 => Expand(value, plane),

                // Write mode 3: colour always comes from Set/Reset; the CPU
                // byte was already folded into the bit mask above.
                _ => Expand(setReset, plane),
            };

            byte latch = _latches[plane];

            if (writeMode == 1)
            {
                // Straight latch copy: no raster op, no bit mask.
                StorePlane(plane, offset, source);
                continue;
            }

            // The raster op combines the source with the latch. XOR is the one
            // that earns its keep: it is how you draw and erase a rubber-band
            // selection box without saving what was underneath.
            byte combined = rasterOp switch
            {
                1 => (byte)(source & latch),
                2 => (byte)(source | latch),
                3 => (byte)(source ^ latch),
                _ => source,
            };

            // Finally the bit mask: a set bit takes the computed value, a clear
            // bit keeps whatever the latch held. Note it keeps the *latch*, not
            // what is in memory -- if the latches were never loaded by a read,
            // the protected pixels get stale data, and this is exactly the bug
            // that makes people say "you must always read before you write".
            byte result = (byte)((combined & effectiveBitMask) | (latch & ~effectiveBitMask));

            StorePlane(plane, offset, result);
        }

        MarkDirty(offset);

        // Replicating one nibble bit across a whole byte: plane N's bit of the
        // colour becomes all eight pixels' bit N.
        static byte Expand(byte color, int plane)
            => (color & (1 << plane)) != 0 ? (byte)0xFF : (byte)0x00;
    }

    private void StorePlane(int plane, uint offset, byte value)
        => _vram[plane * PlaneSize + offset] = value;

    /// <summary>Reads one plane directly, bypassing the read-mode logic.</summary>
    public byte ReadPlane(int plane, uint offset)
        => _vram[(plane & 3) * PlaneSize + (offset & 0xFFFF)];

    /// <summary>Writes one plane directly, bypassing the write-mode logic.</summary>
    public void WritePlane(int plane, uint offset, byte value)
    {
        _vram[(plane & 3) * PlaneSize + (offset & 0xFFFF)] = value;
        MarkDirty(offset & 0xFFFF);
    }

    private void MarkDirty(uint offset)
    {
        _anyDirty = true;

        VgaModeInfo mode = Registers.EffectiveMode;

        switch (mode.Layout)
        {
            case VgaMemoryLayout.Text when offset >= TextWindowOffset:
            {
                uint textOffset = offset - TextWindowOffset;
                int columns = Registers.TextColumns > 0 ? Registers.TextColumns : 80;
                uint row = textOffset / (uint)(columns * 2);
                if (row < _dirtyRows.Length) _dirtyRows[row] = true;
                return;
            }

            case VgaMemoryLayout.Planar4 when offset < 0x10000:
            case VgaMemoryLayout.Linear8 when offset < 0x10000:
            {
                // In a graphics mode the offset maps to a scan line directly:
                // rows are BytesPerRow apart. This is what makes dirty-row
                // tracking worth keeping in graphics modes too -- a program
                // updating a status line at the bottom of the screen should not
                // cost a full 640x480 palette lookup pass every frame.
                int bytesPerRow = Registers.RowOffsetBytes > 0
                    ? Registers.RowOffsetBytes
                    : mode.BytesPerRow;
                if (bytesPerRow <= 0) { Array.Fill(_dirtyRows, true); return; }

                uint row = offset / (uint)bytesPerRow;
                if (row < _dirtyRows.Length) _dirtyRows[row] = true;
                return;
            }

            default:
                // Anything else -- a write outside the active window, or an
                // unknown mode -- cannot be attributed to a row, so play safe.
                Array.Fill(_dirtyRows, true);
                return;
        }
    }

    public bool IsRowDirty(int row) => (uint)row < _dirtyRows.Length && _dirtyRows[row];
    public bool HasChanges => _anyDirty;

    public void ClearDirty()
    {
        Array.Clear(_dirtyRows);
        _anyDirty = false;
    }

    public void MarkAllDirty()
    {
        Array.Fill(_dirtyRows, true);
        _anyDirty = true;
    }

    /// <summary>
    /// Clears the whole of video memory, all four planes. The BIOS does this
    /// on a mode set unless bit 7 of the mode number asks it not to.
    /// </summary>
    public void ClearAllPlanes()
    {
        Array.Clear(_vram);
        Array.Clear(_latches);
        MarkAllDirty();
    }

    /// <summary>Direct access, used by the BIOS handlers and by the renderer.</summary>
    public Span<byte> GetSpan(uint offset, int length) => _vram.AsSpan((int)offset, length);

    /// <summary>
    /// Raw access that bypasses the planar logic entirely. The BIOS text
    /// helpers and the tests use it; anything emulating a CPU access must go
    /// through <see cref="ReadByte"/> / <see cref="WriteByte"/> instead, or the
    /// plane and latch behaviour is lost.
    /// </summary>
    public byte this[uint offset]
    {
        get => _vram[offset];
        set { _vram[offset] = value; MarkDirty(offset); }
    }
}
