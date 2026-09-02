using Aurora386.Core;
using Aurora386.Core.Devices.Video;

namespace Aurora386.Tests;

/// <summary>
/// Tests for the VGA graphics modes.
///
/// Most of these poke the registers directly rather than going through the
/// BIOS, because the point of the planar modes is precisely that the registers
/// decide what a memory write means. A test that only ever wrote pixels through
/// INT 10h would pass just as happily against an implementation that ignored
/// the graphics controller entirely.
/// </summary>
public class VgaGraphicsTests
{
    /// <summary>
    /// Builds a register/memory pair in a given mode without needing a whole
    /// machine. The registers are set the way the BIOS mode set leaves them.
    /// </summary>
    private static (VgaRegisters Registers, VgaMemory Memory) NewAdapter(byte mode)
    {
        var registers = new VgaRegisters { VideoMode = mode };
        var memory = new VgaMemory(registers);

        VgaModeInfo info = VgaModes.Get(mode);
        registers.TextColumns = info.TextColumns;
        registers.TextRows = info.TextRows;
        registers.CharacterHeight = info.CharHeight;
        registers.SetSequencer(VgaRegisters.SeqMapMask, 0x0F);
        registers.SetGraphics(VgaRegisters.GcBitMask, 0xFF);
        registers.SetCrtc(0x13, (byte)(info.BytesPerRow / 2));

        return (registers, memory);
    }

    // --- mode setup -----------------------------------------------------------

    [Fact]
    public void SettingMode12hSelectsSixHundredFortyByFourEightyPlanar()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);

        machine.VideoServices.SetVideoMode(0x12);

        VgaModeInfo info = machine.VideoRegisters.ModeInfo;
        Assert.Equal(640, info.PixelWidth);
        Assert.Equal(480, info.PixelHeight);
        Assert.Equal(VgaMemoryLayout.Planar4, info.Layout);
        Assert.True(machine.VideoRegisters.IsGraphicsMode);

        // 640 pixels at one bit per plane is 80 bytes of each plane per row.
        Assert.Equal(80, info.BytesPerRow);

        // The renderer must have resized to match, or the frontend would be
        // uploading a text-sized texture full of graphics data.
        machine.Video.Render(force: true);
        Assert.Equal(640, machine.Video.Width);
        Assert.Equal(480, machine.Video.Height);
    }

    [Theory]
    [InlineData(0x0D, 320, 200, VgaMemoryLayout.Planar4)]
    [InlineData(0x0E, 640, 200, VgaMemoryLayout.Planar4)]
    [InlineData(0x10, 640, 350, VgaMemoryLayout.Planar4)]
    [InlineData(0x12, 640, 480, VgaMemoryLayout.Planar4)]
    [InlineData(0x13, 320, 200, VgaMemoryLayout.Linear8)]
    public void EachGraphicsModeReportsItsOwnGeometry(
        byte mode, int width, int height, VgaMemoryLayout layout)
    {
        using var machine = new Machine();
        machine.RunInstructions(200);

        machine.VideoServices.SetVideoMode(mode);

        VgaModeInfo info = machine.VideoRegisters.ModeInfo;
        Assert.Equal(width, info.PixelWidth);
        Assert.Equal(height, info.PixelHeight);
        Assert.Equal(layout, info.Layout);

        machine.Video.Render(force: true);
        Assert.Equal(width, machine.Video.Width);
        Assert.Equal(height, machine.Video.Height);
        Assert.Equal(width * height, machine.Video.Framebuffer.Length);
    }

    [Fact]
    public void TextModeStillWorksAfterAGraphicsModeAndBack()
    {
        // MS-DOS boots in mode 03h and never leaves it, so a regression here is
        // the difference between a working emulator and a black screen.
        using var machine = new Machine();
        machine.RunInstructions(200);

        machine.VideoServices.SetVideoMode(0x12);
        machine.VideoServices.SetVideoMode(0x03);

        Assert.False(machine.VideoRegisters.IsGraphicsMode);
        Assert.Equal(80, machine.VideoRegisters.TextColumns);
        Assert.Equal(25, machine.VideoRegisters.TextRows);

        foreach (char ch in "HELLO")
            machine.VideoServices.TeletypeOutput((byte)ch, 0);

        Assert.StartsWith("HELLO", machine.GetScreenText());

        machine.Video.Render(force: true);
        Assert.Equal(640, machine.Video.Width);
        Assert.Equal(400, machine.Video.Height);
    }

    // --- map mask -------------------------------------------------------------

    [Fact]
    public void MapMaskLimitsAWriteToTheSelectedPlanes()
    {
        // The map mask is the "vertical" selector: it says which bits of the
        // pixels' colour may change, not which pixels.
        (VgaRegisters registers, VgaMemory memory) = NewAdapter(0x12);

        // Only planes 0 and 2 accept writes: mask 0b0101.
        registers.SetSequencer(VgaRegisters.SeqMapMask, 0x05);
        memory.WriteByte(0, 0xFF);

        Assert.Equal(0xFF, memory.ReadPlane(0, 0));
        Assert.Equal(0x00, memory.ReadPlane(1, 0));
        Assert.Equal(0xFF, memory.ReadPlane(2, 0));
        Assert.Equal(0x00, memory.ReadPlane(3, 0));
    }

    [Fact]
    public void AMapMaskOfZeroDropsTheWriteEntirely()
    {
        (VgaRegisters registers, VgaMemory memory) = NewAdapter(0x12);

        registers.SetSequencer(VgaRegisters.SeqMapMask, 0x00);
        memory.WriteByte(0, 0xFF);

        for (int plane = 0; plane < 4; plane++)
            Assert.Equal(0x00, memory.ReadPlane(plane, 0));
    }

    // --- write modes ----------------------------------------------------------

    [Fact]
    public void WriteMode0WithSetResetPaintsTheSetResetColour()
    {
        // Set/Reset is how a program writes a solid colour: instead of working
        // out a bit pattern per plane, it puts the colour in one register and
        // lets the hardware spread it across the four planes.
        (VgaRegisters registers, VgaMemory memory) = NewAdapter(0x12);

        registers.SetGraphics(VgaRegisters.GcMode, 0x00);          // write mode 0
        registers.SetGraphics(VgaRegisters.GcSetReset, 0x09);      // colour 9 = 0b1001
        registers.SetGraphics(VgaRegisters.GcEnableSetReset, 0x0F); // all planes from Set/Reset

        // The CPU byte is now only a stencil of which pixels get the colour;
        // with Set/Reset enabled its value no longer supplies the data.
        memory.WriteByte(0, 0xFF);

        Assert.Equal(0xFF, memory.ReadPlane(0, 0));   // bit 0 of 9 is set
        Assert.Equal(0x00, memory.ReadPlane(1, 0));   // bit 1 of 9 is clear
        Assert.Equal(0x00, memory.ReadPlane(2, 0));   // bit 2 of 9 is clear
        Assert.Equal(0xFF, memory.ReadPlane(3, 0));   // bit 3 of 9 is set
    }

    [Fact]
    public void WriteMode0WithoutSetResetUsesTheCpuByteOnEveryPlane()
    {
        (VgaRegisters registers, VgaMemory memory) = NewAdapter(0x12);

        registers.SetGraphics(VgaRegisters.GcMode, 0x00);
        registers.SetGraphics(VgaRegisters.GcEnableSetReset, 0x00);   // CPU data, not Set/Reset

        memory.WriteByte(0, 0xA5);

        // Every enabled plane receives the same CPU byte. That is exactly why
        // this alone cannot draw a multicoloured pattern, and why Set/Reset and
        // write mode 2 exist.
        for (int plane = 0; plane < 4; plane++)
            Assert.Equal(0xA5, memory.ReadPlane(plane, 0));
    }

    [Fact]
    public void WriteMode2SpreadsTheCpuNibbleAcrossThePlanes()
    {
        // Write mode 2 is the "plot a pixel in colour N" mode: the low nibble
        // of the CPU byte is a colour, replicated across the byte per plane.
        (VgaRegisters registers, VgaMemory memory) = NewAdapter(0x12);

        registers.SetGraphics(VgaRegisters.GcMode, 0x02);
        memory.WriteByte(0, 0x06);   // colour 6 = 0b0110

        Assert.Equal(0x00, memory.ReadPlane(0, 0));
        Assert.Equal(0xFF, memory.ReadPlane(1, 0));
        Assert.Equal(0xFF, memory.ReadPlane(2, 0));
        Assert.Equal(0x00, memory.ReadPlane(3, 0));
    }

    [Fact]
    public void WriteMode1CopiesTheLatchesVerbatim()
    {
        // Write mode 1 is the screen-to-screen copy: read one address (which
        // loads the latches), write another, and eight pixels of all four
        // planes move without the CPU ever seeing the data.
        (VgaRegisters registers, VgaMemory memory) = NewAdapter(0x12);

        // Lay down a distinctive four-plane pattern at the source.
        memory.WritePlane(0, 100, 0x11);
        memory.WritePlane(1, 100, 0x22);
        memory.WritePlane(2, 100, 0x44);
        memory.WritePlane(3, 100, 0x88);

        registers.SetGraphics(VgaRegisters.GcMode, 0x01);   // write mode 1

        memory.ReadByte(100);          // loads the latches from all four planes
        memory.WriteByte(200, 0x00);   // the CPU value is ignored in mode 1

        Assert.Equal(0x11, memory.ReadPlane(0, 200));
        Assert.Equal(0x22, memory.ReadPlane(1, 200));
        Assert.Equal(0x44, memory.ReadPlane(2, 200));
        Assert.Equal(0x88, memory.ReadPlane(3, 200));
    }

    [Fact]
    public void WriteMode3UsesTheCpuByteAsAStencilForTheSetResetColour()
    {
        // Write mode 3 is what makes a fast glyph or line routine short: the
        // colour is preset in Set/Reset, and each CPU byte written is a stencil
        // of which of the eight pixels take it.
        (VgaRegisters registers, VgaMemory memory) = NewAdapter(0x12);

        registers.SetGraphics(VgaRegisters.GcMode, 0x03);
        registers.SetGraphics(VgaRegisters.GcSetReset, 0x0F);   // white
        registers.SetGraphics(VgaRegisters.GcBitMask, 0xFF);

        memory.ReadByte(0);            // load the latches (all zero here)
        memory.WriteByte(0, 0x3C);     // stencil: the middle four pixels

        // Every plane gets the stencil, because colour 15 has all four bits set.
        for (int plane = 0; plane < 4; plane++)
            Assert.Equal(0x3C, memory.ReadPlane(plane, 0));
    }

    // --- bit mask -------------------------------------------------------------

    [Fact]
    public void TheBitMaskProtectsTheUnselectedBitsFromTheLatches()
    {
        // The bit mask is the "horizontal" selector: which of the eight pixels
        // in this byte may change. The protected ones come back from the
        // latches, which is why a dummy read has to precede the write.
        (VgaRegisters registers, VgaMemory memory) = NewAdapter(0x12);

        // Existing content: all eight pixels of plane 0 are set.
        memory.WritePlane(0, 0, 0xFF);
        memory.WritePlane(1, 0, 0xFF);
        memory.WritePlane(2, 0, 0xFF);
        memory.WritePlane(3, 0, 0xFF);

        registers.SetGraphics(VgaRegisters.GcMode, 0x00);
        registers.SetGraphics(VgaRegisters.GcEnableSetReset, 0x00);
        registers.SetGraphics(VgaRegisters.GcBitMask, 0x81);   // only the two end pixels

        memory.ReadByte(0);           // load latches with 0xFF on every plane
        memory.WriteByte(0, 0x00);    // try to clear all eight

        // Only the two masked-in bits actually cleared; the other six were
        // restored from the latches.
        for (int plane = 0; plane < 4; plane++)
            Assert.Equal(0x7E, memory.ReadPlane(plane, 0));
    }

    [Fact]
    public void ABitMaskOfZeroLeavesMemoryUnchanged()
    {
        (VgaRegisters registers, VgaMemory memory) = NewAdapter(0x12);

        for (int plane = 0; plane < 4; plane++)
            memory.WritePlane(plane, 0, 0x5A);

        registers.SetGraphics(VgaRegisters.GcBitMask, 0x00);
        memory.ReadByte(0);
        memory.WriteByte(0, 0xFF);

        for (int plane = 0; plane < 4; plane++)
            Assert.Equal(0x5A, memory.ReadPlane(plane, 0));
    }

    [Fact]
    public void TheXorRasterOpCombinesTheSourceWithTheLatch()
    {
        // XOR is what lets a program draw a rubber-band box and erase it by
        // drawing it a second time, without saving what was underneath.
        (VgaRegisters registers, VgaMemory memory) = NewAdapter(0x12);

        for (int plane = 0; plane < 4; plane++)
            memory.WritePlane(plane, 0, 0xF0);

        registers.SetGraphics(VgaRegisters.GcMode, 0x00);
        registers.SetGraphics(VgaRegisters.GcEnableSetReset, 0x00);
        registers.SetGraphics(VgaRegisters.GcDataRotate, 0x18);   // raster op 3 = XOR

        memory.ReadByte(0);
        memory.WriteByte(0, 0xFF);

        for (int plane = 0; plane < 4; plane++)
            Assert.Equal(0x0F, memory.ReadPlane(plane, 0));
    }

    // --- read modes -----------------------------------------------------------

    [Fact]
    public void ReadMode0ReturnsThePlaneChosenByReadMapSelect()
    {
        (VgaRegisters registers, VgaMemory memory) = NewAdapter(0x12);

        memory.WritePlane(0, 0, 0x11);
        memory.WritePlane(1, 0, 0x22);
        memory.WritePlane(2, 0, 0x33);
        memory.WritePlane(3, 0, 0x44);

        registers.SetGraphics(VgaRegisters.GcMode, 0x00);   // read mode 0

        for (int plane = 0; plane < 4; plane++)
        {
            registers.SetGraphics(VgaRegisters.GcReadMapSelect, (byte)plane);
            Assert.Equal((byte)(0x11 * (plane + 1)), memory.ReadByte(0));
        }
    }

    [Fact]
    public void AnyReadLoadsAllFourLatchesRegardlessOfReadMapSelect()
    {
        // The latches load on every read, not just the plane the CPU asked
        // for. Missing this is the classic VGA bug: pixels that come back
        // holding data from an unrelated address.
        (VgaRegisters registers, VgaMemory memory) = NewAdapter(0x12);

        memory.WritePlane(0, 0, 0xAA);
        memory.WritePlane(1, 0, 0xBB);
        memory.WritePlane(2, 0, 0xCC);
        memory.WritePlane(3, 0, 0xDD);

        registers.SetGraphics(VgaRegisters.GcReadMapSelect, 0x00);
        memory.ReadByte(0);

        Assert.Equal(0xAA, memory.Latches[0]);
        Assert.Equal(0xBB, memory.Latches[1]);
        Assert.Equal(0xCC, memory.Latches[2]);
        Assert.Equal(0xDD, memory.Latches[3]);
    }

    [Fact]
    public void ReadMode1ReportsWhichPixelsMatchTheColorCompareValue()
    {
        // One read answers "which of these eight pixels are colour N?" for all
        // eight at once, instead of four reads plus bit shuffling per pixel.
        (VgaRegisters registers, VgaMemory memory) = NewAdapter(0x12);

        // Build a row where the leftmost two pixels are colour 5 (0b0101) and
        // everything else is colour 0.
        memory.WritePlane(0, 0, 0xC0);   // bit 0 of the colour set for pixels 0-1
        memory.WritePlane(1, 0, 0x00);
        memory.WritePlane(2, 0, 0xC0);   // bit 2 set for the same pixels
        memory.WritePlane(3, 0, 0x00);

        registers.SetGraphics(VgaRegisters.GcMode, 0x08);        // read mode 1
        registers.SetGraphics(VgaRegisters.GcColorCompare, 0x05);
        registers.SetGraphics(VgaRegisters.GcColorDontCare, 0x0F);  // compare all planes

        Assert.Equal(0xC0, memory.ReadByte(0));

        // Asking about colour 0 gives the complement: the other six pixels.
        registers.SetGraphics(VgaRegisters.GcColorCompare, 0x00);
        Assert.Equal(0x3F, memory.ReadByte(0));
    }

    // --- mode 13h -------------------------------------------------------------

    [Fact]
    public void Mode13hWritesOnePixelPerByteAtTheExpectedOffset()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);
        machine.VideoServices.SetVideoMode(0x13);

        // Mode 13h is linear: the pixel at (x, y) is byte y * 320 + x.
        const int x = 100;
        const int y = 50;
        machine.VideoMemory.WriteByte((uint)(y * 320 + x), 42);

        Assert.Equal(42, machine.VideoMemory[(uint)(y * 320 + x)]);

        // And nothing else moved: a linear write must not touch other planes.
        Assert.Equal(0, machine.VideoMemory[(uint)(y * 320 + x + 1)]);
    }

    [Fact]
    public void Mode13hPixelsAreColouredByTheDacPalette()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);
        machine.VideoServices.SetVideoMode(0x13);

        // Program DAC entry 42 to pure red through the real ports: index to
        // 0x3C8, then three 6-bit components to 0x3C9.
        machine.Io.WriteByte(0x3C8, 42);
        machine.Io.WriteByte(0x3C9, 0x3F);   // red
        machine.Io.WriteByte(0x3C9, 0x00);   // green
        machine.Io.WriteByte(0x3C9, 0x00);   // blue

        machine.VideoMemory.WriteByte(0, 42);
        machine.Video.Render(force: true);

        // 0x3F is full scale, so it must expand to 0xFF and not 0xFC.
        uint pixel = machine.Video.Framebuffer[0];
        Assert.Equal(0xFFu, pixel & 0xFF);           // red
        Assert.Equal(0x00u, (pixel >> 8) & 0xFF);    // green
        Assert.Equal(0x00u, (pixel >> 16) & 0xFF);   // blue
    }

    [Fact]
    public void TheDacStoresOnlySixBitsPerComponent()
    {
        var registers = new VgaRegisters();

        // The DAC is six bits wide: the top two bits of a byte written to
        // 0x3C9 are simply not stored, which is why VGA palette code always
        // works in 0-63.
        registers.WriteByte(0x3C8, 7);
        registers.WriteByte(0x3C9, 0xFF);
        registers.WriteByte(0x3C9, 0xFF);
        registers.WriteByte(0x3C9, 0xFF);

        (byte r, byte g, byte b) = registers.GetRawPaletteEntry(7);
        Assert.Equal(0x3F, r);
        Assert.Equal(0x3F, g);
        Assert.Equal(0x3F, b);
    }

    // --- BIOS pixel services --------------------------------------------------

    [Fact]
    public void BiosWritePixelAndReadPixelRoundTripInPlanarMode12h()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);
        machine.VideoServices.SetVideoMode(0x12);

        // AH=0Ch: AL = colour, CX = column, DX = row.
        machine.Cpu.State.SetReg8(4, 0x0C);
        machine.Cpu.State.SetReg8(0, 9);      // colour 9
        machine.Cpu.State.Cx = 101;
        machine.Cpu.State.Dx = 77;
        machine.VideoServices.Handle(machine.Cpu);

        // AH=0Dh reads it back into AL.
        machine.Cpu.State.SetReg8(4, 0x0D);
        machine.Cpu.State.Cx = 101;
        machine.Cpu.State.Dx = 77;
        machine.VideoServices.Handle(machine.Cpu);

        Assert.Equal(9, machine.Cpu.State.GetReg8(0));

        // The neighbouring pixel must be untouched: this is what the bit mask
        // is protecting, and getting it wrong paints eight pixels at a time.
        machine.Cpu.State.Cx = 102;
        machine.VideoServices.Handle(machine.Cpu);
        Assert.Equal(0, machine.Cpu.State.GetReg8(0));
    }

    [Fact]
    public void BiosWritePixelLandsAtTheRightPlanarBitPosition()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);
        machine.VideoServices.SetVideoMode(0x12);

        // Pixel (3, 0) is bit 4 (0x10) of byte 0, because bit 7 is the
        // leftmost pixel.
        machine.Cpu.State.SetReg8(4, 0x0C);
        machine.Cpu.State.SetReg8(0, 0x0F);   // white: every plane's bit set
        machine.Cpu.State.Cx = 3;
        machine.Cpu.State.Dx = 0;
        machine.VideoServices.Handle(machine.Cpu);

        for (int plane = 0; plane < 4; plane++)
            Assert.Equal(0x10, machine.VideoMemory.ReadPlane(plane, 0));
    }

    [Fact]
    public void BiosPixelServicesWorkInMode13hToo()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);
        machine.VideoServices.SetVideoMode(0x13);

        machine.Cpu.State.SetReg8(4, 0x0C);
        machine.Cpu.State.SetReg8(0, 200);
        machine.Cpu.State.Cx = 10;
        machine.Cpu.State.Dx = 20;
        machine.VideoServices.Handle(machine.Cpu);

        Assert.Equal(200, machine.VideoMemory[(uint)(20 * 320 + 10)]);

        machine.Cpu.State.SetReg8(4, 0x0D);
        machine.VideoServices.Handle(machine.Cpu);
        Assert.Equal(200, machine.Cpu.State.GetReg8(0));
    }

    // --- rendering ------------------------------------------------------------

    [Fact]
    public void PlanarPixelsReachTheFramebufferInTheRightColour()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);
        machine.VideoServices.SetVideoMode(0x12);

        // Set the leftmost pixel of the first row to colour 4 (red) by putting
        // its bit in plane 2 only.
        machine.VideoMemory.WritePlane(2, 0, 0x80);
        machine.Video.Render(force: true);

        uint expected = ToRgba(machine.VideoRegisters.GetPaletteColor(4));
        Assert.Equal(expected, machine.Video.Framebuffer[0]);

        // The pixel next door is still colour 0.
        uint black = ToRgba(machine.VideoRegisters.GetPaletteColor(0));
        Assert.Equal(black, machine.Video.Framebuffer[1]);

        static uint ToRgba((byte R, byte G, byte B) c)
            => (uint)(c.R | (c.G << 8) | (c.B << 16) | (0xFF << 24));
    }

    [Fact]
    public void GraphicsWritesMarkOnlyTheAffectedScanLineDirty()
    {
        // Dirty-row tracking has to survive into graphics modes, or a 640x480
        // screen costs 307200 palette lookups every frame no matter how little
        // changed.
        (VgaRegisters registers, VgaMemory memory) = NewAdapter(0x12);
        memory.ClearDirty();

        // Row 100 starts at byte 100 * 80.
        memory.WriteByte(100 * 80 + 5, 0xFF);

        Assert.True(memory.HasChanges);
        Assert.True(memory.IsRowDirty(100));
        Assert.False(memory.IsRowDirty(99));
        Assert.False(memory.IsRowDirty(101));
        Assert.False(memory.IsRowDirty(0));
    }

    // --- fonts ----------------------------------------------------------------

    [Fact]
    public void TheAdapterCarriesEightFourteenAndSixteenLineFonts()
    {
        // Three fonts in ROM is what lets a text mode change its row count
        // without the raster changing at all.
        Assert.Equal(8, VgaFonts.GetGlyph((byte)'A', 8).Length);
        Assert.Equal(14, VgaFonts.GetGlyph((byte)'A', 14).Length);
        Assert.Equal(16, VgaFonts.GetGlyph((byte)'A', 16).Length);

        // Each one must actually contain the letter, not blank rows.
        foreach (int height in new[] { 8, 14, 16 })
        {
            bool lit = false;
            foreach (byte row in VgaFonts.GetGlyph((byte)'A', height))
                if (row != 0) { lit = true; break; }
            Assert.True(lit, $"the 8x{height} font has no pixels for 'A'");
        }
    }

    [Fact]
    public void LoadingTheEightLineFontGivesFiftyRowsOfText()
    {
        // INT 10h AH=11h AL=12h does not ask for 50 rows: it asks for the 8x8
        // font, and 400 raster lines divided by 8 is where the 50 comes from.
        using var machine = new Machine();
        machine.RunInstructions(200);
        machine.VideoServices.SetVideoMode(0x03);

        machine.Cpu.State.SetReg8(4, 0x11);   // AH
        machine.Cpu.State.SetReg8(0, 0x12);   // AL: load 8x8
        machine.VideoServices.Handle(machine.Cpu);

        Assert.Equal(8, machine.VideoRegisters.CharacterHeight);
        Assert.Equal(50, machine.VideoRegisters.TextRows);
        Assert.Equal(80, machine.VideoRegisters.TextColumns);

        // The raster is unchanged: still 640x400, just with smaller characters.
        machine.Video.Render(force: true);
        Assert.Equal(640, machine.Video.Width);
        Assert.Equal(400, machine.Video.Height);
    }

    [Fact]
    public void LoadingTheFourteenLineFontGivesTwentyEightRows()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);
        machine.VideoServices.SetVideoMode(0x03);

        machine.Cpu.State.SetReg8(4, 0x11);
        machine.Cpu.State.SetReg8(0, 0x11);   // load 8x14
        machine.VideoServices.Handle(machine.Cpu);

        Assert.Equal(14, machine.VideoRegisters.CharacterHeight);
        Assert.Equal(28, machine.VideoRegisters.TextRows);

        // And back to the default.
        machine.Cpu.State.SetReg8(0, 0x14);   // load 8x16
        machine.VideoServices.Handle(machine.Cpu);

        Assert.Equal(16, machine.VideoRegisters.CharacterHeight);
        Assert.Equal(25, machine.VideoRegisters.TextRows);
    }
}
