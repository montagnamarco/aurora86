using Aurora86.Core;
using Aurora86.Core.Configuration;
using Aurora86.Core.Devices.Video;

namespace Aurora86.Tests;

/// <summary>
/// Exercises the shared VGA ownership of the 1995 Trio64V+ and 1996 ViRGE
/// cards through machine ports, physical memory and display output, retaining
/// the separate accelerator protocols and established address decoders.
/// </summary>
public class VideoCardCompositionTests
{
    [Theory]
    [InlineData(VmVideoModel.S3Trio64VPlus, typeof(S3Trio64VPlusCard), typeof(S3VgaRenderer))]
    [InlineData(VmVideoModel.S3Virge325, typeof(S3VirgeCard), typeof(S3VirgeRenderer))]
    public void SelectedCardOwnsTheRegistersMemoryAndDisplay(
        VmVideoModel model, Type cardType, Type rendererType)
    {
        using var machine = new Machine(videoModel: model);

        Assert.IsType(cardType, machine.VideoCard);
        Assert.IsType(rendererType, machine.VideoCard.Renderer);
        Assert.Same(machine.VideoCard.Registers, machine.VideoRegisters);
        Assert.Same(machine.VideoRegisters, machine.VideoMemory.Registers);
        Assert.Same(machine.VideoCard.VideoMemory, machine.VideoMemory);
        Assert.Same(machine.VideoCard.Display, machine.Display);
        Assert.Same(machine.VideoCard.Renderer, machine.Video);
    }

    [Theory]
    [InlineData(VmVideoModel.S3Trio64VPlus)]
    [InlineData(VmVideoModel.S3Virge325)]
    public void CardDisplayReadsMode13PixelsWrittenThroughA0000(VmVideoModel model)
    {
        using var machine = new Machine(videoModel: model);
        machine.RunInstructions(200);
        machine.VideoServices.SetVideoMode(0x13);
        machine.Memory.WriteByte(0xA0000, 42);
        machine.Io.WriteByte(0x3C8, 42);
        machine.Io.WriteByte(0x3C9, 0x3F);
        machine.Io.WriteByte(0x3C9, 0);
        machine.Io.WriteByte(0x3C9, 0);

        Assert.True(machine.VideoCard.Display.Render(force: true));
        Assert.Equal(320, machine.VideoCard.Display.Width);
        Assert.Equal(200, machine.VideoCard.Display.Height);
        Assert.Equal(42, machine.VideoCard.VideoMemory.ReadLinear(0));
        Assert.Equal(0xFF0000FFu, machine.VideoCard.Display.Framebuffer[0]);
    }

    [Fact]
    public void TrioCurrentX86E8UsesTheSameRegisterAtBothMmioAliases()
    {
        using var machine = new Machine();
        var card = Assert.IsType<S3Trio64VPlusCard>(machine.VideoCard);
        const ushort currentXRegister = 0x86E8;
        machine.Io.WriteWord(currentXRegister, 123);

        Assert.Equal(123, machine.Memory.ReadWord(card.MmioBase + currentXRegister));
        Assert.Equal(123, machine.Memory.ReadWord(
            card.FrameBufferBase + S3Trio64VPlusCard.EngineApertureOffset + currentXRegister));
        Assert.Same(card.Accelerator, machine.Accelerator);
    }

    [Fact]
    public void ViRgeCardDisposalThroughVgaBaseReleasesPciMmioDecode()
    {
        using var machine = new Machine(videoModel: VmVideoModel.S3Virge325);
        machine.RunInstructions(200);
        const uint subsystemStatusRegister = 0x8504;
        uint statusAddress = machine.MmioBase + subsystemStatusRegister;
        Assert.Equal(0x3000u, machine.Memory.ReadDword(statusAddress));

        machine.VideoCard.Dispose();

        Assert.Equal(uint.MaxValue, machine.Memory.ReadDword(statusAddress));
        Assert.Equal(0xFF, machine.Io.ReadByte(0x3CC));
    }
}
