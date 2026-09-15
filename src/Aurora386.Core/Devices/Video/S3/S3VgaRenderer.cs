namespace Aurora386.Core.Devices.Video;

/// <summary>
/// Presents the 1995 S3 Trio64V+ VGA raster and its shared 64x64 hardware
/// cursor, also used by the original 1996 ViRGE. Period Windows drivers
/// program cursor position, pattern and colour stacks through CR45-CR4F;
/// composition reads the same VRAM as the standard VGA display.
/// </summary>
public class S3VgaRenderer : VgaRenderer
{
    protected S3VgaRegisters S3Registers { get; }

    /// <summary>Where the hardware pointer was in the last drawn frame.</summary>
    private int _lastCursorX = -1;
    private int _lastCursorY = -1;

    public S3VgaRenderer(VgaMemory memory, S3VgaRegisters registers)
        : base(memory, registers)
    {
        S3Registers = registers;
    }

    protected override bool InvalidateCardCursorRows()
    {
        // The hardware pointer moves with nothing written to video memory
        // either -- that is the whole point of it -- so a move is its own
        // reason to redraw. The rows it used to cover have to be repainted
        // as well, or it leaves a trail behind it.
        bool cursorMoved = false;
        if (S3Registers.CursorEnabled)
        {
            (int cursorX, int cursorY) = S3Registers.CursorPosition;
            cursorMoved = cursorX != _lastCursorX || cursorY != _lastCursorY;

            if (cursorMoved)
            {
                Memory.MarkRowsDirty(Math.Min(_lastCursorY, cursorY) - 1, 66 + Math.Abs(cursorY - _lastCursorY));
                _lastCursorX = cursorX;
                _lastCursorY = cursorY;
            }
        }

        return cursorMoved;
    }

    protected override void InvalidateCompositionRows()
    {
        // Composition can XOR the background. Even a stationary pointer
        // needs fresh pixels underneath whenever another dirty row causes
        // a frame; otherwise we XOR the already composed pointer again.
        if (S3Registers.CursorEnabled)
        {
            int first = S3Registers.CursorPosition.Y;
            int last = first + 63;
            if (!S3Registers.EffectiveMode.IsGraphics)
            {
                int heightOfCell = Math.Max(1, S3Registers.CharacterHeight);
                first /= heightOfCell;
                last /= heightOfCell;
            }
            Memory.MarkRowsDirty(first, last - first + 1);
        }
    }

    protected override void DrawPointer()
    {
        // The card's own pointer takes precedence over the DOS mouse pointer.
        if (S3Registers.CursorEnabled)
        {
            DrawHardwareCursor();
            return;
        }

        base.DrawPointer();
    }

    /// <summary>
    /// The S3's own 64x64 pointer, drawn over the picture as the beam passes.
    ///
    /// It costs nothing to move: writing two registers is the whole
    /// operation. Doing the same in software means saving what is under the
    /// pointer, drawing it, and putting the background back before the next
    /// move -- three times the work, and it has to happen between frames or
    /// the pointer flickers. At 1024x768 that was the difference between a
    /// pointer that kept up with the hand and one that lagged behind it.
    ///
    /// The pattern is two bits per pixel, and the pair is not a colour index
    /// but an instruction. AND clear with XOR clear paints the background
    /// colour, AND clear with XOR set paints the foreground, and AND set
    /// leaves the screen showing through -- either unchanged, or inverted
    /// when XOR is also set. That last combination is what makes a pointer
    /// visible over any background without knowing what is behind it.
    /// </summary>
    private void DrawHardwareCursor()
    {
        (int originX, int originY) = S3Registers.CursorPosition;
        (int offsetX, int offsetY) = S3Registers.CursorOffset;
        (uint foreground, uint background) = S3Registers.CursorColours;

        uint pattern = S3Registers.CursorPatternAddress;

        uint foregroundColour = HardwareCursorColour(foreground);
        uint backgroundColour = HardwareCursorColour(background);

        for (int row = offsetY; row < 64; row++)
        {
            int y = originY + row - offsetY;
            if (y < 0 || y >= Height) continue;

            for (int column = offsetX; column < 64; column++)
            {
                int x = originX + column - offsetX;
                if (x < 0 || x >= Width) continue;

                // Sixteen bytes per row: eight of AND bits and eight of XOR
                // bits, interleaved in pairs of two bytes. The interleaving
                // is what lets the card fetch both planes for sixteen pixels
                // in one memory access.
                uint at = pattern + (uint)(row * 16) + (uint)(column / 16) * 4;
                int bit = 15 - (column % 16);

                int and = (Memory.ReadLinear(at) << 8) | Memory.ReadLinear(at + 1);
                int xor = (Memory.ReadLinear(at + 2) << 8) | Memory.ReadLinear(at + 3);

                bool andBit = ((and >> bit) & 1) != 0;
                bool xorBit = ((xor >> bit) & 1) != 0;

                int destination = y * Width + x;

                if (!andBit)
                {
                    Pixels[destination] = xorBit ? foregroundColour : backgroundColour;
                }
                else if (xorBit)
                {
                    // Screen through, inverted: the trick that keeps a pointer
                    // visible over a background of any colour.
                    Pixels[destination] ^= 0x00FFFFFF;
                }
            }
        }
    }

    /// <summary>
    /// Converts an S3 cursor colour in the same format as the current screen.
    ///
    /// The cursor stack is a direct colour value above 8 bits per pixel. It
    /// reaches the DAC only in 8-bit modes, where its low byte remains the
    /// palette index that older S3 software programs into CR4A and CR4B.
    /// </summary>
    private uint HardwareCursorColour(uint colour)
    {
        int depth = S3Registers.EffectiveMode.BitsPerPixel;

        switch (depth)
        {
            case 15:
            {
                int rgb555 = (int)(colour & 0xFFFF);

                return From15(rgb555);
            }
            case 16:
            {
                int rgb565 = (int)(colour & 0xFFFF);

                return From16(rgb565);
            }
            case 24:
            case 32:
            {
                byte red = (byte)(colour >> 16);
                byte green = (byte)(colour >> 8);
                byte blue = (byte)colour;

                return FromBytes(red, green, blue);
            }
            default:
            {
                // The original renderer used the DAC for every cursor mode.
                // Preserve that path for VGA-compatible depths such as the
                // planar 4-bit VBE mode; only true-colour modes reinterpret
                // the stack as an RGB value.
                byte paletteIndex = (byte)colour;

                return ToRgba(S3Registers.GetPaletteColor(paletteIndex));
            }
        }
    }
}
