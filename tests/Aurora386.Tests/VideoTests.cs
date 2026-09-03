using Aurora386.Core;
using Aurora386.Core.Devices.Video;

namespace Aurora386.Tests;

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

        Assert.Equal(80 * VgaFont.CharWidth, machine.Video.Width);    // 640
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
        (byte r, byte g, byte b) = registers.GetPaletteColor(15);
        Assert.Equal(0xFF, r);
        Assert.Equal(0xFF, g);
        Assert.Equal(0xFF, b);

        (byte r0, byte g0, byte b0) = registers.GetPaletteColor(0);
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
