using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Video;

namespace Aurora386.Tests;

/// <summary>
/// The VESA BIOS Extension.
///
/// Before VBE, a program that wanted 800x600 had to know which card it was
/// running on and drive that card's own registers, because every maker had
/// invented their own. Games shipped with a menu of card names: you picked
/// yours from the list and hoped. VBE replaced the list with a question --
/// the program asks the card what it can do, and the answer has the same
/// shape whoever built it.
///
/// The return convention is unusual enough to be worth stating: AL=0x4F means
/// the function exists, and AH is the result, zero for success. A program
/// that checks AX==0x004F is checking both at once, which is what VESA
/// intended.
/// </summary>
public class VesaTests
{
    private static Machine Started()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Cpu.State.Halted = false;
        return machine;
    }

    /// <summary>Calls INT 10h the way a program would, through the registers.</summary>
    private static void Int10(Machine machine, ushort ax,
                              ushort bx = 0, ushort cx = 0, ushort dx = 0,
                              ushort es = 0, ushort di = 0)
    {
        machine.Cpu.SetSegment(SegReg.Es, es);
        machine.Cpu.State.Ax = ax;
        machine.Cpu.State.Bx = bx;
        machine.Cpu.State.Cx = cx;
        machine.Cpu.State.Dx = dx;
        machine.Cpu.State.Di = di;
        machine.VideoServices.Handle(machine.Cpu);
    }

    [Fact]
    public void TheCardIdentifiesItselfAsVbeTwo()
    {
        using Machine machine = Started();

        Int10(machine, 0x4F00, es: 0x5000);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);

        // "VESA" at the start of the block, then the version. A program reads
        // the signature to be sure it is looking at a real answer rather than
        // whatever was in its buffer.
        uint at = 0x50000;
        Assert.Equal((byte)'V', machine.Memory.ReadByte(at));
        Assert.Equal((byte)'E', machine.Memory.ReadByte(at + 1));
        Assert.Equal((byte)'S', machine.Memory.ReadByte(at + 2));
        Assert.Equal((byte)'A', machine.Memory.ReadByte(at + 3));
        Assert.Equal(0x0200, machine.Memory.ReadWord(at + 4));
    }

    [Fact]
    public void MemoryIsReportedInSixtyFourKilobyteUnits()
    {
        // The unit VBE chose, and the reason a card with two megabytes
        // reports 32 rather than a number of bytes.
        using Machine machine = Started();

        Int10(machine, 0x4F00, es: 0x5000);

        Assert.Equal(VgaMemory.TotalSize / 0x10000, machine.Memory.ReadWord(0x50000 + 18));
    }

    [Fact]
    public void TheModeListIsReachedThroughAPointerAndEndsInAllOnes()
    {
        // The list is pointed at rather than copied, because a program is
        // entitled to keep the pointer and read it later. It walks the list
        // until 0xFFFF, so a missing terminator sends it off through whatever
        // follows in ROM.
        using Machine machine = Started();

        Int10(machine, 0x4F00, es: 0x5000);

        uint block = 0x50000;
        uint list = (uint)(machine.Memory.ReadWord(block + 16) << 4)
                  + machine.Memory.ReadWord(block + 14);

        var modes = new List<ushort>();
        for (int i = 0; i < 64; i++)
        {
            ushort mode = machine.Memory.ReadWord(list + (uint)i * 2);
            if (mode == 0xFFFF) break;
            modes.Add(mode);
        }

        Assert.Contains((ushort)0x101, modes);       // 640x480x8
        Assert.Contains((ushort)0x105, modes);       // 1024x768x8
        Assert.True(modes.Count < 64);               // it did terminate
    }

    [Theory]
    [InlineData(0x140, 640, 480)]
    [InlineData(0x141, 800, 600)]
    [InlineData(0x220, 640, 480)]
    [InlineData(0x221, 800, 600)]
    public void ThirtyTwoBitModesAreDiscoverableAndDescribeXrgb(ushort mode, int width, int height)
    {
        using Machine machine = Started();
        Int10(machine, 0x4F00, es: 0x5000);
        uint list = (uint)(machine.Memory.ReadWord(0x50010) << 4)
                  + machine.Memory.ReadWord(0x5000E);
        var modes = new List<ushort>();
        for (uint i = 0; i < 64; i++)
        {
            ushort number = machine.Memory.ReadWord(list + i * 2);
            if (number == 0xFFFF) break;
            modes.Add(number);
        }
        Assert.Contains(mode, modes);
        Int10(machine, 0x4F01, cx: mode, es: 0x5100);
        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        const uint info = 0x51000;
        Assert.Equal(width, machine.Memory.ReadWord(info + 18));
        Assert.Equal(height, machine.Memory.ReadWord(info + 20));
        Assert.Equal(width * 4, machine.Memory.ReadWord(info + 16));
        Assert.Equal(32, machine.Memory.ReadByte(info + 25));
        Assert.Equal(6, machine.Memory.ReadByte(info + 27));
        Assert.Equal(7, machine.Memory.ReadByte(info + 2)); // bank A exists, readable and writable
        Assert.Equal(0, machine.Memory.ReadByte(info + 3)); // no bank B
        Assert.Equal(VgaMemory.TotalSize / (width * height * 4) - 1,
                     machine.Memory.ReadByte(info + 29));
        byte[] layout = [8, 16, 8, 8, 8, 0, 8, 24];
        for (uint i = 0; i < layout.Length; i++)
            Assert.Equal(layout[i], machine.Memory.ReadByte(info + 31 + i));
        Assert.Equal(0, machine.Memory.ReadByte(info + 39)); // reserved bits not usable as colour
        for (uint i = 50; i < 256; i++) Assert.Equal(0, machine.Memory.ReadByte(info + i));
    }

    [Theory]
    [InlineData(0x112, 640, 480, 1920)]
    [InlineData(0x115, 800, 600, 2400)]
    public void StandardTrueColourModesRemainPacked24(
        ushort mode,
        int expectedWidth,
        int expectedHeight,
        int expectedPitch)
    {
        using Machine machine = Started();

        Int10(machine, 0x4F01, cx: mode, es: 0x5100);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        const uint info = 0x51000;
        Assert.Equal(expectedWidth, machine.Memory.ReadWord(info + 18));
        Assert.Equal(expectedHeight, machine.Memory.ReadWord(info + 20));
        Assert.Equal(expectedPitch, machine.Memory.ReadWord(info + 16));
        Assert.Equal(24, machine.Memory.ReadByte(info + 25));
    }

    [Fact]
    public void S3OemTrueColourModesRequireMoreThanOneMegabyte()
    {
        const int oneMegabyte = 1024 * 1024;

        VesaModes.VesaModeInfo[] offered = VesaModes.Available(oneMegabyte).ToArray();

        Assert.DoesNotContain(offered, mode => mode.Number == 0x220);
        Assert.DoesNotContain(offered, mode => mode.Number == 0x221);
    }

    [Theory]
    [InlineData(0x140, false)]
    [InlineData(0x140, true)]
    [InlineData(0x141, false)]
    [InlineData(0x141, true)]
    public void ThirtyTwoBitPixelsRenderAcrossBanksAndWithAScrolledStride(ushort mode, bool linear)
    {
        using Machine machine = Started();
        machine.Io.WriteByte(0x92, 0x02); // enable A20 before addressing the full linear buffer
        ushort request = (ushort)(mode | (linear ? 0x4000 : 0));
        Int10(machine, 0x4F02, bx: request);
        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        int width = mode == 0x140 ? 640 : 800;
        int height = mode == 0x140 ? 480 : 600;
        Int10(machine, 0x4F03);
        Assert.Equal(request, machine.Cpu.State.Bx);
        int stride = (width + 16) * 4;
        Int10(machine, 0x4F06, cx: (ushort)(width + 16));
        Assert.Equal(stride, machine.Cpu.State.Bx);
        Assert.Equal(width + 16, machine.Cpu.State.Cx);
        Int10(machine, 0x4F07, cx: 3, dx: 1);
        Int10(machine, 0x4F07, bx: 1);
        Assert.Equal(3, machine.Cpu.State.Cx);
        Assert.Equal(1, machine.Cpu.State.Dx);
        machine.Video.Render(true);

        void Pixel(int x, int y, uint value)
        {
            uint offset = (uint)((y + 1) * stride + (x + 3) * 4);
            if (linear) machine.Memory.WriteDword(machine.LinearFrameBufferBase + offset, value);
            else
            {
                Int10(machine, 0x4F05, dx: (ushort)(offset >> 16));
                machine.Memory.WriteDword(0xA0000 + (offset & 0xFFFF), value);
            }
        }
        Pixel(0, 0, 0xA5123456);
        Pixel(width - 1, height - 1, 0x00789ABC);
        // Pixel immediately after a 64 KB boundary in the virtual screen.
        int boundaryY = 65536 / stride - 1;
        int boundaryX = (65536 % stride) / 4 - 3;
        Pixel(boundaryX - 1, boundaryY, 0xFFFF0000);
        Pixel(boundaryX, boundaryY, 0x8000FF00);
        machine.Video.Render();
        Assert.Equal(0xFF563412u, machine.Video.Framebuffer[0]);
        Assert.Equal(0xFFBC9A78u, machine.Video.Framebuffer[width * height - 1]);
        Assert.Equal(0xFF0000FFu, machine.Video.Framebuffer[boundaryY * width + boundaryX - 1]);
        Assert.Equal(0xFF00FF00u, machine.Video.Framebuffer[boundaryY * width + boundaryX]);
        uint[] incremental = machine.Video.Framebuffer.ToArray();
        machine.Video.Render(true);
        Assert.Equal(incremental, machine.Video.Framebuffer.ToArray());
    }

    [Fact]
    public void AModeTooLargeForTheMemoryIsNotOffered()
    {
        // Reporting a mode and then failing to set it is worse than not
        // reporting it: a program that asked the card what it could do is
        // entitled to believe the answer.
        using Machine machine = Started();

        // 1024x768 at 24 bits needs 2.25 MB, and this card has two.
        Int10(machine, 0x4F01, cx: 0x118, es: 0x5100);

        Assert.Equal(0x014F, machine.Cpu.State.Ax);
    }

    [Fact]
    public void AModeDescribesItsGeometryAndItsFrameBuffer()
    {
        using Machine machine = Started();

        Int10(machine, 0x4F01, cx: 0x105, es: 0x5100);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);

        uint info = 0x51000;
        Assert.Equal(1024, machine.Memory.ReadWord(info + 18));
        Assert.Equal(768, machine.Memory.ReadWord(info + 20));
        Assert.Equal(8, machine.Memory.ReadByte(info + 25));
        Assert.Equal(1024, machine.Memory.ReadWord(info + 16));      // bytes per row

        // The field that changed everything: where the whole picture is,
        // addressable as one flat array.
        Assert.Equal(machine.LinearFrameBufferBase, machine.Memory.ReadDword(info + 40));
    }

    [Fact]
    public void SettingAModeChangesWhatTheRendererDraws()
    {
        using Machine machine = Started();

        Int10(machine, 0x4F02, bx: 0x101);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Equal(VgaMemoryLayout.HighColour, machine.VideoRegisters.EffectiveMode.Layout);
        Assert.Equal(640, machine.Video.Width);
        Assert.Equal(480, machine.Video.Height);
    }

    [Fact]
    public void PixelsWrittenThroughTheWindowAppear()
    {
        using Machine machine = Started();
        Int10(machine, 0x4F02, bx: 0x101);

        machine.VideoMemory.WriteByte(0, 0x0F);
        machine.VideoMemory.MarkRowsDirty(0, 480);
        machine.Video.Render();

        Assert.NotEqual(0u, machine.Video.Framebuffer[0] & 0xFFFFFF);
    }

    [Fact]
    public void TheWindowMovesWhereTheBankRegisterSaysAndTheScreenDoesNot()
    {
        // The distinction that makes banking work: the bank register moves
        // what the processor can reach, not what the monitor shows. The beam
        // draws the whole picture from memory whatever slice is mapped.
        using Machine machine = Started();
        Int10(machine, 0x4F02, bx: 0x101);

        // Bank 1: the window at 0xA0000 now shows the second 64 KB.
        Int10(machine, 0x4F05, bx: 0x0000, dx: 1);
        Assert.Equal(0x004F, machine.Cpu.State.Ax);

        machine.VideoMemory.WriteByte(0, 0x2A);

        // The byte landed at 0x10000 in video memory, which is row 102 of a
        // 640-byte-per-row mode -- not row 0.
        Assert.Equal(0x2A, machine.VideoMemory.ReadLinear(0x10000));
        Assert.Equal(0, machine.VideoMemory.ReadLinear(0));
    }

    [Fact]
    public void TheBankCanBeReadBack()
    {
        using Machine machine = Started();
        Int10(machine, 0x4F02, bx: 0x101);

        Int10(machine, 0x4F05, bx: 0x0000, dx: 3);
        Int10(machine, 0x4F05, bx: 0x0100);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Equal(3, machine.Cpu.State.Dx);
    }

    [Fact]
    public void TheLinearFrameBufferReachesTheSameMemory()
    {
        // What a protected-mode display driver uses: no window, no banking,
        // the whole picture at one address.
        using Machine machine = Started();
        Int10(machine, 0x4F02, bx: 0x101);

        machine.Memory.WriteByte(machine.LinearFrameBufferBase + 640, 0x0A);

        Assert.Equal(0x0A, machine.VideoMemory.ReadLinear(640));

        machine.Video.Render();
        Assert.NotEqual(0u, machine.Video.Framebuffer[640] & 0xFFFFFF);
    }

    [Fact]
    public void SettingAModeClearsTheScreenUnlessAskedNotTo()
    {
        // A program that has just changed resolution should not see the
        // previous mode's pixels reinterpreted at the new one, which looks
        // like static. Bit 15 asks for them to be kept.
        using Machine machine = Started();
        Int10(machine, 0x4F02, bx: 0x101);

        machine.VideoMemory.WriteByte(0, 0x3C);
        Int10(machine, 0x4F02, bx: 0x101);
        Assert.Equal(0, machine.VideoMemory.ReadLinear(0));

        machine.VideoMemory.WriteByte(0, 0x3C);
        Int10(machine, 0x4F02, bx: 0x8101);
        Assert.Equal(0x3C, machine.VideoMemory.ReadLinear(0));
    }

    [Fact]
    public void AVbeCallCanAlsoReturnToAnOrdinaryVgaMode()
    {
        // Programs do exactly this on the way out, rather than switching back
        // to INT 10h AH=00.
        using Machine machine = Started();
        Int10(machine, 0x4F02, bx: 0x101);

        Int10(machine, 0x4F02, bx: 0x03);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Null(machine.VideoRegisters.VesaMode);
        Assert.Equal(VgaMemoryLayout.Text, machine.VideoRegisters.EffectiveMode.Layout);
    }

    [Theory]
    [InlineData(0x03, VgaMemoryLayout.Text)]
    [InlineData(0x83, VgaMemoryLayout.Text)]
    [InlineData(0x13, VgaMemoryLayout.Linear8)]
    public void LegacyBiosModeSetReplacesTheVbeDisplay(ushort mode, VgaMemoryLayout layout)
    {
        using Machine machine = Started();
        Int10(machine, 0x4F02, bx: 0x4103);
        machine.VideoRegisters.VesaWindowBank = 3;
        machine.VideoRegisters.VesaScanLineBytes = 1024;
        machine.VideoRegisters.VesaDisplayStart = 4096;

        Int10(machine, mode);

        Assert.Null(machine.VideoRegisters.VesaMode);
        Assert.False(machine.VideoRegisters.VesaLinearFrameBuffer);
        Assert.Equal(0, machine.VideoRegisters.VesaWindowBank);
        Assert.Equal(0, machine.VideoRegisters.VesaScanLineBytes);
        Assert.Equal(0, machine.VideoRegisters.VesaDisplayStart);
        Assert.Equal(layout, machine.VideoRegisters.EffectiveMode.Layout);
        Int10(machine, 0x4F03);
        Assert.Equal((ushort)(mode & 0x7F), machine.Cpu.State.Bx);
        machine.Video.Render(true);
        Assert.NotEqual(800, machine.Video.Width);
    }

    [Fact]
    public void TheCurrentModeCanBeAskedFor()
    {
        using Machine machine = Started();
        Int10(machine, 0x4F02, bx: 0x4103);      // 800x600x8, linear

        Int10(machine, 0x4F03);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Equal(0x4103, machine.Cpu.State.Bx);
    }

    [Fact]
    public void AnUnknownSubfunctionSaysSoRatherThanStayingSilent()
    {
        // AL=0x4F says VBE is here, AH=0x01 that this call failed. Returning
        // nothing would leave a program unable to tell the two apart.
        using Machine machine = Started();

        Int10(machine, 0x4F42);

        Assert.Equal(0x014F, machine.Cpu.State.Ax);
    }
}

/// <summary>
/// The three VBE calls that make a picture larger than the screen useful.
///
/// 4F06 sets how many bytes lie between one scan line and the next, which
/// need not be the visible width; 4F07 says which pixel of that larger
/// picture appears at the top left. Together they are hardware scrolling at
/// SVGA resolutions, where copying the screen every frame is out of the
/// question -- one register write moves the whole picture and nothing is
/// copied at all.
///
/// 4F08 is unrelated but belongs to the same era: it asks the card for an
/// eight-bit palette instead of the VGA's six, which is the difference
/// between a smooth gradient and a visibly banded one.
/// </summary>
public class VesaScrollAndPaletteTests
{
    private static Machine Started()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Cpu.State.Halted = false;
        return machine;
    }

    private static void Int10(Machine machine, ushort ax,
                              ushort bx = 0, ushort cx = 0, ushort dx = 0)
    {
        machine.Cpu.State.Ax = ax;
        machine.Cpu.State.Bx = bx;
        machine.Cpu.State.Cx = cx;
        machine.Cpu.State.Dx = dx;
        machine.VideoServices.Handle(machine.Cpu);
    }

    [Fact]
    public void ALongerScanLineReportsHowManyLinesNowFit()
    {
        using Machine machine = Started();
        Int10(machine, 0x4F02, bx: 0x101);        // 640x480x8

        Int10(machine, 0x4F06, bx: 0x0002, cx: 1024);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Equal(1024, machine.Cpu.State.Bx);      // bytes per line
        Assert.Equal(1024, machine.Cpu.State.Cx);      // pixels, at one byte each
        Assert.True(machine.Cpu.State.Dx > 480);       // more lines than the screen
    }

    [Fact]
    public void AScanLineNarrowerThanTheScreenIsRefusedQuietly()
    {
        // A line shorter than the picture would make the display read the
        // same bytes twice and show the picture folded onto itself.
        using Machine machine = Started();
        Int10(machine, 0x4F02, bx: 0x101);

        Int10(machine, 0x4F06, bx: 0x0002, cx: 100);

        Assert.Equal(640, machine.Cpu.State.Bx);
    }

    [Fact]
    public void MovingTheDisplayStartScrollsWithoutCopyingAnything()
    {
        // The whole point: a column drawn off the right of the screen appears
        // at the left after one register write.
        using Machine machine = Started();
        Int10(machine, 0x4F02, bx: 0x101);
        Int10(machine, 0x4F06, bx: 0x0002, cx: 1024);

        for (int y = 0; y < 480; y++)
            machine.VideoMemory.WriteLinear((uint)(y * 1024 + 700), 0x0F);

        machine.VideoMemory.MarkRowsDirty(0, 480);
        machine.Video.Render();
        Assert.Equal(0u, machine.Video.Framebuffer[0] & 0xFFFFFF);

        Int10(machine, 0x4F07, bx: 0x0000, cx: 700, dx: 0);

        machine.VideoMemory.MarkRowsDirty(0, 480);
        machine.Video.Render();
        Assert.NotEqual(0u, machine.Video.Framebuffer[0] & 0xFFFFFF);
    }

    [Fact]
    public void TheDisplayStartCanBeReadBackInPixels()
    {
        using Machine machine = Started();
        Int10(machine, 0x4F02, bx: 0x101);
        Int10(machine, 0x4F06, bx: 0x0002, cx: 1024);

        Int10(machine, 0x4F07, bx: 0x0000, cx: 100, dx: 5);
        Int10(machine, 0x4F07, bx: 0x0001);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Equal(100, machine.Cpu.State.Cx);
        Assert.Equal(5, machine.Cpu.State.Dx);
    }

    [Fact]
    public void AModeSetPutsTheVirtualScreenBack()
    {
        // A program that had set up a wider virtual screen has just asked for
        // a different mode, and should not inherit the old geometry.
        using Machine machine = Started();
        Int10(machine, 0x4F02, bx: 0x101);
        Int10(machine, 0x4F06, bx: 0x0002, cx: 1024);
        Int10(machine, 0x4F07, bx: 0x0000, cx: 100, dx: 5);

        Int10(machine, 0x4F02, bx: 0x103);

        Assert.Equal(0, machine.VideoRegisters.VesaScanLineBytes);
        Assert.Equal(0, machine.VideoRegisters.VesaDisplayStart);
    }

    [Fact]
    public void TheDacCanBeAskedForEightBitsPerColour()
    {
        using Machine machine = Started();

        Int10(machine, 0x4F08, bx: 0x0800);

        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Equal(8, machine.Cpu.State.Bx >> 8);
        Assert.Equal(8, machine.VideoRegisters.DacBitsPerColour);
    }

    [Fact]
    public void AtSixBitsFullScaleStillMeansWhite()
    {
        // The VGA's own arrangement: six bits expanded by repeating the top
        // two at the bottom, so 0x3F maps to 0xFF rather than 0xFC.
        using Machine machine = Started();

        machine.Io.WriteByte(0x3C8, 0x01);
        machine.Io.WriteByte(0x3C9, 0x3F);
        machine.Io.WriteByte(0x3C9, 0x3F);
        machine.Io.WriteByte(0x3C9, 0x3F);

        Assert.Equal((byte)0xFF, machine.VideoRegisters.GetPaletteColor(1).R);
    }

    [Fact]
    public void AtEightBitsTheStoredValueIsTheColour()
    {
        // Nothing is expanded any more: the program is writing the full
        // range, which is the whole reason it asked.
        using Machine machine = Started();
        Int10(machine, 0x4F08, bx: 0x0800);

        machine.Io.WriteByte(0x3C8, 0x01);
        machine.Io.WriteByte(0x3C9, 0x3F);
        machine.Io.WriteByte(0x3C9, 0x00);
        machine.Io.WriteByte(0x3C9, 0x00);

        // 0x3F is a middling value at eight bits, not full scale.
        Assert.Equal((byte)0x3F, machine.VideoRegisters.GetPaletteColor(1).R);
    }
}

/// <summary>
/// The half of the eight-bit DAC that is easy to forget: the writing side.
/// </summary>
public class DacWidthWriteTests
{
    [Fact]
    public void AtEightBitsTheTopTwoBitsSurviveBeingWritten()
    {
        // A VGA's DAC keeps six bits and discards the rest, which is the
        // width of the hardware rather than rounding. Truncating anyway with
        // an eight-bit DAC would make asking for it pointless, since the
        // values a program wants smooth gradients from are exactly the ones
        // that would be lost.
        var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Cpu.State.Halted = false;

        machine.Cpu.State.Ax = 0x4F08;
        machine.Cpu.State.Bx = 0x0800;
        machine.VideoServices.Handle(machine.Cpu);

        machine.Io.WriteByte(0x3C8, 0x01);
        machine.Io.WriteByte(0x3C9, 0xC8);
        machine.Io.WriteByte(0x3C9, 0x00);
        machine.Io.WriteByte(0x3C9, 0x00);

        Assert.Equal((byte)0xC8, machine.VideoRegisters.GetPaletteColor(1).R);
        machine.Dispose();
    }

    [Fact]
    public void AtSixBitsTheyAreStillDiscarded()
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);

        machine.Io.WriteByte(0x3C8, 0x01);
        machine.Io.WriteByte(0x3C9, 0xC8);
        machine.Io.WriteByte(0x3C9, 0x00);
        machine.Io.WriteByte(0x3C9, 0x00);

        // 0xC8 & 0x3F is 8; expanding it by repeating the top two bits gives
        // 0x20, since those two bits are zero here.
        Assert.Equal((byte)0x20, machine.VideoRegisters.GetPaletteColor(1).R);
    }
}

/// <summary>
/// The VBE mode set's port writes, done by the ROM with the processor so
/// that a monitor shadowing the ports sees them.
/// </summary>
public class VesaRomTests
{
    private static Machine Started()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Cpu.State.Halted = false;
        return machine;
    }

    /// <summary>
    /// Runs a real-mode program through the processor, so that the ROM's
    /// own code -- not just the native handler -- takes part.
    /// </summary>
    private static void Run(Machine machine, byte[] program)
    {
        uint code = 0x20100;
        for (int i = 0; i < program.Length; i++) machine.Memory.WriteByte(code + (uint)i, program[i]);
        machine.Memory.WriteByte(code + (uint)program.Length, 0xF4);   // HLT
        machine.Cpu.SetSegment(SegReg.Cs, 0x2000);
        machine.Cpu.State.Eip = 0x0100;
        machine.Cpu.SetSegment(SegReg.Ss, 0x3000);
        machine.Cpu.State.Esp = 0xFFFE;
        machine.Cpu.State.Halted = false;
        for (int i = 0; i < 20000 && !machine.Cpu.State.Halted; i++) machine.RunInstructions(1);
        Assert.True(machine.Cpu.State.Halted, "the program did not reach its HLT");
    }

    /// <summary>
    /// A VBE mode set programs the standard VGA registers through the
    /// ports, as a card's BIOS does, and not only the native mode state. A
    /// monitor that shadows the ports learns of the change only that way:
    /// Windows' virtual display device decides from its copy of graphics
    /// controller register 6 which pages the adapter decodes, and after
    /// BIOS mode 6 that copy said B8000h until the VBE mode set corrected
    /// it -- which it did not, and the S3 driver's engine registers at
    /// A8000h were mapped to nothing.
    /// </summary>
    [Fact]
    public void AVbeModeSetProgramsTheGraphicsControllerThroughThePorts()
    {
        using Machine machine = Started();

        Run(machine, [0xB8, 0x06, 0x00, 0xCD, 0x10]);                    // MOV AX,0006h / INT 10h
        Assert.Equal(0x0F, machine.VideoRegisters.Graphics(6));          // graphics, decoded at B8000h

        Run(machine, [0xB8, 0x02, 0x4F, 0xBB, 0x03, 0x81, 0xCD, 0x10]);  // MOV AX,4F02h / MOV BX,8103h / INT 10h
        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Equal((ushort)0x103, machine.VideoRegisters.VesaMode!.Value.Number);
        Assert.Equal(0x05, machine.VideoRegisters.Graphics(6));          // graphics, decoded at A0000h, 64 KB
        Assert.Equal(0x0E, machine.VideoRegisters.Sequencer(4));         // chain 4, one byte per pixel

        Run(machine, [0xB8, 0x02, 0x4F, 0xBB, 0x02, 0x01, 0xCD, 0x10]);  // mode 102h: planar
        Assert.Equal(0x05, machine.VideoRegisters.Graphics(6));
        Assert.Equal(0x06, machine.VideoRegisters.Sequencer(4));

        Run(machine, [0xB8, 0x02, 0x4F, 0xBB, 0x03, 0x00, 0xCD, 0x10]);  // back to text through VBE
        Assert.Null(machine.VideoRegisters.VesaMode);
        Assert.Equal(0x0E, machine.VideoRegisters.Graphics(6));
    }

    [Fact]
    public void TheVbeModeSetPreservesTheRegistersForTheNativeHandler()
    {
        using Machine machine = Started();

        // 4F03h afterwards must still see the mode the native handler set,
        // which it only does if AX and BX reached it intact.
        Run(machine, [0xB8, 0x02, 0x4F, 0xBB, 0x01, 0x41, 0xCD, 0x10,    // 4F02h, mode 4101h
                      0xB8, 0x03, 0x4F, 0xCD, 0x10]);                    // 4F03h
        Assert.Equal(0x004F, machine.Cpu.State.Ax);
        Assert.Equal(0x4101, machine.Cpu.State.Bx);
    }
}
