using System.Buffers.Binary;
using Aurora86.Core.Devices.Video;

namespace Aurora86.Tests;

/// <summary>
/// Exercises the ViRGE 86C325 BitBLT registers at their documented offsets.
/// Explicit byte layouts pin direction, inclusive clipping, DWORD row padding
/// and resumable CPU input without depending on a live DOS driver.
/// </summary>
public class S3Virge2DEngineTests
{
    private const uint SourceBase = 0xA4D4;
    private const uint DestinationBase = 0xA4D8;
    private const uint ClipLeftRight = 0xA4DC;
    private const uint ClipTopBottom = 0xA4E0;
    private const uint Stride = 0xA4E4;
    private const uint PatternForeground = 0xA4F4;
    private const uint Command = 0xA500;
    private const uint WidthHeight = 0xA504;
    private const uint SourceCoordinates = 0xA508;
    private const uint DestinationCoordinates = 0xA50C;

    private const uint RectangleFill16 = 0x17E00124;
    private const uint ScreenCopy8 = 0x07980822;
    private const uint ScreenCopy16 = 0x07980224;
    private const uint CpuColorUpload16 = 0x079808A6;

    [Fact]
    public void RegisterDecoderAcceptsOnlyAlignedA4D4ThroughA50C()
    {
        for (uint register = SourceBase; register <= DestinationCoordinates; register += 4)
        {
            Assert.True(S3Virge2DEngine.IsRegister(register));
        }

        Assert.False(S3Virge2DEngine.IsRegister(SourceBase - 4));
        Assert.False(S3Virge2DEngine.IsRegister(SourceBase + 1));
        Assert.False(S3Virge2DEngine.IsRegister(DestinationCoordinates + 4));
    }

    [Fact]
    public void BaseAndStrideLatchesMaskToEightBytesAndDoNotWrapPastTwoMegabytes()
    {
        VgaMemory memory = CreateMemory();
        var engine = new S3Virge2DEngine(memory);
        engine.WriteDword(SourceBase, uint.MaxValue);
        engine.WriteDword(DestinationBase, uint.MaxValue);
        engine.WriteDword(Stride, uint.MaxValue);

        Assert.Equal(0x003FFFF8u, engine.ReadDword(SourceBase));
        Assert.Equal(0x003FFFF8u, engine.ReadDword(DestinationBase));
        Assert.Equal(0x0FF80FF8u, engine.ReadDword(Stride));

        engine.WriteDword(DestinationBase, (uint)VgaMemory.TotalSize - 8);
        engine.WriteDword(Stride, 0);
        engine.WriteDword(WidthHeight, Pack(widthMinusOne: 1, low: 1));
        engine.WriteDword(DestinationCoordinates, Pack(3, 0));
        engine.WriteDword(PatternForeground, 0xABCD);
        engine.WriteDword(Command, RectangleFill16);

        Assert.Equal(0xABCD, Read16(memory, (uint)VgaMemory.TotalSize - 2));
        Assert.Equal(0, Read16(memory, 0));
    }

    [Fact]
    public void PatCopyRectangleUsesForegroundAndCanClearSixteenBitDepth()
    {
        VgaMemory memory = CreateMemory();
        var engine = new S3Virge2DEngine(memory);
        memory.GetSpan(0x100, 64).Fill(0xCC);
        ProgramDestination(engine, 0x100, 16, x: 1, y: 1, width: 2, height: 2);

        engine.WriteDword(PatternForeground, 0xFFFF);
        engine.WriteDword(Command, RectangleFill16);

        Assert.Equal(0xFFFF, Read16(memory, 0x100 + 1 * 16 + 1 * 2));
        Assert.Equal(0xFFFF, Read16(memory, 0x100 + 1 * 16 + 2 * 2));
        Assert.Equal(0xFFFF, Read16(memory, 0x100 + 2 * 16 + 1 * 2));
        Assert.Equal(0xFFFF, Read16(memory, 0x100 + 2 * 16 + 2 * 2));
        Assert.Equal(0xCCCC, Read16(memory, 0x100 + 1 * 16));

        engine.WriteDword(PatternForeground, 0);
        engine.WriteDword(Command, RectangleFill16);

        Assert.Equal(0, Read16(memory, 0x100 + 1 * 16 + 1 * 2));
        Assert.Equal(0, Read16(memory, 0x100 + 2 * 16 + 2 * 2));
    }

    [Fact]
    public void EightBitScreenCopyUsesNegativeXDirectionForOverlap()
    {
        VgaMemory memory = CreateMemory();
        var engine = new S3Virge2DEngine(memory);
        const uint baseAddress = 0x200;
        for (uint index = 0; index < 6; index++)
        {
            memory.WriteLinear(baseAddress + index, (byte)(index + 1));
        }

        engine.WriteDword(SourceBase, baseAddress);
        engine.WriteDword(DestinationBase, baseAddress);
        engine.WriteDword(Stride, 8u << 16 | 8);
        engine.WriteDword(WidthHeight, Pack(widthMinusOne: 3, low: 1));
        engine.WriteDword(SourceCoordinates, Pack(3, 0));
        engine.WriteDword(DestinationCoordinates, Pack(4, 0));

        uint negativeXCommand = ScreenCopy8 & ~(1u << 25) & ~2u;
        engine.WriteDword(Command, negativeXCommand);

        Assert.Equal(new byte[] { 1, 1, 2, 3, 4, 6 },
            memory.GetSpan(baseAddress, 6).ToArray());
    }

    [Fact]
    public void SixteenBitScreenCopyUsesNegativeYDirectionForOverlap()
    {
        VgaMemory memory = CreateMemory();
        var engine = new S3Virge2DEngine(memory);
        const uint baseAddress = 0x300;
        const uint stride = 8;
        Write16(memory, baseAddress + 0 * stride, 0x1111);
        Write16(memory, baseAddress + 0 * stride + 2, 0x2222);
        Write16(memory, baseAddress + 1 * stride, 0x3333);
        Write16(memory, baseAddress + 1 * stride + 2, 0x4444);
        Write16(memory, baseAddress + 2 * stride, 0xAAAA);
        Write16(memory, baseAddress + 2 * stride + 2, 0xBBBB);

        engine.WriteDword(SourceBase, baseAddress);
        engine.WriteDword(DestinationBase, baseAddress);
        engine.WriteDword(Stride, stride << 16 | stride);
        engine.WriteDword(WidthHeight, Pack(widthMinusOne: 1, low: 2));
        engine.WriteDword(SourceCoordinates, Pack(0, 1));
        engine.WriteDword(DestinationCoordinates, Pack(0, 2));

        uint negativeYCommand = ScreenCopy16 & ~(1u << 26);
        engine.WriteDword(Command, negativeYCommand);

        Assert.Equal(0x1111, Read16(memory, baseAddress + 1 * stride));
        Assert.Equal(0x2222, Read16(memory, baseAddress + 1 * stride + 2));
        Assert.Equal(0x3333, Read16(memory, baseAddress + 2 * stride));
        Assert.Equal(0x4444, Read16(memory, baseAddress + 2 * stride + 2));
    }

    [Fact]
    public void UploadConsumesClippedPixelsAndOddWidthRowPadding()
    {
        VgaMemory memory = CreateMemory();
        var engine = new S3Virge2DEngine(memory);
        const uint baseAddress = 0x400;
        ProgramDestination(engine, baseAddress, 16, x: 10, y: 20, width: 3, height: 2);
        engine.WriteDword(ClipLeftRight, Pack(11, 11));
        engine.WriteDword(ClipTopBottom, Pack(20, 21));
        engine.WriteDword(Command, CpuColorUpload16);

        Assert.True(engine.IsBusy);
        Assert.True(engine.WriteImageDword(0x22221111));
        Assert.True(engine.WriteImageDword(0xAAAA3333));
        Assert.True(engine.WriteImageDword(0x55554444));
        Assert.True(engine.WriteImageDword(0xBBBB6666));

        Assert.False(engine.IsBusy);
        Assert.False(engine.WriteImageDword(0xCCCCCCCC));
        Assert.Equal(0, Read16(memory, baseAddress + 20 * 16 + 10 * 2));
        Assert.Equal(0x2222, Read16(memory, baseAddress + 20 * 16 + 11 * 2));
        Assert.Equal(0, Read16(memory, baseAddress + 20 * 16 + 12 * 2));
        Assert.Equal(0x5555, Read16(memory, baseAddress + 21 * 16 + 11 * 2));
    }

    [Fact]
    public void SnapshotResumesFrozenUploadAtNextDwordWithoutRedrawing()
    {
        VgaMemory sourceMemory = CreateMemory();
        var source = new S3Virge2DEngine(sourceMemory);
        ProgramDestination(source, 0x600, 16, x: 1, y: 2, width: 3, height: 2);
        source.WriteDword(Command, CpuColorUpload16 & ~2u);
        source.WriteImageDword(0x22221111);
        byte[] snapshot = source.Capture();

        VgaMemory restoredMemory = CreateMemory();
        sourceMemory.GetSpan(0, VgaMemory.TotalSize)
            .CopyTo(restoredMemory.GetSpan(0, VgaMemory.TotalSize));
        var restored = new S3Virge2DEngine(restoredMemory);
        restored.Restore(snapshot);

        // Ordinary latch writes after submission cannot redirect either
        // active stream because the command parameters were frozen.
        source.WriteDword(DestinationBase, 0x1000);
        restored.WriteDword(DestinationCoordinates, Pack(100, 100));
        uint[] remaining = [0xAAAA3333, 0x55554444, 0xBBBB6666];
        foreach (uint value in remaining)
        {
            Assert.True(source.WriteImageDword(value));
            Assert.True(restored.WriteImageDword(value));
        }

        Assert.False(source.IsBusy);
        Assert.False(restored.IsBusy);
        Assert.Equal(
            sourceMemory.GetSpan(0, VgaMemory.TotalSize).ToArray(),
            restoredMemory.GetSpan(0, VgaMemory.TotalSize).ToArray());
        Assert.Equal(0x1111, Read16(restoredMemory, 0x600 + 2 * 16 + 1 * 2));
        Assert.Equal(0x6666, Read16(restoredMemory, 0x600 + 3 * 16 + 3 * 2));
    }

    [Fact]
    public void UnsupportedAndAutoexecuteCommandsCannotWriteVideoMemory()
    {
        VgaMemory memory = CreateMemory();
        var engine = new S3Virge2DEngine(memory);
        ProgramDestination(engine, 0x800, 8, x: 0, y: 0, width: 2, height: 1);
        engine.WriteDword(PatternForeground, 0xFFFF);
        int notifications = 0;
        engine.UnsupportedCommandIssued += _ => notifications++;

        engine.WriteDword(Command, RectangleFill16 ^ (1u << 17));
        engine.WriteDword(Command, CpuColorUpload16 | 1u);

        Assert.Equal(2, notifications);
        Assert.False(engine.IsBusy);
        Assert.False(engine.WriteImageDword(0xFFFFFFFF));
        Assert.Equal(new byte[4], memory.GetSpan(0x800, 4).ToArray());
    }

    [Fact]
    public void NopAndResetCancelUploadWithoutTriggeringDiagnostics()
    {
        VgaMemory memory = CreateMemory();
        var engine = new S3Virge2DEngine(memory);
        ProgramDestination(engine, 0, 8, x: 0, y: 0, width: 1, height: 1);
        int notifications = 0;
        engine.UnsupportedCommandIssued += _ => notifications++;
        engine.WriteDword(Command, CpuColorUpload16);

        engine.WriteDword(Command, 0x78000000);

        Assert.False(engine.IsBusy);
        Assert.Equal(0, notifications);
        engine.WriteDword(DestinationBase, 0x12345);
        engine.Reset();
        Assert.Equal(0u, engine.ReadDword(DestinationBase));
        Assert.Equal(0u, engine.ReadDword(Command));
    }

    [Fact]
    public void MalformedSnapshotLeavesRegistersAndTransferUntouched()
    {
        VgaMemory memory = CreateMemory();
        var engine = new S3Virge2DEngine(memory);
        ProgramDestination(engine, 0xA00, 16, x: 0, y: 0, width: 3, height: 2);
        engine.WriteDword(Command, CpuColorUpload16 & ~2u);
        engine.WriteImageDword(0x22221111);
        byte[] before = engine.Capture();
        byte[] malformed = (byte[])before.Clone();

        // The final field is the byte cursor; only DWORD positions below the
        // aligned row size are representable in this snapshot schema.
        BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(malformed.Length - 4), 2);

        Assert.Throws<ArgumentException>(() => S3Virge2DEngine.ValidateSnapshot(malformed));
        Assert.Throws<ArgumentException>(() => engine.Restore(malformed));
        Assert.Equal(before, engine.Capture());
        Assert.True(engine.IsBusy);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MalformedFrozenCommandBitsOrLatchMismatchAreRejected(bool useReservedBit)
    {
        VgaMemory memory = CreateMemory();
        var engine = new S3Virge2DEngine(memory);
        ProgramDestination(engine, 0xC00, 16, x: 0, y: 0, width: 3, height: 2);
        engine.WriteDword(Command, CpuColorUpload16 & ~2u);
        byte[] valid = engine.Capture();
        byte[] malformed = (byte[])valid.Clone();
        const int frozenCommandOffset = 8 + 15 * sizeof(uint) + sizeof(uint);
        uint frozenCommand = BinaryPrimitives.ReadUInt32LittleEndian(
            malformed.AsSpan(frozenCommandOffset));
        uint changedCommand = useReservedBit
            ? frozenCommand | (1u << 14)
            : frozenCommand ^ (1u << 25);
        BinaryPrimitives.WriteUInt32LittleEndian(
            malformed.AsSpan(frozenCommandOffset),
            changedCommand);

        Assert.Throws<ArgumentException>(() => S3Virge2DEngine.ValidateSnapshot(malformed));
        Assert.Equal(valid, engine.Capture());
        Assert.True(engine.IsBusy);
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
        engine.WriteDword(DestinationBase, baseAddress);
        engine.WriteDword(Stride, stride << 16);
        engine.WriteDword(WidthHeight, Pack(width - 1, height));
        engine.WriteDword(DestinationCoordinates, Pack(x, y));
    }

    private static uint Pack(int widthMinusOne, int low)
    {
        return (uint)(widthMinusOne << 16 | low);
    }

    private static ushort Read16(VgaMemory memory, uint address)
    {
        return (ushort)(memory.ReadLinear(address) | memory.ReadLinear(address + 1) << 8);
    }

    private static void Write16(VgaMemory memory, uint address, ushort value)
    {
        memory.WriteLinear(address, (byte)value);
        memory.WriteLinear(address + 1, (byte)(value >> 8));
    }
}
