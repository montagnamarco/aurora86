using Aurora386.Core.IO;

namespace Aurora386.Core.Devices.Video;

/// <summary>
/// VGA register file.
///
/// The VGA does not give each function its own port. It exposes a handful of
/// port addresses and hides dozens of internal registers behind them, each
/// selected by first writing an index to an address port and then reading or
/// writing the data port next to it. This indexed scheme is why VGA
/// programming is so order-sensitive, and why an interrupt landing between
/// the index write and the data write can corrupt an unrelated register.
///
/// The most important group is the CRTC (0x3D4/0x3D5), which sets the screen
/// dimensions, the cursor position and shape, and the start address of the
/// visible region.
/// </summary>
public sealed class VgaRegisters : IIoDevice
{
    // --- CRTC ----------------------------------------------------------------
    private readonly byte[] _crtc = new byte[32];
    private byte _crtcIndex;

    // --- sequencer, graphics controller, attribute controller -----------------
    private readonly byte[] _sequencer = new byte[8];
    private byte _sequencerIndex;

    private readonly byte[] _graphics = new byte[16];
    private byte _graphicsIndex;

    private readonly byte[] _attribute = new byte[32];
    private byte _attributeIndex;
    private bool _attributeExpectingIndex = true;

    // --- palette (DAC) ---------------------------------------------------------
    /// <summary>256 colours, three 6-bit components each.</summary>
    private readonly byte[] _palette = new byte[256 * 3];
    private byte _dacWriteIndex;
    private byte _dacReadIndex;
    private int _dacComponent;

    private byte _miscOutput = 0x67;

    /// <summary>Current video mode, as a BIOS mode number.</summary>
    public byte VideoMode { get; set; } = 0x03;

    /// <summary>True in graphics modes, false in text modes.</summary>
    public bool IsGraphicsMode => VideoMode is not (0x00 or 0x01 or 0x02 or 0x03 or 0x07);

    public int TextColumns { get; set; } = 80;
    public int TextRows { get; set; } = 25;

    /// <summary>Raised on a mode change, so the renderer can resize and redraw everything.</summary>
    public event Action? ModeChanged;

    public VgaRegisters()
    {
        // Default palette: the 16 CGA colours in the first sixteen slots,
        // which is what text mode draws with.
        LoadDefaultPalette();

        // CRTC values for 80x25 text, which is how a PC comes up.
        _crtc[0x01] = 79;    // last displayed column
        _crtc[0x12] = 199;   // last displayed scanline
        _crtc[0x0A] = 13;    // cursor start scanline within the cell
        _crtc[0x0B] = 14;    // cursor end scanline within the cell
    }

    public void Register(IoBus io)
    {
        io.Register(0x3C0, 2, this);    // attribute controller
        io.Register(0x3C4, 2, this);    // sequencer
        io.Register(0x3C6, 4, this);    // DAC / palette
        io.Register(0x3CE, 2, this);    // graphics controller
        io.Register(0x3D4, 2, this);    // CRTC
        io.Register(0x3DA, this);       // input status 1
        io.Register(0x3CC, this);       // miscellaneous output (read)
        io.Register(0x3C2, this);       // miscellaneous output (write)
    }

    // --- cursor position ------------------------------------------------------
    // The cursor lives in two 8-bit CRTC registers that together form an
    // offset, measured in character cells, from the start of video memory --
    // not a row/column pair, which is why moving it needs a multiplication.

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

    /// <summary>Start address of the visible region; changing it scrolls in hardware.</summary>
    public ushort StartAddress => (ushort)((_crtc[0x0C] << 8) | _crtc[0x0D]);

    /// <summary>True when the cursor is visible; bit 5 of CRTC register 0x0A hides it.</summary>
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
                // Input status register 1. Bits 0 and 3 report horizontal and
                // vertical retrace: software polls them to find the moment
                // when the beam is off-screen and video memory can be written
                // without tearing. They must be made to toggle, or any program
                // waiting for retrace spins forever.
                _attributeExpectingIndex = true;   // reading here resets the attribute flip-flop
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
                // One port, alternating between index and data on successive
                // writes. An internal flip-flop tracks which is expected next,
                // and reading 0x3DA is how software resynchronises it.
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
                // Components arrive one at a time, red then green then blue,
                // with the index auto-incrementing after the third.
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

    /// <summary>A palette entry expanded to 8 bits per component.</summary>
    public (byte R, byte G, byte B) GetPaletteColor(int index)
    {
        int offset = (index & 0xFF) * 3;
        // VGA components are 6-bit. Expanding to 8 bits by replicating the
        // top two bits at the bottom makes full-scale 0x3F map to 0xFF rather
        // than 0xFC, so white really is white instead of very slightly grey.
        return (Expand(_palette[offset]), Expand(_palette[offset + 1]), Expand(_palette[offset + 2]));

        static byte Expand(byte sixBits) => (byte)((sixBits << 2) | (sixBits >> 4));
    }

    public void NotifyModeChanged() => ModeChanged?.Invoke();

    private void LoadDefaultPalette()
    {
        // The 16 standard PC colours, as 6-bit components. Brown is the odd
        // one out: it would otherwise come out as dark yellow, so IBM gave it
        // a halved green component to make it look like a distinct colour.
        ReadOnlySpan<byte> cga = [
            0x00, 0x00, 0x00,  // black
            0x00, 0x00, 0x2A,  // blue
            0x00, 0x2A, 0x00,  // green
            0x00, 0x2A, 0x2A,  // cyan
            0x2A, 0x00, 0x00,  // red
            0x2A, 0x00, 0x2A,  // magenta
            0x2A, 0x15, 0x00,  // brown
            0x2A, 0x2A, 0x2A,  // light grey
            0x15, 0x15, 0x15,  // dark grey
            0x15, 0x15, 0x3F,  // light blue
            0x15, 0x3F, 0x15,  // light green
            0x15, 0x3F, 0x3F,  // light cyan
            0x3F, 0x15, 0x15,  // light red
            0x3F, 0x15, 0x3F,  // light magenta
            0x3F, 0x3F, 0x15,  // yellow
            0x3F, 0x3F, 0x3F,  // white
        ];
        cga.CopyTo(_palette);
    }
}
