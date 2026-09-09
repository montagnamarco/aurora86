namespace Aurora386.Core.Devices.Storage;

/// <summary>
/// Makes a blank hard disk image, with or without a filesystem on it.
///
/// A machine with only one disk is a machine that cannot copy a file from one
/// disk to another, install to a second volume, or be given a scratch drive
/// to experiment on. Attaching a second one needs an image to attach, and
/// making that image outside the emulator means FDISK and FORMAT inside a
/// guest that has to be booted first -- a chicken-and-egg problem for anybody
/// who just wants somewhere to put files.
///
/// Two kinds are worth offering. A blank image is what a real new disk is:
/// nothing on it, and the guest partitions and formats it as it would a disk
/// from a shop. A prepared image already has a partition table and an empty
/// FAT16 filesystem, so DOS gives it a drive letter the moment it boots.
/// </summary>
public static class DiskImageBuilder
{
    /// <summary>
    /// The geometry every BIOS of the period used for anything above about
    /// 500 MB, and the one this emulator's own disk services expect: 63
    /// sectors per track, 16 heads.
    ///
    /// These numbers stopped describing anything physical long before they
    /// stopped being used. A drive of the mid-nineties had zones with
    /// different sector counts and no fixed head-per-cylinder relationship at
    /// all; it reported whatever geometry made the arithmetic work, and the
    /// BIOS believed it. What matters is only that everyone agrees.
    /// </summary>
    public const int SectorsPerTrack = 63;
    public const int Heads = 16;
    public const int SectorSize = 512;

    /// <summary>
    /// Writes a blank image of the given size, with nothing on it at all.
    ///
    /// The size is rounded down to a whole number of cylinders, because a
    /// partial cylinder is a part of the disk no CHS address can reach: the
    /// sectors are there and nothing can ask for them.
    /// </summary>
    public static void CreateBlank(string path, long bytes)
    {
        long cylinders = bytes / (SectorsPerTrack * Heads * SectorSize);
        if (cylinders < 1)
            throw new ArgumentException(
                "A disk needs at least one cylinder, which is about half a megabyte.",
                nameof(bytes));

        long total = cylinders * SectorsPerTrack * Heads * SectorSize;

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        file.SetLength(total);
    }

    /// <summary>
    /// Writes an image with one partition filling the disk, formatted FAT16
    /// and empty.
    /// </summary>
    /// <param name="label">
    /// The volume label, up to eleven characters. DOS shows it at the top of
    /// every directory listing, which makes it the quickest way to tell two
    /// otherwise identical scratch disks apart.
    /// </param>
    public static void CreateFat16(string path, long bytes, string label = "AURORA")
    {
        long cylinders = bytes / (SectorsPerTrack * Heads * SectorSize);
        if (cylinders < 2)
            throw new ArgumentException(
                "A FAT16 volume needs at least two cylinders.", nameof(bytes));

        long totalSectors = cylinders * SectorsPerTrack * Heads;
        var image = new byte[totalSectors * SectorSize];

        // The partition starts one track in. That is not an alignment
        // nicety: the master boot record lives in the first sector, and DOS
        // has always begun the first partition at the start of the second
        // track rather than immediately after it.
        const uint firstSector = SectorsPerTrack;
        uint partitionSectors = (uint)(totalSectors - firstSector);

        WriteMasterBootRecord(image, cylinders, firstSector, partitionSectors);
        WriteFat16Volume(image, firstSector * SectorSize, partitionSectors, label);

        File.WriteAllBytes(path, image);
    }

    private static void WriteMasterBootRecord(byte[] image, long cylinders,
                                              uint firstSector, uint partitionSectors)
    {
        const int entry = 0x1BE;

        image[entry] = 0x80;                    // bootable
        image[entry + 1] = 1;                   // starting head
        image[entry + 2] = 1;                   // starting sector, bits 0-5
        image[entry + 3] = 0;                   // starting cylinder
        image[entry + 4] = 0x06;                // FAT16 above 32 MB

        // The end of the partition in the old cylinder/head/sector form,
        // whose sector field carries the top two bits of the cylinder. It is
        // an encoding that ran out of room in 1994 and was kept anyway: DOS
        // reads the 32-bit sector counts below, and the CHS fields are what
        // it checks them against when they disagree.
        long lastCylinder = cylinders - 1;
        image[entry + 5] = Heads - 1;
        image[entry + 6] = (byte)(SectorsPerTrack | ((lastCylinder >> 2) & 0xC0));
        image[entry + 7] = (byte)(lastCylinder & 0xFF);

        WriteDword(image, entry + 8, firstSector);
        WriteDword(image, entry + 12, partitionSectors);

        image[0x1FE] = 0x55;
        image[0x1FF] = 0xAA;
    }

    private static void WriteFat16Volume(byte[] image, long at, uint sectors, string label)
    {
        // Cluster size is chosen so that the table stays within 16-bit
        // entries: a volume with more than 65524 clusters is not FAT16 at
        // all, whatever the boot sector claims, and DOS reads it as FAT32 or
        // refuses it. Doubling the cluster until the count fits is what
        // FORMAT does.
        byte sectorsPerCluster = 4;
        while (sectors / sectorsPerCluster > 65524 && sectorsPerCluster < 64)
            sectorsPerCluster *= 2;

        const ushort reserved = 1;
        const byte fats = 2;
        const ushort rootEntries = 512;

        // Each table entry is two bytes, and the table has to cover every
        // cluster plus the two reserved entries at its start.
        uint clusters = sectors / sectorsPerCluster;
        var sectorsPerFat = (ushort)(((clusters + 2) * 2 + SectorSize - 1) / SectorSize);

        image[at] = 0xEB; image[at + 1] = 0x3C; image[at + 2] = 0x90;   // jump over the BPB
        WriteAscii(image, at + 3, "AURORA  ", 8);

        WriteWord(image, at + 11, SectorSize);
        image[at + 13] = sectorsPerCluster;
        WriteWord(image, at + 14, reserved);
        image[at + 16] = fats;
        WriteWord(image, at + 17, rootEntries);
        WriteWord(image, at + 19, 0);                   // the 16-bit count, unused above 65535
        image[at + 21] = 0xF8;                          // fixed disk
        WriteWord(image, at + 22, sectorsPerFat);
        WriteWord(image, at + 24, SectorsPerTrack);
        WriteWord(image, at + 26, Heads);
        WriteDword(image, at + 28, SectorsPerTrack);    // sectors before the partition
        WriteDword(image, at + 32, sectors);

        image[at + 36] = 0x80;                          // BIOS drive number
        image[at + 38] = 0x29;                          // an extended boot record follows
        WriteDword(image, at + 39, 0x41555230);         // volume serial number
        WriteAscii(image, at + 43, label.ToUpperInvariant(), 11);
        WriteAscii(image, at + 54, "FAT16   ", 8);

        image[at + 510] = 0x55;
        image[at + 511] = 0xAA;

        // Both copies of the table, each beginning with the two reserved
        // entries: the media descriptor and an end-of-chain mark. DOS checks
        // that the first byte matches the descriptor in the boot sector, and
        // reports a volume whose copies disagree as damaged.
        for (int copy = 0; copy < fats; copy++)
        {
            long table = at + (reserved + copy * sectorsPerFat) * (long)SectorSize;
            image[table] = 0xF8;
            image[table + 1] = 0xFF;
            image[table + 2] = 0xFF;
            image[table + 3] = 0xFF;
        }

        // The label appears twice: once in the boot sector, where a program
        // reading the disk directly finds it, and once as a directory entry
        // with the volume-label attribute, which is where DOS looks. Writing
        // only one of them gives a disk whose name depends on who is asking.
        long root = at + (reserved + fats * sectorsPerFat) * (long)SectorSize;
        WriteAscii(image, root, label.ToUpperInvariant(), 11);
        image[root + 11] = 0x08;                        // volume label
    }

    private static void WriteAscii(byte[] image, long at, string text, int length)
    {
        for (int i = 0; i < length; i++)
            image[at + i] = (byte)(i < text.Length ? text[i] : ' ');
    }

    private static void WriteWord(byte[] image, long at, int value)
    {
        image[at] = (byte)value;
        image[at + 1] = (byte)(value >> 8);
    }

    private static void WriteDword(byte[] image, long at, uint value)
    {
        image[at] = (byte)value;
        image[at + 1] = (byte)(value >> 8);
        image[at + 2] = (byte)(value >> 16);
        image[at + 3] = (byte)(value >> 24);
    }
}
