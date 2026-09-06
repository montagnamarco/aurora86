using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Video;
using Aurora386.Core.Firmware;
using Aurora386.Core.Firmware.Fpu;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Diagnostics;
using Aurora386.Core.Devices.Input;
using Aurora386.Core.Devices.Interrupts;
using Aurora386.Core.Devices.Timing;
using Aurora386.Core.Devices.System;
using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Tests;

/// <summary>
/// INT 33h, the mouse driver interface.
///
/// This is what DOS programs and Windows 3.11 actually talk to; the hardware
/// underneath is the driver's problem, not theirs. The interface has no error
/// convention at all -- a program cannot be told that a call failed -- so
/// every mistake here shows up as a pointer that behaves oddly rather than as
/// anything diagnosable.
/// </summary>
public class MouseServiceTests
{
    private static Machine Started()
    {
        var machine = new Machine();
        machine.RunInstructions(2000);      // let POST run
        return machine;
    }

    /// <summary>Makes an INT 33h call with the given function in AX.</summary>
    private static void Call(Machine machine, ushort function)
    {
        machine.Cpu.State.Ax = function;
        machine.MouseServices.Handle(machine.Cpu);
    }

    [Fact]
    public void ResetReportsTheMouseAsInstalledWithExactlyMinusOneInAx()
    {
        // Programs test AX for 0xFFFF rather than for "not zero". A
        // plausible-looking 1 here reads as "no mouse" and the program
        // silently runs without one -- which is the single most likely way
        // for this whole feature to appear not to work.
        using Machine machine = Started();

        Call(machine, 0x0000);

        Assert.Equal(0xFFFF, machine.Cpu.State.Ax);
        Assert.Equal(2, machine.Cpu.State.Bx);
    }

    [Fact]
    public void TheCursorStartsHiddenAndNeedsOneShowToAppear()
    {
        using Machine machine = Started();
        Call(machine, 0x0000);

        Assert.False(machine.MouseServices.CursorVisible);

        Call(machine, 0x0001);
        Assert.True(machine.MouseServices.CursorVisible);
    }

    [Fact]
    public void HideAndShowNestRatherThanToggling()
    {
        // The visibility is a counter, and it is in the specification for a
        // reason: a routine that hides the pointer to redraw must not make it
        // reappear if its caller had already hidden it. Reduce this to a
        // boolean and the pointer flickers into view in the middle of
        // somebody else's drawing.
        using Machine machine = Started();
        Call(machine, 0x0000);
        Call(machine, 0x0001);
        Assert.True(machine.MouseServices.CursorVisible);

        Call(machine, 0x0002);      // outer hide
        Call(machine, 0x0002);      // inner hide
        Assert.False(machine.MouseServices.CursorVisible);

        Call(machine, 0x0001);      // inner show: still hidden by the outer one
        Assert.False(machine.MouseServices.CursorVisible);

        Call(machine, 0x0001);      // outer show: now it may appear
        Assert.True(machine.MouseServices.CursorVisible);
    }

    [Fact]
    public void RepeatedShowsDoNotBankUpHidesThatWouldBeNeededLater()
    {
        // Showing an already-visible pointer must not push the counter above
        // zero: the extra hide needed to undo it is never going to come, and
        // the pointer would stay visible through a hide that should have
        // removed it.
        using Machine machine = Started();
        Call(machine, 0x0000);

        Call(machine, 0x0001);
        Call(machine, 0x0001);
        Call(machine, 0x0001);

        Call(machine, 0x0002);
        Assert.False(machine.MouseServices.CursorVisible);
    }

    [Fact]
    public void ThePositionFollowsTheMouseAndTheButtonsAreReportedWithIt()
    {
        using Machine machine = Started();
        Call(machine, 0x0000);

        int startX = machine.MouseServices.PositionX;
        int startY = machine.MouseServices.PositionY;

        machine.MoveMouse(10, 6);
        machine.SetMouseButton(MouseButtons.Left, true);

        Call(machine, 0x0003);

        Assert.Equal(startX + 10, machine.Cpu.State.Cx);
        Assert.Equal(startY + 6, machine.Cpu.State.Dx);
        Assert.Equal(1, machine.Cpu.State.Bx);      // left button, bit 0
    }

    [Fact]
    public void MovementIsConsumedOnceRatherThanReportedRepeatedly()
    {
        // The position is absolute; the movement behind it is not. Read the
        // same movement twice and the pointer travels twice as far as the
        // mouse did, which is the "pointer accelerates away" bug.
        using Machine machine = Started();
        Call(machine, 0x0000);

        machine.MoveMouse(10, 0);

        Call(machine, 0x0003);
        int after = machine.Cpu.State.Cx;

        Call(machine, 0x0003);
        Assert.Equal(after, machine.Cpu.State.Cx);
    }

    [Fact]
    public void SettingThePositionPutsThePointerExactlyWhereAsked()
    {
        using Machine machine = Started();
        Call(machine, 0x0000);

        machine.Cpu.State.Cx = 100;
        machine.Cpu.State.Dx = 50;
        Call(machine, 0x0004);

        Call(machine, 0x0003);
        Assert.Equal(100, machine.Cpu.State.Cx);
        Assert.Equal(50, machine.Cpu.State.Dx);
    }

    [Fact]
    public void MovementMadeBeforeAWarpDoesNotDragThePointerBackOffIt()
    {
        // A program that warps the pointer -- to a default button, say --
        // expects it to stay there. Movement still sitting in the accumulator
        // would be applied on the very next read and pull it away.
        using Machine machine = Started();
        Call(machine, 0x0000);

        machine.MoveMouse(40, 40);      // not yet read by anything

        machine.Cpu.State.Cx = 10;
        machine.Cpu.State.Dx = 10;
        Call(machine, 0x0004);

        Call(machine, 0x0003);
        Assert.Equal(10, machine.Cpu.State.Cx);
        Assert.Equal(10, machine.Cpu.State.Dx);
    }

    [Fact]
    public void ThePointerIsHeldInsideTheRangeThatWasSet()
    {
        using Machine machine = Started();
        Call(machine, 0x0000);

        machine.Cpu.State.Cx = 100;
        machine.Cpu.State.Dx = 200;
        Call(machine, 0x0007);          // horizontal range 100..200

        machine.Cpu.State.Cx = 50;
        machine.Cpu.State.Dx = 80;
        Call(machine, 0x0008);          // vertical range 50..80

        machine.MoveMouse(10000, 10000);
        Call(machine, 0x0003);
        Assert.Equal(200, machine.Cpu.State.Cx);
        Assert.Equal(80, machine.Cpu.State.Dx);

        machine.MoveMouse(-10000, -10000);
        Call(machine, 0x0003);
        Assert.Equal(100, machine.Cpu.State.Cx);
        Assert.Equal(50, machine.Cpu.State.Dx);
    }

    [Fact]
    public void ARangeGivenBackToFrontIsSwappedRatherThanObeyed()
    {
        // The specification says the driver swaps them, and programs do send
        // them the wrong way round. A driver that does not gets a minimum
        // above its maximum, and the clamp then pins the pointer to a single
        // point where it cannot move at all.
        using Machine machine = Started();
        Call(machine, 0x0000);

        machine.Cpu.State.Cx = 300;     // maximum given first
        machine.Cpu.State.Dx = 100;
        Call(machine, 0x0007);

        Assert.Equal(100, machine.MouseServices.MinX);
        Assert.Equal(300, machine.MouseServices.MaxX);

        machine.Cpu.State.Cx = 200;
        Call(machine, 0x0004);
        Call(machine, 0x0003);
        Assert.Equal(200, machine.Cpu.State.Cx);
    }

    [Fact]
    public void RelativeMotionIsReportedInMickeysAndClearedByReadingIt()
    {
        // Function 0Bh is what a game uses when it wants the raw movement
        // rather than a bounded pointer -- turning a view, where there is no
        // edge of the screen to stop at. Reading it must consume it, or the
        // view spins on by itself.
        using Machine machine = Started();
        Call(machine, 0x0000);

        machine.MoveMouse(12, -7);

        Call(machine, 0x000B);
        Assert.Equal(12, (short)machine.Cpu.State.Cx);
        Assert.Equal(-7, (short)machine.Cpu.State.Dx);

        Call(machine, 0x000B);
        Assert.Equal(0, (short)machine.Cpu.State.Cx);
        Assert.Equal(0, (short)machine.Cpu.State.Dx);
    }

    [Fact]
    public void RelativeMotionIsNotClampedByTheCursorRange()
    {
        // The pointer stops at the edge; the mickey count does not. A game
        // turning its view has no pointer at all, and clamping its input at
        // an invisible screen edge is exactly the bug where the view refuses
        // to turn any further in one direction.
        using Machine machine = Started();
        Call(machine, 0x0000);

        machine.Cpu.State.Cx = 0;
        machine.Cpu.State.Dx = 10;
        Call(machine, 0x0007);          // a very narrow range

        machine.MoveMouse(500, 0);

        Call(machine, 0x000B);
        Assert.Equal(500, (short)machine.Cpu.State.Cx);
    }

    [Fact]
    public void ButtonPressesAreCountedSoAQuickClickIsNeverMissed()
    {
        // A user can click faster than a slow program polls. Reduce this to
        // "a button is down" and a click that begins and ends between two
        // polls vanishes -- which is the bug where a menu sometimes ignores
        // a click for no apparent reason.
        using Machine machine = Started();
        Call(machine, 0x0000);

        machine.SetMouseButton(MouseButtons.Left, true);
        machine.SetMouseButton(MouseButtons.Left, false);
        machine.SetMouseButton(MouseButtons.Left, true);
        machine.SetMouseButton(MouseButtons.Left, false);

        machine.Cpu.State.Ax = 0x0005;
        machine.Cpu.State.Bx = 0;       // the left button
        machine.MouseServices.Handle(machine.Cpu);

        Assert.Equal(2, machine.Cpu.State.Bx);
    }

    [Fact]
    public void ReadingTheButtonCountResetsIt()
    {
        using Machine machine = Started();
        Call(machine, 0x0000);

        machine.SetMouseButton(MouseButtons.Right, true);
        machine.SetMouseButton(MouseButtons.Right, false);

        machine.Cpu.State.Ax = 0x0005;
        machine.Cpu.State.Bx = 1;       // the right button
        machine.MouseServices.Handle(machine.Cpu);
        Assert.Equal(1, machine.Cpu.State.Bx);

        machine.Cpu.State.Ax = 0x0005;
        machine.Cpu.State.Bx = 1;
        machine.MouseServices.Handle(machine.Cpu);
        Assert.Equal(0, machine.Cpu.State.Bx);
    }

    [Fact]
    public void ReleasesAreCountedSeparatelyFromPresses()
    {
        using Machine machine = Started();
        Call(machine, 0x0000);

        machine.SetMouseButton(MouseButtons.Left, true);
        machine.SetMouseButton(MouseButtons.Left, false);
        machine.SetMouseButton(MouseButtons.Left, true);

        machine.Cpu.State.Ax = 0x0006;      // releases
        machine.Cpu.State.Bx = 0;
        machine.MouseServices.Handle(machine.Cpu);
        Assert.Equal(1, machine.Cpu.State.Bx);

        machine.Cpu.State.Ax = 0x0005;      // presses
        machine.Cpu.State.Bx = 0;
        machine.MouseServices.Handle(machine.Cpu);
        Assert.Equal(2, machine.Cpu.State.Bx);
    }

    [Fact]
    public void SensitivityIsStoredAndReadBackUnchanged()
    {
        // Windows' control panel reads this to position its slider, so the
        // values have to be the ones that were set rather than the defaults.
        using Machine machine = Started();
        Call(machine, 0x0000);

        machine.Cpu.State.Bx = 20;
        machine.Cpu.State.Cx = 30;
        machine.Cpu.State.Dx = 40;
        Call(machine, 0x001A);

        machine.Cpu.State.Bx = 0;
        machine.Cpu.State.Cx = 0;
        machine.Cpu.State.Dx = 0;
        Call(machine, 0x001B);

        Assert.Equal(20, machine.Cpu.State.Bx);
        Assert.Equal(30, machine.Cpu.State.Cx);
        Assert.Equal(40, machine.Cpu.State.Dx);
    }

    [Fact]
    public void AHigherSensitivityNumberMakesThePointerMoveLessFar()
    {
        // The number is mickeys per eight pixels, so it is a divisor: turning
        // it up slows the pointer down. Treating it as a multiplier gets the
        // sense exactly backwards, and every sensitivity slider in every
        // program then works the wrong way round.
        using Machine machine = Started();
        Call(machine, 0x0000);

        machine.Cpu.State.Bx = 16;      // twice the default: half speed
        machine.Cpu.State.Cx = 16;
        machine.Cpu.State.Dx = 64;
        Call(machine, 0x001A);

        machine.Cpu.State.Cx = 100;
        machine.Cpu.State.Dx = 100;
        Call(machine, 0x0004);

        machine.MoveMouse(20, 0);
        Call(machine, 0x0003);

        Assert.Equal(110, machine.Cpu.State.Cx);
    }

    [Fact]
    public void SlowMovementAtALowSensitivityStillMovesThePointer()
    {
        // Integer division throws away the remainder. Drop it and every
        // report smaller than the divisor rounds to zero: the pointer does
        // not move at all for slow movements, however far the mouse travels.
        // Carrying the remainder is what makes a low sensitivity slow rather
        // than dead.
        using Machine machine = Started();
        Call(machine, 0x0000);

        machine.Cpu.State.Bx = 32;      // a quarter speed
        machine.Cpu.State.Cx = 32;
        machine.Cpu.State.Dx = 64;
        Call(machine, 0x001A);

        machine.Cpu.State.Cx = 100;
        machine.Cpu.State.Dx = 100;
        Call(machine, 0x0004);

        // Sixteen reports of one mickey each. At a quarter speed that is four
        // pixels, and each individual report rounds to zero.
        for (int i = 0; i < 16; i++)
        {
            machine.MoveMouse(1, 0);
            Call(machine, 0x0003);
        }

        Assert.Equal(104, machine.Cpu.State.Cx);
    }

    [Fact]
    public void ResetForgetsMovementMadeBeforeTheProgramStarted()
    {
        // A program's first act is to reset the driver. Movement the user
        // made beforehand is not its business, and delivering it shows up as
        // the pointer jumping the instant the program initialises.
        using Machine machine = Started();

        machine.MoveMouse(500, 500);

        Call(machine, 0x0000);
        int x = machine.MouseServices.PositionX;

        Call(machine, 0x0003);
        Assert.Equal(x, machine.Cpu.State.Cx);
    }

    [Fact]
    public void ResetPutsThePointerInTheMiddleOfTheScreen()
    {
        using Machine machine = Started();

        Call(machine, 0x0000);

        // 80x25 text mode is 640x200 in the virtual coordinates the interface
        // works in, whatever the adapter is really doing.
        Assert.Equal(639, machine.MouseServices.MaxX);
        Assert.Equal(199, machine.MouseServices.MaxY);
        Assert.Equal(319, machine.MouseServices.PositionX);
        Assert.Equal(99, machine.MouseServices.PositionY);
    }

    [Fact]
    public void TheDriverAnswersItsVersionAsPointZeroZeroInBcd()
    {
        using Machine machine = Started();

        Call(machine, 0x0024);

        Assert.Equal(0x0800, machine.Cpu.State.Bx);
        Assert.Equal(0x04, machine.Cpu.State.Cx >> 8);   // a PS/2 mouse
    }

    [Fact]
    public void AnUnknownFunctionLeavesTheRegistersAlone()
    {
        // The interface has no way to report an error, so a driver of the era
        // simply did nothing. Clobbering AX would have the caller act on a
        // value it never asked for.
        using Machine machine = Started();

        machine.Cpu.State.Bx = 0x1234;
        Call(machine, 0x00FE);

        Assert.Equal(0x1234, machine.Cpu.State.Bx);
    }

    [Fact]
    public void TheInterruptVectorIsInstalledSoAProgramCanCallItWithNoDriverLoaded()
    {
        // INT 33h belongs to MOUSE.COM, not to the BIOS, so on a real machine
        // this vector points at nothing until a driver is loaded. Providing
        // it is the entire point of the exercise: the guest gets a mouse
        // without one.
        using Machine machine = Started();

        ushort offset = machine.Memory.ReadWord(0x33 * 4);
        ushort segment = machine.Memory.ReadWord(0x33 * 4 + 2);

        Assert.Equal(0xF000, segment);
        Assert.Equal(BiosRom.Entry.Mouse, offset);
    }

    /// <summary>
    /// Drives a real INT 33h from emulated code, which is the only way to
    /// check that the ROM entry point, the vector and the dispatch all agree.
    /// Every other test here calls the handler directly and would pass with
    /// the vector pointing at nothing at all.
    /// </summary>
    [Fact]
    public void AGuestProgramCanCallIntThirtyThreeAndGetAnAnswer()
    {
        using Machine machine = Started();

        const uint code = 0x2000;
        machine.Memory.WriteByte(code + 0, 0xCD);      // INT 33h
        machine.Memory.WriteByte(code + 1, 0x33);
        machine.Memory.WriteByte(code + 2, 0xF4);      // HLT

        machine.Cpu.SetSegment(CpuNs.SegReg.Cs, 0);
        machine.Cpu.State.Eip = code;
        machine.Cpu.SetSegment(CpuNs.SegReg.Ss, 0);
        machine.Cpu.State.Esp = 0x7000;
        machine.Cpu.State.Ax = 0x0000;                 // reset and detect
        machine.Cpu.State.Halted = false;

        for (int i = 0; i < 50 && !machine.Cpu.State.Halted; i++)
            machine.Cpu.Step();

        Assert.True(machine.Cpu.State.Halted);
        Assert.Equal(0xFFFF, machine.Cpu.State.Ax);
        Assert.Equal(2, machine.Cpu.State.Bx);
    }

    /// <summary>
    /// The event handler, which is the part Windows 3.11 is built around: it
    /// installs a routine and waits to be called. A driver that never calls
    /// it leaves Windows with a mouse that exists and never moves.
    /// </summary>
    [Fact]
    public void TheEventHandlerIsCalledWithTheEventAndThePositionInTheRegisters()
    {
        using Machine machine = Started();

        // A handler that records what it was given and returns. It stores AX,
        // BX, CX and DX into fixed addresses so the test can read them back.
        const uint handler = 0x3000;
        uint at = handler;
        void Emit(params byte[] bytes)
        {
            foreach (byte b in bytes) machine.Memory.WriteByte(at++, b);
        }

        Emit(0xA3, 0x00, 0x40);              // MOV [4000], AX
        Emit(0x89, 0x1E, 0x02, 0x40);        // MOV [4002], BX
        Emit(0x89, 0x0E, 0x04, 0x40);        // MOV [4004], CX
        Emit(0x89, 0x16, 0x06, 0x40);        // MOV [4006], DX
        Emit(0xCB);                          // RETF

        machine.Cpu.SetSegment(CpuNs.SegReg.Ds, 0);
        machine.Cpu.SetSegment(CpuNs.SegReg.Es, 0);
        machine.Cpu.SetSegment(CpuNs.SegReg.Ss, 0);
        machine.Cpu.State.Esp = 0x7000;

        Call(machine, 0x0000);

        // Install it, asking for movement and left-button events.
        machine.Cpu.State.Cx = 0x0007;
        machine.Cpu.State.Dx = (ushort)handler;
        Call(machine, 0x000C);

        // A program of some sort for the guest to be executing when the event
        // arrives: a jump to itself.
        const uint idle = 0x5000;
        machine.Memory.WriteByte(idle + 0, 0xEB);      // JMP -2
        machine.Memory.WriteByte(idle + 1, 0xFE);

        machine.Cpu.SetSegment(CpuNs.SegReg.Cs, 0);
        machine.Cpu.State.Eip = idle;

        // POST halted the CPU when it found no bootable disk. The guest has
        // to be running for the handler to be stepped through at all.
        machine.Cpu.State.Halted = false;

        machine.MoveMouse(15, 9);
        machine.SetMouseButton(MouseButtons.Left, true);

        Assert.True(machine.MouseServices.TryDispatchEvent(machine.Cpu));

        for (int i = 0; i < 50; i++)
        {
            machine.Cpu.Step();
            if (machine.Cpu.State.Eip == idle) break;
        }

        ushort events = machine.Memory.ReadWord(0x4000);
        Assert.NotEqual(0, events & 0x01);            // moved
        Assert.NotEqual(0, events & 0x02);            // left button down

        Assert.Equal(1, machine.Memory.ReadWord(0x4002));
        Assert.Equal(machine.MouseServices.PositionX, machine.Memory.ReadWord(0x4004));
        Assert.Equal(machine.MouseServices.PositionY, machine.Memory.ReadWord(0x4006));
    }

    [Fact]
    public void TheGuestResumesExactlyWhereItWasWhenTheHandlerReturns()
    {
        // The handler is entered with a far CALL and leaves with RETF, so it
        // needs a return address to pop. Get this wrong and the guest resumes
        // at a wrong address -- somewhere in the middle of an instruction, or
        // in data -- and what follows is unrecoverable and looks nothing like
        // a mouse problem.
        using Machine machine = Started();

        const uint handler = 0x3000;
        machine.Memory.WriteByte(handler, 0xCB);       // RETF and nothing else

        machine.Cpu.SetSegment(CpuNs.SegReg.Ss, 0);
        machine.Cpu.State.Esp = 0x7000;

        Call(machine, 0x0000);
        machine.Cpu.State.Cx = 0x0001;
        machine.Cpu.State.Dx = (ushort)handler;
        Call(machine, 0x000C);

        const uint idle = 0x5000;
        machine.Memory.WriteByte(idle + 0, 0xEB);
        machine.Memory.WriteByte(idle + 1, 0xFE);

        machine.Cpu.SetSegment(CpuNs.SegReg.Cs, 0);
        machine.Cpu.State.Eip = idle;
        machine.Cpu.State.Halted = false;
        uint stackBefore = machine.Cpu.State.Esp;

        machine.MoveMouse(5, 5);
        Assert.True(machine.MouseServices.TryDispatchEvent(machine.Cpu));
        Assert.Equal(handler, machine.Cpu.State.Eip);

        // Two frames must be on the stack, not one: the stub's address on
        // top, the guest's underneath. The topmost is what the handler's own
        // RETF pops, and it has to be the stub -- a handler is entitled to
        // leave with RETF 4 to drop arguments, and the extra frame is what
        // absorbs that instead of the guest's address being skipped past.
        Assert.Equal(stackBefore - 8, machine.Cpu.State.Esp);
        Assert.Equal(BiosRom.Entry.MouseCallbackReturn,
                     machine.Cpu.ReadMemoryWord(CpuNs.SegReg.Ss, machine.Cpu.State.Esp));
        Assert.Equal(0xF000, machine.Cpu.ReadMemoryWord(CpuNs.SegReg.Ss, machine.Cpu.State.Esp + 2));

        // The handler's RETF lands on the stub, still inside the ROM.
        machine.Cpu.Step();
        Assert.Equal(0xF000, machine.Cpu.State.Cs.Selector);
        Assert.Equal(BiosRom.Entry.MouseCallbackReturn, machine.Cpu.State.Eip);

        // The stub's RETF is what finally puts the guest back.
        machine.Cpu.Step();
        Assert.Equal(idle, machine.Cpu.State.Eip);
        Assert.Equal(0, machine.Cpu.State.Cs.Selector);
        Assert.Equal(stackBefore, machine.Cpu.State.Esp);
    }

    [Fact]
    public void NoHandlerIsCalledForAnEventItDidNotAskFor()
    {
        // The mask is how a program says which events it wants. Calling it
        // for the rest wastes time at best, and at worst runs a handler that
        // was written assuming only its chosen events could reach it.
        using Machine machine = Started();

        const uint handler = 0x3000;
        machine.Memory.WriteByte(handler, 0xCB);

        machine.Cpu.SetSegment(CpuNs.SegReg.Ss, 0);
        machine.Cpu.State.Esp = 0x7000;

        Call(machine, 0x0000);
        machine.Cpu.State.Cx = 0x0002;      // left button presses only
        machine.Cpu.State.Dx = (ushort)handler;
        Call(machine, 0x000C);

        machine.Cpu.SetSegment(CpuNs.SegReg.Cs, 0);
        machine.Cpu.State.Eip = 0x5000;

        machine.MoveMouse(5, 5);            // movement, which was not asked for
        Assert.False(machine.MouseServices.TryDispatchEvent(machine.Cpu));
        Assert.Equal(0x5000u, machine.Cpu.State.Eip);

        machine.SetMouseButton(MouseButtons.Left, true);
        Assert.True(machine.MouseServices.TryDispatchEvent(machine.Cpu));
    }

    [Fact]
    public void NothingIsDispatchedWhenNoHandlerHasBeenInstalled()
    {
        // The vector starts at zero, and a far call to 0000:0000 would run
        // whatever happens to be at the bottom of memory -- which is the
        // interrupt vector table, so execution would begin inside a pointer.
        //
        // The mask is set here without an address, which is the case that
        // actually needs guarding: a program that asks for events and leaves
        // the pointer null. With the mask clear the call is refused for the
        // unrelated reason that no event matched, and a missing null check
        // would go unnoticed.
        using Machine machine = Started();
        Call(machine, 0x0000);

        machine.Cpu.State.Cx = 0x0001;      // wanted: movement
        machine.Cpu.State.Dx = 0x0000;      // but nowhere to send it
        machine.Cpu.SetSegment(CpuNs.SegReg.Es, 0);
        Call(machine, 0x000C);

        machine.Cpu.SetSegment(CpuNs.SegReg.Cs, 0);
        machine.Cpu.SetSegment(CpuNs.SegReg.Ss, 0);
        machine.Cpu.State.Esp = 0x7000;
        machine.Cpu.State.Eip = 0x5000;

        machine.MoveMouse(5, 5);

        Assert.False(machine.MouseServices.TryDispatchEvent(machine.Cpu));

        // Nothing moved and nothing was pushed. The stack is the half that
        // matters: a call made and never returned from leaves four bytes
        // behind every time the mouse twitches, and the guest's stack is
        // eaten away until it collides with whatever is beneath it.
        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
        Assert.Equal(0x7000u, machine.Cpu.State.Esp);
    }

    [Fact]
    public void NoEventIsDeliveredIntoAHaltedCpu()
    {
        // A halted CPU is waiting for an interrupt. Redirecting it into a
        // handler resumes the guest at a point it never asked to run from,
        // and it never gets back to the HLT it was parked on -- so whatever
        // it was waiting for is waited for forever.
        using Machine machine = Started();

        machine.Memory.WriteByte(0x3000, 0xCB);
        machine.Cpu.SetSegment(CpuNs.SegReg.Ss, 0);
        machine.Cpu.State.Esp = 0x7000;

        Call(machine, 0x0000);
        machine.Cpu.State.Cx = 0x0001;
        machine.Cpu.State.Dx = 0x3000;
        Call(machine, 0x000C);

        machine.Cpu.SetSegment(CpuNs.SegReg.Cs, 0);
        machine.Cpu.State.Eip = 0x5000;
        machine.Cpu.State.Halted = true;

        machine.MoveMouse(5, 5);
        machine.RunInstructions(4000);

        Assert.Equal(0x5000u, machine.Cpu.State.Eip);
    }

    [Fact]
    public void ResetForgetsTheEventHandler()
    {
        // A program that exits without removing its handler leaves the driver
        // pointing at memory that is about to be somebody else's. The next
        // program's reset is what saves it -- if the reset actually clears
        // the pointer.
        using Machine machine = Started();

        machine.Memory.WriteByte(0x3000, 0xCB);
        machine.Cpu.SetSegment(CpuNs.SegReg.Ss, 0);
        machine.Cpu.State.Esp = 0x7000;

        Call(machine, 0x0000);
        machine.Cpu.State.Cx = 0x0001;
        machine.Cpu.State.Dx = 0x3000;
        Call(machine, 0x000C);

        Call(machine, 0x0000);              // a new program resets the driver

        machine.Cpu.SetSegment(CpuNs.SegReg.Cs, 0);
        machine.Cpu.State.Eip = 0x5000;
        machine.MoveMouse(5, 5);

        Assert.False(machine.MouseServices.TryDispatchEvent(machine.Cpu));
    }

    [Fact]
    public void AVectorDosPointedAtAnIretIsTakenBack()
    {
        // IO.SYS parks INT 33h on an IRET during its own initialisation; a
        // program probing afterwards would find no mouse. The machine notices
        // and puts the driver back.
        using Machine machine = Started();
        machine.Memory.WriteByte(0x7000, 0xCF);            // an IRET, as DOS leaves it
        machine.Memory.WriteWord(0x33 * 4, 0x0000);
        machine.Memory.WriteWord(0x33 * 4 + 2, 0x0700);

        machine.RunInstructions(200_000);

        Assert.Equal((ushort)BiosRom.Entry.Mouse, machine.Memory.ReadWord(0x33 * 4));
        Assert.Equal((ushort)0xF000, machine.Memory.ReadWord(0x33 * 4 + 2));
    }

    [Fact]
    public void AVectorPointingAtARealDriverIsLeftAlone()
    {
        using Machine machine = Started();
        machine.Memory.WriteByte(0x7000, 0x90);            // not an IRET: somebody's code
        machine.Memory.WriteWord(0x33 * 4, 0x0000);
        machine.Memory.WriteWord(0x33 * 4 + 2, 0x0700);

        machine.RunInstructions(200_000);

        Assert.Equal((ushort)0x0700, machine.Memory.ReadWord(0x33 * 4 + 2));
    }

    [Fact]
    public void TheTextPointerSwapsTheColoursOfTheCellUnderIt()
    {
        using Machine machine = Started();
        // A grey-on-black 'A' at column 3, row 2.
        uint cell = VgaMemory.TextWindowOffset + (2 * 80 + 3) * 2;
        machine.VideoMemory[cell] = (byte)'A';
        machine.VideoMemory[cell + 1] = 0x07;

        Call(machine, 0x0000);
        machine.Cpu.State.Cx = 3 * 8; machine.Cpu.State.Dx = 2 * 8;
        Call(machine, 0x0004);                              // position
        Call(machine, 0x0001);                              // show

        machine.Video.Render(force: true);
        // The cell's background is now the old foreground: light grey, not black.
        uint background = machine.Video.Framebuffer[(2 * 16) * machine.Video.Width + 3 * 8];
        Assert.NotEqual(0xFF000000u, background);

        // Hiding restores the cell on the next frame, and marks the row itself
        // dirty so the repaint happens without any memory write.
        Call(machine, 0x0002);
        Assert.True(machine.Video.Render());
        background = machine.Video.Framebuffer[(2 * 16) * machine.Video.Width + 3 * 8];
        Assert.Equal(0xFF000000u, background);
    }

    [Fact]
    public void FunctionTenSetsTheTextMasksAndFunctionNineTheShape()
    {
        using Machine machine = Started();
        Call(machine, 0x0000);

        machine.Cpu.State.Bx = 0; machine.Cpu.State.Cx = 0x00FF; machine.Cpu.State.Dx = 0x4700;
        Call(machine, 0x000A);
        Assert.Equal((ushort)0x00FF, machine.MouseServices.Pointer.TextScreenMask);
        Assert.Equal((ushort)0x4700, machine.MouseServices.Pointer.TextCursorMask);

        // 32 words at ES:DX: AND mask all ones, XOR mask a single top-left pixel.
        for (int row = 0; row < 16; row++)
        {
            machine.Memory.WriteWord(0x8000 + (uint)(row * 2), 0xFFFF);
            machine.Memory.WriteWord(0x8000 + 32 + (uint)(row * 2), row == 0 ? (ushort)0x8000 : (ushort)0);
        }
        machine.Cpu.SetSegment(SegReg.Es, 0x0800);
        machine.Cpu.State.Bx = 2; machine.Cpu.State.Cx = 3; machine.Cpu.State.Dx = 0;
        Call(machine, 0x0009);

        Assert.Equal(2, machine.MouseServices.Pointer.HotSpotX);
        Assert.Equal(3, machine.MouseServices.Pointer.HotSpotY);
        Assert.Equal((ushort)0x8000, machine.MouseServices.Pointer.XorMask[0]);
        Assert.Equal((ushort)0xFFFF, machine.MouseServices.Pointer.AndMask[5]);
    }
}
