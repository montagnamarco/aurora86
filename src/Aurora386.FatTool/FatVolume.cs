namespace Aurora386.FatTool;

/// <summary>
/// Reads and writes the FAT file system inside a disk image.
///
/// This exists because a guest with no network and no shared folder still has
/// to be given files: a driver to load, a program to run, a configuration line
/// to change. Doing it from outside, while the machine is off, is both the
/// simplest way and the one that cannot go wrong halfway through.
///
/// FAT12 and FAT16 are handled, which between them cover every floppy and
/// every hard disk small enough for MS-DOS to boot from. FAT32 is not: it
/// arrived with Windows 95 and nothing that runs on a 386 needs it.
/// </summary>
public sealed class FatVolume
{
    private readonly byte[] _image;
    private readonly uint _partitionOffset;

    private readonly ushort _bytesPerSector;
    private readonly byte _sectorsPerCluster;
    private readonly ushort _reservedSectors;
    private readonly byte _fatCount;
    private readonly ushort _rootEntries;
    private readonly ushort _sectorsPerFat;
    private readonly uint _totalSectors;

    private readonly uint _rootDirectoryOffset;
    private readonly uint _dataOffset;
    private readonly uint _clusterCount;

    /// <summary>Whether the table has 12-bit or 16-bit entries.</summary>
    private readonly bool _isFat12;

    public FatVolume(byte[] image)
    {
        _image = image;
        _partitionOffset = FindPartition(image);

        _bytesPerSector = ReadWord(_partitionOffset + 11);
        _sectorsPerCluster = _image[_partitionOffset + 13];
        _reservedSectors = ReadWord(_partitionOffset + 14);
        _fatCount = _image[_partitionOffset + 16];
        _rootEntries = ReadWord(_partitionOffset + 17);
        _sectorsPerFat = ReadWord(_partitionOffset + 22);

        ushort smallTotal = ReadWord(_partitionOffset + 19);
        _totalSectors = smallTotal != 0 ? smallTotal : ReadDword(_partitionOffset + 32);

        if (_bytesPerSector is 0 || _sectorsPerCluster is 0 || _sectorsPerFat is 0)
            throw new InvalidDataException("This does not look like a FAT volume.");

        uint rootSectors =
            (uint)((_rootEntries * 32 + _bytesPerSector - 1) / _bytesPerSector);

        _rootDirectoryOffset = _partitionOffset
            + (uint)(_reservedSectors + _fatCount * _sectorsPerFat) * _bytesPerSector;
        _dataOffset = _rootDirectoryOffset + rootSectors * _bytesPerSector;

        uint dataSectors = _totalSectors
            - _reservedSectors - (uint)(_fatCount * _sectorsPerFat) - rootSectors;
        _clusterCount = dataSectors / _sectorsPerCluster;

        // The boundary is a count, not a size: a volume is FAT12 when it has
        // fewer than 4085 clusters, whatever its total capacity. Getting this
        // wrong reads the table with the wrong entry width and every chain
        // leads somewhere else.
        _isFat12 = _clusterCount < 4085;
    }

    public static FatVolume Open(string path) => new(File.ReadAllBytes(path));

    public void Save(string path) => File.WriteAllBytes(path, _image);

    public string Describe() =>
        $"FAT{(_isFat12 ? 12 : 16)}, {_bytesPerSector}-byte sectors, " +
        $"{_sectorsPerCluster} per cluster, {_clusterCount} clusters, " +
        $"{_rootEntries} root entries";

    /// <summary>
    /// Finds where the file system starts.
    ///
    /// A floppy has no partition table: the boot sector is the first thing in
    /// the file. A hard disk has an MBR, and the partition entry says how far
    /// in the volume begins. Telling them apart by looking for the partition
    /// table's signature is more reliable than going by the file's size,
    /// because both kinds come in every size.
    /// </summary>
    private static uint FindPartition(byte[] image)
    {
        if (image.Length < 512)
            throw new InvalidDataException("The image is too small to hold a boot sector.");

        // "MSDOS", "MSWIN" or the like sits here on a boot sector; a partition
        // table has no reason to. But the surer test is the jump instruction
        // every boot sector begins with, and the BPB that follows it.
        ushort bytesPerSector = (ushort)(image[11] | (image[12] << 8));
        bool looksLikeBootSector =
            image[0] is 0xEB or 0xE9 && bytesPerSector is 512 or 1024 or 2048 or 4096;

        if (looksLikeBootSector) return 0;

        // Otherwise read the first partition entry. Offset 8 of the entry is
        // the sector the partition starts at.
        const int FirstEntry = 446;
        uint start = (uint)(image[FirstEntry + 8]
                          | (image[FirstEntry + 9] << 8)
                          | (image[FirstEntry + 10] << 16)
                          | (image[FirstEntry + 11] << 24));

        if (start == 0)
            throw new InvalidDataException("No boot sector and no partition table.");

        return start * 512;
    }

    // --- the allocation table -------------------------------------------------

    private uint FatOffset => _partitionOffset + (uint)_reservedSectors * _bytesPerSector;

    /// <summary>
    /// Reads one entry of the allocation table.
    ///
    /// FAT16 entries are plain words. FAT12 entries are twelve bits, so two of
    /// them share three bytes and every other one is split across a byte
    /// boundary -- which is why the odd and even cases differ. It is an
    /// awkward format, and it exists because on a 360 KB floppy the four bits
    /// saved per cluster were worth the trouble.
    /// </summary>
    private uint ReadFat(uint cluster)
    {
        if (!_isFat12) return ReadWord(FatOffset + cluster * 2);

        uint offset = FatOffset + cluster + cluster / 2;
        ushort pair = ReadWord(offset);

        return (cluster & 1) != 0 ? (uint)(pair >> 4) : (uint)(pair & 0x0FFF);
    }

    private void WriteFat(uint cluster, uint value)
    {
        for (int copy = 0; copy < _fatCount; copy++)
        {
            uint tableBase = FatOffset + (uint)copy * _sectorsPerFat * _bytesPerSector;

            if (!_isFat12)
            {
                WriteWord(tableBase + cluster * 2, (ushort)value);
                continue;
            }

            uint offset = tableBase + cluster + cluster / 2;
            ushort pair = ReadWord(offset);

            pair = (cluster & 1) != 0
                ? (ushort)((pair & 0x000Fu) | ((value & 0x0FFFu) << 4))
                : (ushort)((pair & 0xF000u) | (value & 0x0FFFu));

            WriteWord(offset, pair);
        }
    }

    /// <summary>The value that marks the last cluster of a file.</summary>
    private uint EndOfChain => _isFat12 ? 0xFFFu : 0xFFFFu;

    /// <summary>
    /// Finds a run of free clusters and links them into a chain.
    ///
    /// Cluster numbering starts at 2 -- entries 0 and 1 of the table hold the
    /// media descriptor and a marker rather than allocations, a quirk
    /// inherited from the very first version of the format.
    /// </summary>
    private uint AllocateChain(int clustersNeeded)
    {
        var chain = new List<uint>(clustersNeeded);

        for (uint cluster = 2; cluster < _clusterCount + 2 && chain.Count < clustersNeeded; cluster++)
            if (ReadFat(cluster) == 0) chain.Add(cluster);

        if (chain.Count < clustersNeeded)
            throw new IOException(
                $"Not enough room: {clustersNeeded} clusters needed, {chain.Count} free.");

        for (int i = 0; i < chain.Count - 1; i++) WriteFat(chain[i], chain[i + 1]);
        WriteFat(chain[^1], EndOfChain);

        return chain[0];
    }

    private void ReleaseChain(uint firstCluster)
    {
        uint cluster = firstCluster;

        while (cluster >= 2 && cluster < EndOfChain)
        {
            uint next = ReadFat(cluster);
            WriteFat(cluster, 0);
            cluster = next;
        }
    }

    private uint ClusterOffset(uint cluster) =>
        _dataOffset + (cluster - 2) * _sectorsPerCluster * _bytesPerSector;

    // --- the root directory ---------------------------------------------------

    /// <summary>
    /// Puts a file in the root directory, replacing one of the same name.
    ///
    /// Only the root is handled, and deliberately: it is where a driver or a
    /// configuration file belongs, and walking subdirectories would double
    /// the code for a case that does not come up.
    /// </summary>
    public void PutFile(string shortName, byte[] contents)
    {
        (string stem, string extension) = SplitShortName(shortName);
        int entry = FindEntry(stem, extension);

        if (entry >= 0)
        {
            // Replacing: let go of the old contents before allocating, or a
            // file being overwritten with a bigger one could fail for lack of
            // room it already owns.
            uint oldCluster = ReadWord(EntryOffset(entry) + 26);
            if (oldCluster >= 2) ReleaseChain(oldCluster);
        }
        else
        {
            entry = FindFreeEntry();
            if (entry < 0) throw new IOException("The root directory is full.");
        }

        uint clusterBytes = (uint)_sectorsPerCluster * _bytesPerSector;
        int clustersNeeded = contents.Length == 0
            ? 0
            : (int)((contents.Length + clusterBytes - 1) / clusterBytes);

        uint firstCluster = clustersNeeded == 0 ? 0 : AllocateChain(clustersNeeded);

        // Write the contents out along the chain.
        uint cluster = firstCluster;
        int written = 0;

        while (written < contents.Length)
        {
            uint offset = ClusterOffset(cluster);
            int chunk = (int)Math.Min(clusterBytes, (uint)(contents.Length - written));

            contents.AsSpan(written, chunk).CopyTo(_image.AsSpan((int)offset, chunk));

            written += chunk;
            cluster = ReadFat(cluster);
        }

        WriteDirectoryEntry(entry, stem, extension, firstCluster, contents.Length);
    }

    public byte[]? GetFile(string shortName)
    {
        (string stem, string extension) = SplitShortName(shortName);
        int entry = FindEntry(stem, extension);
        if (entry < 0) return null;

        uint offset = EntryOffset(entry);
        uint cluster = ReadWord(offset + 26);
        uint size = ReadDword(offset + 28);

        var contents = new byte[size];
        uint clusterBytes = (uint)_sectorsPerCluster * _bytesPerSector;
        int read = 0;

        while (read < size && cluster >= 2 && cluster < EndOfChain)
        {
            int chunk = (int)Math.Min(clusterBytes, size - (uint)read);
            _image.AsSpan((int)ClusterOffset(cluster), chunk)
                  .CopyTo(contents.AsSpan(read, chunk));

            read += chunk;
            cluster = ReadFat(cluster);
        }

        return contents;
    }

    public IEnumerable<(string Name, uint Size, bool IsDirectory)> List()
    {
        for (int i = 0; i < _rootEntries; i++)
        {
            uint offset = EntryOffset(i);
            byte first = _image[offset];

            if (first is 0x00) break;          // nothing beyond here has ever been used
            if (first is 0xE5) continue;       // deleted

            byte attributes = _image[offset + 11];
            if ((attributes & 0x08) != 0) continue;   // the volume label, not a file

            string stem = System.Text.Encoding.ASCII.GetString(_image, (int)offset, 8).TrimEnd();
            string extension = System.Text.Encoding.ASCII.GetString(_image, (int)offset + 8, 3).TrimEnd();
            string name = extension.Length > 0 ? $"{stem}.{extension}" : stem;

            yield return (name, ReadDword(offset + 28), (attributes & 0x10) != 0);
        }
    }

    private uint EntryOffset(int index) => _rootDirectoryOffset + (uint)index * 32;

    private int FindEntry(string stem, string extension)
    {
        for (int i = 0; i < _rootEntries; i++)
        {
            uint offset = EntryOffset(i);
            if (_image[offset] is 0x00) return -1;
            if (_image[offset] is 0xE5) continue;

            string entryStem = System.Text.Encoding.ASCII.GetString(_image, (int)offset, 8);
            string entryExtension = System.Text.Encoding.ASCII.GetString(_image, (int)offset + 8, 3);

            if (entryStem == stem.PadRight(8) && entryExtension == extension.PadRight(3))
                return i;
        }

        return -1;
    }

    private int FindFreeEntry()
    {
        for (int i = 0; i < _rootEntries; i++)
        {
            byte first = _image[EntryOffset(i)];
            if (first is 0x00 or 0xE5) return i;
        }

        return -1;
    }

    private void WriteDirectoryEntry(int index, string stem, string extension,
                                     uint firstCluster, int size)
    {
        uint offset = EntryOffset(index);

        System.Text.Encoding.ASCII.GetBytes(stem.PadRight(8))
            .CopyTo(_image, (int)offset);
        System.Text.Encoding.ASCII.GetBytes(extension.PadRight(3))
            .CopyTo(_image, (int)offset + 8);

        _image[offset + 11] = 0x20;   // an ordinary file, with the archive bit set

        for (uint i = 12; i < 22; i++) _image[offset + i] = 0;

        // A plausible timestamp rather than zero. DOS shows zeroes as a
        // nonsensical date, and some installers refuse to work with them.
        DateTime now = DateTime.Now;
        ushort time = (ushort)((now.Hour << 11) | (now.Minute << 5) | (now.Second / 2));
        ushort date = (ushort)(((now.Year - 1980) << 9) | (now.Month << 5) | now.Day);

        WriteWord(offset + 22, time);
        WriteWord(offset + 24, date);
        WriteWord(offset + 26, (ushort)firstCluster);
        WriteDword(offset + 28, (uint)size);
    }

    /// <summary>
    /// Turns a name into the eight-and-three form the directory stores.
    ///
    /// Nothing clever is attempted: a name that does not fit is refused rather
    /// than mangled into something the user did not ask for. Long-name
    /// mangling belongs to the file system, not to a tool for putting a
    /// driver on a disk.
    /// </summary>
    private static (string Stem, string Extension) SplitShortName(string name)
    {
        string upper = name.ToUpperInvariant();
        int dot = upper.LastIndexOf('.');

        string stem = dot < 0 ? upper : upper[..dot];
        string extension = dot < 0 ? "" : upper[(dot + 1)..];

        if (stem.Length is 0 or > 8 || extension.Length > 3)
            throw new ArgumentException(
                $"'{name}' is not an 8.3 name. Rename it, or say what it should be called " +
                "on the disk.");

        return (stem, extension);
    }

    // --- raw access -----------------------------------------------------------

    private ushort ReadWord(uint offset) =>
        (ushort)(_image[offset] | (_image[offset + 1] << 8));

    private uint ReadDword(uint offset) =>
        (uint)(_image[offset]
             | (_image[offset + 1] << 8)
             | (_image[offset + 2] << 16)
             | (_image[offset + 3] << 24));

    private void WriteWord(uint offset, ushort value)
    {
        _image[offset] = (byte)value;
        _image[offset + 1] = (byte)(value >> 8);
    }

    private void WriteDword(uint offset, uint value)
    {
        _image[offset] = (byte)value;
        _image[offset + 1] = (byte)(value >> 8);
        _image[offset + 2] = (byte)(value >> 16);
        _image[offset + 3] = (byte)(value >> 24);
    }
}
