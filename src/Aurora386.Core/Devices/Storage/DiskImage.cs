namespace Aurora386.Core.Devices.Storage;

/// <summary>
/// A disk image, either floppy or fixed.
///
/// The BIOS addresses sectors in CHS geometry (cylinder, head, sector), a
/// leftover from how the mechanics of a real drive work: sector numbering
/// starts at 1, not 0, while heads and cylinders start at 0.
/// </summary>
public sealed class DiskImage : IDisposable
{
    public const int SectorSize = 512;

    private readonly Stream _stream;
    private readonly bool _ownsStream;

    public int Cylinders { get; }
    public int Heads { get; }
    public int SectorsPerTrack { get; }
    public bool IsHardDisk { get; }
    public bool ReadOnly { get; }

    /// <summary>BIOS drive number: 0x00 is the first floppy, 0x80 the first hard disk.</summary>
    public byte DriveNumber { get; }

    /// <summary>
    /// Path of the file backing this image, when there is one. Purely
    /// informational: the user interface shows it so you can tell which
    /// floppy is currently in the drive. In-memory images used by tests
    /// leave it null.
    /// </summary>
    public string? SourcePath { get; set; }

    public long TotalSectors => (long)Cylinders * Heads * SectorsPerTrack;

    public DiskImage(Stream stream, byte driveNumber, int cylinders, int heads,
                     int sectorsPerTrack, bool readOnly = false, bool ownsStream = true)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        DriveNumber = driveNumber;
        Cylinders = cylinders;
        Heads = heads;
        SectorsPerTrack = sectorsPerTrack;
        IsHardDisk = driveNumber >= 0x80;
        ReadOnly = readOnly || !stream.CanWrite;
    }

    /// <summary>
    /// Opens an image, inferring its geometry from the file size. Floppy
    /// formats are standardized, so the size identifies them unambiguously.
    /// </summary>
    public static DiskImage OpenFloppy(string path, byte driveNumber = 0x00, bool readOnly = false)
    {
        var info = new FileInfo(path);
        (int cylinders, int heads, int sectors) = info.Length switch
        {
            368_640 => (40, 2, 9),      // 360 KB
            737_280 => (80, 2, 9),      // 720 KB
            1_228_800 => (80, 2, 15),   // 1.2 MB
            1_474_560 => (80, 2, 18),   // 1.44 MB
            2_949_120 => (80, 2, 36),   // 2.88 MB
            _ => throw new InvalidDataException(
                $"Size {info.Length} does not match any known floppy format."),
        };

        FileStream stream = OpenShared(path, readOnly);
        return new DiskImage(stream, driveNumber, cylinders, heads, sectors, readOnly)
               { SourcePath = path };
    }

    /// <summary>
    /// Opens a hard disk image. Geometry cannot be inferred from the size
    /// alone, so we use the most common convention of the era (63 sectors per
    /// track, 16 heads) and derive the cylinder count from it.
    /// </summary>
    public static DiskImage OpenHardDisk(string path, byte driveNumber = 0x80, bool readOnly = false)
    {
        var info = new FileInfo(path);
        HardDiskImage.Geometry geometry = HardDiskImage.Geometry.ForSize(info.Length);

        if (geometry.Cylinders == 0)
            throw new InvalidDataException("The image is too small to be a hard disk.");

        FileStream stream = OpenShared(path, readOnly);
        return new DiskImage(stream, driveNumber, geometry.Cylinders, geometry.Heads,
                             geometry.SectorsPerTrack, readOnly) { SourcePath = path };
    }

    /// <summary>
    /// Creates a partitioned and formatted hard disk image, then opens it.
    /// This is what you need in order to install an operating system onto it.
    /// </summary>
    public static DiskImage CreateHardDisk(string path, long sizeBytes, byte driveNumber = 0x80)
    {
        HardDiskImage.Create(path, sizeBytes);
        return OpenHardDisk(path, driveNumber);
    }

    /// <summary>
    /// Opens an image while sharing it with other processes.
    ///
    /// An image opened read-only should not lock anyone else out: inspecting
    /// the same file with several tools at once is a common thing to do. Even
    /// in read-write mode we still allow other readers, so a sharing violation
    /// surfaces only when two processes genuinely want to write.
    /// </summary>
    private static FileStream OpenShared(string path, bool readOnly)
    {
        FileAccess access = readOnly ? FileAccess.Read : FileAccess.ReadWrite;
        FileShare share = readOnly ? FileShare.ReadWrite : FileShare.Read;

        try
        {
            return new FileStream(path, FileMode.Open, access, share);
        }
        catch (IOException ex)
        {
            // The original exception only says "file in use"; add the piece of
            // information that actually helps resolve it.
            throw new IOException(
                $"Cannot open {Path.GetFileName(path)}: the file is in use by another " +
                $"program (a leftover emulator instance, for example). " +
                $"Close it and try again.", ex);
        }
    }

    /// <summary>Creates an in-memory image, for tests.</summary>
    public static DiskImage CreateInMemory(byte driveNumber, int cylinders, int heads, int sectorsPerTrack)
    {
        long size = (long)cylinders * heads * sectorsPerTrack * SectorSize;
        return new DiskImage(new MemoryStream(new byte[size]), driveNumber,
                             cylinders, heads, sectorsPerTrack);
    }

    /// <summary>
    /// Converts CHS into a linear sector number. Sectors are numbered from 1:
    /// there is no sector 0, and forgetting that is a classic source of
    /// off-by-one bugs.
    /// </summary>
    public long ToLinearSector(int cylinder, int head, int sector)
        => ((long)cylinder * Heads + head) * SectorsPerTrack + (sector - 1);

    public bool IsValidChs(int cylinder, int head, int sector)
        => cylinder >= 0 && cylinder < Cylinders
        && head >= 0 && head < Heads
        && sector >= 1 && sector <= SectorsPerTrack;

    public bool ReadSectors(int cylinder, int head, int sector, int count, Span<byte> destination)
    {
        if (!IsValidChs(cylinder, head, sector)) return false;

        long linear = ToLinearSector(cylinder, head, sector);
        if (linear + count > TotalSectors) return false;

        _stream.Seek(linear * SectorSize, SeekOrigin.Begin);
        int wanted = count * SectorSize;
        int read = _stream.ReadAtLeast(destination[..wanted], wanted, throwOnEndOfStream: false);
        return read == wanted;
    }

    public bool WriteSectors(int cylinder, int head, int sector, int count, ReadOnlySpan<byte> source)
    {
        if (ReadOnly) return false;
        if (!IsValidChs(cylinder, head, sector)) return false;

        long linear = ToLinearSector(cylinder, head, sector);
        if (linear + count > TotalSectors) return false;

        _stream.Seek(linear * SectorSize, SeekOrigin.Begin);
        _stream.Write(source[..(count * SectorSize)]);
        _stream.Flush();
        return true;
    }

    public void Dispose()
    {
        if (_ownsStream) _stream.Dispose();
    }
}
