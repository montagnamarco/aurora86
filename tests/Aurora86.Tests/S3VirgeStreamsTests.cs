using System.Buffers.Binary;
using Aurora86.Core.Devices.Video;

namespace Aurora86.Tests;

/// <summary>
/// Exercises the original 1996 ViRGE primary Streams latches and their
/// unscaled KRGB1555 scanout, including immediate page selection and the
/// deterministic snapshot policy for registers whose silicon reset is undefined.
/// </summary>
public sealed class S3VirgeStreamsTests
{
    private const uint FirstPage = 0x10000;
    private const uint SecondPage = 0x20000;
    private const uint Red = 0xFF0000FF;
    private const uint Green = 0xFF00FF00;
    private const uint Blue = 0xFFFF0000;
    private const uint Black = 0xFF000000;

    private static readonly uint[] RegisterOffsets =
    [
        S3VirgeStreams.PrimaryControlRegister,
        S3VirgeStreams.PrimaryBufferAddress0Register,
        S3VirgeStreams.PrimaryBufferAddress1Register,
        S3VirgeStreams.PrimaryStrideRegister,
        S3VirgeStreams.BufferSelectRegister,
        S3VirgeStreams.PrimaryWindowStartRegister,
        S3VirgeStreams.PrimaryWindowSizeRegister
    ];

    [Fact]
    public void MM8180MM81C0MM81C4MM81C8MM81CCMM81F0AndMM81F4ApplyTheirDocumentedMasks()
    {
        var streams = new S3VirgeStreams(new S3VgaRegisters(S3VideoIdentity.Virge325));

        foreach (uint register in RegisterOffsets)
        {
            Assert.True(S3VirgeStreams.IsRegister(register));
            streams.WriteDword(register, uint.MaxValue);
        }

        Assert.False(S3VirgeStreams.IsRegister(0x81C1));
        Assert.False(S3VirgeStreams.IsRegister(0x81D0));
        Assert.Equal(0x77000000u, streams.ReadDword(S3VirgeStreams.PrimaryControlRegister));
        Assert.Equal(0x003FFFF8u, streams.ReadDword(S3VirgeStreams.PrimaryBufferAddress0Register));
        Assert.Equal(0x003FFFF8u, streams.ReadDword(S3VirgeStreams.PrimaryBufferAddress1Register));
        Assert.Equal(0x00000FFFu, streams.ReadDword(S3VirgeStreams.PrimaryStrideRegister));
        Assert.Equal(1u, streams.ReadDword(S3VirgeStreams.BufferSelectRegister));
        Assert.Equal(0x07FF07FFu, streams.ReadDword(S3VirgeStreams.PrimaryWindowStartRegister));
        Assert.Equal(0x07FF07FFu, streams.ReadDword(S3VirgeStreams.PrimaryWindowSizeRegister));
        Assert.Equal(uint.MaxValue, streams.ReadDword(0x81D0));

        streams.Reset();

        foreach (uint register in RegisterOffsets)
        {
            Assert.Equal(0u, streams.ReadDword(register));
        }
    }

    [Fact]
    public void MM81CCBitZeroSelectsEitherPrimaryAddressAndOddStrideIsPreserved()
    {
        var streams = new S3VirgeStreams(new S3VgaRegisters(S3VideoIdentity.Virge325));
        streams.WriteDword(S3VirgeStreams.PrimaryBufferAddress0Register, FirstPage + 7);
        streams.WriteDword(S3VirgeStreams.PrimaryBufferAddress1Register, SecondPage + 7);
        streams.WriteDword(S3VirgeStreams.PrimaryStrideRegister, 1297);

        Assert.Equal(FirstPage, streams.PrimaryBufferAddress);
        Assert.Equal(1297, streams.PrimaryStride);

        streams.WriteDword(S3VirgeStreams.BufferSelectRegister, uint.MaxValue);

        Assert.Equal(SecondPage, streams.PrimaryBufferAddress);
    }

    [Fact]
    public void MM81C0MM81C4AndMM81CCPageWritesRedrawAndCR67Bits3To2ReturnToVbe()
    {
        VgaRenderer renderer = CreateRenderer(out S3VgaRegisters registers, out VgaMemory memory,
            out S3VirgeStreams streams);
        WritePixel(memory, 0, 0x03E0);
        WritePixel(memory, FirstPage, 0x001F);
        WritePixel(memory, SecondPage, 0x7C00);
        ProgramPrimary(streams, FirstPage, stride: 1280, width: 640, height: 480);
        SetStreamsMode(registers, 0x3C);

        Assert.True(renderer.Render(true));
        Assert.Equal(Blue, renderer.Framebuffer[0]);

        streams.WriteDword(S3VirgeStreams.PrimaryBufferAddress1Register, SecondPage);
        streams.WriteDword(S3VirgeStreams.BufferSelectRegister, 1);
        Assert.True(renderer.Render());
        Assert.Equal(Red, renderer.Framebuffer[0]);

        SetStreamsMode(registers, 0x30);
        Assert.True(renderer.Render());
        Assert.Equal(Green, renderer.Framebuffer[0]);
    }

    [Fact]
    public void CR67Bits3To2SecondaryOverlayModeRetainsTheVgaBackground()
    {
        VgaRenderer renderer = CreateRenderer(out S3VgaRegisters registers, out VgaMemory memory,
            out S3VirgeStreams streams);
        WritePixel(memory, 0, 0x03E0);
        WritePixel(memory, FirstPage, 0x7C00);
        ProgramPrimary(streams, FirstPage, stride: 1280, width: 640, height: 480);
        SetStreamsMode(registers, 0x34);

        renderer.Render(true);

        Assert.Equal(Green, renderer.Framebuffer[0]);
    }

    [Theory]
    [InlineData(0x05000000)] // RGB565 is outside the measured primary subset.
    [InlineData(0x13000000)] // KRGB1555 with 2X replication is not pass-through.
    public void MM8180UnsupportedFullStreamsFormatsRenderBlackInsteadOfTheVbePage(uint primaryControl)
    {
        VgaRenderer renderer = CreateRenderer(out S3VgaRegisters registers, out VgaMemory memory,
            out S3VirgeStreams streams);
        WritePixel(memory, 0, 0x03E0);
        WritePixel(memory, FirstPage, 0x7C00);
        ProgramPrimary(streams, FirstPage, stride: 1280, width: 640, height: 480);
        streams.WriteDword(S3VirgeStreams.PrimaryControlRegister, primaryControl);
        SetStreamsMode(registers, 0x3C);

        renderer.Render(true);

        Assert.Equal(Black, renderer.Framebuffer[0]);
    }

    [Fact]
    public void MM81F0AndMM81F4OneBasedWindowPositionsTheSourceInsideTheScreen()
    {
        VgaRenderer renderer = CreateRenderer(out S3VgaRegisters registers, out VgaMemory memory,
            out S3VirgeStreams streams);
        WritePixel(memory, FirstPage, 0x7C00);
        WritePixel(memory, FirstPage + 3, 0x03E0);
        ProgramPrimary(streams, FirstPage, stride: 3, width: 1, height: 2,
            encodedOrigin: 0x00020002);
        SetStreamsMode(registers, 0x3C);

        renderer.Render(true);

        Assert.Equal(Black, renderer.Framebuffer[0]);
        Assert.Equal(Red, renderer.Framebuffer[1 * 640 + 1]);
        Assert.Equal(Green, renderer.Framebuffer[2 * 640 + 1]);
        Assert.Equal(Black, renderer.Framebuffer[1 * 640 + 2]);
    }

    [Fact]
    public void MM81F0AndMM81F4ClipNegativeOriginAndTheRightEdgeWithoutMovingTheSource()
    {
        VgaRenderer renderer = CreateRenderer(out S3VgaRegisters registers, out VgaMemory memory,
            out S3VirgeStreams streams);
        WritePixel(memory, FirstPage, 0x7C00);
        WritePixel(memory, FirstPage + 2, 0x03E0);
        WritePixel(memory, FirstPage + 4, 0x001F);
        WritePixel(memory, FirstPage + 6, 0x7FFF);
        ProgramPrimary(streams, FirstPage, stride: 4, width: 2, height: 2,
            encodedOrigin: 0);
        SetStreamsMode(registers, 0x3C);

        renderer.Render(true);

        Assert.Equal(0xFFFFFFFFu, renderer.Framebuffer[0]);
        Assert.Equal(Black, renderer.Framebuffer[1]);
        Assert.Equal(Black, renderer.Framebuffer[640]);

        ProgramPrimary(streams, FirstPage, stride: 4, width: 2, height: 1,
            encodedOrigin: 0x02800001);
        Assert.True(renderer.Render());
        Assert.Equal(Red, renderer.Framebuffer[639]);
        Assert.Equal(Black, renderer.Framebuffer[638]);
    }

    [Fact]
    public void MM81C0OffscreenPageVramWriteRedrawsVisibleStreamsRowZero()
    {
        VgaRenderer renderer = CreateRenderer(out S3VgaRegisters registers, out VgaMemory memory,
            out S3VirgeStreams streams);
        WritePixel(memory, FirstPage, 0x001F);
        ProgramPrimary(streams, FirstPage, stride: 1280, width: 640, height: 480);
        SetStreamsMode(registers, 0x3C);
        renderer.Render(true);
        Assert.Equal(Blue, renderer.Framebuffer[0]);

        WritePixel(memory, FirstPage, 0x7C00);

        Assert.True(renderer.Render(false));
        Assert.Equal(Red, renderer.Framebuffer[0]);
    }

    [Fact]
    public void MM81C8ZeroStrideRepeatsTheSameSourceRow()
    {
        VgaRenderer renderer = CreateRenderer(out S3VgaRegisters registers, out VgaMemory memory,
            out S3VirgeStreams streams);
        WritePixel(memory, FirstPage, 0x7C00);
        ProgramPrimary(streams, FirstPage, stride: 0, width: 1, height: 2);
        SetStreamsMode(registers, 0x3C);

        renderer.Render(true);

        Assert.Equal(Red, renderer.Framebuffer[0]);
        Assert.Equal(Red, renderer.Framebuffer[640]);
    }

    [Fact]
    public void MM81C0BaseAndMM81C8StrideOutsideTwoMegabytesRenderBlack()
    {
        VgaRenderer renderer = CreateRenderer(out S3VgaRegisters registers, out VgaMemory memory,
            out S3VirgeStreams streams);
        const uint lastAlignedBlock = VgaMemory.TotalSize - 8;
        WritePixel(memory, lastAlignedBlock, 0x001F);
        ProgramPrimary(streams, lastAlignedBlock, stride: 0xFFF, width: 1, height: 2);
        SetStreamsMode(registers, 0x3C);

        renderer.Render(true);

        Assert.Equal(Blue, renderer.Framebuffer[0]);
        Assert.Equal(Black, renderer.Framebuffer[640]);
    }

    [Fact]
    public void VST1SnapshotRoundTripRestoresPrimaryLatchesWithoutChangingVideoMemory()
    {
        var registers = new S3VgaRegisters(S3VideoIdentity.Virge325);
        var memory = new VgaMemory(registers);
        var streams = new S3VirgeStreams(registers);
        ProgramPrimary(streams, FirstPage, stride: 1297, width: 17, height: 9,
            encodedOrigin: 0x00030004);
        streams.WriteDword(S3VirgeStreams.PrimaryBufferAddress1Register, SecondPage);
        streams.WriteDword(S3VirgeStreams.BufferSelectRegister, 1);
        WritePixel(memory, FirstPage, 0x7C00);
        byte[] pixels = memory.CaptureMemory();
        byte[] snapshot = streams.Capture();

        streams.Reset();
        streams.Restore(snapshot);

        Assert.Equal(36, snapshot.Length);
        Assert.Equal(0x31545356u, BinaryPrimitives.ReadUInt32LittleEndian(snapshot));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(snapshot.AsSpan(4)));
        Assert.Equal(7, BinaryPrimitives.ReadUInt16LittleEndian(snapshot.AsSpan(6)));
        Assert.Equal(SecondPage, streams.PrimaryBufferAddress);
        Assert.Equal(1297, streams.PrimaryStride);
        Assert.Equal((2, 3), streams.PrimaryWindowOrigin);
        Assert.Equal((17, 9), streams.PrimaryWindowSize);
        Assert.Equal(pixels, memory.CaptureMemory());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void VST1MalformedSnapshotLeavesPrimaryLatchesUnchanged(int corruptOffset)
    {
        var streams = new S3VirgeStreams(new S3VgaRegisters(S3VideoIdentity.Virge325));
        ProgramPrimary(streams, FirstPage, stride: 1280, width: 640, height: 480);
        byte[] invalid = streams.Capture();
        if (corruptOffset == 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(invalid, 0);
        }
        else if (corruptOffset == 4)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(invalid.AsSpan(4), 2);
        }
        else if (corruptOffset == 6)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(invalid.AsSpan(6), 8);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(8), uint.MaxValue);
        }

        Assert.Throws<ArgumentException>(() => streams.Restore(invalid));

        Assert.Equal(FirstPage, streams.PrimaryBufferAddress);
        Assert.Equal(1280, streams.PrimaryStride);
    }

    [Fact]
    public void CR67DoesNotChangeTheRendererWithoutVirgeStreams()
    {
        var registers = new S3VgaRegisters();
        registers.SetVesaMode(VesaModes.Get(0x110, VgaMemory.TotalSize), true);
        var memory = new VgaMemory(registers);
        var renderer = new S3VgaRenderer(memory, registers);
        WritePixel(memory, 0, 0x03E0);
        renderer.Render(true);
        Assert.Equal(Green, renderer.Framebuffer[0]);

        SetStreamsMode(registers, 0x3C);

        Assert.False(renderer.Render());
        Assert.Equal(Green, renderer.Framebuffer[0]);
    }

    private static VgaRenderer CreateRenderer(
        out S3VgaRegisters registers,
        out VgaMemory memory,
        out S3VirgeStreams streams)
    {
        registers = new S3VgaRegisters(S3VideoIdentity.Virge325);
        registers.SetVesaMode(VesaModes.Get(0x110, VgaMemory.TotalSize), true);
        memory = new VgaMemory(registers);
        streams = new S3VirgeStreams(registers);
        return new S3VirgeRenderer(memory, registers, streams);
    }

    private static void ProgramPrimary(
        S3VirgeStreams streams,
        uint baseAddress,
        uint stride,
        int width,
        int height,
        uint encodedOrigin = 0x00010001)
    {
        streams.WriteDword(S3VirgeStreams.PrimaryControlRegister, 0x03000000);
        streams.WriteDword(S3VirgeStreams.PrimaryBufferAddress0Register, baseAddress);
        streams.WriteDword(S3VirgeStreams.PrimaryStrideRegister, stride);
        streams.WriteDword(S3VirgeStreams.BufferSelectRegister, 0);
        streams.WriteDword(S3VirgeStreams.PrimaryWindowStartRegister, encodedOrigin);
        streams.WriteDword(S3VirgeStreams.PrimaryWindowSizeRegister,
            ((uint)(width - 1) << 16) | (uint)height);
    }

    private static void SetStreamsMode(VgaRegisters registers, byte cr67)
    {
        registers.SetCrtc(0x38, 0x48);
        registers.SetCrtc(0x39, 0xA0);
        registers.SetCrtc(0x67, cr67);
    }

    private static void WritePixel(VgaMemory memory, uint address, ushort pixel)
    {
        memory.WriteLinear(address, (byte)pixel);
        memory.WriteLinear(address + 1, (byte)(pixel >> 8));
    }
}
