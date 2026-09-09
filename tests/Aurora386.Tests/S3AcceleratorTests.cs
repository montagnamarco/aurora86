using Aurora386.Core;

namespace Aurora386.Tests;

/// <summary>
/// The S3's 2D drawing engine.
///
/// The idea predates the S3 and comes from IBM's 8514/A: instead of the
/// processor writing every pixel of a rectangle through a 64 KB window, it
/// tells the card where the rectangle is and the card fills it. At 1024x768
/// that is the difference between three quarters of a megabyte crossing the
/// bus and about a dozen register writes.
///
/// Every graphical system of the period is built on three operations, and
/// Windows' display driver is a thin layer over them: scrolling a window is a
/// copy, clearing it is a fill, and its border is four lines. Once the card
/// does those, a 486 can move windows about smoothly on a screen it could not
/// possibly repaint pixel by pixel.
/// </summary>
public class S3AcceleratorTests
{
    private static Machine InSvgaMode()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Cpu.State.Halted = false;

        machine.Cpu.State.Ax = 0x4F02;
        machine.Cpu.State.Bx = 0x101;            // 640x480 at 256 colours
        machine.VideoServices.Handle(machine.Cpu);

        machine.Accelerator.ResetClip(640, 480);
        return machine;
    }

    /// <summary>
    /// Writes one of the engine's sixteen-bit registers the way a word OUT
    /// reaches it through the bus: low byte first, then the high one, which
    /// completes the pair and, for the command register, starts the
    /// operation.
    /// </summary>
    private static void Write(Machine machine, ushort port, ushort value)
    {
        machine.Io.WriteByte(port, (byte)value);
        machine.Io.WriteByte((ushort)(port + 1), (byte)(value >> 8));
    }

    private static byte Pixel(Machine machine, int x, int y)
        => machine.VideoMemory.ReadLinear((uint)(y * 640 + x));

    [Fact]
    public void FillingARectangleColoursExactlyIt()
    {
        using Machine machine = InSvgaMode();

        Write(machine, 0x86E8, 10);        // x
        Write(machine, 0x82E8, 20);        // y
        Write(machine, 0x96E8, 99);        // width - 1
        Write(machine, 0x92E8, 49);        // height - 1
        Write(machine, 0xA6E8, 0x0F);      // colour
        Write(machine, 0x9AE8, 0x4000);    // fill

        Assert.Equal(0x0F, Pixel(machine, 50, 40));      // inside

        // The corners, because an off-by-one here is the classic bug: the
        // width register holds width minus one, so 99 means a hundred pixels.
        Assert.Equal(0x0F, Pixel(machine, 10, 20));
        Assert.Equal(0x0F, Pixel(machine, 109, 69));
        Assert.Equal(0x00, Pixel(machine, 110, 70));
        Assert.Equal(0x00, Pixel(machine, 9, 19));
    }

    [Fact]
    public void CopyingARectangleMovesThePicture()
    {
        // What scrolling a window is.
        using Machine machine = InSvgaMode();

        Write(machine, 0x86E8, 10);
        Write(machine, 0x82E8, 20);
        Write(machine, 0x96E8, 99);
        Write(machine, 0x92E8, 49);
        Write(machine, 0xA6E8, 0x0F);
        Write(machine, 0x9AE8, 0x4000);

        Write(machine, 0x86E8, 10);        // source
        Write(machine, 0x82E8, 20);
        Write(machine, 0x8EE8, 210);       // destination
        Write(machine, 0x8AE8, 20);
        Write(machine, 0x96E8, 99);
        Write(machine, 0x92E8, 49);
        Write(machine, 0x9AE8, 0xC0A0);    // copy, both directions increasing

        Assert.Equal(0x0F, Pixel(machine, 250, 40));
    }

    [Fact]
    public void AnOverlappingCopyGoesTheDirectionTheDriverAsksFor()
    {
        // The direction bits matter here and nowhere else. Scrolling is an
        // overlapping copy, and going the wrong way overwrites rows that have
        // not been read yet -- which is why the driver chooses and the card
        // obeys rather than deciding for itself.
        using Machine machine = InSvgaMode();

        // A single lit row at y=100.
        Write(machine, 0x86E8, 0);
        Write(machine, 0x82E8, 100);
        Write(machine, 0x96E8, 99);
        Write(machine, 0x92E8, 0);
        Write(machine, 0xA6E8, 0x0F);
        Write(machine, 0x9AE8, 0x4000);

        // Copy the ten rows from 100 up to 99: overlapping, and the driver
        // asks for increasing Y so each row is read before it is written.
        Write(machine, 0x86E8, 0);
        Write(machine, 0x82E8, 100);
        Write(machine, 0x8EE8, 0);
        Write(machine, 0x8AE8, 99);
        Write(machine, 0x96E8, 99);
        Write(machine, 0x92E8, 9);
        Write(machine, 0x9AE8, 0xC0A0);

        Assert.Equal(0x0F, Pixel(machine, 50, 99));
    }

    [Fact]
    public void ALineStopsWhereItsLengthSays()
    {
        using Machine machine = InSvgaMode();

        Write(machine, 0x86E8, 300);
        Write(machine, 0x82E8, 100);
        Write(machine, 0x96E8, 50);        // length
        Write(machine, 0xA6E8, 0x0C);
        Write(machine, 0x9AE8, 0x20A0);    // line, horizontal, increasing

        Assert.Equal(0x0C, Pixel(machine, 300, 100));
        Assert.Equal(0x0C, Pixel(machine, 350, 100));
        Assert.Equal(0x00, Pixel(machine, 351, 100));
    }

    [Fact]
    public void ClippingCutsTheRectangleRatherThanRefusingIt()
    {
        // Clipping is tested per pixel, as the hardware does it, rather than
        // by trimming the rectangle first. Half of a rectangle is drawn and
        // half is not, which is what a driver painting a window partly behind
        // another one needs.
        using Machine machine = InSvgaMode();

        Write(machine, 0xBEE8, 0x2000 | 400);      // clip left at 400

        Write(machine, 0x86E8, 380);
        Write(machine, 0x82E8, 200);
        Write(machine, 0x96E8, 49);
        Write(machine, 0x92E8, 9);
        Write(machine, 0xA6E8, 0x0A);
        Write(machine, 0x9AE8, 0x4000);

        Assert.Equal(0x00, Pixel(machine, 390, 205));   // outside the clip
        Assert.Equal(0x0A, Pixel(machine, 410, 205));   // inside
    }

    [Fact]
    public void TheEngineReportsItselfIdle()
    {
        // A driver polls the status register and waits for the engine to
        // finish. Ours finished before the write that started it returned, so
        // the wait loop finds it idle immediately -- the fastest possible
        // card.
        using Machine machine = InSvgaMode();

        Write(machine, 0x9AE8, 0x4000);

        Assert.Equal(0x00, machine.Io.ReadByte(0x9AE8) & 0x02);
    }

    [Fact]
    public void EachCommandCountsAsOneOperation()
    {
        using Machine machine = InSvgaMode();
        long before = machine.Accelerator.OperationsPerformed;

        Write(machine, 0x96E8, 9);
        Write(machine, 0x92E8, 9);
        Write(machine, 0x9AE8, 0x4000);
        Write(machine, 0x9AE8, 0x4000);

        Assert.Equal(before + 2, machine.Accelerator.OperationsPerformed);
    }
}

/// <summary>
/// The registers that decide what happens to a pixel on its way into memory:
/// the raster operation, the write mask and the colour compare.
///
/// They are what separates a card that can fill rectangles from one a
/// graphical system can actually be built on. Without them a driver has to
/// read, modify and write every pixel through the bus, which is the cost the
/// engine existed to avoid.
/// </summary>
public class S3RasterOperationTests
{
    private static Machine InSvgaMode()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Cpu.State.Halted = false;

        machine.Cpu.State.Ax = 0x4F02;
        machine.Cpu.State.Bx = 0x101;
        machine.VideoServices.Handle(machine.Cpu);

        machine.Accelerator.ResetClip(640, 480);
        return machine;
    }

    private static void Write(Machine machine, ushort port, ushort value)
    {
        machine.Io.WriteByte(port, (byte)value);
        machine.Io.WriteByte((ushort)(port + 1), (byte)(value >> 8));
    }

    private static byte Pixel(Machine machine, int x, int y)
        => machine.VideoMemory.ReadLinear((uint)(y * 640 + x));

    /// <summary>Fills a small rectangle at (10,10) in the given colour.</summary>
    private static void Fill(Machine machine, ushort colour)
    {
        Write(machine, 0x86E8, 10);
        Write(machine, 0x82E8, 10);
        Write(machine, 0x96E8, 49);
        Write(machine, 0x92E8, 49);
        Write(machine, 0xA6E8, colour);
        Write(machine, 0x9AE8, 0x4000);
    }

    [Fact]
    public void ReplacementIsWhatTheEngineDoesBeforeAnybodyAsks()
    {
        // The mix register powers on as replacement rather than zero, which
        // would mean "clear": a driver that had not set it yet would find
        // every draw painting black.
        using Machine machine = InSvgaMode();

        Fill(machine, 0x0F);

        Assert.Equal(0x0F, Pixel(machine, 20, 20));
    }

    [Fact]
    public void ExclusiveOrDrawnTwiceLeavesNothingBehind()
    {
        // How a rubber-band selection rectangle works: draw it and it
        // appears, draw it again in the same place and it vanishes, leaving
        // what was underneath exactly as it was. No saved background, no
        // second buffer -- which on a card with a megabyte of memory and a
        // screen that fills most of it is the difference between the feature
        // existing and not.
        using Machine machine = InSvgaMode();

        Fill(machine, 0x0F);

        Write(machine, 0xBAE8, 0x25);      // exclusive or, foreground colour
        Fill(machine, 0x0F);
        Assert.Equal(0x00, Pixel(machine, 20, 20));

        Write(machine, 0x9AE8, 0x4000);    // and again
        Assert.Equal(0x0F, Pixel(machine, 20, 20));
    }

    [Fact]
    public void TheWriteMaskLetsThroughOnlyTheBitsItHasSet()
    {
        // A driver that wants to change the low four bits of every pixel and
        // leave the rest alone does it here rather than reading, modifying
        // and writing back.
        using Machine machine = InSvgaMode();

        Fill(machine, 0x0F);

        Write(machine, 0xBAE8, 0x27);      // replacement, foreground colour
        Write(machine, 0xAAE8, 0x0F);      // only the low nibble
        Fill(machine, 0xF3);

        // The low nibble came from the new value, the high one stayed.
        Assert.Equal(0x03, Pixel(machine, 20, 20));
    }

    [Fact]
    public void TheColourCompareCanSkipThePixelsThatMatch()
    {
        // Drawing a sprite with a transparent colour, in one pass and with no
        // mask to carry about.
        using Machine machine = InSvgaMode();

        Fill(machine, 0x03);

        Write(machine, 0xAAE8, 0);
        Write(machine, 0xB2E8, 0x03);              // the transparent colour
        Write(machine, 0xBEE8, 0xA000 | 0x08);     // compare on, skip matches
        Fill(machine, 0x0C);

        Assert.Equal(0x03, Pixel(machine, 20, 20));
    }

    [Fact]
    public void TheColourCompareCanInsteadWriteOnlyWhereItMatches()
    {
        // The other direction, which replaces one colour throughout an area
        // without touching anything else.
        using Machine machine = InSvgaMode();

        Fill(machine, 0x03);

        Write(machine, 0xB2E8, 0x03);
        Write(machine, 0xBEE8, 0xA000 | 0x20);     // write only on a match
        Fill(machine, 0x0C);

        Assert.Equal(0x0C, Pixel(machine, 20, 20));
    }

    [Fact]
    public void WithTheCompareOffEveryPixelIsWritten()
    {
        using Machine machine = InSvgaMode();

        Fill(machine, 0x03);

        Write(machine, 0xBEE8, 0xA000);            // compare off
        Fill(machine, 0x0C);

        Assert.Equal(0x0C, Pixel(machine, 20, 20));
    }

    [Fact]
    public void TheMixCanAlsoLeaveTheDestinationAlone()
    {
        // Mix 3, which sounds useless until you notice it is how a
        // driver measures a shape without drawing it.
        using Machine machine = InSvgaMode();

        Fill(machine, 0x0F);

        Write(machine, 0xBAE8, 0x23);
        Fill(machine, 0x0C);

        Assert.Equal(0x0F, Pixel(machine, 20, 20));
    }
}

/// <summary>
/// Pixels from the processor. Bit 8 of the command -- PCDATA -- makes the
/// engine take a rectangle and then wait for its contents through the pixel
/// transfer port, drawing as the data arrives. In mask mode every bit picks
/// the foreground mix or the background one, which is how a glyph is drawn
/// in one pass; in colour mode every unit is a pixel, which is how a brush
/// is uploaded to off-screen memory. The pattern fill then tiles such a
/// brush across a window's background, anchored to the screen so that two
/// adjoining fills line up instead of showing a seam.
/// </summary>
public class S3ProcessorDataTests
{
    private static Machine InSvgaMode()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Cpu.State.Halted = false;

        machine.Cpu.State.Ax = 0x4F02;
        machine.Cpu.State.Bx = 0x101;
        machine.VideoServices.Handle(machine.Cpu);

        machine.Accelerator.ResetClip(640, 480);
        return machine;
    }

    private static void Write(Machine machine, ushort port, ushort value)
    {
        machine.Io.WriteByte(port, (byte)value);
        machine.Io.WriteByte((ushort)(port + 1), (byte)(value >> 8));
    }

    private static byte Pixel(Machine machine, int x, int y)
        => machine.VideoMemory.ReadLinear((uint)(y * 640 + x));

    /// <summary>A rectangle expecting data from the processor, 16-bit units, low byte first.</summary>
    private static void Expect(Machine machine, int x, int y, int width, int height, ushort command)
    {
        Write(machine, 0x86E8, (ushort)x);
        Write(machine, 0x82E8, (ushort)y);
        Write(machine, 0x96E8, (ushort)(width - 1));
        Write(machine, 0x92E8, (ushort)(height - 1));
        Write(machine, 0x9AE8, command);
    }

    [Fact]
    public void AMaskPicksForegroundOrBackgroundPerBit()
    {
        using Machine machine = InSvgaMode();
        Write(machine, 0xA6E8, 0x0F);
        Write(machine, 0xA2E8, 0x04);
        Write(machine, 0xBAE8, 0x0027);              // foreground: the colour register, replace
        Write(machine, 0xB6E8, 0x0007);              // background: the colour register, replace
        Write(machine, 0xBEE8, 0xA080);              // pixel control: the mask comes from the processor

        Expect(machine, 0, 32, 16, 1, 0x53B1);      // fill, PCDATA, 16-bit, low byte first
        Write(machine, 0xE2E8, 0x55AA);              // low byte 0xAA first: 10101010

        Assert.Equal(0x0F, Pixel(machine, 0, 32));
        Assert.Equal(0x04, Pixel(machine, 1, 32));
        Assert.Equal(0x0F, Pixel(machine, 2, 32));
        Assert.Equal(0x04, Pixel(machine, 8, 32));   // then 0x55: 01010101
        Assert.Equal(0x0F, Pixel(machine, 9, 32));
    }

    [Fact]
    public void ColourDataIsThePixelsThemselves()
    {
        using Machine machine = InSvgaMode();
        Write(machine, 0xBAE8, 0x0047);              // colour source: the processor's data
        Write(machine, 0xBEE8, 0xA000);

        Expect(machine, 10, 10, 4, 1, 0x53B1);
        Write(machine, 0xE2E8, 0x2211);
        Write(machine, 0xE2E8, 0x4433);

        Assert.Equal(0x11, Pixel(machine, 10, 10));
        Assert.Equal(0x22, Pixel(machine, 11, 10));
        Assert.Equal(0x33, Pixel(machine, 12, 10));
        Assert.Equal(0x44, Pixel(machine, 13, 10));
        Assert.Equal(0x00, Pixel(machine, 14, 10));
    }

    [Fact]
    public void EachRowStartsOnAFreshUnit()
    {
        // A three-pixel row in 16-bit units takes two units: the second
        // half of the second unit is padding, and the next row starts on
        // the third unit. A driver's bitmap rows are padded the same way.
        using Machine machine = InSvgaMode();
        Write(machine, 0xBAE8, 0x0047);
        Write(machine, 0xBEE8, 0xA000);

        Expect(machine, 0, 50, 3, 2, 0x53B1);
        Write(machine, 0xE2E8, 0x0201);
        Write(machine, 0xE2E8, 0xFF03);              // 0xFF is padding
        Write(machine, 0xE2E8, 0x0504);
        Write(machine, 0xE2E8, 0xFF06);

        Assert.Equal(0x03, Pixel(machine, 2, 50));
        Assert.Equal(0x04, Pixel(machine, 0, 51));
        Assert.Equal(0x06, Pixel(machine, 2, 51));
        Assert.Equal(0x00, Pixel(machine, 3, 51));
    }

    [Fact]
    public void DataAfterTheRectangleIsFullIsIgnored()
    {
        using Machine machine = InSvgaMode();
        Write(machine, 0xBAE8, 0x0047);
        Write(machine, 0xBEE8, 0xA000);

        Expect(machine, 0, 60, 2, 1, 0x53B1);
        Write(machine, 0xE2E8, 0x0201);
        Write(machine, 0xE2E8, 0x0403);

        Assert.Equal(0x00, Pixel(machine, 2, 60));
        Assert.Equal(0x00, Pixel(machine, 0, 61));
    }

    [Fact]
    public void WithTheByteOrderBitClearTheHighByteComesFirst()
    {
        using Machine machine = InSvgaMode();
        Write(machine, 0xBAE8, 0x0047);
        Write(machine, 0xBEE8, 0xA000);

        Expect(machine, 0, 70, 2, 1, 0x43B1);       // bit 12 clear
        Write(machine, 0xE2E8, 0x2211);

        Assert.Equal(0x22, Pixel(machine, 0, 70));
        Assert.Equal(0x11, Pixel(machine, 1, 70));
    }

    [Fact]
    public void APatternFillTilesAnEightByEightBrushAnchoredToTheScreen()
    {
        using Machine machine = InSvgaMode();

        // The brush: an 8x8 chequerboard uploaded off-screen at (0, 400).
        Write(machine, 0xBAE8, 0x0047);
        Write(machine, 0xBEE8, 0xA000);
        Expect(machine, 0, 400, 8, 8, 0x53B1);
        for (int row = 0; row < 8; row++)
            for (int pair = 0; pair < 4; pair++)
                Write(machine, 0xE2E8, (row & 1) == 0 ? (ushort)0x040F : (ushort)0x0F04);

        // Then painted over a rectangle that starts at an odd column.
        Write(machine, 0xBAE8, 0x0067);              // colour source: the screen, replace
        Write(machine, 0x86E8, 0);                   // the brush
        Write(machine, 0x82E8, 400);
        Write(machine, 0x8EE8, 3);                   // the destination
        Write(machine, 0x8AE8, 100);
        Write(machine, 0x96E8, 19);
        Write(machine, 0x92E8, 3);
        Write(machine, 0x9AE8, 0xE0F3);              // pattern fill

        Assert.Equal(0x04, Pixel(machine, 3, 100));  // column 3 of the motif
        Assert.Equal(0x0F, Pixel(machine, 4, 100));
        Assert.Equal(0x04, Pixel(machine, 11, 100)); // eight columns on, the same
        Assert.Equal(0x0F, Pixel(machine, 3, 101));  // the row below is inverted
        Assert.Equal(0x00, Pixel(machine, 23, 100)); // outside
    }
}

/// <summary>
/// The engine reached through memory instead of ports, the way the S3
/// Windows driver reaches it. With bit 4 of CR53 set the upper half of the
/// VGA window at A0000h stops being video memory and becomes the engine's
/// registers: the classic ones at their port numbers, and from A8100h the
/// Trio64's packed pairs, where one doubleword write sets a coordinate pair
/// or a width and a height. The driver's fills came out as nothing at all
/// before this existed: the writes landed in video memory and the engine
/// never heard of them.
/// </summary>
public class S3MemoryMappedEngineTests
{
    private static Machine InSvgaMode()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Cpu.State.Halted = false;

        machine.Cpu.State.Ax = 0x4F02;
        machine.Cpu.State.Bx = 0x101;
        machine.VideoServices.Handle(machine.Cpu);

        machine.Accelerator.ResetClip(640, 480);
        return machine;
    }

    private static void EnableMmio(Machine machine, bool on)
    {
        machine.Io.WriteByte(0x3D4, 0x39);          // unlock the extended registers
        machine.Io.WriteByte(0x3D5, 0xA5);
        machine.Io.WriteByte(0x3D4, 0x53);
        machine.Io.WriteByte(0x3D5, (byte)(on ? 0x10 : 0x00));
    }

    private static byte Pixel(Machine machine, int x, int y)
        => machine.VideoMemory.ReadLinear((uint)(y * 640 + x));

    [Fact]
    public void PackedRegistersFillARectangle()
    {
        using Machine machine = InSvgaMode();
        EnableMmio(machine, true);

        machine.Memory.WriteDword(0xA8100, (10u << 16) | 20);      // ALT_CURXY: x 10 high, y 20 low
        machine.Memory.WriteDword(0xA8148, (99u << 16) | 49);      // ALT_PCNT: width 100 high, height 50 low
        machine.Memory.WriteWord(0xA8136, 0x0027);                 // FRGD_MIX: foreground colour, replace
        machine.Memory.WriteWord(0xAA6E8, 0x0F);                   // the colour, at its port number
        machine.Memory.WriteWord(0xA8118, 0x40F3);                 // rectangle fill, as the driver writes it

        Assert.Equal(0x0F, Pixel(machine, 10, 20));
        Assert.Equal(0x0F, Pixel(machine, 109, 69));
        Assert.Equal(0x00, Pixel(machine, 110, 69));
        Assert.Equal(0x00, Pixel(machine, 109, 70));
    }

    [Fact]
    public void TheWindowIsVideoMemoryAgainWhenTheBitIsClear()
    {
        using Machine machine = InSvgaMode();
        EnableMmio(machine, true);
        EnableMmio(machine, false);

        machine.Memory.WriteWord(0xA8118, 0x40F3);       // a command that must not run
        Assert.Equal(0xF3, machine.VideoMemory.ReadLinear(0x8118));
        Assert.Equal(0x40, machine.VideoMemory.ReadLinear(0x8119));
    }

    [Fact]
    public void TheLowerHalfIsNotTheScreenWhileTheEngineIsMapped()
    {
        using Machine machine = InSvgaMode();
        EnableMmio(machine, true);

        machine.Memory.WriteByte(0xA0000, 0x77);          // image transfer area, not pixel 0
        Assert.Equal(0x00, machine.VideoMemory.ReadLinear(0));
    }

    [Fact]
    public void ARegisterReadsBackWhatWasWritten()
    {
        using Machine machine = InSvgaMode();
        EnableMmio(machine, true);

        machine.Memory.WriteDword(0xA8100, (321u << 16) | 123);
        Assert.Equal(123, machine.Memory.ReadWord(0xA8100));     // Y
        Assert.Equal(321, machine.Memory.ReadWord(0xA8102));     // X

        // The multifunction read-back at BEE8h: select the top clip and read.
        machine.Memory.WriteWord(0xA8138, 0x0044);
        machine.Memory.WriteWord(0xA8146, 0x0001);
        Assert.Equal(0x0044, machine.Memory.ReadWord(0xABEE8));
    }

    [Fact]
    public void ThePortsAreTheSameEngine()
    {
        using Machine machine = InSvgaMode();
        EnableMmio(machine, true);

        // Coordinates through memory, the command through the port.
        machine.Memory.WriteDword(0xA8100, (5u << 16) | 5);
        machine.Memory.WriteDword(0xA8148, (9u << 16) | 9);
        machine.Memory.WriteWord(0xA8136, 0x0027);
        machine.Memory.WriteWord(0xAA6E8, 0x0C);
        machine.Io.WriteWord(0x9AE8, 0x40F3);

        Assert.Equal(0x0C, Pixel(machine, 5, 5));
        Assert.Equal(0x0C, Pixel(machine, 14, 14));
        Assert.Equal(0x00, Pixel(machine, 15, 15));
    }
}
