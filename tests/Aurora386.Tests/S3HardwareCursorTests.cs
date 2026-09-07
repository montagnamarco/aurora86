using Aurora386.Core;

namespace Aurora386.Tests;

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

    private static Machine WithCursor()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Cpu.State.Halted = false;

        machine.Cpu.State.Ax = 0x4F02;
        machine.Cpu.State.Bx = 0x101;               // 640x480 at 256 colours
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

        Assert.Equal(PatternAddress, machine.VideoRegisters.CursorPatternAddress);
    }
}
