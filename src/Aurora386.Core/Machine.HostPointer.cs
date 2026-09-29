using Aurora386.Core.Devices.Input;

namespace Aurora386.Core;

/// <summary>
/// Where the host's mouse input goes: to the VMware-compatible absolute
/// pointer once a guest driver has asked for it (docs/VMWARE-BACKDOOR.md
/// section 4), otherwise to the USB mouse when a USB driver owns it, and
/// otherwise to the serial or PS/2 port as always.
///
/// Front ends deliver relative motion. The absolute pointer is fed by adding
/// that motion to a position on the displayed picture and scaling it to the
/// 0-FFFFh range the protocol uses; a front end that knows the host
/// cursor's own position can later deliver it directly (plan item M11).
/// </summary>
public sealed partial class Machine
{
    /// <summary>The largest coordinate of the absolute pointer, at the right and bottom edges.</summary>
    private const int AbsolutePointerMaximum = 0xFFFF;

    private Func<int, int, bool>? _usbMotionRoute;
    private Func<MouseButtons, bool, bool>? _usbButtonRoute;

    // The absolute pointer's position in displayed pixels, and its buttons.
    private int _absolutePointerX;
    private int _absolutePointerY;
    private MouseButtons _absolutePointerButtons;

    /// <summary>The buttons the host holds right now, whichever route they went to.</summary>
    private MouseButtons _hostButtons;

    /// <summary>The buttons the ordinary route (USB, or the PS/2 and serial mouse) was last told are held.</summary>
    private MouseButtons _ordinaryRouteButtons;

    /// <summary>Whether the last host event went to the absolute pointer, to notice the route changing.</summary>
    private bool _absoluteRouteWasActive;

    /// <summary>Set while a route change replays button releases into the ordinary route.</summary>
    private bool _replayingButtonReleases;

    /// <summary>Installs the one pair of interceptors every route goes through.</summary>
    private void InstallMouseRoutes()
    {
        Mouse.MotionInterceptor = RouteMotion;
        Mouse.ButtonInterceptor = RouteButton;
    }

    private bool AbsolutePointerActive => Backdoor.Enabled && Backdoor.PointerEnabled && Backdoor.PointerAbsolute;

    private bool RouteMotion(int deltaX, int deltaY)
    {
        NoticeRouteChange();
        if (AbsolutePointerActive)
        {
            int widthLimit = Math.Max(Display.Width, 1) - 1;
            int heightLimit = Math.Max(Display.Height, 1) - 1;
            _absolutePointerX = Math.Clamp(_absolutePointerX + deltaX, 0, widthLimit);
            _absolutePointerY = Math.Clamp(_absolutePointerY + deltaY, 0, heightLimit);
            QueueAbsolutePointer(0);
            return true;
        }

        return _usbMotionRoute?.Invoke(deltaX, deltaY) == true;
    }

    private bool RouteButton(MouseButtons button, bool pressed)
    {
        if (_replayingButtonReleases)
        {
            return false;
        }

        if (pressed)
        {
            _hostButtons |= button;
        }
        else
        {
            _hostButtons &= ~button;
        }

        NoticeRouteChange();
        if (AbsolutePointerActive)
        {
            if (pressed)
            {
                _absolutePointerButtons |= button;
            }
            else
            {
                _absolutePointerButtons &= ~button;
            }

            QueueAbsolutePointer(0);
            return true;
        }

        if (pressed)
        {
            _ordinaryRouteButtons |= button;
        }
        else
        {
            _ordinaryRouteButtons &= ~button;
        }

        return _usbButtonRoute?.Invoke(button, pressed) == true;
    }

    /// <summary>
    /// Keeps the buttons consistent when events start going to another route:
    /// a button pressed on one route and released on the other would
    /// otherwise stay held on the first. Entering the absolute route takes
    /// over the buttons the ordinary mouse holds and starts at the centre of
    /// the picture, where a guest cursor usually starts, rather than jumping
    /// to the top-left corner; leaving it releases on the ordinary mouse
    /// whatever was let go while the absolute route owned the buttons.
    /// </summary>
    private void NoticeRouteChange()
    {
        bool active = AbsolutePointerActive;
        if (active == _absoluteRouteWasActive)
        {
            return;
        }

        _absoluteRouteWasActive = active;
        if (active)
        {
            _absolutePointerButtons = _hostButtons;
            _absolutePointerX = Math.Max(Display.Width, 1) / 2;
            _absolutePointerY = Math.Max(Display.Height, 1) / 2;
            return;
        }

        MouseButtons releasedMeanwhile = _ordinaryRouteButtons & ~_hostButtons;
        foreach (MouseButtons button in new[] { MouseButtons.Left, MouseButtons.Right, MouseButtons.Middle })
        {
            if ((releasedMeanwhile & button) != 0)
            {
                ReleaseOnOrdinaryRoute(button);
            }
        }

        _absolutePointerButtons = MouseButtons.None;
    }

    /// <summary>
    /// Releases one button on whichever ordinary route owns the mouse: the
    /// USB mouse when its driver is active, the PS/2 or serial mouse
    /// otherwise -- the same choice a live release would have taken.
    /// </summary>
    private void ReleaseOnOrdinaryRoute(MouseButtons button)
    {
        _ordinaryRouteButtons &= ~button;
        if (_usbButtonRoute?.Invoke(button, false) == true)
        {
            return;
        }

        _replayingButtonReleases = true;
        try
        {
            Mouse.SetButton(button, false);
        }
        finally
        {
            _replayingButtonReleases = false;
        }
    }

    /// <summary>
    /// True while a guest driver has asked for absolute pointer packets. A
    /// front end reads it to stop capturing the host mouse and deliver the
    /// host cursor's own position through <see cref="PointAbsolute"/>, which
    /// is what lets the pointer leave and re-enter the window freely.
    /// </summary>
    public bool AbsolutePointerRequested => AbsolutePointerActive;

    /// <summary>
    /// Places the absolute pointer at a pixel of the displayed picture, from
    /// a front end that knows where the host cursor is. Ignored unless a
    /// guest driver has asked for absolute packets; coordinates outside the
    /// picture are clamped to its edge.
    /// </summary>
    public void PointAbsolute(int x, int y)
    {
        NoticeRouteChange();
        if (!AbsolutePointerActive)
        {
            return;
        }

        int widthLimit = Math.Max(Display.Width, 1) - 1;
        int heightLimit = Math.Max(Display.Height, 1) - 1;
        int clampedX = Math.Clamp(x, 0, widthLimit);
        int clampedY = Math.Clamp(y, 0, heightLimit);
        if (clampedX == _absolutePointerX && clampedY == _absolutePointerY)
        {
            return;
        }

        _absolutePointerX = clampedX;
        _absolutePointerY = clampedY;
        QueueAbsolutePointer(0);
    }

    /// <summary>Scales the pixel position to 0-FFFFh across the picture and queues one packet.</summary>
    private void QueueAbsolutePointer(int wheel)
    {
        int widthLimit = Math.Max(Display.Width - 1, 1);
        int heightLimit = Math.Max(Display.Height - 1, 1);
        int x = (int)((long)_absolutePointerX * AbsolutePointerMaximum / widthLimit);
        int y = (int)((long)_absolutePointerY * AbsolutePointerMaximum / heightLimit);
        Backdoor.QueueAbsolutePointer(
            Math.Min(x, AbsolutePointerMaximum),
            Math.Min(y, AbsolutePointerMaximum),
            _absolutePointerButtons,
            wheel);
    }
}
