using Aurora86.Core.Devices.Video;

namespace Aurora86.Tests;

/// <summary>
/// The BitBLT commands the Windows 95 ViRGE driver (S3V.DRV 4.03.00.2030)
/// was measured issuing at 640x480x8 while repainting the desktop: 233
/// transparent mono text uploads (079802E2h), opaque ones (079800E2h),
/// mono-pattern PATCOPY (07E00920h) and PATINVERT (06B40920h) BitBLTs and one
/// 8-bit rectangle fill carrying the transparent bit (17E00322h). Each test
/// pins the command word, the register offsets and the pixel bytes written.
/// </summary>
public sealed class S3VirgeDesktopBitBltTests
{
    private const uint SourceBase = 0xA4D4;
    private const uint DestinationBase = 0xA4D8;
    private const uint ClipLeftRight = 0xA4DC;
    private const uint ClipTopBottom = 0xA4E0;
    private const uint Stride = 0xA4E4;
    private const uint MonoPattern0 = 0xA4E8;
    private const uint MonoPattern1 = 0xA4EC;
    private const uint PatternBackground = 0xA4F0;
    private const uint PatternForeground = 0xA4F4;
    private const uint SourceBackground = 0xA4F8;
    private const uint SourceForeground = 0xA4FC;
    private const uint Command = 0xA500;
    private const uint WidthHeight = 0xA504;
    private const uint SourceCoordinates = 0xA508;
    private const uint DestinationCoordinates = 0xA50C;

    private const uint TransparentMonoText8 = 0x079802E2;
    private const uint OpaqueMonoText8 = 0x079800E2;
    private const uint MonoPatternCopy8 = 0x07E00920;
    private const uint MonoPatternInvert8 = 0x06B40920;
    private const uint RectangleFillTransparent8 = 0x17E00322;
    private const uint TransparentColourUploadWordAligned8 = 0x079806A2;
    private const uint FirstDwordOffsetOne = 1u << 12;

    [Fact]
    public void TransparentMonoTextUploadPaintsOnlySetBitsFromByteAlignedRows()
    {
        VgaMemory memory = CreateMemory();
        var engine = new S3Virge2DEngine(memory);
        const uint baseAddress = 0x1000;
        const uint stride = 16;
        memory.GetSpan(baseAddress, 64).Fill(0xCC);
        ProgramDestination(engine, baseAddress, stride, x: 2, y: 1, width: 10, height: 2);
        ProgramFullClip(engine);
        engine.WriteDword(SourceForeground, 0x0F);
        engine.WriteDword(SourceBackground, 0x07);

        engine.WriteDword(Command, TransparentMonoText8);
        Assert.True(engine.IsBusy);

        // Ten pixels need two bytes per row; two rows fit one DWORD:
        // row 0 = A0h 40h (columns 0, 2 and 9), row 1 = FFh C0h (all ten).
        Assert.True(engine.WriteImageDword(0xC0FF40A0));

        Assert.False(engine.IsBusy);
        byte[] row0 = memory.GetSpan(baseAddress + 1 * stride, 14).ToArray();
        byte[] row1 = memory.GetSpan(baseAddress + 2 * stride, 14).ToArray();
        Assert.Equal(
            new byte[] { 0xCC, 0xCC, 0x0F, 0xCC, 0x0F, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0x0F, 0xCC, 0xCC },
            row0);
        Assert.Equal(
            new byte[] { 0xCC, 0xCC, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0xCC, 0xCC },
            row1);
    }

    [Fact]
    public void OpaqueMonoTextUploadPaintsBackgroundAndSkipsFirstDwordOffsetBytes()
    {
        VgaMemory memory = CreateMemory();
        var engine = new S3Virge2DEngine(memory);
        const uint baseAddress = 0x1000;
        const uint stride = 8;
        memory.GetSpan(baseAddress, 32).Fill(0xCC);
        ProgramDestination(engine, baseAddress, stride, x: 0, y: 0, width: 4, height: 2);
        ProgramFullClip(engine);
        engine.WriteDword(SourceForeground, 0x0F);
        engine.WriteDword(SourceBackground, 0x07);

        engine.WriteDword(Command, OpaqueMonoText8 | FirstDwordOffsetOne);

        // FDO=01b discards the first byte (FFh). Row 0 is A0h, row 1 is 50h,
        // and the fourth byte is padding once the two rows are complete.
        Assert.True(engine.WriteImageDword(0x0050A0FF));

        Assert.False(engine.IsBusy);
        Assert.Equal(new byte[] { 0x0F, 0x07, 0x0F, 0x07, 0xCC }, memory.GetSpan(baseAddress, 5).ToArray());
        Assert.Equal(new byte[] { 0x07, 0x0F, 0x07, 0x0F, 0xCC }, memory.GetSpan(baseAddress + stride, 5).ToArray());
    }

    [Fact]
    public void MonoPatternPatcopyBitBltAnchorsTheEightByEightPatternToScreenCoordinates()
    {
        VgaMemory memory = CreateMemory();
        var engine = new S3Virge2DEngine(memory);
        const uint baseAddress = 0x2000;
        const uint stride = 16;
        memory.GetSpan(baseAddress, 128).Fill(0xCC);
        ProgramDestination(engine, baseAddress, stride, x: 1, y: 0, width: 4, height: 5);

        // Lines 0-3 alternate AAh/55h; line 4 (first byte of MONO_PAT_1) is solid.
        engine.WriteDword(MonoPattern0, 0x55AA55AA);
        engine.WriteDword(MonoPattern1, 0x000000FF);
        engine.WriteDword(PatternForeground, 0x01);
        engine.WriteDword(PatternBackground, 0x02);

        engine.WriteDword(Command, MonoPatternCopy8);

        // Screen column 1 reads pattern bit 6 (the leftmost pixel is bit 7).
        Assert.Equal(new byte[] { 0xCC, 0x02, 0x01, 0x02, 0x01, 0xCC }, memory.GetSpan(baseAddress + 0 * stride, 6).ToArray());
        Assert.Equal(new byte[] { 0xCC, 0x01, 0x02, 0x01, 0x02, 0xCC }, memory.GetSpan(baseAddress + 1 * stride, 6).ToArray());
        Assert.Equal(new byte[] { 0xCC, 0x02, 0x01, 0x02, 0x01, 0xCC }, memory.GetSpan(baseAddress + 2 * stride, 6).ToArray());
        Assert.Equal(new byte[] { 0xCC, 0x01, 0x02, 0x01, 0x02, 0xCC }, memory.GetSpan(baseAddress + 3 * stride, 6).ToArray());
        Assert.Equal(new byte[] { 0xCC, 0x01, 0x01, 0x01, 0x01, 0xCC }, memory.GetSpan(baseAddress + 4 * stride, 6).ToArray());
        Assert.Equal(0xCC, memory.ReadLinear(baseAddress + 5 * stride + 1));
    }

    [Fact]
    public void SolidMonoPatternPatcopyFillsTheDesktopBackgroundColour()
    {
        VgaMemory memory = CreateMemory();
        var engine = new S3Virge2DEngine(memory);
        const uint baseAddress = 0;
        const uint stride = 640;
        ProgramDestination(engine, baseAddress, stride, x: 0, y: 0, width: 640, height: 2);
        engine.WriteDword(MonoPattern0, 0xFFFFFFFF);
        engine.WriteDword(MonoPattern1, 0xFFFFFFFF);
        engine.WriteDword(PatternForeground, 0x08);
        engine.WriteDword(PatternBackground, 0x00);

        engine.WriteDword(Command, MonoPatternCopy8);

        Assert.All(memory.GetSpan(0, 1280).ToArray(), pixel => Assert.Equal(0x08, pixel));
        Assert.Equal(0, memory.ReadLinear(1280));
    }

    [Fact]
    public void MonoPatternPatinvertBitBltXorsTheScreenWithThePatternColour()
    {
        VgaMemory memory = CreateMemory();
        var engine = new S3Virge2DEngine(memory);
        const uint baseAddress = 0x3000;
        const uint stride = 8;
        memory.GetSpan(baseAddress, 32).Fill(0x0F);
        ProgramDestination(engine, baseAddress, stride, x: 1, y: 1, width: 2, height: 1);
        engine.WriteDword(MonoPattern0, 0xFFFFFFFF);
        engine.WriteDword(MonoPattern1, 0xFFFFFFFF);
        engine.WriteDword(PatternForeground, 0xF0);

        engine.WriteDword(Command, MonoPatternInvert8);

        Assert.Equal(new byte[] { 0x0F, 0xFF, 0xFF, 0x0F }, memory.GetSpan(baseAddress + stride, 4).ToArray());

        // A second PATINVERT restores the original pixels, as an XOR must.
        engine.WriteDword(Command, MonoPatternInvert8);
        Assert.Equal(new byte[] { 0x0F, 0x0F, 0x0F, 0x0F }, memory.GetSpan(baseAddress + stride, 4).ToArray());
    }

    [Fact]
    public void EightBitRectangleFillUsesTheLowForegroundByteAndIgnoresTransparent()
    {
        VgaMemory memory = CreateMemory();
        var engine = new S3Virge2DEngine(memory);
        const uint baseAddress = 0x4000;
        const uint stride = 16;
        memory.GetSpan(baseAddress, 64).Fill(0xCC);
        ProgramDestination(engine, baseAddress, stride, x: 0, y: 0, width: 4, height: 2);
        engine.WriteDword(ClipLeftRight, Pack(1, 2));
        engine.WriteDword(ClipTopBottom, Pack(0, 0));
        engine.WriteDword(PatternForeground, 0x1234);

        engine.WriteDword(Command, RectangleFillTransparent8);

        Assert.Equal(new byte[] { 0xCC, 0x34, 0x34, 0xCC }, memory.GetSpan(baseAddress, 4).ToArray());
        Assert.Equal(new byte[] { 0xCC, 0xCC, 0xCC, 0xCC }, memory.GetSpan(baseAddress + stride, 4).ToArray());
    }

    [Fact]
    public void TransparentColourUploadSkipsTheSourceForegroundKeyAndWordAlignsRows()
    {
        VgaMemory memory = CreateMemory();
        var engine = new S3Virge2DEngine(memory);
        const uint baseAddress = 0x5000;
        const uint stride = 8;
        memory.GetSpan(baseAddress, 32).Fill(0xCC);
        ProgramDestination(engine, baseAddress, stride, x: 0, y: 0, width: 5, height: 2);
        ProgramFullClip(engine);
        engine.WriteDword(SourceForeground, 0x77);

        engine.WriteDword(Command, TransparentColourUploadWordAligned8);

        // Five 8-bit pixels per row pad to six bytes with word alignment, so
        // the second row begins in the upper half of the second DWORD.
        Assert.True(engine.WriteImageDword(0x44772211));
        Assert.True(engine.WriteImageDword(0x77660055));
        Assert.True(engine.IsBusy);
        Assert.True(engine.WriteImageDword(0x00AA9988));

        Assert.False(engine.IsBusy);
        Assert.Equal(new byte[] { 0x11, 0x22, 0xCC, 0x44, 0x55, 0xCC }, memory.GetSpan(baseAddress, 6).ToArray());
        Assert.Equal(new byte[] { 0x66, 0xCC, 0x88, 0x99, 0xAA, 0xCC }, memory.GetSpan(baseAddress + stride, 6).ToArray());
    }

    [Fact]
    public void SnapshotResumesAByteAlignedMonoUploadInTheMiddleOfARow()
    {
        VgaMemory sourceMemory = CreateMemory();
        var source = new S3Virge2DEngine(sourceMemory);
        ProgramDestination(source, 0x6000, 32, x: 0, y: 0, width: 20, height: 3);
        ProgramFullClip(source);
        source.WriteDword(SourceForeground, 0x0F);
        source.WriteDword(Command, TransparentMonoText8);

        // Three bytes per row, nine in total: after one DWORD the cursor sits
        // at row 1, byte 1, which is not a DWORD boundary.
        source.WriteImageDword(0x80FFFFFF);
        byte[] snapshot = source.Capture();
        S3Virge2DEngine.ValidateSnapshot(snapshot);

        VgaMemory restoredMemory = CreateMemory();
        sourceMemory.GetSpan(0, VgaMemory.TotalSize).CopyTo(restoredMemory.GetSpan(0, VgaMemory.TotalSize));
        var restored = new S3Virge2DEngine(restoredMemory);
        restored.Restore(snapshot);

        foreach (uint value in new uint[] { 0xFFFFFFFF, 0x000000FF })
        {
            Assert.True(source.WriteImageDword(value));
            Assert.True(restored.WriteImageDword(value));
        }

        Assert.False(source.IsBusy);
        Assert.False(restored.IsBusy);
        Assert.Equal(
            sourceMemory.GetSpan(0, VgaMemory.TotalSize).ToArray(),
            restoredMemory.GetSpan(0, VgaMemory.TotalSize).ToArray());
        Assert.Equal(0x0F, restoredMemory.ReadLinear(0x6000 + 2 * 32 + 19));
        Assert.Equal(0, restoredMemory.ReadLinear(0x6000 + 2 * 32 + 20));
    }

    [Theory]
    [InlineData(MonoPatternCopy8 & ~(1u << 8))] // PATCOPY with a colour pattern.
    [InlineData(TransparentMonoText8 | 3u << 10)] // Reserved image alignment.
    [InlineData(MonoPatternCopy8 | 2u << 2)] // 24-bit destination.
    [InlineData(0x079808A6 | FirstDwordOffsetOne)] // 16-bit colour upload split by FDO.
    [InlineData(0x17980122)] // Rectangle fill with SRCCOPY, a source-dependent ROP.
    public void CommandsOutsideTheModelledSubsetAreReportedWithoutDrawing(uint command)
    {
        VgaMemory memory = CreateMemory();
        var engine = new S3Virge2DEngine(memory);
        ProgramDestination(engine, 0x7000, 16, x: 0, y: 0, width: 2, height: 1);
        engine.WriteDword(MonoPattern0, 0xFFFFFFFF);
        engine.WriteDword(MonoPattern1, 0xFFFFFFFF);
        engine.WriteDword(PatternForeground, 0xFF);
        uint reported = 0;
        engine.UnsupportedCommandIssued += issued => reported = issued;

        engine.WriteDword(Command, command);

        Assert.Equal(command, reported);
        Assert.False(engine.IsBusy);
        Assert.Equal(new byte[2], memory.GetSpan(0x7000, 2).ToArray());
    }

    [Fact]
    public void DrawEnableClearConsumesTheUploadWithoutTouchingTheScreen()
    {
        VgaMemory memory = CreateMemory();
        var engine = new S3Virge2DEngine(memory);
        ProgramDestination(engine, 0x8000, 16, x: 0, y: 0, width: 8, height: 1);
        ProgramFullClip(engine);
        engine.WriteDword(SourceForeground, 0x0F);

        engine.WriteDword(Command, OpaqueMonoText8 & ~(1u << 5));

        Assert.True(engine.IsBusy);
        Assert.True(engine.WriteImageDword(0xFFFFFFFF));
        Assert.False(engine.IsBusy);
        Assert.Equal(new byte[8], memory.GetSpan(0x8000, 8).ToArray());
    }

    private static VgaMemory CreateMemory()
    {
        return new VgaMemory(new VgaRegisters());
    }

    private static void ProgramDestination(
        S3Virge2DEngine engine,
        uint baseAddress,
        uint stride,
        int x,
        int y,
        int width,
        int height)
    {
        engine.WriteDword(SourceBase, baseAddress);
        engine.WriteDword(DestinationBase, baseAddress);
        engine.WriteDword(Stride, stride << 16 | stride);
        engine.WriteDword(WidthHeight, Pack(width - 1, height));
        engine.WriteDword(SourceCoordinates, Pack(x, y));
        engine.WriteDword(DestinationCoordinates, Pack(x, y));
    }

    /// <summary>The driver's 640x480 clip window; the text commands set HC.</summary>
    private static void ProgramFullClip(S3Virge2DEngine engine)
    {
        engine.WriteDword(ClipLeftRight, Pack(0, 639));
        engine.WriteDword(ClipTopBottom, Pack(0, 479));
    }

    private static uint Pack(int high, int low)
    {
        return (uint)(high << 16 | low);
    }
}
