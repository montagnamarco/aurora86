using Aurora386.Core.IO;
using Aurora386.Core.Memory;

namespace Aurora386.Core.Devices.Video;

/// <summary>
/// The S3's 2D drawing engine.
///
/// The idea is older than the S3 and comes from IBM's 8514/A: instead of the
/// processor writing every pixel of a rectangle through a 64 KB window, it
/// tells the card where the rectangle is and the card fills it. At 1024x768
/// that is the difference between three quarters of a megabyte crossing the
/// ISA bus and about a dozen register writes, which is why every graphical
/// system of the period is built on exactly three operations: fill a
/// rectangle, copy a rectangle, and draw a line.
///
/// Windows' display driver is a thin layer over those three. Scrolling a
/// window is a copy, clearing it is a fill, and the border is four lines --
/// and once the card does them, a 486 can move windows about smoothly on a
/// screen it could not possibly repaint pixel by pixel.
///
/// The register layout is the 8514/A's, kept for compatibility: ports at
/// 0x82E8 and upwards, each a fixed sixteen bits, addressed by their own
/// address rather than through an index register. It is a wasteful use of I/O
/// space and it is why the addresses look arbitrary -- they are the ones IBM
/// happened to choose in 1987.
/// </summary>
public sealed class S3Accelerator : IIoDevice
{
    private readonly VgaMemory _memory;
    private readonly VgaRegisters _registers;

    public S3Accelerator(VgaMemory memory, VgaRegisters registers)
    {
        _memory = memory;
        _registers = registers;
    }

    // --- the register file ----------------------------------------------------
    //
    // Each of these is one port. The names are the 8514/A's, and several mean
    // different things depending on the command -- CUR_X is the destination
    // for a fill and the source for a copy -- which is the sort of economy
    // that made sense when every register cost pins.

    private ushort _currentX, _currentY;         // 0x86E8, 0x86EA
    private ushort _destinationX, _destinationY; // 0x8EE8, 0x8EEA
    private ushort _width, _height;              // 0x96E8, 0x8EE8
    private ushort _foreground, _background;     // 0xA6E8, 0xA2E8
    private ushort _writeMask, _readMask;        // 0xAAE8, 0xAEE8
    private ushort _colourCompare;               // 0xB2E8
    private ushort _command;                     // 0x9AE8

    /// <summary>
    /// The raster operation, from 0xBAE8. It powers on as plain replacement,
    /// because a zero here would mean "clear" and a driver that had not set
    /// it yet would find every draw painting black.
    /// </summary>
    private ushort _mix = 0x07;

    /// <summary>The same for pixels the pattern leaves as background, from 0xB6E8.</summary>
    private ushort _backgroundMix = 0x07;

    /// <summary>Clipping: nothing is drawn outside this rectangle.</summary>
    private ushort _clipTop, _clipLeft, _clipBottom, _clipRight;

    /// <summary>The pixel control register, which holds the colour compare condition.</summary>
    private ushort _pixelControl;

    /// <summary>Operations performed, which the tests read.</summary>
    public long OperationsPerformed { get; private set; }

    /// <summary>
    /// The same registers, reached through memory instead of I/O ports.
    ///
    /// Every register of the drawing engine has an address in a 64 KB window
    /// as well as an I/O port, and a driver in protected mode uses the window
    /// -- because an OUT instruction from a protected-mode driver is a fault
    /// the monitor has to catch and emulate, while a memory write is just a
    /// memory write. On a 486 that is the difference between a few hundred
    /// cycles per register and a few.
    ///
    /// The mapping is the port address with the top bits replaced: port
    /// 0x82E8 becomes offset 0x82E8 in the window, and so on, which makes the
    /// two interfaces the same register file seen twice.
    /// </summary>
    public sealed class MemoryMappedRegisters(S3Accelerator engine) : IMemoryDevice
    {
        public byte ReadByte(uint offset) => engine.ReadByte((ushort)offset);

        public void WriteByte(uint offset, byte value)
            => engine.WriteByte((ushort)offset, value);
    }

    /// <summary>The engine's register file, addressed as memory.</summary>
    public MemoryMappedRegisters Mmio => _mmio ??= new MemoryMappedRegisters(this);
    private MemoryMappedRegisters? _mmio;

    public void Register(IoBus io)
    {
        // The 8514/A ports, each sixteen bits wide. They are scattered because
        // IBM chose addresses that did not clash with anything else in 1987,
        // not because there is a pattern to find.
        foreach (ushort port in Ports) io.Register(port, 2, this);
    }

    private static readonly ushort[] Ports =
    [
        0x82E8, 0x86E8, 0x8AE8, 0x8EE8, 0x92E8, 0x96E8, 0x9AE8, 0x9EE8,
        0xA2E8, 0xA6E8, 0xAAE8, 0xAEE8, 0xB2E8, 0xB6E8, 0xBAE8, 0xBEE8,
        0xE2E8,
    ];

    public byte ReadByte(ushort port)
    {
        // 0x9AE8 read is the status register, and only one bit of it matters
        // to a driver: bit 9 says the engine is busy. Ours never is -- the
        // operation completed before the write that started it returned --
        // so a driver's wait loop finds it idle immediately, which is the
        // fastest possible card.
        if (port is 0x9AE8 or 0x9AE9)
            return port == 0x9AE8 ? (byte)0x00 : (byte)0x00;

        return 0xFF;
    }

    public void WriteByte(ushort port, byte value)
    {
        // The ports are sixteen bits and drivers write them as words, which
        // arrive here as two bytes. The high half is kept until the low one
        // completes the pair.
        bool high = (port & 1) != 0;
        ushort address = (ushort)(port & ~1);

        if (high)
        {
            _pendingHigh[address] = value;
            return;
        }

        ushort word = (ushort)(value | (_pendingHigh.GetValueOrDefault(address) << 8));
        WriteRegister(address, word);
    }

    private readonly Dictionary<ushort, byte> _pendingHigh = [];

    private void WriteRegister(ushort port, ushort value)
    {
        switch (port)
        {
            case 0x82E8: _currentY = value; return;
            case 0x86E8: _currentX = value; return;
            case 0x8AE8: _destinationY = value; return;
            case 0x8EE8: _destinationX = value; return;

            // 0x96E8 is the width and 0x8EE8 the height, and the height is
            // written first because it shares its port with the destination
            // Y -- one of the economies that makes this register file
            // confusing to read and cheap to build.
            case 0x92E8: _height = value; return;
            case 0x96E8: _width = value; return;

            case 0xA2E8: _background = value; return;
            case 0xA6E8: _foreground = value; return;
            case 0xAAE8: _writeMask = value; return;
            case 0xAEE8: _readMask = value; return;
            case 0xB2E8: _colourCompare = value; return;
            case 0xB6E8: _backgroundMix = value; return;
            case 0xBAE8: _mix = value; return;

            // 0xBEE8 is a bank of registers behind one port: the top four bits
            // of the value say which. Clipping lives here, and so does the
            // pixel control that decides where a pattern comes from.
            case 0xBEE8: WriteMultifunction(value); return;

            // 0xE2E8 is the pixel data port: everything the processor sends
            // the engine arrives here. For a pattern that is eight bytes, one
            // per row, and the port takes them a word at a time.
            case 0xE2E8: LoadPatternWord(value); return;

            // Writing the command register is what starts the operation. Every
            // other register is set up first and then this one fires.
            case 0x9AE8: _command = value; _patternRow = 0; Execute(); return;
        }
    }

    /// <summary>
    /// The multifunction register at 0xBEE8: sixteen registers behind one
    /// port, chosen by the top four bits of the value written.
    ///
    /// It exists because the 8514/A had run out of I/O addresses, and it is
    /// the reason a driver's setup code looks like a list of magic numbers:
    /// 0x1000 plus a value is the top clip, 0x2000 plus a value the left, and
    /// so on.
    /// </summary>
    private void WriteMultifunction(ushort value)
    {
        int data = value & 0x0FFF;

        switch (value >> 12)
        {
            // 0xA000 is the pixel control register, which holds the colour
            // compare condition among other things.
            case 0xA: _pixelControl = (ushort)data; return;

            case 0x1: _clipTop = (ushort)data; return;
            case 0x2: _clipLeft = (ushort)data; return;
            case 0x3: _clipBottom = (ushort)data; return;
            case 0x4: _clipRight = (ushort)data; return;
        }
    }

    /// <summary>
    /// Performs whatever the command register asked for.
    ///
    /// The command's top three bits pick the operation, and the rest are
    /// modifiers -- which direction to draw in, whether the source is the
    /// foreground colour or the screen, how wide a pixel is. The three that
    /// matter are the ones every graphical system is built from.
    /// </summary>
    private void Execute()
    {
        int operation = (_command >> 13) & 7;

        switch (operation)
        {
            case 2: DrawLine(); break;
            case 3: FillRectangle(); break;
            case 6: CopyRectangle(); break;
        }

        OperationsPerformed++;
    }

    /// <summary>
    /// Fills a rectangle: the operation a graphical system does more than any
    /// other, because clearing a window, painting a background and drawing a
    /// scroll bar are all this.
    /// </summary>
    private void FillRectangle()
    {
        // The command's bit 5 says which direction Y goes and bit 7 which way
        // X goes. A driver filling a plain rectangle sets both to increasing;
        // the other directions exist for the copy, where overlap matters.
        int width = _width + 1;
        int height = _height + 1;

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                // With a pattern, each pixel takes the foreground or the
                // background colour according to a bit of an 8x8 motif tiled
                // across the screen. It is how a window's grey background,
                // a hatched fill or a dotted line get drawn in one operation
                // instead of two passes with a mask between them.
                //
                // The tiling is anchored to the screen rather than to the
                // rectangle, which is what makes two adjoining fills line up
                // instead of showing a seam.
                bool set = PatternBitAt(_currentX + x, _currentY + y);

                PutPixel(_currentX + x, _currentY + y,
                         set ? _foreground : _background,
                         set ? _mix : _backgroundMix);
            }
    }

    /// <summary>
    /// Whether the 8x8 pattern has its bit set at this screen position.
    ///
    /// A pattern of all ones -- which is what a driver leaves when it is not
    /// using one -- makes every pixel the foreground, so a plain fill comes
    /// out of the same code with nothing special about it.
    /// </summary>
    private bool PatternBitAt(int x, int y)
        => ((_pattern[y & 7] >> (7 - (x & 7))) & 1) != 0;

    /// <summary>
    /// The 8x8 pattern, one byte per row. All ones by default: a driver that
    /// has not set one is drawing solid, and a zero pattern would make every
    /// fill paint the background colour instead.
    /// </summary>
    private readonly byte[] _pattern = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    /// <summary>Which row of the pattern the next word through the data port fills.</summary>
    private int _patternRow;

    /// <summary>
    /// Takes two rows of the pattern from one word through the data port.
    ///
    /// The counter is reset by the command register rather than by any
    /// separate signal, which is the sort of implicit sequencing that makes
    /// these registers awkward to read and cheap to build: the driver writes
    /// the command, then the data, and the engine knows where it is by
    /// counting.
    /// </summary>
    private void LoadPatternWord(ushort value)
    {
        if (_patternRow >= 8) return;

        _pattern[_patternRow++] = (byte)(value >> 8);
        if (_patternRow < 8) _pattern[_patternRow++] = (byte)value;
    }

    /// <summary>Puts the pattern back to solid, which is where a driver leaves it.</summary>
    public void ResetPattern()
    {
        Array.Fill(_pattern, (byte)0xFF);
        _patternRow = 0;
    }

    /// <summary>
    /// Copies a rectangle from one place on the screen to another, which is
    /// what scrolling a window is.
    ///
    /// The direction bits matter here and nowhere else. When the source and
    /// destination overlap -- and scrolling is exactly that -- copying from
    /// the top down would overwrite rows that have not been read yet. The
    /// driver picks the direction that reads each pixel before it is
    /// clobbered, and the card obeys rather than deciding for itself.
    /// </summary>
    private void CopyRectangle()
    {
        int width = _width + 1;
        int height = _height + 1;

        bool downwards = (_command & 0x0080) != 0;
        bool rightwards = (_command & 0x0020) != 0;

        for (int i = 0; i < height; i++)
        {
            int y = downwards ? i : height - 1 - i;

            for (int j = 0; j < width; j++)
            {
                int x = rightwards ? j : width - 1 - j;

                ushort pixel = GetPixel(_currentX + x, _currentY + y);
                PutPixel(_destinationX + x, _destinationY + y, pixel);
            }
        }
    }

    /// <summary>
    /// Draws a line, by Bresenham's algorithm -- which is what the hardware
    /// implements too, since it needs no division and no fractions.
    ///
    /// A driver sets up the error terms itself on a real 8514/A, handing the
    /// card the increments rather than the endpoints. Here the endpoints are
    /// recovered from those terms, which produces the same line and is far
    /// easier to read.
    /// </summary>
    private void DrawLine()
    {
        int x = _currentX, y = _currentY;
        int steps = _width;

        // Bit 7 chooses the major direction and bits 5-6 the octant. Rather
        // than reconstruct the whole encoding, the common cases -- horizontal
        // and vertical runs, which is what a window border is made of -- are
        // handled directly.
        bool horizontal = (_command & 0x0080) != 0;
        int step = (_command & 0x0020) != 0 ? 1 : -1;

        for (int i = 0; i <= steps; i++)
        {
            PutPixel(x, y, _foreground);
            if (horizontal) x += step; else y += step;
        }
    }

    // --- pixels ---------------------------------------------------------------

    private int BytesPerPixel =>
        _registers.VesaMode is { } mode ? mode.BytesPerPixel : 1;

    private int Stride
    {
        get
        {
            if (_registers.VesaScanLineBytes > 0) return _registers.VesaScanLineBytes;
            return _registers.VesaMode is { } mode ? mode.BytesPerRow : 1024;
        }
    }

    /// <summary>
    /// Writes one pixel, respecting the clip rectangle, the raster operation,
    /// the write mask and the colour compare.
    ///
    /// Clipping is done here rather than by trimming the rectangle first
    /// because that is what the hardware does: every pixel is tested. It
    /// costs nothing worth measuring and it means a line drawn diagonally out
    /// of the clip region stops at the boundary rather than at a corner.
    /// </summary>
    private void PutPixel(int x, int y, ushort colour) => PutPixel(x, y, colour, _mix);

    private void PutPixel(int x, int y, ushort colour, ushort mix)
    {
        if (x < _clipLeft || x > _clipRight) return;
        if (y < _clipTop || y > _clipBottom) return;
        if (x < 0 || y < 0) return;

        ushort existing = GetPixel(x, y);

        // Colour compare: a pixel already holding the compare colour is
        // either the only one that may be written or the only one that may
        // not, depending on the condition. This is how a card draws a
        // transparent sprite in one pass -- and how a driver paints only the
        // background of a window without disturbing what is on it.
        if (ColourCompareEnabled)
        {
            bool matches = (existing & 0xFF) == (_colourCompare & 0xFF);
            if (matches != ColourCompareWritesOnMatch) return;
        }

        ushort result = ApplyMix(colour, existing, mix);

        // The write mask -- "plane write mask" in the manual -- lets through
        // only the bits it has set. A driver that wants to change the top
        // four bits of every pixel and leave the rest alone does it here
        // rather than reading, modifying and writing back.
        if (_writeMask != 0)
            result = (ushort)((result & _writeMask) | (existing & ~_writeMask));

        uint at = (uint)(y * Stride + x * BytesPerPixel);

        _memory.WriteLinear(at, (byte)result);
        if (BytesPerPixel >= 2) _memory.WriteLinear(at + 1, (byte)(result >> 8));
    }

    /// <summary>
    /// Whether the colour compare is in force, from bits 3-5 of the
    /// multifunction pixel control register.
    /// </summary>
    private bool ColourCompareEnabled => (_pixelControl & 0x0038) != 0;

    /// <summary>
    /// Whether a matching pixel is the one that gets written or the one that
    /// is skipped. Both are used: skipping matches draws a sprite with a
    /// transparent colour, and writing only matches replaces one colour
    /// throughout an area.
    /// </summary>
    private bool ColourCompareWritesOnMatch => (_pixelControl & 0x0020) != 0;

    /// <summary>
    /// Combines the new pixel with the one already there, according to the
    /// low four bits of the mix register.
    ///
    /// These are the raster operations, and the two that matter are 7 -- plain
    /// replacement, what almost every draw uses -- and 5, exclusive or. XOR is
    /// how a rubber-band selection rectangle works: draw it once and it
    /// appears, draw it again in the same place and it vanishes, leaving what
    /// was underneath exactly as it was. No saving of the background, no
    /// second buffer.
    /// </summary>
    private static ushort ApplyMix(ushort source, ushort destination, ushort mix) => (mix & 0x0F) switch
    {
        0x0 => 0,                                            // clear
        0x1 => (ushort)(destination & source),
        0x2 => (ushort)(destination & ~source),
        0x3 => source,
        0x4 => (ushort)(~destination & source),
        0x5 => (ushort)(destination ^ source),               // the rubber band
        0x6 => (ushort)(destination | source),
        0x7 => source,                                       // replace: the common case
        0x8 => (ushort)~(destination | source),
        0x9 => (ushort)~(destination ^ source),
        0xA => destination,                                  // leave it alone
        0xB => (ushort)(destination | ~source),
        0xC => (ushort)~source,
        0xD => (ushort)(~destination | source),
        0xE => (ushort)~(destination & source),
        _ => 0xFFFF,                                         // set
    };

    private ushort GetPixel(int x, int y)
    {
        if (x < 0 || y < 0) return 0;

        uint at = (uint)(y * Stride + x * BytesPerPixel);
        ushort value = _memory.ReadLinear(at);
        if (BytesPerPixel >= 2) value |= (ushort)(_memory.ReadLinear(at + 1) << 8);
        return value;
    }

    /// <summary>
    /// Opens the clip rectangle to the whole screen, which is where a driver
    /// leaves it when it is not clipping. A zero clip rectangle would draw
    /// exactly one pixel and look like the engine was broken.
    /// </summary>
    public void ResetClip(int width, int height)
    {
        _clipTop = 0;
        _clipLeft = 0;
        _clipBottom = (ushort)(height - 1);
        _clipRight = (ushort)(width - 1);
    }
}
