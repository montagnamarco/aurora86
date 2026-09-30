using System.Text;
using Aurora86.Core;
using Aurora86.Core.Devices.Storage;

namespace Aurora86.Tests;

/// <summary>
/// Booting from a CD, the way El Torito defined it in 1995.
///
/// Before the specification a PC could boot from a floppy or a hard disk and
/// nothing else, because the BIOS knew how to read nothing else. El Torito
/// works around that rather than fixing it: the BIOS copies a small floppy or
/// hard-disk image off the disc and answers INT 13h for it, so an operating
/// system written for a floppy boots from a CD without knowing it. That is
/// why the emulated drive here is an ordinary DiskImage -- everything above
/// it is meant to be unable to tell.
/// </summary>
public class ElToritoTests
{
    /// <summary>
    /// Builds an ISO 9660 image with a valid El Torito boot catalogue.
    ///
    /// The layout is the smallest one that is still legal: the volume
    /// descriptors at 16 (primary), 17 (boot record) and 18 (terminator), the
    /// catalogue at 19, and the boot image from 20 onwards.
    /// </summary>
    private static MemoryStream BuildBootableDisc(
        ElToritoMedia media = ElToritoMedia.Floppy1440Kb,
        byte bootIndicator = 0x88,
        bool validChecksum = true,
        bool withBootRecord = true,
        string specification = "EL TORITO SPECIFICATION")
    {
        const int catalogueSector = 19;
        const int imageSector = 20;
        const int imageSectors = 800;               // room for a 1.44 MB image

        int sectors = imageSector + imageSectors;
        var bytes = new byte[sectors * IsoImage.SectorSize];

        WriteDescriptor(bytes, 16, type: 1);
        WriteDescriptor(bytes, 18, type: 255);

        if (withBootRecord)
        {
            int bootRecord = 17 * IsoImage.SectorSize;
            WriteDescriptor(bytes, 17, type: 0);
            Encoding.ASCII.GetBytes(specification).CopyTo(bytes, bootRecord + 7);
            BitConverter.GetBytes(catalogueSector).CopyTo(bytes, bootRecord + 71);
        }

        // The validation entry: header, platform, the 0x55 0xAA key, and a
        // checksum chosen so the 16 words of the entry sum to zero.
        int catalogue = catalogueSector * IsoImage.SectorSize;
        bytes[catalogue] = 1;
        bytes[catalogue + 1] = 0;                   // 80x86
        Encoding.ASCII.GetBytes("AURORA86").CopyTo(bytes, catalogue + 4);
        bytes[catalogue + 30] = 0x55;
        bytes[catalogue + 31] = 0xAA;

        ushort sum = 0;
        for (int offset = 0; offset < 32; offset += 2)
            sum += BitConverter.ToUInt16(bytes, catalogue + offset);
        ushort correction = (ushort)(-sum & 0xFFFF);
        if (!validChecksum) correction ^= 0x1234;
        BitConverter.GetBytes(correction).CopyTo(bytes, catalogue + 28);

        // The initial/default entry.
        int entry = catalogue + 32;
        bytes[entry] = bootIndicator;
        bytes[entry + 1] = (byte)media;
        BitConverter.GetBytes((ushort)0).CopyTo(bytes, entry + 2);      // load segment
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, entry + 6);      // virtual sectors
        BitConverter.GetBytes(imageSector).CopyTo(bytes, entry + 8);

        // The boot image itself: a recognisable first sector with the
        // signature every boot sector ends with, and a marker in the second.
        int image = imageSector * IsoImage.SectorSize;
        Encoding.ASCII.GetBytes("AURORABOOT").CopyTo(bytes, image);
        bytes[image + 510] = 0x55;
        bytes[image + 511] = 0xAA;
        Encoding.ASCII.GetBytes("SECOND").CopyTo(bytes, image + 512);

        // A hard-disk image has no standard size, so it carries its geometry
        // in the partition table its own master boot record holds: 20
        // cylinders, 2 heads, 18 sectors, written as the last CHS address of
        // the partition in the packed form the interrupt uses.
        if (media == ElToritoMedia.HardDisk)
        {
            const int firstPartitionEntry = 446;
            bytes[image + firstPartitionEntry + 4] = 0x06;      // FAT16 partition
            bytes[image + firstPartitionEntry + 5] = 1;         // last head (2 heads)
            bytes[image + firstPartitionEntry + 6] = 18;        // last sector, cylinder bits clear
            bytes[image + firstPartitionEntry + 7] = 19;        // last cylinder (20 cylinders)
        }

        var stream = new MemoryStream(bytes);
        stream.Position = 0;
        return stream;
    }

    private static void WriteDescriptor(byte[] bytes, int sector, byte type)
    {
        int offset = sector * IsoImage.SectorSize;
        bytes[offset] = type;
        Encoding.ASCII.GetBytes("CD001").CopyTo(bytes, offset + 1);
        bytes[offset + 6] = 1;
        if (type == 1) Encoding.ASCII.GetBytes("BOOTTEST".PadRight(32)).CopyTo(bytes, offset + 40);
    }

    [Fact]
    public void ABootableDiscYieldsItsCatalogueEntry()
    {
        using var disc = new IsoImage(BuildBootableDisc());

        ElToritoBootEntry? entry = disc.ReadBootCatalogue();

        Assert.NotNull(entry);
        Assert.Equal(ElToritoMedia.Floppy1440Kb, entry.Media);
        Assert.Equal(20u, entry.ImageSector);
        Assert.Equal(1, entry.SectorCount);
        Assert.Equal(0x00, entry.EmulatedDriveNumber);      // a floppy image becomes A:
    }

    /// <summary>
    /// Most CDs are not bootable, including the Windows 95 OSR2 disc this was
    /// written against: those pressings carried SETUP.EXE and expected you to
    /// start from a floppy. A disc with no boot record is an ordinary disc,
    /// not a broken one, so it has to come back as null rather than throw.
    /// </summary>
    [Fact]
    public void ADiscWithNoBootRecordIsSimplyNotBootable()
    {
        using var disc = new IsoImage(BuildBootableDisc(withBootRecord: false));

        Assert.Null(disc.ReadBootCatalogue());
    }

    /// <summary>
    /// The checksum is what stops a BIOS running whatever it happens to land
    /// on: the catalogue is found by following a sector number out of another
    /// structure, and reading the Windows 95 disc's Joliet descriptor as a
    /// catalogue produced a plausible media type and a load segment of
    /// 0x4542 -- which are the letters of a volume name.
    /// </summary>
    [Fact]
    public void ACatalogueThatFailsItsChecksumIsRejected()
    {
        using var disc = new IsoImage(BuildBootableDisc(validChecksum: false));

        Assert.Null(disc.ReadBootCatalogue());
    }

    [Fact]
    public void ABootRecordNamingAnotherSpecificationIsRejected()
    {
        using var disc = new IsoImage(BuildBootableDisc(specification: "SOMETHING ELSE"));

        Assert.Null(disc.ReadBootCatalogue());
    }

    [Fact]
    public void AnEntryNotMarkedBootableIsRejected()
    {
        using var disc = new IsoImage(BuildBootableDisc(bootIndicator: 0x00));

        Assert.Null(disc.ReadBootCatalogue());
    }

    /// <summary>
    /// The emulated floppy is the full size of its media type, not the size
    /// of the image the entry names: a count of 1 means "load one sector and
    /// start it", not "the medium is one sector long". DOS reads the rest of
    /// itself off the same drive afterwards.
    /// </summary>
    [Fact]
    public void TheEmulatedFloppyIsTheFullSizeOfItsFormat()
    {
        using var disc = new IsoImage(BuildBootableDisc());

        using DiskImage? drive = ElTorito.CreateBootDrive(disc);

        Assert.NotNull(drive);
        Assert.Equal(0x00, drive.DriveNumber);
        Assert.Equal(80, drive.Cylinders);
        Assert.Equal(2, drive.Heads);
        Assert.Equal(18, drive.SectorsPerTrack);
        Assert.Equal(1440 * 1024 / DiskImage.SectorSize, drive.TotalSectors);
    }

    /// <summary>
    /// The unit trap in the specification: the catalogue counts 512-byte
    /// virtual sectors while the disc is read in 2048-byte blocks, so four
    /// virtual sectors come out of every block. Reading the second one back
    /// proves the extraction did not stop at the first.
    /// </summary>
    [Fact]
    public void TheImageIsExtractedInVirtualSectorsNotBlocks()
    {
        using var disc = new IsoImage(BuildBootableDisc());
        using DiskImage drive = ElTorito.CreateBootDrive(disc)!;

        var first = new byte[DiskImage.SectorSize];
        var second = new byte[DiskImage.SectorSize];
        Assert.True(drive.ReadLinear(0, 1, first));
        Assert.True(drive.ReadLinear(1, 1, second));

        Assert.Equal("AURORABOOT", Encoding.ASCII.GetString(first, 0, 10));
        Assert.Equal(0x55, first[510]);
        Assert.Equal(0xAA, first[511]);
        Assert.Equal("SECOND", Encoding.ASCII.GetString(second, 0, 6));
    }

    /// <summary>
    /// Writes during setup must not reach the file the user gave us: a disc
    /// is physically read-only, and an installer that writes to its boot
    /// floppy would otherwise modify the ISO.
    /// </summary>
    [Fact]
    public void WritingToTheEmulatedDriveDoesNotTouchTheDisc()
    {
        var backing = BuildBootableDisc();
        using var disc = new IsoImage(backing, ownsStream: false);
        using DiskImage drive = ElTorito.CreateBootDrive(disc)!;

        byte[] original = backing.ToArray();

        var written = new byte[DiskImage.SectorSize];
        Encoding.ASCII.GetBytes("OVERWRITTEN").CopyTo(written, 0);
        Assert.True(drive.WriteLinear(0, 1, written));

        Assert.Equal(original, backing.ToArray());

        // ...but the guest sees its own write.
        var read = new byte[DiskImage.SectorSize];
        drive.ReadLinear(0, 1, read);
        Assert.Equal("OVERWRITTEN", Encoding.ASCII.GetString(read, 0, 11));
    }

    /// <summary>
    /// The Windows 95 OSR2 disc this was written against, when it is present.
    ///
    /// It is deliberately asserted to be *not* bootable: those pressings
    /// carried SETUP.EXE and expected the machine to be started from a floppy,
    /// and the volume descriptors are primary, Joliet and terminator with no
    /// boot record among them. The test guards against the reader inventing a
    /// boot catalogue out of the Joliet descriptor, which is exactly what an
    /// earlier reading of it did.
    /// </summary>
    [Fact]
    public void TheWindows95DiscIsNotBootable()
    {
        // The image is not in the repository -- it is several hundred
        // megabytes -- so the test asserts nothing when it is absent rather
        // than failing on a machine that does not have it.
        string full = Path.Combine(RepositoryRoot(), "images/win95/Win95_OSR25.iso");
        if (!File.Exists(full)) return;

        using var disc = IsoImage.Open(full);

        Assert.Equal("WIN95_OSR25", disc.VolumeLabel);
        Assert.Null(disc.ReadBootCatalogue());
        Assert.Null(ElTorito.CreateBootDrive(disc));
    }

    /// <summary>Walks up from the test assembly to the repository root.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "images")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    /// <summary>
    /// A no-emulation disc is loaded and run directly, with no drive to
    /// present. Every Linux distribution of the later era boots this way; the
    /// guests this emulator targets do not, so there is nothing to build.
    /// </summary>
    [Fact]
    public void ANoEmulationDiscBuildsNoDrive()
    {
        using var disc = new IsoImage(BuildBootableDisc(ElToritoMedia.NoEmulation));

        Assert.NotNull(disc.ReadBootCatalogue());
        Assert.Null(ElTorito.CreateBootDrive(disc));
    }

    /// <summary>
    /// The machine boots from the disc when the order names it: the boot
    /// sector reaches 0000:7C00 and DL carries the drive it came from, which
    /// is how the sector knows where to read the rest of the system.
    /// </summary>
    [Fact]
    public void TheMachineBootsFromABootableDisc()
    {
        using var machine = new Machine();
        machine.AttachCompactDisc(new IsoImage(BuildBootableDisc()));
        machine.BootDrives = [Machine.CompactDiscBootDrive];

        machine.RunInstructions(20_000);

        Assert.True(machine.Booted);
        Assert.Equal("AURORABOOT",
            Encoding.ASCII.GetString(machine.Memory.GetRamSpan(0x7C00, 10).ToArray()));
    }

    /// <summary>
    /// A disc that is not bootable falls through to the next drive in the
    /// order rather than stopping the machine.
    /// </summary>
    [Fact]
    public void AnUnbootableDiscFallsThroughToTheDisk()
    {
        using var machine = new Machine();
        machine.AttachCompactDisc(new IsoImage(BuildBootableDisc(withBootRecord: false)));

        DiskImage disk = DiskImage.CreateInMemory(0x80, 20, 2, 18);
        var bootSector = new byte[DiskImage.SectorSize];
        Encoding.ASCII.GetBytes("FROMDISK").CopyTo(bootSector, 0);
        bootSector[510] = 0x55;
        bootSector[511] = 0xAA;
        disk.WriteLinear(0, 1, bootSector);
        machine.AttachDisk(disk);

        machine.BootDrives = [Machine.CompactDiscBootDrive, 0x80];
        machine.RunInstructions(20_000);

        Assert.True(machine.Booted);
        Assert.Equal("FROMDISK",
            Encoding.ASCII.GetString(machine.Memory.GetRamSpan(0x7C00, 8).ToArray()));
    }

    /// <summary>
    /// A hard-disk boot image takes drive 0x80, and the real first hard disk
    /// moves up to 0x81 -- which the specification requires, and which an
    /// installer depends on when it writes to the disk it is installing onto.
    /// </summary>
    [Fact]
    public void AHardDiskImageDisplacesTheRealFirstDisk()
    {
        using var machine = new Machine();
        machine.AttachCompactDisc(new IsoImage(BuildBootableDisc(ElToritoMedia.HardDisk)));

        DiskImage disk = DiskImage.CreateInMemory(0x80, 20, 2, 18);
        var marker = new byte[DiskImage.SectorSize];
        Encoding.ASCII.GetBytes("REALDISK").CopyTo(marker, 0);
        marker[510] = 0x55;
        marker[511] = 0xAA;
        disk.WriteLinear(0, 1, marker);
        machine.AttachDisk(disk);

        machine.BootDrives = [Machine.CompactDiscBootDrive];
        machine.RunInstructions(20_000);

        Assert.True(machine.Booted);

        // The CD's image answers as 0x80 now, and the real disk as 0x81.
        var fromBootDrive = new byte[DiskImage.SectorSize];
        machine.DiskServices.GetDrive(0x80)!.ReadLinear(0, 1, fromBootDrive);
        Assert.Equal("AURORABOOT", Encoding.ASCII.GetString(fromBootDrive, 0, 10));

        var fromRealDisk = new byte[DiskImage.SectorSize];
        machine.DiskServices.GetDrive(0x81)!.ReadLinear(0, 1, fromRealDisk);
        Assert.Equal("REALDISK", Encoding.ASCII.GetString(fromRealDisk, 0, 8));
    }
}
