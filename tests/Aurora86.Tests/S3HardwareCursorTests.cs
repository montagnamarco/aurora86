using Aurora86.Core;

namespace Aurora86.Tests;

/// <summary>
/// The S3's hardware pointer: a 64x64 pattern the card draws over the picture
/// as the beam passes.
///
/// It costs nothing to move -- writing two registers is the whole operation.
/// Doing the same in software means saving what is under the pointer, drawing
/// it, and putting the background back before the next move: three times the
/// work, and it has to happen between frames or the pointer flickers. At
/// 1024x768 that was the difference between a pointer that kept up with the
/// hand and one that lagged behind it.
///
/// The pattern is two bits per pixel, and the pair is an instruction rather
/// than a colour. AND clear paints -- background or foreground according to
/// XOR -- and AND set lets the screen through, either unchanged or inverted.
/// That last combination is what makes a pointer visible over a background
/// whose colour it does not know.
/// </summary>
public class S3HardwareCursorTests
{
    /// <summary>Where the test puts the 64x64 pattern: one megabyte in.</summary>
    private const uint PatternBlock = 1024;
    private const uint PatternAddress = PatternBlock * 1024;

    private static Machine WithCursor(ushort vesaMode = 0x101)
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Cpu.State.Halted = false;

        machine.Cpu.State.Ax = 0x4F02;
        machine.Cpu.State.Bx = vesaMode;
        machine.VideoServices.Handle(machine.Cpu);

        Crtc(machine, 0x38, 0x48);                  // unlock
        Crtc(machine, 0x39, 0xA5);

        // The address is a sixteen-bit block number split across two
        // registers, high byte first.
        Crtc(machine, 0x4C, (byte)(PatternBlock >> 8));
        Crtc(machine, 0x4D, unchecked((byte)PatternBlock));

        return machine;
    }

    private static void Crtc(Machine machine, int index, byte value)
    {
        machine.Io.WriteByte(0x3D4, (byte)index);
        machine.Io.WriteByte(0x3D5, value);
    }

    private static byte ReadCrtc(Machine machine, int index)
    {
        machine.Io.WriteByte(0x3D4, (byte)index);

        return machine.Io.ReadByte(0x3D5);
    }

    /// <summary>Programs one cursor stack in the documented blue, green, red byte order.</summary>
    private static void CursorColour(Machine machine, int index, byte blue, byte green, byte red)
    {
        ReadCrtc(machine, 0x45);
        Crtc(machine, index, blue);
        Crtc(machine, index, green);
        Crtc(machine, index, red);
    }

    /// <summary>Fills the whole 64x64 pattern with one AND/XOR combination.</summary>
    private static void FillPattern(Machine machine, bool and, bool xor)
    {
        for (int row = 0; row < 64; row++)
            for (int group = 0; group < 4; group++)
            {
                uint at = PatternAddress + (uint)(row * 16) + (uint)group * 4;
                machine.VideoMemory.WriteLinear(at + 0, and ? (byte)0xFF : (byte)0x00);
                machine.VideoMemory.WriteLinear(at + 1, and ? (byte)0xFF : (byte)0x00);
                machine.VideoMemory.WriteLinear(at + 2, xor ? (byte)0xFF : (byte)0x00);
                machine.VideoMemory.WriteLinear(at + 3, xor ? (byte)0xFF : (byte)0x00);
            }
    }

    private static void Place(Machine machine, int x, int y)
    {
        Crtc(machine, 0x46, (byte)(x >> 8));
        Crtc(machine, 0x47, (byte)x);
        Crtc(machine, 0x48, (byte)(y >> 8));
        Crtc(machine, 0x49, (byte)y);
    }

    private static uint RenderForegroundCursor(Machine machine)
    {
        FillPattern(machine, and: false, xor: true);
        Place(machine, 100, 50);
        Crtc(machine, 0x45, 0x01);
        machine.Video.Render(force: true);

        int cursorPixel = 60 * machine.Video.Width + 110;

        return machine.Video.Framebuffer[cursorPixel];
    }

    [Fact]
    public void ReadingCursorModeResetsTheSharedColourStackPointer()
    {
        using Machine machine = WithCursor();

        Crtc(machine, 0x4A, 0x11);
        Crtc(machine, 0x4B, 0x22);
        ReadCrtc(machine, 0x45);
        Crtc(machine, 0x4B, 0x33);

        Assert.Equal(0x000011u, machine.S3Registers.CursorColours.Foreground);
        Assert.Equal(0x002233u, machine.S3Registers.CursorColours.Background);
    }

    [Fact]
    public void CursorColourStacksStoreDistinctBlueGreenRedValues()
    {
        using Machine machine = WithCursor();

        CursorColour(machine, 0x4A, 0x11, 0x22, 0x33);
        CursorColour(machine, 0x4B, 0x44, 0x55, 0x66);

        Assert.Equal(0x332211u, machine.S3Registers.CursorColours.Foreground);
        Assert.Equal(0x665544u, machine.S3Registers.CursorColours.Background);
    }

    [Fact]
    public void ATrueColourWhiteCursorDoesNotUsePaletteEntry255()
    {
        using Machine machine = WithCursor(0x114);
        machine.VideoRegisters.SetPaletteEntry(0xFF, 0, 0, 0);
        CursorColour(machine, 0x4A, 0xFF, 0xFF, 0xFF);

        uint cursor = RenderForegroundCursor(machine);

        Assert.Equal(0xFFFFFFFFu, cursor);
    }

    [Theory]
    [InlineData((ushort)0x113, 0x7C00, 0xFF0000FFu)]
    [InlineData((ushort)0x113, 0x03E0, 0xFF00FF00u)]
    [InlineData((ushort)0x113, 0x001F, 0xFFFF0000u)]
    [InlineData((ushort)0x114, 0xF800, 0xFF0000FFu)]
    [InlineData((ushort)0x114, 0x07E0, 0xFF00FF00u)]
    [InlineData((ushort)0x114, 0x001F, 0xFFFF0000u)]
    public void FifteenAndSixteenBitCursorColoursUseTheScreenPixelFormat(
        ushort vesaMode,
        ushort packedColour,
        uint expected)
    {
        using Machine machine = WithCursor(vesaMode);
        CursorColour(machine,
                     0x4A,
                     (byte)packedColour,
                     (byte)(packedColour >> 8),
                     0);

        uint cursor = RenderForegroundCursor(machine);

        Assert.Equal(expected, cursor);
    }

    [Fact]
    public void AThirtyTwoBitCursorUsesBlueGreenRedStackOrder()
    {
        using Machine machine = WithCursor(0x140);
        CursorColour(machine, 0x4A, 0x11, 0x22, 0x33);

        uint cursor = RenderForegroundCursor(machine);

        Assert.Equal(0xFF112233u, cursor);
    }

    [Fact]
    public void AnEightBitCursorStillSelectsTheDacWithItsLowByte()
    {
        using Machine machine = WithCursor();
        machine.VideoRegisters.DacBitsPerColour = 8;
        machine.VideoRegisters.SetPaletteEntry(0x05, 0x12, 0x34, 0x56);
        CursorColour(machine, 0x4A, 0x05, 0xFF, 0xFF);

        uint cursor = RenderForegroundCursor(machine);

        Assert.Equal(0xFF563412u, cursor);
    }

    [Fact]
    public void APlanarFourBitCursorStillSelectsTheDacWithItsLowByte()
    {
        using Machine machine = WithCursor(0x102);
        machine.VideoRegisters.DacBitsPerColour = 8;
        machine.VideoRegisters.SetPaletteEntry(0x0C, 0x12, 0x34, 0x56);
        CursorColour(machine, 0x4A, 0x0C, 0xFF, 0xFF);

        uint cursor = RenderForegroundCursor(machine);

        Assert.Equal(0xFF563412u, cursor);
    }

    [Fact]
    public void RegisterSnapshotsPreserveAColourStackPartWayThroughAWrite()
    {
        using Machine machine = WithCursor();

        Crtc(machine, 0x4A, 0xAA);
        byte[] saved = machine.VideoRegisters.CaptureRegisters();
        Crtc(machine, 0x4A, 0xBB);
        machine.VideoRegisters.RestoreRegisters(saved);
        Crtc(machine, 0x4A, 0xCC);

        Assert.Equal(0x00CCAAu, machine.S3Registers.CursorColours.Foreground);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StationaryXorPointerSurvivesAnUnrelatedScreenUpdate(bool textMode)
    {
        using Machine machine = WithCursor();
        if (textMode)
        {
            machine.Cpu.State.Ax = 0x0003;
            machine.VideoServices.Handle(machine.Cpu);
        }
        FillPattern(machine, and: true, xor: true);
        Place(machine, 100, 50);
        Crtc(machine, 0x45, 0x01);
        machine.Video.Render(force: true);
        uint expected = machine.Video.Framebuffer[60 * machine.Video.Width + 110];

        // Dirty a row away from the pointer, in scanlines or text cells.
        machine.VideoMemory.MarkRowsDirty(textMode ? 20 : 300, 1);
        machine.Video.Render();
        Assert.Equal(expected, machine.Video.Framebuffer[60 * machine.Video.Width + 110]);
        uint[] incremental = machine.Video.Framebuffer.ToArray();
        machine.Video.Render(force: true);
        Assert.Equal(incremental, machine.Video.Framebuffer.ToArray());
    }

    [Fact]
    public void ThePointerCoversExactlySixtyFourPixels()
    {
        using Machine machine = WithCursor();
        FillPattern(machine, and: false, xor: true);      // solid foreground

        Crtc(machine, 0x4A, 0x0F);                       // foreground white
        Place(machine, 100, 50);
        Crtc(machine, 0x45, 0x01);                       // on

        machine.Video.Render(force: true);
        var frame = machine.Video.Framebuffer;

        Assert.NotEqual(0u, frame[60 * 640 + 110] & 0xFFFFFF);   // inside
        Assert.Equal(0u, frame[60 * 640 + 50] & 0xFFFFFF);       // left of it
        Assert.NotEqual(0u, frame[60 * 640 + 163] & 0xFFFFFF);   // last column
        Assert.Equal(0u, frame[60 * 640 + 164] & 0xFFFFFF);      // one past
    }

    [Fact]
    public void MovingItRedrawsWithoutAnythingBeingWrittenToMemory()
    {
        // The whole point of a hardware pointer, and the thing that makes it
        // awkward for an emulator that redraws only what changed: nothing
        // changes in video memory when the pointer moves.
        using Machine machine = WithCursor();
        FillPattern(machine, and: false, xor: true);
        Crtc(machine, 0x4A, 0x0F);
        Place(machine, 100, 50);
        Crtc(machine, 0x45, 0x01);
        machine.Video.Render(force: true);

        Place(machine, 200, 50);
        bool redrawn = machine.Video.Render();

        Assert.True(redrawn);

        var frame = machine.Video.Framebuffer;
        Assert.Equal(0u, frame[60 * 640 + 110] & 0xFFFFFF);      // no trail left behind
        Assert.NotEqual(0u, frame[60 * 640 + 210] & 0xFFFFFF);   // and it is at the new place
    }

    [Fact]
    public void AndSetWithXorClearLetsTheScreenThrough()
    {
        using Machine machine = WithCursor();

        // A red background, so that "unchanged" is distinguishable.
        for (uint i = 0; i < 640 * 480; i++)
            machine.VideoMemory.WriteLinear(i, 0x0C);

        FillPattern(machine, and: true, xor: false);
        Place(machine, 100, 50);
        Crtc(machine, 0x45, 0x01);

        machine.Video.Render(force: true);
        var frame = machine.Video.Framebuffer;

        // Inside and outside look the same: the pointer is entirely
        // transparent here.
        Assert.Equal(frame[60 * 640 + 50], frame[60 * 640 + 110]);
    }

    [Fact]
    public void AndSetWithXorSetInvertsWhatIsUnderneath()
    {
        // The combination that keeps a pointer visible over a background
        // whose colour it cannot know.
        using Machine machine = WithCursor();

        for (uint i = 0; i < 640 * 480; i++)
            machine.VideoMemory.WriteLinear(i, 0x0C);

        FillPattern(machine, and: true, xor: true);
        Place(machine, 100, 50);
        Crtc(machine, 0x45, 0x01);

        machine.Video.Render(force: true);
        var frame = machine.Video.Framebuffer;

        Assert.NotEqual(frame[60 * 640 + 50] & 0xFFFFFF,
                        frame[60 * 640 + 110] & 0xFFFFFF);
    }

    [Fact]
    public void TheBackgroundColourIsDrawnWhereBothBitsAreClear()
    {
        using Machine machine = WithCursor();
        FillPattern(machine, and: false, xor: false);

        Crtc(machine, 0x4B, 0x0C);                       // background red
        Place(machine, 100, 50);
        Crtc(machine, 0x45, 0x01);

        machine.Video.Render(force: true);
        var frame = machine.Video.Framebuffer;

        Assert.NotEqual(0u, frame[60 * 640 + 110] & 0xFFFFFF);
    }

    [Fact]
    public void TurningItOffLeavesThePictureAlone()
    {
        using Machine machine = WithCursor();
        FillPattern(machine, and: false, xor: true);
        Crtc(machine, 0x4A, 0x0F);
        Place(machine, 100, 50);
        Crtc(machine, 0x45, 0x01);
        machine.Video.Render(force: true);

        Crtc(machine, 0x45, 0x00);
        machine.Video.Render(force: true);

        Assert.Equal(0u, machine.Video.Framebuffer[60 * 640 + 110] & 0xFFFFFF);
    }

    [Fact]
    public void ThePatternAddressCountsInKilobytes()
    {
        // A 64x64 pattern at two bits a pixel is exactly 1024 bytes, which is
        // why the register counts in those units rather than in bytes: it
        // gets a megabyte of reach out of sixteen bits.
        using Machine machine = WithCursor();

        Assert.Equal(PatternAddress, machine.S3Registers.CursorPatternAddress);
    }
}
