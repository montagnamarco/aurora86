using Aurora386.Core.IO;

namespace Aurora386.Core.Devices.Video;

/// <summary>
/// Registri della scheda VGA.
///
/// La VGA non ha registri separati per ogni funzione: ha pochi indirizzi di
/// porta e molti registri interni, selezionati scrivendo prima un indice.
/// Il gruppo piu' importante e' il CRTC (0x3D4/0x3D5), che decide dimensioni
/// dello schermo, posizione del cursore e indirizzo di partenza.
/// </summary>
public sealed class VgaRegisters : IIoDevice
{
    // --- CRTC ----------------------------------------------------------------
    private readonly byte[] _crtc = new byte[32];
    private byte _crtcIndex;

    // --- sequencer, controllo grafica, attributi ------------------------------
    private readonly byte[] _sequencer = new byte[8];
    private byte _sequencerIndex;

    private readonly byte[] _graphics = new byte[16];
    private byte _graphicsIndex;

    private readonly byte[] _attribute = new byte[32];
    private byte _attributeIndex;
    private bool _attributeExpectingIndex = true;

    // --- tavolozza (DAC) -------------------------------------------------------
    /// <summary>256 colori da tre componenti a 6 bit ciascuna.</summary>
    private readonly byte[] _palette = new byte[256 * 3];
    private byte _dacWriteIndex;
    private byte _dacReadIndex;
    private int _dacComponent;

    private byte _miscOutput = 0x67;

    /// <summary>Modalita' video corrente, come numero di modo BIOS.</summary>
    public byte VideoMode { get; set; } = 0x03;

    /// <summary>true nelle modalita' grafiche, false in quelle testuali.</summary>
    public bool IsGraphicsMode => VideoMode is not (0x00 or 0x01 or 0x02 or 0x03 or 0x07);

    public int TextColumns { get; set; } = 80;
    public int TextRows { get; set; } = 25;

    /// <summary>Notifica un cambio di modalita', per forzare il ridisegno.</summary>
    public event Action? ModeChanged;

    public VgaRegisters()
    {
        // Tavolozza di default: i 16 colori CGA nei primi sedici posti, che
        // sono quelli usati in modalita' testo.
        LoadDefaultPalette();

        // Valori CRTC per il modo testo 80x25, che e' come si accende un PC.
        _crtc[0x01] = 79;    // ultima colonna visibile
        _crtc[0x12] = 199;   // ultima riga visibile
        _crtc[0x0A] = 13;    // riga iniziale del cursore
        _crtc[0x0B] = 14;    // riga finale del cursore
    }

    public void Register(IoBus io)
    {
        io.Register(0x3C0, 2, this);    // attributi
        io.Register(0x3C4, 2, this);    // sequencer
        io.Register(0x3C6, 4, this);    // DAC
        io.Register(0x3CE, 2, this);    // controllo grafica
        io.Register(0x3D4, 2, this);    // CRTC
        io.Register(0x3DA, this);       // stato
        io.Register(0x3CC, this);       // uscita varia (lettura)
        io.Register(0x3C2, this);       // uscita varia (scrittura)
    }

    // --- posizione del cursore ------------------------------------------------
    // Il cursore sta in due registri CRTC a 8 bit che insieme formano un
    // offset in celle dall'inizio della memoria video.

    public ushort CursorAddress
    {
        get => (ushort)((_crtc[0x0E] << 8) | _crtc[0x0F]);
        set
        {
            _crtc[0x0E] = (byte)(value >> 8);
            _crtc[0x0F] = (byte)value;
        }
    }

    public int CursorColumn => CursorAddress % TextColumns;
    public int CursorRow => CursorAddress / TextColumns;

    public void SetCursorPosition(int column, int row)
        => CursorAddress = (ushort)(row * TextColumns + column);

    /// <summary>Indirizzo di partenza della zona visibile, per lo scorrimento hardware.</summary>
    public ushort StartAddress => (ushort)((_crtc[0x0C] << 8) | _crtc[0x0D]);

    /// <summary>true se il cursore e' nascosto (bit 5 del registro 0x0A).</summary>
    public bool CursorVisible => (_crtc[0x0A] & 0x20) == 0;

    public byte ReadByte(ushort port)
    {
        switch (port)
        {
            case 0x3C0: return _attributeIndex;
            case 0x3C1: return _attribute[_attributeIndex & 0x1F];
            case 0x3C4: return _sequencerIndex;
            case 0x3C5: return _sequencer[_sequencerIndex & 7];
            case 0x3C7: return 0;
            case 0x3C8: return _dacWriteIndex;
            case 0x3C9:
            {
                byte value = _palette[_dacReadIndex * 3 + _dacComponent];
                if (++_dacComponent == 3) { _dacComponent = 0; _dacReadIndex++; }
                return value;
            }
            case 0x3CC: return _miscOutput;
            case 0x3CE: return _graphicsIndex;
            case 0x3CF: return _graphics[_graphicsIndex & 0x0F];
            case 0x3D4: return _crtcIndex;
            case 0x3D5: return _crtc[_crtcIndex & 0x1F];
            case 0x3DA:
            {
                // Registro di stato. I bit 0 e 3 riportano il ritracciamento
                // del pennello: molti programmi li interrogano in attesa del
                // momento giusto per scrivere senza sfarfallio. Vanno fatti
                // oscillare, altrimenti quei programmi restano bloccati.
                _attributeExpectingIndex = true;   // la lettura reimposta il registro attributi
                _retraceToggle = !_retraceToggle;
                return (byte)(_retraceToggle ? 0x09 : 0x00);
            }
            default: return 0xFF;
        }
    }

    private bool _retraceToggle;

    public void WriteByte(ushort port, byte value)
    {
        switch (port)
        {
            case 0x3C0:
                // La stessa porta riceve alternatamente indice e dato.
                if (_attributeExpectingIndex) _attributeIndex = value;
                else _attribute[_attributeIndex & 0x1F] = value;
                _attributeExpectingIndex = !_attributeExpectingIndex;
                return;

            case 0x3C2: _miscOutput = value; return;
            case 0x3C4: _sequencerIndex = value; return;
            case 0x3C5: _sequencer[_sequencerIndex & 7] = value; return;

            case 0x3C7: _dacReadIndex = value; _dacComponent = 0; return;
            case 0x3C8: _dacWriteIndex = value; _dacComponent = 0; return;
            case 0x3C9:
            {
                // Le componenti arrivano una alla volta: rosso, verde, blu.
                _palette[_dacWriteIndex * 3 + _dacComponent] = (byte)(value & 0x3F);
                if (++_dacComponent == 3) { _dacComponent = 0; _dacWriteIndex++; }
                return;
            }

            case 0x3CE: _graphicsIndex = value; return;
            case 0x3CF: _graphics[_graphicsIndex & 0x0F] = value; return;
            case 0x3D4: _crtcIndex = value; return;
            case 0x3D5: _crtc[_crtcIndex & 0x1F] = value; return;
        }
    }

    /// <summary>Colore della tavolozza come RGB a 8 bit per componente.</summary>
    public (byte R, byte G, byte B) GetPaletteColor(int index)
    {
        int offset = (index & 0xFF) * 3;
        // Le componenti VGA sono a 6 bit: la conversione a 8 bit replica i due
        // bit alti in fondo, cosi' 0x3F diventa 0xFF invece di 0xFC.
        return (Expand(_palette[offset]), Expand(_palette[offset + 1]), Expand(_palette[offset + 2]));

        static byte Expand(byte sixBits) => (byte)((sixBits << 2) | (sixBits >> 4));
    }

    public void NotifyModeChanged() => ModeChanged?.Invoke();

    private void LoadDefaultPalette()
    {
        // I 16 colori standard del PC, in componenti a 6 bit.
        ReadOnlySpan<byte> cga = [
            0x00, 0x00, 0x00,  // nero
            0x00, 0x00, 0x2A,  // blu
            0x00, 0x2A, 0x00,  // verde
            0x00, 0x2A, 0x2A,  // ciano
            0x2A, 0x00, 0x00,  // rosso
            0x2A, 0x00, 0x2A,  // magenta
            0x2A, 0x15, 0x00,  // marrone
            0x2A, 0x2A, 0x2A,  // grigio chiaro
            0x15, 0x15, 0x15,  // grigio scuro
            0x15, 0x15, 0x3F,  // blu chiaro
            0x15, 0x3F, 0x15,  // verde chiaro
            0x15, 0x3F, 0x3F,  // ciano chiaro
            0x3F, 0x15, 0x15,  // rosso chiaro
            0x3F, 0x15, 0x3F,  // magenta chiaro
            0x3F, 0x3F, 0x15,  // giallo
            0x3F, 0x3F, 0x3F,  // bianco
        ];
        cga.CopyTo(_palette);
    }
}
