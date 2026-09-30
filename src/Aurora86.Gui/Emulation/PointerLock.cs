using System;
using System.Runtime.InteropServices;

namespace Aurora86.Gui.Emulation;

/// <summary>
/// Takes the host's pointer away from the host, so the guest can have it.
///
/// This is the part of mouse capture that no toolkit provides. Avalonia's
/// <c>Pointer.Capture</c> routes events to one control, which is a different
/// thing entirely: the pointer still belongs to the desktop, it still stops
/// at the edge of the screen, and it still draws an arrow on top of the
/// emulated one. What is needed is for the host to stop moving its pointer
/// at all while continuing to report how far the mouse travelled -- which is
/// precisely what a real machine's mouse does, and precisely what no
/// cross-platform API exposes.
///
/// So this is an interface with one implementation per platform, and a
/// do-nothing fallback. The fallback is not a failure case to be apologised
/// for: on a platform we have no code for, the emulator degrades to the
/// bounded relative motion it had before, which works for everything except
/// pushing the guest pointer past the window edge.
/// </summary>
public interface IPointerLock
{
    /// <summary>
    /// Whether this implementation can actually do it. False means the caller
    /// should still capture -- the guest should still get the clicks and the
    /// keyboard -- but must expect the pointer to stop at the window edge.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Hides the host pointer and stops it from moving, while the underlying
    /// device keeps reporting motion. Idempotent.
    /// </summary>
    void Engage();

    /// <summary>
    /// Gives the pointer back. Idempotent, and it must be safe to call from a
    /// shutdown path: a process that exits with the pointer still hidden
    /// leaves the user with an invisible cursor and no way to get it back
    /// short of logging out.
    /// </summary>
    void Release();

    /// <summary>
    /// How far the mouse has moved since this was last asked, in host pixels.
    ///
    /// This exists because of a trap that is invisible until it bites. Once
    /// the pointer is unhooked from the mouse, the position carried by the
    /// host's mouse events <i>stops changing</i> -- it is the frozen arrow's
    /// position, not the mouse's. Subtracting successive positions, which is
    /// how the uncaptured path works and is the obvious thing to write,
    /// therefore reads zero movement forever: capture would appear to break
    /// the mouse completely. The only truthful source while captured is the
    /// platform's own delta, which is what this returns.
    ///
    /// Returns (0, 0) when unsupported, so a caller must not use this unless
    /// <see cref="IsSupported"/> said yes.
    /// </summary>
    (double X, double Y) TakeMotion();
}

/// <summary>
/// Chooses the right lock for the machine we are running on.
/// </summary>
public static class PointerLockFactory
{
    public static IPointerLock Create()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacPointerLock();
        }
        return new NullPointerLock();
    }
}

/// <summary>
/// What we fall back to where there is no implementation: capture still
/// works, the pointer merely stays visible and bounded.
/// </summary>
public sealed class NullPointerLock : IPointerLock
{
    public bool IsSupported => false;
    public void Engage() { }
    public void Release() { }
    public (double X, double Y) TakeMotion() => (0, 0);
}

/// <summary>
/// Pointer capture on macOS, through CoreGraphics.
///
/// Two calls do the whole job, and the interesting one is
/// <c>CGAssociateMouseAndMouseCursorPosition(false)</c>. It severs the link
/// between the mouse and the arrow on screen: the arrow freezes where it is,
/// while the mouse keeps generating events with real deltas in them. That is
/// exactly the hardware relationship a PC has -- the device counts, something
/// else decides where the pointer is -- and it is why the guest pointer can
/// then travel past the window edge, because there is no edge any more.
///
/// A warning that is easy to learn the hard way: with the association cut,
/// the position macOS reports in a mouse event stops changing. Only the
/// deltas are meaningful. Code that subtracts successive positions -- which
/// is the obvious way to write it, and is what the uncaptured path here does
/// -- silently reads zero movement forever. That is why capture must switch
/// the frontend over to reading deltas rather than positions.
///
/// <c>CGDisplayHideCursor</c> is reference-counted by the system: every hide
/// needs its matching show, and one too few leaves the cursor invisible for
/// every application, not just this one. Hence the flag and the idempotence.
/// </summary>
public sealed class MacPointerLock : IPointerLock
{
    private const string CoreGraphics =
        "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    [DllImport(CoreGraphics)]
    private static extern int CGAssociateMouseAndMouseCursorPosition(bool connected);

    [DllImport(CoreGraphics)]
    private static extern int CGDisplayHideCursor(uint display);

    [DllImport(CoreGraphics)]
    private static extern int CGDisplayShowCursor(uint display);

    /// <summary>
    /// The movement the mouse reported for the event being handled right now.
    /// It is a snapshot taken by the window server when it built the event,
    /// not a running total, so it must be read while handling that event and
    /// reading it twice for one event gives the same answer twice.
    /// </summary>
    [DllImport(CoreGraphics)]
    private static extern void CGGetLastMouseDelta(out int deltaX, out int deltaY);

    /// <summary>The main display, which is what display id zero means here.</summary>
    private const uint MainDisplay = 0;

    private bool _engaged;

    public bool IsSupported => true;

    public void Engage()
    {
        if (_engaged)
        {
            return;
        }

        // Order matters only in that both must happen; but hiding first means
        // that if the association call throws -- a sandbox denying it, say --
        // we have already told the flag nothing, and Release stays a no-op
        // rather than issuing an unbalanced show.
        CGDisplayHideCursor(MainDisplay);
        CGAssociateMouseAndMouseCursorPosition(false);
        _engaged = true;
    }

    public void Release()
    {
        if (!_engaged)
        {
            return;
        }

        _engaged = false;
        CGAssociateMouseAndMouseCursorPosition(true);
        CGDisplayShowCursor(MainDisplay);
    }

    public (double X, double Y) TakeMotion()
    {
        CGGetLastMouseDelta(out int x, out int y);
        return (x, y);
    }
}
