using System.Buffers;
using System.Runtime.InteropServices;

namespace Aurora86.Core.Devices.Video;

/// <summary>
/// One rectangle of 32-bit pixels in the Aurora Display Adapter's video
/// memory: the displayed frame (surface 0) or an off-screen image a driver
/// defined with DEFINE_SURFACE. Offsets and pitch are in bytes.
/// </summary>
public readonly record struct AuroraGpuSurface(bool Defined, uint Offset, uint Pitch, int Width, int Height);

/// <summary>
/// The Aurora Display Adapter's 2D engine: the drawing half of the command
/// ring, executed at host speed on the card's video memory.
///
/// Every operation clips its destination to the surface and, when enabled,
/// to the clip rectangle, and never writes outside that intersection. Alpha
/// is premultiplied (docs/AURORA-GPU.md section 3), so source-over blending
/// is one multiply-add per channel and no division by the alpha itself.
///
/// The engine validates what a driver could get wrong only where it would
/// reach outside video memory -- a VRAM source rectangle -- and reports that
/// as an error code; clipping a destination to nothing is not an error.
/// </summary>
public sealed class AuroraGpu2DEngine
{
    private const int BytesPerPixel = 4;
    private const int ChannelMaximum = 255;

    private readonly byte[] _videoMemory;
    private readonly Func<uint, uint> _readGuestDword;
    private readonly Func<uint, byte> _readGuestByte;
    private readonly Action<uint, uint> _markDirty;

    /// <summary>
    /// Works on the card's video memory. Guest memory is reached through the
    /// two readers, which are the card's bus-master path; every write to
    /// video memory is reported to <paramref name="markDirty"/> as an offset
    /// and a length, so the display repaints only what changed.
    /// </summary>
    public AuroraGpu2DEngine(
        byte[] videoMemory,
        Func<uint, uint> readGuestDword,
        Func<uint, byte> readGuestByte,
        Action<uint, uint> markDirty)
    {
        _videoMemory = videoMemory;
        _readGuestDword = readGuestDword;
        _readGuestByte = readGuestByte;
        _markDirty = markDirty;
    }

    public bool ClipEnabled { get; set; }
    public int ClipX { get; set; }
    public int ClipY { get; set; }
    public int ClipWidth { get; set; }
    public int ClipHeight { get; set; }

    private Span<uint> Pixels => MemoryMarshal.Cast<byte, uint>(_videoMemory.AsSpan());

    // ---- operations ----------------------------------------------------------------

    /// <summary>FILL_RECT: writes, or blends source-over, one premultiplied colour.</summary>
    public void FillRect(AuroraGpuSurface target, int x, int y, int width, int height, uint colour, bool blend)
    {
        if (!TryClip(target, ref x, ref y, ref width, ref height, out _, out _))
        {
            return;
        }

        Span<uint> pixels = Pixels;
        for (int row = 0; row < height; row++)
        {
            Span<uint> line = pixels.Slice(PixelIndex(target, x, y + row), width);
            if (blend)
            {
                for (int column = 0; column < width; column++)
                {
                    line[column] = BlendOver(colour, line[column]);
                }
            }
            else
            {
                line.Fill(colour);
            }
        }

        MarkRows(target, x, y, width, height);
    }

    /// <summary>
    /// COPY_RECT: copies between surfaces. The source is read completely
    /// before anything is written, so overlapping rectangles -- a window
    /// scrolled within itself -- come out as if nothing had moved underneath.
    /// </summary>
    public void CopyRect(
        AuroraGpuSurface source,
        int sourceX,
        int sourceY,
        AuroraGpuSurface target,
        int x,
        int y,
        int width,
        int height,
        bool blend)
    {
        if (!TryClip(target, ref x, ref y, ref width, ref height, out int skippedColumns, out int skippedRows))
        {
            return;
        }

        // The source must lie inside its own surface as well; the part that
        // does not is simply not copied, like any clipped drawing. Worked in
        // 64 bits: the coordinates come straight from the ring, and a source
        // origin near the ends of the 32-bit range must clip to nothing rather
        // than wrap round into a rectangle that runs off video memory.
        long clippedSourceX = (long)sourceX + skippedColumns;
        long clippedSourceY = (long)sourceY + skippedRows;
        long clippedLeft = Math.Max(0, -clippedSourceX);
        long clippedTop = Math.Max(0, -clippedSourceY);
        long usableWidth = Math.Min(clippedSourceX + width, source.Width) - (clippedSourceX + clippedLeft);
        long usableHeight = Math.Min(clippedSourceY + height, source.Height) - (clippedSourceY + clippedTop);
        if (usableWidth <= 0 || usableHeight <= 0)
        {
            return;
        }

        sourceX = (int)(clippedSourceX + clippedLeft);
        sourceY = (int)(clippedSourceY + clippedTop);
        x += (int)clippedLeft;
        y += (int)clippedTop;
        width = (int)usableWidth;
        height = (int)usableHeight;

        int count = width * height;
        uint[] staging = ArrayPool<uint>.Shared.Rent(count);
        try
        {
            Span<uint> pixels = Pixels;
            for (int row = 0; row < height; row++)
            {
                pixels.Slice(PixelIndex(source, sourceX, sourceY + row), width)
                    .CopyTo(staging.AsSpan(row * width, width));
            }

            for (int row = 0; row < height; row++)
            {
                WriteRow(target, x, y + row, staging.AsSpan(row * width, width), blend);
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(staging);
        }

        MarkRows(target, x, y, width, height);
    }

    /// <summary>
    /// BLIT_IMAGE: copies 32-bit pixels from guest memory or video memory,
    /// one row every <paramref name="sourcePitch"/> bytes.
    /// </summary>
    public uint BlitImage(
        uint sourceSpace,
        uint sourceAddress,
        uint sourcePitch,
        AuroraGpuSurface target,
        int x,
        int y,
        int width,
        int height,
        bool blend)
    {
        uint error = ValidateSource(sourceSpace, sourceAddress, sourcePitch, width, height, BytesPerPixel);
        if (error != AuroraGpuInterface.ErrorNone)
        {
            return error;
        }

        if (!TryClip(target, ref x, ref y, ref width, ref height, out int skippedColumns, out int skippedRows))
        {
            return AuroraGpuInterface.ErrorNone;
        }

        uint[] row = ArrayPool<uint>.Shared.Rent(width);
        try
        {
            for (int line = 0; line < height; line++)
            {
                uint rowAddress = sourceAddress
                    + (uint)(skippedRows + line) * sourcePitch
                    + (uint)skippedColumns * BytesPerPixel;
                for (int column = 0; column < width; column++)
                {
                    row[column] = ReadSourceDword(sourceSpace, rowAddress + (uint)column * BytesPerPixel);
                }

                WriteRow(target, x, y + line, row.AsSpan(0, width), blend);
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(row);
        }

        MarkRows(target, x, y, width, height);
        return AuroraGpuInterface.ErrorNone;
    }

    /// <summary>
    /// DRAW_MASK: one coverage byte per pixel scales a premultiplied colour,
    /// which is then blended source-over. Zero coverage leaves the pixel
    /// untouched; 255 blends the colour itself. This is how antialiased
    /// glyphs are drawn from a cache of coverage masks.
    /// </summary>
    public uint DrawMask(
        uint sourceSpace,
        uint sourceAddress,
        uint sourcePitch,
        AuroraGpuSurface target,
        int x,
        int y,
        int width,
        int height,
        uint colour)
    {
        uint error = ValidateSource(sourceSpace, sourceAddress, sourcePitch, width, height, 1);
        if (error != AuroraGpuInterface.ErrorNone)
        {
            return error;
        }

        if (!TryClip(target, ref x, ref y, ref width, ref height, out int skippedColumns, out int skippedRows))
        {
            return AuroraGpuInterface.ErrorNone;
        }

        Span<uint> pixels = Pixels;
        for (int line = 0; line < height; line++)
        {
            uint rowAddress = sourceAddress + (uint)(skippedRows + line) * sourcePitch + (uint)skippedColumns;
            Span<uint> destination = pixels.Slice(PixelIndex(target, x, y + line), width);
            for (int column = 0; column < width; column++)
            {
                byte coverage = ReadSourceByte(sourceSpace, rowAddress + (uint)column);
                if (coverage == 0)
                {
                    continue;
                }

                uint scaled = ScaleColour(colour, coverage);
                destination[column] = BlendOver(scaled, destination[column]);
            }
        }

        MarkRows(target, x, y, width, height);
        return AuroraGpuInterface.ErrorNone;
    }

    /// <summary>
    /// DRAW_LINE: Bresenham's line, both end points included, each pixel
    /// clipped on its own. The walk visits every point of the line, visible
    /// or not, so the end points are bounded: a line across the whole 32-bit
    /// plane would otherwise keep the emulator busy for billions of steps.
    /// </summary>
    public uint DrawLine(AuroraGpuSurface target, int x0, int y0, int x1, int y1, uint colour, bool blend)
    {
        if (!IsLineCoordinate(x0) || !IsLineCoordinate(y0) || !IsLineCoordinate(x1) || !IsLineCoordinate(y1))
        {
            return AuroraGpuInterface.ErrorBadCoordinate;
        }

        int deltaX = Math.Abs(x1 - x0);
        int deltaY = -Math.Abs(y1 - y0);
        int stepX = x0 < x1 ? 1 : -1;
        int stepY = y0 < y1 ? 1 : -1;
        int error = deltaX + deltaY;

        int x = x0;
        int y = y0;
        Span<uint> pixels = Pixels;
        while (true)
        {
            if (IsVisible(target, x, y))
            {
                int index = PixelIndex(target, x, y);
                pixels[index] = blend ? BlendOver(colour, pixels[index]) : colour;
            }

            if (x == x1 && y == y1)
            {
                break;
            }

            int doubled = 2 * error;
            if (doubled >= deltaY)
            {
                error += deltaY;
                x += stepX;
            }

            if (doubled <= deltaX)
            {
                error += deltaX;
                y += stepY;
            }
        }

        int left = Math.Min(x0, x1);
        int top = Math.Min(y0, y1);
        int right = Math.Max(x0, x1);
        int bottom = Math.Max(y0, y1);
        int markX = left;
        int markY = top;
        int markWidth = right - left + 1;
        int markHeight = bottom - top + 1;
        if (TryClip(target, ref markX, ref markY, ref markWidth, ref markHeight, out _, out _))
        {
            MarkRows(target, markX, markY, markWidth, markHeight);
        }

        return AuroraGpuInterface.ErrorNone;
    }

    private static bool IsLineCoordinate(int value)
    {
        return value >= AuroraGpuInterface.MinimumLineCoordinate && value <= AuroraGpuInterface.MaximumLineCoordinate;
    }

    /// <summary>
    /// Reads a 64x64 cursor image into <paramref name="cursor"/>, one row of
    /// 256 bytes after another.
    /// </summary>
    public uint ReadCursorImage(uint sourceSpace, uint sourceAddress, Span<uint> cursor)
    {
        const int Size = AuroraGpuInterface.CursorSize;
        const uint Pitch = Size * BytesPerPixel;

        uint error = ValidateSource(sourceSpace, sourceAddress, Pitch, Size, Size, BytesPerPixel);
        if (error != AuroraGpuInterface.ErrorNone)
        {
            return error;
        }

        for (int index = 0; index < Size * Size; index++)
        {
            cursor[index] = ReadSourceDword(sourceSpace, sourceAddress + (uint)index * BytesPerPixel);
        }

        return AuroraGpuInterface.ErrorNone;
    }

    // ---- pixel arithmetic ------------------------------------------------------------

    /// <summary>
    /// Source-over with premultiplied alpha, per channel:
    /// result = source + destination x (255 - source alpha) / 255, rounded.
    /// </summary>
    public static uint BlendOver(uint source, uint destination)
    {
        uint sourceAlpha = source >> 24;
        if (sourceAlpha == ChannelMaximum)
        {
            return source;
        }

        if (sourceAlpha == 0 && (source & 0x00FFFFFF) == 0)
        {
            return destination;
        }

        uint inverse = ChannelMaximum - sourceAlpha;
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            uint sourceChannel = (source >> shift) & 0xFF;
            uint destinationChannel = (destination >> shift) & 0xFF;
            uint channel = sourceChannel + DivideBy255(destinationChannel * inverse);
            result |= Math.Min(channel, ChannelMaximum) << shift;
        }

        return result;
    }

    /// <summary>Multiplies every channel of a premultiplied colour by a coverage of 0-255.</summary>
    public static uint ScaleColour(uint colour, byte coverage)
    {
        if (coverage == ChannelMaximum)
        {
            return colour;
        }

        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            uint channel = (colour >> shift) & 0xFF;
            result |= DivideBy255(channel * coverage) << shift;
        }

        return result;
    }

    /// <summary>x / 255 rounded to nearest, exact for every product of two bytes.</summary>
    private static uint DivideBy255(uint value)
    {
        uint rounded = value + 128;
        return (rounded + (rounded >> 8)) >> 8;
    }

    // ---- helpers ---------------------------------------------------------------------

    /// <summary>
    /// Intersects a destination rectangle with its surface and the clip
    /// rectangle. Reports how many columns and rows were cut from the left
    /// and top, so a source can be advanced by the same amount.
    /// </summary>
    private bool TryClip(
        AuroraGpuSurface target,
        ref int x,
        ref int y,
        ref int width,
        ref int height,
        out int skippedColumns,
        out int skippedRows)
    {
        long left = x;
        long top = y;
        long right = (long)x + width;
        long bottom = (long)y + height;

        long boundLeft = 0;
        long boundTop = 0;
        long boundRight = target.Width;
        long boundBottom = target.Height;
        if (ClipEnabled)
        {
            boundLeft = Math.Max(boundLeft, ClipX);
            boundTop = Math.Max(boundTop, ClipY);
            boundRight = Math.Min(boundRight, (long)ClipX + ClipWidth);
            boundBottom = Math.Min(boundBottom, (long)ClipY + ClipHeight);
        }

        long clippedLeft = Math.Max(left, boundLeft);
        long clippedTop = Math.Max(top, boundTop);
        long clippedRight = Math.Min(right, boundRight);
        long clippedBottom = Math.Min(bottom, boundBottom);

        skippedColumns = (int)Math.Max(0, clippedLeft - left);
        skippedRows = (int)Math.Max(0, clippedTop - top);
        if (width <= 0 || height <= 0 || clippedRight <= clippedLeft || clippedBottom <= clippedTop)
        {
            return false;
        }

        x = (int)clippedLeft;
        y = (int)clippedTop;
        width = (int)(clippedRight - clippedLeft);
        height = (int)(clippedBottom - clippedTop);
        return true;
    }

    private bool IsVisible(AuroraGpuSurface target, int x, int y)
    {
        int clippedX = x;
        int clippedY = y;
        int width = 1;
        int height = 1;
        return TryClip(target, ref clippedX, ref clippedY, ref width, ref height, out _, out _);
    }

    private static int PixelIndex(AuroraGpuSurface surface, int x, int y)
    {
        long byteOffset = surface.Offset + (long)y * surface.Pitch + (long)x * BytesPerPixel;
        return (int)(byteOffset / BytesPerPixel);
    }

    private void WriteRow(AuroraGpuSurface target, int x, int y, ReadOnlySpan<uint> source, bool blend)
    {
        Span<uint> destination = Pixels.Slice(PixelIndex(target, x, y), source.Length);
        if (!blend)
        {
            source.CopyTo(destination);
            return;
        }

        for (int column = 0; column < source.Length; column++)
        {
            destination[column] = BlendOver(source[column], destination[column]);
        }
    }

    private void MarkRows(AuroraGpuSurface target, int x, int y, int width, int height)
    {
        uint first = (uint)(PixelIndex(target, x, y) * BytesPerPixel);
        uint last = (uint)(PixelIndex(target, x + width - 1, y + height - 1) * BytesPerPixel) + BytesPerPixel;
        _markDirty(first, last - first);
    }

    /// <summary>
    /// A VRAM source rectangle must lie wholly inside video memory; guest
    /// memory is not checked here, since reading past installed RAM returns
    /// the open bus's FFh like any other bus-master read.
    /// </summary>
    private uint ValidateSource(uint space, uint address, uint pitch, int width, int height, int bytesPerElement)
    {
        if (space == AuroraGpuInterface.SourceSpaceGuestMemory)
        {
            return AuroraGpuInterface.ErrorNone;
        }

        if (space != AuroraGpuInterface.SourceSpaceVideoMemory)
        {
            return AuroraGpuInterface.ErrorBadSource;
        }

        if (width <= 0 || height <= 0)
        {
            return AuroraGpuInterface.ErrorNone;
        }

        long end = address + (long)(height - 1) * pitch + (long)width * bytesPerElement;
        bool aligned = bytesPerElement == 1 || (address % BytesPerPixel == 0 && pitch % BytesPerPixel == 0);
        if (!aligned || end > _videoMemory.Length)
        {
            return AuroraGpuInterface.ErrorBadSource;
        }

        return AuroraGpuInterface.ErrorNone;
    }

    private uint ReadSourceDword(uint space, uint address)
    {
        if (space == AuroraGpuInterface.SourceSpaceVideoMemory)
        {
            return Pixels[(int)(address / BytesPerPixel)];
        }

        return _readGuestDword(address);
    }

    private byte ReadSourceByte(uint space, uint address)
    {
        if (space == AuroraGpuInterface.SourceSpaceVideoMemory)
        {
            return _videoMemory[address];
        }

        return _readGuestByte(address);
    }
}
