namespace Aurora86.Core.Devices.Storage;

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

        // What the BIOS will present, which past 1024 cylinders is fewer of
        // them and more heads; the partition table and the BPB describe
        // that disk, because that is the one DOS addresses.
        HardDiskImage.Geometry presented =
            new HardDiskImage.Geometry((int)cylinders, Heads, SectorsPerTrack).Presented();

        // The partition starts one track in. That is not an alignment
        // nicety: the master boot record lives in the first sector, and DOS
        // has always begun the first partition at the start of the second
        // track rather than immediately after it.
        const uint firstSector = SectorsPerTrack;
        uint partitionSectors = (uint)(presented.TotalSectors - firstSector);

        WriteMasterBootRecord(image, presented, firstSector, partitionSectors);
        WriteFat16Volume(image, firstSector * SectorSize, partitionSectors, label, presented.Heads);

        File.WriteAllBytes(path, image);
    }

    /// <summary>
    /// Appends the footer that turns a raw image into a fixed-size VHD.
    ///
    /// The format is the raw sectors followed by one 512-byte descriptor, and
    /// that ordering is the whole trick: everything before the footer is
    /// exactly what a raw image would be, so the same file can be handed to
    /// something that knows the format and to something that does not.
    ///
    /// Worth having because both macOS and Windows mount a VHD by
    /// double-clicking it. A disk the guest wrote can then be opened on the
    /// host without the emulator running, which is the difference between an
    /// image and a file somebody can actually use.
    ///
    /// Every field is big-endian, which no PC format is: the format came from
    /// Connectix, whose product ran on the Macintosh.
    /// </summary>
    public static void AppendVhdFooter(string path)
    {
        long size = new FileInfo(path).Length;
        long cylinders = size / (SectorsPerTrack * Heads * SectorSize);

        var footer = new byte[512];

        WriteAscii(footer, 0, "conectix", 8);           // the spelling is the format's own
        WriteBigEndian32(footer, 8, 2);                 // features: reserved bit only
        WriteBigEndian32(footer, 12, 0x0001_0000);      // format version 1.0

        // Where the data begins, which for a fixed disk is nowhere: the
        // footer describes the whole file, so this is all ones.
        for (int i = 16; i < 24; i++) footer[i] = 0xFF;

        WriteBigEndian32(footer, 24, 0);                // timestamp: seconds since 2000
        WriteAscii(footer, 28, "aur3", 4);              // creating application
        WriteBigEndian32(footer, 32, 0x0001_0000);      // its version
        WriteAscii(footer, 36, "Wi2k", 4);              // creating host, from the format's list

        WriteBigEndian64(footer, 40, size);             // original size
        WriteBigEndian64(footer, 48, size);             // current size

        WriteBigEndian16(footer, 56, (int)cylinders);
        footer[58] = Heads;
        footer[59] = SectorsPerTrack;

        WriteBigEndian32(footer, 60, 2);                // disk type: fixed

        // A unique identifier. Two disks sharing one confuse anything that
        // tracks them by identity rather than by path, so it is generated
        // rather than fixed.
        Guid.NewGuid().ToByteArray().CopyTo(footer, 68);

        // The checksum is over the whole footer with the checksum field
        // zeroed, which is why it is computed last.
        uint sum = 0;
        foreach (byte b in footer) sum += b;
        WriteBigEndian32(footer, 64, ~sum);

        using var file = new FileStream(path, FileMode.Append, FileAccess.Write);
        file.Write(footer);
    }

    /// <summary>
    /// Writes an image with one partition filling the disk, formatted FAT32
    /// and empty.
    ///
    /// FAT32 rather than FAT16 because it is what a modern guest expects: it
    /// is the file system of the UEFI system partition, the one every machine
    /// made since 1996 reads, and the one Aurora OS mounts. A test that
    /// formatted FAT16 would exercise a driver nobody has.
    ///
    /// MIND that FAT32 is not FAT16 with wider entries. Three things move:
    /// the root directory becomes an ordinary cluster chain rather than a
    /// fixed region, the table size moves to a 32-bit field because the
    /// 16-bit one cannot express it, and two zeroes in the old fields are
    /// what identifies the format. Getting the last of those wrong produces a
    /// volume every reader takes for FAT16.
    /// </summary>
    public static void CreateFat32(string path, long bytes, string label = "AURORA")
    {
        long cylinders = bytes / (SectorsPerTrack * Heads * SectorSize);

        // FAT32 needs at least 65525 clusters by definition -- fewer makes it
        // FAT16 whatever the fields say, because that is how every reader
        // decides. With one sector per cluster that is about 32 MB.
        if (cylinders < 70)
            throw new ArgumentException(
                "A FAT32 volume needs about 35 MB to hold enough clusters.",
                nameof(bytes));

        long totalSectors = cylinders * SectorsPerTrack * Heads;
        var image = new byte[totalSectors * SectorSize];

        var geometry = HardDiskImage.Geometry.ForSize(totalSectors * SectorSize);

        const uint firstSector = 63;
        uint partitionSectors = (uint)(totalSectors - firstSector);

        WriteMasterBootRecord(image, geometry, firstSector, partitionSectors);

        // 0x0C is FAT32 with LBA addressing, which is what anything written
        // since about 1998 uses. 0x0B is the same file system addressed
        // through cylinder, head and sector, and choosing it would ask every
        // reader to go through a geometry that stopped meaning anything.
        image[0x1BE + 4] = 0x0C;

        WriteFat32Volume(image, firstSector * SectorSize, partitionSectors, label);

        File.WriteAllBytes(path, image);
    }

    private static void WriteFat32Volume(byte[] image, long at, uint sectors, string label)
    {
        const uint reserved = 32;          // the conventional FAT32 figure
        const uint tables = 2;
        const uint sectorsPerCluster = 1;

        // How big the table must be to describe the clusters, which depends
        // on how many clusters there are, which depends on how big the table
        // is. The circularity is resolved by solving it rather than by
        // iterating: each cluster needs four bytes of table, so
        //
        //     clusters = (sectors - reserved - tables * tableSectors) / spc
        //     tableSectors = ceil(clusters * 4 / sectorSize)
        //
        // which rearranges into the expression below.
        uint dataSectors = sectors - reserved;
        uint tableSectors = (dataSectors / sectorsPerCluster * 4 + SectorSize - 1)
                            / (SectorSize + tables * 4 / sectorsPerCluster);
        tableSectors += 1;                  // round up rather than short

        uint firstDataSector = reserved + tables * tableSectors;
        uint clusters = (sectors - firstDataSector) / sectorsPerCluster;

        const uint rootCluster = 2;

        // The boot sector. It begins with a jump because the BIOS loads and
        // executes it, so the fields start at offset 11.
        image[at + 0] = 0xEB;
        image[at + 1] = 0x58;
        image[at + 2] = 0x90;
        WriteAscii(image, at + 3, "MSWIN4.1", 8);

        WriteWord(image, at + 11, SectorSize);
        image[at + 13] = (byte)sectorsPerCluster;
        WriteWord(image, at + 14, (int)reserved);
        image[at + 16] = (byte)tables;

        // THE TWO ZEROES THAT IDENTIFY FAT32. The root entry count and the
        // 16-bit table size are zero only on FAT32, and the specification
        // says explicitly that this -- not the "FAT32" string below -- is how
        // a reader decides.
        WriteWord(image, at + 17, 0);
        WriteWord(image, at + 22, 0);

        WriteWord(image, at + 19, 0);       // the 16-bit total, unused here
        image[at + 21] = 0xF8;              // fixed disk
        WriteWord(image, at + 24, SectorsPerTrack);
        WriteWord(image, at + 26, Heads);
        WriteDword(image, at + 28, 63);     // sectors before this partition
        WriteDword(image, at + 32, sectors);

        WriteDword(image, at + 36, tableSectors);
        WriteWord(image, at + 40, 0);       // flags: both tables live
        WriteWord(image, at + 42, 0);       // version
        WriteDword(image, at + 44, rootCluster);
        WriteWord(image, at + 48, 1);       // the information sector
        WriteWord(image, at + 50, 6);       // where the backup boot sector is

        image[at + 64] = 0x80;              // drive number
        image[at + 66] = 0x29;              // an extended signature follows
        WriteDword(image, at + 67, 0x41555241);
        WriteAscii(image, at + 71, label, 11);
        WriteAscii(image, at + 82, "FAT32   ", 8);

        image[at + 510] = 0x55;
        image[at + 511] = 0xAA;

        // The backup boot sector, which FAT32 keeps because losing the boot
        // sector loses the volume. Every reader that repairs a disk looks
        // here first.
        for (int i = 0; i < SectorSize; i++)
            image[at + 6 * SectorSize + i] = image[at + i];

        // The allocation tables. The first two entries are reserved and hold
        // the media byte and an end marker; the root's cluster holds an end
        // marker because it is one cluster long and empty.
        for (uint table = 0; table < tables; table++)
        {
            long tableAt = at + (reserved + table * tableSectors) * SectorSize;

            WriteDword(image, tableAt + 0, 0x0FFFFFF8);
            WriteDword(image, tableAt + 4, 0x0FFFFFFF);
            WriteDword(image, tableAt + 8, 0x0FFFFFFF);   // the root, ending
        }

        // The root directory: one cluster, zeroed, which is an empty
        // directory. The volume label would go here as an entry; leaving it
        // out keeps the root genuinely empty, which is what a test wants.
        _ = clusters;
        _ = firstDataSector;
    }

    private static void WriteBigEndian16(byte[] buffer, int at, int value)
    {
        buffer[at] = (byte)(value >> 8);
        buffer[at + 1] = (byte)value;
    }

    private static void WriteBigEndian32(byte[] buffer, int at, uint value)
    {
        buffer[at] = (byte)(value >> 24);
        buffer[at + 1] = (byte)(value >> 16);
        buffer[at + 2] = (byte)(value >> 8);
        buffer[at + 3] = (byte)value;
    }

    private static void WriteBigEndian64(byte[] buffer, int at, long value)
    {
        for (int i = 0; i < 8; i++)
            buffer[at + i] = (byte)(value >> (56 - i * 8));
    }

    private static void WriteMasterBootRecord(byte[] image, HardDiskImage.Geometry geometry,
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
        long lastCylinder = geometry.Cylinders - 1;
        image[entry + 5] = (byte)(geometry.Heads - 1);
        image[entry + 6] = (byte)(SectorsPerTrack | ((lastCylinder >> 2) & 0xC0));
        image[entry + 7] = (byte)(lastCylinder & 0xFF);

        WriteDword(image, entry + 8, firstSector);
        WriteDword(image, entry + 12, partitionSectors);

        image[0x1FE] = 0x55;
        image[0x1FF] = 0xAA;
    }

    private static void WriteFat16Volume(byte[] image, long at, uint sectors, string label, int heads)
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
        WriteWord(image, at + 26, (ushort)heads);
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
