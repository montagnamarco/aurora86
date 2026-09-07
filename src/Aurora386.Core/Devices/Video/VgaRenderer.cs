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

    /// <summary>The blink phase the last drawn frame was drawn in.</summary>
    private bool _lastBlinkPhase = true;

    public VgaRenderer(VgaMemory memory, VgaRegisters registers)
    {
        _memory = memory;
        _registers = registers;
        Width = registers.TextColumns * registers.CharacterWidth;
        Height = registers.TextRows * registers.CharacterHeight;
        _framebuffer = new uint[Width * Height];
        registers.ModeChanged += OnModeChanged;
    }

    public ReadOnlySpan<uint> Framebuffer => _framebuffer;

    /// <summary>
    /// The mouse pointer to compose over each frame, if a driver provides one.
    /// See <see cref="MousePointer"/> for why it is drawn here and not in
    /// video memory.
    /// </summary>
    public MousePointer? Pointer { get; set; }


    // Where the pointer was drawn last frame, so those rows are repainted.
    private int _pointerRowFirst = -1, _pointerRowCount;
    private bool _pointerWasVisible;

    /// <summary>The size the current mode wants the framebuffer to be.</summary>
    private (int Width, int Height) TargetSize()
    {
        VgaModeInfo mode = _registers.EffectiveMode;

        if (mode.IsGraphics) return (mode.PixelWidth, mode.PixelHeight);

        // Text geometry follows the registers rather than the table, because
        // INT 10h AH=11h can switch the font -- and therefore the row count --
        // without going through a mode set.
        return (_registers.TextColumns * _registers.CharacterWidth,
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
        InvalidatePointerRows();

        // The blink phase changes with nothing writing to video memory, so a
        // frame that differs only by the phase would otherwise be skipped as
        // having nothing to draw -- and the cursor and any blinking text
        // would simply stop flashing. Only the phase actually changing counts
        // here: checking it every frame would redraw the screen continuously.
        bool phaseChanged = CursorBlinkOn != _lastBlinkPhase;
        _lastBlinkPhase = CursorBlinkOn;

        if (!force && !phaseChanged && !_memory.HasChanges) return false;

        // A mode set the renderer never heard about -- a program programming
        // the CRTC directly, say -- would otherwise write past the end of a
        // stale framebuffer. Reconciling the size here makes that impossible.
        (int width, int height) = TargetSize();
        if (width != Width || height != Height)
        {
            Resize(width, height);
            force = true;
        }

        switch (_registers.EffectiveMode.Layout)
        {
            case VgaMemoryLayout.Planar4: RenderPlanar(force); break;
            case VgaMemoryLayout.Linear8: RenderLinear(force); break;
            case VgaMemoryLayout.Cga: RenderCga(force); break;
            default: RenderText(force); break;
        }

        DrawPointer();

        _memory.ClearDirty();
        FramesRendered++;
        return true;
    }

    /// <summary>
    /// The rows under the pointer's last position have to be repainted from
    /// memory when it moves or hides, or a trail of stale pointers is left
    /// behind: nothing in video memory changed there, so nothing else would
    /// mark them dirty.
    /// </summary>
    private void InvalidatePointerRows()
    {
        MousePointer? pointer = Pointer;
        bool visible = pointer is { Visible: true } && !(pointer.UsesHardwareTextCursor && !_registers.EffectiveMode.IsGraphics);
        (int first, int count) = visible ? PointerRows(pointer!) : (-1, 0);

        bool moved = visible != _pointerWasVisible || first != _pointerRowFirst || count != _pointerRowCount;
        if (!moved) return;

        if (_pointerWasVisible) _memory.MarkRowsDirty(_pointerRowFirst, _pointerRowCount);
        if (visible) _memory.MarkRowsDirty(first, count);

        _pointerWasVisible = visible;
        _pointerRowFirst = first;
        _pointerRowCount = count;
    }

    /// <summary>The dirty-row span the pointer covers: one text row, or sixteen scanlines.</summary>
    private (int First, int Count) PointerRows(MousePointer pointer)
    {
        if (_registers.EffectiveMode.IsGraphics)
            return (pointer.Y - pointer.HotSpotY, 16);

        int charHeight = Math.Max(1, _registers.CharacterHeight);
        return (pointer.Y / 8, 1);
    }

    private void DrawPointer()
    {
        MousePointer? pointer = Pointer;
        if (pointer is not { Visible: true }) return;

        if (_registers.EffectiveMode.IsGraphics) DrawGraphicsPointer(pointer);
        else if (!pointer.UsesHardwareTextCursor) DrawTextPointer(pointer);
    }

    /// <summary>
    /// The text pointer: the cell under the pointer is redrawn with its
    /// character/attribute word passed through the driver's two masks. With
    /// the default masks that means the same character with foreground and
    /// background swapped.
    /// </summary>
    private void DrawTextPointer(MousePointer pointer)
    {
        int columns = _registers.TextColumns;
        int rows = _registers.TextRows;
        int column = pointer.X / 8, row = pointer.Y / 8;
        if (column < 0 || column >= columns || row < 0 || row >= rows) return;

        uint baseOffset = VgaMemory.TextWindowOffset + (uint)(_registers.StartAddress * 2);
        uint offset = baseOffset + (uint)((row * columns + column) * 2);
        ushort cell = (ushort)(_memory[offset] | (_memory[offset + 1] << 8));
        cell = (ushort)((cell & pointer.TextScreenMask) ^ pointer.TextCursorMask);

        byte character = (byte)cell;
        int attribute = cell >> 8;
        DrawGlyph(column, row, _registers.CharacterHeight, character,
                  attribute & 0x0F, (attribute >> 4) & 0x07, drawCursor: false);
    }

    /// <summary>
    /// The graphics pointer, composed pixel by pixel: a clear AND bit blanks
    /// the pixel, a set XOR bit inverts it. The usual arrow is AND clear and
    /// XOR set for the body (white), AND clear and XOR clear for the outline
    /// (black), and both set where the screen shows through unchanged.
    /// </summary>
    private void DrawGraphicsPointer(MousePointer pointer)
    {
        int left = pointer.X - pointer.HotSpotX;
        int top = pointer.Y - pointer.HotSpotY;

        for (int row = 0; row < 16; row++)
        {
            int y = top + row;
            if (y < 0 || y >= Height) continue;

            ushort and = pointer.AndMask[row], xor = pointer.XorMask[row];
            for (int bit = 0; bit < 16; bit++)
            {
                int x = left + bit;
                if (x < 0 || x >= Width) continue;

                int mask = 0x8000 >> bit;
                bool keep = (and & mask) != 0, invert = (xor & mask) != 0;
                if (keep && !invert) continue;

                int index = y * Width + x;
                uint pixel = keep ? _framebuffer[index] : 0xFF000000;
                if (invert) pixel ^= 0x00FFFFFF;
                _framebuffer[index] = pixel;
            }
        }
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
            // no write to video memory would ever mark that row dirty. The
            // same is true of any row holding a blinking character, so those
            // rows are found once per frame rather than skipped as clean.
            bool hasCursor = row == cursorRow && _registers.CursorVisible;
            bool blinking = _registers.BlinkEnabled && RowHasBlinkingText(baseOffset, row, columns);
            if (!force && !_memory.IsRowDirty(row) && !hasCursor && !blinking) continue;

            for (int column = 0; column < columns; column++)
            {
                uint offset = baseOffset + (uint)((row * columns + column) * 2);

                // Each text cell is two bytes: the character code and its
                // attribute byte.
                byte character = _memory[offset];
                byte attribute = _memory[offset + 1];

                // The attribute byte packs two colours: the low four bits are
                // the foreground, the next three the background. Bit 7 has
                // meant two different things since the CGA, and which one is
                // a setting in the attribute controller rather than a
                // convention.
                //
                // With blinking enabled -- what a mode set leaves -- there
                // are eight background colours and the character flashes.
                // With it disabled there are sixteen and nothing flashes,
                // which is what a program wanting a bright background asks
                // for.
                int foreground = attribute & 0x0F;
                int background = _registers.BlinkEnabled
                    ? (attribute >> 4) & 0x07
                    : (attribute >> 4) & 0x0F;

                // A blinking character spends half its cycle showing its
                // background colour. The cursor's phase is reused rather than
                // kept separately: on real hardware both are driven from the
                // same vertical retrace counter, which is why a blinking
                // character and the cursor flash together rather than
                // drifting against each other.
                if (_registers.BlinkEnabled && (attribute & 0x80) != 0 && !CursorBlinkOn)
                    foreground = background;

                bool drawCursor = hasCursor && column == cursorColumn && CursorBlinkOn;
                DrawGlyph(column, row, charHeight, character, foreground, background, drawCursor);
            }
        }
    }

    /// <summary>
    /// Whether any cell in this row has its blink bit set.
    ///
    /// A blinking character changes on a timer with nothing writing to video
    /// memory, so the dirty-row tracking that makes rendering affordable
    /// would skip it for ever. Scanning the row's attribute bytes is the
    /// cheap half of drawing it, and it means only the rows that actually
    /// blink pay for being redrawn.
    /// </summary>
    private bool RowHasBlinkingText(uint baseOffset, int row, int columns)
    {
        uint at = baseOffset + (uint)(row * columns * 2) + 1;
        for (int column = 0; column < columns; column++, at += 2)
            if ((_memory[at] & 0x80) != 0) return true;
        return false;
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

        int cellWidth = _registers.CharacterWidth;
        int originX = column * cellWidth;
        int originY = row * charHeight;
        if (originY + charHeight > Height) return;

        // The ninth column, when there is one, is normally blank -- it is the
        // space between one character and the next, and it is why text on a
        // VGA looks less cramped than on an EGA. The exception is the
        // line-drawing characters, 0xC0 to 0xDF, where the ninth column
        // repeats the eighth: without that a horizontal rule drawn out of
        // them would come apart into dashes, one blank column per cell.
        bool extendGlyph = cellWidth > 8
                        && _registers.NinthColumnRepeats
                        && character >= 0xC0 && character <= 0xDF;

        for (int y = 0; y < charHeight; y++)
        {
            byte bits = glyph[y];
            int destination = (originY + y) * Width + originX;

            for (int x = 0; x < cellWidth; x++)
            {
                // The most significant bit is the leftmost pixel. Past the
                // eighth there is no glyph data, so the pixel is background
                // unless this is one of the characters whose line has to
                // carry on into the gap.
                bool on = x < 8
                    ? (bits & (0x80 >> x)) != 0
                    : extendGlyph && (bits & 0x01) != 0;

                _framebuffer[destination + x] = on ? foregroundColor : backgroundColor;
            }
        }

        if (!drawCursor) return;

        // The cursor's shape comes from two CRTC registers naming the first
        // and last scan line of the cell it covers. Programs change it: an
        // underline for insert mode and a full block for overwrite is the
        // convention every DOS editor follows, and it is two register writes
        // rather than any drawing.
        (int start, int end) = _registers.CursorShape;

        for (int y = 0; y < charHeight; y++)
        {
            // The hardware switches the cursor on at the start line and off
            // after the end line. With the two reversed it never switches off
            // within the cell, giving a cursor split into a bar at the top
            // and one at the bottom -- which is not a bug to be tidied away,
            // some programs asked for exactly that.
            bool on = start <= end
                ? y >= start && y <= end
                : y >= start || y <= end;

            if (!on) continue;

            int destination = (originY + y) * Width + originX;
            for (int x = 0; x < cellWidth; x++)
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
    /// <summary>
    /// Where in memory a given scan line reads from.
    ///
    /// Three registers conspire here, and together they are the whole of VGA
    /// hardware scrolling. The start address says where the picture begins,
    /// so moving it scrolls everything without copying a byte. The line
    /// compare register names a scan line below which the display restarts
    /// from address zero, which is how a game keeps a status panel still
    /// while the world moves behind it. And the panning register shifts the
    /// result by up to eight pixels, covering the positions between one
    /// character and the next -- a start address alone scrolls in jerky
    /// eight-pixel steps.
    /// </summary>
    private uint SourceRow(int y, int bytesPerRow, out int panning)
    {
        int split = _registers.LineCompare;

        // At or below the split the display is drawn from the start of
        // memory, and the panning register does not apply: the panel a game
        // puts there must not slide about with the scrolling world above it.
        if (split < Height && y > split)
        {
            panning = 0;
            return (uint)((y - split - 1) * bytesPerRow);
        }

        panning = _registers.HorizontalPan;
        return (uint)(_registers.StartAddress * 2 + y * bytesPerRow);
    }

    private void RenderLinear(bool force)
    {
        int bytesPerRow = _registers.RowOffsetBytes > 0
            ? _registers.RowOffsetBytes
            : _registers.ModeInfo.BytesPerRow;

        for (int y = 0; y < Height; y++)
        {
            if (!force && !_memory.IsRowDirty(y)) continue;

            uint rowStart = SourceRow(y, bytesPerRow, out int panning);
            int destination = y * Width;

            // In a 256-colour mode each byte is one pixel, so panning is a
            // plain offset into the row rather than a shift within a byte.
            for (int x = 0; x < Width; x++)
                _framebuffer[destination + x] =
                    ToRgba(_registers.GetPaletteColor(_memory[rowStart + (uint)(x + panning)]));
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

            uint rowStart = SourceRow(y, bytesPerRow, out int panning);
            int destination = y * Width;

            for (int x = 0; x < Width; x += 8)
            {
                // Panning shifts by pixels, and a byte covers eight of them,
                // so a panned group of eight straddles two bytes: the fetch
                // moves along by whole bytes and the remainder becomes the
                // bit to start at. That is precisely why the register exists
                // -- without it a scroll can only step a whole byte at a
                // time, which at eight pixels a step is visibly jerky.
                int shifted = x + panning;
                uint offset = rowStart + (uint)(shifted >> 3);
                int firstBit = shifted & 7;

                // Two fetches per plane when the group straddles, one when it
                // does not. The second byte is read unconditionally only when
                // it is actually needed, since at panning zero -- which is
                // almost always -- there is nothing to straddle.
                byte plane0 = _memory.ReadPlane(0, offset);
                byte plane1 = _memory.ReadPlane(1, offset);
                byte plane2 = _memory.ReadPlane(2, offset);
                byte plane3 = _memory.ReadPlane(3, offset);

                byte next0 = 0, next1 = 0, next2 = 0, next3 = 0;
                if (firstBit != 0)
                {
                    next0 = _memory.ReadPlane(0, offset + 1);
                    next1 = _memory.ReadPlane(1, offset + 1);
                    next2 = _memory.ReadPlane(2, offset + 1);
                    next3 = _memory.ReadPlane(3, offset + 1);
                }

                int remaining = Math.Min(8, Width - x);
                for (int bit = 0; bit < remaining; bit++)
                {
                    // Bit 7 is the leftmost pixel, matching the text font's
                    // convention and the order the CRTC shifts pixels out.
                    int position = firstBit + bit;
                    bool fromNext = position >= 8;
                    int shift = 7 - (position & 7);

                    byte b0 = fromNext ? next0 : plane0;
                    byte b1 = fromNext ? next1 : plane1;
                    byte b2 = fromNext ? next2 : plane2;
                    byte b3 = fromNext ? next3 : plane3;

                    int color = ((b0 >> shift) & 1)
                              | (((b1 >> shift) & 1) << 1)
                              | (((b2 >> shift) & 1) << 2)
                              | (((b3 >> shift) & 1) << 3);

                    _framebuffer[destination + x + bit] = colors[color];
                }
            }
        }
    }

    /// <summary>
    /// The CGA graphics modes, which the VGA kept for the sake of everything
    /// written before 1987.
    ///
    /// Two things make this layout its own renderer rather than a variation
    /// on the linear one. The pixels are packed several to a byte -- four at
    /// two bits each, or eight at one -- and, more awkwardly, the scan lines
    /// are interleaved: even lines live in the first 8 KB and odd lines in
    /// the second, so line 1 is not after line 0 but 8192 bytes further on.
    ///
    /// That interleave was not a design choice so much as an admission. The
    /// CGA could not read a contiguous frame fast enough to keep up with the
    /// beam, so it read from two banks alternately. It made the card
    /// buildable in 1981 and it has made every program that draws a vertical
    /// line pay for the arithmetic ever since.
    /// </summary>
    private void RenderCga(bool force)
    {
        // Four colours at 320 wide, two at 640. The mode's own width says
        // which, since both read the same 80 bytes per line.
        bool fourColour = _registers.EffectiveMode.PixelWidth <= 320;
        int pixelsPerByte = fourColour ? 4 : 8;

        // The CGA's palette is not programmable in the way the VGA's is: a
        // program picks one of a few fixed sets. This is the common one --
        // black, cyan, magenta, white -- which is what almost every CGA game
        // used and what everyone remembers the machine looking like.
        Span<uint> colors = stackalloc uint[4];
        if (fourColour)
        {
            colors[0] = ToRgba(_registers.GetPaletteColor(0));
            colors[1] = ToRgba(_registers.GetPaletteColor(0x0B));   // cyan
            colors[2] = ToRgba(_registers.GetPaletteColor(0x0D));   // magenta
            colors[3] = ToRgba(_registers.GetPaletteColor(0x0F));   // white
        }
        else
        {
            colors[0] = ToRgba(_registers.GetPaletteColor(0));
            colors[1] = ToRgba(_registers.GetPaletteColor(0x0F));
        }

        for (int y = 0; y < Height; y++)
        {
            if (!force && !_memory.IsRowDirty(y)) continue;

            // The interleave, in one line: even scan lines from the first
            // bank, odd ones from the second, 8 KB apart. Both banks are
            // measured from 0xB8000, which is where the CGA's memory was and
            // where every program written for it still writes.
            uint rowStart = VgaMemory.TextWindowOffset
                          + (uint)((y & 1) * 0x2000 + (y >> 1) * 80);
            int destination = y * Width;

            for (int x = 0; x < Width; x += pixelsPerByte)
            {
                byte packed = _memory[rowStart + (uint)(x / pixelsPerByte)];

                int remaining = Math.Min(pixelsPerByte, Width - x);
                for (int i = 0; i < remaining; i++)
                {
                    // The leftmost pixel is in the high bits, as everywhere
                    // else on this hardware.
                    int color = fourColour
                        ? (packed >> (6 - i * 2)) & 3
                        : (packed >> (7 - i)) & 1;

                    _framebuffer[destination + x + i] = colors[color];
                }
            }
        }
    }

    private static uint ToRgba((byte R, byte G, byte B) color)
        => (uint)(color.R | (color.G << 8) | (color.B << 16) | (0xFF << 24));
}
