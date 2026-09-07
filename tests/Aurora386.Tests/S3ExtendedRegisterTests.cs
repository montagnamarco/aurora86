using Aurora386.Core;

namespace Aurora386.Tests;

/// <summary>
/// The S3's extended CRTC registers, and the lock that guards them.
///
/// A VGA has 25 CRTC registers ending at 0x18. Everything an S3 adds sits
/// above 0x30, and it is locked at power-on: writes are dropped and reads
/// come back as all ones until software writes 0x48 to register 0x38, and
/// 0xA0 or more to 0x39 for the range above 0x40.
///
/// That is not security -- every S3 driver has the values, and they were
/// published. It is a guard against software written for a plain VGA
/// stumbling into registers it does not know exist. On a real card the
/// extended range includes the pixel clock, and a stray write there leaves
/// the monitor showing nothing at all.
///
/// One register is exempt: 0x30, the chip identifier. A driver has to be able
/// to ask what card this is before it can know which unlock sequence to use,
/// so that one answers whether or not anything has been unlocked.
/// </summary>
public class S3ExtendedRegisterTests
{
    private static Machine Started()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        return machine;
    }

    private static void Write(Machine machine, int index, byte value)
    {
        machine.Io.WriteByte(0x3D4, (byte)index);
        machine.Io.WriteByte(0x3D5, value);
    }

    private static byte Read(Machine machine, int index)
    {
        machine.Io.WriteByte(0x3D4, (byte)index);
        return machine.Io.ReadByte(0x3D5);
    }

    [Fact]
    public void ALockedRegisterStillAnswers()
    {
        // The lock protects writes, not reads. On the silicon the register is
        // there and readable, and what the lock stops is software written for
        // a plain VGA from changing it by accident.
        //
        // The difference is not academic: 0xFF is what the bus puts on the
        // wire when nothing responds, so a locked register answering 0xFF
        // tells every driver that the card is not there at all.
        using Machine machine = Started();

        Assert.Equal(0xE1, Read(machine, 0x30));      // Trio64
        Assert.Equal(0x00, Read(machine, 0x31));      // readable, and empty
    }

    [Fact]
    public void WritesToLockedRegistersAreDropped()
    {
        using Machine machine = Started();

        Write(machine, 0x31, 0x0A);

        Write(machine, 0x38, 0x48);                   // unlock
        Assert.Equal(0x00, Read(machine, 0x31));      // the write never landed
    }

    [Fact]
    public void UnlockingMakesTheRegistersOrdinaryStorage()
    {
        using Machine machine = Started();

        Write(machine, 0x38, 0x48);
        Write(machine, 0x31, 0x0A);

        Assert.Equal(0x0A, Read(machine, 0x31));
    }

    [Fact]
    public void TheRangeAboveFortyNeedsItsOwnUnlock()
    {
        // Two locks, not one: 0x38 opens 0x30-0x3F and 0x39 opens the rest.
        using Machine machine = Started();

        // With only the first lock open, a write above 0x40 goes nowhere --
        // though the register still reads back, as any register does.
        Write(machine, 0x38, 0x48);
        Write(machine, 0x51, 0x77);
        Assert.Equal(0x00, Read(machine, 0x51));

        Write(machine, 0x39, 0xA5);
        Write(machine, 0x51, 0x33);
        Assert.Equal(0x33, Read(machine, 0x51));
    }

    [Fact]
    public void AWrongValueLocksThemAgain()
    {
        // The lock is the value, not the act of writing: a driver puts the
        // registers back by writing something else.
        using Machine machine = Started();

        Write(machine, 0x38, 0x48);
        Write(machine, 0x31, 0x0A);
        Assert.Equal(0x0A, Read(machine, 0x31));

        // Locked again: the value stays readable, but a further write is
        // dropped.
        Write(machine, 0x38, 0x00);
        Write(machine, 0x31, 0x55);
        Assert.Equal(0x0A, Read(machine, 0x31));
    }

    [Fact]
    public void TheCardReportsItsMemorySize()
    {
        // Bits 6-5 of register 0x36 say how much: 2 means two megabytes,
        // which is what a Trio64 shipped with and what 1024x768 at 16-bit
        // colour needs.
        using Machine machine = Started();

        Assert.Equal(2, (Read(machine, 0x36) >> 5) & 3);
    }

    [Fact]
    public void AModeSetDoesNotRelockTheExtensions()
    {
        // A BIOS mode set programs the standard controller and leaves the
        // card's own registers alone. Clearing them would lose the
        // configuration a driver had set up -- and on real hardware a mode
        // set most certainly does not do that.
        using Machine machine = Started();

        Write(machine, 0x38, 0x48);
        Write(machine, 0x39, 0xA5);
        Write(machine, 0x51, 0x33);

        machine.VideoRegisters.VideoMode = 0x12;

        Assert.Equal(0x33, Read(machine, 0x51));
    }

    [Fact]
    public void TheVgaRegistersBelowThirtyAreNeverLocked()
    {
        // The lock is about the extensions. Everything a plain VGA has stays
        // reachable, or nothing written before 1991 would run.
        using Machine machine = Started();

        Write(machine, 0x0C, 0x12);
        Write(machine, 0x0D, 0x34);

        Assert.Equal(0x12, Read(machine, 0x0C));
        Assert.Equal(0x34, Read(machine, 0x0D));
    }
}

/// <summary>
/// The S3's extended sequencer registers, and the clock synthesiser among
/// them.
///
/// A VGA's sequencer ends at 0x04 and its pixel clock comes from one of two
/// crystals -- which is why a VGA has the resolutions it has and no others.
/// An SVGA has a synthesiser instead: registers 0x12 and 0x13 hold a
/// multiplier and a divider that turn one reference frequency into any pixel
/// clock, and that is what makes 800x600 and 1024x768 possible.
///
/// There is nothing to synthesise here -- this emulator draws a frame when
/// asked, not when a clock says so -- but the registers must hold what is
/// written, because a driver programs them and reads them back to check the
/// card accepted the mode.
/// </summary>
public class S3SequencerTests
{
    private static Machine Started()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        return machine;
    }

    private static void Write(Machine machine, int index, byte value)
    {
        machine.Io.WriteByte(0x3C4, (byte)index);
        machine.Io.WriteByte(0x3C5, value);
    }

    private static byte Read(Machine machine, int index)
    {
        machine.Io.WriteByte(0x3C4, (byte)index);
        return machine.Io.ReadByte(0x3C5);
    }

    [Fact]
    public void TheExtendedRegistersAreLockedAtPowerOn()
    {
        // Same idea as the CRTC's lock, and for the same reason: a wrong
        // pixel clock takes the monitor out of range and shows nothing, so
        // software written for a plain VGA must not reach these by accident.
        using Machine machine = Started();

        Write(machine, 0x12, 0x40);
        Assert.Equal(0x00, Read(machine, 0x12));
    }

    [Fact]
    public void WritingSixToRegisterEightUnlocksThem()
    {
        using Machine machine = Started();

        Write(machine, 0x08, 0x06);
        Write(machine, 0x12, 0x40);

        Assert.Equal(0x40, Read(machine, 0x12));
    }

    [Fact]
    public void TheLockRegisterReadsBackWhatUnlocksIt()
    {
        // How a driver checks that it succeeded, rather than assuming.
        using Machine machine = Started();

        Assert.Equal(0x00, Read(machine, 0x08));

        Write(machine, 0x08, 0x06);
        Assert.Equal(0x06, Read(machine, 0x08));
    }

    [Fact]
    public void TheClockRegistersKeepWhatADriverProgrammed()
    {
        // 0x12 and 0x13 are the synthesiser's divider and multiplier. A
        // driver writes them and reads them back, and a card that lost the
        // value would look like one that refused the mode.
        using Machine machine = Started();
        Write(machine, 0x08, 0x06);

        Write(machine, 0x12, 0x21);
        Write(machine, 0x13, 0x4D);

        Assert.Equal(0x21, Read(machine, 0x12));
        Assert.Equal(0x4D, Read(machine, 0x13));
    }

    [Fact]
    public void TheVgaSequencerRegistersAreNeverLocked()
    {
        // Everything a plain VGA has stays reachable, or no graphics mode
        // would work at all.
        using Machine machine = Started();

        Write(machine, 0x02, 0x0F);          // map mask
        Assert.Equal(0x0F, Read(machine, 0x02));

        Write(machine, 0x04, 0x0E);          // memory mode
        Assert.Equal(0x0E, Read(machine, 0x04));
    }
}
