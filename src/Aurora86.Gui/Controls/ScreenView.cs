using Aurora86.Graphics;
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Input;
using Avalonia.Threading;
using Aurora86.Core.Devices.Storage;
using Aurora86.Core.Devices.Input;
using Aurora86.Core.Devices.Interrupts;
using Aurora86.Core.Devices.Timing;
using Aurora86.Core.Devices.System;
using Aurora86.Core.Devices.Video;
using Aurora86.Gui.Emulation;

namespace Aurora86.Gui.Controls;

/// <summary>
/// Displays the emulated VGA output.
///
/// The core renders into a plain array of RGBA pixels; this control copies
/// that array into a bitmap and draws it while preserving its active display
/// aspect ratio, except for legacy VGA modes presented on a 4:3 monitor.
///
/// That last point is worth explaining, because getting it wrong is a
/// classic emulator bug. In 80x25 text mode the VGA has 400 active scanlines
/// (25 rows of 16-pixel characters), but it doubles them on the way out so
/// the monitor sees 640x480 -- a 4:3 picture. Presenting the 640x400
/// framebuffer at its literal size squashes every character to 83% of its
/// proper height, which reads as subtly wrong: letters look wide and
/// stubby. That exception applies to historical VGA modes. Linear color
/// scanout has square pixels, so its native framebuffer aspect ratio is the
/// right one for drawing and absolute pointer input.
/// </summary>
public sealed class ScreenView : Control
{
    private WriteableBitmap? _bitmap;
    private int _bitmapWidth, _bitmapHeight;
    private readonly FramebufferUploadTracker _frameUploads = new();
    private bool _bitmapUploadPending;
    private EmulatorSession? _session;

    public static readonly DirectProperty<ScreenView, EmulatorSession?> SessionProperty =
        AvaloniaProperty.RegisterDirect<ScreenView, EmulatorSession?>(
            nameof(Session), o => o.Session, (o, v) => o.Session = v);

    public EmulatorSession? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(_session, value))
            {
                return;
            }

            if (_session is not null)
            {
                _session.FrameReady -= OnFrameReady;
            }
            SetAndRaise(SessionProperty, ref _session, value);
            if (_session is not null)
            {
                _session.FrameReady += OnFrameReady;
            }
        }
    }

    public ScreenView()
    {
        // Nearest-neighbour keeps the pixels crisp. Smoothing a 640x400 image
        // up to a modern display turns sharp character edges into mush.
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        Focusable = true;

        // Pointer events only reach a control the hit test finds, and the hit
        // test asks the control to draw itself. This one paints its whole
        // bounds on every frame -- the letterbox bars included -- so it is
        // found anywhere inside itself, and no backing rectangle is needed to
        // make the mouse work.
    }

    // --- mouse --------------------------------------------------------------
    //
    // The guest's mouse is a relative device: it counts how far it moved,
    // never where it is. So the host's movements have to reach it as
    // distances, and there are two quite different ways of obtaining one.
    //
    // Uncaptured, the host reports a position and we subtract the previous
    // one. That is lossy in the direction that matters: the host pointer
    // stops at the edge of the window, and once it has stopped there are no
    // more position changes to subtract -- the guest pointer freezes short of
    // the edge of the emulated screen even though the user is still pushing.
    // It is good enough for pointing at things inside the window, which is
    // why it remains the default: a user who only wants to click a button
    // should not have to capture anything first.
    //
    // Captured, the host pointer is unhooked from the mouse entirely and the
    // platform hands us the real movement. There is no edge any more, so the
    // guest pointer can be pushed as far as the guest's own driver allows.
    // See MouseCapture and IPointerLock.

    /// <summary>
    /// Whether the guest owns the pointer, and the rules for taking and
    /// giving it back. Created here rather than injected because the control
    /// is the only thing that can act on it; the rules themselves live in
    /// <see cref="MouseCapture"/> where they can be tested without a screen.
    /// </summary>
    public MouseCapture Capture { get; } = new(PointerLockFactory.Create());

    /// <summary>
    /// Where the host pointer was last seen, in this control's coordinates.
    /// Null when the pointer has not been inside the control yet, which is
    /// the case that must not produce a movement: the first position is a
    /// reference point, not a delta from the origin.
    /// </summary>
    private Point? _lastPointerPosition;

    /// <summary>
    /// How many mouse counts one pixel of host movement is worth.
    ///
    /// The emulated screen is far smaller than the window it is drawn in --
    /// 640 pixels stretched across perhaps 1400 -- so host pixels have to be
    /// scaled back down or the guest pointer moves at twice the speed of the
    /// real one. The scale comes from the drawn rectangle rather than being a
    /// constant, so it stays right when the window is resized.
    /// </summary>
    private double _mouseScaleX = 1, _mouseScaleY = 1;

    /// <summary>
    /// The mouse driver's coordinate range, refreshed with each frame. It is
    /// not the picture's size: in text mode the driver counts eight units per
    /// character cell, so an 80x25 screen is 640x200 units against a 640x400
    /// picture, and scaling movement against the picture makes the pointer
    /// crawl downwards at half the speed it moves sideways.
    /// </summary>
    private int _virtualWidth = 640, _virtualHeight = 200;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        // A guest driver that understands the VMware-compatible absolute
        // pointer wants the host cursor's own position, not a delta -- see
        // TryDeliverAbsolutePointer for why that replaces capture entirely.
        if (TryDeliverAbsolutePointer(e))
        {
            return;
        }

        if (!TryReadMovement(e, out double hostX, out double hostY))
        {
            return;
        }

        // Round rather than truncate, and accumulate what rounding drops.
        // A slow drag produces sub-pixel movements every frame; truncating
        // each one to zero means a mouse moved slowly does not move the guest
        // pointer at all, however far it travels.
        double deltaX = hostX * _mouseScaleX + _residualX;
        double deltaY = hostY * _mouseScaleY + _residualY;

        int countsX = (int)Math.Round(deltaX);
        int countsY = (int)Math.Round(deltaY);

        _residualX = deltaX - countsX;
        _residualY = deltaY - countsY;

        if (countsX == 0 && countsY == 0)
        {
            return;
        }

        _session?.WithMachine(machine => machine.MoveMouse(countsX, countsY));
    }

    /// <summary>
    /// Works out how far the mouse moved, from whichever source is telling
    /// the truth right now.
    ///
    /// Once captured the reported position is frozen -- it belongs to a
    /// pointer that is no longer following the mouse -- so subtracting
    /// positions would read zero forever and capture would look like it had
    /// broken the mouse. The platform's own delta is the only honest answer
    /// then, and the remembered position is thrown away so that the first
    /// move after release is a reference point rather than a leap across the
    /// screen.
    /// </summary>
    private bool TryReadMovement(PointerEventArgs e, out double x, out double y)
    {
        if (Capture.IsCaptured && Capture.IsUnbounded)
        {
            _lastPointerPosition = null;
            (x, y) = Capture.TakeMotion();
            return x != 0 || y != 0;
        }

        Point position = e.GetPosition(this);
        x = y = 0;

        if (_lastPointerPosition is not { } previous)
        {
            _lastPointerPosition = position;
            return false;
        }

        _lastPointerPosition = position;
        x = position.X - previous.X;
        y = position.Y - previous.Y;
        return true;
    }

    private double _residualX, _residualY;

    /// <summary>
    /// Feeds the host cursor's own position to the machine's absolute
    /// pointer, when a guest driver has asked for one, and reports whether it
    /// did.
    ///
    /// This is the alternative to capture, not a variant of it. Once the
    /// guest tracks the host cursor exactly there is nothing left for capture
    /// to add -- the pointer already follows the host across the whole
    /// screen without ever needing to leave the window or be hidden -- so a
    /// relative capture still held from before the driver loaded is let go
    /// here rather than fighting the absolute route for the same motion.
    ///
    /// The picture's drawn rectangle is <see cref="FitToMonitorAspect"/>'s,
    /// the same one <see cref="Render"/> uses to place the bitmap: computing
    /// it twice from two different ideas of where the picture sits is how a
    /// pointer ends up parked in a letterbox bar while the guest is drawn
    /// somewhere else entirely.
    /// </summary>
    private bool TryDeliverAbsolutePointer(PointerEventArgs e)
    {
        if (_session is null)
        {
            return false;
        }

        Point position = e.GetPosition(this);
        bool delivered = false;

        _session.WithMachine(machine =>
        {
            if (!machine.AbsolutePointerRequested)
            {
                return;
            }

            if (Capture.IsCaptured)
            {
                Capture.Release();
            }

            DisplayViewport target = DisplayViewport.Fit(
                Bounds.Size,
                machine.Display.Width,
                machine.Display.Height,
                UsesSquarePixels(machine),
                CurrentRenderScaling);
            if (target.Width <= 0 || target.Height <= 0)
            {
                return;
            }

            (int x, int y) = target.MapToSource(
                position,
                machine.Display.Width,
                machine.Display.Height);
            machine.PointAbsolute(x, y);
            delivered = true;
        });

        if (delivered)
        {
            // A reference point for whichever route resumes once absolute
            // mode ends, so the first relative move afterwards reads as a
            // small nudge rather than a leap from wherever the pointer
            // happened to be sitting last.
            _lastPointerPosition = position;
        }

        return delivered;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        // Forget where the pointer was. It will come back somewhere else
        // entirely, and treating the re-entry point as a continuation of the
        // exit point would fling the guest pointer across the screen.
        _lastPointerPosition = null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        Focus();

        // Clicking the emulated screen is the gesture that hands the pointer
        // to the guest, the way it is in every virtual machine. The click
        // itself is still delivered: the user meant to click something in
        // there, and swallowing the first click of every capture would be a
        // small but permanent annoyance.
        //
        // Not while a guest driver has already asked for the absolute
        // pointer, though: it already tracks the host cursor exactly, so
        // there is nothing capture would add and hiding the cursor would
        // only take away the host's own feedback for where the click landed.
        //
        // A click in absolute mode is positioned first: without a move
        // before it -- a window activated by a click, a pen or touch tap --
        // the button would otherwise land wherever the pointer last was.
        bool absoluteRequested = TryDeliverAbsolutePointer(e);
        if (!absoluteRequested)
        {
            Capture.Capture();
        }

        PointerPointProperties properties = e.GetCurrentPoint(this).Properties;
        SetButtons(properties);

        // Hold on to the pointer for the duration of the drag, so that a drag
        // that wanders out of the window still reports movement and, above
        // all, so the release is delivered here. Without it a button released
        // outside stays down as far as the guest is concerned -- the classic
        // stuck-mouse-button bug.
        e.Pointer.Capture(this);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        SetButtons(e.GetCurrentPoint(this).Properties);

        if (!AnyButtonDown(e.GetCurrentPoint(this).Properties))
            e.Pointer.Capture(null);
    }

    private static bool AnyButtonDown(PointerPointProperties properties)
        => properties.IsLeftButtonPressed
        || properties.IsRightButtonPressed
        || properties.IsMiddleButtonPressed;

    /// <summary>
    /// Sends the whole button state rather than just the one that changed.
    ///
    /// Avalonia reports which buttons are down at the moment of the event, so
    /// setting all three from that is both simpler and more robust than
    /// tracking transitions: a button whose release was missed -- because the
    /// window lost focus, say -- is corrected on the very next event instead
    /// of staying stuck down forever.
    /// </summary>
    private void SetButtons(PointerPointProperties properties)
    {
        _session?.WithMachine(machine =>
        {
            machine.SetMouseButton(MouseButtons.Left, properties.IsLeftButtonPressed);
            machine.SetMouseButton(MouseButtons.Right, properties.IsRightButtonPressed);
            machine.SetMouseButton(MouseButtons.Middle, properties.IsMiddleButtonPressed);
        });
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
        bool squarePixels = false;
        session.WithMachine(machine =>
        {
            // Report whole logical host pixels: a Retina display still presents
            // each guest pixel at an integer physical scale. The guest decides
            // whether to follow this advisory size; publishing never changes mode.
            bool minimized = TopLevel.GetTopLevel(this) is Window
                { WindowState: WindowState.Minimized };
            if (HostPreferredSize.TryFromLogical(Bounds.Width, Bounds.Height, minimized,
                out int preferredWidth, out int preferredHeight))
            {
                machine.AuroraGpu?.SetHostPreferredSize(preferredWidth, preferredHeight);
            }

            // Render says whether anything actually changed. In text mode
            // almost every frame changes nothing at all -- a blinking cursor
            // aside -- and copying a quarter of a million pixels to say so is
            // the single most expensive thing the interface does.
            bool changed = machine.Display.Render();
            width = machine.Display.Width;
            height = machine.Display.Height;
            squarePixels = UsesSquarePixels(machine);

            if (width <= 0 || height <= 0)
            {
                return;
            }

            bool resized = _bitmap is null || _bitmapWidth != width || _bitmapHeight != height;
            EnsureBitmap(width, height);
            if (changed || resized || _bitmapUploadPending)
            {
                _bitmapUploadPending = true;
                CopyFramebuffer(machine.Display.Framebuffer, width, height);
            }

            // Read here, inside the lock the frame copy already holds, rather
            // than taking it a second time further down: this runs sixty
            // times a second and the emulation thread wants the same lock for
            // every batch.
            (_virtualWidth, _virtualHeight) = machine.MouseServices.VirtualScreenSize();
        });

        if (_bitmap is null || width <= 0 || height <= 0)
        {
            context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
            return;
        }

        DisplayViewport viewport = DisplayViewport.Fit(
            Bounds.Size,
            width,
            height,
            squarePixels,
            CurrentRenderScaling);
        Rect target = viewport.ToRect();

        // The picture is letterboxed and scaled, so a host pixel is worth
        // less than a guest one. Recording the ratio here keeps it in step
        // with whatever is actually on screen, resizes included.
        //
        // The ratio is against the driver's coordinate range, not against the
        // picture: in text mode the mouse driver counts eight units per
        // character cell, so an 80x25 screen is 640x200 units while the
        // picture is 640x400 pixels. Scaling against the picture made the
        // pointer crawl downwards at half the speed it moved sideways.
        if (target.Width > 0 && target.Height > 0)
        {
            _mouseScaleX = _virtualWidth / target.Width;
            _mouseScaleY = _virtualHeight / target.Height;
        }

        // Letterbox the rest: black bars look like a real monitor, whereas
        // stretching to fill would distort the picture.
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        context.DrawImage(_bitmap, new Rect(0, 0, width, height), target);
    }

    /// <summary>Whether this active display mode uses square pixels.</summary>
    private static bool UsesSquarePixels(Aurora86.Core.Machine machine)
    {
        return machine.AuroraGpu?.ScanoutEnabled == true
            || machine.VideoRegisters.EffectiveMode.Layout == VgaMemoryLayout.HighColour;
    }

    private double CurrentRenderScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

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
        _frameUploads.Invalidate();
        _bitmapUploadPending = true;
    }

    private unsafe void CopyFramebuffer(ReadOnlySpan<uint> source, int width, int height)
    {
        if (_bitmap is null)
        {
            return;
        }

        FramebufferRowRange rows = _frameUploads.FindChangedRows(source, width, height);
        if (rows.RowCount == 0)
        {
            _bitmapUploadPending = false;
            return;
        }

        using ILockedFramebuffer locked = _bitmap.Lock();
        byte* firstRow = (byte*)locked.Address + checked(rows.FirstRow * locked.RowBytes);
        var destination = new Span<byte>(firstRow, checked(rows.RowCount * locked.RowBytes));
        _frameUploads.CopyRows(source, rows, destination, locked.RowBytes);
        _frameUploads.ConfirmUpload(source, rows);
        _bitmapUploadPending = false;
    }
}
