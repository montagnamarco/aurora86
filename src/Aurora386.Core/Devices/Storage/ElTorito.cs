namespace Aurora386.Core.Devices.Storage;

/// <summary>
/// Turns an El Torito boot image on a CD into an ordinary <see cref="DiskImage"/>.
///
/// This is the whole trick of booting from a CD, and it is a trick rather
/// than a new capability: the BIOS copies a floppy or hard-disk image off the
/// disc into memory and then answers INT 13h for that drive out of the copy.
/// The operating system on the disc boots exactly as it would from the real
/// medium, unaware that the sectors are coming from a CD, and by the time it
/// needs the rest of the disc it has loaded a CD driver of its own.
///
/// Two details of the specification matter here and are easy to get wrong.
/// The catalogue counts the image in 512-byte virtual sectors while the disc
/// is read in 2048-byte blocks, so four virtual sectors come out of every
/// block. And a sector count of 1 does not mean a one-sector medium: it means
/// "load one sector and start it", which is what a floppy image says, so the
/// emulated medium is the full size its media type implies and the rest is
/// read from the disc behind it.
/// </summary>
public static class ElTorito
{
    /// <summary>
    /// Builds the drive a bootable disc asks to be presented as, or returns
    /// null when the disc is not bootable or asks for no emulation.
    ///
    /// The image is read into memory rather than streamed: a floppy image is
    /// at most 2.88 MB, it is written to during setup by nearly every
    /// operating system of the period, and those writes must not reach the
    /// file the user gave us.
    /// </summary>
    public static DiskImage? CreateBootDrive(IsoImage disc)
    {
        ElToritoBootEntry? entry = disc.ReadBootCatalogue();
        if (entry is null)
        {
            return null;
        }

        // No-emulation discs are loaded and run directly; there is no drive to
        // build, and the caller handles them separately.
        if (entry.Media == ElToritoMedia.NoEmulation)
        {
            return null;
        }

        (int cylinders, int heads, int sectorsPerTrack) = entry.Media == ElToritoMedia.HardDisk
            ? GeometryFromPartitionTable(disc, entry.ImageSector)
            : GeometryForFloppy(entry.Media);

        if (cylinders == 0)
        {
            return null;
        }

        long sizeInBytes = (long)cylinders * heads * sectorsPerTrack * DiskImage.SectorSize;
        var contents = new byte[sizeInBytes];

        // The image begins at the block the entry names and runs to the end of
        // the emulated medium. Reading past the end of the disc yields zeroes,
        // which is the right answer for a medium larger than its image.
        int blocksToRead = (int)((sizeInBytes + IsoImage.SectorSize - 1) / IsoImage.SectorSize);
        disc.ReadSectors(entry.ImageSector, blocksToRead, contents);

        var stream = new MemoryStream(contents, writable: true);
        return new DiskImage(stream, entry.EmulatedDriveNumber,
                             cylinders, heads, sectorsPerTrack);
    }

    /// <summary>
    /// The geometry of a hard-disk boot image, taken from the first entry of
    /// the partition table in its own master boot record.
    ///
    /// A hard disk has no standard size to fall back on, so the specification
    /// says the image carries a partition table and the BIOS believes it. The
    /// entry stores the last cylinder, head and sector of the partition in
    /// the packed CHS form the interrupt uses -- six bits of sector and two
    /// spare bits of cylinder in the same byte -- and the geometry is those
    /// numbers plus one, because CHS counts heads and cylinders from zero and
    /// sectors from one.
    /// </summary>
    private static (int Cylinders, int Heads, int SectorsPerTrack) GeometryFromPartitionTable(
        IsoImage disc, uint imageSector)
    {
        var masterBootRecord = new byte[IsoImage.SectorSize];
        disc.ReadSector(imageSector, masterBootRecord);

        if (masterBootRecord[510] != 0x55 || masterBootRecord[511] != 0xAA)
        {
            return (0, 0, 0);
        }

        const int firstPartitionEntry = 446;
        byte lastHead = masterBootRecord[firstPartitionEntry + 5];
        byte packedSector = masterBootRecord[firstPartitionEntry + 6];
        byte lastCylinderLow = masterBootRecord[firstPartitionEntry + 7];

        int sectorsPerTrack = packedSector & 0x3F;
        int lastCylinder = lastCylinderLow | ((packedSector & 0xC0) << 2);

        if (sectorsPerTrack == 0)
        {
            return (0, 0, 0);
        }

        return (lastCylinder + 1, lastHead + 1, sectorsPerTrack);
    }

    /// <summary>
    /// The geometry each floppy format is presented with. These are the real
    /// formats' own numbers, and they matter because DOS reads them back out
    /// of the BIOS parameter block and believes them.
    /// </summary>
    private static (int Cylinders, int Heads, int SectorsPerTrack) GeometryForFloppy(ElToritoMedia media)
        => media switch
        {
            ElToritoMedia.Floppy1200Kb => (80, 2, 15),
            ElToritoMedia.Floppy1440Kb => (80, 2, 18),
            ElToritoMedia.Floppy2880Kb => (80, 2, 36),
            _ => (0, 0, 0),
        };
}
