using Aurora86.Core.Devices.Storage;

namespace Aurora86.Core.Diagnostics;

/// <summary>
/// Reads a disk image the way a person looking for a problem needs to see it:
/// one sector at a time, with the structures that give a sector its meaning
/// decoded alongside the bytes.
///
/// A disk is a flat array of 512-byte sectors, and on its own that tells you
/// nothing. What turns sector 63 into "the boot sector of the first partition"
/// is a chain of agreements: the master boot record at sector 0 says where
/// partitions start; the BIOS parameter block at the start of a partition says
/// how big a cluster is and where the FAT lives; the FAT says which cluster
/// follows which. Every one of those is a place where an emulator's disk
/// writes can go subtly wrong -- and did, more than once here -- so being able
/// to look at them without leaving the emulator is worth the code.
///
/// This is deliberately read-mostly and forgiving: a field that makes no sense
/// is reported as it is rather than refused, because a corrupt structure is
/// exactly what you are usually looking at.
/// </summary>
public sealed class DiskInspector
{
    private readonly DiskImage _image;

    public DiskInspector(DiskImage image) => _image = image;

    public long TotalSectors => _image.TotalSectors;
    public bool ReadOnly => _image.ReadOnly;

    /// <summary>Reads one sector by its linear number, or an empty sector past the end.</summary>
    public byte[] ReadSector(long linear)
    {
        var buffer = new byte[DiskImage.SectorSize];
        if (linear < 0 || linear >= TotalSectors) return buffer;

        (int cylinder, int head, int sector) = ToChs(linear);
        _image.ReadSectors(cylinder, head, sector, 1, buffer);
        return buffer;
    }

    /// <summary>Writes one sector back. Returns false on a read-only image or a bad number.</summary>
    public bool WriteSector(long linear, ReadOnlySpan<byte> data)
    {
        if (ReadOnly || linear < 0 || linear >= TotalSectors) return false;

        (int cylinder, int head, int sector) = ToChs(linear);
        return _image.WriteSectors(cylinder, head, sector, 1, data);
    }

    /// <summary>
    /// Linear sector to cylinder/head/sector, the geometry the image declares.
    /// Sectors are numbered from one, heads and cylinders from zero -- an
    /// inconsistency inherited from the floppy controllers of 1981.
    /// </summary>
    public (int Cylinder, int Head, int Sector) ToChs(long linear)
    {
        int sector = (int)(linear % _image.SectorsPerTrack) + 1;
        long track = linear / _image.SectorsPerTrack;
        int head = (int)(track % _image.Heads);
        int cylinder = (int)(track / _image.Heads);
        return (cylinder, head, sector);
    }

    /// <summary>One entry of the master boot record's partition table.</summary>
    public readonly record struct Partition(
        int Index, bool Bootable, byte Type, long FirstSector, long SectorCount)
    {
        public string TypeName => Type switch
        {
            0x00 => "empty",
            0x01 => "FAT12",
            0x04 => "FAT16 <32M",
            0x05 => "extended",
            0x06 => "FAT16",
            0x0B => "FAT32",
            0x0C => "FAT32 LBA",
            0x0E => "FAT16 LBA",
            0x0F => "extended LBA",
            _ => $"type {Type:X2}",
        };
    }

    /// <summary>
    /// The four partition entries of the master boot record, in order. An
    /// image with no signature at 0x1AA55 has none: a floppy, or a hard disk
    /// formatted without a partition table, which DOS also accepted.
    /// </summary>
    public IReadOnlyList<Partition> ReadPartitions()
    {
        byte[] mbr = ReadSector(0);
        if (mbr[510] != 0x55 || mbr[511] != 0xAA) return [];

        var result = new List<Partition>();
        for (int i = 0; i < 4; i++)
        {
            int at = 0x1BE + i * 16;
            byte type = mbr[at + 4];
            uint first = BitConverter.ToUInt32(mbr, at + 8);
            uint count = BitConverter.ToUInt32(mbr, at + 12);
            if (type == 0 && count == 0) continue;

            result.Add(new Partition(i, mbr[at] == 0x80, type, first, count));
        }
        return result;
    }

    /// <summary>
    /// The BIOS parameter block at the start of a volume: the numbers every
    /// piece of software needs before it can read a single file.
    /// </summary>
    public readonly record struct BootParameters(
        string OemName, int BytesPerSector, int SectorsPerCluster, int ReservedSectors,
        int FatCount, int RootEntries, long TotalSectors, int SectorsPerFat,
        int SectorsPerTrack, int Heads, long HiddenSectors, string Label, string FileSystem)
    {
        public long FirstFatSector => ReservedSectors;
        public long RootDirectorySector => FirstFatSector + (long)FatCount * SectorsPerFat;
        public long RootDirectorySectors => (RootEntries * 32 + BytesPerSector - 1) / Math.Max(1, BytesPerSector);
        public long FirstDataSector => RootDirectorySector + RootDirectorySectors;
        public long ClusterCount => SectorsPerCluster == 0 ? 0
            : (TotalSectors - FirstDataSector) / SectorsPerCluster;

        /// <summary>FAT12 below 4085 clusters, FAT16 above: the boundary DOS itself uses.</summary>
        public int FatBits => ClusterCount < 4085 ? 12 : 16;
    }

    /// <summary>Decodes the boot sector of a volume starting at the given sector.</summary>
    public BootParameters? ReadBootParameters(long volumeStart)
    {
        byte[] boot = ReadSector(volumeStart);
        int bytesPerSector = BitConverter.ToUInt16(boot, 11);

        // A plausible sector size is the cheapest sanity check there is: a
        // sector that is not a BPB almost never has a power of two here.
        if (bytesPerSector is not (128 or 256 or 512 or 1024 or 2048 or 4096)) return null;

        long total = BitConverter.ToUInt16(boot, 19);
        if (total == 0) total = BitConverter.ToUInt32(boot, 32);

        return new BootParameters(
            OemName: Text(boot, 3, 8),
            BytesPerSector: bytesPerSector,
            SectorsPerCluster: boot[13],
            ReservedSectors: BitConverter.ToUInt16(boot, 14),
            FatCount: boot[16],
            RootEntries: BitConverter.ToUInt16(boot, 17),
            TotalSectors: total,
            SectorsPerFat: BitConverter.ToUInt16(boot, 22),
            SectorsPerTrack: BitConverter.ToUInt16(boot, 24),
            Heads: BitConverter.ToUInt16(boot, 26),
            HiddenSectors: BitConverter.ToUInt32(boot, 28),
            Label: Text(boot, 43, 11),
            FileSystem: Text(boot, 54, 8));
    }

    /// <summary>What a sector is, as far as the structures can tell.</summary>
    public string DescribeSector(long linear)
    {
        if (linear == 0) return ReadPartitions().Count > 0 ? "master boot record" : "boot sector";

        foreach (Partition partition in ReadPartitions())
        {
            if (linear < partition.FirstSector) continue;
            if (linear >= partition.FirstSector + partition.SectorCount) continue;

            long offset = linear - partition.FirstSector;
            if (offset == 0) return $"partition {partition.Index + 1} boot sector ({partition.TypeName})";

            if (ReadBootParameters(partition.FirstSector) is not { } bpb)
                return $"partition {partition.Index + 1}, sector {offset}";

            if (offset < bpb.FirstFatSector) return $"partition {partition.Index + 1}, reserved";
            if (offset < bpb.RootDirectorySector)
            {
                long fat = (offset - bpb.FirstFatSector) / Math.Max(1, bpb.SectorsPerFat);
                return $"partition {partition.Index + 1}, FAT {fat + 1}";
            }
            if (offset < bpb.FirstDataSector) return $"partition {partition.Index + 1}, root directory";

            long cluster = (offset - bpb.FirstDataSector) / Math.Max(1, bpb.SectorsPerCluster) + 2;
            return $"partition {partition.Index + 1}, cluster {cluster}";
        }

        return "outside every partition";
    }

    /// <summary>
    /// The first sector of a cluster, in the volume that starts at
    /// <paramref name="volumeStart"/>. Clusters are numbered from two: the
    /// first two entries of the FAT are reserved, and rather than waste them
    /// the numbering simply begins after them.
    /// </summary>
    public long ClusterToSector(long volumeStart, long cluster)
    {
        if (ReadBootParameters(volumeStart) is not { } bpb) return -1;
        if (cluster < 2) return -1;

        return volumeStart + bpb.FirstDataSector + (cluster - 2) * bpb.SectorsPerCluster;
    }

    /// <summary>Where a file lives, found by its path: its first cluster and its size.</summary>
    public readonly record struct FileLocation(long FirstCluster, uint Size, bool IsDirectory);

    /// <summary>
    /// Finds a file by its DOS path within the volume -- "\DOS\COMMAND.COM"
    /// -- and says where it starts.
    ///
    /// This is the direction <see cref="DescribeSector"/> does not go. That
    /// one answers "what is at this sector"; the question a person brings to
    /// a disk editor is the other way round, "where is this file", and
    /// answering it by hand means reading the root directory, finding the
    /// cluster, converting it to a sector, and doing it again for every
    /// directory on the way. Long names are ignored: DOS never sees them
    /// either, and the eight-plus-three entry is the one that owns the
    /// clusters.
    /// </summary>
    public FileLocation? FindFile(long volumeStart, string path)
    {
        if (ReadBootParameters(volumeStart) is not { } bpb) return null;

        // The root directory is a fixed run of sectors before the data area;
        // every directory below it is a file whose content is entries, and
        // is read the way any file is, cluster by cluster.
        IEnumerable<long> sectors = Enumerable.Range(0, (int)bpb.RootDirectorySectors)
            .Select(i => volumeStart + bpb.RootDirectorySector + i);

        FileLocation? found = null;
        foreach (string part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            found = FindEntry(sectors, part);
            if (found is not { } entry) return null;

            sectors = entry.IsDirectory
                ? ReadClusterChain(volumeStart, entry.FirstCluster)
                    .SelectMany(cluster => Enumerable.Range(0, bpb.SectorsPerCluster)
                        .Select(i => ClusterToSector(volumeStart, cluster) + i))
                : [];                                   // a file: nothing lies beneath it
        }

        return found;
    }

    private FileLocation? FindEntry(IEnumerable<long> sectors, string name)
    {
        string wanted = ToEightThree(name);

        foreach (long sector in sectors)
        {
            byte[] data = ReadSector(sector);
            for (int at = 0; at + 32 <= data.Length; at += 32)
            {
                byte first = data[at];
                if (first == 0) return null;                    // the end of the directory
                if (first == 0xE5) continue;                    // deleted

                // Bit 3 marks a volume label, and every piece of a long name
                // sets it too, which is what makes skipping them one test.
                byte attributes = data[at + 11];
                if ((attributes & 0x08) != 0) continue;

                if (System.Text.Encoding.ASCII.GetString(data, at, 11) != wanted) continue;

                long cluster = data[at + 26] | (data[at + 27] << 8);
                uint size = (uint)(data[at + 28] | (data[at + 29] << 8)
                                 | (data[at + 30] << 16) | (data[at + 31] << 24));
                return new FileLocation(cluster, size, (attributes & 0x10) != 0);
            }
        }

        return null;
    }

    /// <summary>
    /// "command.com" as the directory stores it: upper case, the stem padded
    /// to eight and the extension to three, no dot. Not a display convention
    /// but the storage format itself, which is why the comparison is on the
    /// stored form and not the typed one.
    /// </summary>
    private static string ToEightThree(string name)
    {
        name = name.ToUpperInvariant();
        int dot = name.IndexOf('.');
        string stem = dot < 0 ? name : name[..dot];
        string extension = dot < 0 ? "" : name[(dot + 1)..];
        return stem.PadRight(8)[..8] + extension.PadRight(3)[..3];
    }

    /// <summary>
    /// The whole of a file, by its path, or null when there is no such file
    /// or it is a directory. Follows the chain and stops at the size the
    /// directory entry declares, because the last cluster is full of
    /// whatever was there before.
    /// </summary>
    public byte[]? ReadFile(long volumeStart, string path)
    {
        if (FindFile(volumeStart, path) is not { IsDirectory: false } file) return null;
        if (ReadBootParameters(volumeStart) is not { } bpb) return null;

        var output = new MemoryStream();
        foreach (long cluster in ReadClusterChain(volumeStart, file.FirstCluster))
        {
            long first = ClusterToSector(volumeStart, cluster);
            for (int i = 0; i < bpb.SectorsPerCluster && output.Length < file.Size; i++)
                output.Write(ReadSector(first + i));
        }

        output.SetLength(Math.Min(output.Length, file.Size));
        return output.ToArray();
    }

    /// <summary>Follows the FAT chain from a cluster, stopping at the end or on a loop.</summary>
    public IReadOnlyList<long> ReadClusterChain(long volumeStart, long first, int limit = 100_000)
    {
        if (ReadBootParameters(volumeStart) is not { } bpb) return [];

        var chain = new List<long>();
        var seen = new HashSet<long>();
        long cluster = first;

        while (cluster >= 2 && chain.Count < limit && seen.Add(cluster))
        {
            long end = bpb.FatBits == 12 ? 0xFF8 : 0xFFF8;
            if (cluster >= end) break;

            chain.Add(cluster);
            cluster = ReadFatEntry(volumeStart, bpb, cluster);
        }

        return chain;
    }

    /// <summary>
    /// One FAT entry. FAT12 packs three nibbles per pair of entries, which is
    /// why an odd cluster takes the high twelve bits of the word and an even
    /// one the low twelve -- the format was designed when a floppy's FAT had
    /// to fit in a few hundred bytes.
    /// </summary>
    public long ReadFatEntry(long volumeStart, BootParameters bpb, long cluster)
    {
        if (bpb.FatBits == 16)
        {
            long offset = cluster * 2;
            byte[] sector = ReadSector(volumeStart + bpb.FirstFatSector + offset / bpb.BytesPerSector);
            int at = (int)(offset % bpb.BytesPerSector);
            return BitConverter.ToUInt16(sector, at);
        }

        long byteOffset = cluster + cluster / 2;
        long sectorNumber = volumeStart + bpb.FirstFatSector + byteOffset / bpb.BytesPerSector;
        int within = (int)(byteOffset % bpb.BytesPerSector);

        byte[] first = ReadSector(sectorNumber);
        byte low = first[within];
        byte high = within + 1 < bpb.BytesPerSector
            ? first[within + 1]
            : ReadSector(sectorNumber + 1)[0];

        int value = low | (high << 8);
        return (cluster & 1) != 0 ? value >> 4 : value & 0xFFF;
    }

    /// <summary>One directory entry, as the 32 bytes on disk describe it.</summary>
    public readonly record struct DirectoryEntry(
        string Name, byte Attributes, long FirstCluster, long Size, DateTime Modified)
    {
        public bool IsDirectory => (Attributes & 0x10) != 0;
        public bool IsVolumeLabel => (Attributes & 0x08) != 0;
        public bool IsLongNameFragment => (Attributes & 0x0F) == 0x0F;
    }

    /// <summary>
    /// Reads a directory: the root of a volume when <paramref name="cluster"/>
    /// is zero, otherwise the chain starting at that cluster.
    /// </summary>
    public IReadOnlyList<DirectoryEntry> ReadDirectory(long volumeStart, long cluster = 0)
    {
        if (ReadBootParameters(volumeStart) is not { } bpb) return [];

        var bytes = new List<byte>();
        if (cluster == 0)
        {
            for (long i = 0; i < bpb.RootDirectorySectors; i++)
                bytes.AddRange(ReadSector(volumeStart + bpb.RootDirectorySector + i));
        }
        else
        {
            foreach (long link in ReadClusterChain(volumeStart, cluster))
                for (int i = 0; i < bpb.SectorsPerCluster; i++)
                    bytes.AddRange(ReadSector(ClusterToSector(volumeStart, link) + i));
        }

        var entries = new List<DirectoryEntry>();
        for (int at = 0; at + 32 <= bytes.Count; at += 32)
        {
            byte first = bytes[at];
            if (first == 0x00) break;          // nothing beyond here has ever been used
            if (first == 0xE5) continue;       // deleted

            byte attributes = bytes[at + 11];
            string name = Text(bytes, at, 8).TrimEnd();
            string extension = Text(bytes, at + 8, 3).TrimEnd();
            if (extension.Length > 0) name += "." + extension;

            entries.Add(new DirectoryEntry(
                name,
                attributes,
                BitConverter.ToUInt16(bytes.ToArray(), at + 26),
                BitConverter.ToUInt32(bytes.ToArray(), at + 28),
                DecodeTimestamp(BitConverter.ToUInt16(bytes.ToArray(), at + 24),
                                BitConverter.ToUInt16(bytes.ToArray(), at + 22))));
        }
        return entries;
    }

    /// <summary>
    /// The DOS date and time: a date packed into sixteen bits counting from
    /// 1980, and a time whose seconds field is halved because two bits were
    /// not to be spared.
    /// </summary>
    private static DateTime DecodeTimestamp(ushort date, ushort time)
    {
        int year = 1980 + (date >> 9);
        int month = Math.Clamp((date >> 5) & 0x0F, 1, 12);
        int day = Math.Clamp(date & 0x1F, 1, 31);
        int hour = Math.Clamp(time >> 11, 0, 23);
        int minute = Math.Clamp((time >> 5) & 0x3F, 0, 59);
        int second = Math.Clamp((time & 0x1F) * 2, 0, 59);

        try { return new DateTime(year, month, day, hour, minute, second); }
        catch (ArgumentOutOfRangeException) { return DateTime.MinValue; }
    }

    private static string Text(IReadOnlyList<byte> data, int at, int length)
    {
        var builder = new System.Text.StringBuilder(length);
        for (int i = 0; i < length && at + i < data.Count; i++)
        {
            byte value = data[at + i];
            builder.Append(value is >= 32 and < 127 ? (char)value : ' ');
        }
        return builder.ToString();
    }

    private static string Text(byte[] data, int at, int length) => Text((IReadOnlyList<byte>)data, at, length);
}
