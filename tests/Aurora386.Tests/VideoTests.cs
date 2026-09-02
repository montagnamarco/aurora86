using Aurora386.Core;
using Aurora386.Core.Devices.Video;

namespace Aurora386.Tests;

public class VgaFontTests
{
    [Fact]
    public void OgniCarattereHaSedicRigheDiPixel()
    {
        // Il font si costruisce tutto alla prima richiesta: se il calcolo
        // degli indici sfora, l'errore emerge qui invece che al primo
        // fotogramma disegnato.
        for (int ch = 0; ch < 256; ch++)
        {
            ReadOnlySpan<byte> glyph = VgaFont.GetGlyph((byte)ch);
            Assert.Equal(VgaFont.CharHeight, glyph.Length);
        }
    }

    [Fact]
    public void CaratteriStampabiliNonSonoVuoti()
    {
        // Una lettera senza pixel accesi significa font non caricato: lo
        // schermo resterebbe bianco senza alcun errore visibile.
        foreach (char ch in "ABCXYZabcxyz0189")
        {
            ReadOnlySpan<byte> glyph = VgaFont.GetGlyph((byte)ch);
            bool hasPixels = false;
            foreach (byte row in glyph) if (row != 0) { hasPixels = true; break; }
            Assert.True(hasPixels, $"il carattere '{ch}' non ha pixel accesi");
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
        // 0xDB e' il blocco pieno: tutte le righe devono essere piene, ed e'
        // il carattere con cui DOS disegna barre e riempimenti.
        foreach (byte row in VgaFont.GetGlyph(0xDB))
            Assert.Equal(0xFF, row);
    }

    [Fact]
    public void IlRaddoppioVerticaleProduceRigheAppaiate()
    {
        // Ogni riga di origine occupa due righe consecutive della cella.
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

        // Scrive 'A' bianco su nero nella prima cella.
        machine.VideoMemory[VgaMemory.TextWindowOffset] = (byte)'A';
        machine.VideoMemory[VgaMemory.TextWindowOffset + 1] = 0x0F;

        Assert.True(machine.Video.Render(force: true));

        // La cella occupa 8x16 pixel in alto a sinistra: almeno uno deve
        // essere del colore di primo piano.
        bool foundLitPixel = false;
        for (int y = 0; y < VgaFont.CharHeight && !foundLitPixel; y++)
        for (int x = 0; x < VgaFont.CharWidth; x++)
        {
            uint pixel = machine.Video.Framebuffer[y * machine.Video.Width + x];
            if ((pixel & 0x00FFFFFF) != 0) { foundLitPixel = true; break; }
        }

        Assert.True(foundLitPixel, "nessun pixel acceso dove doveva esserci una 'A'");
    }

    [Fact]
    public void SenzaModificheIlRenderNonRifaIlLavoro()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);

        machine.Video.Render(force: true);
        long frames = machine.Video.FramesRendered;

        // Nulla e' cambiato: il render deve accorgersene e non ridisegnare.
        Assert.False(machine.Video.Render());
        Assert.Equal(frames, machine.Video.FramesRendered);

        // Dopo una scrittura invece deve ridisegnare.
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

        // Il bianco della tavolozza CGA e' 0x3F su tutte le componenti, che a
        // 8 bit deve dare 0xFF pieno, non 0xFC.
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

        // Riempie lo schermo di righe numerate scrivendo con il BIOS.
        for (int row = 0; row < 30; row++)
        {
            string line = $"riga{row}\r\n";
            foreach (char ch in line)
                machine.VideoServices.TeletypeOutput((byte)ch, 0);
        }

        string[] lines = machine.GetScreenText().Split(Environment.NewLine);

        // Dopo 30 righe su uno schermo da 25 le prime sono uscite in alto.
        // L'ultimo "\r\n" fa scorrere un'altra volta, quindi l'ultima riga
        // scritta si trova alla penultima posizione e la 24 resta vuota.
        Assert.DoesNotContain("riga0", lines[0]);
        Assert.StartsWith("riga29", lines[23]);
        Assert.Equal("", lines[24].TrimEnd());
    }
}
