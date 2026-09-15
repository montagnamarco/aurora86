namespace Aurora386.Core.Devices.Video;

/// <summary>
/// The extended VGA register file shared by the 1995 S3 Trio64V+ and 1996
/// ViRGE 86C325. Period display drivers probe CR2D-CR30, unlock CR38/CR39
/// and SR08, and program banking, scanout and the hardware cursor through
/// the standard VGA indexed ports. ViRGE adds its own aperture latches;
/// physical decoding remains the concrete card's responsibility.
/// </summary>
public sealed class S3VgaRegisters : VgaRegisters
{
    private readonly S3VideoIdentity _identity;

    /// <summary>Creates the register file for the established Trio64V+ card.</summary>
    public S3VgaRegisters()
        : this(S3VideoIdentity.Trio64VPlus)
    {
    }

    /// <summary>Creates one immutable S3 chip identity and its address latches.</summary>
    public S3VgaRegisters(S3VideoIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _identity = identity;
        if (identity == S3VideoIdentity.Virge325)
        {
            VirgeAperture = new S3VirgeApertureControls(CrtcRegisters);
        }
    }

    /// <summary>
    /// The offset selected for CPU accesses through the 64 KB VGA window.
    ///
    /// VBE keeps its bank outside the VGA register file, but an S3 driver
    /// programs the card's own CPU-base latch instead.  CR31 bit zero chooses
    /// the legacy latch. CR35 supplies its low four bits and CR51 supplies
    /// bits 5-4. The later CR6A register is independent: the Trio64V+ manual
    /// says that a non-zero CR6A value makes the old fields ignored. In
    /// chained mode one value names a 64 KB page; without chaining it names a
    /// 16 KB page before the VGA plane decode.
    /// </summary>
    public override uint CpuWindowOffset
    {
        get
        {
            if ((CrtcRegisters[0x31] & 0x01) == 0)
                return (uint)(VesaWindowBank * 0x10000);

            int alternateBank = CrtcRegisters[0x6A] & 0x3F;
            int legacyBank = (CrtcRegisters[0x35] & 0x0F) | ((CrtcRegisters[0x51] & 0x0C) << 2);
            int bank = alternateBank != 0 ? alternateBank : legacyBank;
            int pageSize = Chain4 ? 0x10000 : 0x4000;
            return (uint)(bank * pageSize);
        }
    }

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

    /// <summary>
    /// The chip identifier in register 0x30, which names the family: 0xE1 is
    /// the Trio series. Which member it is comes from 0x2D and 0x2E.
    /// </summary>
    public const byte S3DeviceId = 0xE1;

    /// <summary>ViRGE-specific address latches, absent on the fixed Trio path.</summary>
    public S3VirgeApertureControls? VirgeAperture { get; }

    /// <summary>
    /// The Trio's fixed host aperture, or the ViRGE's raw CR59/CR5A address.
    /// A ViRGE card owner must separately apply size alignment and decode gates
    /// before mapping memory; changing these latches alone does not map a device.
    /// </summary>
    public override uint ApertureBase
    {
        get => VirgeAperture?.RawLinearBase ?? base.ApertureBase;
        set
        {
            if (VirgeAperture is not null)
            {
                VirgeAperture.SetRawLinearBase(value);
                return;
            }

            base.ApertureBase = value;
        }
    }

    /// <summary>
    /// Bit 4 of CR53: the engine's registers appear in the window at
    /// A8000h-AFFFFh, and the 32 KB below become the image transfer area,
    /// in place of video memory. The S3 Windows driver switches this on
    /// around every drawing operation and off again for the processor's
    /// own writes to the screen.
    /// </summary>
    public override bool MemoryMappedEngine => (CrtcRegisters[0x53] & 0x10) != 0;

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

    /// <summary>The Hardware Graphics Cursor Mode register resets both colour stacks' shared pointer.</summary>
    private const byte CursorModeRegister = 0x45;

    /// <summary>The Hardware Graphics Cursor Foreground Stack register.</summary>
    private const byte CursorForegroundStackRegister = 0x4A;

    /// <summary>The Hardware Graphics Cursor Background Stack register.</summary>
    private const byte CursorBackgroundStackRegister = 0x4B;

    /// <summary>The marker after the old snapshot tail that identifies saved cursor colour stacks.</summary>
    private const byte CursorColourSnapshotMarker = 0xC3;

    /// <summary>
    /// The S3 cursor colour stacks contain three bytes, so the value is a
    /// 24-bit RGB colour with blue in the low byte. Reading CR45 resets the
    /// shared stack pointer before a driver programs either colour.
    /// </summary>
    private uint _cursorForegroundColour;
    private uint _cursorBackgroundColour;
    private int _cursorColourStackPointer;

    /// <summary>Whether the hardware pointer is being drawn (register 0x45 bit 0).</summary>
    public bool CursorEnabled => (CrtcRegisters[0x45] & 0x01) != 0;

    /// <summary>Where the pointer's top left corner is, in pixels.</summary>
    public (int X, int Y) CursorPosition =>
        ((CrtcRegisters[0x46] << 8) | CrtcRegisters[0x47], (CrtcRegisters[0x48] << 8) | CrtcRegisters[0x49]);

    /// <summary>
    /// How far into the 64x64 pattern the top left of the pointer starts.
    ///
    /// This is what lets a pointer walk off the top or left of the screen
    /// rather than stopping at the edge: the position stays at zero and the
    /// offset moves instead, so the visible part shrinks as it goes.
    /// </summary>
    public (int X, int Y) CursorOffset => (CrtcRegisters[0x4E] & 0x3F, CrtcRegisters[0x4F] & 0x3F);

    /// <summary>
    /// Where the cursor's pattern lives in video memory, as a multiple of
    /// 1024 bytes -- which is exactly what a 64x64 two-bit pattern occupies.
    /// </summary>
    public uint CursorPatternAddress => (uint)(((CrtcRegisters[0x4C] << 8) | CrtcRegisters[0x4D]) * 1024);

    /// <summary>The 24-bit colours the hardware pointer is drawn in.</summary>
    public (uint Foreground, uint Background) CursorColours =>
        (_cursorForegroundColour, _cursorBackgroundColour);

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
    protected override byte ReadGraphics(byte index)
    {
        byte at = (byte)(index & 0x0F);
        if (at != 0x0F) return GraphicsRegisters[at];

        // The top five bits report the card, not the last write: zero here
        // says a plain member of the family, which is what this emulator is
        // pretending to be. The low three are whatever software put there.
        return (byte)(GraphicsRegisters[0x0F] & 0x07);
    }

    // --- the S3 extended sequencer registers -----------------------------------
    //
    // A VGA's sequencer ends at register 0x04. Above 0x08 an S3 puts its own,
    // and the interesting ones are the clock synthesiser: 0x12 and 0x13 hold
    // the multiplier and divider that turn the card's reference crystal into
    // the pixel clock.
    //
    // That is how an SVGA reaches resolutions a VGA cannot. A VGA has two
    // crystals and therefore two pixel clocks; a card with a synthesiser can
    // produce any frequency, which is what 800x600 and 1024x768 need. There
    // is nothing to synthesise here -- the emulator draws a frame when it is
    // asked to, not when a clock says so -- but the registers have to hold
    // what is written, because a driver programs them and then reads them
    // back to check the card accepted the mode.

    /// <summary>
    /// Sequencer register 0x08 is the lock: 0x06 unlocks the extended range,
    /// anything else locks it. Same idea as the CRTC's 0x38, and for the same
    /// reason -- software written for a plain VGA must not stumble into the
    /// clock registers, because a wrong pixel clock takes the monitor out of
    /// range and shows nothing at all.
    /// </summary>
    private bool _sequencerUnlocked;

    protected override byte ReadSequencer(byte index)
    {
        byte at = (byte)(index & 0x1F);

        // Below 0x05 is the VGA's own, always readable.
        if (at < 0x05) return SequencerRegisters[at];

        // The lock register reads back what unlocks it, which is how a driver
        // checks whether it succeeded.
        if (at == 0x08) return _sequencerUnlocked ? (byte)0x06 : (byte)0x00;

        return SequencerRegisters[at];
    }

    protected override void WriteSequencer(byte index, byte value)
    {
        byte at = (byte)(index & 0x1F);

        RegistersChanged();
        if (at < 0x05) { SequencerRegisters[at] = value; return; }

        if (at == 0x08)
        {
            _sequencerUnlocked = value == 0x06;
            SequencerRegisters[at] = value;
            return;
        }

        // The extended registers take writes only while unlocked.
        if (!_sequencerUnlocked) return;
        SequencerRegisters[at] = value;
    }

    protected override byte ReadCrtc(byte index)
    {
        if (VirgeAperture is not null && VirgeAperture.TryReadCrtc(index, out byte value))
        {
            return value;
        }

        switch (index)
        {
            // The Trio64V+ manual states that a CR45 read resets the one
            // stack pointer shared by CR4A and CR4B. Windows 95 does exactly
            // that before each three-byte colour write.
            case CursorModeRegister:
                _cursorColourStackPointer = 0;
                return CrtcRegisters[index];

            // The identification registers. These come first because they
            // sit below 0x30, in the range a VGA also uses -- and an earlier
            // version of this method returned from the VGA range before
            // reaching them, so a driver asking what card this was got zero
            // and concluded there was no S3 here at all.

            // 0x2D and 0x2E are the device identifier, high byte then low.
            // Together they name the selected profile precisely: 0x88 0x11
            // is the Trio64V+ profile and 0x56 0x31 is the ViRGE 86C325
            // profile, while 0x30 names the shared S3 family.
            case 0x2D: return (byte)(_identity.DeviceId >> 8);
            case 0x2E: return (byte)_identity.DeviceId;

            // 0x2F is the profile-specific revision that drivers use for
            // card detection and stepping-specific workarounds.
            case 0x2F: return _identity.CrtcRevision;

            // 0x30 is the chip identifier, and it is readable whether or not
            // anything has been unlocked: a driver has to be able to ask what
            // card this is before it knows which unlock sequence to use.
            case 0x30: return _identity.ChipId;
        }

        // Below 0x30 the rest is the VGA's own, always readable.
        if (index < 0x30) return CrtcRegisters[index];

        switch (index)
        {
            // 0x36 reports how the card is configured. Bits 7-5 say how
            // much memory, in a three-bit code: 000 is 4 MB, 010 is 3, 100
            // is 2, 110 is 1, 111 is half a megabyte. This card has two.
            // It used to answer 010, read as two under a two-bit reading of
            // the field, and the S3 Windows driver, believing it had three
            // megabytes, kept its brushes and its cursor in the third:
            // every window background came out black.
            case 0x36: return (byte)(0x80 | (CrtcRegisters[0x36] & 0x1F));

            // --- the linear aperture -------------------------------------
            //
            // Where the whole of video memory appears in the physical address
            // space, as one flat array. A driver in protected mode writes
            // there and never touches the 64 KB window or the bank register,
            // which is what makes an SVGA resolution usable at all: moving a
            // window every 64 KB while painting a 1024x768 screen is most of
            // the work.
            //
            // The address is not something the driver chooses. It reads it
            // from these registers, because on a real card the BIOS decided
            // it at power-on from what else was in the address space. That is
            // why the driver's own configuration leaves "aperture-base"
            // empty: it is a question for the hardware, not a setting.

            // 0x58: aperture size in bits 0-1 -- 0 is 64 KB, 1 is 1 MB, 2 is
            // 2 MB, 3 is 4 MB -- and bit 4 enables it. Two megabytes, on.
            case 0x58: return 0x12;

            // 0x59 and 0x5A are the top two bytes of the address, which is
            // therefore aligned to a megabyte. The emulator maps the frame
            // buffer directly above the installed RAM rather than at a fixed
            // address a real card would use: a card in a slot cannot know how
            // much memory the machine has, and this one can.
            case 0x59: return (byte)(ApertureBase >> 24);
            case 0x5A: return (byte)(ApertureBase >> 16);
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
        return CrtcRegisters[index];
    }

    protected override void WriteCrtc(byte index, byte value)
    {
        RegistersChanged();
        if (index < 0x30) { CrtcRegisters[index] = value; return; }

        // The two lock registers themselves are always writable, or there
        // would be no way in.
        if (index == 0x38)
        {
            _systemUnlocked = value == 0x48;
            CrtcRegisters[index] = value;
            return;
        }

        if (index == 0x39)
        {
            _extendedUnlocked = value >= 0xA0;
            CrtcRegisters[index] = value;
            return;
        }

        if (index < 0x40 && !_systemUnlocked) return;
        if (index >= 0x40 && !_extendedUnlocked) return;

        if (VirgeAperture is not null && VirgeAperture.TryWriteCrtc(index, value))
        {
            return;
        }

        if (index is CursorForegroundStackRegister or CursorBackgroundStackRegister)
        {
            WriteCursorColour(index, value);
            CrtcRegisters[index] = value;
            return;
        }

        CrtcRegisters[index] = value;
    }

    /// <summary>
    /// Writes one byte through CR4A or CR4B's three-byte colour stack.
    ///
    /// The documented first three writes select the low, middle, and high
    /// bytes. 86Box models the physical two-bit pointer too: its fourth
    /// position ignores the byte and its fifth returns to the low byte.
    /// Keeping that behaviour makes an overlong driver sequence deterministic
    /// without inventing a fourth colour component.
    /// </summary>
    private void WriteCursorColour(byte register, byte value)
    {
        if (_cursorColourStackPointer < 3)
        {
            int shift = _cursorColourStackPointer * 8;
            uint componentMask = 0xFFu << shift;
            uint component = (uint)value << shift;

            if (register == CursorForegroundStackRegister)
            {
                _cursorForegroundColour = (_cursorForegroundColour & ~componentMask) | component;
            }
            else
            {
                _cursorBackgroundColour = (_cursorBackgroundColour & ~componentMask) | component;
            }
        }

        _cursorColourStackPointer = (_cursorColourStackPointer + 1) & 0x03;
    }

    /// <summary>Start address of the visible region; changing it scrolls in hardware.</summary>
    /// <summary>
    /// Where the display starts, with the extended bits an S3 adds.
    ///
    /// The VGA's own start address is sixteen bits, which reaches 64 KB --
    /// enough when a screen was 64 KB. At 800x600 in 256 colours a screen is
    /// 480000 bytes, so the address needs more bits, and the S3 put them in
    /// two extended registers: two in 0x31 and two more in 0x51.
    ///
    /// Without them a driver can only ever show the first 64 KB of video
    /// memory, which at 800x600 is the top 81 rows and nothing else.
    /// </summary>
    public override uint ExtendedStartAddress =>
        (uint)((CrtcRegisters[0x0C] << 8) | CrtcRegisters[0x0D])
        | (uint)((CrtcRegisters[0x31] & 0x30) << 12)
        | (uint)((CrtcRegisters[0x51] & 0x03) << 18);

    /// <summary>
    /// Bytes between one scan line and the next, with the ninth and tenth
    /// bits an S3 adds -- 0x43 bit 2 and 0x51 bits 4-5. The VGA's own field
    /// is eight bits, which stops at a logical width of 2040 bytes.
    /// </summary>
    /// <summary>
    /// Whether a driver has programmed the extended bits of the row offset.
    /// The plain register always holds something -- whatever the last mode
    /// set left -- so its value alone says nothing about whether the card's
    /// own geometry is in force.
    /// </summary>
    public override bool HasExtendedRowOffset => (CrtcRegisters[0x43] & 0x04) != 0
        || (CrtcRegisters[0x51] & 0x30) != 0
        || (VirgeAperture is not null && (CrtcRegisters[0x31] & 8) != 0);

    public override int ExtendedRowOffset
    {
        get
        {
            if (VirgeAperture is not null && (CrtcRegisters[0x31] & 8) != 0)
            {
                // ViRGE CR13 (16-20): CR31.3 forces doubleword addressing,
                // hence four bytes per offset unit, independently of Chain-4.
                // CR51[5:4] overrides CR43.2 when nonzero (18-16).
                int extension = (CrtcRegisters[0x51] & 0x30) << 4;
                if (extension == 0)
                {
                    extension = (CrtcRegisters[0x43] & 4) << 6;
                }

                return (CrtcRegisters[0x13] | extension) * 4;
            }

            return (CrtcRegisters[0x13]
                | ((CrtcRegisters[0x43] & 0x04) << 6)
                | ((CrtcRegisters[0x51] & 0x30) << 4)) * (Chain4 ? 8 : 2);
        }
    }

    /// <summary>Preserves the established Paradise-compatible GC0F probe on S3 cards.</summary>
    protected override void WriteGraphics(byte index, byte value)
    {
        RegistersChanged();
        byte at = (byte)(index & 0x0F);
        GraphicsRegisters[at] = at == 0x0F ? (byte)(value & 0x07) : value;
    }

    // These positions are part of the existing 1136-byte VGA register record.
    // Keep the vendor fields in its reserved tail so old snapshots remain readable.
    private const int SystemLockSnapshotOffset = 3;
    private const int ExtendedLockSnapshotOffset = 4;
    private const int SequencerLockSnapshotOffset = 5;
    private const int CursorSnapshotOffset = 17;

    protected override void CaptureRegisterExtensions(Span<byte> tail)
    {
        tail[SystemLockSnapshotOffset] = (byte)(_systemUnlocked ? 1 : 0);
        tail[ExtendedLockSnapshotOffset] = (byte)(_extendedUnlocked ? 1 : 0);
        tail[SequencerLockSnapshotOffset] = (byte)(_sequencerUnlocked ? 1 : 0);
        int at = CursorSnapshotOffset;
        tail[at++] = CursorColourSnapshotMarker;
        WriteCursorColourToSnapshot(tail, ref at, _cursorForegroundColour);
        WriteCursorColourToSnapshot(tail, ref at, _cursorBackgroundColour);
        tail[at] = (byte)_cursorColourStackPointer;
    }

    protected override void RestoreRegisterExtensions(ReadOnlySpan<byte> tail)
    {
        _systemUnlocked = tail[SystemLockSnapshotOffset] != 0;
        _extendedUnlocked = tail[ExtendedLockSnapshotOffset] != 0;
        _sequencerUnlocked = tail[SequencerLockSnapshotOffset] != 0;
        int at = CursorSnapshotOffset;
        if (tail[at++] == CursorColourSnapshotMarker)
        {
            _cursorForegroundColour = ReadCursorColourFromSnapshot(tail, ref at);
            _cursorBackgroundColour = ReadCursorColourFromSnapshot(tail, ref at);
            _cursorColourStackPointer = tail[at] & 0x03;
        }
        else
        {
            // The old snapshot tail had no colour stacks. CR4A and CR4B
            // supply the legacy low bytes, matching the historical restore.
            _cursorForegroundColour = CrtcRegisters[CursorForegroundStackRegister];
            _cursorBackgroundColour = CrtcRegisters[CursorBackgroundStackRegister];
            _cursorColourStackPointer = 0;
        }

        VirgeAperture?.NotifyRestored();
    }

    /// <summary>Writes the three stored bytes of one cursor colour into a register snapshot.</summary>
    private static void WriteCursorColourToSnapshot(Span<byte> state, ref int at, uint colour)
    {
        state[at++] = (byte)colour;
        state[at++] = (byte)(colour >> 8);
        state[at++] = (byte)(colour >> 16);
    }

    /// <summary>Reads the three stored bytes of one cursor colour from a register snapshot.</summary>
    private static uint ReadCursorColourFromSnapshot(ReadOnlySpan<byte> state, ref int at)
    {
        uint lowByte = state[at++];
        uint middleByte = (uint)state[at++] << 8;
        uint highByte = (uint)state[at++] << 16;

        return lowByte | middleByte | highByte;
    }

}
