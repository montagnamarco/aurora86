namespace Aurora386.Core.Devices.Video;

/// <summary>
/// Converte la memoria video in un framebuffer di pixel RGBA pronto per essere
/// caricato come texture.
///
/// Il rendering avviene per righe di caratteri e solo su quelle marcate come
/// modificate: un prompt DOS cambia poche celle per volta, e riconvertire
/// 80x25 caratteri a ogni fotogramma sarebbe lavoro quasi tutto sprecato.
/// </summary>
public sealed class VgaRenderer
{
    private readonly VgaMemory _memory;
    private readonly VgaRegisters _registers;

    /// <summary>Pixel in formato RGBA, quattro byte per pixel.</summary>
    private uint[] _framebuffer;

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Fotogrammi disegnati, per diagnostica.</summary>
    public long FramesRendered { get; private set; }

    /// <summary>Stato lampeggiante del cursore, alternato dal chiamante.</summary>
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
    /// Aggiorna il framebuffer. Ritorna true se qualcosa e' cambiato, cosi'
    /// il chiamante puo' evitare di ricaricare la texture inutilmente.
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
            // La riga del cursore va ridisegnata comunque: il cursore lampeggia
            // senza che la memoria cambi.
            bool hasCursor = row == cursorRow && _registers.CursorVisible;
            if (!force && !_memory.IsRowDirty(row) && !hasCursor) continue;

            for (int column = 0; column < columns; column++)
            {
                uint offset = baseOffset + (uint)((row * columns + column) * 2);

                // Ogni cella di testo e' due byte: carattere e attributo.
                byte character = _memory[offset];
                byte attribute = _memory[offset + 1];

                // L'attributo impacchetta due colori: i quattro bit bassi il
                // primo piano, i tre successivi lo sfondo. Il bit 7 e' il
                // lampeggio, che qui viene reso come sfondo intenso.
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
                // Il bit piu' significativo e' il pixel piu' a sinistra.
                bool on = (bits & (0x80 >> x)) != 0;
                _framebuffer[destination + x] = on ? foregroundColor : backgroundColor;
            }
        }

        if (!drawCursor) return;

        // Il cursore e' un blocco pieno sulle ultime due righe della cella,
        // disegnato invertendo i colori.
        for (int y = VgaFont.CharHeight - 3; y < VgaFont.CharHeight - 1; y++)
        {
            int destination = (originY + y) * Width + originX;
            for (int x = 0; x < VgaFont.CharWidth; x++)
                _framebuffer[destination + x] = foregroundColor;
        }
    }

    /// <summary>
    /// Modalita' grafiche. Per ora e' implementata la 13h (320x200 a 256
    /// colori), che e' lineare e quindi diretta da convertire; le modalita' a
    /// piani (EGA e SVGA) richiedono la logica dei quattro piani e arriveranno
    /// con il resto del supporto SVGA.
    /// </summary>
    private void RenderGraphics()
    {
        if (_registers.VideoMode != 0x13)
        {
            // Modalita' non ancora supportata: schermo nero invece di
            // interpretare male la memoria e mostrare rumore.
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

        // Nel modo 13h ogni byte e' un pixel, indice nella tavolozza.
        for (int i = 0; i < graphicsWidth * graphicsHeight; i++)
            _framebuffer[i] = ToRgba(_registers.GetPaletteColor(_memory[(uint)i]));
    }

    private static uint ToRgba((byte R, byte G, byte B) color)
        => (uint)(color.R | (color.G << 8) | (color.B << 16) | (0xFF << 24));
}
