using Aurora386.Core;
using Xunit;

namespace Aurora386.Tests;

/// <summary>
/// After a mode set the adapter's registers, read back through the ports,
/// must describe the mode the way a VGA ROM leaves them. Windows' virtual
/// display device reads the whole set before it starts and refuses to go on
/// when what it finds does not look like a working adapter.
/// </summary>
public class VgaRegisterTableTests
{
    private static byte Indexed(Machine machine, ushort indexPort, byte index)
    {
        machine.Io.WriteByte(indexPort, index);
        return machine.Io.ReadByte((ushort)(indexPort + 1));
    }

    private static byte Attribute(Machine machine, byte index)
    {
        machine.Io.ReadByte(0x3DA);              // reset the flip-flop
        machine.Io.WriteByte(0x3C0, index);
        return machine.Io.ReadByte(0x3C1);
    }

    private static void SetMode(Machine machine, byte mode) => machine.VideoServices.SetVideoMode(mode);

    [Fact]
    public void ColourTextModeReadsBackTheRomValues()
    {
        using var machine = new Machine();
        SetMode(machine, 0x03);

        Assert.Equal(0x67, machine.Io.ReadByte(0x3CC));
        Assert.Equal(0x5F, Indexed(machine, 0x3D4, 0x00));   // horizontal total
        Assert.Equal(0x50, Indexed(machine, 0x3D4, 0x02));   // horizontal blanking start
        Assert.Equal(0xBF, Indexed(machine, 0x3D4, 0x06));   // vertical total
        Assert.Equal(0x1F, Indexed(machine, 0x3D4, 0x07));   // overflow
        Assert.Equal(0x28, Indexed(machine, 0x3D4, 0x13));   // offset: 80 columns
        Assert.Equal(0xA3, Indexed(machine, 0x3D4, 0x17));   // mode control
        Assert.Equal(0x02, Indexed(machine, 0x3C4, 0x04));   // memory mode: odd/even
        Assert.Equal(0x10, Indexed(machine, 0x3CE, 0x05));   // graphics mode: odd/even
        Assert.Equal(0x0E, Indexed(machine, 0x3CE, 0x06));   // memory map at B8000
        Assert.Equal(0x0C, Attribute(machine, 0x10));        // attribute mode: text, blink
        Assert.Equal(0x08, Attribute(machine, 0x13));        // pixel panning for 9-dot cells
    }

    [Fact]
    public void TheMonochromeCrtcAddressesAnswerToo()
    {
        using var machine = new Machine();
        SetMode(machine, 0x07);

        Assert.Equal(0x66, machine.Io.ReadByte(0x3CC));
        Assert.Equal(0x5F, Indexed(machine, 0x3B4, 0x00));
        Assert.Equal(0x0A, Indexed(machine, 0x3CE, 0x06));   // memory map at B0000
    }

    [Fact]
    public void TheLinearModeReadsBackChainFourAndTheShiftMode()
    {
        using var machine = new Machine();
        SetMode(machine, 0x13);

        Assert.Equal(0x63, machine.Io.ReadByte(0x3CC));
        Assert.Equal(0x0E, Indexed(machine, 0x3C4, 0x04));   // chain-4
        Assert.Equal(0x40, Indexed(machine, 0x3CE, 0x05));   // 256-colour shift
        Assert.Equal(0x41, Attribute(machine, 0x10));        // 8-bit colour
        Assert.Equal(0x9C, Indexed(machine, 0x3D4, 0x10));   // vertical retrace start
    }

    [Fact]
    public void GoingBackToTextClearsWhatTheGraphicsModeSet()
    {
        using var machine = new Machine();
        SetMode(machine, 0x12);
        Assert.Equal(0xE3, machine.Io.ReadByte(0x3CC));
        Assert.Equal(0xDF, Indexed(machine, 0x3D4, 0x12));   // 480 lines

        SetMode(machine, 0x03);
        Assert.Equal(0x67, machine.Io.ReadByte(0x3CC));
        Assert.Equal(0x8F, Indexed(machine, 0x3D4, 0x12));   // 400 lines
        Assert.Equal(0x02, Indexed(machine, 0x3C4, 0x04));
    }

    [Fact]
    public void FeatureControlAndSubsystemEnableReadLikeARealAdapter()
    {
        using var machine = new Machine();
        Assert.Equal(0x00, machine.Io.ReadByte(0x3CA));
        Assert.Equal(0x01, machine.Io.ReadByte(0x3C3));
    }

    [Fact]
    public void TheSwitchSenseIdentifiesAColourMonitor()
    {
        using var machine = new Machine();
        var seen = new List<bool>();
        foreach (byte clock in new byte[] { 0xE3, 0xE7, 0xEB, 0xEF })
        {
            machine.Io.WriteByte(0x3C2, clock);
            seen.Add((machine.Io.ReadByte(0x3C2) & 0x10) != 0);
        }
        Assert.Equal(new[] { true, false, false, true }, seen);
        Assert.Equal(0xEF, machine.Io.ReadByte(0x3CC));   // the write went to misc output
        Assert.NotEqual(0xFF, machine.Io.ReadByte(0x3C2) | 0x80);
    }

    [Fact]
    public void PixelsReachTheDacThroughTheAttributePalette()
    {
        using var machine = new Machine();
        var regs = machine.VideoRegisters;
        machine.VideoServices.SetVideoMode(0x03);

        // Text colour 6 is brown (entry 14h), the bright colours sit at 38h+.
        Assert.Equal(0x14, regs.PaletteIndexFor(6));
        Assert.Equal(0x39, regs.PaletteIndexFor(9));
        Assert.Equal((0xAA, 0x55, 0x00), regs.GetPaletteColor(0x14));
        Assert.Equal((0x55, 0x55, 0xFF), regs.GetPaletteColor(0x39));
        Assert.Equal((0x00, 0x00, 0xAA), regs.GetPaletteColor(regs.PaletteIndexFor(1)));

        // A driver that remaps palette register 4 to DAC entry 1 gets blue
        // for pixel value 4, as Windows' VGA driver does.
        machine.VideoServices.SetVideoMode(0x12);
        machine.Io.ReadByte(0x3DA);
        machine.Io.WriteByte(0x3C0, 0x04);
        machine.Io.WriteByte(0x3C0, 0x01);
        machine.Io.WriteByte(0x3C0, 0x20);
        Assert.Equal(1, regs.PaletteIndexFor(4));
        Assert.Equal((0x00, 0x00, 0xAA), regs.GetPaletteColor(regs.PaletteIndexFor(4)));
    }
}

/// <summary>
/// The colour text window and its odd/even mapping.
///
/// The CPU sees characters and attributes alternating at 0xB8000, but that is
/// not how they are stored: even addresses go to plane 0 and odd ones to
/// plane 1, both at the same offset. So a screenful of characters is
/// contiguous in one plane and a screenful of attributes contiguous in
/// another, which is exactly what the character generator needs -- it fetches
/// both from the same address in one memory cycle.
///
/// It matters because a program can see through the illusion: period code
/// reads plane 0 directly to scan the screen at twice the speed, and storing
/// the interleaved bytes as the CPU wrote them would hand it an alternating
/// string of characters and colour bytes.
/// </summary>
public class TextWindowMappingTests
{
    [Fact]
    public void CharactersAndAttributesLandInDifferentPlanes()
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);

        machine.VideoMemory.WriteByte(0x18000, (byte)'A');
        machine.VideoMemory.WriteByte(0x18001, 0x1F);

        Assert.Equal((byte)'A', machine.VideoMemory.ReadPlane(0, 0x8000));
        Assert.Equal(0x1F, machine.VideoMemory.ReadPlane(1, 0x8000));
    }

    [Fact]
    public void TheCpuStillSeesThemInterleaved()
    {
        // The mapping is invisible from the address bus, which is the whole
        // point: every program written since 1981 writes a character and then
        // its attribute at the next address.
        using var machine = new Machine();
        machine.RunInstructions(3000);

        machine.VideoMemory.WriteByte(0x18000, (byte)'X');
        machine.VideoMemory.WriteByte(0x18001, 0x4E);

        Assert.Equal((byte)'X', machine.VideoMemory.ReadByte(0x18000));
        Assert.Equal(0x4E, machine.VideoMemory.ReadByte(0x18001));
    }

    [Fact]
    public void ConsecutiveCharactersAreContiguousInPlaneZero()
    {
        // What the character generator wants, and what a program scanning the
        // screen through plane 0 relies on: no attribute bytes in between.
        using var machine = new Machine();
        machine.RunInstructions(3000);

        machine.VideoMemory.WriteByte(0x18000, (byte)'H');
        machine.VideoMemory.WriteByte(0x18002, (byte)'i');

        Assert.Equal((byte)'H', machine.VideoMemory.ReadPlane(0, 0x8000));
        Assert.Equal((byte)'i', machine.VideoMemory.ReadPlane(0, 0x8001));
    }

    [Fact]
    public void TheBiosHelpersUseTheSameMapping()
    {
        // The indexer bypasses the write modes -- the BIOS is writing
        // characters, not driving the graphics controller -- but it must not
        // bypass where the byte lives, or the screen would look right through
        // that one path and wrong to everything else.
        using var machine = new Machine();
        machine.RunInstructions(3000);

        machine.VideoMemory[0x18000] = (byte)'Q';
        machine.VideoMemory[0x18001] = 0x70;

        Assert.Equal((byte)'Q', machine.VideoMemory.ReadPlane(0, 0x8000));
        Assert.Equal(0x70, machine.VideoMemory.ReadPlane(1, 0x8000));
        Assert.Equal((byte)'Q', machine.VideoMemory[0x18000]);
    }
}

/// <summary>
/// The two registers that decide what the adapter shows and where it listens.
///
/// Both are invisible until something deliberately changes them, which is
/// exactly why they are worth testing: a program that uses them is relying on
/// behaviour nothing else exercises, and an emulator that ignores them looks
/// perfectly correct right up until it does not.
/// </summary>
public class VgaWindowAndMaskTests
{
    [Fact]
    public void ThePelMaskNarrowsWhichPaletteEntriesAPixelCanReach()
    {
        // What it is for: hold two complete palettes in the 256 entries and
        // switch between them with one write. Fades, day and night versions
        // of a picture, a flashing selection -- all without rewriting 768
        // bytes of DAC and without tearing.
        using var machine = new Machine();
        machine.RunInstructions(3000);

        var registers = machine.VideoRegisters;

        // Written through the DAC ports, the way a program would: an index,
        // then red, green and blue, with the index stepping on by itself.
        machine.Io.WriteByte(0x3C8, 0x01);
        machine.Io.WriteByte(0x3C9, 0x3F);
        machine.Io.WriteByte(0x3C9, 0x00);
        machine.Io.WriteByte(0x3C9, 0x00);   // entry 1: red

        machine.Io.WriteByte(0x3C8, 0x81);
        machine.Io.WriteByte(0x3C9, 0x00);
        machine.Io.WriteByte(0x3C9, 0x3F);
        machine.Io.WriteByte(0x3C9, 0x00);   // entry 0x81: green

        // With the mask wide open, pixel 0x81 finds the green.
        machine.Io.WriteByte(0x3C6, 0xFF);
        Assert.Equal(0x00, registers.GetPaletteColor(0x81).R);

        // Masking off the top bit sends the same pixel to entry 1 instead.
        machine.Io.WriteByte(0x3C6, 0x7F);
        Assert.Equal(0xFF, registers.GetPaletteColor(0x81).R);
    }

    [Fact]
    public void ThePelMaskStartsWideOpen()
    {
        // It has to: a zero here would send every pixel to colour 0 and leave
        // the screen uniformly black, which is a memorable way to appear
        // broken for a register nobody has touched.
        using var machine = new Machine();
        machine.RunInstructions(3000);

        Assert.Equal(0xFF, machine.Io.ReadByte(0x3C6));
    }

    [Fact]
    public void TheDacReportsWhetherItWasLastSetForReadingOrWriting()
    {
        // A handler that interrupted a palette update reads this to find out
        // whether it may safely take the DAC over.
        using var machine = new Machine();
        machine.RunInstructions(3000);

        machine.Io.WriteByte(0x3C8, 0x10);
        Assert.Equal(3, machine.Io.ReadByte(0x3C7));

        machine.Io.WriteByte(0x3C7, 0x10);
        Assert.Equal(0, machine.Io.ReadByte(0x3C7));
    }

    [Fact]
    public void TheAdapterOnlyAnswersInsideTheSelectedWindow()
    {
        // Selecting the 64 KB graphics window at 0xA0000 takes the adapter off
        // the bus at 0xB8000. A program writing there is writing where nothing
        // is listening, and the screen not changing is correct rather than
        // broken.
        using var machine = new Machine();
        machine.RunInstructions(3000);

        machine.Io.WriteByte(0x3CE, 0x06);
        machine.Io.WriteByte(0x3CF, 0x05);      // graphics, 0xA0000 for 64 KB

        machine.VideoMemory.WriteByte(0x18000, 0x42);
        Assert.Equal(0xFF, machine.VideoMemory.ReadByte(0x18000));
    }

    [Fact]
    public void SelectingTheCgaWindowPutsItBack()
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);

        machine.Io.WriteByte(0x3CE, 0x06);
        machine.Io.WriteByte(0x3CF, 0x0C);      // 0xB8000, 32 KB: where CGA lived

        machine.VideoMemory.WriteByte(0x18000, 0x42);
        Assert.Equal(0x42, machine.VideoMemory.ReadByte(0x18000));

        // And now 0xA0000 is the address nothing answers at.
        Assert.Equal(0xFF, machine.VideoMemory.ReadByte(0x0000));
    }
}

/// <summary>
/// The text cursor's shape, which lives entirely in two CRTC registers.
///
/// Programs really do change it: an underline for insert mode and a full
/// block for overwrite is the convention every DOS editor follows, and it is
/// done by writing two registers rather than by drawing anything. An emulator
/// that draws a fixed shape looks right at the DOS prompt and wrong in every
/// editor.
/// </summary>
public class TextCursorShapeTests
{
    private static Machine InTextMode()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        return machine;
    }

    private static void SetCrtc(Machine machine, int index, byte value)
    {
        machine.Io.WriteByte(0x3D4, (byte)index);
        machine.Io.WriteByte(0x3D5, value);
    }

    [Fact]
    public void TheShapeComesFromTheTwoRegisters()
    {
        using Machine machine = InTextMode();

        SetCrtc(machine, 0x0A, 0x0D);
        SetCrtc(machine, 0x0B, 0x0E);

        Assert.Equal((13, 14), machine.VideoRegisters.CursorShape);
    }

    [Fact]
    public void ABlockCursorCoversTheWholeCell()
    {
        // What an editor asks for in overwrite mode.
        using Machine machine = InTextMode();

        SetCrtc(machine, 0x0A, 0x00);
        SetCrtc(machine, 0x0B, 0x0F);

        Assert.Equal((0, 15), machine.VideoRegisters.CursorShape);
    }

    [Fact]
    public void BitFiveOfTheStartRegisterHidesTheCursor()
    {
        // Separate from the shape: this is how a program turns the cursor off
        // without forgetting what shape it had.
        using Machine machine = InTextMode();

        SetCrtc(machine, 0x0A, 0x20);
        Assert.False(machine.VideoRegisters.CursorVisible);

        SetCrtc(machine, 0x0A, 0x0D);
        Assert.True(machine.VideoRegisters.CursorVisible);
    }

    [Fact]
    public void TheHideBitDoesNotDisturbTheShape()
    {
        using Machine machine = InTextMode();

        SetCrtc(machine, 0x0A, 0x26);      // hidden, starting at line 6
        SetCrtc(machine, 0x0B, 0x07);

        Assert.False(machine.VideoRegisters.CursorVisible);
        Assert.Equal((6, 7), machine.VideoRegisters.CursorShape);
    }
}

/// <summary>
/// Bit 7 of a text attribute, which has meant two different things since the
/// CGA and whose meaning is a setting rather than a convention.
///
/// With blinking enabled -- what a mode set leaves -- there are eight
/// background colours and the character flashes. With it disabled there are
/// sixteen and nothing flashes. Both are used: a program wanting a bright
/// background writes the register first, and one wanting attention writes the
/// attribute and expects a blink.
/// </summary>
public class TextBlinkTests
{
    private static Machine InTextMode()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        return machine;
    }

    /// <summary>
    /// Writes an attribute controller register. Index and data share one
    /// port, and the flip-flop that says which is which is reset by reading
    /// the status register -- which is why every program that touches this
    /// reads 0x3DA first.
    /// </summary>
    private static void SetAttribute(Machine machine, byte index, byte value)
    {
        machine.Io.ReadByte(0x3DA);
        machine.Io.WriteByte(0x3C0, index);
        machine.Io.WriteByte(0x3C0, value);
    }

    [Fact]
    public void BlinkingIsOnAfterAModeSet()
    {
        // It has to be: every program written for the CGA assumes bit 7 makes
        // text flash, and a mode set is what puts the adapter into the state
        // those programs expect.
        using Machine machine = InTextMode();
        Assert.True(machine.VideoRegisters.BlinkEnabled);
    }

    [Fact]
    public void ClearingTheBitGivesSixteenBackgroundColours()
    {
        using Machine machine = InTextMode();

        SetAttribute(machine, 0x10, 0x04);      // line-graphics bit set, blink off
        Assert.False(machine.VideoRegisters.BlinkEnabled);

        SetAttribute(machine, 0x10, 0x04 | 0x08);
        Assert.True(machine.VideoRegisters.BlinkEnabled);
    }

    [Fact]
    public void ABlinkingCharacterSpendsHalfItsCycleInvisible()
    {
        // The visible consequence, checked through the frame buffer rather
        // than through a flag: the same cell, the same memory, two different
        // pictures depending on the phase.
        using Machine machine = InTextMode();

        // A blinking bright-white character on black at the top left.
        machine.VideoMemory[0x18000] = (byte)'A';
        machine.VideoMemory[0x18001] = 0x8F;

        machine.Video.CursorBlinkOn = true;
        machine.Video.Render();
        uint lit = CountLitPixels(machine);

        machine.Video.CursorBlinkOn = false;
        machine.Video.Render();
        uint dark = CountLitPixels(machine);

        Assert.True(lit > 0);
        Assert.Equal(0u, dark);
    }

    [Fact]
    public void WithBlinkingOffTheSameAttributeIsJustABrightBackground()
    {
        using Machine machine = InTextMode();
        SetAttribute(machine, 0x10, 0x04);      // blink off

        machine.VideoMemory[0x18000] = (byte)'A';
        machine.VideoMemory[0x18001] = 0x8F;

        machine.Video.CursorBlinkOn = true;
        machine.Video.Render();
        uint onPhase = CountLitPixels(machine);

        // The other phase of the blink timer changes nothing at all now.
        machine.Video.CursorBlinkOn = false;
        machine.VideoMemory.MarkRowsDirty(0, 25);
        machine.Video.Render();

        Assert.Equal(onPhase, CountLitPixels(machine));
    }

    /// <summary>Lit pixels in the top-left character cell.</summary>
    private static uint CountLitPixels(Machine machine)
    {
        var frame = machine.Video.Framebuffer;
        int width = machine.Video.Width;
        int height = machine.VideoRegisters.CharacterHeight;

        uint count = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < 8; x++)
                if ((frame[y * width + x] & 0xFFFFFF) != 0) count++;
        return count;
    }
}

/// <summary>
/// The ninth column of a text cell.
///
/// A VGA text mode is 720 pixels across, not 640, because a character cell is
/// nine pixels wide: eight of glyph and a ninth of space before the next.
/// That extra column is the whole reason text on a VGA looks less cramped
/// than text on an EGA, and it is chosen by one bit of the sequencer.
/// </summary>
public class NinthColumnTests
{
    private static Machine InTextMode()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        return machine;
    }

    [Fact]
    public void TextModeCellsAreNinePixelsWide()
    {
        using Machine machine = InTextMode();

        Assert.Equal(9, machine.VideoRegisters.CharacterWidth);
        Assert.Equal(720, machine.Video.Width);
    }

    [Fact]
    public void GraphicsModesUseEight()
    {
        // In a graphics mode a "character" is just eight pixels of a byte,
        // and a ninth would have nothing in it.
        using Machine machine = InTextMode();
        machine.VideoRegisters.VideoMode = 0x12;
        machine.VideoRegisters.NotifyModeChanged();

        Assert.Equal(8, machine.VideoRegisters.CharacterWidth);
    }

    [Fact]
    public void TheNinthColumnCarriesTheLineDrawingCharactersAcross()
    {
        // The reason the hardware has this rule at all: a horizontal rule
        // drawn out of character 0xC4 would otherwise come apart into dashes,
        // one blank column per cell, because the line reaches the right edge
        // of the glyph and stops.
        using Machine machine = InTextMode();

        machine.VideoMemory[0x18000] = 0xC4;      // horizontal line
        machine.VideoMemory[0x18001] = 0x07;
        machine.VideoMemory[0x18002] = 0xC4;
        machine.VideoMemory[0x18003] = 0x07;
        machine.VideoMemory.MarkRowsDirty(0, 25);
        machine.Video.Render();

        // Eighteen unbroken columns across two cells: no gap at the seam.
        var frame = machine.Video.Framebuffer;
        int width = machine.Video.Width;
        for (int x = 0; x < 18; x++)
            Assert.NotEqual(0u, frame[7 * width + x] & 0xFFFFFF);
    }

    [Fact]
    public void AnOrdinaryCharacterLeavesTheNinthColumnBlank()
    {
        // Only 0xC0 to 0xDF get the repeat, and that range is where the
        // line-drawing characters live. A letter would look badly spaced if
        // its last column were doubled.
        using Machine machine = InTextMode();

        // A solid block, so that its eighth column is certainly lit.
        machine.VideoMemory[0x18000] = 0xDB;
        machine.VideoMemory[0x18001] = 0x07;
        machine.VideoMemory[0x18002] = (byte)'M';
        machine.VideoMemory[0x18003] = 0x07;
        machine.VideoMemory.MarkRowsDirty(0, 25);
        machine.Video.Render();

        var frame = machine.Video.Framebuffer;
        int width = machine.Video.Width;

        // 0xDB is inside the range, so its ninth column repeats the eighth.
        Assert.NotEqual(0u, frame[7 * width + 8] & 0xFFFFFF);

        // 'M' is not, so the ninth column of the second cell stays blank
        // whatever the glyph does.
        Assert.Equal(0u, frame[7 * width + 17] & 0xFFFFFF);
    }
}

/// <summary>
/// Vertical and horizontal retrace, which have to be durations rather than a
/// bit that flips on every read.
///
/// A program polls these to find the moment the beam is off-screen, because
/// writing video memory or the palette while it is drawing produces visible
/// tearing and snow. A bit that simply alternates keeps such a program from
/// spinning for ever, which is why it was written that way first -- but it
/// makes the answer depend on how fast the polling loop runs rather than on
/// where the beam is, so anything that measures how long retrace lasts, or
/// counts frames to time an animation, gets nonsense.
/// </summary>
public class RetraceTimingTests
{
    private static Machine Started()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        return machine;
    }

    [Fact]
    public void AProgramWaitingForVerticalRetraceEventuallySeesIt()
    {
        // The first thing that must be true, and the reason the old
        // alternating bit existed at all: a wait loop has to terminate.
        using Machine machine = Started();

        bool seen = false;
        for (int i = 0; i < 5000 && !seen; i++)
        {
            machine.RunInstructions(2000);
            seen = (machine.Io.ReadByte(0x3DA) & 0x08) != 0;
        }

        Assert.True(seen);
    }

    [Fact]
    public void VerticalRetraceIsASmallFractionOfTheFrame()
    {
        // About a twentieth, as on real hardware. What matters is that it is
        // a minority of the time: a program that writes its palette during
        // retrace must find a window, not a coin flip.
        using Machine machine = Started();

        int inside = 0;
        const int samples = 3000;
        for (int i = 0; i < samples; i++)
        {
            machine.RunInstructions(2000);
            if ((machine.Io.ReadByte(0x3DA) & 0x08) != 0) inside++;
        }

        double fraction = (double)inside / samples;
        Assert.InRange(fraction, 0.01, 0.15);
    }

    [Fact]
    public void HorizontalRetraceHappensFarMoreOftenThanVertical()
    {
        // Once per scan line against once per frame: a few hundred times as
        // often, which is what tells the two apart for a program sampling
        // both.
        using Machine machine = Started();

        int horizontal = 0, vertical = 0;
        for (int i = 0; i < 3000; i++)
        {
            machine.RunInstructions(200);
            byte status = machine.Io.ReadByte(0x3DA);
            if ((status & 0x01) != 0) horizontal++;
            if ((status & 0x08) != 0) vertical++;
        }

        Assert.True(horizontal > vertical);
    }

    [Fact]
    public void ReadingTheStatusStillResetsTheAttributeFlipFlop()
    {
        // The other job this port does, and the reason every program that
        // touches the attribute controller reads it first: the index and data
        // registers share one address, and this is how software makes sure
        // the next write is taken as an index.
        using Machine machine = Started();

        // Leave the flip-flop expecting data by writing an index only.
        machine.Io.WriteByte(0x3C0, 0x10);

        machine.Io.ReadByte(0x3DA);

        // Now an index and a value both land where they should.
        machine.Io.WriteByte(0x3C0, 0x10);
        machine.Io.WriteByte(0x3C0, 0x04);
        Assert.False(machine.VideoRegisters.BlinkEnabled);
    }
}
