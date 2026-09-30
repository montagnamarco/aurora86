using System.Text;
using Aurora86.Core.Firmware;
using Aurora86.Core.Firmware.Fpu;
using Aurora86.Core.Devices.Storage;
using Aurora86.Core.Diagnostics;
namespace Aurora86.Tests;

/// <summary>
/// Tests for ISO 9660 image reading.
///
/// The format is built around fixed offsets so that a machine with very
/// little memory can read it without parsing anything, and these tests pin
/// down the offsets that matter.
/// </summary>
public class IsoImageTests
{
    /// <summary>
    /// Builds a minimal but structurally valid disc: the 16 reserved system
    /// sectors followed by a Primary Volume Descriptor.
    /// </summary>
    private static MemoryStream BuildDisc(string label, int extraSectors = 4)
    {
        int sectors = IsoImage.PrimaryVolumeDescriptorSector + 1 + extraSectors;
        var bytes = new byte[sectors * IsoImage.SectorSize];

        int pvd = IsoImage.PrimaryVolumeDescriptorSector * IsoImage.SectorSize;

        // Type 1 marks the primary descriptor; "CD001" is the signature a
        // reader checks to confirm this is ISO 9660 at all.
        bytes[pvd] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(bytes, pvd + 1);

        // The volume identifier is 32 characters at offset 40, padded with
        // spaces rather than terminated -- fixed width, no parsing needed.
        byte[] padded = Encoding.ASCII.GetBytes(label.PadRight(32));
        padded.AsSpan(0, 32).CopyTo(bytes.AsSpan(pvd + 40));

        var stream = new MemoryStream();
        stream.Write(bytes);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void ReadsTheVolumeLabelFromThePrimaryDescriptor()
    {
        using var disc = new IsoImage(BuildDisc("AURORA86"));
        Assert.Equal("AURORA86", disc.VolumeLabel);
    }

    [Fact]
    public void ReportsTheNumberOfBlocks()
    {
        using var disc = new IsoImage(BuildDisc("TEST", extraSectors: 9));
        Assert.Equal(26, disc.TotalSectors);   // 16 system + 1 descriptor + 9
    }

    [Fact]
    public void ADiscWithoutASignatureHasNoLabel()
    {
        // Random data where the descriptor should be: a reader must notice
        // the missing "CD001" rather than showing 32 bytes of garbage.
        var bytes = new byte[20 * IsoImage.SectorSize];
        Array.Fill(bytes, (byte)0xAB);

        using var disc = new IsoImage(new MemoryStream(bytes));
        Assert.Equal("", disc.VolumeLabel);
    }

    [Fact]
    public void SectorsAreTwoKilobytes()
    {
        // Unlike a disk's 512-byte sector. Getting this wrong scales every
        // block address by four.
        Assert.Equal(2048, IsoImage.SectorSize);
    }

    [Fact]
    public void ReadsBackWhatWasWrittenToASector()
    {
        MemoryStream stream = BuildDisc("DATA");
        stream.Position = 18 * IsoImage.SectorSize;
        stream.WriteByte(0x42);
        stream.Position = 18 * IsoImage.SectorSize + 2047;
        stream.WriteByte(0x99);

        using var disc = new IsoImage(stream);
        var sector = new byte[IsoImage.SectorSize];
        disc.ReadSector(18, sector);

        Assert.Equal(0x42, sector[0]);
        Assert.Equal(0x99, sector[2047]);
    }

    [Fact]
    public void ReadingPastTheEndYieldsZeroes()
    {
        // A real drive would report an error; returning zeroes keeps the
        // caller in charge of deciding what that means.
        using var disc = new IsoImage(BuildDisc("SHORT"));

        var sector = new byte[IsoImage.SectorSize];
        Array.Fill(sector, (byte)0xFF);
        disc.ReadSector(disc.TotalSectors + 10, sector);

        Assert.All(sector, value => Assert.Equal(0, value));
    }

    [Fact]
    public void ReadsSeveralConsecutiveBlocks()
    {
        MemoryStream stream = BuildDisc("MULTI", extraSectors: 6);
        foreach ((int sector, byte value) in new[] { (18, (byte)0x11), (19, (byte)0x22), (20, (byte)0x33) })
        {
            stream.Position = sector * IsoImage.SectorSize;
            stream.WriteByte(value);
        }

        using var disc = new IsoImage(stream);
        var buffer = new byte[3 * IsoImage.SectorSize];
        disc.ReadSectors(18, 3, buffer);

        Assert.Equal(0x11, buffer[0]);
        Assert.Equal(0x22, buffer[IsoImage.SectorSize]);
        Assert.Equal(0x33, buffer[2 * IsoImage.SectorSize]);
    }

    [Fact]
    public void NegativeBlockNumbersAreRefusedQuietly()
    {
        using var disc = new IsoImage(BuildDisc("GUARD"));

        var sector = new byte[IsoImage.SectorSize];
        Array.Fill(sector, (byte)0x7E);
        disc.ReadSector(-1, sector);

        Assert.All(sector, value => Assert.Equal(0, value));
    }
}
