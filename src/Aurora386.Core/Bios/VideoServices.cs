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
            case 0x0E: TeletypeOutput(cpu.State.GetReg8(0), cpu.State.GetReg8(3)); return;
            case 0x0F: GetVideoMode(cpu); return;
            case 0x13: WriteString(cpu); return;

            case 0x11:
                // Font functions. Subfunction 0x30 returns a pointer to the
                // character table, which some programs read.
                if (cpu.State.GetReg8(0) == 0x30) GetFontPointer(cpu);
                return;

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

    private void SetVideoMode(byte mode)
    {
        // Bit 7 asks for the screen not to be cleared.
        bool preserveScreen = (mode & 0x80) != 0;
        mode &= 0x7F;

        _registers.VideoMode = mode;

        (int columns, int rows) = mode switch
        {
            0x00 or 0x01 => (40, 25),
            0x02 or 0x03 => (80, 25),
            0x07 => (80, 25),
            _ => (80, 25),
        };

        _registers.TextColumns = columns;
        _registers.TextRows = rows;

        _bda.VideoMode = mode;
        _bda.VideoColumns = (ushort)columns;
        _bda.VideoPageSize = (ushort)(columns * rows * 2);
        _bda.VideoRowsMinusOne = (byte)(rows - 1);
        _bda.CursorColumn = 0;
        _bda.CursorRow = 0;

        if (!preserveScreen) ClearScreen();

        _registers.SetCursorPosition(0, 0);
        _registers.NotifyModeChanged();
    }

    private void ClearScreen()
    {
        int cells = _registers.TextColumns * _registers.TextRows;
        for (int i = 0; i < cells; i++)
        {
            uint offset = VgaMemory.TextWindowOffset + (uint)(i * 2);
            _video[offset] = (byte)' ';
            _video[offset + 1] = 0x07;   // light grey on black
        }
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

    private void GetFontPointer(CpuNs.Cpu cpu)
    {
        // The font lives in the emulated ROM; all that is needed here is a
        // valid pointer plus the character height, which is what programs
        // actually use.
        cpu.SetSegment(SegReg.Es, 0xF000);
        cpu.State.Bp = 0x2000;
        cpu.State.Cx = VgaFont.CharHeight;
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
                uint offset = VgaMemory.TextWindowOffset + (uint)((row * columns + column) * 2);
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
            ScrollScreenUp(1, 0x07, SchermoIntero);
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
            uint offset = VgaMemory.TextWindowOffset + (uint)((position + i) * 2);
            _video[offset] = character;
            if (writeAttribute) _video[offset + 1] = attribute;
        }
        // The cursor deliberately does not advance: that is the documented
        // behavior of functions 0x09 and 0x0A.
    }

    private void ReadCharacterAtCursor(CpuNs.Cpu cpu)
    {
        int columns = _registers.TextColumns;
        uint offset = VgaMemory.TextWindowOffset
                    + (uint)((_bda.CursorRow * columns + _bda.CursorColumn) * 2);

        cpu.State.SetReg8(0, _video[offset]);        // AL: character
        cpu.State.SetReg8(4, _video[offset + 1]);    // AH: attribute
    }

    private void ScrollUp(CpuNs.Cpu cpu)
    {
        byte lines = cpu.State.GetReg8(0);       // AL, 0 = clear the whole window
        byte attribute = cpu.State.GetReg8(7);   // BH
        if (lines == 0) { ClearRegion(cpu, attribute); return; }
        var area = LeggiRegione(cpu);
        ScrollScreenUp(lines, attribute, area);
    }

    private void ScrollDown(CpuNs.Cpu cpu)
    {
        byte lines = cpu.State.GetReg8(0);
        byte attribute = cpu.State.GetReg8(7);
        if (lines == 0) { ClearRegion(cpu, attribute); return; }
        var area = LeggiRegione(cpu);
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
    private readonly record struct Regione(int Left, int Top, int Right, int Bottom);

    /// <summary>The whole screen, used by the teletype scroll.</summary>
    private Regione SchermoIntero
        => new(0, 0, _registers.TextColumns - 1, _registers.TextRows - 1);

    private Regione LeggiRegione(CpuNs.Cpu cpu)
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
        return new Regione(left, top, right, bottom);
    }

    /// <summary>
    /// Scrolls only the given window upwards: this is how a full-screen program
    /// moves the contents of a pane without disturbing the frame drawn around
    /// it.
    /// </summary>
    private void ScrollScreenUp(int lines, byte attribute, Regione area)
    {
        int columns = _registers.TextColumns;
        int altezza = area.Bottom - area.Top + 1;
        if (altezza <= 0 || area.Right < area.Left) return;
        if (lines >= altezza) { ClearRegione(area, attribute); return; }

        for (int row = area.Top; row <= area.Bottom - lines; row++)
        for (int column = area.Left; column <= area.Right; column++)
        {
            uint destinazione = VgaMemory.TextWindowOffset + (uint)((row * columns + column) * 2);
            uint sorgente = VgaMemory.TextWindowOffset
                          + (uint)(((row + lines) * columns + column) * 2);
            _video[destinazione] = _video[sorgente];
            _video[destinazione + 1] = _video[sorgente + 1];
        }

        for (int row = area.Bottom - lines + 1; row <= area.Bottom; row++)
            ClearRigaRegione(row, area, attribute);
    }

    private void ScrollScreenDown(int lines, byte attribute, Regione area)
    {
        int columns = _registers.TextColumns;
        int altezza = area.Bottom - area.Top + 1;
        if (altezza <= 0 || area.Right < area.Left) return;
        if (lines >= altezza) { ClearRegione(area, attribute); return; }

        for (int row = area.Bottom; row >= area.Top + lines; row--)
        for (int column = area.Left; column <= area.Right; column++)
        {
            uint destinazione = VgaMemory.TextWindowOffset + (uint)((row * columns + column) * 2);
            uint sorgente = VgaMemory.TextWindowOffset
                          + (uint)(((row - lines) * columns + column) * 2);
            _video[destinazione] = _video[sorgente];
            _video[destinazione + 1] = _video[sorgente + 1];
        }

        for (int row = area.Top; row < area.Top + lines; row++)
            ClearRigaRegione(row, area, attribute);
    }

    private void ClearRegione(Regione area, byte attribute)
    {
        for (int row = area.Top; row <= area.Bottom; row++)
            ClearRigaRegione(row, area, attribute);
    }

    private void ClearRigaRegione(int row, Regione area, byte attribute)
    {
        int columns = _registers.TextColumns;
        for (int column = area.Left; column <= area.Right; column++)
        {
            uint offset = VgaMemory.TextWindowOffset + (uint)((row * columns + column) * 2);
            _video[offset] = (byte)' ';
            _video[offset + 1] = attribute;
        }
    }

    private void ClearRow(int row, byte attribute)
    {
        int columns = _registers.TextColumns;
        for (int column = 0; column < columns; column++)
        {
            uint offset = VgaMemory.TextWindowOffset + (uint)((row * columns + column) * 2);
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
            uint offset = VgaMemory.TextWindowOffset + (uint)((row * columns + column) * 2);
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
            ScrollScreenUp(1, 0x07, SchermoIntero);
            row = _registers.TextRows - 1;
        }

        SetCursorPosition((byte)column, (byte)row);
    }
}
