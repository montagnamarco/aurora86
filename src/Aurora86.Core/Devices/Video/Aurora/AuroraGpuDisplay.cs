using System.Runtime.InteropServices;

namespace Aurora86.Core.Devices.Video;

/// <summary>
/// What a front end shows for the Aurora Display Adapter: the VGA picture
/// while the adapter's own scanout is off, and otherwise the displayed frame
/// read out of video memory, with the hardware cursor composited on top.
///
/// Video memory holds pixels as AARRGGBB (blue in the low byte); front ends
/// take 0xAABBGGRR (red in the low byte, see VgaRenderer.ToRgba), so red and
/// blue are exchanged on the way out and alpha is forced opaque -- scanout
/// ignores alpha, and a front end that honoured it would show the desktop
/// through a half-transparent window. Only the rows the card reports written
/// are converted, which keeps a 1920x1080 frame whose cursor alone moved from
/// costing two million conversions.
/// </summary>
public sealed class AuroraGpuDisplay : IVideoOutput
{
    private const int BytesPerPixel = 4;
    private const uint OpaqueAlpha = 0xFF000000;

    /// <summary>What an enabled scanout with an invalid geometry shows: black, at a size the front end can open.</summary>
    private const int InvalidGeometryWidth = 640;
    private const int InvalidGeometryHeight = 480;

    private readonly AuroraGpuCard _card;
    private readonly VgaRenderer _vga;
    private uint[] _framebuffer = [];
    private bool _showingScanout;
    private int _renderedVersion = -1;
    private (bool Visible, int X, int Y) _lastCursor;

    /// <summary>The first frame row the cursor covered when it was last drawn.</summary>
    private int _lastCursorTop;

    public AuroraGpuDisplay(AuroraGpuCard card, VgaRenderer vga)
    {
        _card = card;
        _vga = vga;
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public ReadOnlySpan<uint> Framebuffer => _showingScanout ? _framebuffer : _vga.Framebuffer;

    public bool CursorBlinkOn
    {
        get => _vga.CursorBlinkOn;
        set => _vga.CursorBlinkOn = value;
    }

    /// <summary>
    /// Brings the picture up to date. Returns true when it changed, so the
    /// front end can skip uploading an identical texture.
    /// </summary>
    public bool Render(bool force = false)
    {
        if (!_card.ScanoutEnabled)
        {
            return RenderVga(force);
        }

        bool switched = !_showingScanout;
        _showingScanout = true;

        if (!_card.ScanoutActive)
        {
            return RenderBlack(force || switched);
        }

        int width = _card.DisplayWidth;
        int height = _card.DisplayHeight;
        bool resized = width != Width || height != Height || _framebuffer.Length != width * height;
        if (resized)
        {
            Width = width;
            Height = height;
            _framebuffer = new uint[width * height];
        }

        var cursor = (_card.CursorVisible, _card.CursorX, _card.CursorY);
        bool unchanged = _card.DisplayVersion == _renderedVersion && cursor == _lastCursor;
        if (unchanged && !force && !switched && !resized)
        {
            return false;
        }

        bool fullFrame = force || switched || resized || _card.FullRepaintPending;
        if (fullFrame)
        {
            ConvertRows(0, Height - 1);
        }
        else
        {
            ConvertDirtyRows();

            // The rows the cursor covered last time must lose it.
            if (_lastCursor.Visible)
            {
                ConvertRows(_lastCursorTop, _lastCursorTop + AuroraGpuInterface.CursorSize - 1);
            }
        }

        CompositeCursor();

        _card.ConsumeDirtyRange();
        _renderedVersion = _card.DisplayVersion;
        _lastCursor = cursor;
        _lastCursorTop = _card.CursorY - _card.CursorHotSpotY;
        return true;
    }

    private bool RenderVga(bool force)
    {
        bool switched = _showingScanout;
        _showingScanout = false;
        bool changed = _vga.Render(force || switched);
        Width = _vga.Width;
        Height = _vga.Height;
        return changed || switched;
    }

    private bool RenderBlack(bool changed)
    {
        if (Width != InvalidGeometryWidth || Height != InvalidGeometryHeight
            || _framebuffer.Length != InvalidGeometryWidth * InvalidGeometryHeight)
        {
            Width = InvalidGeometryWidth;
            Height = InvalidGeometryHeight;
            _framebuffer = new uint[Width * Height];
            changed = true;
        }

        if (changed)
        {
            Array.Fill(_framebuffer, OpaqueAlpha);
        }

        _renderedVersion = -1;
        return changed;
    }

    /// <summary>Converts the rows that the video memory written since the last frame falls on.</summary>
    private void ConvertDirtyRows()
    {
        if (_card.DirtyEnd <= _card.DirtyStart)
        {
            return;
        }

        long frameBytes = (long)_card.DisplayPitch * Height;
        long start = (long)_card.DirtyStart - _card.DisplayOffset;
        long end = (long)_card.DirtyEnd - _card.DisplayOffset;
        if (end <= 0 || start >= frameBytes)
        {
            return;
        }

        long pitch = _card.DisplayPitch;
        int firstRow = (int)(Math.Max(start, 0) / pitch);
        int lastRow = (int)((Math.Min(end, frameBytes) - 1) / pitch);
        ConvertRows(firstRow, lastRow);
    }

    /// <summary>Converts frame rows <paramref name="firstRow"/> to <paramref name="lastRow"/>, clamped to the frame.</summary>
    private void ConvertRows(int firstRow, int lastRow)
    {
        firstRow = Math.Max(firstRow, 0);
        lastRow = Math.Min(lastRow, Height - 1);

        ReadOnlySpan<byte> videoMemory = _card.VideoMemoryBytes;
        uint offset = _card.DisplayOffset;
        uint pitch = _card.DisplayPitch;

        for (int row = firstRow; row <= lastRow; row++)
        {
            int rowStart = (int)(offset + (uint)row * pitch);
            ReadOnlySpan<uint> source = MemoryMarshal.Cast<byte, uint>(
                videoMemory.Slice(rowStart, Width * BytesPerPixel));
            Span<uint> destination = _framebuffer.AsSpan(row * Width, Width);
            for (int column = 0; column < Width; column++)
            {
                destination[column] = ToFrontEnd(source[column]);
            }
        }
    }

    /// <summary>
    /// Blends the 64x64 premultiplied cursor over the converted frame, its
    /// hot spot at the cursor position. It lives only in this picture, never
    /// in video memory, so moving it costs the guest nothing.
    /// </summary>
    private void CompositeCursor()
    {
        if (!_card.CursorVisible)
        {
            return;
        }

        const int Size = AuroraGpuInterface.CursorSize;
        ReadOnlySpan<uint> image = _card.CursorImage;
        int left = _card.CursorX - _card.CursorHotSpotX;
        int top = _card.CursorY - _card.CursorHotSpotY;

        for (int row = 0; row < Size; row++)
        {
            int y = top + row;
            if (y < 0 || y >= Height)
            {
                continue;
            }

            for (int column = 0; column < Size; column++)
            {
                int x = left + column;
                if (x < 0 || x >= Width)
                {
                    continue;
                }

                uint cursorPixel = image[row * Size + column];
                if (cursorPixel >> 24 == 0 && (cursorPixel & 0x00FFFFFF) == 0)
                {
                    continue;
                }

                int index = y * Width + x;
                uint underneath = FromFrontEnd(_framebuffer[index]);
                uint blended = AuroraGpu2DEngine.BlendOver(cursorPixel, underneath);
                _framebuffer[index] = ToFrontEnd(blended);
            }
        }
    }

    /// <summary>AARRGGBB from video memory to the front end's opaque 0xAABBGGRR.</summary>
    private static uint ToFrontEnd(uint pixel)
    {
        uint red = (pixel >> 16) & 0xFF;
        uint green = pixel & 0x0000FF00;
        uint blue = pixel & 0xFF;
        return OpaqueAlpha | (blue << 16) | green | red;
    }

    private static uint FromFrontEnd(uint pixel)
    {
        uint red = pixel & 0xFF;
        uint green = pixel & 0x0000FF00;
        uint blue = (pixel >> 16) & 0xFF;
        return OpaqueAlpha | (red << 16) | green | blue;
    }
}
