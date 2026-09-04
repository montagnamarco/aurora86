namespace Aurora386.Core.Devices;

/// <summary>
/// Which buttons are down. The bit order is the one the PS/2 packet uses,
/// and INT 33h happens to use the same one for its low three bits, so the
/// same value travels the whole way without being reshuffled.
/// </summary>
[Flags]
public enum MouseButtons
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 4,
}

/// <summary>
/// Which port the mouse is plugged into, as far as the guest can tell.
///
/// A real PC of this era had one mouse on one port, and software found it by
/// looking for that port: a driver written for a serial mouse pokes COM1 and
/// concludes there is no mouse if nothing answers. So the choice is not a
/// preference, it is part of the hardware the guest is being shown, and it
/// changes what the guest can detect.
///
/// <see cref="None"/> is not a nicety either. Some period software probes the
/// PS/2 port during startup and hangs if it finds a mouse it does not expect,
/// and unplugging the thing is the only way past that.
/// </summary>
public enum MouseInterface
{
    /// <summary>No mouse is plugged in; the guest sees empty ports.</summary>
    None,

    /// <summary>On the 8042's auxiliary port, interrupting on IRQ 12.</summary>
    Ps2,

    /// <summary>A Microsoft serial mouse on COM1.</summary>
    Serial,
}

/// <summary>
/// The physical mouse, shared by everything that wants to hear from it.
///
/// A real PC has one mouse plugged into one port, but the emulator has to
/// serve three consumers at once: the PS/2 device on the 8042, a serial
/// mouse on COM1, and the INT 33h driver interface. Which of them the guest
/// listens to depends on what it loaded -- Windows 3.11 talks to whichever
/// port its driver was configured for, a DOS game usually talks to INT 33h,
/// and a program that pokes the hardware directly talks to neither.
///
/// So the host's movements land here first, and each consumer pulls from its
/// own accumulator. That is the crucial detail: the accumulators must be
/// separate. Share one and whichever consumer reads first eats the motion,
/// and the other sees a mouse that only moves every other time -- which is
/// exactly how a mouse that "works but is jerky" comes about.
///
/// Which of the two <i>ports</i> is fed is decided by
/// <see cref="ConnectedInterface"/>, because a machine has one mouse and it
/// is plugged into one socket. INT 33h is fed regardless, since it is a
/// driver rather than a socket.
///
/// Motion is accumulated rather than delivered as a position because that is
/// what the hardware reports. A mouse has no idea where the pointer is on
/// screen; it counts how far the ball turned since it was last asked, and
/// the driver adds that up into a position. Everything about mouse
/// behaviour -- sensitivity, acceleration, the pointer stopping at the edge
/// of the screen -- follows from the driver owning the position and the
/// hardware owning only the deltas.
/// </summary>
public sealed class MouseState
{
    /// <summary>Buttons currently held down.</summary>
    public MouseButtons Buttons { get; private set; }

    /// <summary>
    /// Motion not yet collected, one accumulator per consumer. Counts are in
    /// mouse units ("mickeys"), positive X to the right and positive Y
    /// <b>downwards</b> -- the direction the host reports and the direction
    /// INT 33h uses. The PS/2 packet inverts Y on its way out, because that
    /// protocol counts upwards; getting that inversion wrong gives a mouse
    /// that moves perfectly except that it goes the wrong way vertically.
    /// </summary>
    private int _ps2DeltaX, _ps2DeltaY;
    private int _serialDeltaX, _serialDeltaY;
    private int _driverDeltaX, _driverDeltaY;

    /// <summary>
    /// Raised whenever motion or a button change arrives, so that a port
    /// which streams packets can send one immediately rather than waiting to
    /// be polled. A real mouse pushes; only INT 33h pulls.
    /// </summary>
    public event Action? Moved;

    /// <summary>
    /// Which port the mouse is currently plugged into. Defaults to PS/2,
    /// which is what a 386 of the later years shipped with.
    ///
    /// Changing this is meant to be possible while the machine runs, because
    /// that is the only way to find out which port a given piece of software
    /// wants: you try one, the pointer does not move, you try the other. The
    /// alternative -- restarting the guest for each attempt -- makes the
    /// experiment cost a minute of DOS booting every time.
    ///
    /// Unplugging does not retract what the guest has already been told. A
    /// driver that has already found a PS/2 mouse goes on believing in it;
    /// it simply stops receiving packets, exactly as it would if the cable
    /// were pulled out of the back of a running machine.
    /// </summary>
    public MouseInterface ConnectedInterface { get; set; } = MouseInterface.Ps2;

    /// <summary>
    /// Reports movement since the last report, in mickeys.
    /// </summary>
    public void Move(int deltaX, int deltaY)
    {
        if (deltaX == 0 && deltaY == 0) return;
        if (ConnectedInterface == MouseInterface.None) return;

        // Only the port the mouse is actually plugged into accumulates. The
        // tempting simplification is to fill all three and let the unselected
        // ports simply not be read, but that is wrong in a way that surfaces
        // later: switch from serial to PS/2 after a minute of movement and
        // the PS/2 port immediately flushes that whole minute, sending the
        // pointer to a corner. What a port did not receive, it never had.
        if (ConnectedInterface == MouseInterface.Ps2)
        {
            _ps2DeltaX += deltaX; _ps2DeltaY += deltaY;
        }
        else
        {
            _serialDeltaX += deltaX; _serialDeltaY += deltaY;
        }

        // INT 33h is fed whichever port is fitted, because it is a driver and
        // not a port: a program calling it does not care how the mouse is
        // wired, only that there is one.
        _driverDeltaX += deltaX; _driverDeltaY += deltaY;

        Moved?.Invoke();
    }

    /// <summary>
    /// How many times each button has gone down and come back up since the
    /// driver last collected the counts.
    ///
    /// Counting has to happen here, at the moment the button actually moves,
    /// and not by comparing the button state between two polls. A user can
    /// click faster than a slow program looks: a press and its release that
    /// both happen between two polls leave the state exactly as it was, and
    /// a driver that only compares states sees nothing at all. That is the
    /// bug where a menu occasionally ignores a click for no visible reason,
    /// and it is impossible to reproduce deliberately because it depends on
    /// out-running the program.
    /// </summary>
    private readonly int[] _pressCount = new int[3];
    private readonly int[] _releaseCount = new int[3];

    /// <summary>
    /// Takes the press and release counts for one button, resetting them.
    /// Buttons are numbered 0 for left, 1 for right and 2 for middle, which
    /// is the order INT 33h uses.
    /// </summary>
    public (int Presses, int Releases) TakeButtonCounts(int button)
    {
        if (button is < 0 or > 2) return (0, 0);

        int presses = _pressCount[button];
        int releases = _releaseCount[button];
        _pressCount[button] = 0;
        _releaseCount[button] = 0;
        return (presses, releases);
    }

    /// <summary>Forgets the counts, as the INT 33h reset does.</summary>
    public void ClearButtonCounts()
    {
        Array.Clear(_pressCount);
        Array.Clear(_releaseCount);
    }

    /// <summary>Presses or releases a button.</summary>
    public void SetButton(MouseButtons button, bool pressed)
    {
        // A mouse that is not plugged in has no buttons to press. Letting
        // clicks through while blocking motion would give the guest a mouse
        // that cannot move but can still click, which is worse than no mouse
        // at all: a program would see the click at whatever stale position
        // it last knew about.
        if (ConnectedInterface == MouseInterface.None) return;

        MouseButtons updated = pressed ? Buttons | button : Buttons & ~button;
        if (updated == Buttons) return;

        Buttons = updated;

        int index = button switch
        {
            MouseButtons.Left => 0,
            MouseButtons.Right => 1,
            MouseButtons.Middle => 2,
            _ => -1,
        };

        if (index >= 0)
        {
            if (pressed) _pressCount[index]++;
            else _releaseCount[index]++;
        }

        // A button change is an event in its own right, not merely a change
        // of state to be noticed later. A click with no motion still has to
        // produce a packet, otherwise a mouse that is not being moved cannot
        // be clicked at all.
        Moved?.Invoke();
    }

    /// <summary>True when any consumer still has motion waiting for it.</summary>
    public bool HasPs2Motion => _ps2DeltaX != 0 || _ps2DeltaY != 0;
    public bool HasSerialMotion => _serialDeltaX != 0 || _serialDeltaY != 0;

    /// <summary>
    /// Takes the motion accumulated for the PS/2 port, clamped to what one
    /// packet can carry.
    ///
    /// The packet has nine signed bits per axis, so a fast flick can overflow
    /// it. Whatever does not fit stays in the accumulator and goes out in the
    /// next packet: the pointer then lags very slightly instead of leaping to
    /// a wrong place, which is what truncating the value would do.
    /// </summary>
    public (int X, int Y) TakePs2Motion()
    {
        int x = Math.Clamp(_ps2DeltaX, -255, 255);
        int y = Math.Clamp(_ps2DeltaY, -255, 255);
        _ps2DeltaX -= x;
        _ps2DeltaY -= y;
        return (x, y);
    }

    /// <summary>
    /// Takes the motion accumulated for the serial port. The Microsoft
    /// protocol carries only eight signed bits per axis, so the clamp is
    /// tighter than the PS/2 one.
    /// </summary>
    public (int X, int Y) TakeSerialMotion()
    {
        int x = Math.Clamp(_serialDeltaX, -128, 127);
        int y = Math.Clamp(_serialDeltaY, -128, 127);
        _serialDeltaX -= x;
        _serialDeltaY -= y;
        return (x, y);
    }

    /// <summary>
    /// Takes the motion accumulated for the INT 33h driver. Nothing clamps
    /// here: the driver reads a count, not a packet, and function 0Bh is
    /// documented to return however far the mouse has travelled.
    /// </summary>
    public (int X, int Y) TakeDriverMotion()
    {
        int x = _driverDeltaX, y = _driverDeltaY;
        _driverDeltaX = 0;
        _driverDeltaY = 0;
        return (x, y);
    }

    /// <summary>
    /// Throws away motion nobody has collected. The INT 33h reset does this,
    /// so that a driver starting up does not immediately receive a jump made
    /// up of everything the user did before the program was even loaded.
    /// </summary>
    public void ClearDriverMotion()
    {
        _driverDeltaX = 0;
        _driverDeltaY = 0;
    }

}
