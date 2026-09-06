using System.Text;
using Aurora386.Core.Firmware;
using Aurora386.Core.Firmware.Fpu;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Diagnostics;
namespace Aurora386.Tests;

/// <summary>
/// Tests for walking ISO 9660 directories.
///
/// The format is all fixed offsets and both-endian numbers, so every one of
/// these bugs is silent: read the wrong half of a both-endian field and you
/// get a plausible-looking block number pointing at the wrong part of the
/// disc, which surfaces as a file full of garbage rather than as an error.
/// </summary>
public class IsoDirectoryTests
{
    /// <summary>
    /// Offset of the root directory record inside the Primary Volume
    /// Descriptor, spelled out here rather than read from the code under
    /// test. The whole value of these tests is that they say what ISO 9660
    /// requires, independently of what the implementation believes.
    /// </summary>
    private const int RootDirectoryRecordOffset = 156;

    /// <summary>
    /// A disc builder that produces a real ISO 9660 structure: a Primary
    /// Volume Descriptor whose embedded root directory record points at a
    /// directory of the entries asked for.
    /// </summary>
    private sealed class DiscBuilder
    {
        private readonly List<(string name, bool isDirectory, int sector, int length)> _root = [];
        private int _nextDataSector = 20;

        public int RootSector => 18;

        public DiscBuilder AddFile(string name, int length)
        {
            _root.Add((name, false, _nextDataSector, length));
            _nextDataSector += Math.Max(1, (length + 2047) / 2048);
            return this;
        }

        public DiscBuilder AddDirectory(string name, int sector, int length)
        {
            _root.Add((name, true, sector, length));
            return this;
        }

        public byte[] Build(int totalSectors = 40)
        {
            var image = new byte[totalSectors * IsoImage.SectorSize];

            int pvd = IsoImage.PrimaryVolumeDescriptorSector * IsoImage.SectorSize;
            image[pvd] = 1;
            Encoding.ASCII.GetBytes("CD001").CopyTo(image, pvd + 1);
            Encoding.ASCII.GetBytes("TESTDISC".PadRight(32)).CopyTo(image, pvd + 40);

            // The root directory record embedded at offset 156, pointing at
            // the directory we are about to write. The 156 is written out as
            // a number rather than taken from the class under test: using the
            // implementation's own constant to build the fixture would make
            // this test pass for any offset at all, which is precisely how a
            // test comes to encode a bug instead of catching one.
            WriteDirectoryRecord(image, pvd + RootDirectoryRecordOffset,
                                 nameBytes: [0x00], isDirectory: true,
                                 sector: RootSector, length: IsoImage.SectorSize);

            WriteDirectory(image, RootSector, _root);
            return image;
        }

        public static void WriteDirectory(byte[] image, int sector,
            List<(string name, bool isDirectory, int sector, int length)> entries)
        {
            int offset = sector * IsoImage.SectorSize;

            // Every directory opens with its own record and its parent's,
            // named 0x00 and 0x01. A reader that does not skip them shows the
            // user two entries with unprintable names.
            offset += WriteDirectoryRecord(image, offset, [0x00], true, sector, IsoImage.SectorSize);
            offset += WriteDirectoryRecord(image, offset, [0x01], true, sector, IsoImage.SectorSize);

            foreach ((string name, bool isDirectory, int entrySector, int length) in entries)
            {
                // Files carry the ";1" version suffix the standard demands.
                string identifier = isDirectory ? name : name + ";1";
                offset += WriteDirectoryRecord(image, offset,
                                               Encoding.ASCII.GetBytes(identifier),
                                               isDirectory, entrySector, length);
            }
        }

        private static int WriteDirectoryRecord(byte[] image, int offset, byte[] nameBytes,
                                                bool isDirectory, int sector, int length)
        {
            int recordLength = 33 + nameBytes.Length;
            if (recordLength % 2 != 0) recordLength++;   // records are padded to even lengths

            image[offset] = (byte)recordLength;
            WriteBothEndian(image, offset + 2, sector);
            WriteBothEndian(image, offset + 10, length);

            // The seven-byte timestamp: years since 1900, then month and day.
            image[offset + 18] = 95;   // 1995
            image[offset + 19] = 6;
            image[offset + 20] = 15;
            image[offset + 21] = 10;
            image[offset + 22] = 30;
            image[offset + 23] = 0;

            image[offset + 25] = (byte)(isDirectory ? 0x02 : 0x00);
            image[offset + 32] = (byte)nameBytes.Length;
            nameBytes.CopyTo(image, offset + 33);

            return recordLength;
        }

        /// <summary>
        /// Writes one of ISO 9660's both-endian numbers: the same value twice,
        /// little-endian then big-endian. A reader that takes the wrong four
        /// bytes gets a byte-swapped block number, which for small values
        /// looks like a huge one and reads off the end of the disc.
        /// </summary>
        private static void WriteBothEndian(byte[] image, int offset, int value)
        {
            image[offset + 0] = (byte)value;
            image[offset + 1] = (byte)(value >> 8);
            image[offset + 2] = (byte)(value >> 16);
            image[offset + 3] = (byte)(value >> 24);

            image[offset + 4] = (byte)(value >> 24);
            image[offset + 5] = (byte)(value >> 16);
            image[offset + 6] = (byte)(value >> 8);
            image[offset + 7] = (byte)value;
        }
    }

    [Fact]
    public void TheRootDirectoryIsFoundThroughThePrimaryVolumeDescriptor()
    {
        // The root's location is not stored as a plain number: it is a
        // complete directory record embedded at offset 156 of the descriptor.
        // Reading it from anywhere else finds nothing at all.
        byte[] image = new DiscBuilder().AddFile("README.TXT", 100).Build();
        using var disc = new IsoImage(new MemoryStream(image));

        Assert.Equal(18, disc.RootDirectorySector);
        Assert.Equal(IsoImage.SectorSize, disc.RootDirectoryLength);
    }

    [Fact]
    public void TheVersionSuffixIsStrippedFromFileNames()
    {
        // Every file on a pressed disc is named "SOMETHING;1". Showing that
        // to the user, or worse trying to open it under that name, is the
        // most common ISO 9660 bug there is.
        byte[] image = new DiscBuilder().AddFile("SETUP.EXE", 4096).Build();
        using var disc = new IsoImage(new MemoryStream(image));

        IsoDirectoryEntry entry = disc.ReadRootDirectory().Single(e => !e.IsSpecial);
        Assert.Equal("SETUP.EXE", entry.Name);
    }

    [Fact]
    public void TheSelfAndParentRecordsAreMarkedSpecial()
    {
        // They are named with a single byte of value 0 and 1, which is not
        // text. A reader that treats them as names produces two entries whose
        // names are control characters.
        byte[] image = new DiscBuilder().AddFile("DATA.BIN", 10).Build();
        using var disc = new IsoImage(new MemoryStream(image));

        List<IsoDirectoryEntry> entries = disc.ReadRootDirectory().ToList();

        Assert.Equal(3, entries.Count);
        Assert.Equal([".", ".."], entries.Where(e => e.IsSpecial).Select(e => e.Name));
        Assert.All(entries.Where(e => e.IsSpecial), e => Assert.True(e.IsDirectory));
    }

    [Fact]
    public void ADirectoryEntryIsDistinguishedFromAFileByItsFlagsByte()
    {
        // Bit 1 of the flags at offset 25. Get the bit number wrong -- bit 0
        // is "hidden" -- and every file on the disc becomes a directory.
        byte[] image = new DiscBuilder()
            .AddFile("FILE.TXT", 50)
            .AddDirectory("GAMES", 25, IsoImage.SectorSize)
            .Build();
        using var disc = new IsoImage(new MemoryStream(image));

        List<IsoDirectoryEntry> entries = disc.ReadRootDirectory()
                                              .Where(e => !e.IsSpecial).ToList();

        Assert.False(entries.Single(e => e.Name == "FILE.TXT").IsDirectory);
        Assert.True(entries.Single(e => e.Name == "GAMES").IsDirectory);
    }

    [Fact]
    public void FileLengthsComeFromTheLittleEndianHalfOfTheBothEndianField()
    {
        // The length is stored twice, in both byte orders. Reading the
        // big-endian copy gives a byte-swapped size: 0x1234 read as 0x3412.
        // Since the value is plausible either way, nothing errors -- files
        // simply come out the wrong length.
        byte[] image = new DiscBuilder().AddFile("BIG.DAT", 0x1234).Build();
        using var disc = new IsoImage(new MemoryStream(image));

        IsoDirectoryEntry entry = disc.ReadRootDirectory().Single(e => !e.IsSpecial);
        Assert.Equal(0x1234, entry.Length);
    }

    [Fact]
    public void ARecordWithZeroLengthEndsTheSectorRatherThanTheDirectory()
    {
        // ISO 9660 forbids a record from straddling a sector boundary, so the
        // tail of each 2048-byte block is zero padding. A reader that treats
        // the first zero as end-of-directory sees only the entries that
        // happened to fit in the first block -- which on a real disc means a
        // truncated listing that looks entirely plausible.
        var image = new byte[40 * IsoImage.SectorSize];

        int pvd = IsoImage.PrimaryVolumeDescriptorSector * IsoImage.SectorSize;
        image[pvd] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(image, pvd + 1);

        // A root directory two sectors long, with one entry in each.
        WriteRootRecord(image, pvd + RootDirectoryRecordOffset,
                        sector: 18, length: 2 * IsoImage.SectorSize);

        WriteSimpleFileRecord(image, 18 * IsoImage.SectorSize, "FIRST.TXT", 10);
        WriteSimpleFileRecord(image, 19 * IsoImage.SectorSize, "SECOND.TXT", 20);

        using var disc = new IsoImage(new MemoryStream(image));
        List<string> names = disc.ReadRootDirectory().Select(e => e.Name).ToList();

        Assert.Equal(["FIRST.TXT", "SECOND.TXT"], names);
    }

    [Fact]
    public void ATrailingDotOnAnExtensionlessNameIsRemoved()
    {
        // "README." is how ISO 9660 writes a name with an empty extension,
        // because the standard requires the separator to be present. Left in
        // place, DOS would see a filename ending in a dot -- which it cannot
        // represent at all.
        var image = new byte[40 * IsoImage.SectorSize];
        int pvd = IsoImage.PrimaryVolumeDescriptorSector * IsoImage.SectorSize;
        image[pvd] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(image, pvd + 1);
        WriteRootRecord(image, pvd + RootDirectoryRecordOffset, 18, IsoImage.SectorSize);

        WriteSimpleFileRecord(image, 18 * IsoImage.SectorSize, "README.", 5, addVersion: false);

        using var disc = new IsoImage(new MemoryStream(image));
        Assert.Equal("README", disc.ReadRootDirectory().Single().Name);
    }

    [Fact]
    public void ADiscWithNoValidDescriptorReportsNoRootDirectory()
    {
        // Zero means "there is no filesystem here", which a caller has to be
        // able to tell apart from "the root is empty" -- otherwise a floppy
        // image handed to the ISO reader by mistake presents as a blank disc
        // instead of as an error.
        var image = new byte[30 * IsoImage.SectorSize];
        Array.Fill(image, (byte)0x5A);

        using var disc = new IsoImage(new MemoryStream(image));
        Assert.Equal(0, disc.RootDirectorySector);
        Assert.Empty(disc.ReadRootDirectory());
    }

    [Fact]
    public void TheRecordedTimestampIsReadAsYearsSince1900()
    {
        // The year byte is an offset from 1900, not a two-digit year and not
        // an offset from 1980 as FAT uses. Reading it as either produces a
        // date decades out.
        byte[] image = new DiscBuilder().AddFile("DATED.TXT", 1).Build();
        using var disc = new IsoImage(new MemoryStream(image));

        IsoDirectoryEntry entry = disc.ReadRootDirectory().Single(e => !e.IsSpecial);
        Assert.Equal(new DateTime(1995, 6, 15, 10, 30, 0), entry.RecordedAt);
    }

    private static void WriteRootRecord(byte[] image, int offset, int sector, int length)
    {
        image[offset] = 34;
        WriteLittleEndianBoth(image, offset + 2, sector);
        WriteLittleEndianBoth(image, offset + 10, length);
        image[offset + 25] = 0x02;
        image[offset + 32] = 1;
        image[offset + 33] = 0x00;
    }

    private static void WriteSimpleFileRecord(byte[] image, int offset, string name,
                                              int length, bool addVersion = true)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(addVersion ? name + ";1" : name);
        int recordLength = 33 + nameBytes.Length;
        if (recordLength % 2 != 0) recordLength++;

        image[offset] = (byte)recordLength;
        WriteLittleEndianBoth(image, offset + 2, 25);
        WriteLittleEndianBoth(image, offset + 10, length);
        image[offset + 18] = 95;
        image[offset + 19] = 1;
        image[offset + 20] = 1;
        image[offset + 32] = (byte)nameBytes.Length;
        nameBytes.CopyTo(image, offset + 33);
    }

    private static void WriteLittleEndianBoth(byte[] image, int offset, int value)
    {
        image[offset + 0] = (byte)value;
        image[offset + 1] = (byte)(value >> 8);
        image[offset + 2] = (byte)(value >> 16);
        image[offset + 3] = (byte)(value >> 24);
        image[offset + 4] = (byte)(value >> 24);
        image[offset + 5] = (byte)(value >> 16);
        image[offset + 6] = (byte)(value >> 8);
        image[offset + 7] = (byte)value;
    }
}
