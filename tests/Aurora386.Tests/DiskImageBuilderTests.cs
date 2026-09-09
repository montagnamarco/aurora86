using Aurora386.Core.Devices.Storage;

namespace Aurora386.Tests;

/// <summary>
/// Making a disk image, checked the way DOS checks one.
///
/// The interesting property is not that the bytes are where the specification
/// says. It is that a guest accepts the result: a partition table DOS will
/// not read, or a cluster count that puts the volume over the FAT16 boundary
/// without saying so, produces an image that looks perfectly well formed and
/// that MS-DOS reports as an invalid drive.
/// </summary>
public class DiskImageBuilderTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"aurora-{Guid.NewGuid():N}.img");

    [Fact]
    public void ABlankImageIsAWholeNumberOfCylinders()
    {
        string path = TempPath();
        try
        {
            // Deliberately not a round number: the size has to be rounded
            // down to a cylinder boundary, because a partial cylinder holds
            // sectors that no CHS address can name.
            DiskImageBuilder.CreateBlank(path, 20_000_000);

            long size = new FileInfo(path).Length;
            long cylinderBytes = DiskImageBuilder.SectorsPerTrack
                               * DiskImageBuilder.Heads
                               * DiskImageBuilder.SectorSize;

            Assert.Equal(0, size % cylinderBytes);
            Assert.True(size <= 20_000_000, "rounded down, never up");
            Assert.True(size > 20_000_000 - cylinderBytes, "and by less than one cylinder");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ADiskTooSmallForACylinderIsRefused()
    {
        string path = TempPath();
        try
        {
            Assert.Throws<ArgumentException>(() => DiskImageBuilder.CreateBlank(path, 1000));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void AFormattedImageHasAPartitionTableDosCanRead()
    {
        string path = TempPath();
        try
        {
            DiskImageBuilder.CreateFat16(path, 20_000_000, "PROVA");
            byte[] image = File.ReadAllBytes(path);

            Assert.Equal(0x55, image[0x1FE]);
            Assert.Equal(0xAA, image[0x1FF]);
            Assert.Equal(0x80, image[0x1BE]);           // bootable
            Assert.Equal(0x06, image[0x1BE + 4]);       // FAT16 above 32 MB

            uint firstSector = BitConverter.ToUInt32(image, 0x1BE + 8);
            uint sectors = BitConverter.ToUInt32(image, 0x1BE + 12);

            Assert.Equal((uint)DiskImageBuilder.SectorsPerTrack, firstSector);
            Assert.Equal(image.Length / DiskImageBuilder.SectorSize - firstSector, sectors);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheVolumeStaysWithinTheFat16ClusterLimit()
    {
        // A gigabyte with the smallest cluster would need far more than the
        // 65524 clusters a 16-bit table can address. The cluster size has to
        // grow to compensate, and a volume that does not do so is read as
        // something other than FAT16 -- or refused.
        string path = TempPath();
        try
        {
            DiskImageBuilder.CreateFat16(path, 1_000_000_000);
            byte[] image = File.ReadAllBytes(path);

            long boot = DiskImageBuilder.SectorsPerTrack * DiskImageBuilder.SectorSize;
            byte sectorsPerCluster = image[boot + 13];
            uint sectors = BitConverter.ToUInt32(image, (int)boot + 32);

            Assert.True(sectorsPerCluster > 0);
            Assert.True(sectors / sectorsPerCluster <= 65524,
                $"{sectors / sectorsPerCluster} clusters is more than FAT16 can address");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void BothCopiesOfTheTableAgree()
    {
        string path = TempPath();
        try
        {
            DiskImageBuilder.CreateFat16(path, 20_000_000);
            byte[] image = File.ReadAllBytes(path);

            long boot = DiskImageBuilder.SectorsPerTrack * DiskImageBuilder.SectorSize;
            ushort reserved = BitConverter.ToUInt16(image, (int)boot + 14);
            ushort sectorsPerFat = BitConverter.ToUInt16(image, (int)boot + 22);

            long first = boot + reserved * (long)DiskImageBuilder.SectorSize;
            long second = first + sectorsPerFat * (long)DiskImageBuilder.SectorSize;

            // The first four bytes are the whole of a freshly formatted
            // table: the media descriptor and an end-of-chain mark. DOS
            // compares the copies and calls a volume damaged when they differ.
            for (int i = 0; i < 4; i++)
                Assert.Equal(image[first + i], image[second + i]);

            Assert.Equal(0xF8, image[first]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheLabelIsWrittenWhereDosLooksForIt()
    {
        string path = TempPath();
        try
        {
            DiskImageBuilder.CreateFat16(path, 20_000_000, "SECONDO");
            byte[] image = File.ReadAllBytes(path);

            long boot = DiskImageBuilder.SectorsPerTrack * DiskImageBuilder.SectorSize;
            ushort reserved = BitConverter.ToUInt16(image, (int)boot + 14);
            byte fats = image[boot + 16];
            ushort sectorsPerFat = BitConverter.ToUInt16(image, (int)boot + 22);

            long root = boot + (reserved + fats * sectorsPerFat) * (long)DiskImageBuilder.SectorSize;

            string entry = System.Text.Encoding.ASCII.GetString(image, (int)root, 11);
            Assert.Equal("SECONDO    ", entry);
            Assert.Equal(0x08, image[root + 11]);       // the volume-label attribute

            // And in the boot sector too, which is where a program reading
            // the disk directly finds it.
            string inBoot = System.Text.Encoding.ASCII.GetString(image, (int)boot + 43, 11);
            Assert.Equal("SECONDO    ", inBoot);
        }
        finally { File.Delete(path); }
    }
}
