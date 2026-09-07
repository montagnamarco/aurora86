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
    /// <summary>
    /// The CRTC registers. A VGA has 25 of them, ending at 0x18; the array is
    /// 256 because an S3 puts its own extensions above 0x30, and those are
    /// how a driver finds the card at all.
    ///
    /// The extended registers are locked at power-on and stay locked until
    /// software writes the right values to 0x38 and 0x39. That is not
    /// security -- the values are published -- but a guard against software
    /// written for a plain VGA stumbling into registers it has no idea
    /// exist, which on a real card could reprogram the pixel clock and take
    /// the monitor out of range.
    /// </summary>
    private readonly byte[] _crtc = new byte[256];

    /// <summary>
    /// The PEL mask (port 0x3C6), ANDed with every pixel value before it
    /// reaches the palette. 0xFF -- everything through -- is the state after
    /// any mode set, and the state it must power on in: a zero here would
    /// leave every pixel reading colour 0 and the screen uniformly black,
    /// which is a memorable way to appear broken.
    /// </summary>
    private byte _pelMask = 0xFF;

    /// <summary>Whether the last DAC index written was for writing rather than reading.</summary>
    private bool _dacWritePending;
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

    /// <summary>
    /// Where in the first megabyte the adapter answers, from bits 2-3 of the
    /// Miscellaneous register.
    ///
    /// The four choices are not arbitrary: they are the addresses the earlier
    /// adapters used, and a VGA claims whichever one the software it is
    /// pretending to be expects. A graphics program wants the full 128 KB at
    /// 0xA0000; a program written for a monochrome adapter looks at 0xB0000;
    /// a CGA program looks at 0xB8000. Getting this wrong does not corrupt
    /// anything -- the writes simply land somewhere the adapter is not
    /// listening, and the screen stays blank while the program runs happily
    /// on.
    /// </summary>
    public (uint Base, uint Size) MemoryWindow => ((_graphics[GcMiscellaneous] >> 2) & 3) switch
    {
        0 => (0xA0000u, 0x20000u),   // 128 KB: the whole aperture
        1 => (0xA0000u, 0x10000u),   // 64 KB: what EGA and VGA graphics use
        2 => (0xB0000u, 0x08000u),   // where a monochrome adapter lived
        _ => (0xB8000u, 0x08000u),   // and where CGA lived
    };

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
            // A BIOS mode set programs the whole controller, so the registers
            // are made to agree with the table here as well: memory decoding
            // and rendering both read the registers, not the mode number.
            LoadStandardRegisters(value);
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
            // A VBE mode overrides everything the VGA registers say: the
            // planar hardware is not in the path at all, and the CRTC values
            // an SVGA card would hold for these resolutions are extensions
            // this emulator does not pretend to have.
            if (VesaMode is { } vesa)
                return new VgaModeInfo(0xFF,
                                       // A sixteen-colour VBE mode is the
                                       // VGA's own planar arrangement at a
                                       // larger size, so it goes down the
                                       // planar path rather than needing one
                                       // of its own.
                                       vesa.IsPlanar ? VgaMemoryLayout.Planar4
                                                     : VgaMemoryLayout.HighColour,
                                       vesa.Width, vesa.Height,
                                       vesa.Width / 8, vesa.Height / 16, 16,
                                       vesa.BitsPerPixel);

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

    /// <summary>
    /// How many pixels wide a character cell is, from bit 0 of the
    /// sequencer's clocking mode register: nine, or eight when the bit is
    /// set.
    ///
    /// Nine is the odd-looking number and it is the one the text modes use.
    /// The VGA's text modes are 720 pixels across, not 640, precisely so that
    /// eighty characters can have nine columns each: eight for the glyph and
    /// a ninth of space between it and the next, which is what makes text on
    /// a VGA look less cramped than text on an EGA.
    ///
    /// The ninth column is normally blank, with one exception that matters
    /// enough to be built into the hardware -- see
    /// <see cref="NinthColumnRepeats"/>.
    ///
    /// Graphics modes set the bit and get eight, because there a "character"
    /// is just eight pixels of a byte and a ninth would have nothing in it.
    /// </summary>
    public int CharacterWidth => (_sequencer[0x01] & 0x01) != 0 ? 8 : 9;

    /// <summary>
    /// Whether the ninth column repeats the eighth instead of being blank,
    /// from bit 2 of the attribute controller's mode register.
    ///
    /// It applies only to character codes 0xC0 to 0xDF, and that range is not
    /// arbitrary: it is where the line-drawing characters live. A horizontal
    /// rule drawn out of them would otherwise come apart into dashes, one
    /// blank column per cell, because the line reaches the right edge of the
    /// glyph and then stops. Repeating the last column carries it across the
    /// gap, and a box drawn in text mode has continuous edges.
    ///
    /// It is switched on for every standard text mode, and the reason the
    /// range is fixed in hardware rather than configurable is that it only
    /// ever needed to serve the one font.
    /// </summary>
    public bool NinthColumnRepeats => (_attribute[0x10] & 0x04) != 0;

    // --- VESA BIOS Extension state --------------------------------------------
    //
    // The VBE modes are not VGA modes with bigger numbers: they bypass the
    // planar hardware entirely and treat video memory as one flat array. So
    // the geometry is held here rather than derived from the CRTC, which on a
    // real SVGA card is programmed through vendor-specific extended registers
    // that no two makers agreed on -- and agreeing on that is precisely what
    // VBE was invented to avoid having to do.

    /// <summary>The VBE mode in force, or null when the card is in a VGA mode.</summary>
    public VesaModes.VesaModeInfo? VesaMode { get; private set; }

    /// <summary>
    /// Bytes between one scan line and the next, which need not be the width
    /// of the picture.
    ///
    /// A program that makes this longer gets a picture wider than the screen
    /// and scrolls a window across it, which is how side-scrolling works at
    /// resolutions where copying the screen every frame is out of the
    /// question. Zero means "the same as the visible width", which is what a
    /// mode set leaves.
    /// </summary>
    public int VesaScanLineBytes { get; set; }

    /// <summary>
    /// The byte within video memory that appears at the top left of the
    /// screen: the SVGA equivalent of the VGA's start address, and the
    /// register that does the scrolling.
    /// </summary>
    public int VesaDisplayStart { get; set; }

    /// <summary>
    /// How many bits of each colour the palette holds.
    ///
    /// A VGA's DAC keeps six, which gives 262144 colours and visible banding
    /// in a gradient. VBE 2.0 cards could switch to eight, and a program that
    /// wants smooth gradients asks for it -- because on a card that cannot,
    /// the same values come out four times too bright.
    /// </summary>
    public int DacBitsPerColour { get; set; } = 6;

    /// <summary>
    /// Which 64 KB slice of video memory the window at 0xA0000 shows.
    ///
    /// This is how a real-mode program reaches more than 64 KB of pixels: the
    /// address space has no room for the rest, so the card shows one slice at
    /// a time and the program moves the window. Drawing anything that crosses
    /// a boundary means splitting the write in two, which is the tedium the
    /// linear frame buffer exists to end.
    /// </summary>
    public int VesaWindowBank { get; set; }

    /// <summary>
    /// Whether the mode was set with the linear frame buffer requested, in
    /// which case the window at 0xA0000 is not used at all and the whole of
    /// video memory is reached at one physical address.
    /// </summary>
    public bool VesaLinearFrameBuffer { get; private set; }

    /// <summary>
    /// Puts the card into a VBE mode, or back into VGA modes when given null.
    /// </summary>
    public void SetVesaMode(VesaModes.VesaModeInfo? mode, bool linearFrameBuffer)
    {
        VesaMode = mode;
        VesaLinearFrameBuffer = linearFrameBuffer;
        VesaWindowBank = 0;

        // A mode set puts the scan line back to the visible width and the
        // display start back to the beginning. A program that had set up a
        // wider virtual screen has just asked for a different one.
        VesaScanLineBytes = 0;
        VesaDisplayStart = 0;
        ModeChanged?.Invoke();
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
        // Only the VGA's own registers are reset. A BIOS mode set programs
        // the standard controller and leaves the card's extensions alone:
        // clearing them would relock the extended registers and lose the
        // configuration a driver had set up, which on real hardware a mode
        // set most certainly does not do.
        Array.Clear(_crtc, 0, 0x30);
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
    public void SetCrtc(int index, byte value) => WriteCrtc((byte)index, value);

    public byte Crtc(int index) => ReadCrtc((byte)index);

    // --- the S3 extended registers --------------------------------------------
    //
    // Everything above 0x30 belongs to the card rather than to the VGA
    // standard, and reaching it takes two specific writes first. The values
    // are not secret -- every S3 driver has them -- but the lock stops
    // software written for a plain VGA from stumbling into registers it does
    // not know exist. On a real card that could reprogram the pixel clock and
    // leave the monitor showing nothing.

    /// <summary>0x4838 unlocks registers 0x30-0x3F; anything else locks them.</summary>
    private bool _systemUnlocked;

    /// <summary>0xA0 or higher in 0x39 unlocks 0x40 and above.</summary>
    private bool _extendedUnlocked;

    /// <summary>The card's identifier, which is how a driver knows what it has.</summary>
    public const byte S3DeviceId = 0xE1;       // Trio64

    // --- the hardware cursor --------------------------------------------------
    //
    // Registers 0x45 to 0x4E. The card draws a 64x64 pointer over the picture
    // as the beam passes, which means the pointer costs nothing at all to
    // move: writing two registers is the whole operation.
    //
    // Doing it in software means saving what is under the pointer, drawing
    // it, and putting the background back before the next move -- three
    // times the work, and it has to happen between frames or the pointer
    // flickers. At 1024x768 that was the difference between a pointer that
    // kept up with the hand and one that lagged behind it.

    /// <summary>Whether the hardware pointer is being drawn (register 0x45 bit 0).</summary>
    public bool CursorEnabled => (_crtc[0x45] & 0x01) != 0;

    /// <summary>Where the pointer's top left corner is, in pixels.</summary>
    public (int X, int Y) CursorPosition =>
        ((_crtc[0x46] << 8) | _crtc[0x47], (_crtc[0x48] << 8) | _crtc[0x49]);

    /// <summary>
    /// How far into the 64x64 pattern the top left of the pointer starts.
    ///
    /// This is what lets a pointer walk off the top or left of the screen
    /// rather than stopping at the edge: the position stays at zero and the
    /// offset moves instead, so the visible part shrinks as it goes.
    /// </summary>
    public (int X, int Y) CursorOffset => (_crtc[0x4E] & 0x3F, _crtc[0x4F] & 0x3F);

    /// <summary>
    /// Where the cursor's pattern lives in video memory, as a multiple of
    /// 1024 bytes -- which is exactly what a 64x64 two-bit pattern occupies.
    /// </summary>
    public uint CursorPatternAddress => (uint)(((_crtc[0x4C] << 8) | _crtc[0x4D]) * 1024);

    /// <summary>The colours the pointer is drawn in, from registers 0x4A and 0x4B.</summary>
    public (byte Foreground, byte Background) CursorColours => (_crtc[0x4A], _crtc[0x4B]);

    // --- Paradise/Western Digital compatibility -------------------------------
    //
    // A VGA's graphics controller ends at register 0x08. Paradise put its own
    // register at 0x0F, and Windows 3.1's Super VGA driver identifies the
    // card by testing it -- it makes no VESA call at all, so a card that does
    // not answer here is one the driver refuses outright.
    //
    // The test, disassembled from the driver itself, is cleverer than looking
    // for a signature. It reads the register, saves the top five bits
    // unchanged, then writes back those five bits *inverted* with 101 in the
    // low three. It reads again and cancels out the five it saved. What
    // should be left is exactly the 5 it wrote.
    //
    // Work through what that demands. For the top five to cancel, they must
    // come back as they were -- so the write to them has to be *ignored*. For
    // the low three to survive, they must be real storage. The driver is not
    // asking "are you a Paradise" but "does register 0x0F have three writable
    // bits under five read-only ones", which is a thing no plain VGA does and
    // no other card of the period did by accident.
    //
    // It is a good test, and it is why a register that simply kept everything
    // written to it failed: the top five bits came back inverted and the
    // cancellation left rubbish.

    /// <summary>
    /// Reads a graphics controller register. Everything below 0x09 is the
    /// VGA's own; 0x0F is Paradise's, where the top five bits read back as
    /// the card's configuration rather than as what was written.
    /// </summary>
    private byte ReadGraphics(byte index)
    {
        byte at = (byte)(index & 0x0F);
        if (at != 0x0F) return _graphics[at];

        // The top five bits report the card, not the last write: zero here
        // says a plain member of the family, which is what this emulator is
        // pretending to be. The low three are whatever software put there.
        return (byte)(_graphics[0x0F] & 0x07);
    }

    private byte ReadCrtc(byte index)
    {
        // Below 0x30 is the VGA, always readable.
        if (index < 0x30) return _crtc[index];

        switch (index)
        {
            // 0x30 is the chip identifier, and it is readable whether or not
            // anything has been unlocked: a driver has to be able to ask what
            // card this is before it knows which unlock sequence to use.
            case 0x30: return S3DeviceId;

            // 0x36 reports how the card is configured. Bits 5-6 say how much
            // memory: 0 is 4 MB, 1 is 3, 2 is 2, 3 is 1. This card has two.
            case 0x36: return (byte)(0x40 | (_crtc[0x36] & 0x1F));

            // 0x2E and 0x2F together are the secondary identifier some
            // drivers check, and 0x2D is the high byte.
            case 0x2D: return 0x88;
            case 0x2E: return S3DeviceId;
        }

        // A locked register still answers. The lock protects writes, not
        // reads: on the silicon the register is there and readable, and what
        // the lock stops is software written for a plain VGA from changing
        // it by accident.
        //
        // The difference matters more than it sounds. 0xFF is what the bus
        // puts on the wire when nothing responds at all -- it is what a
        // driver reads on a machine without the card -- so returning it from
        // a locked register tells every driver that the card is absent. That
        // was a wrong choice here, and the Super VGA driver reading 0xFF from
        // the whole extended range is exactly what it looks like.
        return _crtc[index];
    }

    private void WriteCrtc(byte index, byte value)
    {
        if (index < 0x30) { _crtc[index] = value; return; }

        // The two lock registers themselves are always writable, or there
        // would be no way in.
        if (index == 0x38)
        {
            _systemUnlocked = value == 0x48;
            _crtc[index] = value;
            return;
        }

        if (index == 0x39)
        {
            _extendedUnlocked = value >= 0xA0;
            _crtc[index] = value;
            return;
        }

        if (index < 0x40 && !_systemUnlocked) return;
        if (index >= 0x40 && !_extendedUnlocked) return;

        _crtc[index] = value;
    }
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

    /// <summary>
    /// The scan line at which the display restarts from address zero,
    /// regardless of where the start address put it: the split screen.
    ///
    /// This is how a game keeps a status panel still while the world scrolls
    /// behind it. The top of the screen is drawn from wherever the start
    /// address points -- and moving that pointer scrolls the whole picture
    /// with no copying at all -- while everything below the split comes from
    /// address zero and does not move. The panel is drawn once, at the
    /// beginning, and then simply never scrolls.
    ///
    /// The field is ten bits scattered across three registers, which is the
    /// usual VGA story: the 286-era design ran out of room and later bits
    /// were put wherever there was space.
    /// </summary>
    public int LineCompare =>
        _crtc[0x18]
        | ((_crtc[0x07] & 0x10) << 4)
        | ((_crtc[0x09] & 0x40) << 3);

    /// <summary>
    /// Horizontal panning, from the attribute controller's register 0x13:
    /// how many pixels the picture is shifted left.
    ///
    /// The start address moves the picture in whole characters -- eight
    /// pixels at a time in most modes -- which makes a scroll visibly jerky.
    /// This register covers the eight positions in between, and a game that
    /// combines the two scrolls one pixel at a time without moving a single
    /// byte of memory.
    /// </summary>
    public int HorizontalPan => _attribute[0x13] & 0x0F;

    /// <summary>True when the cursor is visible; bit 5 of CRTC register 0x0A hides it.</summary>
    public bool CursorVisible => (_crtc[0x0A] & 0x20) == 0;

    /// <summary>
    /// The first and last scan lines of the cell that the cursor covers, from
    /// CRTC registers 0x0A and 0x0B.
    ///
    /// This is the whole of the cursor's shape, and programs really do change
    /// it: an underline for insert mode and a full block for overwrite is the
    /// convention every DOS editor follows, and it is done by writing two
    /// registers rather than by drawing anything.
    ///
    /// A start below the end is not a mistake to be corrected. The hardware
    /// turns the cursor on at the start line and off after the end line, and
    /// with the two reversed it never turns off within the cell: the result
    /// is a cursor split into a bar at the top and a bar at the bottom, which
    /// some programs used deliberately.
    /// </summary>
    public (int Start, int End) CursorShape => (_crtc[0x0A] & 0x1F, _crtc[0x0B] & 0x1F);

    public byte ReadByte(ushort port)
    {
        switch (port)
        {
            case 0x3C0: return _attributeIndex;
            case 0x3C1: return _attribute[_attributeIndex & 0x1F];
            case 0x3C4: return _sequencerIndex;
            case 0x3C5: return _sequencer[_sequencerIndex & 7];
            // The DAC's state: 0 means the last thing written was a read
            // index, 3 that it was a write index. Software that finds the DAC
            // mid-triple -- a handler that interrupted a palette update --
            // reads this to know whether it may safely take over.
            case 0x3C7: return _dacWritePending ? (byte)3 : (byte)0;

            case 0x3C6: return _pelMask;
            case 0x3C8: return _dacWriteIndex;
            case 0x3C9:
            {
                byte value = _palette[_dacReadIndex * 3 + _dacComponent];
                if (++_dacComponent == 3) { _dacComponent = 0; _dacReadIndex++; }
                return value;
            }
            case 0x3CC: return _miscOutput;
            case 0x3CE: return _graphicsIndex;
            case 0x3CF: return ReadGraphics(_graphicsIndex);
            case 0x3D4 or 0x3B4: return _crtcIndex;
            case 0x3D5 or 0x3B5: return ReadCrtc(_crtcIndex);
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
                if ((RetraceStatus & 0x08) != 0) status |= 0x80;
                return status;
            }
            case 0x3DA or 0x3BA:
            {
                // Input status register 1. Bits 0 and 3 report horizontal and
                // vertical retrace: software polls them to find the moment
                // when the beam is off-screen and video memory can be written
                // without tearing.
                //
                // Reading here also resets the attribute controller's
                // index/data flip-flop, which is why every program that
                // touches the attribute controller reads this port first.
                _attributeExpectingIndex = true;
                return RetraceStatus;
            }
            default: return 0xFF;
        }
    }

    /// <summary>
    /// Where the beam is, as a fraction of one frame, advanced by the machine
    /// as emulated time passes.
    /// </summary>
    private double _framePhase;

    /// <summary>
    /// Advances the beam by a number of microseconds of emulated time.
    ///
    /// A VGA frame takes about 1/70th of a second in the 400-line text modes
    /// and 1/60th at 480 lines. The value that matters is not the exact
    /// figure but that it is a duration at all: a program measuring how long
    /// vertical retrace lasts, or counting frames to time an animation, gets
    /// a plausible answer instead of whatever the polling loop's own speed
    /// happens to produce.
    /// </summary>
    public void AdvanceBeam(double microseconds)
    {
        // 70 Hz is the text and 200-line rate, which is what almost
        // everything of the period runs at; 480-line modes drop to 60.
        double frameMicroseconds = (_crtc[0x12] > 0xE0 || (_crtc[0x07] & 0x02) != 0)
            ? 1_000_000.0 / 60.0
            : 1_000_000.0 / 70.0;

        _framePhase += microseconds / frameMicroseconds;
        if (_framePhase >= 1.0) _framePhase -= Math.Floor(_framePhase);
    }

    /// <summary>
    /// The retrace bits of input status register 1, from where the beam is.
    ///
    /// Vertical retrace is the interval between the last visible line and the
    /// first line of the next frame -- about a twentieth of the frame on real
    /// hardware. Horizontal retrace happens once per scan line and is far
    /// shorter; a program polling it is waiting for a gap of a few
    /// microseconds, and what it needs is that the bit spends most of its
    /// time clear and briefly goes set.
    /// </summary>
    private byte RetraceStatus
    {
        get
        {
            // The last 5% of the frame is the vertical blanking interval,
            // which is the window a program writes its palette in: changing
            // the DAC while the beam is drawing produces visible snow, and
            // this is the only moment it is safe.
            bool vertical = _framePhase >= 0.95;

            // Within a frame there are a few hundred scan lines, each ending
            // in a short retrace. Deriving it from the same phase keeps the
            // two consistent -- horizontal retrace really does happen during
            // vertical retrace as well, since the beam keeps sweeping.
            double linePhase = _framePhase * 449.0;
            bool horizontal = linePhase - Math.Floor(linePhase) >= 0.8;

            return (byte)((vertical ? 0x08 : 0x00) | (horizontal ? 0x01 : 0x00));
        }
    }

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

            // Writing 0xFF to the PEL mask is what every mode set does, and
            // it is also how a program that has been masking colours puts
            // things back. Anything else narrows which palette entries the
            // pixel values can reach.
            case 0x3C6: _pelMask = value; return;

            case 0x3C7: _dacReadIndex = value; _dacComponent = 0; _dacWritePending = false; return;
            case 0x3C8: _dacWriteIndex = value; _dacComponent = 0; _dacWritePending = true; return;
            case 0x3C9:
            {
                // Components arrive one at a time, red then green then blue,
                // with the index auto-incrementing after the third.
                // A VGA's DAC keeps six bits and discards the top two, which
                // is not rounding but the width of the hardware. With an
                // eight-bit DAC there is nothing to discard -- and truncating
                // anyway would make the whole point of asking for it
                // disappear, since a program that wants smooth gradients is
                // writing precisely the values that would be lost.
                _palette[_dacWriteIndex * 3 + _dacComponent] =
                    DacBitsPerColour >= 8 ? value : (byte)(value & 0x3F);
                if (++_dacComponent == 3) { _dacComponent = 0; _dacWriteIndex++; }
                return;
            }

            case 0x3CE: _graphicsIndex = value; return;
            case 0x3CF:
                // Register 0x0F takes only its low three bits. The top five
                // are the card's own configuration and a write to them goes
                // nowhere -- which is precisely what the Super VGA driver
                // tests for.
                if ((_graphicsIndex & 0x0F) == 0x0F)
                {
                    _graphics[0x0F] = (byte)(value & 0x07);
                    return;
                }
                _graphics[_graphicsIndex & 0x0F] = value;
                return;
            case 0x3D4 or 0x3B4: _crtcIndex = value; return;
            case 0x3D5 or 0x3B5: WriteCrtc(_crtcIndex, value); return;
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
        // The PEL mask sits between the pixel value and the palette lookup:
        // whatever the frame buffer says, only the bits this register lets
        // through select a colour. It reads 0xFF after any normal mode set, so
        // it is invisible until something deliberately narrows it.
        //
        // What it is for: a program can hold two or four complete palettes in
        // the 256 entries and switch between them in one write, with no
        // reprogramming and no tearing. Fade to black, day and night versions
        // of the same picture, flashing a selection -- all done by masking off
        // the top bits of the index rather than rewriting 768 bytes of DAC.
        int offset = ((index & _pelMask) & 0xFF) * 3;

        // With an eight-bit DAC the stored value is already the full range
        // and nothing is expanded. With six -- what a VGA has -- the top two
        // bits are repeated at the bottom, so that full scale 0x3F maps to
        // 0xFF rather than 0xFC and white really is white.
        if (DacBitsPerColour >= 8)
            return (_palette[offset], _palette[offset + 1], _palette[offset + 2]);

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
    /// <summary>
    /// Whether bit 7 of a text attribute means "blink" or "bright background",
    /// from bit 3 of the attribute controller's mode register.
    ///
    /// The same bit has meant two things since the CGA, and which one it means
    /// is a setting rather than a convention. With blinking enabled -- the
    /// state a mode set leaves -- there are eight background colours and the
    /// text flashes; with it disabled there are sixteen and nothing flashes.
    /// A program that wants a bright background writes this register first,
    /// and one that wants attention writes the attribute and expects a blink.
    /// </summary>
    public bool BlinkEnabled => (_attribute[0x10] & 0x08) != 0;

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
