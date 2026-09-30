using Aurora86.Core;

namespace Aurora86.Tests;

/// <summary>
/// Tests the frontend-facing video output contract over guest video memory.
/// </summary>
public class VideoOutputTests
{
    [Fact]
    public void DisplayRendersGuestVideoMemory()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);
        machine.VideoServices.SetVideoMode(0x13);

        machine.Memory.WriteByte(0xA0000, 42);
        machine.Io.WriteByte(0x3C8, 42);
        machine.Io.WriteByte(0x3C9, 0x3F);
        machine.Io.WriteByte(0x3C9, 0x00);
        machine.Io.WriteByte(0x3C9, 0x00);

        Assert.True(machine.Display.Render(force: true));
        Assert.Equal(320, machine.Display.Width);
        Assert.Equal(200, machine.Display.Height);
        Assert.Equal(0xFF0000FFu, machine.Display.Framebuffer[0]);
    }

    [Fact]
    public void DisplayTracksModeGeometry()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);
        machine.VideoServices.SetVideoMode(0x13);

        Assert.True(machine.Display.Render(force: true));
        Assert.Equal(320, machine.Display.Width);
        Assert.Equal(200, machine.Display.Height);

        machine.VideoServices.SetVideoMode(0x03);
        Assert.True(machine.Display.Render(force: true));
        Assert.Equal(720, machine.Display.Width);
        Assert.Equal(400, machine.Display.Height);
    }

    [Fact]
    public void DisplayUsesTheExistingRenderer()
    {
        using var machine = new Machine();

        Assert.Same(machine.Video, machine.Display);

        machine.Display.CursorBlinkOn = false;
        Assert.False(machine.Video.CursorBlinkOn);

        machine.Display.CursorBlinkOn = true;
        Assert.True(machine.Video.CursorBlinkOn);
    }
}
