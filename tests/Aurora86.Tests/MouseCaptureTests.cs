using Aurora86.Gui.Emulation;
using Avalonia.Input;

namespace Aurora86.Tests;

/// <summary>
/// A pointer lock that records what was asked of it instead of touching the
/// real display. Hiding the host's cursor from a test suite would be a poor
/// joke on whoever ran it.
/// </summary>
internal sealed class FakePointerLock : IPointerLock
{
    public bool IsSupported { get; init; } = true;

    /// <summary>
    /// How many times the pointer has been taken and given back. Counting
    /// rather than flagging is deliberate: the platform call underneath is
    /// reference-counted, so an extra hide with no matching show leaves the
    /// user's cursor invisible system-wide. Counts catch that; a boolean
    /// cannot.
    /// </summary>
    public int Engaged { get; private set; }
    public int Released { get; private set; }

    public bool IsEngaged => Engaged > Released;

    /// <summary>Movement the fake platform will report next.</summary>
    public (double X, double Y) Motion { get; set; }

    public void Engage() => Engaged++;
    public void Release() => Released++;

    public (double X, double Y) TakeMotion()
    {
        (double x, double y) = Motion;
        Motion = (0, 0);
        return (x, y);
    }
}

/// <summary>
/// The rules for taking the pointer away from the host and giving it back.
///
/// These are worth testing apart from the window because they are the part
/// that is easy to get subtly wrong in a way nobody notices until the cursor
/// has disappeared for good: an unbalanced hide, or a release key that also
/// swallows Ctrl-C.
/// </summary>
public class MouseCaptureTests
{
    [Fact]
    public void ClickingTheScreenTakesThePointerAwayFromTheHost()
    {
        var pointerLock = new FakePointerLock();
        var capture = new MouseCapture(pointerLock);

        Assert.False(capture.IsCaptured);

        capture.Capture();

        Assert.True(capture.IsCaptured);
        Assert.True(pointerLock.IsEngaged);
    }

    [Fact]
    public void TappingTheReleaseKeyOnItsOwnGivesThePointerBack()
    {
        var pointerLock = new FakePointerLock();
        var capture = new MouseCapture(pointerLock);
        capture.Capture();

        capture.NotifyKeyDown(PhysicalKey.ControlLeft);
        Assert.True(capture.NotifyKeyUp(PhysicalKey.ControlLeft));

        Assert.False(capture.IsCaptured);
        Assert.False(pointerLock.IsEngaged);
    }

    [Fact]
    public void ControlCReachesTheGuestWithoutReleasingThePointer()
    {
        // This is the whole difficulty of choosing Control as the release
        // key, and the reason the gesture is recognised on the way up rather
        // than the way down. Ctrl-C is how a DOS user stops a running
        // program; a release key that fired on the Control-down would give
        // the pointer back every single time, and the user would find the
        // machine spitting the cursor out whenever they tried to interrupt
        // anything.
        var pointerLock = new FakePointerLock();
        var capture = new MouseCapture(pointerLock);
        capture.Capture();

        capture.NotifyKeyDown(PhysicalKey.ControlLeft);
        capture.NotifyKeyDown(PhysicalKey.C);
        capture.NotifyKeyUp(PhysicalKey.C);
        Assert.False(capture.NotifyKeyUp(PhysicalKey.ControlLeft));

        Assert.True(capture.IsCaptured);
        Assert.True(pointerLock.IsEngaged);
    }

    [Fact]
    public void ControlAltDeleteDoesNotReleaseEither()
    {
        // The three-finger salute has Control held throughout, and it is
        // released last as often as not. If the gesture only looked at which
        // key came up last, rebooting the guest would also throw the pointer
        // back at the host.
        var pointerLock = new FakePointerLock();
        var capture = new MouseCapture(pointerLock);
        capture.Capture();

        capture.NotifyKeyDown(PhysicalKey.ControlLeft);
        capture.NotifyKeyDown(PhysicalKey.AltLeft);
        capture.NotifyKeyDown(PhysicalKey.Delete);
        capture.NotifyKeyUp(PhysicalKey.Delete);
        capture.NotifyKeyUp(PhysicalKey.AltLeft);
        capture.NotifyKeyUp(PhysicalKey.ControlLeft);

        Assert.True(capture.IsCaptured);
    }

    [Fact]
    public void ARightControlDoesNotRelease()
    {
        // Only one of the two is the escape hatch, so that the other stays a
        // key the guest can use freely.
        var pointerLock = new FakePointerLock();
        var capture = new MouseCapture(pointerLock);
        capture.Capture();

        capture.NotifyKeyDown(PhysicalKey.ControlRight);
        capture.NotifyKeyUp(PhysicalKey.ControlRight);

        Assert.True(capture.IsCaptured);
    }

    [Fact]
    public void LosingFocusAlwaysGivesThePointerBack()
    {
        // The failure this prevents is the nastiest one available: switch to
        // another application while captured and the cursor is invisible
        // everywhere, with the key that would restore it going to somebody
        // else's window.
        var pointerLock = new FakePointerLock();
        var capture = new MouseCapture(pointerLock);
        capture.Capture();

        capture.NotifyWindowDeactivated();

        Assert.False(capture.IsCaptured);
        Assert.False(pointerLock.IsEngaged);
    }

    [Fact]
    public void TakingAndGivingBackAreBalancedHoweverOftenTheyAreAsked()
    {
        // The platform call underneath counts hides and shows. One hide too
        // many and the cursor never comes back -- not for this application,
        // for the whole desktop -- so a repeated capture must not stack up.
        var pointerLock = new FakePointerLock();
        var capture = new MouseCapture(pointerLock);

        capture.Capture();
        capture.Capture();
        capture.Capture();
        capture.Release();
        capture.Release();

        Assert.Equal(pointerLock.Engaged, pointerLock.Released);
        Assert.False(capture.IsCaptured);
    }

    [Fact]
    public void ThePointerIsNotTakenTwiceOverAFailedReleaseGesture()
    {
        // A Control tapped while not captured must not leave the state
        // machine thinking anything happened.
        var pointerLock = new FakePointerLock();
        var capture = new MouseCapture(pointerLock);

        capture.NotifyKeyDown(PhysicalKey.ControlLeft);
        Assert.False(capture.NotifyKeyUp(PhysicalKey.ControlLeft));
        Assert.Equal(0, pointerLock.Released);
    }
}
