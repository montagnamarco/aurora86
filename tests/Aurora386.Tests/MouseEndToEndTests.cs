using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Devices.Host;
using Aurora386.Core.Devices.Input;
using Aurora386.Core.Devices.Interrupts;
using Aurora386.Core.Devices.Timing;
using Aurora386.Core.Devices.System;
using Aurora386.Gui.Controls;
using Aurora386.Gui.ViewModels;
using Aurora386.Gui.Views;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;

namespace Aurora386.Tests;

/// <summary>
/// The whole mouse path, driven the way a user drives it: events raised on
/// the real window, and the answer read out of the emulated hardware.
///
/// The other mouse tests in this project start halfway along -- they call
/// <c>MouseState.Move</c>, or read packets out of a device built by hand.
/// Every one of them passed while the frontend was still throwing the
/// movement away at the window's edge, because the fault lay in the part they
/// skipped. So these press the buttons and push the mouse on the actual
/// window, and look at what came out of port 0x60.
/// </summary>
[Collection(HeadlessAppCollection.Name)]
public class MouseEndToEndTests
{
    private readonly HeadlessApp _app;

    public MouseEndToEndTests(HeadlessApp app) => _app = app;

    /// <summary>
    /// A window with a machine behind it and a fake platform underneath, so a
    /// test run never hides the cursor of whoever started it.
    /// </summary>
    private (MainWindow Window, MainWindowViewModel Model, Machine Machine,
             FakePointerLock Lock) Open(bool unbounded = true) => _app.Invoke(() =>
    {
        var model = new MainWindowViewModel();
        var window = new MainWindow { DataContext = model };
        window.Show();

        var pointerLock = new FakePointerLock { IsSupported = unbounded };
        window.Screen.Capture.UsePointerLock(pointerLock);

        // The headless platform lays out on demand, and the pointer has to
        // have somewhere to land: without this the emulated screen has no
        // size and the hit test under the pointer finds nothing.
        window.Width = 800;
        window.Height = 600;
        Dispatcher.UIThread.RunJobs();

        Machine? machine = null;
        model.Session.WithMachine(m => machine = m);
        Assert.NotNull(machine);

        // The model comes back alongside the window because reading
        // DataContext off the UI thread is not allowed, and every assertion
        // below happens on the test's own thread.
        return (window, model, machine!, pointerLock);
    });

    /// <summary>
    /// Reads whatever the emulated PS/2 mouse has produced.
    ///
    /// The 8042 shares one data port between the keyboard and the mouse, and
    /// bit 5 of the status register is the only thing that says which the
    /// waiting byte belongs to. So the read has to check it, exactly as the
    /// guest's own interrupt handler does -- taking every byte regardless
    /// would mix scancodes into the packet.
    /// </summary>
    private static List<byte> DrainMousePackets(Machine machine)
    {
        var bytes = new List<byte>();

        while ((machine.Io.ReadByte(0x64) & 0x01) != 0)
        {
            bool fromMouse = (machine.Io.ReadByte(0x64) & 0x20) != 0;
            byte value = machine.Io.ReadByte(0x60);
            if (fromMouse) bytes.Add(value);
        }

        return bytes;
    }

    /// <summary>
    /// Puts the PS/2 mouse into the state a driver leaves it in: reporting.
    /// It powers up silent on purpose, so without this nothing at all comes
    /// out and every test below would pass by saying nothing happened.
    /// </summary>
    private static void EnableReporting(Machine machine)
    {
        // The port under test is the PS/2 one; the machine defaults to COM1.
        machine.MouseInterface = MouseInterface.Ps2;

        // Exactly what a driver does: open the auxiliary port, allow its
        // interrupt, then tell the mouse to start reporting.
        machine.Io.WriteByte(0x64, 0xA8);
        machine.Io.WriteByte(0x64, 0x60);
        machine.Io.WriteByte(0x60, 0x47);
        machine.Io.WriteByte(0x64, 0xD4);
        machine.Io.WriteByte(0x60, 0xF4);

        machine.Keyboard.PollMouse();
        DrainMousePackets(machine);
    }

    /// <summary>
    /// Moves the host pointer, through the headless platform's own input
    /// pipeline rather than by synthesising a routed event.
    ///
    /// The difference matters: this goes through hit testing, so the event
    /// only reaches the emulated screen if the screen really is under the
    /// pointer and really is hit-testable. Raising the event on the control
    /// directly would pass even if the control had stopped receiving the
    /// mouse altogether -- which is one of the ways this can break.
    /// </summary>
    private void Move(MainWindow window, Point to) =>
        _app.Invoke(() => window.MouseMove(to));

    /// <summary>
    /// The vertical band the emulated screen occupies in the window.
    ///
    /// It is not the whole window: the toolbar takes the top of it and the
    /// status bar the bottom. A test that moves the pointer to y=10 is
    /// pointing at the Start button, the screen never hears about it, and the
    /// test then proves nothing while appearing to pass -- which is how the
    /// first draft of the release test below fooled itself.
    /// </summary>
    private const double InsideScreenTop = 120;
    private const double InsideScreenBottom = 500;

    // --- capture -------------------------------------------------------------

    [Fact]
    public void ClickingTheEmulatedScreenTakesThePointer()
    {
        (MainWindow window, _, _, FakePointerLock pointerLock) = Open();

        Assert.False(window.Screen.Capture.IsCaptured);

        ClickLeft(window);

        Assert.True(window.Screen.Capture.IsCaptured);
        Assert.True(pointerLock.IsEngaged);
    }

    [Fact]
    public void TappingLeftControlOnTheWindowGivesThePointerBack()
    {
        // The escape hatch, exercised through the window rather than through
        // the state machine on its own: a rule nothing calls is a rule that
        // does not exist, and the window is where the key events actually
        // arrive.
        (MainWindow window, _, _, FakePointerLock pointerLock) = Open();
        ClickLeft(window);

        Key(window, PhysicalKey.ControlLeft, down: true);
        Key(window, PhysicalKey.ControlLeft, down: false);

        Assert.False(window.Screen.Capture.IsCaptured);
        Assert.False(pointerLock.IsEngaged);
    }

    [Fact]
    public void TheStatusBarSaysThePointerIsCapturedAndHowToGetItBack()
    {
        // Without this the user has no clue at all: the cursor simply
        // vanishes, and nothing on screen changes.
        (MainWindow window, MainWindowViewModel model, _, _) = Open();

        ClickLeft(window);

        Assert.True(model.MouseCaptured);
        Assert.Contains("Ctrl", model.MouseCaptureText);

        Key(window, PhysicalKey.ControlLeft, down: true);
        Key(window, PhysicalKey.ControlLeft, down: false);

        Assert.False(model.MouseCaptured);
    }

    [Fact]
    public void TheNoticeAdmitsWhenThePointerIsStillBoundedByTheWindow()
    {
        // On a platform with no lock of its own, capture still routes clicks
        // and keys but the pointer stops at the window edge. Saying "mouse
        // captured" and nothing else would leave the user pushing at that
        // edge wondering what was broken.
        (MainWindow window, MainWindowViewModel model, _, _) = Open(unbounded: false);

        ClickLeft(window);

        Assert.Contains("bounded", model.MouseCaptureText);
    }

    [Fact]
    public void ControlCStillReachesTheGuestWhileCaptured()
    {
        // The release key is a key the guest needs. If capture ate it, there
        // would be no way to interrupt a running DOS program without first
        // giving the pointer back.
        (MainWindow window, _, Machine machine, _) = Open();
        ClickLeft(window);

        List<byte> codes = _app.Invoke(() =>
        {
            DrainAll(machine);

            RaiseKey(window, PhysicalKey.ControlLeft, down: true);
            RaiseKey(window, PhysicalKey.C, down: true);

            return DrainAll(machine);
        });

        Assert.Contains((byte)0x1D, codes);   // left Control's position
        Assert.Contains((byte)0x2E, codes);   // C's position
        Assert.True(window.Screen.Capture.IsCaptured);
    }

    // --- movement ------------------------------------------------------------

    [Fact]
    public void MovingInsideTheWindowReachesTheGuestUncaptured()
    {
        // The behaviour that was already there, kept honest: without capture
        // the movement still has to arrive, because clicking a button in a
        // guest program should not require capturing anything first.
        (MainWindow window, _, Machine machine, _) = Open();
        EnableReporting(machine);

        Move(window, new Point(100, InsideScreenTop));
        Move(window, new Point(160, InsideScreenTop));

        Assert.NotEqual(0, HorizontalMotionDelivered(machine));
    }

    [Fact]
    public void OnceCapturedTheMovementComesFromThePlatformAndNotFromThePosition()
    {
        // This is the test that catches the trap the whole feature turns on.
        // Once the host's pointer is unhooked from the mouse, the position
        // carried by a mouse event stops changing -- it belongs to a frozen
        // arrow. Code that subtracts successive positions therefore reads
        // zero movement forever, and capture, which is supposed to remove the
        // limit, instead kills the mouse outright.
        //
        // So the event below reports the *same* position twice over, exactly
        // as the platform would, while the platform's own delta says the
        // mouse travelled. The guest must be told it travelled.
        (MainWindow window, _, Machine machine, FakePointerLock pointerLock) = Open();
        EnableReporting(machine);

        ClickLeft(window);
        Move(window, new Point(400, 300));
        DrainMousePackets(machine);

        pointerLock.Motion = (60, 0);
        Move(window, new Point(400, 300));

        Assert.NotEqual(0, HorizontalMotionDelivered(machine));
    }

    [Fact]
    public void CapturedMovementIsNotStoppedByTheEdgeOfTheWindow()
    {
        // The complaint this feature exists to answer. The host pointer is
        // pinned at one corner and cannot move any further, yet the mouse
        // keeps being pushed; the guest must keep receiving movement.
        (MainWindow window, _, Machine machine, FakePointerLock pointerLock) = Open();
        EnableReporting(machine);

        ClickLeft(window);

        // Pinned in the corner: the reported position never changes again.
        var corner = new Point(1, InsideScreenTop);
        Move(window, corner);
        DrainMousePackets(machine);

        int total = 0;
        for (int push = 0; push < 5; push++)
        {
            pointerLock.Motion = (40, 0);
            Move(window, corner);
            total += HorizontalMotionDelivered(machine);
        }

        Assert.True(total > 0,
            "the guest stopped receiving movement once the host pointer hit the edge");
    }

    [Fact]
    public void ReleasingThePointerDoesNotFlingTheGuestPointerAcrossTheScreen()
    {
        // While captured the remembered position is meaningless -- it is the
        // frozen arrow's. Keeping it and subtracting from it after release
        // would turn the first real move into a leap of however far the mouse
        // wandered in the meantime.
        (MainWindow window, _, Machine machine, FakePointerLock pointerLock) = Open();
        EnableReporting(machine);

        // Establish a reference point on the left of the emulated screen
        // while the pointer still belongs to the host.
        Move(window, new Point(60, InsideScreenTop));
        Move(window, new Point(70, InsideScreenTop));
        HorizontalMotionDelivered(machine);

        ClickLeft(window);

        // Captured, so the mouse is reported as still while the host's own
        // pointer is dragged right across the window. That is what the real
        // platform does once the two are unhooked: the reported position
        // wanders and means nothing.
        pointerLock.Motion = (0, 0);
        Move(window, new Point(740, InsideScreenBottom));
        HorizontalMotionDelivered(machine);

        Key(window, PhysicalKey.ControlLeft, down: true);
        Key(window, PhysicalKey.ControlLeft, down: false);
        HorizontalMotionDelivered(machine);

        // The first move after release is a reference point, not a delta. Had
        // the position from before capture been kept, this would be reported
        // as the whole journey from x=70 to x=730 instead of a small nudge.
        Move(window, new Point(730, InsideScreenBottom));
        int first = Math.Abs(HorizontalMotionDelivered(machine));

        Assert.True(first < 100,
            $"the guest was flung {first} counts by the first move after release");
    }

    // --- which port the mouse is plugged into --------------------------------

    [Fact]
    public void ChoosingTheSerialMouseStopsFeedingThePs2Port()
    {
        // The selection has to reach the running machine, and it has to mean
        // something: a mouse cannot be in two sockets at once. Feeding both
        // and letting the guest pick would look like it worked, right up to
        // the moment a program probes the PS/2 port it was told is empty.
        (MainWindow window, MainWindowViewModel model, Machine machine, _) = Open();
        EnableReporting(machine);

        _app.Invoke(() => model.MouseInterface = MouseInterface.Serial);

        Move(window, new Point(100, InsideScreenTop));
        Move(window, new Point(300, 200));

        Assert.Equal(0, HorizontalMotionDelivered(machine));
    }

    [Fact]
    public void ChoosingNoMouseStopsTheMovementAltogether()
    {
        (MainWindow window, MainWindowViewModel model, Machine machine, _) = Open();
        EnableReporting(machine);

        _app.Invoke(() => model.MouseInterface = MouseInterface.None);

        Move(window, new Point(100, InsideScreenTop));
        Move(window, new Point(300, 200));
        ClickLeft(window);

        Assert.Equal(0, HorizontalMotionDelivered(machine));

        // The PS/2 port being silent is not enough to prove it on its own:
        // it would also be silent if the movement had merely been misrouted
        // to the serial port. INT 33h is the interface that is fed whichever
        // port is fitted, so it is the one that can tell "unplugged" apart
        // from "plugged in somewhere else".
        (int driverX, int driverY) = machine.Mouse.TakeDriverMotion();
        Assert.Equal(0, driverX);
        Assert.Equal(0, driverY);

        // And the click has to be missing too. A mouse that cannot move but
        // can still click is worse than no mouse at all: a program would act
        // on the click at whatever stale position it last believed in.
        Assert.Equal(MouseButtons.None, machine.Mouse.Buttons);
    }

    [Fact]
    public void SwitchingBackToThePs2PortStartsFeedingItAgain()
    {
        // Changing the choice while the machine runs is the point of putting
        // it in a menu rather than in a settings file.
        (MainWindow window, MainWindowViewModel model, Machine machine, _) = Open();
        EnableReporting(machine);

        _app.Invoke(() => model.MouseInterface = MouseInterface.Serial);
        Move(window, new Point(100, InsideScreenTop));
        Move(window, new Point(300, 200));
        DrainMousePackets(machine);

        _app.Invoke(() => model.MouseInterface = MouseInterface.Ps2);
        Move(window, new Point(300, 200));
        Move(window, new Point(500, 300));

        Assert.NotEqual(0, HorizontalMotionDelivered(machine));
    }

    [Fact]
    public void TimeSpentOnTheOtherPortIsNotFlushedIntoThePs2OneOnSwitching()
    {
        // The tempting shortcut is to keep filling every accumulator and
        // simply not read the unselected ones. It looks identical until the
        // user switches: the newly selected port then hands over everything
        // that happened while it was unplugged, and the guest pointer leaps
        // into a corner. What a port did not receive, it never had.
        (MainWindow window, MainWindowViewModel model, Machine machine, _) = Open();
        EnableReporting(machine);

        _app.Invoke(() => model.MouseInterface = MouseInterface.Serial);

        // A long journey while the PS/2 port is unplugged.
        Move(window, new Point(10, InsideScreenTop));
        for (int step = 1; step <= 20; step++)
            Move(window, new Point(10 + step * 30, InsideScreenTop + step * 15));

        _app.Invoke(() => model.MouseInterface = MouseInterface.Ps2);

        // Nothing has moved since the switch, so whatever packet the port
        // produces must carry no movement at all. The assertion is about the
        // counts rather than about there being no packet: an enabled mouse
        // that is polled reports its buttons whether it moved or not, which
        // is correct and is not what is being examined here.
        Assert.Equal(0, HorizontalMotionDelivered(machine));
    }

    [Fact]
    public void TheChosenPortSurvivesAReset()
    {
        // A reset builds a brand-new machine, which comes up with the default
        // port. The choice belongs to the emulator's settings rather than to
        // the machine, so it has to be reapplied -- otherwise the serial
        // mouse someone selected because their program needed it quietly
        // becomes a PS/2 one the moment they press Reset, and the mouse stops
        // working for a reason nothing on screen explains.
        (_, MainWindowViewModel model, _, _) = Open();

        _app.Invoke(() => model.MouseInterface = MouseInterface.Serial);
        _app.Invoke(() => model.ResetCommand.Execute(null));

        MouseInterface afterReset = model.Session.WithMachine(
            machine => machine.MouseInterface);

        Assert.Equal(MouseInterface.Serial, afterReset);
    }

    // --- the VMware-compatible absolute pointer -------------------------------
    //
    // A guest that has loaded VMware Tools' mouse driver (or Linux's
    // vmmouse) no longer wants deltas: it wants to know exactly where the
    // host cursor is, in the 0-FFFFh range docs/VMWARE-BACKDOOR.md section 4
    // describes. Once that driver is active there is nothing left for
    // capture to do -- the pointer already tracks the host exactly, without
    // ever needing to be hidden or leave the window -- so the front end must
    // stop capturing and feed the cursor's own position instead.

    [Fact]
    public void MovingToTheCentreOfTheScreenFeedsTheAbsolutePointer()
    {
        (MainWindow window, _, Machine machine, _) = Open();

        EnableAbsolutePointer(machine);

        // The picture is always centred within the screen control's own
        // bounds -- FitToMonitorAspect shrinks it symmetrically -- so the
        // centre of the drawn picture is the centre of the control, whatever
        // its aspect ratio ends up being.
        Point centre = TranslateToWindow(window, new Point(
            window.Screen.Bounds.Width / 2, window.Screen.Bounds.Height / 2));

        // Machine.HostPointer.cs starts a freshly entered absolute route at
        // the centre of the picture, on the theory that is roughly where a
        // guest cursor already is -- see NoticeRouteChange's own summary. A
        // move that lands exactly there first would then match that default
        // and change nothing, queueing no packet at all. Moving off-centre
        // first, and draining that packet, gives the move to the centre
        // something to actually change from.
        Move(window, TranslateToWindow(window, new Point(20, 20)));
        DrainAbsolutePointerPacket(machine);

        Move(window, centre);

        (uint queueLength, int x, int y) = DrainAbsolutePointerPacket(machine);

        // One packet is four dwords (status/buttons, X, Y, wheel); STATUS
        // reports the queue in dwords, which is what a driver checks before
        // asking DATA for a whole packet.
        Assert.Equal(4u, queueLength);

        // Centre of the picture scales to about the middle of the 0-FFFFh
        // range; "about" because a picture a few hundred pixels wide can
        // never land on FFFFh's exact midpoint.
        Assert.InRange(x, 0x7000, 0x9000);
        Assert.InRange(y, 0x7000, 0x9000);
    }

    [Fact]
    public void ClickingWhileTheAbsolutePointerIsActiveDoesNotCaptureTheMouse()
    {
        (MainWindow window, _, Machine machine, _) = Open();

        EnableAbsolutePointer(machine);

        ClickLeft(window);

        Assert.False(window.Screen.Capture.IsCaptured);
    }

    /// <summary>
    /// A click with no move before it -- a window activated by the click
    /// itself, a pen or touch tap -- lands where it was made, not wherever
    /// the absolute pointer last was: the click is positioned first.
    /// </summary>
    [Fact]
    public void AClickInAbsoluteModeLandsWhereItWasMade()
    {
        const uint LeftButtonBit = 0x20;
        (MainWindow window, _, Machine machine, _) = Open();
        EnableAbsolutePointer(machine);

        Point nearTopLeft = TranslateToWindow(window, new Point(20, 20));
        _app.Invoke(() => window.MouseDown(nearTopLeft, MouseButton.Left));

        uint lastStatus = 0;
        int lastX = -1;
        machine.Backdoor.Enabled = true;
        while (true)
        {
            CpuState status = CallBackdoor(machine, 40, 0);
            if (status.Eax == 0)
            {
                break;
            }

            CpuState packet = CallBackdoor(machine, 39, 4);
            lastStatus = packet.Eax;
            lastX = (int)packet.Ebx;
        }

        Assert.Equal(LeftButtonBit, lastStatus & LeftButtonBit);
        Assert.InRange(lastX, 0, 0x2000);
    }

    [Fact]
    public void RequestingTheAbsolutePointerReleasesACaptureAlreadyHeld()
    {
        // A guest can load its VMware-aware driver after the user has
        // already clicked the screen and taken capture the ordinary way.
        // The next pointer event has to notice and let go, or the host
        // pointer would stay hidden for a route nothing is reading any more.
        (MainWindow window, _, Machine machine, _) = Open();

        ClickLeft(window);
        Assert.True(window.Screen.Capture.IsCaptured);

        EnableAbsolutePointer(machine);
        Move(window, new Point(400, 300));

        Assert.False(window.Screen.Capture.IsCaptured);
    }

    /// <summary>
    /// Enables the absolute pointer the way Linux's vmmouse driver does:
    /// ENABLE, read the version dword back, REQUEST_ABSOLUTE
    /// (docs/VMWARE-BACKDOOR.md section 4).
    /// </summary>
    private static void EnableAbsolutePointer(Machine machine)
    {
        const uint AbsolutePointerCommand = 41;
        const uint AbsolutePointerData = 39;
        const uint EnableSubcommand = 0x45414552;
        const uint RequestAbsoluteSubcommand = 0x53424152;

        machine.Backdoor.Enabled = true;
        CallBackdoor(machine, AbsolutePointerCommand, EnableSubcommand);
        CpuState version = CallBackdoor(machine, AbsolutePointerData, 1);
        Assert.Equal(0x3442554Au, version.Eax);
        CallBackdoor(machine, AbsolutePointerCommand, RequestAbsoluteSubcommand);
    }

    /// <summary>
    /// Reads the queue length from ABSPOINTER_STATUS and, when a whole
    /// packet is waiting, dequeues it through ABSPOINTER_DATA. X and Y come
    /// back from EBX and ECX -- see VmwareBackdoor.Pointer.cs's
    /// HandlePointerData for why those are the second and third dwords of a
    /// packet.
    /// </summary>
    private static (uint QueueLength, int X, int Y) DrainAbsolutePointerPacket(Machine machine)
    {
        const uint AbsolutePointerStatus = 40;
        const uint AbsolutePointerData = 39;
        const uint WholePacket = 4;

        CpuState status = CallBackdoor(machine, AbsolutePointerStatus, 0);
        CpuState data = CallBackdoor(machine, AbsolutePointerData, WholePacket);

        return (status.Eax, (int)(ushort)data.Ebx, (int)(ushort)data.Ecx);
    }

    /// <summary>
    /// Converts a point in the screen control's own coordinates to the
    /// window's, the way <see cref="Move"/> expects it: raised on the
    /// window's own pointer pipeline, not on the control directly.
    /// </summary>
    private Point TranslateToWindow(MainWindow window, Point pointOnScreen) => _app.Invoke(() =>
    {
        Point? translated = window.Screen.TranslatePoint(pointOnScreen, window);
        Assert.NotNull(translated);
        return translated!.Value;
    });

    /// <summary>One backdoor call made directly on the device, with EBX as its argument.</summary>
    private static CpuState CallBackdoor(Machine machine, uint command, uint argument)
    {
        var state = new CpuState
        {
            Eax = VmwareBackdoor.Magic,
            Ecx = command,
            Ebx = argument,
        };
        Assert.True(machine.Backdoor.TryHandleInput(state, VmwareBackdoor.Port));
        return state;
    }

    // --- plumbing ------------------------------------------------------------

    /// <summary>
    /// How far the guest was told the mouse moved horizontally, taken out of
    /// whatever PS/2 packets are waiting.
    ///
    /// The packet is three bytes: flags, then the X count, then the Y count,
    /// with the X sign living in bit 4 of the flags. Reassembling it here
    /// rather than merely counting bytes means the test would notice a
    /// movement that arrived mangled as well as one that never arrived.
    /// </summary>
    private static int HorizontalMotionDelivered(Machine machine)
    {
        machine.Keyboard.PollMouse();
        List<byte> bytes = DrainMousePackets(machine);

        int total = 0;
        for (int at = 0; at + 2 < bytes.Count; at += 3)
        {
            int x = bytes[at + 1];
            if ((bytes[at] & 0x10) != 0) x -= 256;
            total += x;
        }

        return total;
    }

    private static List<byte> DrainAll(Machine machine)
    {
        var codes = new List<byte>();
        while ((machine.Io.ReadByte(0x64) & 0x01) != 0)
            codes.Add(machine.Io.ReadByte(0x60));
        return codes;
    }

    /// <summary>
    /// Clicks in the middle of the window, which is where the emulated screen
    /// is. This is the gesture that hands the pointer to the guest.
    /// </summary>
    private void ClickLeft(MainWindow window) =>
        _app.Invoke(() => window.MouseDown(new Point(400, 300), MouseButton.Left));

    private void Key(MainWindow window, PhysicalKey key, bool down) =>
        _app.Invoke(() => RaiseKey(window, key, down));

    private static void RaiseKey(MainWindow window, PhysicalKey key, bool down) =>
        window.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = down ? InputElement.KeyDownEvent : InputElement.KeyUpEvent,
            PhysicalKey = key,
            KeyModifiers = KeyModifiers.None,
        });
}
