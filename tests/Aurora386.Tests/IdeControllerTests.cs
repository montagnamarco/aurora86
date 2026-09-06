using System.Text;
using Aurora386.Core;
using Aurora386.Core.Bios;

namespace Aurora386.Tests;

/// <summary>
/// The IDE controller, exercised the way a driver exercises it.
///
/// Every test here corresponds to a fault that stopped a real CD-ROM driver
/// dead, each with the same symptom: MSCDEX reporting "CDR101: not ready
/// reading drive D" over a drive that was working. Nothing about that message
/// says which of half a dozen protocol details was wrong, which is why they
/// are all pinned down here.
/// </summary>
public class IdeControllerTests
{
    /// <summary>
    /// A machine with a disc in the drive. The image is built rather than
    /// loaded: a test that needs a file on disk is a test that stops working
    /// when somebody moves it.
    /// </summary>
    private static Machine WithDisc(int sectors = 100)
    {
        var machine = new Machine();
        machine.RunInstructions(200);

        var bytes = new byte[sectors * IsoImage.SectorSize];

        int pvd = IsoImage.PrimaryVolumeDescriptorSector * IsoImage.SectorSize;
        bytes[pvd] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(bytes, pvd + 1);
        Encoding.ASCII.GetBytes("TEST".PadRight(32)).CopyTo(bytes, pvd + 40);

        // Stamp each sector with its own number, so a read from the wrong
        // place is obvious rather than merely wrong.
        for (int sector = 0; sector < sectors; sector++)
            bytes[sector * IsoImage.SectorSize] = (byte)sector;

        machine.AttachCompactDisc(new IsoImage(new MemoryStream(bytes)));

        // A freshly inserted disc answers its first command with unit
        // attention, as a real drive does; a driver clears that with a TEST
        // UNIT READY before it does anything else, and so do these tests.
        SendPacket(machine, 0x00);
        return machine;
    }

    private static byte[] RequestSense(Machine machine) => SendPacket(machine, 0x03, 0, 0, 0, 18);

    /// <summary>Selects the CD-ROM, which sits as the slave on the cable.</summary>
    private static void SelectDrive(Machine machine) => machine.Io.WriteByte(0x1F6, 0x10);

    /// <summary>
    /// Sends a twelve-byte packet and collects whatever comes back.
    ///
    /// This is the whole conversation a driver has with an ATAPI drive:
    /// PACKET, twelve bytes of SCSI, then read the data register until the
    /// drive stops asking.
    /// </summary>
    private static byte[] SendPacket(Machine machine, params byte[] command)
    {
        SelectDrive(machine);
        machine.Io.WriteByte(0x1F7, 0xA0);

        var packet = new byte[12];
        command.CopyTo(packet, 0);

        foreach (byte b in packet) machine.Io.WriteByte(0x1F0, b);

        var received = new List<byte>();
        while ((machine.Io.ReadByte(0x1F7) & 0x08) != 0)
            received.Add(machine.Io.ReadByte(0x1F0));

        return [.. received];
    }

    [Fact]
    public void AnEmptyCableReadsBackAsNothingRatherThanAsBusy()
    {
        // The master position has no drive on it. A cable with nothing on it
        // must not look like a drive that is merely busy, or a driver waits
        // for it to become ready and never stops waiting.
        using Machine machine = WithDisc();

        machine.Io.WriteByte(0x1F6, 0x00);   // select master

        byte status = machine.Io.ReadByte(0x1F7);
        Assert.Equal(0, status & 0x80);      // not busy
    }

    [Fact]
    public void TheDriveAnnouncesItselfAsAtapiAfterAReset()
    {
        // A driver resets the cable and reads the address registers to find
        // out what answered. 0xEB14 in the two middle bytes is the signature
        // every ATAPI device leaves, and it is how a CD-ROM is told from a
        // hard disk before either has been asked anything.
        using Machine machine = WithDisc();

        SelectDrive(machine);
        machine.Io.WriteByte(0x3F6, 0x04);   // software reset

        Assert.Equal(0x14, machine.Io.ReadByte(0x1F4));
        Assert.Equal(0xEB, machine.Io.ReadByte(0x1F5));
    }

    [Fact]
    public void IdentifyDeviceIsRefusedBecauseThisIsNotADisk()
    {
        // A driver written before ATAPI existed asks this. The refusal is
        // what tells it to stop treating the drive as a disk -- answering
        // would have it read a partition table off a CD.
        using Machine machine = WithDisc();

        SelectDrive(machine);
        machine.Io.WriteByte(0x1F7, 0xEC);

        Assert.Equal(0x01, machine.Io.ReadByte(0x1F7) & 0x01);   // error
    }

    [Fact]
    public void TheIdentifyBlockNamesTheDriveWithItsBytesSwapped()
    {
        // The text fields of an identify block are words on a big-endian bus,
        // so every pair of characters arrives the other way round and every
        // driver un-swaps them. A drive that does not swap them shows up in
        // the driver's banner with its name scrambled.
        using Machine machine = WithDisc();

        SelectDrive(machine);
        machine.Io.WriteByte(0x1F7, 0xA1);   // identify packet device

        var block = new List<byte>();
        while ((machine.Io.ReadByte(0x1F7) & 0x08) != 0)
            block.Add(machine.Io.ReadByte(0x1F0));

        Assert.Equal(512, block.Count);

        // The model name lives at word 27. Un-swapping it must give back
        // something readable.
        var name = new StringBuilder();
        for (int word = 27; word < 47; word++)
        {
            name.Append((char)block[word * 2 + 1]);
            name.Append((char)block[word * 2]);
        }

        Assert.Contains("Aurora", name.ToString());
    }

    [Fact]
    public void ReadTocAnswersTheFormatTheDriverAsksForInEitherPlace()
    {
        // The format field moved between revisions of the standard: it began
        // in the top bits of byte 9 and was later given a home in byte 2.
        // Drivers written before the change still use byte 9, and the one of
        // the period does exactly that -- reading byte 2 alone answers the
        // wrong question and MSCDEX reports the drive as not ready.
        using Machine machine = WithDisc();

        // Format 1 requested the old way: byte 9 set, byte 2 zero.
        byte[] old = SendPacket(machine,
            0x43, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x40);

        // And the new way: byte 2 set.
        byte[] modern = SendPacket(machine,
            0x43, 0x02, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x00);

        Assert.Equal(modern, old);
    }

    [Fact]
    public void SessionInformationPointsAtWhereTheTrackBeginsNotWhereTheDiscEnds()
    {
        // Format 1 names the first track of the last session, and the address
        // is where that track starts. Format 0 finishes with a lead-out
        // descriptor and this one looks just like it, which makes the mistake
        // easy: put the lead-out here and the driver hunts for the file
        // system past the end of the disc.
        using Machine machine = WithDisc(sectors: 100);

        byte[] toc = SendPacket(machine,
            0x43, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x40);

        Assert.Equal(12, toc.Length);

        // Minutes, seconds and frames of the start address. Block zero is at
        // two seconds exactly, because the first two seconds of a CD are the
        // lead-in.
        Assert.Equal(0, toc[9]);    // minutes
        Assert.Equal(2, toc[10]);   // seconds
        Assert.Equal(0, toc[11]);   // frames
    }

    [Fact]
    public void AddressesComeBackAsMinutesAndSecondsWhenAskedFor()
    {
        // A CD counts in frames, 75 to the second. Answering in block numbers
        // when minutes and seconds were asked for is wrong by a factor of
        // about a hundred, and the driver concludes the disc is unreadable.
        using Machine machine = WithDisc(sectors: 7650);   // 100 seconds' worth

        byte[] inBlocks = SendPacket(machine,
            0x43, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x14, 0x00);
        byte[] inMinutes = SendPacket(machine,
            0x43, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x14, 0x00);

        // The lead-out address, which is the one that differs visibly.
        Assert.NotEqual(inBlocks[16..20], inMinutes[16..20]);

        // 7650 blocks plus the 150-frame lead-in is 104 seconds: 01:44:00.
        Assert.Equal(1, inMinutes[17]);
        Assert.Equal(44, inMinutes[18]);
    }

    [Fact]
    public void ReadingSectorsReturnsWhatIsOnTheDisc()
    {
        using Machine machine = WithDisc();

        // READ(10) for one block at sector 5.
        byte[] data = SendPacket(machine,
            0x28, 0x00, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x01);

        Assert.Equal(IsoImage.SectorSize, data.Length);
        Assert.Equal(5, data[0]);   // each sector is stamped with its number
    }

    [Fact]
    public void TheCommandInterruptsAgainWhenTheLastByteHasGone()
    {
        // An ATAPI drive interrupts twice: once when data is ready to collect,
        // and again when the transfer is finished. The second is what tells
        // the driver it may use what it has -- without it the driver holds the
        // bytes, waits to be told, and eventually gives up. That surfaces as
        // CDR101, which says nothing about interrupts at all.
        //
        // The reason register says which of the two it is. Bit 1 means the
        // data is travelling from the drive to the host, and stays set
        // throughout; bit 0 goes from clear to set as the transfer turns from
        // data into a completion.
        using Machine machine = WithDisc();

        SelectDrive(machine);
        machine.Io.WriteByte(0x1F7, 0xA0);

        byte[] packet = [0x28, 0x00, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x01, 0, 0, 0];
        foreach (byte b in packet) machine.Io.WriteByte(0x1F0, b);

        // Data on offer: sending to the host, and it is data rather than a
        // completion.
        Assert.Equal(0x02, machine.Io.ReadByte(0x1F2) & 0x03);

        while ((machine.Io.ReadByte(0x1F7) & 0x08) != 0) machine.Io.ReadByte(0x1F0);

        // Finished: still to the host, and now a completion.
        Assert.Equal(0x03, machine.Io.ReadByte(0x1F2) & 0x03);
    }

    [Fact]
    public void ACommandThatReturnsNoDataStillSaysItIsFinished()
    {
        // The interrupt reason register tells the driver where in the
        // conversation it is, and a command that returns nothing has to say
        // it is over -- not that it is still waiting to be given a command,
        // which is what the register holds from the moment the drive asks for
        // a packet.
        //
        // Traced against the real MSCDEX: for a read that returns data it
        // reads the byte counts and collects the bytes; for one that returns
        // none it reads this register three times, sees "send me a command"
        // each time, and asks nothing further. That is why a directory on the
        // disc could be requested and then never read, and why "cd" into one
        // put the user straight back where they started.
        using Machine machine = WithDisc();

        // A read of zero blocks: a legal request that transfers nothing.
        SendPacket(machine, 0x28, 0x00, 0x00, 0x00, 0x00, 0x17, 0x00, 0x00, 0x00);

        byte reason = machine.Io.ReadByte(0x1F2);

        // Bit 0: this is a command phase rather than data. Bit 1: from the
        // drive to the host. Together they mean the command has completed.
        Assert.Equal(0x03, reason & 0x03);
    }

    [Fact]
    public void ReadSubChannelIsAnsweredEvenOnADataDisc()
    {
        // This is a command about audio playback, and MSCDEX asks it about a
        // data disc anyway -- it wants to know the drive is not playing music
        // before it treats the disc as a file system. Refusing it makes
        // MSCDEX decide the drive cannot be talked to.
        using Machine machine = WithDisc();

        byte[] answer = SendPacket(machine,
            0x42, 0x02, 0x40, 0x01, 0x00, 0x00, 0x00, 0x00, 0x10);

        Assert.NotEmpty(answer);
        Assert.Equal(0x15, answer[1]);   // audio status: stopped
    }

    [Fact]
    public void AnEmptyDriveSaysTheTrayIsEmptyRatherThanThatItIsBroken()
    {
        // The two are different answers and a driver acts on them
        // differently: no medium means ask again later, a broken drive means
        // give up.
        using var machine = new Machine();
        machine.RunInstructions(200);

        SendPacket(machine, 0x00);   // test unit ready

        Assert.Equal(0x01, machine.Io.ReadByte(0x1F7) & 0x01);   // error

        byte[] sense = SendPacket(machine, 0x03, 0x00, 0x00, 0x00, 0x12);

        Assert.Equal(0x02, sense[2] & 0x0F);   // not ready
        Assert.Equal(0x3A, sense[12]);         // medium not present
    }

    [Fact]
    public void ReadingPastTheEndOfTheDiscIsRefused()
    {
        using Machine machine = WithDisc(sectors: 100);

        SendPacket(machine, 0x28, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x01);

        Assert.Equal(0x01, machine.Io.ReadByte(0x1F7) & 0x01);
    }

    [Fact]
    public void TheDiscsSizeIsReportedAsTheLastBlockNotTheCount()
    {
        // READ CAPACITY answers with the number of the last valid block,
        // which is one less than how many there are. It is an off-by-one that
        // has confused implementers since 1986, and a driver relies on it to
        // work out where the disc ends.
        using Machine machine = WithDisc(sectors: 100);

        byte[] capacity = SendPacket(machine, 0x25);

        uint lastBlock = (uint)((capacity[0] << 24) | (capacity[1] << 16)
                             | (capacity[2] << 8) | capacity[3]);

        Assert.Equal(99u, lastBlock);

        uint blockSize = (uint)((capacity[4] << 24) | (capacity[5] << 16)
                             | (capacity[6] << 8) | capacity[7]);

        Assert.Equal((uint)IsoImage.SectorSize, blockSize);
    }

    [Fact]
    public void TheFirstPacketFromTheGuestTakesTheDiscOffTheRedirector()
    {
        // Until a driver speaks ATAPI the disc is reachable through the
        // redirector's letter. The moment one does, the redirector lets go:
        // the driver and MSCDEX own the disc now, and a second letter for the
        // same disc is what left D: empty next to a working E:.
        using var machine = new Machine();
        machine.RunInstructions(200);
        machine.AttachCompactDisc(new IsoImage(new MemoryStream(new byte[20 * IsoImage.SectorSize])));

        Assert.Contains(machine.CompactDiscServices.DriveLetter, machine.Redirector.MountedDrives);
        Assert.False(machine.GuestHasItsOwnCompactDiscExtensions);

        SendPacket(machine, 0x00);

        Assert.True(machine.GuestHasItsOwnCompactDiscExtensions);
        Assert.DoesNotContain(machine.CompactDiscServices.DriveLetter, machine.Redirector.MountedDrives);
    }

    [Fact]
    public void ADiscInsertedAfterTheDriverLoadedIsAnnouncedOnceAsUnitAttention()
    {
        using Machine machine = WithDisc();
        SelectDrive(machine);

        machine.AttachCompactDisc(new IsoImage(new MemoryStream(new byte[20 * IsoImage.SectorSize])));

        // Not mounted on the redirector: the guest's own driver has the disc.
        Assert.DoesNotContain(machine.CompactDiscServices.DriveLetter, machine.Redirector.MountedDrives);

        SendPacket(machine, 0x00);                                  // TEST UNIT READY
        Assert.Equal(0x01, machine.Io.ReadByte(0x1F7) & 0x01);      // error
        byte[] sense = RequestSense(machine);
        Assert.Equal(0x06, sense[2] & 0x0F);                        // unit attention
        Assert.Equal(0x28, sense[12]);                              // medium may have changed

        SendPacket(machine, 0x00);
        Assert.Equal(0x00, machine.Io.ReadByte(0x1F7) & 0x01);      // and then all is well
    }
}
