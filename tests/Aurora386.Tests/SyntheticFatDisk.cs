using System.Text;

using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Diagnostics;

namespace Aurora386.Tests;

/// <summary>
/// A small disposable FAT16 hard disk for tests that need DOS files without
/// depending on an installed, copyrighted disk image. Its directory and FAT
/// are written through the same inspector that debugger-facing tests use.
/// </summary>
internal sealed class SyntheticFatDisk : IDisposable
{
    private const int DiskSizeBytes = 10 * 1024 * 1024;
    private const ushort EndOfChain = 0xFFFF;
    private const int DirectoryEntrySize = 32;
    private const byte ArchiveAttribute = 0x20;
    private const byte DirectoryAttribute = 0x10;

    private readonly string _path;
    private readonly HardDiskImage.Geometry _geometry;

    public SyntheticFatDisk()
    {
        _path = Path.Combine(Path.GetTempPath(), $"aurora-synthetic-fat-{Guid.NewGuid():N}.img");
        _geometry = HardDiskImage.Create(_path, DiskSizeBytes);

        using DiskImage image = OpenImage();
        var inspector = new DiskInspector(image);
        VolumeStart = Assert.Single(inspector.ReadPartitions()).FirstSector;
        BootParameters = Assert.NotNull(inspector.ReadBootParameters(VolumeStart));

        // The second fragment forces the reader to follow 2 -> 5 instead of
        // assuming contiguous allocation. Clusters 3 and 4 hold DOS and its file.
        CommandContent = MakeContent("synthetic COMMAND.COM payload", ClusterBytes + 123);
        WriteFile(inspector, "COMMAND COM", ArchiveAttribute, 2, CommandContent, [2, 5]);
        WriteFile(inspector, "DOS        ", DirectoryAttribute, 3, [], [3]);
        WriteFile(inspector, "CONFIG  SYS", ArchiveAttribute, 6, "FILES=30\r\n"u8.ToArray(), [6]);
        WriteFile(inspector, "AUTOEXECBAT", ArchiveAttribute, 7, "PATH=C:\\DOS\r\n"u8.ToArray(), [7]);

        byte[] smartDriveContent = "synthetic SMARTDRV.EXE payload"u8.ToArray();
        WriteDirectory(inspector, 3,
            (".          ", DirectoryAttribute, 3, 0u),
            ("..         ", DirectoryAttribute, 0, 0u),
            ("SMARTDRVEXE", ArchiveAttribute, 4, (uint)smartDriveContent.Length));
        WriteCluster(inspector, 4, smartDriveContent);
        WriteFatEntry(inspector, 2, 5);
        WriteFatEntry(inspector, 3, EndOfChain);
        WriteFatEntry(inspector, 4, EndOfChain);
        WriteFatEntry(inspector, 5, EndOfChain);
        WriteFatEntry(inspector, 6, EndOfChain);
        WriteFatEntry(inspector, 7, EndOfChain);
    }

    public long VolumeStart { get; }
    public DiskInspector.BootParameters BootParameters { get; }
    public byte[] CommandContent { get; }

    /// <summary>Opens this fixture as the first BIOS hard disk.</summary>
    public DiskImage OpenImage()
    {
        return DiskImage.OpenHardDisk(_path);
    }

    /// <summary>Returns a data cluster's first sector from this volume's BPB.</summary>
    public long ClusterToSector(long cluster)
    {
        // FAT data clusters are numbered from 2; entries 0 and 1 are reserved.
        return VolumeStart + BootParameters.FirstDataSector
            + (cluster - 2) * BootParameters.SectorsPerCluster;
    }

    /// <summary>Converts an absolute sector to the BIOS geometry of this image.</summary>
    public (int Cylinder, int Head, int Sector) ToChs(long sector)
    {
        return HardDiskImage.ToChs(_geometry, sector);
    }

    public void Dispose()
    {
        File.Delete(_path);
    }

    /// <summary>Writes one root entry and distributes its contents among explicit data clusters.</summary>
    private void WriteFile(DiskInspector inspector, string name, byte attributes,
                           int firstCluster, byte[] content, int[] clusters)
    {
        WriteDirectoryEntry(inspector, name, attributes, firstCluster, (uint)content.Length);

        int offset = 0;
        foreach (int cluster in clusters)
        {
            int bytes = Math.Min(ClusterBytes, content.Length - offset);
            WriteCluster(inspector, cluster, content.AsSpan(offset, bytes));
            offset += bytes;
        }
    }

    /// <summary>Creates the subdirectory entries independently of the inspector's path lookup.</summary>
    private void WriteDirectory(DiskInspector inspector, int cluster,
                                params (string Name, int Attributes, int FirstCluster, uint Size)[] entries)
    {
        byte[] sector = new byte[DiskImage.SectorSize];
        for (int index = 0; index < entries.Length; index++)
        {
            (string name, int attributes, int firstCluster, uint size) = entries[index];
            Span<byte> entry = sector.AsSpan(index * DirectoryEntrySize, DirectoryEntrySize);
            WriteDirectoryEntry(entry, name, (byte)attributes, firstCluster, size);
        }

        WriteCluster(inspector, cluster, sector);
    }

    /// <summary>Appends a root entry to the fixture's initially empty FAT16 directory.</summary>
    private void WriteDirectoryEntry(DiskInspector inspector, string name, byte attributes,
                                     int firstCluster, uint size)
    {
        long root = VolumeStart + BootParameters.RootDirectorySector;
        byte[] sector = inspector.ReadSector(root);
        int entry = 0;
        while (sector[entry * DirectoryEntrySize] != 0)
        {
            entry++;
        }

        Span<byte> directoryEntry = sector.AsSpan(entry * DirectoryEntrySize, DirectoryEntrySize);
        WriteDirectoryEntry(directoryEntry, name, attributes, firstCluster, size);
        Assert.True(inspector.WriteSector(root, sector));
    }

    /// <summary>Encodes the DOS 8.3 name, attributes, cluster and byte length at their FAT16 offsets.</summary>
    private static void WriteDirectoryEntry(Span<byte> entry, string name, byte attributes,
                                            int firstCluster, uint size)
    {
        Encoding.ASCII.GetBytes(name).CopyTo(entry);
        entry[11] = attributes; // Attribute byte follows the eleven-byte short name.
        BitConverter.GetBytes((ushort)firstCluster).CopyTo(entry[26..]); // First cluster.
        BitConverter.GetBytes(size).CopyTo(entry[28..]); // File size in bytes.
    }

    /// <summary>Writes every sector of a data cluster, padding the unused tail with zeros.</summary>
    private void WriteCluster(DiskInspector inspector, int cluster, ReadOnlySpan<byte> content)
    {
        byte[] clusterBytes = new byte[ClusterBytes];
        content.CopyTo(clusterBytes);

        long firstSector = ClusterToSector(cluster);
        for (int sector = 0; sector < BootParameters.SectorsPerCluster; sector++)
        {
            ReadOnlySpan<byte> bytes = clusterBytes.AsSpan(sector * DiskImage.SectorSize,
                                                            DiskImage.SectorSize);
            Assert.True(inspector.WriteSector(firstSector + sector, bytes));
        }
    }

    /// <summary>Mirrors a cluster-chain link into both FAT copies.</summary>
    private void WriteFatEntry(DiskInspector inspector, int cluster, ushort next)
    {
        long offset = cluster * sizeof(ushort);
        for (int copy = 0; copy < BootParameters.FatCount; copy++)
        {
            long sectorNumber = VolumeStart + BootParameters.FirstFatSector
                              + copy * BootParameters.SectorsPerFat
                              + offset / BootParameters.BytesPerSector;
            byte[] sector = inspector.ReadSector(sectorNumber);
            BitConverter.GetBytes(next).CopyTo(sector, (int)(offset % BootParameters.BytesPerSector));
            Assert.True(inspector.WriteSector(sectorNumber, sector));
        }
    }

    private int ClusterBytes => BootParameters.SectorsPerCluster * DiskImage.SectorSize;

    /// <summary>Builds deterministic synthetic file contents which can be compared in full.</summary>
    private static byte[] MakeContent(string line, int length)
    {
        byte[] pattern = Encoding.ASCII.GetBytes(line + "\r\n");
        var content = new byte[length];
        for (int offset = 0; offset < content.Length; offset += pattern.Length)
        {
            int fragmentLength = Math.Min(pattern.Length, content.Length - offset);
            pattern.AsSpan(0, fragmentLength).CopyTo(content.AsSpan(offset));
        }

        return content;
    }
}
