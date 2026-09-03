using System.Text;

namespace Aurora386.Core.Dos;

/// <summary>
/// A directory on the host machine, presented to DOS as a drive letter.
///
/// This is the modern reason to want a redirector at all: it is far more
/// convenient to drop a file into a folder on the host than to build a disk
/// image around it. The mechanism is the one Microsoft shipped for file
/// servers, and DOS cannot tell the difference -- to it, the drive answers
/// the same questions a Novell volume would have answered.
///
/// The hard part is not the file access; it is the names. DOS has 8.3 names
/// in a single case, drawn from a restricted character set, and a host
/// directory has none of those constraints. Every name that comes in has to
/// be squeezed into that shape, and the squeezing has to be stable and
/// reversible enough that a name DOS was shown can be turned back into the
/// host file it came from. Two host files can easily collide once shortened
/// -- "ReadMe.markdown" and "readme.md" both want to be "README.MD" -- and
/// the answer here is the same one Windows 95 arrived at: append "~1", "~2"
/// and so on until the name is unique within its directory.
/// </summary>
public sealed class HostDirectoryFileSystem : IRedirectedFileSystem
{
    private readonly DirectoryInfo _root;

    /// <summary>
    /// Cache mapping a DOS path to the host path it was derived from.
    ///
    /// It exists because shortening is not reversible on its own: given
    /// "READ~1.TXT" there is no way to compute which host file produced it
    /// without listing the directory again. Every enumeration records what it
    /// generated, and lookups consult the record first. A stale entry is
    /// harmless -- the file simply is not there any more and the lookup falls
    /// through to a fresh scan.
    /// </summary>
    private readonly Dictionary<string, string> _shortNames =
        new(StringComparer.OrdinalIgnoreCase);

    public HostDirectoryFileSystem(string hostPath, string? volumeLabel = null)
    {
        _root = new DirectoryInfo(hostPath);
        if (!_root.Exists)
            throw new DirectoryNotFoundException($"No such directory: {hostPath}");

        VolumeLabel = MakeVolumeLabel(volumeLabel ?? _root.Name);
    }

    public string VolumeLabel { get; }

    /// <summary>
    /// A shared folder is writable. That is the whole point of it, and it is
    /// what distinguishes this backend from the CD one.
    /// </summary>
    public bool IsReadOnly { get; init; }

    public RedirectedFileInfo? GetInfo(string path)
    {
        if (IsRootPath(path))
        {
            return new RedirectedFileInfo
            {
                Name = "",
                Attributes = RedirectedFileAttributes.Directory,
                Modified = _root.LastWriteTime,
            };
        }

        string? host = ResolveHostPath(path);
        if (host is null) return null;

        if (Directory.Exists(host))
        {
            var directory = new DirectoryInfo(host);
            return Describe(directory);
        }

        if (File.Exists(host))
        {
            var file = new FileInfo(host);
            return Describe(file);
        }

        return null;
    }

    public IEnumerable<RedirectedFileInfo> Enumerate(string directoryPath)
    {
        string? host = IsRootPath(directoryPath) ? _root.FullName
                                                 : ResolveHostPath(directoryPath);
        if (host is null || !Directory.Exists(host)) yield break;

        // Shortening has to consider the whole directory at once, because
        // uniqueness is only meaningful within one directory: two files in
        // different folders may perfectly well both be "README.TXT".
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string dosDirectory = NormalizePath(directoryPath);

        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(host).GetFileSystemInfos();
        }
        catch (IOException)
        {
            // A directory that vanished, or one the host will not let us
            // read. DOS has no way to express either, so it sees an empty
            // directory -- which is at least a truthful list of what we can
            // reach.
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (FileSystemInfo entry in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            // Files whose names cannot be represented at all -- names that
            // shorten to nothing once the illegal characters are stripped --
            // are hidden rather than shown under a made-up name.
            string? shortName = ShortenName(entry.Name, taken);
            if (shortName is null) continue;

            taken.Add(shortName);
            _shortNames[JoinDosPath(dosDirectory, shortName)] = entry.FullName;

            yield return entry is DirectoryInfo directory
                ? Describe(directory, shortName)
                : Describe((FileInfo)entry, shortName);
        }
    }

    public int Read(string path, long offset, Span<byte> destination)
    {
        string? host = ResolveHostPath(path);
        if (host is null || !File.Exists(host)) return 0;

        using var stream = new FileStream(host, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite);
        if (offset >= stream.Length) return 0;

        stream.Seek(offset, SeekOrigin.Begin);
        return stream.Read(destination);
    }

    public (long free, long total) GetDiskSpace()
    {
        try
        {
            var drive = new DriveInfo(_root.FullName);
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Not every path the host offers belongs to a drive we can
            // interrogate. Reporting a plausible size beats reporting zero,
            // which installers read as "no room" and refuse to run.
            return (512L * 1024 * 1024, 1024L * 1024 * 1024);
        }
    }

    // --- writing ---------------------------------------------------------------

    public void Create(string path, RedirectedFileAttributes attributes)
    {
        RequireWritable();
        string host = ResolveOrPlanHostPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        using var stream = new FileStream(host, FileMode.Create, FileAccess.Write);
        RememberShortName(path, host);
    }

    public void Write(string path, long offset, ReadOnlySpan<byte> data)
    {
        RequireWritable();
        string host = ResolveOrPlanHostPath(path);
        using var stream = new FileStream(host, FileMode.OpenOrCreate, FileAccess.Write,
                                          FileShare.ReadWrite);

        // Writing past the end of a file is how DOS grows one, and the gap in
        // between is defined to read as zeroes. .NET's Seek already does that
        // on every filesystem we care about.
        stream.Seek(offset, SeekOrigin.Begin);
        stream.Write(data);
        RememberShortName(path, host);
    }

    public void SetLength(string path, long length)
    {
        RequireWritable();
        string? host = ResolveHostPath(path);
        if (host is null || !File.Exists(host)) return;

        using var stream = new FileStream(host, FileMode.Open, FileAccess.Write);
        stream.SetLength(length);
    }

    public void Delete(string path)
    {
        RequireWritable();
        string? host = ResolveHostPath(path);
        if (host is null || !File.Exists(host)) return;

        File.Delete(host);
        _shortNames.Remove(NormalizePath(path));
    }

    public void Rename(string fromPath, string toPath)
    {
        RequireWritable();
        string? source = ResolveHostPath(fromPath);
        if (source is null) return;

        string target = ResolveOrPlanHostPath(toPath);

        if (Directory.Exists(source)) Directory.Move(source, target);
        else File.Move(source, target, overwrite: false);

        _shortNames.Remove(NormalizePath(fromPath));
        RememberShortName(toPath, target);
    }

    public void CreateDirectory(string path)
    {
        RequireWritable();
        string host = ResolveOrPlanHostPath(path);
        Directory.CreateDirectory(host);
        RememberShortName(path, host);
    }

    public void RemoveDirectory(string path)
    {
        RequireWritable();
        string? host = ResolveHostPath(path);
        if (host is null || !Directory.Exists(host)) return;

        // Non-recursive on purpose: DOS's RMDIR removes an empty directory
        // and nothing else, and a redirector that quietly deleted a tree
        // would be a trap.
        Directory.Delete(host, recursive: false);
        _shortNames.Remove(NormalizePath(path));
    }

    private void RequireWritable()
    {
        if (IsReadOnly) throw new NotSupportedException("This share is read-only.");
    }

    // --- path handling ---------------------------------------------------------

    private static bool IsRootPath(string path)
        => string.IsNullOrEmpty(path) || path == "\\" || path == "/";

    /// <summary>
    /// Puts a DOS path into the one form used as a dictionary key: leading
    /// backslash, uppercase, no trailing separator.
    /// </summary>
    private static string NormalizePath(string path)
    {
        string cleaned = path.Replace('/', '\\').Trim();
        if (!cleaned.StartsWith('\\')) cleaned = "\\" + cleaned;
        while (cleaned.Length > 1 && cleaned.EndsWith('\\')) cleaned = cleaned[..^1];
        return cleaned.ToUpperInvariant();
    }

    private static string JoinDosPath(string directory, string name)
        => directory == "\\" ? "\\" + name : directory + "\\" + name;

    /// <summary>
    /// Turns a DOS path into a host path, walking one component at a time.
    ///
    /// The walk cannot be skipped by simply concatenating: an intermediate
    /// component may itself be a shortened name, so each level has to be
    /// resolved against the real directory listing before the next can be
    /// looked at. Resolving is why <see cref="Enumerate"/> populates the
    /// cache as a side effect -- it is the only place the mapping exists.
    /// </summary>
    private string? ResolveHostPath(string path)
    {
        string normalized = NormalizePath(path);
        if (normalized == "\\") return _root.FullName;

        if (_shortNames.TryGetValue(normalized, out string? cached)
            && (File.Exists(cached) || Directory.Exists(cached)))
            return cached;

        string current = _root.FullName;
        string dosSoFar = "\\";

        foreach (string component in normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            // The common case: the host already has a file by exactly this
            // name. Only when it does not do we pay for a directory scan.
            string direct = Path.Combine(current, component);
            if (File.Exists(direct) || Directory.Exists(direct))
            {
                current = direct;
                dosSoFar = JoinDosPath(dosSoFar, component);
                continue;
            }

            string? matched = FindByShortName(current, component);
            if (matched is null) return null;

            current = matched;
            dosSoFar = JoinDosPath(dosSoFar, component);
            _shortNames[dosSoFar] = current;
        }

        return current;
    }

    /// <summary>
    /// Scans a host directory for the entry whose shortened name matches.
    /// The scan has to shorten every name in the directory, in the same order
    /// <see cref="Enumerate"/> would, because the "~1" suffixes depend on
    /// what else is present.
    /// </summary>
    private string? FindByShortName(string hostDirectory, string dosName)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(hostDirectory).GetFileSystemInfos();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (FileSystemInfo entry in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            string? shortName = ShortenName(entry.Name, taken);
            if (shortName is null) continue;

            taken.Add(shortName);
            if (string.Equals(shortName, dosName, StringComparison.OrdinalIgnoreCase))
                return entry.FullName;
        }

        return null;
    }

    /// <summary>
    /// Where a path that does not exist yet should be created. Used by the
    /// write operations, which are handed a name DOS invented and must map it
    /// onto a host name that does not exist yet.
    /// </summary>
    private string ResolveOrPlanHostPath(string path)
    {
        string normalized = NormalizePath(path);
        int lastSeparator = normalized.LastIndexOf('\\');
        string parent = lastSeparator <= 0 ? "\\" : normalized[..lastSeparator];
        string name = normalized[(lastSeparator + 1)..];

        string hostParent = ResolveHostPath(parent) ?? _root.FullName;

        // A file created from DOS keeps the 8.3 name DOS gave it. There is
        // nothing to shorten, so the host name is the DOS name verbatim --
        // which also makes the mapping trivially reversible afterwards.
        return Path.Combine(hostParent, name);
    }

    private void RememberShortName(string dosPath, string hostPath)
        => _shortNames[NormalizePath(dosPath)] = hostPath;

    // --- 8.3 name generation ---------------------------------------------------

    /// <summary>
    /// Characters DOS refuses inside a filename. Most are delimiters the
    /// command interpreter or the file system would otherwise read as
    /// structure.
    /// </summary>
    private const string IllegalCharacters = "\"*+,/:;<=>?[\\]|. ";

    /// <summary>
    /// Squeezes a host name into 8.3, making it unique against the names
    /// already handed out for this directory. Returns null for a name that
    /// cannot be represented at all.
    /// </summary>
    internal static string? ShortenName(string hostName, HashSet<string> taken)
    {
        if (string.IsNullOrEmpty(hostName)) return null;

        // The extension is whatever follows the LAST dot; earlier dots are
        // part of the name and get stripped, which is why "my.notes.txt"
        // becomes "MYNOTES.TXT" and not "MY.TXT".
        int lastDot = hostName.LastIndexOf('.');
        string stem = lastDot > 0 ? hostName[..lastDot] : hostName;
        string extension = lastDot > 0 ? hostName[(lastDot + 1)..] : "";

        // A leading dot means a hidden file on the host, not an extension:
        // ".profile" has stem ".profile" by the rule above only because
        // lastDot is 0, which the test above already excluded.
        string cleanStem = Sanitize(stem);
        string cleanExtension = Sanitize(extension);

        if (cleanStem.Length == 0) return null;

        if (cleanExtension.Length > 3) cleanExtension = cleanExtension[..3];

        // A name that already fits and is already unique passes through
        // untouched -- important, because most files on a share put there for
        // DOS were named for DOS in the first place, and mangling them into
        // "READ~1.TXT" when "README.TXT" would do is pure damage.
        string candidate = Compose(cleanStem.Length > 8 ? cleanStem[..8] : cleanStem,
                                   cleanExtension);

        bool needsMangling = cleanStem.Length > 8 || taken.Contains(candidate);
        if (!needsMangling) return candidate;

        for (int index = 1; index < 1000; index++)
        {
            string suffix = "~" + index;
            int keep = Math.Max(1, 8 - suffix.Length);
            string head = cleanStem.Length > keep ? cleanStem[..keep] : cleanStem;

            candidate = Compose(head + suffix, cleanExtension);
            if (!taken.Contains(candidate)) return candidate;
        }

        return null;
    }

    private static string Compose(string stem, string extension)
        => extension.Length == 0 ? stem : stem + "." + extension;

    /// <summary>
    /// Strips everything DOS cannot hold and folds the rest to upper case.
    /// DOS filenames are not case-insensitive so much as case-less: the
    /// directory entry holds capitals and there is nowhere to record that the
    /// user typed anything else.
    /// </summary>
    private static string Sanitize(string part)
    {
        var builder = new StringBuilder(part.Length);

        foreach (char c in part)
        {
            if (c < 32 || c > 126) continue;
            if (IllegalCharacters.Contains(c)) continue;
            builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }

    private static string MakeVolumeLabel(string name)
    {
        var builder = new StringBuilder(11);
        foreach (char c in name.ToUpperInvariant())
        {
            if (builder.Length == 11) break;
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')
                builder.Append(c);
        }
        return builder.Length == 0 ? "SHARED" : builder.ToString();
    }

    private RedirectedFileInfo Describe(FileInfo file, string? name = null)
    {
        var attributes = RedirectedFileAttributes.Archive;
        if (IsReadOnly || file.IsReadOnly) attributes |= RedirectedFileAttributes.ReadOnly;
        if (IsHiddenOnHost(file)) attributes |= RedirectedFileAttributes.Hidden;

        return new RedirectedFileInfo
        {
            Name = name ?? ShortenName(file.Name, []) ?? file.Name,
            Attributes = attributes,
            Size = file.Length,
            Modified = ClampToFatEpoch(file.LastWriteTime),
        };
    }

    private RedirectedFileInfo Describe(DirectoryInfo directory, string? name = null)
    {
        var attributes = RedirectedFileAttributes.Directory;
        if (IsHiddenOnHost(directory)) attributes |= RedirectedFileAttributes.Hidden;

        return new RedirectedFileInfo
        {
            Name = name ?? ShortenName(directory.Name, []) ?? directory.Name,
            Attributes = attributes,
            Size = 0,
            Modified = ClampToFatEpoch(directory.LastWriteTime),
        };
    }

    /// <summary>
    /// Unix hides a file by starting its name with a dot; DOS hides one with
    /// an attribute bit. Translating between the two conventions is the least
    /// surprising thing to do -- a share full of ".DS_Store" and ".git" would
    /// otherwise clutter every DIR.
    ///
    /// The leading-dot test is spelled out rather than left to the host,
    /// because .NET maps the convention onto <see cref="FileAttributes"/> on
    /// Unix but has nothing to map on Windows, where a dot-file is simply a
    /// file whose name begins with a dot. Checking both means a share behaves
    /// the same whichever machine the emulator is running on.
    /// </summary>
    private static bool IsHiddenOnHost(FileSystemInfo entry)
        => entry.Name.StartsWith('.')
        || (entry.Attributes & FileAttributes.Hidden) != 0;

    /// <summary>
    /// FAT timestamps cannot express a date before 1980 or after 2107, and a
    /// host file can easily have one -- an epoch-zero timestamp, or a clock
    /// that ran away. Clamping keeps the packed fields in range instead of
    /// wrapping into a nonsense date.
    /// </summary>
    private static DateTime ClampToFatEpoch(DateTime time)
    {
        if (time.Year < 1980) return new DateTime(1980, 1, 1);
        if (time.Year > 2107) return new DateTime(2107, 12, 31);
        return time;
    }
}
