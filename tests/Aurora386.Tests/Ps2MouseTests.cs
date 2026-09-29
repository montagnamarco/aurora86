using Aurora386.Core;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Devices.Input;
using Aurora386.Core.Devices.Interrupts;
using Aurora386.Core.Devices.Timing;
using Aurora386.Core.Devices.System;
using Aurora386.Core.IO;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// The PS/2 mouse and the controller it shares with the keyboard.
///
/// Almost everything that can go wrong with a PS/2 mouse goes wrong because
/// of that sharing: one data port, one output register, and a single status
/// bit to say which device a byte came from. A mistake there does not produce
/// a mouse that fails to work -- it produces a mouse whose packets are read
/// as keystrokes, or a keyboard that stops responding because a mouse byte is
/// sitting in front of every scancode.
/// </summary>
public class Ps2MouseTests
{
    private sealed class Rig : IDisposable
    {
        public PhysicalMemoryBus Memory { get; } = new();
        public InterruptController Interrupts { get; } = new();
        public IoBus Io { get; } = new();
        public Keyboard8042 Controller { get; }
        public MouseState State { get; } = new() { ConnectedInterface = MouseInterface.Ps2 };   // the port under test
        public Ps2Mouse Mouse { get; }

        public Rig()
        {
            Controller = new Keyboard8042(Interrupts, Memory);
            Controller.Register(Io);
            Interrupts.Register(Io);
            Mouse = new Ps2Mouse(State);
            Controller.AttachMouse(Mouse);

            // The PICs have to be initialised before they will deliver
            // anything, exactly as the BIOS does at boot.
            Io.WriteByte(0x20, 0x11); Io.WriteByte(0x21, 0x08);
            Io.WriteByte(0x21, 0x04); Io.WriteByte(0x21, 0x01);
            Io.WriteByte(0x21, 0x00);
            Io.WriteByte(0xA0, 0x11); Io.WriteByte(0xA1, 0x70);
            Io.WriteByte(0xA1, 0x02); Io.WriteByte(0xA1, 0x01);
            Io.WriteByte(0xA1, 0x00);
        }

        /// <summary>Sends a byte to the mouse the way a driver does.</summary>
        public void SendToMouse(byte value)
        {
            Io.WriteByte(0x64, 0xD4);
            Io.WriteByte(0x60, value);
        }

        public byte Status => Io.ReadByte(0x64);
        public byte Data => Io.ReadByte(0x60);

        /// <summary>Reads a byte and throws it away, to get past one that is not what is being checked.</summary>
        public void Skip() => Io.ReadByte(0x60);
        public bool OutputFull => (Status & 0x01) != 0;
        public bool FromMouse => (Status & 0x20) != 0;

        /// <summary>
        /// Brings the mouse up the way a driver does: enable the auxiliary
        /// port, turn on the auxiliary interrupt, then tell the mouse to
        /// report.
        /// </summary>
        public void EnableMouse()
        {
            Io.WriteByte(0x64, 0xA8);
            Io.WriteByte(0x64, 0x60);
            Io.WriteByte(0x60, 0x47);      // keyboard and auxiliary interrupts on
            SendToMouse(0xF4);
            Assert.Equal(0xFA, Data);      // the acknowledgement
        }

        public void Dispose() => Memory.Dispose();
    }

    [Fact]
    public void TheControllerRoutesACommandToTheMouseAndReturnsItsAcknowledgement()
    {
        // Command 0xD4 is the only way a driver can say anything to the
        // mouse. A controller that ignores it swallows the enable command,
        // and the mouse then sits there forever without sending a single
        // packet -- which looks exactly like no mouse being fitted.
        using var rig = new Rig();

        rig.SendToMouse(0xF4);

        Assert.True(rig.OutputFull);
        Assert.Equal(0xFA, rig.Data);
        Assert.True(rig.Mouse.Reporting);
    }

    [Fact]
    public void AMouseReplyIsFlaggedAsComingFromTheAuxiliaryPort()
    {
        // The reply to a mouse command must carry the auxiliary bit, because
        // the driver is waiting on IRQ 12 for it. Delivered as keyboard data
        // it would be handed to the keyboard handler instead, and the mouse
        // driver would time out waiting for an acknowledgement that had
        // already been eaten.
        using var rig = new Rig();

        rig.SendToMouse(0xF4);

        Assert.True(rig.FromMouse);
    }

    [Fact]
    public void MovementProducesAThreeBytePacketWithTheSignsAndButtonsInTheHeader()
    {
        using var rig = new Rig();
        rig.EnableMouse();

        // Left down, moving right and up. Up is the interesting direction:
        // the host counts Y downwards and the mouse counts it upwards, so a
        // negative host delta has to come out positive in the packet.
        rig.State.SetButton(MouseButtons.Left, true);
        rig.State.Move(5, -3);
        rig.Controller.PollMouse();

        byte header = rig.Data;

        Assert.Equal(MouseButtons.Left, (MouseButtons)(header & 0x07));
        Assert.Equal(0x08, header & 0x08);            // bit 3 always set
        Assert.Equal(0, header & 0x10);               // X is positive
        Assert.Equal(0, header & 0x20);               // Y is positive too, after inversion

        Assert.Equal(5, (sbyte)rig.Data);
        Assert.Equal(3, (sbyte)rig.Data);             // -3 downwards is +3 upwards
    }

    [Fact]
    public void TheVerticalAxisIsInvertedOnItsWayIntoThePacket()
    {
        // This is the whole of the "mouse moves the wrong way vertically"
        // bug, and it is invisible in any test that only moves horizontally.
        using var rig = new Rig();
        rig.EnableMouse();

        rig.State.Move(0, 4);            // downwards, as the host reports it
        rig.Controller.PollMouse();

        byte header = rig.Data;
        Assert.Equal(0x20, header & 0x20);   // the Y sign bit is set: negative
        rig.Skip();                           // the X count, which is zero here
        Assert.Equal(-4, (sbyte)rig.Data);
    }

    [Fact]
    public void EveryByteOfAPacketIsFlaggedAsComingFromTheMouse()
    {
        // Bit 5 of the status register is the only thing separating a
        // movement packet from a scancode. Flag only the first byte and the
        // driver hands the other two to the keyboard handler, which reads
        // them as key presses the user never made.
        using var rig = new Rig();
        rig.EnableMouse();

        rig.State.Move(3, 3);
        rig.Controller.PollMouse();

        for (int i = 0; i < 3; i++)
        {
            Assert.True(rig.OutputFull, $"byte {i} should be waiting");
            Assert.True(rig.FromMouse, $"byte {i} should be flagged as mouse data");
            rig.Skip();
        }
    }

    [Fact]
    public void AScancodeIsNeverFlaggedAsMouseData()
    {
        // The complement of the previous test, and the one that catches a
        // flag left set from a packet that has been read: a keystroke that
        // arrives looking like mouse data is handed to the mouse driver, and
        // the key is simply lost.
        using var rig = new Rig();
        rig.EnableMouse();

        rig.State.Move(1, 1);
        rig.Controller.PollMouse();
        rig.Skip(); rig.Skip(); rig.Skip();      // drain the packet

        rig.Controller.PushScanCode(0x1E);

        Assert.True(rig.OutputFull);
        Assert.False(rig.FromMouse);
        Assert.Equal(0x1E, rig.Data);
    }

    [Fact]
    public void AKeystrokeCannotSlipIntoTheMiddleOfAPacket()
    {
        // A packet has no framing of its own: three bytes, and nothing in
        // them says which is the first. A scancode delivered between two of
        // them is counted as part of the packet, and the driver stays out of
        // step for the rest of the session -- reading the buttons out of the
        // Y count forever after.
        using var rig = new Rig();
        rig.EnableMouse();

        rig.State.Move(7, 0);
        rig.Controller.PollMouse();

        byte first = rig.Data;
        Assert.Equal(0x08, first & 0x08);

        // A key is pressed halfway through the packet.
        rig.Controller.PushScanCode(0x1E);

        // The rest of the packet must come out first, still flagged as mouse
        // data, before the keystroke gets a turn.
        Assert.True(rig.FromMouse);
        Assert.Equal(7, (sbyte)rig.Data);
        Assert.True(rig.FromMouse);
        Assert.Equal(0, (sbyte)rig.Data);

        Assert.False(rig.FromMouse);
        Assert.Equal(0x1E, rig.Data);
    }

    [Fact]
    public void APacketRaisesIrqTwelveAndNotIrqOne()
    {
        // The mouse's line is IRQ 12 and the keyboard's is IRQ 1. Raise the
        // wrong one and the guest runs its keyboard handler for a movement
        // packet, which reads the byte and treats it as a scancode.
        using var rig = new Rig();
        rig.EnableMouse();

        rig.State.Move(2, 2);
        rig.Controller.PollMouse();

        // IRQ 12 lives on the slave controller, which reaches the CPU through
        // the master's line 2. Vector 0x70 + 4 is IRQ 12's.
        Assert.Equal(0x74, rig.Interrupts.Master.GetPendingVector());
    }

    [Fact]
    public void NoInterruptIsRaisedWhileTheAuxiliaryInterruptIsDisabled()
    {
        // Bit 1 of the command byte is the driver's way of saying it polls
        // rather than taking the interrupt. An IRQ 12 it did not ask for
        // sends it into a handler it never installed.
        using var rig = new Rig();

        rig.Io.WriteByte(0x64, 0xA8);       // auxiliary port on...
        rig.Io.WriteByte(0x64, 0x60);
        rig.Io.WriteByte(0x60, 0x45);       // ...but its interrupt off
        rig.SendToMouse(0xF4);
        rig.Skip();

        rig.State.Move(2, 2);
        rig.Controller.PollMouse();

        Assert.Equal(-1, rig.Interrupts.Master.GetPendingVector());

        // The data is still there to be polled; it is only the interrupt that
        // was withheld.
        Assert.True(rig.OutputFull);
        Assert.True(rig.FromMouse);
    }

    [Fact]
    public void AMouseThatHasNotBeenEnabledSendsNothing()
    {
        // A mouse powers on quiet and stays quiet until 0xF4. One that
        // streams packets before the driver has finished initialising has
        // them read as replies to the commands the driver is still sending.
        using var rig = new Rig();
        rig.Io.WriteByte(0x64, 0xA8);

        rig.State.Move(10, 10);
        rig.Controller.PollMouse();

        Assert.False(rig.OutputFull);
    }

    [Fact]
    public void ResettingTheMouseAnswersWithTheAcknowledgementSelfTestAndDeviceId()
    {
        // A driver reads all three bytes. One that gets fewer concludes the
        // mouse failed its self-test and gives up on it.
        using var rig = new Rig();

        rig.SendToMouse(0xFF);

        Assert.Equal(0xFA, rig.Data);
        Assert.Equal(0xAA, rig.Data);
        Assert.Equal(0x00, rig.Data);
    }

    [Fact]
    public void TheMouseIdentifiesItselfAsAPlainTwoButtonMouse()
    {
        // Answering 0x03 would claim a wheel, and the driver would then
        // expect four bytes per packet from a device that sends three -- it
        // would be permanently a byte out of step.
        using var rig = new Rig();

        rig.SendToMouse(0xF2);

        Assert.Equal(0xFA, rig.Data);
        Assert.Equal(0x00, rig.Data);
    }

    [Fact]
    public void DisablingReportingStopsPacketsWithoutSilencingCommands()
    {
        using var rig = new Rig();
        rig.EnableMouse();

        rig.SendToMouse(0xF5);
        Assert.Equal(0xFA, rig.Data);

        rig.State.Move(5, 5);
        rig.Controller.PollMouse();
        Assert.False(rig.OutputFull);

        // Still answering, though: a disabled mouse is not a deaf one.
        rig.SendToMouse(0xF2);
        Assert.Equal(0xFA, rig.Data);
    }

    [Fact]
    public void TheStatusRequestReportsBackTheResolutionThatWasSet()
    {
        // Drivers write a setting and read it back to confirm it took. Fixed
        // constants pass a casual test and fail this one.
        using var rig = new Rig();

        rig.SendToMouse(0xE8); Assert.Equal(0xFA, rig.Data);
        rig.SendToMouse(0x03); Assert.Equal(0xFA, rig.Data);
        rig.SendToMouse(0xF3); Assert.Equal(0xFA, rig.Data);
        rig.SendToMouse(0xC8); Assert.Equal(0xFA, rig.Data);   // 200 samples/sec

        rig.SendToMouse(0xE9);
        Assert.Equal(0xFA, rig.Data);
        rig.Skip();                              // the status byte
        Assert.Equal(0x03, rig.Data);            // resolution
        Assert.Equal(200, rig.Data);             // sample rate
    }

    [Fact]
    public void ACommandParameterIsNotMistakenForACommand()
    {
        // 0xF3 takes a parameter. Read the parameter back as a command and
        // 200 is not one, so the mouse answers with an error in the middle of
        // a perfectly valid exchange -- and the driver decides it is faulty.
        using var rig = new Rig();

        rig.SendToMouse(0xF3);
        Assert.Equal(0xFA, rig.Data);

        rig.SendToMouse(0xC8);           // 200, which is not a valid command
        Assert.Equal(0xFA, rig.Data);    // acknowledged as data, not refused
    }

    [Fact]
    public void MovementLargerThanOnePacketIsCarriedOverRatherThanTruncated()
    {
        // Nine signed bits per axis is not much for a fast flick. Truncating
        // the value makes the pointer leap to the wrong place; carrying the
        // remainder makes it lag by one packet instead, which nobody sees.
        using var rig = new Rig();
        rig.EnableMouse();

        rig.State.Move(400, 0);
        rig.Controller.PollMouse();

        byte header = rig.Data;
        Assert.Equal(0, header & 0x10);        // positive
        Assert.Equal(255, rig.Data);
        rig.Skip();

        // The remaining 145 counts come out in the next packet.
        rig.Controller.PollMouse();
        rig.Skip();
        Assert.Equal(145, rig.Data);
    }

    [Fact]
    public void AButtonPressWithNoMovementStillProducesAPacket()
    {
        // A click made without moving the mouse must be reported, otherwise
        // a mouse held still cannot be clicked at all.
        using var rig = new Rig();
        rig.EnableMouse();

        rig.State.SetButton(MouseButtons.Right, true);
        rig.Controller.PollMouse();

        byte header = rig.Data;
        Assert.Equal(MouseButtons.Right, (MouseButtons)(header & 0x07));
        Assert.Equal(0, (sbyte)rig.Data);
        Assert.Equal(0, (sbyte)rig.Data);
    }

    [Fact]
    public void NothingIsDeliveredWhileTheAuxiliaryPortIsDisabled()
    {
        // System software shuts the auxiliary port while it interrogates the
        // controller, exactly as it does the keyboard, so that a packet
        // cannot wedge itself between a command and its reply.
        using var rig = new Rig();
        rig.EnableMouse();

        rig.Io.WriteByte(0x64, 0xA7);       // disable the auxiliary port

        rig.State.Move(4, 4);
        rig.Controller.PollMouse();
        Assert.False(rig.OutputFull);

        // Re-enabling releases what was held back rather than losing it.
        rig.Io.WriteByte(0x64, 0xA8);
        Assert.True(rig.OutputFull);
        Assert.True(rig.FromMouse);
    }

    [Fact]
    public void TheAuxiliaryPortTestReportsAFaultWhenNoMouseIsFitted()
    {
        // Some BIOSes stop looking for a mouse on anything but a zero here,
        // so the answer has to follow whether one is actually attached rather
        // than being a cheerful constant.
        using var withMouse = new Rig();
        withMouse.Io.WriteByte(0x64, 0xA9);
        Assert.Equal(0x00, withMouse.Data);

        using var bare = new Rig();
        var controller = new Keyboard8042(bare.Interrupts, bare.Memory);
        var io = new IoBus();
        controller.Register(io);

        io.WriteByte(0x64, 0xA9);
        Assert.Equal(0x02, io.ReadByte(0x60));
    }

    [Fact]
    public void ARequestedWakeupPacketIsEmptyAndRaisesTheInterrupt()
    {
        // The VMware backdoor's absolute pointer channel (docs/VMWARE-BACKDOOR.md,
        // section 4) has no interrupt of its own; it borrows the PS/2
        // mouse's IRQ 12 by asking for an empty packet -- header 08h, no
        // motion -- purely to raise it, even though nothing about this mouse
        // has changed.
        using var rig = new Rig();
        rig.EnableMouse();

        rig.Mouse.RequestWakeupPacket();
        rig.Controller.PollMouse();

        Assert.True(rig.OutputFull);
        Assert.Equal(0x74, rig.Interrupts.Master.GetPendingVector());   // IRQ 12

        byte header = rig.Data;
        Assert.Equal(0x08, header);            // no buttons, no sign bits, bit 3 set
        Assert.Equal(0, (sbyte)rig.Data);      // dx
        Assert.Equal(0, (sbyte)rig.Data);      // dy
    }

    [Fact]
    public void ARequestedWakeupPacketIsSentExactlyOncePerRequest()
    {
        // A second poll with nothing new to report -- and no fresh request --
        // must go back to being silent, or the borrowed IRQ would fire on
        // every poll forever after a single ABSPOINTER_DATA queue event.
        using var rig = new Rig();
        rig.EnableMouse();

        rig.Mouse.RequestWakeupPacket();
        rig.Controller.PollMouse();
        rig.Skip(); rig.Skip(); rig.Skip();      // drain the one wakeup packet

        rig.Controller.PollMouse();

        Assert.False(rig.OutputFull, "a second poll without a new request must send nothing");
    }

    [Fact]
    public void AWakeupRequestDoesNothingWhileReportingIsOff()
    {
        // The PS/2 mouse only volunteers packets in stream mode; a driver
        // that has not yet enabled it is not listening for IRQ 12 either.
        using var rig = new Rig();

        rig.Mouse.RequestWakeupPacket();
        rig.Controller.PollMouse();

        Assert.False(rig.OutputFull);
    }

    [Fact]
    public void ARealMovementSatisfiesAPendingWakeupRequestWithoutAnExtraPacket()
    {
        // If the PS/2 mouse already has real news, that packet raises IRQ 12
        // on its own; the wakeup request is satisfied by it rather than
        // adding a second, empty packet behind it.
        using var rig = new Rig();
        rig.EnableMouse();

        rig.Mouse.RequestWakeupPacket();
        rig.State.Move(5, 0);
        rig.Controller.PollMouse();

        byte header = rig.Data;
        Assert.Equal(5, (sbyte)rig.Data);       // the real motion, not an empty packet
        rig.Skip();

        rig.Controller.PollMouse();
        Assert.False(rig.OutputFull, "the wakeup request must not queue a second packet");
    }

    [Fact]
    public void AMachineFittedWithAMouseUnmasksIrqTwelve()
    {
        // The mouse's line has to be open on the slave controller and the
        // cascade open on the master. Leave either masked and the packets are
        // built, queued, and never delivered -- a mouse that is perfectly
        // functional and completely invisible.
        using var machine = new Machine();
        machine.MouseInterface = MouseInterface.Ps2;   // these tests exercise the PS/2 port
        machine.RunInstructions(2000);

        machine.Io.WriteByte(0x64, 0xA8);
        machine.Io.WriteByte(0x64, 0x60);
        machine.Io.WriteByte(0x60, 0x47);
        machine.Io.WriteByte(0x64, 0xD4);
        machine.Io.WriteByte(0x60, 0xF4);
        machine.Io.ReadByte(0x60);

        machine.MoveMouse(3, 3);
        machine.Keyboard.PollMouse();

        Assert.Equal(0x74, machine.Interrupts.Master.GetPendingVector());
    }
}
