using Aurora86.Core.Devices.Storage;
using Aurora86.Core.Devices.Input;
using Aurora86.Core.Devices.Interrupts;
using Aurora86.Core.Devices.Timing;
using Aurora86.Core.Devices.System;
using Aurora86.Core.Devices.Video;
using CpuNs = Aurora86.Core.Cpu;

namespace Aurora86.Core.Firmware;

/// <summary>
/// INT 33h: the mouse driver interface.
///
/// This interrupt is not part of the BIOS. It belongs to MOUSE.COM, the
/// driver Microsoft shipped with the mouse itself, and it became the standard
/// only because everybody copied it -- there is no ROM anywhere that provides
/// it. Implementing it here means a guest gets a working mouse without having
/// to load a driver at all, which matters because the period drivers talk to
/// the hardware in ways that are far harder to emulate convincingly than the
/// interface they present.
///
/// The unit the interface works in is not the pixel. It is the "mickey", the
/// smallest movement the mouse itself can report, and the driver converts
/// mickeys into screen position using a sensitivity the program can change.
/// The position it hands out is then in an odd unit of its own: virtual
/// coordinates, which are always as if the screen were 640x200 in text mode,
/// regardless of the mode actually in use. A text-mode program divides by
/// eight to get a character cell. This indirection is the single most
/// confusing thing about the interface, and it exists because the driver
/// predates every graphics mode it eventually had to work in.
///
/// The other thing worth knowing is that this driver, uniquely among the
/// interfaces in this project, calls <b>back</b> into the guest: function 0Ch
/// installs a routine that the driver is expected to invoke, as a far call
/// with registers set, whenever the mouse does something interesting.
/// Windows 3.11's mouse driver is built entirely around that callback, so
/// without it Windows sees a mouse that exists and never moves.
/// </summary>
public sealed class MouseServices
{
    private readonly MouseState _state;
    private readonly Machine _machine;

    public MouseServices(Machine machine, MouseState state)
    {
        _machine = machine;
        _state = state;
    }

    /// <summary>
    /// What the renderer draws for the pointer. Kept in step with the
    /// driver's position and show count after every call, so that the
    /// picture never lags the state a program would read back.
    /// </summary>
    public MousePointer Pointer { get; } = new();

    /// <summary>
    /// Whether INT 33h still points at us. A program is free to install its
    /// own mouse driver, and from that moment ours must be silent: it neither
    /// draws a pointer nor calls anybody's callback.
    /// </summary>
    private bool OwnsInterruptVector
        => _machine.Memory.ReadWord(0x33 * 4 + 2) == 0xF000
        && _machine.Memory.ReadWord(0x33 * 4) == BiosRom.Entry.Mouse;

    /// <summary>
    /// Brings the driver up to date with the mouse without a program asking:
    /// the renderer calls this once per frame so the pointer follows the
    /// mouse even while the program is busy and not polling, exactly as the
    /// real driver's interrupt handler moved the pointer behind a program's
    /// back.
    /// </summary>
    public void Refresh()
    {
        Update();
        SyncPointer();
    }

    private void SyncPointer()
    {
        // Nothing is drawn once somebody else owns INT 33h. A program that
        // loaded its own driver draws its own pointer, and two pointers on
        // one screen is worse than none.
        Pointer.Visible = _enabled && CursorVisible && OwnsInterruptVector;
        Pointer.X = PositionX;
        Pointer.Y = PositionY;
    }

    /// <summary>
    /// The pointer's position in virtual coordinates. The driver owns this,
    /// not the hardware: the mouse only ever says how far it moved.
    /// </summary>
    public int PositionX { get; private set; }
    public int PositionY { get; private set; }

    /// <summary>
    /// How far the pointer may travel. A program that wants the pointer
    /// confined to a window sets these, and the driver is responsible for the
    /// clamping -- a program that draws its own pointer does not check.
    /// </summary>
    public int MinX { get; private set; }
    public int MaxX { get; private set; } = 639;
    public int MinY { get; private set; }
    public int MaxY { get; private set; } = 199;

    /// <summary>
    /// The cursor's visibility, kept as a counter rather than a flag.
    ///
    /// This is deliberate and it is in the specification: function 01h
    /// increments and 02h decrements, and the cursor is drawn only at zero.
    /// The reason is nesting. A routine that needs the pointer out of the way
    /// while it redraws hides it and shows it again, and it must not make the
    /// pointer appear if its caller had already hidden it. Reduce this to a
    /// boolean and the pointer flickers into view in the middle of somebody
    /// else's drawing -- and, worse, is left visible when it should not be.
    ///
    /// It starts at -1: hidden, and needing one show to appear.
    /// </summary>
    private int _showCount = -1;

    /// <summary>True when the driver should be drawing the pointer.</summary>
    public bool CursorVisible => _showCount >= 0;

    /// <summary>
    /// Mickeys per eight pixels, horizontally and vertically. Eight is the
    /// unit the interface uses because it is one text character wide; the
    /// default of eight mickeys means one-to-one movement.
    /// </summary>
    public int SensitivityX { get; private set; } = 8;
    public int SensitivityY { get; private set; } = 8;

    /// <summary>
    /// The speed above which movement is doubled, in mickeys per second.
    /// Stored and reported back, but not applied: acceleration computed
    /// against emulated time would make the pointer's feel depend on how fast
    /// the host happens to be running the guest.
    /// </summary>
    public int DoubleSpeedThreshold { get; private set; } = 64;

    /// <summary>
    /// Where the guest's event handler lives, and which events it wants.
    /// A zero segment and offset means none is installed.
    /// </summary>
    public ushort HandlerSegment { get; private set; }
    public ushort HandlerOffset { get; private set; }
    public ushort HandlerMask { get; private set; }

    // Bits of the event mask, which is also what is passed to the handler in
    // AX to say why it was called.
    private const ushort EventMoved = 0x01;
    private const ushort EventLeftDown = 0x02;
    private const ushort EventLeftUp = 0x04;
    private const ushort EventRightDown = 0x08;
    private const ushort EventRightUp = 0x10;
    private const ushort EventMiddleDown = 0x20;
    private const ushort EventMiddleUp = 0x40;

    /// <summary>
    /// Where the pointer was for the most recent press and release of each
    /// button, which functions 05h and 06h report alongside the counts.
    ///
    /// The counts themselves live in <see cref="MouseState"/>, because they
    /// have to be incremented the instant the button moves: a press and its
    /// release that both fall between two polls leave the button state
    /// exactly as it was, and anything that works by comparing states sees
    /// nothing happen at all.
    /// </summary>
    private readonly int[] _pressX = new int[3];
    private readonly int[] _pressY = new int[3];
    private readonly int[] _releaseX = new int[3];
    private readonly int[] _releaseY = new int[3];

    /// <summary>
    /// Counts collected from the mouse but not yet handed to the program.
    /// They are drained out of <see cref="MouseState"/> on every update,
    /// because the position has to be recorded against them while it is
    /// still current, and held here until the program asks.
    /// </summary>
    private readonly int[] _pressCount = new int[3];
    private readonly int[] _releaseCount = new int[3];

    private MouseButtons _lastButtons;

    /// <summary>
    /// Motion left over after converting mickeys to pixels.
    ///
    /// Integer division throws away the remainder, and at the default
    /// sensitivity of eight mickeys per eight pixels that is invisible. Turn
    /// the sensitivity down, though, and every report smaller than the
    /// divisor rounds to zero: the pointer stops moving altogether for slow
    /// movements, however long the user keeps sliding the mouse. Carrying the
    /// remainder is what makes a low sensitivity slow rather than dead.
    /// </summary>
    private int _remainderX, _remainderY;

    /// <summary>
    /// Handles an INT 33h call. Unknown functions are left alone rather than
    /// reported as errors: the interface has no error convention at all, and
    /// a program that asks for something exotic simply gets its registers
    /// back unchanged, which is what a driver of the era did too.
    /// </summary>
    public void Handle(CpuNs.Cpu cpu)
    {
        ushort function = cpu.State.Ax;

        try
        {
            Dispatch(cpu, function);
        }
        finally
        {
            SyncPointer();
        }
    }

    private void Dispatch(CpuNs.Cpu cpu, ushort function)
    {
        switch (function)
        {
            case 0x0000: Reset(cpu); return;
            case 0x0001: ShowCursor(); return;
            case 0x0002: HideCursor(); return;
            case 0x0003: ReportPosition(cpu); return;
            case 0x0004: SetPosition(cpu); return;
            case 0x0005: ReportButtonPress(cpu); return;
            case 0x0006: ReportButtonRelease(cpu); return;
            case 0x0007: SetHorizontalRange(cpu); return;
            case 0x0008: SetVerticalRange(cpu); return;
            case 0x0009: SetGraphicsCursor(cpu); return;
            case 0x000A: SetTextCursor(cpu); return;
            case 0x000B: ReportMotion(cpu); return;
            case 0x000C: SetEventHandler(cpu); return;
            case 0x000F: SetSensitivityFromMickeys(cpu); return;
            case 0x0010: return;   // exclusion area for a cursor we do not draw
            case 0x001A: SetSensitivity(cpu); return;
            case 0x001B: ReportSensitivity(cpu); return;
            case 0x001F: DisableDriver(cpu); return;
            case 0x0020: EnableDriver(); return;
            case 0x0021: Reset(cpu); return;   // software reset: same effect, different reply
            case 0x0024: ReportVersion(cpu); return;
            default: return;
        }
    }

    /// <summary>
    /// Function 0000h: reset, and the call every program makes first to find
    /// out whether a mouse is there at all.
    ///
    /// The answer is in AX: 0xFFFF means installed, 0x0000 means not. It has
    /// to be exactly 0xFFFF -- programs test for that value rather than for
    /// "non-zero", so a plausible-looking 1 reads as "no mouse". BX gets the
    /// number of buttons.
    /// </summary>
    private void Reset(CpuNs.Cpu cpu)
    {
        cpu.State.Ax = 0xFFFF;
        cpu.State.Bx = 2;

        _showCount = -1;
        MinX = 0; MinY = 0;

        // The range follows the video mode, and the vertical limit is the
        // part that differs: a graphics mode is 200 or more virtual rows
        // where text mode is a flat 200. Getting this wrong confines the
        // pointer to the top of the screen in one mode or lets it run off the
        // bottom in another.
        (MaxX, MaxY) = DefaultRange();

        PositionX = (MinX + MaxX) / 2;
        PositionY = (MinY + MaxY) / 2;

        SensitivityX = 8;
        SensitivityY = 8;
        DoubleSpeedThreshold = 64;

        HandlerSegment = 0;
        HandlerOffset = 0;
        HandlerMask = 0;

        Pointer.LoadDefaultArrow();
        Pointer.TextScreenMask = 0x77FF;
        Pointer.TextCursorMask = 0x7700;
        Pointer.UsesHardwareTextCursor = false;

        Array.Clear(_pressCount);
        Array.Clear(_releaseCount);
        _state.ClearButtonCounts();
        _remainderX = 0;
        _remainderY = 0;
        _lastButtons = _state.Buttons;

        // Movement made before the program started is not the program's
        // business, and delivering it would show up as the pointer jumping
        // the instant the mouse is initialised.
        _state.ClearDriverMotion();
    }

    /// <summary>
    /// The coordinate range for the current video mode.
    ///
    /// Virtual coordinates are pixels in a graphics mode and eight-pixel
    /// character cells scaled back up in text mode, which is why 80 columns
    /// comes out as 640 either way.
    /// </summary>
    /// <summary>
    /// The coordinate range the driver hands out, which a frontend needs in
    /// order to scale host movement correctly. It is not the same as the
    /// picture's size: in text mode the driver counts eight units per
    /// character cell, so an 80x25 screen is 640x200 virtual units while the
    /// picture is 640x400 pixels. Scaling movement against the picture makes
    /// the pointer crawl vertically at half speed.
    /// </summary>
    public (int Width, int Height) VirtualScreenSize()
    {
        (int maxX, int maxY) = DefaultRange();
        return (maxX + 1, maxY + 1);
    }

    private (int MaxX, int MaxY) DefaultRange()
    {
        Devices.Video.VgaRegisters registers = _machine.VideoRegisters;

        if (registers.IsGraphicsMode)
            return (Math.Max(1, registers.ModeInfo.PixelWidth) - 1,
                    Math.Max(1, registers.ModeInfo.PixelHeight) - 1);

        return (registers.TextColumns * 8 - 1, registers.TextRows * 8 - 1);
    }

    private void ShowCursor()
    {
        // Saturates at zero rather than climbing: the counter's whole purpose
        // is to make hide/show nest, and a show beyond visible would have to
        // be undone by an extra hide that nobody is going to send.
        if (_showCount < 0) _showCount++;
    }

    private void HideCursor() => _showCount--;

    /// <summary>
    /// Function 0009h: the graphics pointer's shape. BX,CX are the hot spot
    /// and ES:DX points at 32 words: sixteen rows of screen (AND) mask
    /// followed by sixteen rows of cursor (XOR) mask, most significant bit
    /// leftmost. Programs that draw their own arrow, hand or hourglass all
    /// come through here.
    /// </summary>
    private void SetGraphicsCursor(CpuNs.Cpu cpu)
    {
        Pointer.HotSpotX = (short)cpu.State.Bx;
        Pointer.HotSpotY = (short)cpu.State.Cx;

        uint offset = cpu.State.Dx;
        for (int row = 0; row < 16; row++)
        {
            Pointer.AndMask[row] = cpu.ReadMemoryWord(CpuNs.SegReg.Es, offset + (uint)(row * 2));
            Pointer.XorMask[row] = cpu.ReadMemoryWord(CpuNs.SegReg.Es, offset + 32 + (uint)(row * 2));
        }
    }

    /// <summary>
    /// Function 000Ah: the text pointer. BX selects the kind: 0 is the
    /// software cursor, with CX the screen mask and DX the cursor mask applied
    /// to the cell's character/attribute word; 1 asks for the hardware cursor
    /// instead, with CX and DX its start and end scanlines.
    /// </summary>
    private void SetTextCursor(CpuNs.Cpu cpu)
    {
        if (cpu.State.Bx == 0)
        {
            Pointer.UsesHardwareTextCursor = false;
            Pointer.TextScreenMask = cpu.State.Cx;
            Pointer.TextCursorMask = cpu.State.Dx;
            return;
        }

        Pointer.UsesHardwareTextCursor = true;
    }

    /// <summary>
    /// Function 0003h: where the pointer is and which buttons are down.
    /// BX carries the buttons with left in bit 0, and CX and DX the position.
    /// </summary>
    private void ReportPosition(CpuNs.Cpu cpu)
    {
        Update();
        cpu.State.Bx = (ushort)((byte)_state.Buttons & 0x07);
        cpu.State.Cx = (ushort)PositionX;
        cpu.State.Dx = (ushort)PositionY;
    }

    /// <summary>
    /// Function 0004h: put the pointer somewhere. Used when a program wants
    /// the pointer to start on a particular button, and by anything that
    /// warps it.
    /// </summary>
    private void SetPosition(CpuNs.Cpu cpu)
    {
        PositionX = Math.Clamp((short)cpu.State.Cx, MinX, MaxX);
        PositionY = Math.Clamp((short)cpu.State.Dx, MinY, MaxY);

        // Motion accumulated but not yet applied would drag the pointer
        // straight back off the position just set.
        _state.ClearDriverMotion();
        _remainderX = 0;
        _remainderY = 0;
    }

    /// <summary>
    /// Function 0005h: how many times a button has been pressed since this
    /// was last asked, and where it was for the most recent one. The count is
    /// reset by reading it, which is what makes a click impossible to miss
    /// however slowly the program polls.
    /// </summary>
    private void ReportButtonPress(CpuNs.Cpu cpu)
    {
        Update();

        int button = Math.Min((int)cpu.State.Bx, 2);

        cpu.State.Ax = (ushort)((byte)_state.Buttons & 0x07);
        cpu.State.Bx = (ushort)_pressCount[button];
        cpu.State.Cx = (ushort)_pressX[button];
        cpu.State.Dx = (ushort)_pressY[button];

        _pressCount[button] = 0;
    }

    private void ReportButtonRelease(CpuNs.Cpu cpu)
    {
        Update();

        int button = Math.Min((int)cpu.State.Bx, 2);

        cpu.State.Ax = (ushort)((byte)_state.Buttons & 0x07);
        cpu.State.Bx = (ushort)_releaseCount[button];
        cpu.State.Cx = (ushort)_releaseX[button];
        cpu.State.Dx = (ushort)_releaseY[button];

        _releaseCount[button] = 0;
    }

    /// <summary>
    /// Functions 0007h and 0008h: confine the pointer.
    ///
    /// The two limits arrive in CX and DX, and a program is entitled to send
    /// them the wrong way round -- the specification says the driver swaps
    /// them. A driver that does not gets a range whose minimum exceeds its
    /// maximum, and the clamp then pins the pointer to a single point where
    /// it cannot move at all.
    /// </summary>
    private void SetHorizontalRange(CpuNs.Cpu cpu)
    {
        int low = (short)cpu.State.Cx;
        int high = (short)cpu.State.Dx;
        if (low > high) (low, high) = (high, low);

        MinX = low;
        MaxX = high;
        PositionX = Math.Clamp(PositionX, MinX, MaxX);
    }

    private void SetVerticalRange(CpuNs.Cpu cpu)
    {
        int low = (short)cpu.State.Cx;
        int high = (short)cpu.State.Dx;
        if (low > high) (low, high) = (high, low);

        MinY = low;
        MaxY = high;
        PositionY = Math.Clamp(PositionY, MinY, MaxY);
    }

    /// <summary>
    /// Function 000Bh: how far the mouse has moved since this was last asked,
    /// in mickeys rather than in pixels.
    ///
    /// This is the call a program makes when it wants the raw movement rather
    /// than a bounded pointer -- a first-person game turning the view, most
    /// obviously, where there is no pointer and no edge of the screen to stop
    /// at. Reading it consumes the movement, so the pointer position must be
    /// brought up to date first or the two callers would each see only part
    /// of it.
    /// </summary>
    private void ReportMotion(CpuNs.Cpu cpu)
    {
        Update();

        cpu.State.Cx = (ushort)(short)_mickeysX;
        cpu.State.Dx = (ushort)(short)_mickeysY;
        _mickeysX = 0;
        _mickeysY = 0;
    }

    /// <summary>
    /// Mickeys counted since function 000Bh last reported them. They are kept
    /// separately from the pixel position because the two are consumed by
    /// different callers at different times, and a program is entitled to use
    /// both.
    /// </summary>
    private int _mickeysX, _mickeysY;

    /// <summary>
    /// Function 000Ch: install the routine the driver calls when the mouse
    /// does something. ES:DX is where it lives and CX says which events it
    /// wants.
    /// </summary>
    private void SetEventHandler(CpuNs.Cpu cpu)
    {
        HandlerMask = cpu.State.Cx;
        HandlerSegment = cpu.State.Es.Selector;
        HandlerOffset = cpu.State.Dx;
    }

    /// <summary>
    /// Function 000Fh: sensitivity expressed as mickeys per eight pixels,
    /// which is the same quantity function 001Ah sets, differently packaged.
    /// A value of zero would divide by zero, so it is treated as the default.
    /// </summary>
    private void SetSensitivityFromMickeys(CpuNs.Cpu cpu)
    {
        SensitivityX = cpu.State.Cx == 0 ? 8 : cpu.State.Cx;
        SensitivityY = cpu.State.Dx == 0 ? 8 : cpu.State.Dx;
    }

    /// <summary>
    /// Function 001Ah: sensitivity for both axes and the double-speed
    /// threshold, in one call. Windows sets this from its control panel.
    /// </summary>
    private void SetSensitivity(CpuNs.Cpu cpu)
    {
        SensitivityX = cpu.State.Bx == 0 ? 8 : cpu.State.Bx;
        SensitivityY = cpu.State.Cx == 0 ? 8 : cpu.State.Cx;
        DoubleSpeedThreshold = cpu.State.Dx;
    }

    /// <summary>
    /// Function 001Bh: read back what 001Ah set. A control panel that shows a
    /// sensitivity slider reads this to position it, so the values have to be
    /// the ones that were actually stored rather than the defaults.
    /// </summary>
    private void ReportSensitivity(CpuNs.Cpu cpu)
    {
        cpu.State.Bx = (ushort)SensitivityX;
        cpu.State.Cx = (ushort)SensitivityY;
        cpu.State.Dx = (ushort)DoubleSpeedThreshold;
    }

    /// <summary>
    /// Function 001Fh: shut the driver down, which a program does when it
    /// wants the mouse hardware to itself.
    ///
    /// The reply is the old interrupt vector in ES:BX, so that function 0020h
    /// can put it back. Ours is not a resident driver with a vector to hand
    /// over, but the answer still has to be the shape the caller expects:
    /// AX = 0x001F confirms the driver was disabled, and anything else means
    /// it refused.
    /// </summary>
    private void DisableDriver(CpuNs.Cpu cpu)
    {
        _enabled = false;
        cpu.State.Ax = 0x001F;
        cpu.SetSegment(CpuNs.SegReg.Es, 0xF000);
        cpu.State.Bx = BiosRom.Entry.Mouse;
    }

    private void EnableDriver() => _enabled = true;

    private bool _enabled = true;

    /// <summary>
    /// Function 0024h: driver version, mouse type and the IRQ it uses.
    /// BH.BL is the version in BCD, CH says what kind of mouse it is
    /// (4 = PS/2) and CL the IRQ, which is zero for a PS/2 mouse because it
    /// is serviced through the BIOS rather than by the driver hooking a line.
    /// </summary>
    private void ReportVersion(CpuNs.Cpu cpu)
    {
        cpu.State.Bx = 0x0800;   // version 8.00

        // CH is the mouse type and CL the IRQ it uses: 4 and 12 for a PS/2
        // mouse, 2 and 4 for a serial one on COM1. Windows' MOUSE.DRV reads
        // both when it finds a resident driver, to know which line to mask.
        cpu.State.Cx = _state.ConnectedInterface == MouseInterface.Ps2
            ? (ushort)0x040C
            : (ushort)0x0204;
    }

    /// <summary>
    /// Folds everything that has happened since the last call into the
    /// driver's own state: the pointer moves, the button counters advance.
    ///
    /// This runs on demand rather than on a clock because nothing observes
    /// the driver's state except through a call, and doing it here means a
    /// program that polls fast and one that polls slowly both see exactly the
    /// movement that took place.
    /// </summary>
    private void Update()
    {
        if (!_enabled) return;

        (int deltaX, int deltaY) = _state.TakeDriverMotion();

        _mickeysX += deltaX;
        _mickeysY += deltaY;

        // Mickeys to pixels. The sensitivity is expressed per eight pixels,
        // so the conversion is delta * 8 / sensitivity, and the remainder is
        // carried rather than dropped.
        int scaledX = deltaX * 8 + _remainderX;
        int scaledY = deltaY * 8 + _remainderY;

        int pixelsX = scaledX / SensitivityX;
        int pixelsY = scaledY / SensitivityY;

        _remainderX = scaledX - pixelsX * SensitivityX;
        _remainderY = scaledY - pixelsY * SensitivityY;

        int previousX = PositionX, previousY = PositionY;

        PositionX = Math.Clamp(PositionX + pixelsX, MinX, MaxX);
        PositionY = Math.Clamp(PositionY + pixelsY, MinY, MaxY);

        MouseButtons buttons = _state.Buttons;

        ushort events = 0;
        if (PositionX != previousX || PositionY != previousY) events |= EventMoved;

        for (int button = 0; button < 3; button++)
        {
            (int presses, int releases) = _state.TakeButtonCounts(button);
            if (presses == 0 && releases == 0) continue;

            if (presses > 0)
            {
                _pressCount[button] += presses;
                _pressX[button] = PositionX;
                _pressY[button] = PositionY;
                events |= (ushort)(EventLeftDown << (button * 2));
            }

            if (releases > 0)
            {
                _releaseCount[button] += releases;
                _releaseX[button] = PositionX;
                _releaseY[button] = PositionY;
                events |= (ushort)(EventLeftUp << (button * 2));
            }
        }

        _lastButtons = buttons;

        if (events != 0) _pendingEvents |= events;
    }

    /// <summary>
    /// Events that have happened but whose handler has not been called yet.
    /// The call cannot be made from inside <see cref="Update"/>: that runs in
    /// the middle of servicing an INT 33h, with the guest's return address on
    /// the stack, and pushing a far call on top of it would return into the
    /// wrong place.
    /// </summary>
    private ushort _pendingEvents;

    /// <summary>
    /// Calls the guest's event handler, if one is installed and something it
    /// asked for has happened.
    ///
    /// The machine drives this from a safe point -- between instructions,
    /// with the guest not inside one of our own handlers -- because it is a
    /// call <b>into</b> guest code and needs a clean stack to build on.
    ///
    /// The register convention is fixed and every driver depends on it:
    ///   AX  which events are being reported
    ///   BX  the button state
    ///   CX,DX  the position
    ///   SI,DI  the movement in mickeys
    /// and the handler returns with RETF, not IRET.
    /// </summary>
    public bool TryDispatchEvent(CpuNs.Cpu cpu)
    {
        if (!_enabled || !OwnsInterruptVector) return false;
        if (HandlerSegment == 0 && HandlerOffset == 0) return false;

        Update();

        ushort events = (ushort)(_pendingEvents & HandlerMask);
        if (events == 0)
        {
            // Events the handler did not ask for are dropped rather than kept
            // waiting: holding them would have the next interesting event
            // arrive with a stale one bolted onto it.
            _pendingEvents = 0;
            return false;
        }

        _pendingEvents = 0;

        cpu.State.Ax = events;
        cpu.State.Bx = (ushort)((byte)_state.Buttons & 0x07);
        cpu.State.Cx = (ushort)PositionX;
        cpu.State.Dx = (ushort)PositionY;
        cpu.State.Si = (ushort)(short)_mickeysX;
        cpu.State.Di = (ushort)(short)_mickeysY;

        EnterHandler(cpu, HandlerSegment, HandlerOffset);
        return true;
    }

    /// <summary>
    /// Redirects the guest into its event handler and arranges for it to come
    /// back where it was.
    ///
    /// The handler finishes with RETF, so it needs two return addresses'
    /// worth of care. The interrupted address goes on the stack first; on top
    /// of it goes the address of a lone RETF in our ROM, which is what the
    /// handler's own RETF pops. Unwinding then continues through that stub
    /// and lands exactly on the instruction the guest was about to execute.
    ///
    /// Doing it in one step instead -- pushing the interrupted address and
    /// jumping straight in -- looks equivalent and is not: a handler is
    /// entitled to leave with more than a plain RETF (RETF 4 to drop
    /// arguments, say), and the extra frame absorbs that where a bare return
    /// address would be skipped past, dropping the guest somewhere
    /// unpredictable.
    /// </summary>
    private static void EnterHandler(CpuNs.Cpu cpu, ushort segment, ushort offset)
    {
        ushort returnSegment = cpu.State.Cs.Selector;
        ushort returnOffset = (ushort)cpu.State.Eip;

        // The guest's own return address, at the bottom.
        Push(cpu, returnSegment);
        Push(cpu, returnOffset);

        // The stub the handler returns through, on top.
        Push(cpu, 0xF000);
        Push(cpu, BiosRom.Entry.MouseCallbackReturn);

        cpu.SetSegment(CpuNs.SegReg.Cs, segment);
        cpu.State.Eip = offset;
    }

    private static void Push(CpuNs.Cpu cpu, ushort value)
    {
        ushort sp = (ushort)(cpu.State.Sp - 2);
        cpu.State.Sp = sp;
        cpu.WriteMemoryWord(CpuNs.SegReg.Ss, sp, value);
    }
}
