using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Aurora386.Gui.Emulation;

namespace Aurora386.Gui.Controls;

/// <summary>
/// Displays the emulated VGA output.
///
/// The core renders into a plain array of RGBA pixels; this control copies
/// that array into a bitmap and draws it, scaled to fill the available space
/// while preserving the aspect ratio a real monitor would have shown.
///
/// That last point is worth explaining, because getting it wrong is a
/// classic emulator bug. In 80x25 text mode the VGA has 400 active scanlines
/// (25 rows of 16-pixel characters), but it doubles them on the way out so
/// the monitor sees 640x480 -- a 4:3 picture. Presenting the 640x400
/// framebuffer at its literal size squashes every character to 83% of its
/// proper height, which reads as subtly wrong: letters look wide and
/// stubby. So the picture is always fitted to 4:3, not to its pixel
/// dimensions.
/// </summary>
public sealed class ScreenView : Control
{
    private WriteableBitmap? _bitmap;
    private int _bitmapWidth, _bitmapHeight;
    private EmulatorSession? _session;

    /// <summary>
    /// The aspect ratio a period monitor presented, regardless of how many
    /// pixels the adapter actually generated.
    /// </summary>
    private const double MonitorAspect = 4.0 / 3.0;

    public static readonly DirectProperty<ScreenView, EmulatorSession?> SessionProperty =
        AvaloniaProperty.RegisterDirect<ScreenView, EmulatorSession?>(
            nameof(Session), o => o.Session, (o, v) => o.Session = v);

    public EmulatorSession? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(_session, value)) return;

            if (_session is not null) _session.FrameReady -= OnFrameReady;
            SetAndRaise(SessionProperty, ref _session, value);
            if (_session is not null) _session.FrameReady += OnFrameReady;
        }
    }

    public ScreenView()
    {
        // Nearest-neighbour keeps the pixels crisp. Smoothing a 640x400 image
        // up to a modern display turns sharp character edges into mush.
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        Focusable = true;
    }

    private void OnFrameReady()
    {
        // Called on the emulation thread. Marshal to the UI thread at low
        // priority: if the emulator outruns the compositor we simply drop
        // frames rather than queueing an unbounded backlog.
        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        EmulatorSession? session = _session;
        if (session is null)
        {
            context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
            return;
        }

        int width = 0, height = 0;
        session.WithMachine(machine =>
        {
            machine.Video.Render();
            width = machine.Video.Width;
            height = machine.Video.Height;

            if (width <= 0 || height <= 0) return;

            EnsureBitmap(width, height);
            CopyFramebuffer(machine.Video.Framebuffer, width, height);
        });

        if (_bitmap is null || width <= 0 || height <= 0)
        {
            context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
            return;
        }

        Rect target = FitToMonitorAspect(Bounds.Size);

        // Letterbox the rest: black bars look like a real monitor, whereas
        // stretching to fill would distort the picture.
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        context.DrawImage(_bitmap, new Rect(0, 0, width, height), target);
    }

    /// <summary>
    /// Computes the largest 4:3 rectangle that fits in the available space,
    /// centred. This is what corrects the squashed characters: the source may
    /// be 640x400, but it is always shown as if it were 4:3.
    /// </summary>
    private static Rect FitToMonitorAspect(Size available)
    {
        if (available.Width <= 0 || available.Height <= 0) return default;

        double width = available.Width;
        double height = width / MonitorAspect;

        if (height > available.Height)
        {
            height = available.Height;
            width = height * MonitorAspect;
        }

        return new Rect((available.Width - width) / 2,
                        (available.Height - height) / 2,
                        width, height);
    }

    private void EnsureBitmap(int width, int height)
    {
        if (_bitmap is not null && _bitmapWidth == width && _bitmapHeight == height)
            return;

        _bitmap?.Dispose();
        _bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            // The renderer packs each pixel as R | G<<8 | B<<16 | A<<24. On a
            // little-endian machine that lands in memory as R,G,B,A, which is
            // what Avalonia calls Rgba8888. Declaring Bgra8888 here would make
            // the display read the red byte as blue: MS-DOS setup's blue
            // screen comes out red, and nothing else looks obviously wrong.
            PixelFormat.Rgba8888,
            AlphaFormat.Opaque);

        _bitmapWidth = width;
        _bitmapHeight = height;
    }

    private unsafe void CopyFramebuffer(ReadOnlySpan<uint> source, int width, int height)
    {
        if (_bitmap is null) return;

        using ILockedFramebuffer locked = _bitmap.Lock();
        var destination = (uint*)locked.Address;

        int rowPixels = locked.RowBytes / sizeof(uint);
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<uint> row = source.Slice(y * width, width);
            for (int x = 0; x < width; x++)
                destination[y * rowPixels + x] = row[x];
        }
    }
}
