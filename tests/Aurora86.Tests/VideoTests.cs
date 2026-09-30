using Aurora86.Core;
using Aurora86.Core.Devices.Video;

namespace Aurora86.Tests;

public class VgaFontTests
{
    [Fact]
    public void EveryGlyphIsSixteenPixelRowsTall()
    {
        // The whole font is built on first use, so if the index arithmetic
        // overruns anywhere the failure shows up here rather than in the
        // middle of drawing the first frame.
        for (int ch = 0; ch < 256; ch++)
        {
            ReadOnlySpan<byte> glyph = VgaFont.GetGlyph((byte)ch);
            Assert.Equal(VgaFont.CharHeight, glyph.Length);
        }
    }

    [Fact]
    public void PrintableCharactersAreNotBlank()
    {
        // A letter with no lit pixels means the font never loaded, and the
        // screen would come up blank with no error anywhere to explain it.
        foreach (char ch in "ABCXYZabcxyz0189")
        {
            ReadOnlySpan<byte> glyph = VgaFont.GetGlyph((byte)ch);
            bool hasPixels = false;
            foreach (byte row in glyph) if (row != 0) { hasPixels = true; break; }
            Assert.True(hasPixels, $"character '{ch}' has no lit pixels");
        }
    }

    [Fact]
    public void TheSpaceGlyphIsBlank()
    {
        foreach (byte row in VgaFont.GetGlyph((byte)' '))
            Assert.Equal(0, row);
    }

    [Fact]
    public void BoxDrawingCharactersHavePixels()
    {
        // 0xDB is the full block: every scanline must be solid. It is the
        // character DOS draws bars and fills with.
        foreach (byte row in VgaFont.GetGlyph(0xDB))
            Assert.Equal(0xFF, row);
    }

    [Fact]
    public void VerticalDoublingProducesPairedScanlines()
    {
        // Each source row occupies two consecutive rows of the cell.
        ReadOnlySpan<byte> glyph = VgaFont.GetGlyph((byte)'A');
        for (int row = 0; row < VgaFont.CharHeight; row += 2)
            Assert.Equal(glyph[row], glyph[row + 1]);
    }
}

public class VgaRendererTests
{
    [Fact]
    public void TextWrittenToVideoMemoryBecomesPixels()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);        // POST

        // Write a white-on-black 'A' into the first cell.
        machine.VideoMemory[VgaMemory.TextWindowOffset] = (byte)'A';
        machine.VideoMemory[VgaMemory.TextWindowOffset + 1] = 0x0F;

        Assert.True(machine.Video.Render(force: true));

        // The cell covers 8x16 pixels in the top-left corner, and at least
        // one of them must be in the foreground colour.
        bool foundLitPixel = false;
        for (int y = 0; y < VgaFont.CharHeight && !foundLitPixel; y++)
        for (int x = 0; x < VgaFont.CharWidth; x++)
        {
            uint pixel = machine.Video.Framebuffer[y * machine.Video.Width + x];
            if ((pixel & 0x00FFFFFF) != 0) { foundLitPixel = true; break; }
        }

        Assert.True(foundLitPixel, "no lit pixel where an 'A' should have been drawn");
    }

    [Fact]
    public void RenderingSkipsUnchangedRows()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);

        machine.Video.Render(force: true);
        long frames = machine.Video.FramesRendered;

        // Nothing changed, so the renderer must notice and skip the work.
        Assert.False(machine.Video.Render());
        Assert.Equal(frames, machine.Video.FramesRendered);

        // After a write, though, it has to redraw.
        machine.VideoMemory[VgaMemory.TextWindowOffset] = (byte)'Z';
        Assert.True(machine.Video.Render());
    }

    [Fact]
    public void FramebufferSizeMatchesTheTextMode()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);
        machine.Video.Render(force: true);

        // 720, not 640: a VGA text cell is nine pixels wide, eight of glyph
        // and a ninth of space, which is what makes text on a VGA look less
        // cramped than text on an EGA.
        Assert.Equal(720, machine.Video.Width);
        Assert.Equal(25 * VgaFont.CharHeight, machine.Video.Height);  // 400
        Assert.Equal(machine.Video.Width * machine.Video.Height,
                     machine.Video.Framebuffer.Length);
    }

    [Fact]
    public void PaletteExpandsSixBitComponentsToFullRgb()
    {
        var registers = new VgaRegisters();

        // White in the CGA palette is 0x3F on every component, which must
        // expand to a full 0xFF at 8 bits, not 0xFC.
        (byte r, byte g, byte b) = registers.GetPaletteColor(registers.PaletteIndexFor(15));
        Assert.Equal(0xFF, r);
        Assert.Equal(0xFF, g);
        Assert.Equal(0xFF, b);

        (byte r0, byte g0, byte b0) = registers.GetPaletteColor(registers.PaletteIndexFor(0));
        Assert.Equal(0, r0);
        Assert.Equal(0, g0);
        Assert.Equal(0, b0);
    }
}

public class ScrollingTests
{
    [Fact]
    public void TextPastTheLastRowScrollsTheScreen()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);

        // Fill the screen with numbered lines, writing through the BIOS.
        for (int row = 0; row < 30; row++)
        {
            string line = $"riga{row}\r\n";
            foreach (char ch in line)
                machine.VideoServices.TeletypeOutput((byte)ch, 0);
        }

        string[] lines = machine.GetScreenText().Split(Environment.NewLine);

        // After 30 lines on a 25-line screen the first ones have scrolled off
        // the top. The trailing "\r\n" scrolls once more, so the last line
        // written ends up second from the bottom and line 24 is left blank.
        Assert.DoesNotContain("riga0", lines[0]);
        Assert.StartsWith("riga29", lines[23]);
        Assert.Equal("", lines[24].TrimEnd());
    }
}

/// <summary>
/// Tests that pin down how a pixel is laid out in the framebuffer.
///
/// This is worth a test of its own because getting it wrong is invisible in
/// every way except colour: everything renders, nothing crashes, and the only
/// symptom is that red and blue have swapped places. MS-DOS setup's blue
/// screen coming out red is exactly that bug.
/// </summary>
public class FramebufferFormatTests
{
    [Fact]
    public void PixelsArePackedRedInTheLowByte()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);

        // Fill the screen with a character whose foreground is colour 4 --
        // the standard palette's red -- on a black background.
        for (uint cell = 0; cell < 80 * 25; cell++)
        {
            machine.VideoMemory[VgaMemory.TextWindowOffset + cell * 2] = (byte)'#';
            machine.VideoMemory[VgaMemory.TextWindowOffset + cell * 2 + 1] = 0x04;
        }

        machine.Video.Render(force: true);

        // Find a lit pixel: the glyph is drawn in the foreground colour.
        uint lit = 0;
        foreach (uint pixel in machine.Video.Framebuffer)
            if ((pixel & 0x00FFFFFF) != 0) { lit = pixel; break; }

        Assert.NotEqual(0u, lit);

        byte low = (byte)lit;
        byte middle = (byte)(lit >> 8);
        byte high = (byte)(lit >> 16);

        // Red must dominate the low byte. If the packing were BGR the same
        // colour would land in the high byte instead, and the display -- which
        // is told the buffer is RGBA -- would show it as blue.
        Assert.True(low > high,
            $"expected red in the low byte, got R={low:X2} G={middle:X2} B={high:X2}");
    }
}

/// <summary>
/// Tests for the "preserve screen" bit of INT 10h AH=00.
///
/// Bit 7 of the mode number asks the BIOS to leave video memory alone. It is
/// used by anything that re-selects the current mode for a side effect --
/// loading a code page, changing the font -- and would rather not lose what
/// is on screen.
/// </summary>
public class PreserveScreenTests
{
    private static Machine BootedMachine()
    {
        var machine = new Machine();
        machine.RunInstructions(200);
        return machine;
    }

    [Fact]
    public void PreservingTheScreenAlsoPreservesTheCursor()
    {
        using Machine machine = BootedMachine();

        machine.VideoServices.SetVideoMode(0x03);
        machine.Bda.CursorRow = 8;
        machine.Bda.CursorColumn = 12;

        machine.VideoServices.SetVideoMode(0x83);   // mode 3, keep the screen

        // Homing the cursor while leaving the text in place is the worst of
        // both: the next thing printed lands on top of what is already there,
        // which is what made the DOS prompt overwrite its own boot messages.
        Assert.Equal(8, machine.Bda.CursorRow);
        Assert.Equal(12, machine.Bda.CursorColumn);
    }

    [Fact]
    public void ClearingTheScreenHomesTheCursor()
    {
        using Machine machine = BootedMachine();

        machine.Bda.CursorRow = 8;
        machine.Bda.CursorColumn = 12;

        machine.VideoServices.SetVideoMode(0x03);   // no bit 7: clear

        Assert.Equal(0, machine.Bda.CursorRow);
        Assert.Equal(0, machine.Bda.CursorColumn);
    }

    [Fact]
    public void PreservingTheScreenLeavesTheTextAlone()
    {
        using Machine machine = BootedMachine();
        machine.VideoServices.SetVideoMode(0x03);

        machine.VideoMemory[VgaMemory.TextWindowOffset] = (byte)'X';

        machine.VideoServices.SetVideoMode(0x83);

        Assert.Equal((byte)'X', machine.VideoMemory[VgaMemory.TextWindowOffset]);
    }

    [Fact]
    public void ClearingTheScreenBlanksTheText()
    {
        using Machine machine = BootedMachine();
        machine.VideoServices.SetVideoMode(0x03);

        machine.VideoMemory[VgaMemory.TextWindowOffset] = (byte)'X';

        machine.VideoServices.SetVideoMode(0x03);

        Assert.Equal((byte)' ', machine.VideoMemory[VgaMemory.TextWindowOffset]);
    }
}

/// <summary>
/// Tests for how the teletype function wraps at the right margin.
///
/// The timing of the wrap is the whole point. Getting it wrong is invisible
/// on short lines and only shows up once something fills a row exactly.
/// </summary>
public class TeletypeWrapTests
{
    private static Machine BootedMachine()
    {
        var machine = new Machine();
        machine.RunInstructions(200);
        machine.VideoServices.SetVideoMode(0x03);
        return machine;
    }

    private static void Write(Machine machine, string text)
    {
        foreach (char c in text) machine.VideoServices.TeletypeOutput((byte)c, 0);
    }

    [Fact]
    public void FillingTheLastColumnLeavesTheCursorPastIt()
    {
        using Machine machine = BootedMachine();

        Write(machine, new string('x', 80));

        // A real BIOS parks the cursor beyond the last column and only wraps
        // when the next character arrives. Wrapping straight away would make
        // the carriage return and line feed that follow move the cursor a
        // second time.
        Assert.Equal(0, machine.Bda.CursorRow);
        Assert.Equal(80, machine.Bda.CursorColumn);
    }

    [Fact]
    public void AFullLineFollowedByCarriageReturnAndLineFeedAdvancesOneRow()
    {
        using Machine machine = BootedMachine();

        Write(machine, new string('x', 80) + "\r\n");

        // This is the case that matters: DOS ends every line this way. Two
        // advances instead of one leave a blank row between every pair of
        // printed lines, and text marches down the screen twice as fast as it
        // should.
        Assert.Equal(1, machine.Bda.CursorRow);
        Assert.Equal(0, machine.Bda.CursorColumn);
    }

    [Fact]
    public void TheCharacterAfterAFullLineLandsOnTheNextRow()
    {
        using Machine machine = BootedMachine();

        Write(machine, new string('x', 80) + "Z");

        Assert.Equal((byte)'Z', machine.VideoMemory[VgaMemory.TextWindowOffset + 80 * 2]);
        Assert.Equal(1, machine.Bda.CursorRow);
        Assert.Equal(1, machine.Bda.CursorColumn);
    }

    [Fact]
    public void ShortLinesAdvanceOneRowEach()
    {
        using Machine machine = BootedMachine();

        Write(machine, "one\r\ntwo\r\nthree\r\n");

        Assert.Equal(3, machine.Bda.CursorRow);
    }

    [Fact]
    public void CarriageReturnAloneReturnsToTheStartOfTheSameRow()
    {
        using Machine machine = BootedMachine();

        Write(machine, "hello\r");

        Assert.Equal(0, machine.Bda.CursorRow);
        Assert.Equal(0, machine.Bda.CursorColumn);
    }
}

/// <summary>
/// The BIOS reports the cursor's position in DX, and reads it back from DX.
/// The two halves of that register are easy to swap, and a swap is invisible
/// on the diagonal -- row 5 column 5 survives it -- so these tests deliberately
/// use a position whose row and column differ.
/// </summary>
public class CursorPositionRoundTripTests
{
    private static Machine PoweredOn()
    {
        var machine = new Machine();
        machine.RunInstructions(200);
        return machine;
    }

    [Fact]
    public void ReadingBackTheCursorGivesTheRowAndColumnThatWereSet()
    {
        // A program that saves the cursor, prints something and puts the
        // cursor back must land where it started. When the read swaps row and
        // column, it instead lands somewhere it has already written, and the
        // text ends up on top of earlier lines.
        using var machine = PoweredOn();

        const byte Row = 8;
        const byte Column = 3;

        // AH=02, BH=page 0, DH=row, DL=column.
        machine.Cpu.State.SetReg8(4, 0x02);   // AH
        machine.Cpu.State.SetReg8(7, 0);      // BH
        machine.Cpu.State.SetReg8(6, Row);    // DH
        machine.Cpu.State.SetReg8(2, Column); // DL
        machine.VideoServices.Handle(machine.Cpu);

        // AH=03 reads it back into the same two registers.
        machine.Cpu.State.SetReg8(4, 0x03);   // AH
        machine.Cpu.State.SetReg8(7, 0);      // BH
        machine.Cpu.State.SetReg8(6, 0xFF);
        machine.Cpu.State.SetReg8(2, 0xFF);
        machine.VideoServices.Handle(machine.Cpu);

        Assert.Equal(Row, machine.Cpu.State.GetReg8(6));      // DH
        Assert.Equal(Column, machine.Cpu.State.GetReg8(2));   // DL
    }

    [Fact]
    public void TheReportedPositionMatchesTheBiosDataArea()
    {
        // The BIOS data area is the other place software looks for the cursor,
        // so the two must agree about which number is the row.
        using var machine = PoweredOn();

        machine.Cpu.State.SetReg8(4, 0x02);
        machine.Cpu.State.SetReg8(7, 0);
        machine.Cpu.State.SetReg8(6, 11);   // DH: row
        machine.Cpu.State.SetReg8(2, 40);   // DL: column
        machine.VideoServices.Handle(machine.Cpu);

        Assert.Equal(11, machine.Bda.CursorRow);
        Assert.Equal(40, machine.Bda.CursorColumn);
    }
}

/// <summary>
/// CP437's upper half carries the accented letters, the currency signs and
/// the mathematical symbols. Without them an Italian or Spanish DOS prints
/// its own messages full of holes, so what matters here is that the glyphs
/// exist and look like letters -- not that any particular pixel is set.
/// </summary>
public class HighCharacterGlyphTests
{
    private static bool IsBlank(byte code)
    {
        foreach (byte row in VgaFont.GetGlyph(code))
            if (row != 0) return false;

        return true;
    }

    [Fact]
    public void TheAccentedLettersAreDrawn()
    {
        // The five Italian accented vowels and the capital E acute: between
        // them they cover every accent that appears in MS-DOS's own Italian
        // messages.
        byte[] accented = [0x85, 0x8A, 0x8D, 0x95, 0x97, 0x90];

        foreach (byte code in accented)
            Assert.False(IsBlank(code), $"CP437 {code:X2} is blank");
    }

    [Fact]
    public void OnlyTheNonBreakingSpaceIsBlankInTheUpperHalf()
    {
        // 0xFF is a space and belongs blank. Everything else in the upper
        // half is a printable character, and a blank one would show up as a
        // hole in the middle of a word.
        for (int code = 0x80; code < 0xFF; code++)
            Assert.False(IsBlank((byte)code), $"CP437 {code:X2} is blank");

        Assert.True(IsBlank(0xFF));
    }

    [Fact]
    public void AnAccentedLetterKeepsTheShapeOfTheLetterUnderIt()
    {
        // An accent is drawn into the top two scanlines and the letter
        // occupies the rest, so the bottom of "a grave" has to match the
        // bottom of a plain "a". Get this wrong and the accented text is
        // legible but visibly a different typeface from the rest of the line.
        ReadOnlySpan<byte> plain = VgaFont.GetGlyph((byte)'a');
        ReadOnlySpan<byte> grave = VgaFont.GetGlyph(0x85);

        // The glyphs are doubled to sixteen rows, so the letter's own body
        // starts a third of the way down.
        for (int row = 8; row < VgaFont.CharHeight; row++)
            Assert.Equal(plain[row], grave[row]);
    }

    [Fact]
    public void TheAccentSitsAboveTheLetterRatherThanBesideIt()
    {
        // A grave accent leans one way and an acute the other: if they came
        // out identical, something would be copying the wrong bitmap.
        ReadOnlySpan<byte> grave = VgaFont.GetGlyph(0x8A);   // e grave
        ReadOnlySpan<byte> acute = VgaFont.GetGlyph(0x82);   // e acute

        Assert.NotEqual(grave[0], acute[0]);

        // Both must actually have something drawn up there.
        Assert.NotEqual(0, grave[0]);
        Assert.NotEqual(0, acute[0]);
    }
}

/// <summary>
/// The VGA's character generator is RAM, and software may fill it with a font
/// of its own. DOS does so for every code page but the American one, which is
/// how an Italian or Spanish system gets its accented letters, so a machine
/// that ignores the load draws those words out of whatever the ROM font
/// happens to hold.
/// </summary>
public class EffectiveModeTests
{
    [Fact]
    public void AGraphicsModeProgrammedThroughTheRegistersAloneIsRendered()
    {
        // Windows' VDD restores the Windows screen after a full-screen DOS
        // box by reloading the VGA registers, never through INT 10h: the
        // BIOS mode byte still says text while the controller shows 640x480.
        using var machine = new Machine();
        machine.RunInstructions(200);
        var regs = machine.VideoRegisters;
        regs.VideoMode = 0x03;

        regs.SetGraphics(VgaRegisters.GcMiscellaneous, 0x05);   // alphanumeric disable, A000 map
        regs.SetGraphics(VgaRegisters.GcMode, 0x00);            // planar, not shift-256
        regs.SetCrtc(0x01, 0x4F);                                // 80 character clocks -> 640 pixels
        regs.SetCrtc(0x12, 0xDF);                                // vertical display end 0x1DF -> 480 lines
        regs.SetCrtc(0x07, 0x3E);
        regs.SetCrtc(0x09, 0x40);

        Assert.Equal(VgaMemoryLayout.Planar4, regs.EffectiveMode.Layout);
        machine.Video.Render(force: true);
        Assert.Equal(640, machine.Video.Width);
        Assert.Equal(480, machine.Video.Height);
    }

    [Fact]
    public void PlanarWritesFollowTheRegistersNotTheBiosModeByte()
    {
        // After the VDD hands the display back, the BIOS mode byte still says
        // text while the controller is planar. Writes through the window must
        // honour the map mask and land in the selected plane, or the VDD's
        // plane-by-plane restore piles all four planes into one.
        using var machine = new Machine();
        machine.RunInstructions(200);
        var regs = machine.VideoRegisters;
        regs.VideoMode = 0x03;
        regs.SetGraphics(VgaRegisters.GcMiscellaneous, 0x05);
        regs.SetGraphics(VgaRegisters.GcMode, 0x00);
        regs.SetGraphics(VgaRegisters.GcBitMask, 0xFF);
        regs.SetCrtc(0x01, 0x4F); regs.SetCrtc(0x12, 0xDF); regs.SetCrtc(0x07, 0x3E); regs.SetCrtc(0x09, 0x40);

        regs.SetSequencer(VgaRegisters.SeqMapMask, 0x02);       // plane 1 only
        machine.VideoMemory.WriteByte(0x1234, 0xA5);

        Assert.Equal(0xA5, machine.VideoMemory.ReadPlane(1, 0x1234));
        Assert.Equal(0x00, machine.VideoMemory.ReadPlane(0, 0x1234));
        Assert.Equal(0x00, machine.VideoMemory.ReadPlane(2, 0x1234));
    }

    [Fact]
    public void ATwoHundredFiftySixColourModeIsRecognisedFromTheShiftBit()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);
        var regs = machine.VideoRegisters;
        regs.VideoMode = 0x03;

        regs.SetGraphics(VgaRegisters.GcMiscellaneous, 0x05);
        regs.SetGraphics(VgaRegisters.GcMode, 0x40);            // shift-256
        regs.SetCrtc(0x01, 0x4F);                                // 80 clocks, 4 pixels each -> 320
        regs.SetCrtc(0x12, 0x8F);                                // 0x18F -> 400 lines
        regs.SetCrtc(0x07, 0x1F);
        regs.SetCrtc(0x09, 0x41);                                // doubled scanlines -> 200

        VgaModeInfo mode = regs.EffectiveMode;
        Assert.Equal(VgaMemoryLayout.Linear8, mode.Layout);
        Assert.Equal(320, mode.PixelWidth);
        Assert.Equal(200, mode.PixelHeight);
    }

    [Fact]
    public void WhenTheRegistersAgreeWithTheBiosModeTheTableWins()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);
        var regs = machine.VideoRegisters;
        regs.VideoMode = 0x12;
        regs.SetGraphics(VgaRegisters.GcMiscellaneous, 0x05);

        Assert.Equal(VgaModes.Get(0x12), regs.EffectiveMode);
    }
}

public class UserFontTests
{
    private static Machine PoweredOn()
    {
        var machine = new Machine();
        machine.RunInstructions(200);
        return machine;
    }

    /// <summary>
    /// Calls INT 10h AH=11 AL=00 with a font laid out in guest memory, the
    /// way MODE does when it installs a code page.
    /// </summary>
    private static void LoadFont(Machine machine, int height, int firstCharacter,
                                 byte[] glyphs, ushort segment = 0x3000, ushort offset = 0x100)
    {
        uint address = (uint)(segment << 4) + offset;
        for (int i = 0; i < glyphs.Length; i++)
            machine.Memory.WriteByte(address + (uint)i, glyphs[i]);

        machine.Cpu.State.SetReg8(4, 0x11);              // AH: character generator
        machine.Cpu.State.SetReg8(0, 0x00);              // AL: load user font
        machine.Cpu.State.SetReg8(7, (byte)height);      // BH: bytes per character
        machine.Cpu.State.Cx = (ushort)(height > 0 ? glyphs.Length / height : 1);
        machine.Cpu.State.Dx = (ushort)firstCharacter;
        machine.Cpu.SetSegment(Aurora86.Core.Cpu.SegReg.Es, segment);
        machine.Cpu.State.Bp = offset;

        machine.VideoServices.Handle(machine.Cpu);
    }

    [Fact]
    public void AFontTheGuestLoadsReplacesTheOneInRom()
    {
        using var machine = PoweredOn();

        // One character, 16 rows, every row solid.
        var solid = new byte[16];
        Array.Fill(solid, (byte)0xFF);
        LoadFont(machine, 16, 0x41, solid);   // 'A'

        Assert.NotNull(machine.VideoRegisters.UserFont);
        Assert.Equal(16, machine.VideoRegisters.UserFontHeight);

        ReadOnlySpan<byte> loaded = machine.VideoRegisters.UserFont.AsSpan(0x41 * 16, 16);
        foreach (byte row in loaded) Assert.Equal(0xFF, row);
    }

    [Fact]
    public void CharactersOutsideTheLoadedRangeKeepTheirRomShapes()
    {
        // Software usually loads only the half it cares about: DOS replaces
        // the accented letters and leaves ASCII alone. Blanking the rest
        // would wipe out the alphabet.
        using var machine = PoweredOn();

        ReadOnlySpan<byte> romB = VgaFont.GetGlyph((byte)'B');
        byte[] expected = romB.ToArray();

        var solid = new byte[16];
        Array.Fill(solid, (byte)0xFF);
        LoadFont(machine, 16, 0x41, solid);   // only 'A'

        ReadOnlySpan<byte> b = machine.VideoRegisters.UserFont.AsSpan((byte)'B' * 16, 16);
        Assert.True(b.SequenceEqual(expected));
    }

    [Fact]
    public void ARangeOfCharactersLoadsInOrder()
    {
        // DOS hands over a run of characters in one call, and each glyph has
        // to land under its own code rather than all at the first one.
        using var machine = PoweredOn();

        const int height = 16;
        const int first = 0x80;
        const int count = 4;

        var glyphs = new byte[count * height];
        for (int i = 0; i < count; i++)
            for (int row = 0; row < height; row++)
                glyphs[i * height + row] = (byte)(i + 1);

        LoadFont(machine, height, first, glyphs);

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> glyph =
                machine.VideoRegisters.UserFont.AsSpan((first + i) * height, height);

            foreach (byte row in glyph)
                Assert.Equal((byte)(i + 1), row);
        }
    }

    [Fact]
    public void AnImpossibleCellHeightIsRefused()
    {
        // A height of zero would mean glyphs of no size at all, and the
        // arithmetic downstream divides by it. A guest is free to pass
        // nonsense, so the BIOS has to turn it away rather than fault.
        using var machine = PoweredOn();

        LoadFont(machine, 0, 0x41, new byte[16]);

        Assert.Null(machine.VideoRegisters.UserFont);
    }

    [Fact]
    public void AFontThatRunsPastTheEndOfMemoryIsTruncatedRatherThanFaulting()
    {
        // A count far larger than the glyphs actually supplied must not walk
        // off the end of the buffer: the guest's numbers are not to be
        // trusted, and a fault here would take the whole machine down.
        using var machine = PoweredOn();

        machine.Cpu.State.SetReg8(4, 0x11);
        machine.Cpu.State.SetReg8(0, 0x00);
        machine.Cpu.State.SetReg8(7, 16);
        machine.Cpu.State.Cx = 300;              // more characters than exist
        machine.Cpu.State.Dx = 0;
        machine.Cpu.SetSegment(Aurora86.Core.Cpu.SegReg.Es, 0x3000);
        machine.Cpu.State.Bp = 0x100;

        machine.VideoServices.Handle(machine.Cpu);

        // Whatever it did, it kept to a font of the right size.
        Assert.NotNull(machine.VideoRegisters.UserFont);
        Assert.Equal(256 * 16, machine.VideoRegisters.UserFont.Length);
    }
}
