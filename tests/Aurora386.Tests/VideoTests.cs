using Aurora386.Core;
using Aurora386.Core.Devices.Video;

namespace Aurora386.Tests;

public class VgaFontTests
{
    [Fact]
    public void OgniCarattereHaSedicRigheDiPixel()
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
    public void CaratteriStampabiliNonSonoVuoti()
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
    public void SpazioEVuoto()
    {
        foreach (byte row in VgaFont.GetGlyph((byte)' '))
            Assert.Equal(0, row);
    }

    [Fact]
    public void CaratteriSemigraficiSonoDisegnati()
    {
        // 0xDB is the full block: every scanline must be solid. It is the
        // character DOS draws bars and fills with.
        foreach (byte row in VgaFont.GetGlyph(0xDB))
            Assert.Equal(0xFF, row);
    }

    [Fact]
    public void IlRaddoppioVerticaleProduceRigheAppaiate()
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
    public void IlTestoScrittoDiventaPixel()
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
    public void SenzaModificheIlRenderNonRifaIlLavoro()
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
    public void LaDimensioneDelFramebufferCorrispondeAlModoTesto()
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
    public void LaTavolozzaConverteLeComponentiA6BitInRgbPieno()
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
    public void IlTestoOltreLUltimaRigaFaScorrereLoSchermo()
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
