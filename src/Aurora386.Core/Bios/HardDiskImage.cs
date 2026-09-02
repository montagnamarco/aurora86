namespace Aurora386.Core.Bios;

/// <summary>
/// Creation and preparation of hard disk images.
///
/// Unlike a floppy, a hard disk does not begin with a file system at the very
/// first sector: it starts with the Master Boot Record, which holds the
/// partition table plus the code that loads the boot sector of the active
/// partition. That is the layout FDISK creates and the one DOS expects.
/// </summary>
public static class HardDiskImage
{
    public const int SectorSize = 512;

    /// <summary>
    /// Geometry of a hard disk. The BIOS addresses sectors in CHS, so the size
    /// alone is not enough: cylinders, heads and sectors per track are all
    /// needed. The prevailing convention of the era was 16 heads and 63 sectors
    /// per track, with the cylinder count making up the rest.
    /// </summary>
    public readonly record struct Geometry(int Cylinders, int Heads, int SectorsPerTrack)
    {
        public long TotalSectors => (long)Cylinders * Heads * SectorsPerTrack;
        public long TotalBytes => TotalSectors * SectorSize;

        /// <summary>
        /// Derives a plausible geometry for the requested size. The 1024
        /// cylinder ceiling is the limit of the BIOS CHS interface: beyond it,
        /// period drives resorted to "translated" geometries.
        /// </summary>
        public static Geometry ForSize(long bytes)
        {
            const int heads = 16;
            const int sectorsPerTrack = 63;
            int cylinders = (int)(bytes / (heads * sectorsPerTrack * SectorSize));

            if (cylinders < 1) cylinders = 1;
            if (cylinders > 1024) cylinders = 1024;   // classic CHS limit

            return new Geometry(cylinders, heads, sectorsPerTrack);
        }
    }

    /// <summary>
    /// Partition types DOS recognizes. The type tells the system which file
    /// system to expect and how large the partition is allowed to be.
    /// </summary>
    public static class PartitionType
    {
        public const byte Fat12 = 0x01;
        /// <summary>FAT16 up to 32 MB, the only kind DOS 3.x-4.x can handle.</summary>
        public const byte Fat16Small = 0x04;
        public const byte Extended = 0x05;
        /// <summary>FAT16 above 32 MB, introduced with DOS 4.0.</summary>
        public const byte Fat16 = 0x06;
    }

    /// <summary>
    /// Creates an empty image of the requested size, with an MBR and a single
    /// active primary partition formatted FAT16 -- that is, a disk ready to
    /// have DOS installed onto it.
    /// </summary>
    public static Geometry Create(string path, long sizeBytes, bool bootable = true)
    {
        Geometry geometry = Geometry.ForSize(sizeBytes);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        stream.SetLength(geometry.TotalBytes);

        // The partition starts at track 1: the first sector of head 0 is the
        // MBR, and by convention the partition begins on the following track,
        // leaving the rest of that first track free for boot managers.
        int partitionStartSector = geometry.SectorsPerTrack;
        long partitionSectors = geometry.TotalSectors - partitionStartSector;

        byte[] mbr = BuildMasterBootRecord(geometry, partitionStartSector,
                                           partitionSectors, bootable);
        stream.Seek(0, SeekOrigin.Begin);
        stream.Write(mbr);

        // Format the partition as FAT16 so it is usable right away.
        byte[] bootSector = BuildFat16BootSector(geometry, partitionSectors);
        stream.Seek((long)partitionStartSector * SectorSize, SeekOrigin.Begin);
        stream.Write(bootSector);

        WriteEmptyFatTables(stream, partitionStartSector, bootSector);

        return geometry;
    }

    /// <summary>
    /// Builds the Master Boot Record: the partition table plus the code that
    /// loads and starts the active partition.
    /// </summary>
    private static byte[] BuildMasterBootRecord(Geometry geometry, int startSector,
                                                long partitionSectors, bool bootable)
    {
        var mbr = new byte[SectorSize];

        // Boot code: relocate itself lower in memory, find the active
        // partition, load its first sector to 0000:7C00 and jump into it. This
        // is what the MBR written by FDISK does.
        //
        // The relocation is necessary because the partition's boot sector
        // insists on living at 0x7C00 -- exactly where the MBR itself is
        // currently running.
        int p = 0;
        void Emit(params byte[] bytes) { foreach (byte b in bytes) mbr[p++] = b; }

        Emit(0xFA);                          // CLI
        Emit(0x33, 0xC0);                    // XOR AX,AX
        Emit(0x8E, 0xD0);                    // MOV SS,AX
        Emit(0xBC, 0x00, 0x7C);              // MOV SP,0x7C00
        Emit(0x8E, 0xD8);                    // MOV DS,AX
        Emit(0x8E, 0xC0);                    // MOV ES,AX
        Emit(0xFB);                          // STI
        Emit(0xFC);                          // CLD (copy moves forward)

        Emit(0xBE, 0x00, 0x7C);              // MOV SI,0x7C00
        Emit(0xBF, 0x00, 0x06);              // MOV DI,0x0600
        Emit(0xB9, 0x00, 0x02);              // MOV CX,512
        Emit(0xF3, 0xA4);                    // REP MOVSB

        // Jump to the relocated copy. The target address can only be computed
        // now that the length of the preamble is known: execution continues at
        // the same offset, but inside the moved block.
        int afterJump = p + 5;
        Emit(0xEA);                                        // JMP FAR
        Emit((byte)(0x0600 + afterJump), (byte)((0x0600 + afterJump) >> 8));
        Emit(0x00, 0x00);                                  // segment 0

        // From here on execution happens at 0x0600 + offset.
        // The relocated partition table therefore sits at 0x0600 + 0x1BE = 0x7BE.
        Emit(0xBE, 0xBE, 0x07);              // MOV SI,0x07BE
        Emit(0xB9, 0x04, 0x00);              // MOV CX,4  (four entries)

        int searchLoop = 0x0600 + p;
        Emit(0x80, 0x3C, 0x80);              // CMP byte [SI],0x80  (active?)
        Emit(0x74, 0x0E);                    // JZ found  (+14: skips the 4 instructions below)
        Emit(0x83, 0xC6, 0x10);              // ADD SI,16   (next entry)
        int loopTarget = searchLoop - (0x0600 + p + 2);
        Emit(0xE2, (byte)loopTarget);        // LOOP back

        // No active partition: print a message and stop.
        Emit(0xBE, 0x00, 0x07);              // MOV SI,message (0x0700)
        Emit(0xEB, 0x1A);                    // JMP print

        // found: load the boot sector of the partition.
        Emit(0x8A, 0x14);                    // MOV DL,[SI]      (status byte, not the drive)
        Emit(0xB2, 0x80);                    // MOV DL,0x80      (fixed disk)
        Emit(0x8B, 0x4C, 0x02);              // MOV CX,[SI+2]    (cylinder and sector)
        Emit(0x8A, 0x74, 0x01);              // MOV DH,[SI+1]    (head)
        Emit(0xBB, 0x00, 0x7C);              // MOV BX,0x7C00
        Emit(0xB8, 0x01, 0x02);              // MOV AX,0x0201    (read 1 sector)
        Emit(0xCD, 0x13);                    // INT 13h
        Emit(0x72, 0x07);                    // JC error (+7)
        Emit(0xEA, 0x00, 0x7C, 0x00, 0x00);  // JMP FAR 0000:7C00

        // read error: show the message.
        Emit(0xBE, 0x20, 0x07);              // MOV SI,0x0720

        // print: write the string at SI, then halt.
        Emit(0xAC);                          // LODSB
        Emit(0x08, 0xC0);                    // OR AL,AL
        Emit(0x74, 0x06);                    // JZ done
        Emit(0xB4, 0x0E);                    // MOV AH,0x0E
        Emit(0xCD, 0x10);                    // INT 10h
        Emit(0xEB, 0xF5);                    // JMP print
        Emit(0xFA);                          // CLI
        Emit(0xF4);                          // HLT
        Emit(0xEB, 0xFD);                    // JMP -3

        // The messages live at fixed offsets so the code above can point at them.
        // These strings are guest-visible screen output rather than developer
        // text, and the test suite asserts on them verbatim, so they are left
        // in the original language.
        "Nessuna partizione avviabile\r\n\0"u8.CopyTo(mbr.AsSpan(0x100));
        "Errore di lettura del disco\r\n\0"u8.CopyTo(mbr.AsSpan(0x120));

        // Partition table: four 16-byte entries starting at 0x1BE.
        WritePartitionEntry(mbr, 0x1BE, geometry, startSector, partitionSectors,
                            PartitionType.Fat16, bootable);

        mbr[0x1FE] = 0x55;
        mbr[0x1FF] = 0xAA;
        return mbr;
    }

    /// <summary>
    /// Writes one entry of the partition table. The CHS addresses use a packed
    /// encoding: the two high bits of the cylinder end up in the high bits of
    /// the sector byte.
    /// </summary>
    private static void WritePartitionEntry(byte[] sector, int offset, Geometry geometry,
                                            long startSector, long sectorCount,
                                            byte type, bool active)
    {
        sector[offset + 0] = (byte)(active ? 0x80 : 0x00);

        (int startCyl, int startHead, int startSec) = ToChs(geometry, startSector);
        sector[offset + 1] = (byte)startHead;
        sector[offset + 2] = (byte)((startSec & 0x3F) | ((startCyl >> 2) & 0xC0));
        sector[offset + 3] = (byte)(startCyl & 0xFF);

        sector[offset + 4] = type;

        (int endCyl, int endHead, int endSec) = ToChs(geometry, startSector + sectorCount - 1);
        sector[offset + 5] = (byte)endHead;
        sector[offset + 6] = (byte)((endSec & 0x3F) | ((endCyl >> 2) & 0xC0));
        sector[offset + 7] = (byte)(endCyl & 0xFF);

        BitConverter.GetBytes((uint)startSector).CopyTo(sector, offset + 8);
        BitConverter.GetBytes((uint)sectorCount).CopyTo(sector, offset + 12);
    }

    /// <summary>Converts a linear sector number into cylinder, head and sector.</summary>
    public static (int Cylinder, int Head, int Sector) ToChs(Geometry geometry, long linear)
    {
        int sector = (int)(linear % geometry.SectorsPerTrack) + 1;   // sectors are numbered from 1
        long track = linear / geometry.SectorsPerTrack;
        int head = (int)(track % geometry.Heads);
        int cylinder = (int)(track / geometry.Heads);

        // Past the CHS limit we report the maximum value, the same way real
        // drives did once the geometry could no longer describe them.
        if (cylinder > 1023) { cylinder = 1023; head = geometry.Heads - 1; sector = geometry.SectorsPerTrack; }

        return (cylinder, head, sector);
    }

    /// <summary>
    /// Builds the boot sector of a FAT16 partition, including the BPB that
    /// describes the file system. DOS reads these values to find the FAT, the
    /// root directory and the data area.
    /// </summary>
    private static byte[] BuildFat16BootSector(Geometry geometry, long partitionSectors)
    {
        var boot = new byte[SectorSize];

        boot[0] = 0xEB; boot[1] = 0x3C; boot[2] = 0x90;      // short JMP + NOP
        "MSDOS5.0"u8.CopyTo(boot.AsSpan(3));

        // The cluster size determines how many clusters are needed: FAT16 tops
        // out at 65524 of them, so on a large disk the cluster has to grow.
        int sectorsPerCluster = ChooseClusterSize(partitionSectors);

        BitConverter.GetBytes((ushort)SectorSize).CopyTo(boot, 11);
        boot[13] = (byte)sectorsPerCluster;
        BitConverter.GetBytes((ushort)1).CopyTo(boot, 14);     // reserved sectors
        boot[16] = 2;                                          // two copies of the FAT
        BitConverter.GetBytes((ushort)512).CopyTo(boot, 17);   // root directory entries
        BitConverter.GetBytes((ushort)0).CopyTo(boot, 19);     // total sectors: use the 32-bit field instead
        boot[21] = 0xF8;                                       // media descriptor: fixed disk

        long dataSectors = partitionSectors;
        long clusters = dataSectors / sectorsPerCluster;
        int sectorsPerFat = (int)((clusters * 2 + SectorSize - 1) / SectorSize) + 1;

        BitConverter.GetBytes((ushort)sectorsPerFat).CopyTo(boot, 22);
        BitConverter.GetBytes((ushort)geometry.SectorsPerTrack).CopyTo(boot, 24);
        BitConverter.GetBytes((ushort)geometry.Heads).CopyTo(boot, 26);
        BitConverter.GetBytes((uint)geometry.SectorsPerTrack).CopyTo(boot, 28);  // hidden sectors
        BitConverter.GetBytes((uint)partitionSectors).CopyTo(boot, 32);

        boot[36] = 0x80;                                       // BIOS drive number
        boot[38] = 0x29;                                       // extended boot signature
        BitConverter.GetBytes((uint)0x12345678).CopyTo(boot, 39);
        "NO NAME    "u8.CopyTo(boot.AsSpan(43));
        "FAT16   "u8.CopyTo(boot.AsSpan(54));

        // Minimal code and a message: a freshly partitioned disk is not
        // bootable until SYS copies the system files onto it. As in the MBR
        // above, the message itself is guest-visible output and stays as it is.
        int p = 62;
        boot[p++] = 0xBE; boot[p++] = 0x80; boot[p++] = 0x7D;  // MOV SI,message
        boot[p++] = 0xAC;                                       // LODSB
        boot[p++] = 0x08; boot[p++] = 0xC0;                     // OR AL,AL
        boot[p++] = 0x74; boot[p++] = 0x06;                     // JZ done
        boot[p++] = 0xB4; boot[p++] = 0x0E;                     // MOV AH,0x0E
        boot[p++] = 0xCD; boot[p++] = 0x10;                     // INT 10h
        boot[p++] = 0xEB; boot[p++] = 0xF5;                     // JMP loop
        boot[p++] = 0xF4;                                       // HLT
        boot[p++] = 0xEB; boot[p++] = 0xFD;                     // JMP -3

        "Disco non di sistema\r\n\0"u8.CopyTo(boot.AsSpan(0x180));

        boot[0x1FE] = 0x55;
        boot[0x1FF] = 0xAA;
        return boot;
    }

    /// <summary>
    /// Picks the cluster size. FAT16 addresses at most 65524 usable clusters;
    /// going beyond that would require FAT32, which DOS 6.22 knows nothing
    /// about.
    /// </summary>
    private static int ChooseClusterSize(long partitionSectors)
    {
        foreach (int candidate in new[] { 4, 8, 16, 32, 64, 128 })
        {
            if (partitionSectors / candidate <= 65524) return candidate;
        }
        return 128;
    }

    /// <summary>
    /// Lays down empty FATs and an empty root directory. The first two FAT
    /// entries are reserved and carry the media descriptor.
    /// </summary>
    private static void WriteEmptyFatTables(Stream stream, int partitionStart, byte[] bootSector)
    {
        int reservedSectors = BitConverter.ToUInt16(bootSector, 14);
        int fatCount = bootSector[16];
        int sectorsPerFat = BitConverter.ToUInt16(bootSector, 22);
        int rootEntries = BitConverter.ToUInt16(bootSector, 17);

        var emptyFat = new byte[SectorSize];
        emptyFat[0] = 0xF8; emptyFat[1] = 0xFF;    // media descriptor
        emptyFat[2] = 0xFF; emptyFat[3] = 0xFF;    // end-of-chain marker

        for (int copy = 0; copy < fatCount; copy++)
        {
            long fatStart = (long)(partitionStart + reservedSectors + copy * sectorsPerFat) * SectorSize;
            stream.Seek(fatStart, SeekOrigin.Begin);
            stream.Write(emptyFat);                 // only the first sector has content

            // The rest of the FAT stays zero: it already is in a freshly created file.
        }

        // A zeroed root directory means "no files", and that too is already the
        // state of a freshly created file.
        long rootStart = (long)(partitionStart + reservedSectors + fatCount * sectorsPerFat) * SectorSize;
        stream.Seek(rootStart + (long)rootEntries * 32 - 1, SeekOrigin.Begin);
        stream.WriteByte(0);
    }
}
