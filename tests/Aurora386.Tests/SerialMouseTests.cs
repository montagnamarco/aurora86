using Aurora386.Core;
using Aurora386.Core.Devices;
using Aurora386.Core.IO;

namespace Aurora386.Tests;

/// <summary>
/// The Microsoft serial mouse on COM1.
///
/// This protocol is entirely a product of its constraints: 1200 baud, seven
/// data bits, and no way for the driver to ask the mouse anything. Everything
/// awkward about it -- the movement counts split across three bytes, the
/// single framing bit, the identification sent on power-up -- exists because
/// those were the only bits available.
/// </summary>
public class SerialMouseTests
{
    private sealed class Rig
    {
        public InterruptController Interrupts { get; } = new();
        public IoBus Io { get; } = new();
        public MouseState State { get; } = new();
        public SerialMouse Mouse { get; }

        public Rig()
        {
            // This rig is a machine with the mouse plugged into COM1, which
            // has to be said out loud: the shared state feeds whichever port
            // the mouse is actually in, and it defaults to PS/2 because that
            // is what a late 386 shipped with.
            State.ConnectedInterface = MouseInterface.Serial;

            Mouse = new SerialMouse(State, Interrupts);
            Mouse.Register(Io);
            Interrupts.Register(Io);

            Io.WriteByte(0x20, 0x11); Io.WriteByte(0x21, 0x08);
            Io.WriteByte(0x21, 0x04); Io.WriteByte(0x21, 0x01);
            Io.WriteByte(0x21, 0x00);
        }

        private const ushort Base = SerialMouse.BasePort;

        public byte Read(int register) => Io.ReadByte((ushort)(Base + register));
        public void Write(int register, byte value) => Io.WriteByte((ushort)(Base + register), value);

        public bool DataWaiting => (Read(5) & 0x01) != 0;

        /// <summary>
        /// Powers the mouse up the way a driver does, by raising DTR and RTS,
        /// and swallows the identification byte it sends back.
        /// </summary>
        public void PowerUp(bool wantInterrupts = false)
        {
            if (wantInterrupts) Write(1, 0x01);
            Write(4, 0x03);
            Assert.Equal((byte)'M', Read(0));
        }
    }

    [Fact]
    public void TheMouseAnnouncesItselfWithTheLetterMWhenTheDriverPowersItUp()
    {
        // This single byte is the whole of serial mouse detection. A driver
        // that does not see it concludes the port is empty, and never reads
        // another thing from it -- so a mouse that sends perfect packets but
        // no 'M' does not exist as far as any driver is concerned.
        var rig = new Rig();

        rig.Write(4, 0x03);          // DTR and RTS

        Assert.True(rig.DataWaiting);
        Assert.Equal((byte)'M', rig.Read(0));
    }

    [Fact]
    public void NothingIsAnnouncedUntilBothHandshakeLinesAreRaised()
    {
        // The mouse takes its power from those lines. With only one of them
        // up it is not running, and answering anyway would have the driver
        // find a mouse on a port it has not finished opening.
        var rig = new Rig();

        rig.Write(4, 0x01);          // DTR alone
        Assert.False(rig.DataWaiting);

        rig.Write(4, 0x02);          // RTS alone
        Assert.False(rig.DataWaiting);

        rig.Write(4, 0x03);
        Assert.True(rig.DataWaiting);
    }

    [Fact]
    public void AnUnpoweredMouseReportsNoMovementAtAll()
    {
        var rig = new Rig();

        rig.State.Move(10, 10);
        rig.Mouse.Poll();

        Assert.False(rig.DataWaiting);
    }

    [Fact]
    public void AReportSplitsTheMovementCountsAcrossTheThreeBytes()
    {
        // The counts are seven and eight bits wide but the bytes have only
        // six spare bits each, so the top two bits of each count ride in the
        // first byte. Reassembling them wrongly gives a pointer that moves
        // correctly for small movements and jumps for large ones -- which is
        // the hardest kind of bug to notice.
        var rig = new Rig();
        rig.PowerUp();

        rig.State.Move(0x25, 0x1A);
        rig.Mouse.Poll();

        byte first = rig.Read(0);
        byte second = rig.Read(0);
        byte third = rig.Read(0);

        Assert.Equal(0x40, first & 0x40);       // the framing bit, on byte one only
        Assert.Equal(0, second & 0x40);
        Assert.Equal(0, third & 0x40);

        int x = ((first & 0x03) << 6) | (second & 0x3F);
        int y = (((first >> 2) & 0x03) << 6) | (third & 0x3F);

        Assert.Equal(0x25, x);
        Assert.Equal(0x1A, y);
    }

    [Fact]
    public void NegativeMovementSurvivesTheRoundTripAsASignedEightBitValue()
    {
        // Movement is signed and the sign lives in the bits carried up into
        // the first byte. Lose them and leftwards movement comes out as a
        // large rightwards one: the pointer flies to the right edge whenever
        // the mouse is pulled back.
        var rig = new Rig();
        rig.PowerUp();

        rig.State.Move(-5, -9);
        rig.Mouse.Poll();

        byte first = rig.Read(0);
        byte second = rig.Read(0);
        byte third = rig.Read(0);

        var x = (sbyte)(((first & 0x03) << 6) | (second & 0x3F));
        var y = (sbyte)((((first >> 2) & 0x03) << 6) | (third & 0x3F));

        Assert.Equal(-5, x);
        Assert.Equal(-9, y);
    }

    [Fact]
    public void TheVerticalAxisIsNotInvertedOnTheSerialProtocol()
    {
        // Unlike the PS/2 packet, the Microsoft protocol counts Y downwards,
        // the same way the screen does. Applying the PS/2 inversion here as
        // well gives a mouse that works upside down on one port and correctly
        // on the other.
        var rig = new Rig();
        rig.PowerUp();

        rig.State.Move(0, 7);        // downwards
        rig.Mouse.Poll();

        byte first = rig.Read(0);
        rig.Read(0);
        byte third = rig.Read(0);

        var y = (sbyte)((((first >> 2) & 0x03) << 6) | (third & 0x3F));
        Assert.Equal(7, y);
    }

    [Fact]
    public void TheButtonsAppearInTheFirstByte()
    {
        var rig = new Rig();
        rig.PowerUp();

        rig.State.SetButton(MouseButtons.Left, true);
        rig.Mouse.Poll();

        byte first = rig.Read(0);
        Assert.Equal(0x20, first & 0x20);   // left
        Assert.Equal(0, first & 0x10);      // right is not down

        rig.Read(0); rig.Read(0);

        rig.State.SetButton(MouseButtons.Left, false);
        rig.State.SetButton(MouseButtons.Right, true);
        rig.Mouse.Poll();

        first = rig.Read(0);
        Assert.Equal(0, first & 0x20);
        Assert.Equal(0x10, first & 0x10);
    }

    [Fact]
    public void AClickWithNoMovementStillProducesAReport()
    {
        // A serial mouse has no reason of its own to speak unless something
        // changed, so a button change has to be one of the things that counts
        // as a change. Otherwise a mouse held perfectly still cannot be
        // clicked -- and a user clicking a button does tend to hold still.
        var rig = new Rig();
        rig.PowerUp();

        rig.State.SetButton(MouseButtons.Left, true);
        rig.Mouse.Poll();

        Assert.True(rig.DataWaiting);
        Assert.Equal(0x20, rig.Read(0) & 0x20);
    }

    [Fact]
    public void ASecondReportDoesNotStartUntilTheFirstHasBeenReadOut()
    {
        // The framing bit is all a driver has to find the start of a packet.
        // Queue a second packet behind an unread first and the driver reads
        // six bytes as two packets correctly -- but interleave them and it is
        // lost for good.
        var rig = new Rig();
        rig.PowerUp();

        rig.State.Move(3, 0);
        rig.Mouse.Poll();
        rig.State.Move(4, 0);
        rig.Mouse.Poll();

        // Exactly one packet's worth is waiting; the second movement is still
        // in the accumulator.
        Assert.Equal(0x40, rig.Read(0) & 0x40);
        Assert.Equal(0, rig.Read(0) & 0x40);
        Assert.Equal(0, rig.Read(0) & 0x40);
        Assert.False(rig.DataWaiting);

        // And it comes out on the next poll, not lost.
        rig.Mouse.Poll();
        Assert.True(rig.DataWaiting);
        byte first = rig.Read(0);
        byte second = rig.Read(0);
        Assert.Equal(4, ((first & 0x03) << 6) | (second & 0x3F));
    }

    [Fact]
    public void TheDivisorLatchRemapsTheFirstTwoRegisters()
    {
        // Software sets the latch bit, writes the baud divisor, and clears it
        // again. A UART that forgets the bit hands back a data byte where the
        // divisor should be, and the driver decides the port is faulty.
        var rig = new Rig();

        rig.Write(3, 0x80);          // latch on
        rig.Write(0, 0x60);          // 1200 baud
        rig.Write(1, 0x00);
        Assert.Equal(0x60, rig.Read(0));
        Assert.Equal(0x00, rig.Read(1));

        rig.Write(3, 0x02);          // latch off: 7 data bits
        // The same register is now the receive buffer, which is empty.
        Assert.Equal(0x00, rig.Read(0));
    }

    [Fact]
    public void TheTransmitterAlwaysReportsItselfEmpty()
    {
        // A driver that writes to the port waits for these bits before
        // sending the next byte. A transmitter that never empties hangs it
        // forever, which looks like the machine freezing on startup.
        var rig = new Rig();

        byte status = rig.Read(5);
        Assert.Equal(0x20, status & 0x20);
        Assert.Equal(0x40, status & 0x40);
    }

    [Fact]
    public void TheHandshakeLinesAreLoopedBackSoTheDriverSeesItsOwnSignals()
    {
        // A mouse has no modem to assert CTS and DSR, but drivers check them.
        // Looping the outputs back is what a three-wire cable does anyway.
        var rig = new Rig();

        Assert.Equal(0, rig.Read(6));

        rig.Write(4, 0x03);
        byte modem = rig.Read(6);
        Assert.Equal(0x10, modem & 0x10);   // CTS follows RTS
        Assert.Equal(0x80, modem & 0x80);   // DCD follows DTR
    }

    [Fact]
    public void ReceivedDataRaisesIrqFourOnlyWhenTheDriverAskedForIt()
    {
        // Bit 0 of the interrupt enable register is that request. An
        // interrupt the driver did not ask for runs a handler it never
        // installed.
        var quiet = new Rig();
        quiet.Write(4, 0x03);
        Assert.Equal(-1, quiet.Interrupts.Master.GetPendingVector());

        var listening = new Rig();
        listening.Write(1, 0x01);
        listening.Write(4, 0x03);
        Assert.Equal(0x0C, listening.Interrupts.Master.GetPendingVector());   // IRQ 4
    }

    [Fact]
    public void TheInterruptStaysAssertedUntilTheWholePacketHasBeenRead()
    {
        // The UART asserts its line while data is waiting, so a driver that
        // reads one byte of three must still find the line asserted for the
        // rest. Dropping it after the first byte leaves two bytes stranded
        // and the driver a byte out of step from then on.
        var rig = new Rig();
        rig.PowerUp(wantInterrupts: true);

        rig.State.Move(5, 5);
        rig.Mouse.Poll();

        rig.Read(0);
        Assert.True(rig.DataWaiting);
        Assert.Equal(0x04, rig.Read(2) & 0x04);   // "received data available"

        rig.Read(0);
        rig.Read(0);
        Assert.False(rig.DataWaiting);
        Assert.Equal(0x01, rig.Read(2) & 0x01);   // no interrupt pending
    }

    [Fact]
    public void CuttingThePowerDiscardsAnythingInFlight()
    {
        // The driver drops the lines precisely so the mouse starts from a
        // known state. A packet that survived the power cut would be read as
        // the start of the identification sequence.
        var rig = new Rig();
        rig.PowerUp();

        rig.State.Move(9, 9);
        rig.Mouse.Poll();
        Assert.True(rig.DataWaiting);

        rig.Write(4, 0x00);
        Assert.False(rig.DataWaiting);

        // And powering back up starts again with the identification.
        rig.Write(4, 0x03);
        Assert.Equal((byte)'M', rig.Read(0));
    }

    [Fact]
    public void TheBiosPublishesComOnesAddressSoADriverCanFindIt()
    {
        // A serial mouse driver does not probe for the port: it reads the
        // address out of the BIOS data area. Leave that word zero, as it was
        // when no port was fitted, and the driver concludes there is no COM1
        // and never touches 0x3F8 at all.
        using var machine = new Machine();
        machine.RunInstructions(2000);

        Assert.Equal(SerialMouse.BasePort, machine.Bda.Com1Port);

        // And the equipment word has to agree, since some software counts the
        // ports there instead.
        Assert.NotEqual(0, (machine.Bda.EquipmentWord >> 9) & 0x07);
    }
}

/// <summary>
/// The BIOS data area and INT 14h have to agree about which serial ports
/// exist.
///
/// They are two answers to the same question, and software asks both: it
/// reads the data area to find the port address, then asks the BIOS about the
/// port. Told the port is there and then told nothing is connected to it, a
/// driver concludes the hardware has failed -- which is worse than saying
/// there is no port at all, because a missing port it would simply skip.
/// </summary>
public class SerialPortConsistencyTests
{
    private static Machine PoweredOn()
    {
        var machine = new Machine();
        machine.RunInstructions(200);
        return machine;
    }

    /// <summary>
    /// Executes a real INT 14h AH=03 in the guest and returns AX.
    ///
    /// Going through the interrupt rather than calling the handler directly
    /// is the point: it is the path software takes, and it exercises the
    /// vector as well as the code behind it.
    /// </summary>
    private static ushort Status(Machine machine, ushort port)
    {
        const uint CodeAddress = 0x7000;

        machine.Cpu.State.SetReg8(4, 0x03);
        machine.Cpu.State.Dx = port;

        machine.Memory.WriteByte(CodeAddress, 0xCD);       // INT
        machine.Memory.WriteByte(CodeAddress + 1, 0x14);

        machine.Cpu.SetSegment(Aurora386.Core.Cpu.SegReg.Cs, 0);
        machine.Cpu.State.Eip = CodeAddress;
        machine.Cpu.State.Halted = false;

        // The INT itself, then the handler behind it.
        machine.RunInstructions(4);

        return machine.Cpu.State.Ax;
    }

    [Fact]
    public void TheDataAreaDeclaresThePortTheMouseIsOn()
    {
        // A serial mouse driver does not probe the hardware looking for a
        // port: it reads this word and believes it.
        using Machine machine = PoweredOn();

        Assert.Equal(0x3F8, machine.Bda.Com1Port);
    }

    [Fact]
    public void TheDeclaredPortReportsItselfReady()
    {
        using Machine machine = PoweredOn();

        ushort status = Status(machine, 0);
        byte lineStatus = (byte)(status >> 8);

        // Bit 5 says a byte may be written, bit 6 that the transmitter is
        // idle. With both clear nothing can ever be sent, and a driver that
        // waits for them waits for ever.
        Assert.Equal(0x20, lineStatus & 0x20);
        Assert.Equal(0x40, lineStatus & 0x40);
    }

    [Fact]
    public void TheOtherPortsStillReportNothing()
    {
        // Only COM1 exists. Claiming the rest would have software enumerate
        // four ports and wait on three that will never answer.
        using Machine machine = PoweredOn();

        Assert.Equal(0, Status(machine, 1));
        Assert.Equal(0, Status(machine, 2));
        Assert.Equal(0, Status(machine, 3));
    }
}
