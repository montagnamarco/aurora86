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

    /// <summary>Path of the backing file, for the user interface to display.</summary>
    public string? SourcePath { get; init; }

    public IsoImage(Stream stream, bool ownsStream = true)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        TotalSectors = stream.Length / SectorSize;

        VolumeLabel = ReadVolumeLabel();
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

        bool isPrimary = sector[0] == 1
                      && sector[1] == 'C' && sector[2] == 'D'
                      && sector[3] == '0' && sector[4] == '0' && sector[5] == '1';
        if (!isPrimary) return "";

        return Encoding.ASCII.GetString(sector.Slice(40, 32)).TrimEnd(' ', '\0');
    }

    public void Dispose()
    {
        if (_ownsStream) _stream.Dispose();
    }
}
