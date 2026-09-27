using System;
using Avalonia.Input;

namespace Aurora386.Gui.Emulation;

/// <summary>
/// Decides when the guest owns the pointer, and how the release key is
/// recognised.
///
/// This is deliberately a plain object with no Avalonia control behind it,
/// because the rules are the fiddly part and they deserve to be tested on
/// their own. The control's job is only to feed it events and act on what it
/// says.
///
/// The rules, and why:
///
/// <b>Clicking the screen captures.</b> That is what every virtual machine
/// does, and it is the only gesture that is unambiguous -- a click on the
/// emulated screen is a click meant for the guest, so the guest should have
/// the pointer from then on.
///
/// <b>The left Control key alone releases.</b> This is VirtualBox's host key,
/// which makes it the one an experienced user will try first, and unlike
/// Command it is not swallowed by macOS before the application sees it. The
/// snag is that Control is a key the guest genuinely needs -- Ctrl-C, Ctrl-
/// Break, Ctrl-Alt-Del -- so it cannot simply be eaten. The resolution is the
/// same one VirtualBox uses: the key is passed to the guest on the way down
/// as normal, and capture is released on the way <i>up</i>, but only if no
/// other key was pressed while it was held. So Ctrl-C reaches the guest and
/// leaves capture alone, while a Control tapped by itself lets go.
/// </summary>
public sealed class MouseCapture
{
    private IPointerLock _lock;

    /// <summary>Raised whenever capture is taken or given back, so the interface can say so.</summary>
    public event Action<bool>? CapturedChanged;

    public MouseCapture(IPointerLock pointerLock) => _lock = pointerLock;

    /// <summary>
    /// Swaps the platform underneath, for tests.
    ///
    /// It replaces the lock rather than the capture object because whoever
    /// owns the capture has already subscribed to it -- the status bar, for
    /// one -- and handing them a different object would quietly orphan those
    /// subscriptions, which is exactly the sort of thing that makes a test
    /// pass while the real interface says nothing.
    /// </summary>
    internal void UsePointerLock(IPointerLock pointerLock)
    {
        Release();
        _lock = pointerLock;
    }

    /// <summary>Whether the guest currently owns the pointer.</summary>
    public bool IsCaptured { get; private set; }

    /// <summary>
    /// Whether the host can actually free the pointer from the screen. When
    /// false, capture still routes clicks and keys to the guest but the
    /// pointer stays visible and stops at the window edge; the interface says
    /// so rather than pretending.
    /// </summary>
    public bool IsUnbounded => _lock.IsSupported;

    /// <summary>
    /// The key that gives the pointer back, spelled the way it should appear
    /// on screen. Kept here rather than in the window so the message and the
    /// behaviour cannot drift apart.
    /// </summary>
    public const string ReleaseKeyName = "left Ctrl";

    public void Capture()
    {
        if (IsCaptured)
        {
            return;
        }

        IsCaptured = true;
        _lock.Engage();
        CapturedChanged?.Invoke(true);
    }

    public void Release()
    {
        if (!IsCaptured)
        {
            return;
        }

        IsCaptured = false;
        _releaseKeyHeld = false;
        _lock.Release();
        CapturedChanged?.Invoke(false);
    }

    /// <summary>
    /// How far the mouse moved, straight from the platform. Only meaningful
    /// while captured on a platform that supports it; see
    /// <see cref="IPointerLock.TakeMotion"/> for why the alternative -- the
    /// position carried by the event -- goes silently dead once captured.
    /// </summary>
    public (double X, double Y) TakeMotion() => _lock.TakeMotion();

    /// <summary>
    /// True while the left Control key is down and nothing else has been
    /// pressed since. This is what turns "Control was held as a modifier"
    /// into "Control was tapped on its own".
    /// </summary>
    private bool _releaseKeyHeld;

    /// <summary>
    /// Tells the capture about a key going down. Returns nothing and consumes
    /// nothing: the guest must see every key, including the release key, or
    /// the guest's own idea of which modifiers are held drifts out of step
    /// with reality.
    /// </summary>
    public void NotifyKeyDown(PhysicalKey key)
    {
        if (key == PhysicalKey.ControlLeft)
        {
            _releaseKeyHeld = true;
            return;
        }

        // Any other key while Control is down makes this a combination, not a
        // tap. Note that this fires even when the other key is released
        // first: once it has been part of a combination, the Control-up can
        // no longer mean "release".
        _releaseKeyHeld = false;
    }

    /// <summary>
    /// Tells the capture about a key coming up, and returns true if that was
    /// the gesture that freed the pointer.
    /// </summary>
    public bool NotifyKeyUp(PhysicalKey key)
    {
        if (key != PhysicalKey.ControlLeft)
        {
            return false;
        }

        bool wasTap = _releaseKeyHeld;
        _releaseKeyHeld = false;

        if (!wasTap || !IsCaptured)
        {
            return false;
        }

        Release();
        return true;
    }

    /// <summary>
    /// Called when the window stops being the active one.
    ///
    /// Losing focus with the pointer still hidden is the worst failure this
    /// feature has: the user switches to another application and finds no
    /// cursor anywhere, with no obvious way to get it back because the key
    /// that would release it goes to whatever window now has focus. So focus
    /// loss always releases, unconditionally.
    /// </summary>
    public void NotifyWindowDeactivated() => Release();
}
