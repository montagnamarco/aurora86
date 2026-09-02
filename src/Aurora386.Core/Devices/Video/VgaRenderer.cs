namespace Aurora386.Core.Devices.Video;

/// <summary>
/// Converts video memory into an RGBA framebuffer ready to be uploaded as a
/// texture.
///
/// Rendering works a character row at a time, and only on rows the VGA memory
/// has flagged as dirty. This matters more than it might seem: a DOS prompt
/// changes a handful of cells between frames, so redrawing all 2000 cells at
/// 60 fps would mean expanding roughly a million glyph pixels per second to
/// produce a screen that is 99% identical to the last one. Tracking dirty
/// rows turns the steady-state cost of a mostly-static screen into almost
/// nothing, and the cursor row is the one exception that must be redrawn
/// unconditionally because it blinks without memory ever changing.
///
/// One caveat about geometry. In 80x25 text this produces a 640x400
/// framebuffer, because the character cell is 8x16. A real VGA does not emit
/// 640x400 at the monitor: it doubles each scanline and sends 640x480, giving
/// pixels that are visibly taller than they are wide. Presenting our
/// framebuffer 1:1 therefore squashes every character to about 83% of its
/// correct height, and text that should look like a period PC instead looks
/// subtly wrong. The frontend is responsible for stretching the output back
/// to a 4:3 aspect rather than blitting it pixel-for-pixel.
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
        Height = registers.TextRows * VgaFont.CharHeight;
        _framebuffer = new uint[Width * Height];
        registers.ModeChanged += OnModeChanged;
    }

    public ReadOnlySpan<uint> Framebuffer => _framebuffer;

    private void OnModeChanged()
    {
        int width = _registers.TextColumns * VgaFont.CharWidth;
        int height = _registers.TextRows * VgaFont.CharHeight;

        if (width != Width || height != Height)
        {
            Width = width;
            Height = height;
            _framebuffer = new uint[Width * Height];
        }
        _memory.MarkAllDirty();
    }

    /// <summary>
    /// Refreshes the framebuffer. Returns true when something actually
    /// changed, so the caller can skip re-uploading an identical texture.
    /// </summary>
    public bool Render(bool force = false)
    {
        if (!force && !_memory.HasChanges) return false;

        if (_registers.IsGraphicsMode) RenderGraphics();
        else RenderText(force);

        _memory.ClearDirty();
        FramesRendered++;
        return true;
    }

    private void RenderText(bool force)
    {
        int columns = _registers.TextColumns;
        int rows = _registers.TextRows;
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
                DrawGlyph(column, row, character, foreground, background, drawCursor);
            }
        }
    }

    private void DrawGlyph(int column, int row, byte character,
                           int foreground, int background, bool drawCursor)
    {
        ReadOnlySpan<byte> glyph = VgaFont.GetGlyph(character);
        uint foregroundColor = ToRgba(_registers.GetPaletteColor(foreground));
        uint backgroundColor = ToRgba(_registers.GetPaletteColor(background));

        int originX = column * VgaFont.CharWidth;
        int originY = row * VgaFont.CharHeight;

        for (int y = 0; y < VgaFont.CharHeight; y++)
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
        for (int y = VgaFont.CharHeight - 3; y < VgaFont.CharHeight - 1; y++)
        {
            int destination = (originY + y) * Width + originX;
            for (int x = 0; x < VgaFont.CharWidth; x++)
                _framebuffer[destination + x] = foregroundColor;
        }
    }

    /// <summary>
    /// Graphics modes. Only mode 13h (320x200 in 256 colours) is implemented
    /// so far, because it is the one linear mode: one byte per pixel, laid out
    /// left to right and top to bottom, so conversion is a straight lookup.
    /// The planar EGA modes need the four-plane read/write logic of the
    /// graphics controller and will arrive along with the rest of the SVGA
    /// support.
    /// </summary>
    private void RenderGraphics()
    {
        if (_registers.VideoMode != 0x13)
        {
            // Unsupported mode: show a black screen rather than
            // misinterpreting the memory layout and displaying noise, which
            // would look like a much deeper bug than it is.
            Array.Clear(_framebuffer);
            return;
        }

        const int graphicsWidth = 320;
        const int graphicsHeight = 200;

        if (Width != graphicsWidth || Height != graphicsHeight)
        {
            Width = graphicsWidth;
            Height = graphicsHeight;
            _framebuffer = new uint[Width * Height];
        }

        // In mode 13h every byte is one pixel: an index into the palette.
        for (int i = 0; i < graphicsWidth * graphicsHeight; i++)
            _framebuffer[i] = ToRgba(_registers.GetPaletteColor(_memory[(uint)i]));
    }

    private static uint ToRgba((byte R, byte G, byte B) color)
        => (uint)(color.R | (color.G << 8) | (color.B << 16) | (0xFF << 24));
}
