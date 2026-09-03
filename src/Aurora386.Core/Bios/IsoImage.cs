using System.Text;

namespace Aurora386.Core.Bios;

/// <summary>
/// A CD-ROM image in the ISO 9660 format.
///
/// A CD is a very different animal from a disk. Its sectors are 2048 bytes
/// rather than 512, it is addressed by a linear block number rather than by
/// cylinder, head and sector, and it is read-only by nature. None of the
/// BIOS disk services apply: DOS reaches a CD through MSCDEX, which talks to
/// a device driver, which talks to the drive. So this class deliberately does
/// not pretend to be a <see cref="DiskImage"/> -- the abstraction would leak
/// immediately.
///
/// ISO 9660 itself is simple by design, because it had to be readable by
/// machines with very little memory. Sector 16 holds the Primary Volume
/// Descriptor, which names the disc and points at the root directory; every
/// directory is a flat list of fixed-layout records. That is all a boot
/// loader or a DOS driver needs.
/// </summary>
public sealed class IsoImage : IDisposable
{
    /// <summary>
    /// CD sectors hold 2048 bytes of user data. (The physical sector is
    /// larger -- 2352 bytes -- but the extra space carries synchronisation
    /// and error correction that the drive handles and never exposes.)
    /// </summary>
    public const int SectorSize = 2048;

    /// <summary>
    /// The first 16 sectors are the system area, reserved for boot code and
    /// left empty on a data disc. The volume descriptors begin right after.
    /// </summary>
    public const int PrimaryVolumeDescriptorSector = 16;

    private readonly Stream _stream;
    private readonly bool _ownsStream;

    /// <summary>Total number of 2048-byte blocks on the disc.</summary>
    public long TotalSectors { get; }

    /// <summary>Volume label from the Primary Volume Descriptor, trimmed.</summary>
    public string VolumeLabel { get; } = "";

    /// <summary>
    /// Block number where the root directory's records begin, taken from the
    /// Primary Volume Descriptor. Zero when the descriptor is missing or
    /// malformed, which is how a caller tells "no filesystem here" apart from
    /// "an empty root".
    /// </summary>
    public long RootDirectorySector { get; }

    /// <summary>Length of the root directory in bytes.</summary>
    public long RootDirectoryLength { get; }

    /// <summary>Path of the backing file, for the user interface to display.</summary>
    public string? SourcePath { get; init; }

    public IsoImage(Stream stream, bool ownsStream = true)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        TotalSectors = stream.Length / SectorSize;

        VolumeLabel = ReadVolumeLabel();
        (RootDirectorySector, RootDirectoryLength) = ReadRootDirectoryExtent();
    }

    public static IsoImage Open(string path)
    {
        // A disc is physically read-only, and opening the file that way also
        // means several emulators can share one image.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                    FileShare.ReadWrite);
        return new IsoImage(stream) { SourcePath = path };
    }

    /// <summary>
    /// Reads one 2048-byte block. Blocks past the end of the disc read as
    /// zeroes rather than throwing: a drive would report an error, and the
    /// caller decides how to surface that.
    /// </summary>
    public void ReadSector(long lba, Span<byte> destination)
    {
        destination.Clear();
        if (lba < 0 || lba >= TotalSectors) return;

        _stream.Seek(lba * SectorSize, SeekOrigin.Begin);

        int wanted = Math.Min(destination.Length, SectorSize);
        _stream.ReadExactly(destination[..wanted]);
    }

    /// <summary>
    /// Reads several consecutive blocks, which is how a driver actually asks
    /// for data: seeking a CD is slow, so requests come in runs.
    /// </summary>
    public void ReadSectors(long lba, int count, Span<byte> destination)
    {
        for (int i = 0; i < count; i++)
        {
            int offset = i * SectorSize;
            if (offset >= destination.Length) break;

            int length = Math.Min(SectorSize, destination.Length - offset);
            ReadSector(lba + i, destination.Slice(offset, length));
        }
    }

    /// <summary>
    /// Pulls the volume identifier out of the Primary Volume Descriptor.
    ///
    /// The descriptor starts with a type byte (1 for primary) and the string
    /// "CD001", which is how a reader confirms it is looking at ISO 9660 at
    /// all. The volume identifier sits at offset 40 and is 32 characters,
    /// space-padded -- fixed width, because a machine with 640 KB of memory
    /// should not have to parse anything.
    /// </summary>
    private string ReadVolumeLabel()
    {
        if (TotalSectors <= PrimaryVolumeDescriptorSector) return "";

        Span<byte> sector = stackalloc byte[SectorSize];
        ReadSector(PrimaryVolumeDescriptorSector, sector);

        if (!IsPrimaryDescriptor(sector)) return "";

        return Encoding.ASCII.GetString(sector.Slice(40, 32)).TrimEnd(' ', '\0');
    }

    /// <summary>
    /// Pulls the root directory's location out of the Primary Volume
    /// Descriptor.
    ///
    /// At offset 156 the descriptor embeds a complete directory record --
    /// the very same 34-byte structure that every directory is built out of,
    /// simply written inline instead of being pointed at. That is the trick
    /// which makes the format bootstrappable: a reader that knows how to walk
    /// one directory record already knows how to find the root, and needs no
    /// second parser for the top of the tree.
    /// </summary>
    private (long sector, long length) ReadRootDirectoryExtent()
    {
        if (TotalSectors <= PrimaryVolumeDescriptorSector) return (0, 0);

        Span<byte> sector = stackalloc byte[SectorSize];
        ReadSector(PrimaryVolumeDescriptorSector, sector);

        if (!IsPrimaryDescriptor(sector)) return (0, 0);

        ReadOnlySpan<byte> record = sector.Slice(RootDirectoryRecordOffset, 34);
        long extent = ReadBothEndianDword(record, 2);
        long length = ReadBothEndianDword(record, 10);

        // A root that claims to start beyond the end of the disc is a
        // truncated or corrupt image, not a filesystem.
        if (extent <= 0 || extent >= TotalSectors) return (0, 0);

        return (extent, length);
    }

    /// <summary>
    /// Offset of the root directory record inside the Primary Volume
    /// Descriptor. Fixed by the standard, like everything else in ISO 9660.
    /// </summary>
    public const int RootDirectoryRecordOffset = 156;

    private static bool IsPrimaryDescriptor(ReadOnlySpan<byte> sector)
        => sector[0] == 1
        && sector[1] == (byte)'C' && sector[2] == (byte)'D'
        && sector[3] == (byte)'0' && sector[4] == (byte)'0' && sector[5] == (byte)'1';

    /// <summary>
    /// Reads one of ISO 9660's "both-endian" numbers: the same 32-bit value
    /// stored twice, little-endian first and then big-endian.
    ///
    /// The standard was written when it was not yet settled which byte order
    /// would win, so rather than choose it stored both and let each machine
    /// read the half that suited it. We take the little-endian copy, because
    /// that is the one an x86 would have read.
    /// </summary>
    private static long ReadBothEndianDword(ReadOnlySpan<byte> data, int offset)
        => (uint)(data[offset]
                | (data[offset + 1] << 8)
                | (data[offset + 2] << 16)
                | (data[offset + 3] << 24));

    /// <summary>
    /// Walks the directory whose records start at <paramref name="sector"/>
    /// and span <paramref name="length"/> bytes, returning one entry per
    /// record.
    ///
    /// A directory is a plain list of variable-length records, each beginning
    /// with its own length. Two rules make the walk less obvious than it
    /// sounds. First, a record is never allowed to straddle a sector
    /// boundary: when the remaining space in a 2048-byte block cannot hold
    /// the next record the block is padded with zeroes, so a zero length byte
    /// means "skip to the next sector", not "end of directory". Second, the
    /// first two records are always the directory itself and its parent,
    /// distinguished not by name but by a single-byte name of 0x00 and 0x01
    /// respectively -- ISO 9660 has no "." or ".." strings, DOS's own dot
    /// entries are supplied by whatever presents the disc.
    /// </summary>
    public IEnumerable<IsoDirectoryEntry> ReadDirectory(long sector, long length)
    {
        if (sector <= 0 || length <= 0) yield break;

        int sectorCount = (int)((length + SectorSize - 1) / SectorSize);
        var block = new byte[SectorSize];

        for (int index = 0; index < sectorCount; index++)
        {
            ReadSector(sector + index, block);

            int offset = 0;
            while (offset < SectorSize)
            {
                byte recordLength = block[offset];

                // Zero length: the rest of this sector is padding.
                if (recordLength == 0) break;

                // A record that would run off the end of the block is a sign
                // of a corrupt image; stop rather than read whatever follows.
                if (offset + recordLength > SectorSize) break;
                if (recordLength < 34) break;

                IsoDirectoryEntry? entry = ParseRecord(block.AsSpan(offset, recordLength));
                if (entry is not null) yield return entry;

                offset += recordLength;
            }
        }
    }

    /// <summary>Walks the root directory of the disc.</summary>
    public IEnumerable<IsoDirectoryEntry> ReadRootDirectory()
        => ReadDirectory(RootDirectorySector, RootDirectoryLength);

    private static IsoDirectoryEntry? ParseRecord(ReadOnlySpan<byte> record)
    {
        long extent = ReadBothEndianDword(record, 2);
        long dataLength = ReadBothEndianDword(record, 10);
        byte flags = record[25];
        int nameLength = record[32];

        if (nameLength == 0 || 33 + nameLength > record.Length) return null;

        ReadOnlySpan<byte> rawName = record.Slice(33, nameLength);

        // The self and parent records carry a one-byte name that is not text
        // at all: 0x00 for "this directory", 0x01 for "the parent". They are
        // structure, not files, and the caller should never see them as
        // entries with strange names.
        if (nameLength == 1 && (rawName[0] == 0x00 || rawName[0] == 0x01))
        {
            return new IsoDirectoryEntry
            {
                Name = rawName[0] == 0x00 ? "." : "..",
                IsDirectory = true,
                IsSpecial = true,
                Sector = extent,
                Length = dataLength,
                RecordedAt = ReadTimestamp(record.Slice(18, 7)),
            };
        }

        return new IsoDirectoryEntry
        {
            Name = CleanName(rawName),
            IsDirectory = (flags & DirectoryFlag) != 0,
            IsHidden = (flags & HiddenFlag) != 0,
            Sector = extent,
            Length = dataLength,
            RecordedAt = ReadTimestamp(record.Slice(18, 7)),
        };
    }

    /// <summary>Bit 1 of the file flags marks a directory.</summary>
    private const byte DirectoryFlag = 0x02;

    /// <summary>Bit 0 marks an entry the presenter should not normally list.</summary>
    private const byte HiddenFlag = 0x01;

    /// <summary>
    /// Turns an ISO 9660 identifier into the name a user expects to see.
    ///
    /// Files carry a version suffix -- "README.TXT;1" -- which the standard
    /// requires and which nobody ever wanted. It exists because ISO 9660 was
    /// designed to describe archival media where several generations of the
    /// same file might sit side by side; in practice every disc ever pressed
    /// has version 1 and nothing else, and every operating system that reads
    /// discs hides the suffix. A trailing dot on an extensionless name is the
    /// other artefact of the same rule, since "README." is how the format
    /// writes a name with an empty extension.
    /// </summary>
    private static string CleanName(ReadOnlySpan<byte> rawName)
    {
        string name = Encoding.ASCII.GetString(rawName);

        int semicolon = name.IndexOf(';');
        if (semicolon >= 0) name = name[..semicolon];

        if (name.EndsWith('.')) name = name[..^1];

        return name;
    }

    /// <summary>
    /// Reads a directory record's seven-byte timestamp: years since 1900,
    /// month, day, hour, minute, second, then the offset from GMT in quarter
    /// hours. Returns null when the record is blank, which discs mastered by
    /// sloppy tools do produce.
    /// </summary>
    private static DateTime? ReadTimestamp(ReadOnlySpan<byte> stamp)
    {
        int year = 1900 + stamp[0];
        int month = stamp[1];
        int day = stamp[2];

        if (month is < 1 or > 12 || day is < 1 or > 31) return null;
        if (year is < 1900 or > 9999) return null;

        int hour = Math.Min(stamp[3], (byte)23);
        int minute = Math.Min(stamp[4], (byte)59);
        int second = Math.Min(stamp[5], (byte)59);

        try
        {
            return new DateTime(year, month, day, hour, minute, second);
        }
        catch (ArgumentOutOfRangeException)
        {
            // February the 30th and its friends: discs do contain them.
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsStream) _stream.Dispose();
    }
}

/// <summary>
/// One entry from an ISO 9660 directory, with the format's peculiarities
/// already smoothed away: the version suffix stripped from the name, and the
/// self and parent records flagged so a caller can skip them.
/// </summary>
public sealed class IsoDirectoryEntry
{
    public required string Name { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsHidden { get; init; }

    /// <summary>True for the "." and ".." records that open every directory.</summary>
    public bool IsSpecial { get; init; }

    /// <summary>First block of the file's or directory's data.</summary>
    public long Sector { get; init; }

    /// <summary>Length in bytes.</summary>
    public long Length { get; init; }

    public DateTime? RecordedAt { get; init; }
}
