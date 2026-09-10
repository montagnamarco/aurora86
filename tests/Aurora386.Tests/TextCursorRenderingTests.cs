using Aurora386.Core;

namespace Aurora386.Tests;

// The text caret is composed by the CRTC, not stored in video RAM.
public class TextCursorRenderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrtcOnlyCursorChangeErasesTheOldCaret(bool hide)
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);
        machine.VideoServices.SetVideoMode(3);
        machine.VideoRegisters.SetCursorPosition(7, 3);
        machine.Video.Render(true);

        if (hide)
        {
            machine.Io.WriteByte(0x3D4, 0x0A);
            machine.Io.WriteByte(0x3D5, 0x20);
        }
        else machine.VideoRegisters.SetCursorPosition(0, 19);

        Assert.True(machine.Video.Render());
        uint[] incremental = machine.Video.Framebuffer.ToArray();
        machine.Video.Render(true);
        Assert.Equal(machine.Video.Framebuffer.ToArray(), incremental);
    }
}
