using System.Runtime.InteropServices;

namespace Aurora86.Core.Devices.Video;

/// <summary>
/// Rows a frontend must refresh in its retained presentation buffer. The
/// range includes unchanged rows between its endpoints to require one upload.
/// </summary>
public readonly record struct FramebufferRowRange(int FirstRow, int RowCount);

/// <summary>
/// Tracks the pixels successfully presented by a host frontend. Video devices
/// retain their authoritative RAM framebuffer; this cache is presentation
/// state only and never participates in guest reads, fences or snapshots.
/// Callers serialize access with rendering and confirm only successful copies.
/// </summary>
public sealed class FramebufferUploadTracker
{
    private uint[] _presented = [];
    private int _width;
    private int _height;
    private bool _initialized;

    /// <summary>Forces a full refresh after texture replacement or renderer loss.</summary>
    public void Invalidate()
    {
        _initialized = false;
    }

    /// <summary>
    /// Finds the bounding range of changed rows. An unconfirmed range remains
    /// pending even if the video device reports no changes on the next frame.
    /// </summary>
    public FramebufferRowRange FindChangedRows(ReadOnlySpan<uint> source, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        int pixels = checked(width * height);
        if (source.Length < pixels)
        {
            throw new ArgumentException("The framebuffer is shorter than its dimensions.", nameof(source));
        }

        if (_width != width || _height != height)
        {
            _presented = new uint[pixels];
            _width = width;
            _height = height;
            _initialized = false;
        }

        if (!_initialized)
        {
            return new FramebufferRowRange(0, height);
        }

        int first = 0;
        while (first < height && RowMatches(source, first))
        {
            first++;
        }

        if (first == height)
        {
            return new FramebufferRowRange(0, 0);
        }

        int last = height - 1;
        while (last > first && RowMatches(source, last))
        {
            last--;
        }

        return new FramebufferRowRange(first, last - first + 1);
    }

    /// <summary>
    /// Copies a selected range into a locked destination whose first byte is
    /// that range's first row, preserving padding and allowing arbitrary pitch.
    /// Returns the pixel bytes written, excluding destination padding.
    /// </summary>
    public long CopyRows(ReadOnlySpan<uint> source, FramebufferRowRange rows,
        Span<byte> destination, int destinationPitch)
    {
        ValidateRange(source, rows);
        int rowBytes = checked(_width * sizeof(uint));
        if (destinationPitch < rowBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationPitch));
        }

        long required = rows.RowCount == 0 ? 0 :
            (long)(rows.RowCount - 1) * destinationPitch + rowBytes;
        if (destination.Length < required)
        {
            throw new ArgumentException("The destination does not contain the selected rows.", nameof(destination));
        }

        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(source);
        for (int row = 0; row < rows.RowCount; row++)
        {
            int sourceOffset = checked((rows.FirstRow + row) * rowBytes);
            bytes.Slice(sourceOffset, rowBytes).CopyTo(destination.Slice(row * destinationPitch, rowBytes));
        }

        return (long)rowBytes * rows.RowCount;
    }

    /// <summary>Commits only rows actually copied into the retained destination.</summary>
    public void ConfirmUpload(ReadOnlySpan<uint> source, FramebufferRowRange rows)
    {
        ValidateRange(source, rows);
        if (!_initialized && (rows.FirstRow != 0 || rows.RowCount != _height))
        {
            throw new InvalidOperationException("A new presentation buffer needs a complete first upload.");
        }

        int offset = checked(rows.FirstRow * _width);
        int count = checked(rows.RowCount * _width);
        source.Slice(offset, count).CopyTo(_presented.AsSpan(offset, count));
        _initialized = true;
    }

    private bool RowMatches(ReadOnlySpan<uint> source, int row)
    {
        int offset = row * _width;
        return source.Slice(offset, _width).SequenceEqual(_presented.AsSpan(offset, _width));
    }

    private void ValidateRange(ReadOnlySpan<uint> source, FramebufferRowRange rows)
    {
        if (_width == 0 || source.Length < _presented.Length || rows.FirstRow < 0 ||
            rows.RowCount < 0 || rows.FirstRow > _height || rows.RowCount > _height - rows.FirstRow)
        {
            throw new ArgumentOutOfRangeException(nameof(rows));
        }
    }
}
