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
    /// The raster operation, from 0xBAE8: the mix in the low four bits and,
    /// in bits 6-5, where the colour comes from -- the background register,
    /// the foreground one, the processor or the screen. It powers on as
    /// plain replacement in the foreground colour, because a zero here would
    /// mean "clear" and a driver that had not set it yet would find every
    /// draw painting black.
    /// </summary>
    private ushort _mix = 0x27;

    /// <summary>The same for pixels the pattern leaves as background, from 0xB6E8.</summary>
    private ushort _backgroundMix = 0x07;

    /// <summary>Clipping: nothing is drawn outside this rectangle.</summary>
    private ushort _clipTop, _clipLeft, _clipBottom, _clipRight;

    /// <summary>The pixel control register, which holds the colour compare condition.</summary>
    private ushort _pixelControl;

    /// <summary>Operations performed, which the tests read.</summary>
    public long OperationsPerformed { get; private set; }

    /// <summary>
    /// Diagnostics: every command as it executes, with the operation bits
    /// and the rectangle it names. What a driver asks the engine to do is
    /// the only evidence there is when the screen comes out wrong.
    /// </summary>
    public Action<ushort, int, int, int, int, ushort, ushort, ushort>? CommandTrace { get; set; }

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
        private readonly Dictionary<uint, byte> _pendingLow = [];

        public byte ReadByte(uint offset)
        {
            ushort word = engine.ReadMmioWord((ushort)(offset & 0xFFFE));
            return (offset & 1) != 0 ? (byte)(word >> 8) : (byte)word;
        }

        public ushort ReadWord(uint offset) => engine.ReadMmioWord((ushort)(offset & 0xFFFE));

        public uint ReadDword(uint offset) =>
            (uint)(engine.ReadMmioWord((ushort)(offset & 0xFFFC)) | (engine.ReadMmioWord((ushort)((offset & 0xFFFC) + 2)) << 16));

        // Bytes arrive low first, and every register here is sixteen bits
        // wide or a pair of them: the low byte waits for its partner. Below
        // 0x8000 is the image transfer area, where anything written is
        // pixel data for the operation in progress.
        public void WriteByte(uint offset, byte value)
        {
            if (offset < 0x8000) { engine.PushDataByte(value); return; }
            uint address = offset & 0xFFFE;
            if ((offset & 1) == 0)
            {
                _pendingLow[address] = value;
                return;
            }
            engine.WriteMmioWord((ushort)address, (ushort)(_pendingLow.GetValueOrDefault(address) | (value << 8)));
        }

        public void WriteWord(uint offset, ushort value)
        {
            if (offset < 0x8000) { engine.PushDataByte((byte)value); engine.PushDataByte((byte)(value >> 8)); return; }
            engine.WriteMmioWord((ushort)(offset & 0xFFFE), value);
        }

        public void WriteDword(uint offset, uint value)
        {
            if (offset < 0x8000) { for (int i = 0; i < 4; i++) engine.PushDataByte((byte)(value >> (8 * i))); return; }
            engine.WriteMmioWord((ushort)(offset & 0xFFFC), (ushort)value);
            engine.WriteMmioWord((ushort)((offset & 0xFFFC) + 2), (ushort)(value >> 16));
        }
    }

    /// <summary>
    /// A register written through memory. Above 0x8200 the offset is the
    /// port number and the register is the same one; from 0x8100 to 0x81FF
    /// sit the Trio64's packed registers, pairs of 16-bit values at
    /// doubleword addresses so that a driver sets a coordinate pair or a
    /// width and height with one 32-bit write: current Y and X, destination
    /// Y and X, the command, the colours, the mixes, the clip rectangle, the
    /// pixel control and the two miscellaneous registers, the two axis
    /// counts, the pattern position. The S3 Windows driver uses nothing
    /// else for drawing.
    /// </summary>
    internal void WriteMmioWord(ushort offset, ushort value)
    {
        switch (offset)
        {
            case 0x8100: _currentY = value; return;
            case 0x8102: _currentX = value; return;
            case 0x8108: _destinationY = value; return;
            case 0x810A: _destinationX = value; return;
            case 0x8110: return;                        // error term: the line takes endpoints here
            case 0x8118: _command = value; Execute(); return;
            case 0x811A: return;                        // short stroke vectors
            case 0x8120: _background = value; return;
            case 0x8124: _foreground = value; return;
            case 0x8128: _writeMask = value; return;
            case 0x812C: _readMask = value; return;
            case 0x8130: _colourCompare = value; return;
            case 0x8134: _backgroundMix = value; return;
            case 0x8136: _mix = value; return;
            case 0x8138: _clipTop = (ushort)(value & 0x0FFF); return;
            case 0x813A: _clipLeft = (ushort)(value & 0x0FFF); return;
            case 0x813C: _clipBottom = (ushort)(value & 0x0FFF); return;
            case 0x813E: _clipRight = (ushort)(value & 0x0FFF); return;
            case 0x8140: _pixelControl = value; return;
            case 0x8142: _multiMisc2 = (ushort)(value & 0x0FFF); return;
            case 0x8144: _multiMisc = (ushort)(value & 0x0FFF); return;
            case 0x8146: _readSelect = value & 0x0F; return;
            case 0x8148: _height = value; return;       // ALT_PCNT: the minor count low, the major high
            case 0x814A: _width = value; return;
            case 0x814C: _patternY = value; return;      // ALT_PAT: Y low, X high, like the others
            case 0x814E: _patternX = value; return;
        }

        // The high halves of 32-bit writes to the classic registers -- a
        // driver writing its colour with a doubleword -- land on odd port
        // numbers plus two, which no register answers to.
        if (offset >= 0x8200 && (offset & 3) == 0 || offset is 0x82E8 or 0x86E8 or 0x8AE8 or 0x8EE8 or 0x92E8 or 0x96E8 or 0x9AE8 or 0x9EE8
            or 0xA2E8 or 0xA6E8 or 0xAAE8 or 0xAEE8 or 0xB2E8 or 0xB6E8 or 0xBAE8 or 0xBEE8 or 0xE2E8 or 0xE2EA)
            WriteRegister(offset, value);
    }

    internal ushort ReadMmioWord(ushort offset)
    {
        return offset switch
        {
            0x8100 => _currentY,
            0x8102 => _currentX,
            0x8138 => _clipTop, 0x813A => _clipLeft, 0x813C => _clipBottom, 0x813E => _clipRight,
            0x8140 => _pixelControl, 0x8142 => _multiMisc2, 0x8144 => _multiMisc,
            0x8148 => _height, 0x814A => _width,
            _ => ReadRegister(offset),
        };
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
        ushort word = ReadRegister((ushort)(port & ~1));
        return (port & 1) != 0 ? (byte)(word >> 8) : (byte)word;
    }

    public ushort ReadWord(ushort port) => ReadRegister((ushort)(port & ~1));

    /// <summary>
    /// The registers a driver reads back. 0x9AE8 is the status: bit 9 says
    /// the engine is busy, and ours never is -- the operation completed
    /// before the write that started it returned -- so a wait loop finds it
    /// idle at once. 0xBEE8 returns whichever multifunction register the
    /// read-select register named, and the selection advances after each
    /// read, as the Trio64 does; the S3 Windows driver reads a register
    /// back at start-up to satisfy itself that the engine is there.
    /// </summary>
    private ushort ReadRegister(ushort port)
    {
        switch (port)
        {
            case 0x9AE8: return 0x0000;
            case 0x82E8: return _currentY;
            case 0x86E8: return _currentX;
            case 0x96E8: return _width;
            case 0xBEE8: return ReadMultifunction();
            default: return 0xFFFF;
        }
    }

    private ushort ReadMultifunction()
    {
        int selected = _readSelect;
        _readSelect = (_readSelect + 1) & 0x0F;
        return selected switch
        {
            0 => _height,
            1 => _clipTop,
            2 => _clipLeft,
            3 => _clipBottom,
            4 => _clipRight,
            5 => _pixelControl,
            6 => _multiMisc2,
            7 => _multiMisc,
            _ => 0,
        };
    }

    public void WriteByte(ushort port, byte value)
    {
        // The ports are sixteen bits and drivers write them as words. A word
        // written through the bus arrives here as two bytes, the low one
        // first; the low half is kept until the high one completes the pair,
        // and the register is written then, as the chip does.
        ushort address = (ushort)(port & ~1);
        if ((port & 1) == 0)
        {
            _pendingLow[address] = value;
            return;
        }

        ushort word = (ushort)(_pendingLow.GetValueOrDefault(address) | (value << 8));
        WriteRegister(address, word);
    }

    public void WriteWord(ushort port, ushort value) => WriteRegister((ushort)(port & ~1), value);

    private readonly Dictionary<ushort, byte> _pendingLow = [];
    private int _readSelect;
    private ushort _multiMisc;
    private ushort _multiMisc2;
    private ushort _patternX;
    private ushort _patternY;

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
            // the engine arrives here, a word at a time, or a doubleword
            // when the high half comes through 0xE2EA.
            case 0xE2E8:
            case 0xE2EA:
                PushDataByte((byte)value);
                PushDataByte((byte)(value >> 8));
                return;

            // Writing the command register is what starts the operation. Every
            // other register is set up first and then this one fires.
            case 0x9AE8: _command = value; Execute(); return;
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

            case 0x0: _height = (ushort)data; return;   // MIN_AXIS_PCNT again, by another door
            case 0x1: _clipTop = (ushort)data; return;
            case 0x2: _clipLeft = (ushort)data; return;
            case 0x3: _clipBottom = (ushort)data; return;
            case 0x4: _clipRight = (ushort)data; return;

            // The two miscellaneous registers and the read-select. Nothing
            // here changes what is drawn, but a driver writes them and reads
            // them back, and a card that answers with 0xFFFF is a card the
            // driver decides is not there.
            case 0xD: _multiMisc2 = (ushort)data; return;
            case 0xE: _multiMisc = (ushort)data; return;
            case 0xF: _readSelect = data & 0x0F; return;
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
        CommandTrace?.Invoke(_command, _currentX, _currentY, _width + 1, _height + 1, _foreground, _mix, _pixelControl);
        _transfer = null;
        _unitBytes = 0;

        // The encoding is the 8514/A's, kept by every S3: 1 draws a line, 2
        // fills a rectangle, 3 fills a polygon (a rectangle here: the
        // polygon form differs only in how the edges are given), 6 copies
        // a rectangle, 7 fills with a pattern taken from the screen. It
        // used to say 2 was the line and 3 the fill, and a driver's every
        // fill came out as a line along its top edge.
        switch (operation)
        {
            case 1: DrawLine(); break;
            case 2 or 3: FillRectangle(); break;
            case 6: CopyRectangle(); break;
            case 7: PatternFill(); break;
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
        // Bit 8 -- "PCDATA" -- says the pixels come from the processor: the
        // engine takes the rectangle and then waits for data through the
        // pixel transfer port or the image transfer area, painting as it
        // arrives. It is how a driver uploads a brush, draws text from a
        // glyph bitmap or copies an image in: the rectangle is the frame,
        // the processor supplies what goes in it.
        if ((_command & 0x0100) != 0)
        {
            _transfer = new Transfer(_currentX, _currentY, _width + 1, _height + 1,
                                     (_command & 0x0020) != 0, (_command & 0x0080) != 0,
                                     (_pixelControl & 0x00C0) == 0x0080);
            return;
        }

        int width = _width + 1;
        int height = _height + 1;

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                PutPixel(_currentX + x, _currentY + y, ForegroundSource(_currentX + x, _currentY + y), _mix);
    }

    /// <summary>
    /// The colour a foreground pixel takes: bits 6-5 of the mix register
    /// say whether it is the background colour, the foreground colour, the
    /// processor's data or what is already on the screen at the source.
    /// </summary>
    private ushort ForegroundSource(int x, int y) => ((_mix >> 5) & 3) switch
    {
        0 => _background,
        3 => GetPixel(x, y),
        _ => _foreground,
    };

    private ushort BackgroundSource(int x, int y) => ((_backgroundMix >> 5) & 3) switch
    {
        1 => _foreground,
        3 => GetPixel(x, y),
        _ => _background,
    };

    // --- data from the processor ------------------------------------------------

    /// <summary>
    /// A rectangle waiting for its pixels from the processor. The cursor
    /// walks it in the command's directions, one pixel per unit of data in
    /// colour mode and one per bit in mask mode, where each bit picks the
    /// foreground mix or the background one -- which is how a font glyph
    /// is drawn in one pass.
    /// </summary>
    private sealed class Transfer(int left, int top, int width, int height, bool rightwards, bool downwards, bool mask)
    {
        public readonly bool Mask = mask;
        private int _column, _row;

        public bool Done => _row >= height;
        public bool RowStarting => _column == 0;

        /// <summary>The next pixel's position, or null when the rectangle is full.</summary>
        public (int X, int Y)? Next()
        {
            if (Done) return null;
            int x = rightwards ? left + _column : left - _column;
            int y = downwards ? top + _row : top - _row;
            if (++_column >= width) { _column = 0; _row++; }
            return (x, y);
        }
    }

    private Transfer? _transfer;
    private uint _unit;
    private int _unitBytes;

    /// <summary>
    /// The width of one unit of processor data, in bytes: bits 10-9 of the
    /// command say whether the driver sends bytes, words or doublewords.
    /// Each row of the rectangle starts on a fresh unit; whatever is left of
    /// the previous one is thrown away, which is the padding a driver's
    /// bitmap rows carry.
    /// </summary>
    private int UnitBytes => ((_command >> 9) & 3) switch { 0 => 1, 1 => 2, _ => 4 };

    /// <summary>
    /// One byte of data from the processor, by whichever door it came
    /// through. Bytes are gathered into a unit and the unit is consumed
    /// whole; bit 12 of the command -- BYTSEQ -- says whether the low byte
    /// of a unit is the first pixel or the last.
    /// </summary>
    internal void PushDataByte(byte value)
    {
        if (_transfer is null || _transfer.Done) return;

        _unit |= (uint)value << (8 * _unitBytes);
        if (++_unitBytes < UnitBytes) return;

        uint unit = _unit;
        int bytes = _unitBytes;
        _unit = 0;
        _unitBytes = 0;
        ConsumeUnit(unit, bytes);
    }

    private void ConsumeUnit(uint unit, int bytes)
    {
        bool lowFirst = (_command & 0x1000) != 0;
        int pixelBytes = _transfer!.Mask ? 1 : BytesPerPixel;
        uint pixel = 0;
        int pixelFilled = 0;

        for (int i = 0; i < bytes; i++)
        {
            int index = lowFirst ? i : bytes - 1 - i;
            byte b = (byte)(unit >> (8 * index));

            if (_transfer.Mask)
            {
                for (int bit = 7; bit >= 0; bit--)
                {
                    if (!PlotFromProcessor(((b >> bit) & 1) != 0, null)) return;
                    if (_transfer.RowStarting) return;      // the row ended: the rest of the unit is padding
                }
                continue;
            }

            pixel |= (uint)b << (8 * pixelFilled);
            if (++pixelFilled < pixelBytes) continue;

            bool more = PlotFromProcessor(true, (ushort)pixel);
            pixel = 0;
            pixelFilled = 0;
            if (!more || _transfer.RowStarting) return;
        }
    }

    /// <summary>Paints the next pixel of the transfer; false when the rectangle is full.</summary>
    private bool PlotFromProcessor(bool foreground, ushort? data)
    {
        if (_transfer!.Next() is not { } at) return false;

        if (data is { } colour && ((_mix >> 5) & 3) == 2)
            PutPixel(at.X, at.Y, colour, _mix);
        else if (foreground)
            PutPixel(at.X, at.Y, ForegroundSource(at.X, at.Y), _mix);
        else
            PutPixel(at.X, at.Y, BackgroundSource(at.X, at.Y), _backgroundMix);

        if (_transfer.Done) _transfer = null;
        return _transfer is not null;
    }

    /// <summary>
    /// Copies a rectangle from one place on the screen to another, which is
    /// what scrolling a window is.
    ///
    /// When the source and
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
            // CUR and DEST name the first pixel transferred, not always the
            // top-left corner. For a window moved down/right the S3 driver
            // supplies both bottom-right corners and clears INC_X/INC_Y.
            // Reversing a loop over positive offsets would copy a different
            // rectangle entirely, leaving only fragments of the window.
            int y = downwards ? i : -i;

            for (int j = 0; j < width; j++)
            {
                int x = rightwards ? j : -j;

                ushort pixel = GetPixel(_currentX + x, _currentY + y);
                PutPixel(_destinationX + x, _destinationY + y, pixel);
            }
        }
    }

    /// <summary>
    /// The pattern fill: an 8x8 tile of pixels already on the screen,
    /// repeated over the destination rectangle. A Windows driver keeps its
    /// brushes off-screen as pixels and paints a window's background with
    /// one of these, which is why the pattern comes from video memory and
    /// not from the data port. The source is where the current position
    /// points, the destination where the destination registers point, and
    /// the tile is anchored to the screen rather than to the rectangle, so
    /// that two adjoining fills line up instead of showing a seam.
    /// </summary>
    private void PatternFill()
    {
        int width = _width + 1;
        int height = _height + 1;

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                ushort pixel = GetPixel(_currentX + ((_destinationX + x) & 7), _currentY + ((_destinationY + y) & 7));
                PutPixel(_destinationX + x, _destinationY + y, pixel, _mix);
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
            PutPixel(x, y, ForegroundSource(x, y), _mix);
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
    /// <summary>
    /// The sixteen mixes of the 8514/A, in its own numbering, which every
    /// S3 kept: 3 leaves the destination alone, 5 is the exclusive or of the
    /// rubber band, 7 the plain replacement. A Windows driver draws text
    /// with the background mix set to 3, so that only the glyph's own
    /// pixels touch the screen; a table in another order painted the
    /// background too, and every word came out as a black block.
    /// </summary>
    private static ushort ApplyMix(ushort source, ushort destination, ushort mix) => (mix & 0x0F) switch
    {
        0x0 => (ushort)~destination,
        0x1 => 0,                                            // clear
        0x2 => 0xFFFF,                                       // set
        0x3 => destination,                                  // leave it alone
        0x4 => (ushort)~source,
        0x5 => (ushort)(destination ^ source),               // the rubber band
        0x6 => (ushort)~(destination ^ source),
        0x7 => source,                                       // replace: the common case
        0x8 => (ushort)(~source | ~destination),
        0x9 => (ushort)(source | ~destination),
        0xA => (ushort)(~source | destination),
        0xB => (ushort)(source | destination),
        0xC => (ushort)(source & destination),
        0xD => (ushort)(source & ~destination),
        0xE => (ushort)(~source & destination),
        _ => (ushort)(~source & ~destination),
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
