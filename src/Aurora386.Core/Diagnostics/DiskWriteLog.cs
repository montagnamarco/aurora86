using System.Text;

using Aurora386.Core.Devices.Storage;
namespace Aurora386.Core.Diagnostics;

/// <summary>
/// Records which files the guest writes to, by resolving each written sector
/// back to the directory entry that owns it.
///
/// A sector number on its own says almost nothing. The same trace read as file
/// names says what the guest was doing and how far it got, which is the
/// question worth asking when an installer stops making progress: the last
/// file written is where it was up to.
///
/// The mapping is rebuilt from the volume's own tables rather than tracked as
/// writes go by, because a write may land in a file that did not exist when
/// the run started. Rebuilding is cheap enough at the rate whole files are
/// created, and always tells the truth about the volume as it now stands.
/// </summary>
public sealed class DiskWriteLog : IDisposable
{
    private readonly DiskImage _image;
    private readonly TextWriter _output;
    private readonly Dictionary<uint, string> _owners = new();

    private uint _dataStart;
    private uint _sectorsPerCluster = 1;
    private bool _mapped;
    private string? _last;
    private long _sinceRemap;

    /// <summary>A log written to a file, one line per file the guest touches.</summary>
    public DiskWriteLog(DiskImage image, string path)
        : this(image, new StreamWriter(path) { AutoFlush = true }) { }

    /// <summary>
    /// A log that keeps nothing on disk and only raises <see cref="LineWritten"/>,
    /// for a window that wants to show the writes as they happen.
    /// </summary>
    public DiskWriteLog(DiskImage image) : this(image, TextWriter.Null) { }

    private DiskWriteLog(DiskImage image, TextWriter output)
    {
        _image = image;
        _output = output;
        Emit("# files written by the guest, in order");
    }

    /// <summary>
    /// Raised with every line as it is written, on the emulation thread.
    /// Anything slow here slows the guest being watched.
    /// </summary>
    public event Action<string>? LineWritten;

    private void Emit(string line)
    {
        _output.WriteLine(line);
        LineWritten?.Invoke(line);
    }

    /// <summary>
    /// Attach to a machine and record every write it makes to the hard disk
    /// -- to a file when a path is given, otherwise only through
    /// <see cref="LineWritten"/>.
    /// </summary>
    public static DiskWriteLog Attach(Machine machine, DiskImage image, string? path = null)
    {
        var log = path is null ? new DiskWriteLog(image) : new DiskWriteLog(image, path);

        machine.DiskServices.SectorTransferred += (drive, write, cylinder, head, sector, count) =>
        {
            if (!write || drive < 0x80) return;
            log.Note(cylinder, head, sector, count);
        };

        return log;
    }

    private void Note(int cylinder, int head, int sector, int count)
    {
        // The geometry the BIOS reports for this image is what the guest used
        // to build the request, so it is what turns it back into an offset.
        long lba = ((long)cylinder * _image.Heads + head) * _image.SectorsPerTrack + (sector - 1);

        // A file that has just been created is not in the map yet. Rebuilding
        // on a miss keeps the map current without rebuilding on every write.
        if (!_mapped || ++_sinceRemap > 400) BuildMap();

        string owner = Resolve(lba) ?? "(structures)";
        if (owner == "(structures)") return;

        if (owner != _last)
        {
            Emit(owner);
            _last = owner;
        }
    }

    private string? Resolve(long lba)
    {
        long offset = lba * DiskImage.SectorSize;
        if (offset < _dataStart) return null;

        uint cluster = (uint)((offset - _dataStart) / (_sectorsPerCluster * DiskImage.SectorSize)) + 2;
        return _owners.TryGetValue(cluster, out string? name) ? name : null;
    }

    /// <summary>Walks the volume and records which file owns each cluster.</summary>
    private void BuildMap()
    {
        _owners.Clear();
        _sinceRemap = 0;
        _mapped = true;

        var boot = new byte[DiskImage.SectorSize];
        if (!_image.ReadSectors(0, 0, 1, 1, boot)) return;

        // Either a partition table or a boot sector; the partition table has
        // its own signature at 0x1FE and a first entry pointing further in.
        uint partition = 0;
        if (boot[0x1FE] == 0x55 && boot[0x1FF] == 0xAA && boot[0x1C2] != 0)
            partition = BitConverter.ToUInt32(boot, 0x1C6);

        var record = new byte[DiskImage.SectorSize];
        if (!ReadAbsolute(partition, record)) return;

        ushort bytesPerSector = BitConverter.ToUInt16(record, 11);
        _sectorsPerCluster = record[13];
        ushort reserved = BitConverter.ToUInt16(record, 14);
        byte fatCount = record[16];
        ushort rootEntries = BitConverter.ToUInt16(record, 17);
        ushort sectorsPerFat = BitConverter.ToUInt16(record, 22);

        if (bytesPerSector == 0 || _sectorsPerCluster == 0 || sectorsPerFat == 0) return;

        uint rootStart = partition + reserved + (uint)(fatCount * sectorsPerFat);
        uint rootSectors = (uint)(rootEntries * 32 / bytesPerSector);

        _dataStart = (rootStart + rootSectors) * bytesPerSector;
        uint fatStart = (partition + reserved) * bytesPerSector;

        var fat = new byte[sectorsPerFat * bytesPerSector];
        for (uint i = 0; i < sectorsPerFat; i++)
            if (!ReadAbsolute(partition + reserved + i, fat.AsSpan((int)(i * bytesPerSector), bytesPerSector))) return;

        var root = new byte[rootSectors * bytesPerSector];
        for (uint i = 0; i < rootSectors; i++)
            if (!ReadAbsolute(rootStart + i, root.AsSpan((int)(i * bytesPerSector), bytesPerSector))) return;

        WalkDirectory(root, fat, "C:", bytesPerSector, depth: 0);
    }

    private bool ReadAbsolute(uint lba, Span<byte> into)
    {
        int sectorsPerTrack = _image.SectorsPerTrack;
        int heads = _image.Heads;
        if (sectorsPerTrack == 0 || heads == 0) return false;

        int sector = (int)(lba % sectorsPerTrack) + 1;
        int head = (int)(lba / sectorsPerTrack % heads);
        int cylinder = (int)(lba / sectorsPerTrack / heads);

        var buffer = new byte[DiskImage.SectorSize];
        if (!_image.ReadSectors(cylinder, head, sector, 1, buffer)) return false;

        buffer.AsSpan(0, into.Length).CopyTo(into);
        return true;
    }

    private void WalkDirectory(byte[] directory, byte[] fat, string path,
                               ushort bytesPerSector, int depth)
    {
        // Deeply nested trees are not worth chasing, and a corrupt volume can
        // present a cycle that would otherwise never end.
        if (depth > 8) return;

        for (int i = 0; i + 32 <= directory.Length; i += 32)
        {
            byte first = directory[i];
            if (first == 0) break;
            if (first == 0xE5 || (directory[i + 11] & 0x08) != 0) continue;

            string name = Encoding.ASCII.GetString(directory, i, 8).TrimEnd();
            string extension = Encoding.ASCII.GetString(directory, i + 8, 3).TrimEnd();
            if (name.StartsWith('.')) continue;

            ushort start = BitConverter.ToUInt16(directory, i + 26);
            if (start < 2) continue;

            string full = extension.Length > 0 ? $"{path}\\{name}.{extension}" : $"{path}\\{name}";

            if ((directory[i + 11] & 0x10) != 0)
            {
                byte[] contents = ReadChain(start, fat, bytesPerSector);
                WalkDirectory(contents, fat, full, bytesPerSector, depth + 1);
                continue;
            }

            for (uint cluster = start; cluster >= 2 && cluster < 0xFFF8;)
            {
                if (!_owners.TryAdd(cluster, full)) break;   // a loop in the chain
                cluster = BitConverter.ToUInt16(fat, (int)(cluster * 2));
            }
        }
    }

    private byte[] ReadChain(uint start, byte[] fat, ushort bytesPerSector)
    {
        var result = new List<byte>();
        var seen = new HashSet<uint>();

        for (uint cluster = start; cluster >= 2 && cluster < 0xFFF8 && seen.Add(cluster);)
        {
            uint offset = _dataStart + (cluster - 2) * _sectorsPerCluster * bytesPerSector;
            for (uint s = 0; s < _sectorsPerCluster; s++)
            {
                var sector = new byte[DiskImage.SectorSize];
                if (!ReadAbsolute(offset / bytesPerSector + s, sector)) return result.ToArray();
                result.AddRange(sector);
            }
            cluster = BitConverter.ToUInt16(fat, (int)(cluster * 2));
        }

        return result.ToArray();
    }

    public void Dispose() => _output.Dispose();
}
