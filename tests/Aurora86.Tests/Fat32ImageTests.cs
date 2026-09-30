using Aurora86.Core.Devices.Storage;

namespace Aurora86.Tests;

/// <summary>
/// The FAT32 image builder.
///
/// These check the fields a reader actually uses to decide what a volume is,
/// because a builder that produced a plausible-looking image the guest could
/// not mount would send somebody hunting through the guest's driver for a
/// defect that was here.
/// </summary>
public class Fat32ImageTests
{
    private const int SectorSize = 512;

    private static byte[] BuildImage(long megabytes = 64)
    {
        string path = Path.GetTempFileName();

        try
        {
            DiskImageBuilder.CreateFat32(path, megabytes * 1024 * 1024, "TESTVOL");
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ushort ReadWord(byte[] image, long at)
        => (ushort)(image[at] | (image[at + 1] << 8));

    private static uint ReadDword(byte[] image, long at)
        => (uint)(image[at] | (image[at + 1] << 8)
                | (image[at + 2] << 16) | (image[at + 3] << 24));

    [Fact]
    public void ThePartitionTableNamesFat32WithLbaAddressing()
    {
        byte[] image = BuildImage();

        Assert.Equal(0x55, image[0x1FE]);
        Assert.Equal(0xAA, image[0x1FF]);

        // 0x0C is FAT32 addressed by LBA. 0x0B is the same file system
        // through cylinder, head and sector, and choosing it would ask every
        // reader to go through a geometry that stopped meaning anything.
        Assert.Equal(0x0C, image[0x1BE + 4]);

        Assert.Equal(63u, ReadDword(image, 0x1BE + 8));
    }

    [Fact]
    public void TheTwoZeroesThatIdentifyFat32ArePresent()
    {
        // THE CHECK THAT MATTERS MOST. The root entry count and the 16-bit
        // table size are zero only on FAT32, and the specification says
        // explicitly that this -- not the "FAT32" string -- is how a reader
        // decides. A volume with either non-zero is read as FAT16 by
        // everything, whatever else it says.
        byte[] image = BuildImage();
        long boot = 63 * SectorSize;

        Assert.Equal(0, ReadWord(image, boot + 17));
        Assert.Equal(0, ReadWord(image, boot + 22));

        // And the 32-bit fields that replace them are not zero.
        Assert.NotEqual(0u, ReadDword(image, boot + 36));
        Assert.NotEqual(0u, ReadDword(image, boot + 32));
    }

    [Fact]
    public void TheGeometryFieldsAreSelfConsistent()
    {
        byte[] image = BuildImage();
        long boot = 63 * SectorSize;

        Assert.Equal(SectorSize, ReadWord(image, boot + 11));

        int sectorsPerCluster = image[boot + 13];
        Assert.True(sectorsPerCluster > 0);
        Assert.Equal(0, sectorsPerCluster & (sectorsPerCluster - 1));

        Assert.True(ReadWord(image, boot + 14) > 0);    // reserved sectors
        Assert.True(image[boot + 16] > 0);              // number of tables

        // The data must begin inside the volume. A table sized wrongly puts
        // the first data sector past the end, and every read then fails.
        uint reserved = ReadWord(image, boot + 14);
        uint tables = image[boot + 16];
        uint tableSectors = ReadDword(image, boot + 36);
        uint total = ReadDword(image, boot + 32);

        uint firstData = reserved + tables * tableSectors;

        Assert.True(firstData < total,
                    $"data starts at {firstData} in a volume of {total} sectors");
    }

    [Fact]
    public void ThereAreEnoughClustersToBeFat32()
    {
        // Fewer than 65525 clusters makes a volume FAT16 whatever its fields
        // say, because that is how every reader decides. A builder producing
        // too few would make an image the guest correctly refuses.
        byte[] image = BuildImage();
        long boot = 63 * SectorSize;

        uint reserved = ReadWord(image, boot + 14);
        uint tables = image[boot + 16];
        uint tableSectors = ReadDword(image, boot + 36);
        uint total = ReadDword(image, boot + 32);
        uint sectorsPerCluster = image[boot + 13];

        uint clusters = (total - reserved - tables * tableSectors) / sectorsPerCluster;

        Assert.True(clusters >= 65525,
                    $"{clusters} clusters is FAT16 territory, not FAT32");

        // And the table must be large enough to describe them all, or the
        // last clusters have no entry and a file reaching them walks off the
        // end of the table.
        uint describable = tableSectors * SectorSize / 4;

        Assert.True(describable >= clusters + 2,
                    $"a table of {describable} entries cannot describe {clusters} clusters");
    }

    [Fact]
    public void TheRootDirectoryIsAnEmptyChain()
    {
        byte[] image = BuildImage();
        long boot = 63 * SectorSize;

        uint rootCluster = ReadDword(image, boot + 44);
        Assert.Equal(2u, rootCluster);

        uint reserved = ReadWord(image, boot + 14);
        long table = boot + reserved * SectorSize;

        // Entries 0 and 1 are reserved and hold the media byte and an end
        // marker. Entry 2 is the root, and it ends immediately because the
        // directory is one cluster and empty.
        Assert.Equal(0x0FFFFFF8u, ReadDword(image, table));
        Assert.Equal(0x0FFFFFFFu, ReadDword(image, table + 4));
        Assert.Equal(0x0FFFFFFFu, ReadDword(image, table + 8));
    }

    [Fact]
    public void TheBackupBootSectorMatchesTheOriginal()
    {
        // FAT32 keeps a copy because losing the boot sector loses the volume,
        // and every repair tool looks there first. A copy that differed would
        // be worse than none: a repair would install the wrong one.
        byte[] image = BuildImage();
        long boot = 63 * SectorSize;
        long backup = boot + 6 * SectorSize;

        for (int i = 0; i < SectorSize; i++)
            Assert.Equal(image[boot + i], image[backup + i]);
    }

    [Fact]
    public void ATooSmallVolumeIsRefusedRatherThanBuiltWrong()
    {
        // Refusing is the honest answer. Building a volume with too few
        // clusters would produce an image that says FAT32 and that every
        // reader treats as FAT16.
        string path = Path.GetTempFileName();

        try
        {
            Assert.Throws<ArgumentException>(
                () => DiskImageBuilder.CreateFat32(path, 4 * 1024 * 1024));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
