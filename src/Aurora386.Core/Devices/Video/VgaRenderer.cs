namespace Aurora386.Core.Devices.Video;

/// <summary>
/// Converts video memory into an RGBA framebuffer ready to be uploaded as a
/// texture.
///
/// Rendering works a row at a time, and only on rows the VGA memory has
/// flagged as dirty. This matters more than it might seem: a DOS prompt
/// changes a handful of cells between frames, so redrawing all 2000 cells at
/// 60 fps would mean expanding roughly a million glyph pixels per second to
/// produce a screen that is 99% identical to the last one. Tracking dirty
/// rows turns the steady-state cost of a mostly-static screen into almost
/// nothing, and the cursor row is the one exception that must be redrawn
/// unconditionally because it blinks without memory ever changing.
///
/// The same argument applies in graphics modes, where the stakes are higher
/// still: a full 640x480 planar frame costs 307200 four-plane bit extractions
/// and palette lookups. A program updating a status bar should pay for the
/// status bar, not for the whole screen.
///
/// One caveat about geometry. In 80x25 text this produces a 640x400
/// framebuffer, because the character cell is 8x16. A real VGA does not emit
/// 640x400 at the monitor: it doubles each scanline and sends 640x480, giving
/// pixels that are visibly taller than they are wide. Presenting our
/// framebuffer 1:1 therefore squashes every character to about 83% of its
/// correct height, and text that should look like a period PC instead looks
/// subtly wrong. The frontend is responsible for stretching the output back
/// to a 4:3 aspect rather than blitting it pixel-for-pixel. The same is true
/// of modes 0Dh, 0Eh and 13h, whose 200 scanlines a real VGA doubles to 400.
/// </summary>
public sealed class VgaRenderer
{
    private readonly VgaMemory _memory;
    private readonly VgaRegisters _registers;

    /// <summary>Pixels in RGBA order, four bytes each.</summary>
    private uint[] _framebuffer;

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Frames actually redrawn, for diagnostics.</summary>
    public long FramesRendered { get; private set; }

    /// <summary>Cursor blink phase; the caller toggles it on a timer.</summary>
    public bool CursorBlinkOn { get; set; } = true;

    public VgaRenderer(VgaMemory memory, VgaRegisters registers)
    {
        _memory = memory;
        _registers = registers;
        Width = registers.TextColumns * VgaFont.CharWidth;
        Height = registers.TextRows * registers.CharacterHeight;
        _framebuffer = new uint[Width * Height];
        registers.ModeChanged += OnModeChanged;
    }

    public ReadOnlySpan<uint> Framebuffer => _framebuffer;

    /// <summary>The size the current mode wants the framebuffer to be.</summary>
    private (int Width, int Height) TargetSize()
    {
        VgaModeInfo mode = _registers.ModeInfo;

        if (mode.IsGraphics) return (mode.PixelWidth, mode.PixelHeight);

        // Text geometry follows the registers rather than the table, because
        // INT 10h AH=11h can switch the font -- and therefore the row count --
        // without going through a mode set.
        return (_registers.TextColumns * VgaFont.CharWidth,
                _registers.TextRows * _registers.CharacterHeight);
    }

    private void OnModeChanged()
    {
        (int width, int height) = TargetSize();
        Resize(width, height);
        _memory.MarkAllDirty();
    }

    private void Resize(int width, int height)
    {
        if (width == Width && height == Height) return;
        Width = width;
        Height = height;
        _framebuffer = new uint[Width * Height];
    }

    /// <summary>
    /// Refreshes the framebuffer. Returns true when something actually
    /// changed, so the caller can skip re-uploading an identical texture.
    /// </summary>
    public bool Render(bool force = false)
    {
        if (!force && !_memory.HasChanges) return false;

        // A mode set the renderer never heard about -- a program programming
        // the CRTC directly, say -- would otherwise write past the end of a
        // stale framebuffer. Reconciling the size here makes that impossible.
        (int width, int height) = TargetSize();
        if (width != Width || height != Height)
        {
            Resize(width, height);
            force = true;
        }

        switch (_registers.ModeInfo.Layout)
        {
            case VgaMemoryLayout.Planar4: RenderPlanar(force); break;
            case VgaMemoryLayout.Linear8: RenderLinear(force); break;
            default: RenderText(force); break;
        }

        _memory.ClearDirty();
        FramesRendered++;
        return true;
    }

    private void RenderText(bool force)
    {
        int columns = _registers.TextColumns;
        int rows = _registers.TextRows;
        int charHeight = _registers.CharacterHeight;
        uint baseOffset = VgaMemory.TextWindowOffset + (uint)(_registers.StartAddress * 2);

        int cursorRow = _registers.CursorRow;
        int cursorColumn = _registers.CursorColumn;

        for (int row = 0; row < rows; row++)
        {
            // The cursor's row is always redrawn: it blinks on a timer, and
            // no write to video memory would ever mark that row dirty.
            bool hasCursor = row == cursorRow && _registers.CursorVisible;
            if (!force && !_memory.IsRowDirty(row) && !hasCursor) continue;

            for (int column = 0; column < columns; column++)
            {
                uint offset = baseOffset + (uint)((row * columns + column) * 2);

                // Each text cell is two bytes: the character code and its
                // attribute byte.
                byte character = _memory[offset];
                byte attribute = _memory[offset + 1];

                // The attribute byte packs two colours: the low four bits are
                // the foreground, the next three the background. Bit 7 is the
                // blink flag on real hardware; we render it as an intense
                // background instead, which is the same choice the VGA makes
                // when blinking is disabled in the attribute controller.
                int foreground = attribute & 0x0F;
                int background = (attribute >> 4) & 0x07;

                bool drawCursor = hasCursor && column == cursorColumn && CursorBlinkOn;
                DrawGlyph(column, row, charHeight, character, foreground, background, drawCursor);
            }
        }
    }

    private void DrawGlyph(int column, int row, int charHeight, byte character,
                           int foreground, int background, bool drawCursor)
    {
        // A font the guest loaded into the character generator wins over the
        // one in ROM: that is the whole point of the generator being RAM.
        ReadOnlySpan<byte> glyph =
            _registers.UserFont is { } userFont && _registers.UserFontHeight == charHeight
                ? userFont.AsSpan(character * charHeight, charHeight)
                : VgaFonts.GetGlyph(character, charHeight);
        uint foregroundColor = ToRgba(_registers.GetPaletteColor(_registers.PaletteIndexFor(foreground)));
        uint backgroundColor = ToRgba(_registers.GetPaletteColor(_registers.PaletteIndexFor(background)));

        int originX = column * VgaFont.CharWidth;
        int originY = row * charHeight;
        if (originY + charHeight > Height) return;

        for (int y = 0; y < charHeight; y++)
        {
            byte bits = glyph[y];
            int destination = (originY + y) * Width + originX;

            for (int x = 0; x < VgaFont.CharWidth; x++)
            {
                // The most significant bit is the leftmost pixel.
                bool on = (bits & (0x80 >> x)) != 0;
                _framebuffer[destination + x] = on ? foregroundColor : backgroundColor;
            }
        }

        if (!drawCursor) return;

        // The cursor is a solid block on the last couple of scanlines of the
        // cell, drawn in the foreground colour -- the familiar DOS underline.
        int cursorTop = Math.Max(0, charHeight - 3);
        for (int y = cursorTop; y < charHeight - 1; y++)
        {
            int destination = (originY + y) * Width + originX;
            for (int x = 0; x < VgaFont.CharWidth; x++)
                _framebuffer[destination + x] = foregroundColor;
        }
    }

    /// <summary>
    /// Mode 13h and friends: one byte per pixel, laid out left to right and
    /// top to bottom, each byte an index into the 256-entry DAC. This is the
    /// only VGA graphics mode where the memory layout matches what a
    /// programmer would design from scratch, which is exactly why it became
    /// the mode every DOS game used.
    /// </summary>
    private void RenderLinear(bool force)
    {
        int bytesPerRow = _registers.RowOffsetBytes > 0
            ? _registers.RowOffsetBytes
            : _registers.ModeInfo.BytesPerRow;

        for (int y = 0; y < Height; y++)
        {
            if (!force && !_memory.IsRowDirty(y)) continue;

            uint rowStart = (uint)(y * bytesPerRow);
            int destination = y * Width;

            for (int x = 0; x < Width; x++)
                _framebuffer[destination + x] =
                    ToRgba(_registers.GetPaletteColor(_memory[rowStart + (uint)x]));
        }
    }

    /// <summary>
    /// The planar 16-colour modes: 0Dh, 0Eh, 10h, 12h.
    ///
    /// A pixel's four-bit colour is scattered one bit per plane, all four at
    /// the same byte offset and the same bit position. Reassembling it means
    /// fetching four bytes and picking the same bit out of each -- so the loop
    /// is arranged to fetch the four plane bytes once and then walk their eight
    /// bits, rather than doing four fetches per pixel. That is a 32-fold
    /// difference in memory traffic over the naive version, and at 640x480 it
    /// is the difference between a comfortable frame and a slideshow.
    ///
    /// The resulting 0-15 value indexes the DAC. On real hardware it first
    /// passes through the attribute controller's 16-entry palette, which remaps
    /// it to a 6-bit DAC index; we treat that mapping as the identity, which is
    /// what the BIOS programs for every standard mode.
    /// </summary>
    private void RenderPlanar(bool force)
    {
        int bytesPerRow = _registers.RowOffsetBytes > 0
            ? _registers.RowOffsetBytes
            : _registers.ModeInfo.BytesPerRow;

        // Palette lookups dominate this loop, and there are only sixteen
        // possible results. Hoisting them out of the pixel loop turns a
        // per-pixel tuple return and shift into a single array read.
        Span<uint> colors = stackalloc uint[16];
        for (int i = 0; i < 16; i++)
            colors[i] = ToRgba(_registers.GetPaletteColor(_registers.PaletteIndexFor(i)));

        for (int y = 0; y < Height; y++)
        {
            if (!force && !_memory.IsRowDirty(y)) continue;

            uint rowStart = (uint)(y * bytesPerRow);
            int destination = y * Width;

            for (int x = 0; x < Width; x += 8)
            {
                uint offset = rowStart + (uint)(x >> 3);

                // One fetch per plane covers the next eight pixels.
                byte plane0 = _memory.ReadPlane(0, offset);
                byte plane1 = _memory.ReadPlane(1, offset);
                byte plane2 = _memory.ReadPlane(2, offset);
                byte plane3 = _memory.ReadPlane(3, offset);

                int remaining = Math.Min(8, Width - x);
                for (int bit = 0; bit < remaining; bit++)
                {
                    // Bit 7 is the leftmost pixel, matching the text font's
                    // convention and the order the CRTC shifts pixels out.
                    int shift = 7 - bit;
                    int color = ((plane0 >> shift) & 1)
                              | (((plane1 >> shift) & 1) << 1)
                              | (((plane2 >> shift) & 1) << 2)
                              | (((plane3 >> shift) & 1) << 3);

                    _framebuffer[destination + x + bit] = colors[color];
                }
            }
        }
    }

    private static uint ToRgba((byte R, byte G, byte B) color)
        => (uint)(color.R | (color.G << 8) | (color.B << 16) | (0xFF << 24));
}
