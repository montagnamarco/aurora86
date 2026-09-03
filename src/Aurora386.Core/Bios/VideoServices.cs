using Aurora386.Core.Cpu;
using CpuNs = Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Video;

namespace Aurora386.Core.Bios;

/// <summary>
/// BIOS video services (INT 10h). This is how DOS and application software put
/// text on the screen without touching the adapter hardware directly.
/// </summary>
public sealed class VideoServices
{
    private readonly VgaMemory _video;
    private readonly VgaRegisters _registers;
    private readonly BiosDataArea _bda;

    public VideoServices(VgaMemory video, VgaRegisters registers, BiosDataArea bda)
    {
        _video = video;
        _registers = registers;
        _bda = bda;
    }

    /// <summary>
    /// Address of a text cell, taking the CRTC's start address into account.
    ///
    /// The adapter does not necessarily display the first cell of video memory
    /// at the top left. CRTC registers 0x0C and 0x0D hold a start address, and
    /// scrolling the screen by bumping that register is far cheaper than
    /// copying 4000 bytes -- which is exactly what a fast console driver does.
    ///
    /// The renderer honours it. The BIOS has to as well, or the two disagree
    /// about where row zero is: text written through INT 10h lands at a
    /// different place from where it appears, and the prompt ends up printed
    /// over lines that were already on the screen.
    /// </summary>
    private uint CellAddress(int row, int column)
    {
        int columns = _registers.TextColumns;
        int cell = _registers.StartAddress + row * columns + column;
        return VgaMemory.TextWindowOffset + (uint)(cell * 2);
    }

    private uint CellAddress(int linearCell)
        => VgaMemory.TextWindowOffset
         + (uint)((_registers.StartAddress + linearCell) * 2);

    public void Handle(CpuNs.Cpu cpu)
    {
        byte function = cpu.State.GetReg8(4);   // AH

        switch (function)
        {
            case 0x00: SetVideoMode(cpu.State.GetReg8(0)); return;
            case 0x01: SetCursorShape(cpu.State.Cx); return;
            // DH is the row and DL the column. In the 8-bit register ordering
            // (0=AL 1=CL 2=DL 3=BL 4=AH 5=CH 6=DH 7=BH) those are indices 6 and
            // 2, and they must be passed in that order: getting them backwards
            // makes text march down the screen vertically instead of across.
            case 0x02: SetCursorPosition(cpu.State.GetReg8(2), cpu.State.GetReg8(6)); return;
            case 0x03: GetCursorPosition(cpu); return;
            case 0x05: SetActivePage(cpu.State.GetReg8(0)); return;
            case 0x06: ScrollUp(cpu); return;
            case 0x07: ScrollDown(cpu); return;
            case 0x08: ReadCharacterAtCursor(cpu); return;
            case 0x09: WriteCharacterWithAttribute(cpu, advance: false); return;
            case 0x0A: WriteCharacterOnly(cpu); return;
            // Pixel access in graphics modes. The register indices are the
            // usual trap: 0=AL 1=CL 2=DL 3=BL 4=AH 5=CH 6=DH 7=BH, so the
            // column is CX (a full 16 bits, since 640 does not fit in eight)
            // and the row is DX.
            case 0x0C: WritePixel(cpu.State.Cx, cpu.State.Dx, cpu.State.GetReg8(0)); return;
            case 0x0D: cpu.State.SetReg8(0, ReadPixel(cpu.State.Cx, cpu.State.Dx)); return;

            case 0x0E: TeletypeOutput(cpu.State.GetReg8(0), cpu.State.GetReg8(3)); return;
            case 0x0F: GetVideoMode(cpu); return;
            case 0x13: WriteString(cpu); return;

            case 0x11: CharacterGenerator(cpu); return;

            case 0x12:
                // EGA/VGA configuration functions. Subfunction 0x10 reports how
                // much memory the adapter has.
                if (cpu.State.GetReg8(3) == 0x10)
                {
                    cpu.State.Bx = 0x0003;   // 256 KB of video memory
                    cpu.State.Cx = 0x0009;
                }
                return;

            case 0x1A:
                // Display combination code: identifies the adapter type.
                cpu.State.SetReg8(0, 0x1A);    // function supported
                cpu.State.Bx = 0x0808;         // color VGA
                return;

            default:
                // Unimplemented function: return without doing anything, which
                // is the least harmful behavior.
                return;
        }
    }

    /// <summary>
    /// INT 10h AH=00: set the video mode.
    ///
    /// On real hardware this is by far the heaviest BIOS video function: it
    /// looks the mode up in a ROM table and pushes some seventy register
    /// values into the CRTC, sequencer, graphics controller and attribute
    /// controller, then loads the font and the palette and clears memory. We
    /// do the same thing, just with a much smaller table -- but the registers
    /// really are programmed, because software is entitled to read them back
    /// and, more importantly, because the planar write logic in
    /// <see cref="VgaMemory"/> is driven entirely by them.
    /// </summary>
    public void SetVideoMode(byte mode)
    {
        // Bit 7 asks for the screen not to be cleared.
        bool preserveScreen = (mode & 0x80) != 0;
        mode &= 0x7F;

        VgaModeInfo info = VgaModes.Get(mode);

        _registers.VideoMode = mode;
        _registers.TextColumns = info.TextColumns;
        _registers.TextRows = info.TextRows;
        _registers.CharacterHeight = info.CharHeight;

        ProgramRegisters(info);

        _bda.VideoMode = mode;
        _bda.VideoColumns = (ushort)info.TextColumns;
        _bda.VideoPageSize = (ushort)(info.TextColumns * info.TextRows * 2);
        _bda.VideoRowsMinusOne = (byte)(info.TextRows - 1);

        // Homing the cursor and clearing the screen go together. Asking to
        // keep the screen (bit 7) and then moving the cursor to the top left
        // anyway is the worst of both: the old text stays, and whatever is
        // printed next lands on top of it.
        //
        // This is what makes DOS's prompt overwrite lines that are already on
        // screen. MODE re-selects the current text mode to load a code page,
        // asking for the contents to be preserved -- and the cursor jumped
        // back to row zero while eight rows of boot messages stayed put.
        if (!preserveScreen)
        {
            _bda.CursorColumn = 0;
            _bda.CursorRow = 0;
        }

        // The BIOS reloads the DAC on every mode set, which is why a program
        // that changed the palette and then switched modes gets the standard
        // colours back rather than its own.
        _registers.ResetPalette(includeMode13Ramp: info.Layout == VgaMemoryLayout.Linear8);

        if (!preserveScreen) ClearScreen(info);

        // Keep the hardware cursor in step with the BDA, wherever it now is.
        _registers.SetCursorPosition(_bda.CursorColumn, _bda.CursorRow);
        _registers.NotifyModeChanged();
    }

    /// <summary>
    /// Loads the adapter registers that actually change behaviour, rather than
    /// the full ROM table. Three of them matter to us:
    ///
    /// <list type="bullet">
    /// <item><b>Sequencer Map Mask</b> -- all four planes enabled, which is what
    /// every mode starts with. Leaving a stale value here from a previous mode
    /// would silently drop writes to some planes.</item>
    /// <item><b>Sequencer Memory Mode bit 3 (chain-4)</b> -- set only for mode
    /// 13h. It is the single bit that turns the four planes from a planar
    /// arrangement into one linear byte-per-pixel space.</item>
    /// <item><b>Graphics Bit Mask</b> -- opened to 0xFF. A program that left a
    /// narrow mask behind would find its first writes in the new mode changing
    /// only one pixel in eight, which is a memorably baffling symptom.</item>
    /// </list>
    /// </summary>
    private void ProgramRegisters(VgaModeInfo info)
    {
        _registers.SetSequencer(VgaRegisters.SeqMapMask, 0x0F);
        _registers.SetSequencer(VgaRegisters.SeqMemoryMode,
            info.Layout == VgaMemoryLayout.Linear8 ? (byte)0x0E : (byte)0x06);

        // Graphics controller back to its post-mode-set state: no set/reset,
        // write mode 0, read mode 0, bit mask wide open.
        _registers.SetGraphics(VgaRegisters.GcSetReset, 0x00);
        _registers.SetGraphics(VgaRegisters.GcEnableSetReset, 0x00);
        _registers.SetGraphics(VgaRegisters.GcColorCompare, 0x00);
        _registers.SetGraphics(VgaRegisters.GcDataRotate, 0x00);
        _registers.SetGraphics(VgaRegisters.GcReadMapSelect, 0x00);
        _registers.SetGraphics(VgaRegisters.GcMode, info.Layout switch
        {
            // Bit 6 selects 256-colour shift mode, bit 5 the CGA-style
            // interleave. Neither changes anything here, but software reads
            // these back to detect the mode.
            VgaMemoryLayout.Linear8 => 0x40,
            VgaMemoryLayout.Planar4 => 0x00,
            _ => 0x10,   // odd/even, as text modes use
        });
        _registers.SetGraphics(VgaRegisters.GcMiscellaneous,
            info.IsGraphics ? (byte)0x05 : (byte)0x0E);
        _registers.SetGraphics(VgaRegisters.GcColorDontCare, 0x0F);
        _registers.SetGraphics(VgaRegisters.GcBitMask, 0xFF);

        // CRTC 0x13 is the row offset in words. The renderer uses it to step
        // from one scan line to the next, so it has to match the mode.
        _registers.SetCrtc(0x13, (byte)(info.BytesPerRow / 2));
        _registers.SetCrtc(0x09, (byte)((info.CharHeight - 1) & 0x1F));
    }

    private void ClearScreen(VgaModeInfo info)
    {
        if (info.IsGraphics)
        {
            // A graphics mode clears to colour 0 across every plane. Doing it
            // through the planar write path would be correct but pointlessly
            // slow, and would depend on the registers we have just programmed.
            _video.ClearAllPlanes();
            return;
        }

        int cells = info.TextColumns * info.TextRows;
        for (int i = 0; i < cells; i++)
        {
            uint offset = VgaMemory.TextWindowOffset + (uint)(i * 2);
            _video[offset] = (byte)' ';
            _video[offset + 1] = 0x07;   // light grey on black
        }
    }

    /// <summary>
    /// INT 10h AH=0Ch: write one pixel.
    ///
    /// The BIOS pixel calls are notoriously slow -- a full interrupt, a mode
    /// dispatch and several port writes to set one pixel -- which is precisely
    /// why every real graphics program bypasses them and drives the graphics
    /// controller itself. They still have to work: setup programs and simple
    /// utilities use them, and they are the clearest possible demonstration of
    /// what the planar registers are for.
    ///
    /// Bit 7 of AL means XOR the colour with what is already there, which is
    /// how a program draws a cursor it can erase by drawing it again.
    /// </summary>
    private void WritePixel(int x, int y, byte color)
    {
        VgaModeInfo info = _registers.ModeInfo;
        if (!info.IsGraphics) return;
        if ((uint)x >= (uint)info.PixelWidth || (uint)y >= (uint)info.PixelHeight) return;

        if (info.Layout == VgaMemoryLayout.Linear8)
        {
            // 256-colour mode uses the whole byte for the colour index, so
            // there is no spare bit to carry the XOR flag: colour 200 is
            // colour 200, not "XOR with 72". The flag only exists in the
            // 16-colour modes, where four bits are enough for the colour and
            // bit 7 is free.
            uint offset = (uint)(y * info.BytesPerRow + x);
            _video[offset] = color;
            return;
        }

        bool exclusiveOr = (color & 0x80) != 0;

        // Planar. The pixel lives at bit (7 - x % 8) of byte x/8 in each of the
        // four planes, so this is the textbook use of write mode 2 plus a
        // one-bit mask: the hardware spreads the colour's four bits across the
        // planes and the mask confines the change to this single pixel.
        uint byteOffset = (uint)(y * info.BytesPerRow + (x >> 3));
        byte bitMask = (byte)(0x80 >> (x & 7));

        byte savedMode = _registers.Graphics(VgaRegisters.GcMode);
        byte savedMask = _registers.Graphics(VgaRegisters.GcBitMask);
        byte savedRotate = _registers.Graphics(VgaRegisters.GcDataRotate);
        byte savedEnable = _registers.Graphics(VgaRegisters.GcEnableSetReset);

        _registers.SetGraphics(VgaRegisters.GcMode, 0x02);        // write mode 2
        _registers.SetGraphics(VgaRegisters.GcBitMask, bitMask);
        _registers.SetGraphics(VgaRegisters.GcEnableSetReset, 0x00);
        // XOR is the data rotate register's raster op field, bits 3-4.
        _registers.SetGraphics(VgaRegisters.GcDataRotate, exclusiveOr ? (byte)0x18 : (byte)0x00);

        // The dummy read is not optional: it loads the four latches, and the
        // bit mask hands the seven untouched pixels straight back from them.
        // Skip it and those pixels take whatever the latches last happened to
        // hold.
        _video.ReadByte(byteOffset);
        _video.WriteByte(byteOffset, (byte)(color & 0x0F));

        _registers.SetGraphics(VgaRegisters.GcMode, savedMode);
        _registers.SetGraphics(VgaRegisters.GcBitMask, savedMask);
        _registers.SetGraphics(VgaRegisters.GcDataRotate, savedRotate);
        _registers.SetGraphics(VgaRegisters.GcEnableSetReset, savedEnable);
    }

    /// <summary>
    /// INT 10h AH=0Dh: read one pixel back.
    ///
    /// In a planar mode this needs four reads, one per plane, because a plane
    /// only ever hands back its own bit of the colour. That asymmetry -- cheap
    /// writes, expensive reads -- is the reason planar VGA code is written to
    /// avoid reading the screen wherever it possibly can.
    /// </summary>
    private byte ReadPixel(int x, int y)
    {
        VgaModeInfo info = _registers.ModeInfo;
        if (!info.IsGraphics) return 0;
        if ((uint)x >= (uint)info.PixelWidth || (uint)y >= (uint)info.PixelHeight) return 0;

        if (info.Layout == VgaMemoryLayout.Linear8)
            return _video[(uint)(y * info.BytesPerRow + x)];

        uint byteOffset = (uint)(y * info.BytesPerRow + (x >> 3));
        int shift = 7 - (x & 7);

        int color = 0;
        for (int plane = 0; plane < 4; plane++)
            color |= ((_video.ReadPlane(plane, byteOffset) >> shift) & 1) << plane;

        return (byte)color;
    }

    private void SetCursorShape(ushort shape)
    {
        _bda.CursorShape = shape;
    }

    private void SetCursorPosition(byte column, byte row)
    {
        _bda.CursorColumn = column;
        _bda.CursorRow = row;
        _registers.SetCursorPosition(column, row);
    }

    private void GetCursorPosition(CpuNs.Cpu cpu)
    {
        cpu.State.Cx = _bda.CursorShape;
        cpu.State.SetReg8(2, _bda.CursorRow);      // DH
        cpu.State.SetReg8(6, _bda.CursorColumn);   // DL
    }

    private void SetActivePage(byte page)
    {
        _bda.ActivePage = page;
        _bda.VideoPageOffset = (ushort)(page * _bda.VideoPageSize);
    }

    private void GetVideoMode(CpuNs.Cpu cpu)
    {
        cpu.State.SetReg8(0, _bda.VideoMode);                   // AL
        cpu.State.SetReg8(4, (byte)_bda.VideoColumns);          // AH
        cpu.State.SetReg8(7, _bda.ActivePage);                  // BH
    }

    /// <summary>
    /// INT 10h AH=11h: the character generator functions.
    ///
    /// This is how a program gets 43- or 50-row text. It does not ask for a
    /// row count: it asks the BIOS to load a different font, and the row count
    /// follows from the arithmetic. The raster is 400 scanlines tall, so the
    /// 8-line font gives 50 rows and the 14-line font gives 28 -- and on an
    /// EGA, whose raster is 350 lines, that same 8-line font gives 43, which
    /// is why "43-line mode" and "50-line mode" are both spoken of as if they
    /// were modes when they are really just two fonts.
    ///
    /// Subfunctions 0x1x are the same as 0x0x except that they follow up with
    /// a full mode recalculation; we treat them identically because we always
    /// recalculate.
    /// </summary>
    private void CharacterGenerator(CpuNs.Cpu cpu)
    {
        byte subfunction = cpu.State.GetReg8(0);   // AL

        switch (subfunction)
        {
            case 0x01 or 0x11: LoadFont(14); return;   // 8x14, giving 28 rows
            case 0x02 or 0x12: LoadFont(8); return;    // 8x8, giving 50 rows
            case 0x04 or 0x14: LoadFont(16); return;   // 8x16, the default 25 rows
            case 0x30: GetFontPointer(cpu); return;
            default: return;
        }
    }

    private void LoadFont(int characterHeight)
    {
        // Only meaningful in a text mode: in graphics the "font" is whatever
        // the program draws itself.
        if (_registers.IsGraphicsMode) return;

        // 400 scanlines divided by the cell height. The columns do not change:
        // the character cell is eight pixels wide in every one of these fonts.
        const int rasterLines = 400;
        int rows = rasterLines / characterHeight;

        _registers.CharacterHeight = characterHeight;
        _registers.TextRows = rows;
        _registers.SetCrtc(0x09, (byte)((characterHeight - 1) & 0x1F));

        _bda.VideoRowsMinusOne = (byte)(rows - 1);
        _bda.VideoPageSize = (ushort)(_registers.TextColumns * rows * 2);

        // The rows that have just appeared hold whatever was in memory behind
        // the old screen, so they have to be blanked or the new bottom of the
        // screen fills with garbage.
        for (int row = 0; row < rows; row++)
            ClearRow(row, 0x07);

        _registers.NotifyModeChanged();
    }

    private void GetFontPointer(CpuNs.Cpu cpu)
    {
        // The font lives in the emulated ROM; all that is needed here is a
        // valid pointer plus the character height, which is what programs
        // actually use.
        cpu.SetSegment(SegReg.Es, 0xF000);
        cpu.State.Bp = 0x2000;
        cpu.State.Cx = (ushort)_registers.CharacterHeight;
        cpu.State.SetReg8(2, _bda.VideoRowsMinusOne);   // DL
    }

    /// <summary>
    /// Teletype-style output: the character appears at the cursor position, the
    /// cursor advances, and line wrapping and scrolling happen automatically.
    /// This is the function DOS uses for nearly all of its output.
    /// </summary>
    public void TeletypeOutput(byte character, byte page)
    {
        int column = _bda.CursorColumn;
        int row = _bda.CursorRow;
        int columns = _registers.TextColumns;
        int rows = _registers.TextRows;

        switch (character)
        {
            case 0x0D: column = 0; break;                       // carriage return
            case 0x0A: row++; break;                            // line feed
            case 0x08: if (column > 0) column--; break;         // backspace
            case 0x07: break;                                   // bell: no visible effect
            default:
            {
                uint offset = CellAddress(row, column);
                _video[offset] = character;
                // The existing attribute is left alone: function 0x0E writes
                // the character but does not change the colors.
                column++;
                if (column >= columns) { column = 0; row++; }
                break;
            }
        }

        if (row >= rows)
        {
            ScrollScreenUp(1, 0x07, WholeScreen);
            row = rows - 1;
        }

        SetCursorPosition((byte)column, (byte)row);
    }

    private void WriteCharacterWithAttribute(CpuNs.Cpu cpu, bool advance)
    {
        byte character = cpu.State.GetReg8(0);   // AL
        byte attribute = cpu.State.GetReg8(3);   // BL
        ushort count = cpu.State.Cx;

        WriteRepeated(character, attribute, count, writeAttribute: true);
    }

    private void WriteCharacterOnly(CpuNs.Cpu cpu)
    {
        byte character = cpu.State.GetReg8(0);
        ushort count = cpu.State.Cx;

        WriteRepeated(character, 0, count, writeAttribute: false);
    }

    private void WriteRepeated(byte character, byte attribute, ushort count, bool writeAttribute)
    {
        int columns = _registers.TextColumns;
        int position = _bda.CursorRow * columns + _bda.CursorColumn;
        int cells = columns * _registers.TextRows;

        for (int i = 0; i < count && position + i < cells; i++)
        {
            uint offset = CellAddress(position + i);
            _video[offset] = character;
            if (writeAttribute) _video[offset + 1] = attribute;
        }
        // The cursor deliberately does not advance: that is the documented
        // behavior of functions 0x09 and 0x0A.
    }

    private void ReadCharacterAtCursor(CpuNs.Cpu cpu)
    {
        int columns = _registers.TextColumns;
        uint offset = CellAddress(_bda.CursorRow, _bda.CursorColumn);

        cpu.State.SetReg8(0, _video[offset]);        // AL: character
        cpu.State.SetReg8(4, _video[offset + 1]);    // AH: attribute
    }

    private void ScrollUp(CpuNs.Cpu cpu)
    {
        byte lines = cpu.State.GetReg8(0);       // AL, 0 = clear the whole window
        byte attribute = cpu.State.GetReg8(7);   // BH
        if (lines == 0) { ClearRegion(cpu, attribute); return; }
        var area = ReadRegion(cpu);
        ScrollScreenUp(lines, attribute, area);
    }

    private void ScrollDown(CpuNs.Cpu cpu)
    {
        byte lines = cpu.State.GetReg8(0);
        byte attribute = cpu.State.GetReg8(7);
        if (lines == 0) { ClearRegion(cpu, attribute); return; }
        var area = ReadRegion(cpu);
        ScrollScreenDown(lines, attribute, area);
    }

    /// <summary>
    /// Scrolls the screen upwards: this is what happens when text reaches the
    /// bottom. The rows shift up and a blank one appears at the bottom.
    /// </summary>
    /// <summary>
    /// The rectangle the scroll functions operate on: top-left corner in CH/CL,
    /// bottom-right corner in DH/DL.
    /// </summary>
    private readonly record struct Region(int Left, int Top, int Right, int Bottom);

    /// <summary>The whole screen, used by the teletype scroll.</summary>
    private Region WholeScreen
        => new(0, 0, _registers.TextColumns - 1, _registers.TextRows - 1);

    private Region ReadRegion(CpuNs.Cpu cpu)
    {
        // Same 8-bit register ordering trap as everywhere else in INT 10h:
        // 0=AL 1=CL 2=DL 3=BL 4=AH 5=CH 6=DH 7=BH. The right-hand column is DL
        // (index 2) and the bottom row is DH (index 6), never the other way
        // round.
        int left = cpu.State.GetReg8(1);      // CL
        int top = cpu.State.GetReg8(5);       // CH
        int right = cpu.State.GetReg8(2);     // DL
        int bottom = cpu.State.GetReg8(6);    // DH

        right = Math.Min(right, _registers.TextColumns - 1);
        bottom = Math.Min(bottom, _registers.TextRows - 1);
        return new Region(left, top, right, bottom);
    }

    /// <summary>
    /// Scrolls only the given window upwards: this is how a full-screen program
    /// moves the contents of a pane without disturbing the frame drawn around
    /// it.
    /// </summary>
    private void ScrollScreenUp(int lines, byte attribute, Region area)
    {
        int columns = _registers.TextColumns;
        int height = area.Bottom - area.Top + 1;
        if (height <= 0 || area.Right < area.Left) return;
        if (lines >= height) { ClearRegion(area, attribute); return; }

        for (int row = area.Top; row <= area.Bottom - lines; row++)
        for (int column = area.Left; column <= area.Right; column++)
        {
            uint destination = CellAddress(row, column);
            uint source = CellAddress(row + lines, column);
            _video[destination] = _video[source];
            _video[destination + 1] = _video[source + 1];
        }

        for (int row = area.Bottom - lines + 1; row <= area.Bottom; row++)
            ClearRegionRow(row, area, attribute);
    }

    private void ScrollScreenDown(int lines, byte attribute, Region area)
    {
        int columns = _registers.TextColumns;
        int height = area.Bottom - area.Top + 1;
        if (height <= 0 || area.Right < area.Left) return;
        if (lines >= height) { ClearRegion(area, attribute); return; }

        for (int row = area.Bottom; row >= area.Top + lines; row--)
        for (int column = area.Left; column <= area.Right; column++)
        {
            uint destination = CellAddress(row, column);
            uint source = CellAddress(row - lines, column);
            _video[destination] = _video[source];
            _video[destination + 1] = _video[source + 1];
        }

        for (int row = area.Top; row < area.Top + lines; row++)
            ClearRegionRow(row, area, attribute);
    }

    private void ClearRegion(Region area, byte attribute)
    {
        for (int row = area.Top; row <= area.Bottom; row++)
            ClearRegionRow(row, area, attribute);
    }

    private void ClearRegionRow(int row, Region area, byte attribute)
    {
        int columns = _registers.TextColumns;
        for (int column = area.Left; column <= area.Right; column++)
        {
            uint offset = CellAddress(row, column);
            _video[offset] = (byte)' ';
            _video[offset + 1] = attribute;
        }
    }

    private void ClearRow(int row, byte attribute)
    {
        int columns = _registers.TextColumns;
        for (int column = 0; column < columns; column++)
        {
            uint offset = CellAddress(row, column);
            _video[offset] = (byte)' ';
            _video[offset + 1] = attribute;
        }
    }

    /// <summary>Clears the rectangle given by CX (top-left) and DX (bottom-right).</summary>
    private void ClearRegion(CpuNs.Cpu cpu, byte attribute)
    {
        // The 8-bit register indices are 0=AL 1=CL 2=DL 3=BL
        // 4=AH 5=CH 6=DH 7=BH: the right-hand column is in DL (2) and the
        // bottom row in DH (6), not the other way round.
        int left = cpu.State.GetReg8(1);      // CL
        int top = cpu.State.GetReg8(5);       // CH
        int right = cpu.State.GetReg8(2);     // DL
        int bottom = cpu.State.GetReg8(6);    // DH

        int columns = _registers.TextColumns;
        int rows = _registers.TextRows;

        right = Math.Min(right, columns - 1);
        bottom = Math.Min(bottom, rows - 1);

        for (int row = top; row <= bottom; row++)
        for (int column = left; column <= right; column++)
        {
            uint offset = CellAddress(row, column);
            _video[offset] = (byte)' ';
            _video[offset + 1] = attribute;
        }
    }

    /// <summary>Function 0x13: writes an entire string in one call.</summary>
    private void WriteString(CpuNs.Cpu cpu)
    {
        byte mode = cpu.State.GetReg8(0);        // AL
        byte attribute = cpu.State.GetReg8(3);   // BL
        ushort length = cpu.State.Cx;
        byte row = cpu.State.GetReg8(6);         // DH
        byte column = cpu.State.GetReg8(2);      // DL

        SetCursorPosition(column, row);

        for (ushort i = 0; i < length; i++)
        {
            // In modes 2 and 3 the string holds alternating character and
            // attribute bytes; in the others it is characters only.
            bool hasAttributes = (mode & 0x02) != 0;
            ushort offset = (ushort)(cpu.State.Bp + (hasAttributes ? i * 2 : i));
            byte character = cpu.ReadMemoryByte(SegReg.Es, offset);

            if (hasAttributes)
                attribute = cpu.ReadMemoryByte(SegReg.Es, (uint)(offset + 1));

            // Write the character with its attribute and advance the cursor.
            int columns = _registers.TextColumns;
            uint videoOffset = VgaMemory.TextWindowOffset
                             + (uint)((_bda.CursorRow * columns + _bda.CursorColumn) * 2);
            _video[videoOffset] = character;
            _video[videoOffset + 1] = attribute;

            TeletypeAdvance();
        }

        // Bit 0 of the mode decides whether the cursor stays where it started.
        if ((mode & 0x01) == 0) SetCursorPosition(column, row);
    }

    private void TeletypeAdvance()
    {
        int column = _bda.CursorColumn + 1;
        int row = _bda.CursorRow;

        if (column >= _registers.TextColumns) { column = 0; row++; }
        if (row >= _registers.TextRows)
        {
            ScrollScreenUp(1, 0x07, WholeScreen);
            row = _registers.TextRows - 1;
        }

        SetCursorPosition((byte)column, (byte)row);
    }
}
