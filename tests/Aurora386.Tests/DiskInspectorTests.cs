using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Diagnostics;

namespace Aurora386.Tests;

/// <summary>
/// The disk inspector behind the debugger's disk editor: it has to read a
/// real image the way DOS does, because the whole point is looking at a disk
/// that may be wrong and saying where.
/// </summary>
public class DiskInspectorTests
{
    /// <summary>A 10 MB image with a partition table and a FAT volume on it.</summary>
    private static DiskImage Formatted()
    {
        string path = Path.Combine(Path.GetTempPath(), $"aurora-inspect-{Guid.NewGuid():N}.img");
        HardDiskImage.Create(path, 10 * 1024 * 1024);
        return DiskImage.OpenHardDisk(path);
    }

    [Fact]
    public void ThePartitionTableIsReadFromTheMasterBootRecord()
    {
        using DiskImage image = Formatted();
        var inspector = new DiskInspector(image);

        IReadOnlyList<DiskInspector.Partition> partitions = inspector.ReadPartitions();

        DiskInspector.Partition first = Assert.Single(partitions);
        Assert.True(first.Bootable);
        Assert.True(first.FirstSector > 0);          // never sector 0: that is the table itself
        Assert.Contains("FAT", first.TypeName);
    }

    [Fact]
    public void TheBootParametersDescribeAVolumeThatAddsUp()
    {
        using DiskImage image = Formatted();
        var inspector = new DiskInspector(image);
        long start = inspector.ReadPartitions()[0].FirstSector;

        DiskInspector.BootParameters bpb = Assert.NotNull(inspector.ReadBootParameters(start));

        Assert.Equal(512, bpb.BytesPerSector);
        Assert.Equal(2, bpb.FatCount);
        Assert.True(bpb.SectorsPerCluster > 0);
        Assert.True(bpb.RootEntries > 0);

        // The regions have to follow one another without overlapping, which is
        // the arithmetic every DOS utility does and gets wrong at its peril.
        Assert.True(bpb.FirstFatSector < bpb.RootDirectorySector);
        Assert.True(bpb.RootDirectorySector < bpb.FirstDataSector);
        Assert.True(bpb.FirstDataSector < bpb.TotalSectors);
    }

    [Fact]
    public void EverySectorIsGivenTheNameOfWhatLivesThere()
    {
        using DiskImage image = Formatted();
        var inspector = new DiskInspector(image);
        long start = inspector.ReadPartitions()[0].FirstSector;
        DiskInspector.BootParameters bpb = inspector.ReadBootParameters(start)!.Value;

        Assert.Contains("master boot record", inspector.DescribeSector(0));
        Assert.Contains("boot sector", inspector.DescribeSector(start));
        Assert.Contains("FAT 1", inspector.DescribeSector(start + bpb.FirstFatSector));
        Assert.Contains("FAT 2", inspector.DescribeSector(start + bpb.FirstFatSector + bpb.SectorsPerFat));
        Assert.Contains("root directory", inspector.DescribeSector(start + bpb.RootDirectorySector));
        Assert.Contains("cluster 2", inspector.DescribeSector(start + bpb.FirstDataSector));
    }

    [Fact]
    public void ADirectoryEntryAndItsClusterChainAreReadBackAsWritten()
    {
        using DiskImage image = Formatted();
        var inspector = new DiskInspector(image);
        long start = inspector.ReadPartitions()[0].FirstSector;
        DiskInspector.BootParameters bpb = inspector.ReadBootParameters(start)!.Value;

        // Three clusters chained by hand: 2 -> 5 -> 9 -> end. A chain and not
        // a single link, and not consecutive either, so an off-by-one or an
        // "assume contiguous" in the FAT walk would show up at once.
        WriteFat16(inspector, start, bpb, cluster: 2, next: 5);
        WriteFat16(inspector, start, bpb, cluster: 5, next: 9);
        WriteFat16(inspector, start, bpb, cluster: 9, next: 0xFFFF);

        byte[] root = inspector.ReadSector(start + bpb.RootDirectorySector);
        "CHAIN   BIN"u8.CopyTo(root.AsSpan(0, 11));
        root[11] = 0x20;                                        // archive
        BitConverter.GetBytes((ushort)2).CopyTo(root, 26);      // first cluster
        BitConverter.GetBytes(1234u).CopyTo(root, 28);          // size
        Assert.True(inspector.WriteSector(start + bpb.RootDirectorySector, root));

        DiskInspector.DirectoryEntry entry =
            Assert.Single(inspector.ReadDirectory(start), e => e.Name == "CHAIN.BIN");
        Assert.Equal(1234, entry.Size);
        Assert.Equal(2, entry.FirstCluster);
        Assert.False(entry.IsDirectory);

        Assert.Equal(new long[] { 2, 5, 9 }, inspector.ReadClusterChain(start, entry.FirstCluster));

        // And cluster 5 really is where the arithmetic says it is.
        long sector = inspector.ClusterToSector(start, 5);
        Assert.Equal(start + bpb.FirstDataSector + 3 * bpb.SectorsPerCluster, sector);
    }

    /// <summary>Writes one FAT16 entry, the way FORMAT and DOS do.</summary>
    private static void WriteFat16(DiskInspector inspector, long start,
                                   DiskInspector.BootParameters bpb, long cluster, ushort next)
    {
        long offset = cluster * 2;
        long sectorNumber = start + bpb.FirstFatSector + offset / bpb.BytesPerSector;
        byte[] sector = inspector.ReadSector(sectorNumber);
        BitConverter.GetBytes(next).CopyTo(sector, (int)(offset % bpb.BytesPerSector));
        inspector.WriteSector(sectorNumber, sector);
    }

    [Fact]
    public void ASectorWrittenThroughTheInspectorComesBackChanged()
    {
        using DiskImage image = Formatted();
        var inspector = new DiskInspector(image);

        byte[] sector = inspector.ReadSector(100);
        sector[0] = 0xC0;
        sector[511] = 0xDE;
        Assert.True(inspector.WriteSector(100, sector));

        byte[] again = inspector.ReadSector(100);
        Assert.Equal(0xC0, again[0]);
        Assert.Equal(0xDE, again[511]);
    }

    [Fact]
    public void ASectorPastTheEndReadsAsZeroesRatherThanThrowing()
    {
        using DiskImage image = Formatted();
        var inspector = new DiskInspector(image);

        byte[] sector = inspector.ReadSector(inspector.TotalSectors + 10);

        Assert.Equal(512, sector.Length);
        Assert.All(sector, b => Assert.Equal(0, b));
        Assert.False(inspector.WriteSector(inspector.TotalSectors + 10, sector));
    }
}
