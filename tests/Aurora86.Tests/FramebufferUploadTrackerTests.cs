using System.Runtime.InteropServices;
using Aurora86.Core.Devices.Video;

namespace Aurora86.Tests;

/// <summary>
/// Compares retained host presentation buffers against full independent
/// framebuffer copies, including padding, failed uploads and mode changes.
/// No guest-visible graphics state is replaced by the presentation cache.
/// </summary>
public sealed class FramebufferUploadTrackerTests
{
    [Fact]
    public void SmallChangeCopiesOnlySelectedRowsAndPreservesPadding()
    {
        const int width = 1280;
        const int height = 800;
        const int padding = 12;
        const int pitch = width * sizeof(uint) + padding;
        uint[] source = new uint[width * height];
        byte[] destination = Enumerable.Repeat((byte)0xA5, pitch * height).ToArray();
        var tracker = new FramebufferUploadTracker();

        long initial = Upload(tracker, source, width, height, destination, pitch);
        Assert.Equal(4096000, initial);
        Array.Fill(source, 0x12345678u, width * 300, width * 32);
        FramebufferRowRange changed = tracker.FindChangedRows(source, width, height);
        Assert.Equal(new FramebufferRowRange(300, 32), changed);
        long partial = Upload(tracker, source, width, height, destination, pitch);
        Assert.Equal(163840, partial);
        AssertImageAndPadding(source, destination, width, height, pitch);
        Assert.Equal(new FramebufferRowRange(0, 0), tracker.FindChangedRows(source, width, height));
    }

    [Fact]
    public void UnconfirmedUploadIsRetriedAndUsesNewestPixels()
    {
        const int width = 5;
        const int height = 7;
        uint[] source = new uint[width * height];
        var tracker = new FramebufferUploadTracker();
        FramebufferRowRange first = tracker.FindChangedRows(source, width, height);
        Assert.Equal(first, tracker.FindChangedRows(source, width, height));
        Assert.Throws<InvalidOperationException>(() => tracker.ConfirmUpload(source, new(0, 1)));
        tracker.ConfirmUpload(source, first);

        source[width * 2] = 1;
        Assert.Equal(new FramebufferRowRange(2, 1), tracker.FindChangedRows(source, width, height));
        source[width * 5] = 2;
        FramebufferRowRange retry = tracker.FindChangedRows(source, width, height);
        Assert.Equal(new FramebufferRowRange(2, 4), retry);
        tracker.ConfirmUpload(source, retry);
        Assert.Equal(0, tracker.FindChangedRows(source, width, height).RowCount);
    }

    [Fact]
    public void TextureReplacementAndSamePixelCountResizeRequireFullUpload()
    {
        uint[] source = new uint[24];
        var tracker = new FramebufferUploadTracker();
        tracker.ConfirmUpload(source, tracker.FindChangedRows(source, 6, 4));
        tracker.Invalidate();
        Assert.Equal(new FramebufferRowRange(0, 4), tracker.FindChangedRows(source, 6, 4));
        tracker.ConfirmUpload(source, new(0, 4));
        Assert.Equal(new FramebufferRowRange(0, 6), tracker.FindChangedRows(source, 4, 6));
        tracker.ConfirmUpload(source, new(0, 6));
        Assert.Equal(0, tracker.FindChangedRows(source, 4, 6).RowCount);
    }

    [Fact]
    public void RandomChangesMatchFullCopyAcrossEveryByte()
    {
        const int width = 37;
        const int height = 23;
        const int pitch = width * sizeof(uint) + 7;
        var random = new Random(0x4132);
        var tracker = new FramebufferUploadTracker();
        uint[] source = new uint[width * height];
        byte[] destination = Enumerable.Repeat((byte)0xA5, pitch * height).ToArray();

        Upload(tracker, source, width, height, destination, pitch);
        for (int frame = 0; frame < 300; frame++)
        {
            int changes = random.Next(0, 100);
            for (int change = 0; change < changes; change++)
            {
                source[random.Next(source.Length)] = (uint)random.NextInt64(0, 1L << 32);
            }

            // A skipped host upload must not lose the video device's updates.
            tracker.FindChangedRows(source, width, height);
            if (frame % 3 == 0)
            {
                continue;
            }

            Upload(tracker, source, width, height, destination, pitch);
            AssertImageAndPadding(source, destination, width, height, pitch);
        }

        Upload(tracker, source, width, height, destination, pitch);
        AssertImageAndPadding(source, destination, width, height, pitch);
    }

    [Fact]
    public void LastRowDoesNotRequireTrailingDestinationPadding()
    {
        uint[] source = [0x12345678, 0xFEDCBA98, 0x10203040, 0x55667788];
        var tracker = new FramebufferUploadTracker();
        FramebufferRowRange rows = tracker.FindChangedRows(source, 2, 2);
        byte[] destination = Enumerable.Repeat((byte)0xA5, 19).ToArray();
        Assert.Equal(16, tracker.CopyRows(source, rows, destination, 11));
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(source.AsSpan());
        Assert.True(bytes[..8].SequenceEqual(destination.AsSpan(0, 8)));
        Assert.True(bytes[8..].SequenceEqual(destination.AsSpan(11, 8)));
        Assert.Equal(new byte[] { 0xA5, 0xA5, 0xA5 }, destination[8..11]);
    }

    [Fact]
    public void InvalidSourcePitchAndDestinationFailBeforeWriting()
    {
        var tracker = new FramebufferUploadTracker();
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.FindChangedRows([], 0, 1));
        Assert.Throws<OverflowException>(() => tracker.FindChangedRows([], int.MaxValue, 2));
        Assert.Throws<ArgumentException>(() => tracker.FindChangedRows(new uint[3], 2, 2));

        uint[] source = [1, 2, 3, 4];
        FramebufferRowRange rows = tracker.FindChangedRows(source, 2, 2);
        byte[] destination = Enumerable.Repeat((byte)0xA5, 24).ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.CopyRows(source, rows, destination, 7));
        Assert.Throws<ArgumentException>(() => tracker.CopyRows(source, rows, destination.AsSpan(0, 15), 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.CopyRows(source, new(1, int.MaxValue), destination, 8));
        Assert.All(destination, value => Assert.Equal(0xA5, value));
        Assert.Equal(rows, tracker.FindChangedRows(source, 2, 2));
    }

    private static long Upload(FramebufferUploadTracker tracker, uint[] source, int width,
        int height, byte[] destination, int pitch)
    {
        FramebufferRowRange rows = tracker.FindChangedRows(source, width, height);
        if (rows.RowCount == 0)
        {
            return 0;
        }

        long bytes = tracker.CopyRows(source, rows,
            destination.AsSpan(rows.FirstRow * pitch, rows.RowCount * pitch), pitch);
        tracker.ConfirmUpload(source, rows);
        return bytes;
    }

    private static void AssertImageAndPadding(uint[] source, byte[] destination, int width,
        int height, int pitch)
    {
        ReadOnlySpan<byte> sourceBytes = MemoryMarshal.AsBytes(source.AsSpan());
        int rowBytes = width * sizeof(uint);
        for (int row = 0; row < height; row++)
        {
            Assert.True(sourceBytes.Slice(row * rowBytes, rowBytes)
                .SequenceEqual(destination.AsSpan(row * pitch, rowBytes)));
            for (int offset = rowBytes; offset < pitch; offset++)
            {
                Assert.Equal(0xA5, destination[row * pitch + offset]);
            }
        }
    }
}
