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
/// visible region. The Sequencer (0x3C4/0x3C5) and the Graphics Controller
/// (0x3CE/0x3CF) are what turn a plain byte write at 0xA0000 into an update
/// of up to four bit planes at once; see <see cref="VgaMemory"/> for how
/// those two groups cooperate.
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

    // --- named register indices ----------------------------------------------
    // Naming these is not decoration: the whole planar write path is four
    // registers deep, and "_graphics[0x08]" tells a reader nothing about why
    // a byte lost half its bits on the way to memory.

    /// <summary>Sequencer index 0x02: which planes accept CPU writes.</summary>
    public const int SeqMapMask = 0x02;

    /// <summary>Sequencer index 0x04: memory mode (bit 3 = chain-4, i.e. mode 13h).</summary>
    public const int SeqMemoryMode = 0x04;

    /// <summary>Graphics index 0x00: the colour written when Enable Set/Reset says so.</summary>
    public const int GcSetReset = 0x00;

    /// <summary>Graphics index 0x01: per-plane switch between Set/Reset and CPU data.</summary>
    public const int GcEnableSetReset = 0x01;

    /// <summary>Graphics index 0x02: reference colour for read mode 1.</summary>
    public const int GcColorCompare = 0x02;

    /// <summary>Graphics index 0x03: raster op (bits 3-4) and rotate count (bits 0-2).</summary>
    public const int GcDataRotate = 0x03;

    /// <summary>Graphics index 0x04: which plane read mode 0 returns.</summary>
    public const int GcReadMapSelect = 0x04;

    /// <summary>Graphics index 0x05: write mode (bits 0-1) and read mode (bit 3).</summary>
    public const int GcMode = 0x05;

    /// <summary>Graphics index 0x06: memory map select and the text/graphics flag.</summary>
    public const int GcMiscellaneous = 0x06;

    /// <summary>Graphics index 0x07: planes that take part in read mode 1's compare.</summary>
    public const int GcColorDontCare = 0x07;

    /// <summary>Graphics index 0x08: which bits of the target byte may change.</summary>
    public const int GcBitMask = 0x08;

    /// <summary>Current video mode, as a BIOS mode number.</summary>
    public byte VideoMode
    {
        get => _videoMode;
        set
        {
            _videoMode = value;
            ModeInfo = VgaModes.Get(value);
        }
    }

    private byte _videoMode = 0x03;

    /// <summary>Geometry and memory organisation of the current mode.</summary>
    public VgaModeInfo ModeInfo { get; private set; } = VgaModes.Text80x25;

    /// <summary>True in graphics modes, false in text modes.</summary>
    public bool IsGraphicsMode => ModeInfo.IsGraphics;

    /// <summary>
    /// The mode the registers actually describe, which is what the monitor
    /// on a real VGA would show. Normally it agrees with the BIOS table; it
    /// differs when software programs the controller directly, without an
    /// INT 10h mode set -- Windows' VDD does exactly that when it hands the
    /// display back to the Windows VM after a full-screen DOS session, and
    /// a renderer that trusted the BIOS mode number kept showing text.
    /// Graphics is the graphics controller's "alphanumeric disable" bit;
    /// 256-colour is its shift-256 bit; the geometry comes from the CRTC.
    /// </summary>
    public VgaModeInfo EffectiveMode
    {
        get
        {
            bool graphics = (_graphics[GcMiscellaneous] & 0x01) != 0;
            if (graphics == ModeInfo.IsGraphics) return ModeInfo;
            if (!graphics)
                return new VgaModeInfo(ModeInfo.Mode, VgaMemoryLayout.Text, 0, 0, TextColumns, TextRows, CharacterHeight);

            bool linear8 = (_graphics[GcMode] & 0x40) != 0;
            int width = (_crtc[0x01] + 1) * (linear8 ? 4 : 8);
            int verticalEnd = _crtc[0x12] | ((_crtc[0x07] & 0x02) << 7) | ((_crtc[0x07] & 0x40) << 3);
            int rows = verticalEnd + 1;
            int repeat = (_crtc[0x09] & 0x1F) + 1;
            if ((_crtc[0x09] & 0x80) != 0) repeat *= 2;
            int height = Math.Max(1, rows / repeat);
            return new VgaModeInfo(ModeInfo.Mode, linear8 ? VgaMemoryLayout.Linear8 : VgaMemoryLayout.Planar4,
                                   width, height, TextColumns, TextRows, CharacterHeight);
        }
    }

    public int TextColumns { get; set; } = 80;
    public int TextRows { get; set; } = 25;

    /// <summary>
    /// Height of the character cell in scanlines. 16 is the VGA default; the
    /// 43- and 50-row text modes get there by switching to a 14- or 8-line
    /// font rather than by changing the raster, which stays at 400 lines.
    /// </summary>
    public int CharacterHeight { get; set; } = 16;

    /// <summary>
    /// A font the guest has loaded into the character generator, replacing
    /// the one in ROM, or null while the ROM font is in use.
    ///
    /// The VGA's character generator is RAM, not ROM: the BIOS copies one of
    /// its built-in fonts into it at mode-set time, and software is free to
    /// copy something else. DOS does exactly that for every code page other
    /// than the American one -- MODE CON CP PREPARE loads a font in which the
    /// upper half carries that country's accented letters -- so a machine
    /// that ignores the load draws those letters from whatever the ROM font
    /// happens to have there.
    ///
    /// Stored as 256 glyphs of <see cref="UserFontHeight"/> scanlines each.
    /// </summary>
    public byte[]? UserFont { get; private set; }

    /// <summary>Cell height of <see cref="UserFont"/>, in scanlines.</summary>
    public int UserFontHeight { get; private set; }

    /// <summary>
    /// Installs a font the guest has supplied. Only the characters it covers
    /// are replaced: software often loads just the upper half, leaving the
    /// ASCII range as the ROM drew it.
    /// </summary>
    public void LoadUserFont(int height, int firstCharacter, int count, ReadOnlySpan<byte> glyphs)
    {
        if (height is < 1 or > 32) return;

        // A different cell height means the previous font's glyphs no longer
        // describe anything, so start again rather than mixing two shapes.
        if (UserFont is null || UserFontHeight != height)
        {
            UserFont = BuildRomFontCopy(height);
            UserFontHeight = height;
        }

        for (int i = 0; i < count; i++)
        {
            int character = firstCharacter + i;
            if (character is < 0 or > 255) continue;

            int source = i * height;
            if (source + height > glyphs.Length) break;

            glyphs.Slice(source, height).CopyTo(UserFont.AsSpan(character * height, height));
        }

        ModeChanged?.Invoke();
    }

    /// <summary>
    /// Starts a user font off as a copy of the ROM one, so that characters
    /// the guest does not supply keep their usual shapes instead of turning
    /// blank.
    /// </summary>
    private static byte[] BuildRomFontCopy(int height)
    {
        var font = new byte[256 * height];

        for (int ch = 0; ch < 256; ch++)
        {
            ReadOnlySpan<byte> glyph = VgaFonts.GetGlyph((byte)ch, height);
            int rows = Math.Min(height, glyph.Length);
            glyph[..rows].CopyTo(font.AsSpan(ch * height, rows));
        }

        return font;
    }

    /// <summary>Goes back to the ROM font, as a mode set does.</summary>
    public void ClearUserFont()
    {
        UserFont = null;
        UserFontHeight = 0;
    }

    /// <summary>Raised on a mode change, so the renderer can resize and redraw everything.</summary>
    public event Action? ModeChanged;

    public VgaRegisters()
    {
        // Default palette: the 16 CGA colours in the first sixteen slots,
        // which is what text mode draws with.
        LoadDefaultPalette();

        // The full register set for 80x25 text, which is how a PC comes up.
        LoadStandardRegisters(0x03);
    }

    /// <summary>
    /// Loads every register a VGA ROM programs for a standard mode: what a
    /// program reading the adapter back through the ports expects to find
    /// after a mode set. The DAC and the cursor position are left alone; the
    /// BIOS handles those separately, as the real one does.
    /// </summary>
    public void LoadStandardRegisters(byte mode)
    {
        VgaRegisterTables.RegisterSet set = VgaRegisterTables.For(mode);
        _miscOutput = set.MiscOutput;
        Array.Clear(_sequencer);
        set.Sequencer.CopyTo(_sequencer, 0);
        Array.Clear(_crtc);
        set.Crtc.CopyTo(_crtc, 0);
        Array.Clear(_graphics);
        set.Graphics.CopyTo(_graphics, 0);
        Array.Clear(_attribute);
        set.Attribute.CopyTo(_attribute, 0);
        _attributeExpectingIndex = true;
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
        io.Register(0x3CA, this);       // feature control (read)
        io.Register(0x3C3, this);       // video subsystem enable

        // The monochrome addresses. A real adapter answers on one set or the
        // other according to bit 0 of the miscellaneous output register;
        // answering on both is a simplification that no program notices,
        // while a program in a monochrome mode that found nothing at 0x3B4
        // would conclude there is no CRTC at all.
        io.Register(0x3B4, 2, this);    // CRTC, monochrome
        io.Register(0x3BA, this);       // input status 1, monochrome
    }

    // --- register access for the memory path ----------------------------------
    // The planar read/write logic in VgaMemory needs these on every single byte
    // touched, so they are plain indexed reads rather than anything clever.

    public byte Sequencer(int index) => _sequencer[index & 7];
    public byte Graphics(int index) => _graphics[index & 0x0F];

    /// <summary>Directly sets a sequencer register; used by the BIOS mode setup and by tests.</summary>
    public void SetSequencer(int index, byte value) => _sequencer[index & 7] = value;

    /// <summary>Directly sets a graphics controller register.</summary>
    public void SetGraphics(int index, byte value) => _graphics[index & 0x0F] = value;

    /// <summary>Directly sets a CRTC register.</summary>
    public void SetCrtc(int index, byte value) => _crtc[index & 0x1F] = value;

    public byte Crtc(int index) => _crtc[index & 0x1F];
    public byte Attribute(int index) => _attribute[index & 0x1F];

    /// <summary>Map Mask: bit N enables CPU writes to plane N.</summary>
    public byte MapMask => (byte)(_sequencer[SeqMapMask] & 0x0F);

    /// <summary>Write mode 0-3, from the low two bits of the Graphics Mode register.</summary>
    public int WriteMode => _graphics[GcMode] & 0x03;

    /// <summary>Read mode: 0 returns one plane, 1 returns a colour-compare result.</summary>
    public int ReadMode => (_graphics[GcMode] >> 3) & 1;

    /// <summary>True when the sequencer has chained the four planes into one linear space (mode 13h).</summary>
    public bool Chain4 => (_sequencer[SeqMemoryMode] & 0x08) != 0;

    /// <summary>
    /// Offset in bytes between one displayed scan line and the next, in plane
    /// units. CRTC register 0x13 holds it in words, hence the doubling.
    /// </summary>
    /// <summary>
    /// CPU bytes from one scan line to the next. The offset register counts
    /// in words of display memory; with chain-4 on, each address holds four
    /// CPU bytes rather than one, which is how mode 13h's 0x28 comes to
    /// mean 320 bytes while the same value in a planar mode means 80.
    /// </summary>
    public int RowOffsetBytes => _crtc[0x13] * (Chain4 ? 8 : 2);

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
            case 0x3D4 or 0x3B4: return _crtcIndex;
            case 0x3D5 or 0x3B5: return _crtc[_crtcIndex & 0x1F];
            case 0x3CA: return 0x00;   // feature control: nothing set
            case 0x3C3: return 0x01;   // the video subsystem is enabled
            case 0x3C2:
            {
                // Input status 0. Bit 4 is the switch sense: the line the
                // BIOS -- and Windows' display device -- reads four times,
                // once for each clock-select setting in the miscellaneous
                // output register, to identify the monitor. The pattern that
                // comes back here is the one an analogue colour display
                // gives on a real adapter: the bit set for settings 0 and 3,
                // clear for 1 and 2. Bit 7 reports a vertical retrace
                // interrupt; it follows the retrace state.
                int clockSelect = (_miscOutput >> 2) & 3;
                byte status = 0x60;
                if (clockSelect is 0 or 3) status |= 0x10;
                if (_retraceToggle) status |= 0x80;
                return status;
            }
            case 0x3DA or 0x3BA:
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
            case 0x3D4 or 0x3B4: _crtcIndex = value; return;
            case 0x3D5 or 0x3B5: _crtc[_crtcIndex & 0x1F] = value; return;
        }
    }

    /// <summary>Writes one DAC entry directly, in 6-bit components.</summary>
    public void SetPaletteEntry(int index, byte red, byte green, byte blue)
    {
        int offset = (index & 0xFF) * 3;
        _palette[offset] = (byte)(red & 0x3F);
        _palette[offset + 1] = (byte)(green & 0x3F);
        _palette[offset + 2] = (byte)(blue & 0x3F);
    }

    /// <summary>The raw 6-bit components of a palette entry, as the DAC stores them.</summary>
    public (byte R, byte G, byte B) GetRawPaletteEntry(int index)
    {
        int offset = (index & 0xFF) * 3;
        return (_palette[offset], _palette[offset + 1], _palette[offset + 2]);
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

    /// <summary>
    /// Restores the 16 CGA colours plus the standard 256-colour ramp, which is
    /// what the BIOS loads on a mode set. Programs that never touch the DAC --
    /// most 16-colour ones -- rely entirely on this.
    /// </summary>
    public void ResetPalette(bool includeMode13Ramp)
    {
        Array.Clear(_palette);
        LoadDefaultPalette();
        if (includeMode13Ramp) LoadDefault256Palette();
    }

    private void LoadDefaultPalette()
    {
        // The 64 colours a VGA comes up with in its first 64 DAC entries: the
        // EGA palette, one bit per component at two-thirds intensity (bits 0,
        // 1, 2 for blue, green, red) and one at one-third (bits 3, 4, 5). The
        // attribute controller's standard tables point into this set -- the
        // sixteen text colours are entries 0-5, 14h, 7 and 38h-3Fh, which is
        // how colour 6 comes out brown (14h: red plus a third of green) and
        // not dark yellow, and how the bright colours get their grey tint.
        for (int i = 0; i < 64; i++)
        {
            byte r = (byte)(((i >> 2) & 1) * 0x2A + ((i >> 5) & 1) * 0x15);
            byte g = (byte)(((i >> 1) & 1) * 0x2A + ((i >> 4) & 1) * 0x15);
            byte b = (byte)((i & 1) * 0x2A + ((i >> 3) & 1) * 0x15);
            _palette[i * 3] = r;
            _palette[i * 3 + 1] = g;
            _palette[i * 3 + 2] = b;
        }
    }

    /// <summary>
    /// Where a four-bit pixel (or text attribute nibble) lands in the DAC:
    /// through the attribute controller's palette register for that value,
    /// with the colour select register supplying the top bits -- all four of
    /// them when the mode register's bit 7 asks for it, the top two always.
    /// Software that programs the sixteen palette registers rather than the
    /// DAC -- Windows' display driver, for one -- gets its colours only if
    /// this mapping is honoured.
    /// </summary>
    public int PaletteIndexFor(int pixel)
    {
        int index = _attribute[pixel & 0x0F] & 0x3F;
        byte mode = _attribute[0x10];
        byte select = _attribute[0x14];
        if ((mode & 0x80) != 0) index = (index & 0x0F) | ((select & 0x03) << 4);
        return index | ((select & 0x0C) << 4);
    }

    /// <summary>
    /// The standard mode 13h palette above the first sixteen entries: sixteen
    /// greys, then a 6x6x6-ish colour body arranged as hue rings. Programs
    /// that load their own palette overwrite it immediately, but the ones that
    /// do not -- and there are many -- expect this exact layout to be present.
    /// </summary>
    private void LoadDefault256Palette()
    {
        // 16-31: a grey ramp.
        for (int i = 0; i < 16; i++)
        {
            byte level = (byte)(i * 4 + i / 4);   // 0..63 in sixteen steps
            SetPaletteEntry(16 + i, level, level, level);
        }

        // 32-247: 9 hues x 6 value/saturation blocks x 4 shades. Reproducing
        // IBM's table exactly is not the point here; what matters is that the
        // upper entries are populated with a smoothly varying spread rather
        // than left black, so an image drawn with them is visible.
        int index = 32;
        for (int block = 0; block < 6 && index < 248; block++)
        {
            // Each block dims the previous one, mimicking the value ramp of
            // the real table.
            int scale = 63 - block * 10;
            for (int hue = 0; hue < 36 && index < 248; hue++)
            {
                (byte r, byte g, byte b) = HueToRgb(hue / 36.0, scale);
                SetPaletteEntry(index++, r, g, b);
            }
        }

        // 248-255 stay black, as they do on real hardware.

        static (byte, byte, byte) HueToRgb(double hue, int scale)
        {
            double sector = hue * 6.0;
            int slice = (int)sector % 6;
            double fraction = sector - Math.Floor(sector);
            byte high = (byte)Math.Clamp(scale, 0, 63);
            byte rising = (byte)Math.Clamp(scale * fraction, 0, 63);
            byte falling = (byte)Math.Clamp(scale * (1 - fraction), 0, 63);

            return slice switch
            {
                0 => (high, rising, (byte)0),
                1 => (falling, high, (byte)0),
                2 => ((byte)0, high, rising),
                3 => ((byte)0, falling, high),
                4 => (rising, (byte)0, high),
                _ => (high, (byte)0, falling),
            };
        }
    }
}
