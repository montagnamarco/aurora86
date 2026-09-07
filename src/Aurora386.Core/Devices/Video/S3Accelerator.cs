using Aurora386.Core.IO;

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
    private ushort _mix, _backgroundMix;         // 0xBAE8, 0xB6E8
    private ushort _command;                     // 0x9AE8

    /// <summary>Clipping: nothing is drawn outside this rectangle.</summary>
    private ushort _clipTop, _clipLeft, _clipBottom, _clipRight;

    /// <summary>Operations performed, which the tests read.</summary>
    public long OperationsPerformed { get; private set; }

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
                PutPixel(_currentX + x, _currentY + y, _foreground);
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
    /// Writes one pixel, respecting the clip rectangle.
    ///
    /// Clipping is done here rather than by trimming the rectangle first
    /// because that is what the hardware does: every pixel is tested. It
    /// costs nothing worth measuring and it means a line drawn diagonally out
    /// of the clip region stops at the boundary rather than at a corner.
    /// </summary>
    private void PutPixel(int x, int y, ushort colour)
    {
        if (x < _clipLeft || x > _clipRight) return;
        if (y < _clipTop || y > _clipBottom) return;
        if (x < 0 || y < 0) return;

        uint at = (uint)(y * Stride + x * BytesPerPixel);

        _memory.WriteLinear(at, (byte)colour);
        if (BytesPerPixel >= 2) _memory.WriteLinear(at + 1, (byte)(colour >> 8));
    }

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
