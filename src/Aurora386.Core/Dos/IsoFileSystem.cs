using Aurora386.Core.Firmware;
using Aurora386.Core.Firmware.Fpu;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Diagnostics;
namespace Aurora386.Core.Dos;

/// <summary>
/// An ISO 9660 disc presented to DOS as a drive letter.
///
/// This is what MSCDEX actually was. The INT 2Fh function 15h interface that
/// programs call to detect a CD is only the announcement; the part that makes
/// "DIR D:" work is the redirector, and it does its job by translating DOS's
/// questions into walks over the disc's directory records.
///
/// ISO 9660 in its strictest form -- "level 1" -- allows names of eight
/// characters plus a three-character extension, which is not a coincidence:
/// the standard was written so that a disc could be read by MS-DOS without
/// any name mangling at all. Discs that use longer names (level 2, or the
/// Joliet extension Microsoft added for Windows 95) do exist, and for those
/// this class shortens the way the host backend does, because a DOS program
/// has nowhere to put a longer name.
/// </summary>
public sealed class IsoFileSystem(IsoImage image) : IRedirectedFileSystem
{
    private readonly IsoImage _image = image;

    public string VolumeLabel => _image.VolumeLabel;

    /// <summary>A pressed disc cannot be written to, and neither can an image of one.</summary>
    public bool IsReadOnly => true;

    public RedirectedFileInfo? GetInfo(string path)
    {
        if (IsRootPath(path))
        {
            return new RedirectedFileInfo
            {
                Name = "",
                Attributes = RedirectedFileAttributes.Directory
                           | RedirectedFileAttributes.ReadOnly,
            };
        }

        return Locate(path)?.Info;
    }

    public IEnumerable<RedirectedFileInfo> Enumerate(string directoryPath)
    {
        (long sector, long length)? extent = ResolveDirectory(directoryPath);
        if (extent is null)
        {
            yield break;
        }

        foreach (RedirectedFileInfo info in ListDirectory(extent.Value.sector, extent.Value.length)
                     .Select(e => e.Info))
            yield return info;
    }

    public int Read(string path, long offset, Span<byte> destination)
    {
        Located? found = Locate(path);
        if (found is null || found.Info.IsDirectory)
        {
            return 0;
        }

        long size = found.Info.Size;
        if (offset < 0 || offset >= size)
        {
            return 0;
        }

        int wanted = (int)Math.Min(destination.Length, size - offset);
        if (wanted <= 0)
        {
            return 0;
        }

        // A file's data is a straight run of blocks -- ISO 9660 has no
        // fragmentation, because a disc is written once and in order. So a
        // read at an arbitrary offset is a block number plus a remainder, and
        // never a chain to follow.
        var block = new byte[IsoImage.SectorSize];
        int copied = 0;

        while (copied < wanted)
        {
            long absolute = offset + copied;
            long blockIndex = absolute / IsoImage.SectorSize;
            int withinBlock = (int)(absolute % IsoImage.SectorSize);

            _image.ReadSector(found.Sector + blockIndex, block);

            int chunk = Math.Min(IsoImage.SectorSize - withinBlock, wanted - copied);
            block.AsSpan(withinBlock, chunk).CopyTo(destination[copied..]);
            copied += chunk;
        }

        return copied;
    }

    /// <summary>
    /// A disc is full by definition: every block is accounted for and none
    /// can be written. Reporting zero free space is not a failure but the
    /// literal truth, and it is what MSCDEX reported too.
    /// </summary>
    public (long free, long total) GetDiskSpace()
        => (0, _image.TotalSectors * IsoImage.SectorSize);

    // --- writing: refused ------------------------------------------------------

    public void Create(string path, RedirectedFileAttributes attributes) => throw ReadOnlyError();
    public void Write(string path, long offset, ReadOnlySpan<byte> data) => throw ReadOnlyError();
    public void SetLength(string path, long length) => throw ReadOnlyError();
    public void Delete(string path) => throw ReadOnlyError();
    public void Rename(string fromPath, string toPath) => throw ReadOnlyError();
    public void CreateDirectory(string path) => throw ReadOnlyError();
    public void RemoveDirectory(string path) => throw ReadOnlyError();

    private static NotSupportedException ReadOnlyError()
        => new("A CD-ROM cannot be written to.");

    // --- lookup ----------------------------------------------------------------

    /// <summary>
    /// An entry found on the disc, remembering where its data starts: the
    /// redirector will come back asking to read it, and re-walking the
    /// directory tree for every 512-byte read would be absurd.
    /// </summary>
    private sealed record Located(RedirectedFileInfo Info, long Sector, long Length);

    private static bool IsRootPath(string path)
        => string.IsNullOrEmpty(path) || path is "\\" or "/";

    private Located? Locate(string path)
    {
        string[] components = path.Replace('/', '\\')
                                  .Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0)
        {
            return null;
        }

        long sector = _image.RootDirectorySector;
        long length = _image.RootDirectoryLength;
        Located? current = null;

        foreach (string component in components)
        {
            current = ListDirectory(sector, length)
                .FirstOrDefault(e => string.Equals(e.Info.Name, component,
                                                   StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                return null;
            }

            sector = current.Sector;
            length = current.Length;
        }

        return current;
    }

    private (long sector, long length)? ResolveDirectory(string path)
    {
        if (IsRootPath(path))
            return (_image.RootDirectorySector, _image.RootDirectoryLength);

        Located? found = Locate(path);
        if (found is null || !found.Info.IsDirectory)
        {
            return null;
        }

        return (found.Sector, found.Length);
    }

    /// <summary>
    /// Reads one directory and converts its records into DOS entries,
    /// shortening any name that will not fit and dropping the "." and ".."
    /// records -- the redirector supplies those itself, because DOS expects
    /// them in a form ISO 9660 does not store.
    /// </summary>
    private List<Located> ListDirectory(long sector, long length)
    {
        var result = new List<Located>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (IsoDirectoryEntry entry in _image.ReadDirectory(sector, length))
        {
            // The self and parent records are skipped explicitly rather than
            // left to fall out of the name shortening. "." and ".." do get
            // rejected by the shortener, since a dot is not a legal character
            // inside a DOS name -- but relying on that would make the
            // behaviour an accident of a rule written for something else, and
            // it would break the moment the shortener grew a special case.
            if (entry.IsSpecial)
            {
                continue;
            }

            string? name = HostDirectoryFileSystem.ShortenName(entry.Name, taken);
            if (name is null)
            {
                continue;
            }
            taken.Add(name);

            var attributes = RedirectedFileAttributes.ReadOnly;
            if (entry.IsDirectory)
            {
                attributes |= RedirectedFileAttributes.Directory;
            }
            else attributes |= RedirectedFileAttributes.Archive;
            if (entry.IsHidden)
            {
                attributes |= RedirectedFileAttributes.Hidden;
            }

            var info = new RedirectedFileInfo
            {
                Name = name,
                Attributes = attributes,
                Size = entry.IsDirectory ? 0 : entry.Length,
                Modified = entry.RecordedAt ?? new DateTime(1980, 1, 1),
            };

            result.Add(new Located(info, entry.Sector, entry.Length));
        }

        return result;
    }
}
