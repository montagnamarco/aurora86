using Emulator.Core.Cpu;
using CpuNs = Emulator.Core.Cpu;
using Emulator.Core.Devices.Video;

namespace Emulator.Core.Bios;

/// <summary>
/// Funzioni video del BIOS (INT 10h). Sono il modo in cui DOS e i programmi
/// scrivono sullo schermo senza toccare direttamente l'hardware.
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
            // DH e' la riga, DL la colonna: nell'ordine dei registri a 8 bit
            // sono gli indici 6 e 2, e vanno passati in quest'ordine.
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
                // Funzioni sui font. La 0x30 restituisce un puntatore alla
                // tabella dei caratteri, che alcuni programmi leggono.
                if (cpu.State.GetReg8(0) == 0x30) GetFontPointer(cpu);
                return;

            case 0x12:
                // Funzioni EGA/VGA di configurazione. La 0x10 riporta quanta
                // memoria ha la scheda.
                if (cpu.State.GetReg8(3) == 0x10)
                {
                    cpu.State.Bx = 0x0003;   // 256 KB di memoria video
                    cpu.State.Cx = 0x0009;
                }
                return;

            case 0x1A:
                // Codice di combinazione video: identifica il tipo di scheda.
                cpu.State.SetReg8(0, 0x1A);    // funzione supportata
                cpu.State.Bx = 0x0808;         // VGA a colori
                return;

            default:
                // Funzione non implementata: si ritorna senza fare nulla,
                // che e' il comportamento meno dannoso.
                return;
        }
    }

    private void SetVideoMode(byte mode)
    {
        // Il bit 7 chiede di non cancellare lo schermo.
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
            _video[offset + 1] = 0x07;   // grigio su nero
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
        // Il font vive nella ROM emulata; qui basta un puntatore valido e
        // l'altezza dei caratteri, che e' cio' che i programmi usano davvero.
        cpu.SetSegment(SegReg.Es, 0xF000);
        cpu.State.Bp = 0x2000;
        cpu.State.Cx = VgaFont.CharHeight;
        cpu.State.SetReg8(2, _bda.VideoRowsMinusOne);   // DL
    }

    /// <summary>
    /// Scrittura in stile telescrivente: il carattere appare alla posizione
    /// del cursore, che avanza, con ritorno a capo e scorrimento automatici.
    /// E' la funzione che DOS usa per quasi tutto l'output.
    /// </summary>
    public void TeletypeOutput(byte character, byte page)
    {
        int column = _bda.CursorColumn;
        int row = _bda.CursorRow;
        int columns = _registers.TextColumns;
        int rows = _registers.TextRows;

        switch (character)
        {
            case 0x0D: column = 0; break;                       // ritorno carrello
            case 0x0A: row++; break;                            // avanzamento riga
            case 0x08: if (column > 0) column--; break;         // backspace
            case 0x07: break;                                   // campanello: nessun effetto visivo
            default:
            {
                uint offset = VgaMemory.TextWindowOffset + (uint)((row * columns + column) * 2);
                _video[offset] = character;
                // L'attributo esistente viene preservato: la funzione 0x0E
                // scrive il carattere ma non cambia i colori.
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
        // Il cursore non avanza: e' il comportamento documentato delle
        // funzioni 0x09 e 0x0A.
    }

    private void ReadCharacterAtCursor(CpuNs.Cpu cpu)
    {
        int columns = _registers.TextColumns;
        uint offset = VgaMemory.TextWindowOffset
                    + (uint)((_bda.CursorRow * columns + _bda.CursorColumn) * 2);

        cpu.State.SetReg8(0, _video[offset]);        // AL: carattere
        cpu.State.SetReg8(4, _video[offset + 1]);    // AH: attributo
    }

    private void ScrollUp(CpuNs.Cpu cpu)
    {
        byte lines = cpu.State.GetReg8(0);       // AL, 0 = cancella tutto
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
    /// Fa scorrere lo schermo verso l'alto: e' cio' che accade quando il testo
    /// arriva in fondo. Le righe si spostano e in basso ne compare una vuota.
    /// </summary>
    /// <summary>
    /// Rettangolo su cui agiscono le funzioni di scorrimento: angolo alto a
    /// sinistra in CH/CL, angolo basso a destra in DH/DL.
    /// </summary>
    private readonly record struct Regione(int Left, int Top, int Right, int Bottom);

    /// <summary>L'intero schermo, per lo scorrimento del teletype.</summary>
    private Regione SchermoIntero
        => new(0, 0, _registers.TextColumns - 1, _registers.TextRows - 1);

    private Regione LeggiRegione(CpuNs.Cpu cpu)
    {
        int left = cpu.State.GetReg8(1);      // CL
        int top = cpu.State.GetReg8(5);       // CH
        int right = cpu.State.GetReg8(2);     // DL
        int bottom = cpu.State.GetReg8(6);    // DH

        right = Math.Min(right, _registers.TextColumns - 1);
        bottom = Math.Min(bottom, _registers.TextRows - 1);
        return new Regione(left, top, right, bottom);
    }

    /// <summary>
    /// Fa scorrere verso l'alto la sola finestra indicata: e' cosi' che un
    /// programma a schermate muove il contenuto di un riquadro senza toccare
    /// la cornice che gli sta intorno.
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

    /// <summary>Cancella il rettangolo indicato da CX (angolo alto) e DX (basso).</summary>
    private void ClearRegion(CpuNs.Cpu cpu, byte attribute)
    {
        // Gli indici dei registri a 8 bit sono 0=AL 1=CL 2=DL 3=BL
        // 4=AH 5=CH 6=DH 7=BH: la colonna destra sta in DL (2) e la riga
        // inferiore in DH (6), non il contrario.
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

    /// <summary>Funzione 0x13: scrive una stringa intera in un colpo solo.</summary>
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
            // Nei modi 2 e 3 la stringa contiene carattere e attributo
            // alternati, negli altri solo caratteri.
            bool hasAttributes = (mode & 0x02) != 0;
            ushort offset = (ushort)(cpu.State.Bp + (hasAttributes ? i * 2 : i));
            byte character = cpu.ReadMemoryByte(SegReg.Es, offset);

            if (hasAttributes)
                attribute = cpu.ReadMemoryByte(SegReg.Es, (uint)(offset + 1));

            // Scrive il carattere con il suo attributo e avanza il cursore.
            int columns = _registers.TextColumns;
            uint videoOffset = VgaMemory.TextWindowOffset
                             + (uint)((_bda.CursorRow * columns + _bda.CursorColumn) * 2);
            _video[videoOffset] = character;
            _video[videoOffset + 1] = attribute;

            TeletypeAdvance();
        }

        // Il bit 0 del modo decide se il cursore resta dove era.
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
