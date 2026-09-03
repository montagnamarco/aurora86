namespace Aurora386.Core.Dos;

/// <summary>
/// What the DOS redirector needs from whatever is standing in for a disk.
///
/// The redirector's job is to answer, on behalf of a drive letter, the
/// questions DOS would otherwise put to its own FAT code: what is in this
/// directory, does this file exist, give me the next chunk of it. Anything
/// that can answer those questions can be a drive -- which is exactly why
/// Microsoft built the interface. It was meant for LAN Manager and Novell
/// redirectors talking to a file server over the wire, and MSCDEX used it to
/// present a CD, whose 2048-byte sectors and ISO 9660 directories DOS could
/// never have parsed itself.
///
/// The abstraction is deliberately shaped around DOS's questions rather than
/// around a filesystem's structure. There is no "seek" because DOS tracks the
/// file position itself, in the System File Table entry; there is no handle
/// table because DOS allocates the handles. What a backend must supply is the
/// ability to look something up by path and to hand over bytes at an offset,
/// and nothing more.
///
/// Paths arriving here are always in DOS form: backslash-separated, uppercase,
/// 8.3 components, and rooted -- "\GAMES\DOOM.EXE" -- with no drive letter,
/// since the redirector has already established which drive is being asked
/// about. A backend must not assume the path is well-formed beyond that.
/// </summary>
public interface IRedirectedFileSystem
{
    /// <summary>
    /// The volume label DOS shows at the top of a directory listing. It is
    /// also what a program checking for its own CD compares against.
    /// </summary>
    string VolumeLabel { get; }

    /// <summary>
    /// True when nothing on this filesystem may be modified. A CD says yes,
    /// and DOS then refuses writes before the redirector is ever asked.
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// Looks up one path. Returns null when there is nothing there, which is
    /// the answer to the overwhelming majority of calls DOS makes: it probes
    /// for files constantly, and "no" is not an error.
    /// </summary>
    RedirectedFileInfo? GetInfo(string path);

    /// <summary>
    /// Lists the contents of a directory, without the "." and ".." entries --
    /// the redirector synthesises those, because DOS expects them to exist
    /// even on filesystems that have no such concept.
    /// </summary>
    IEnumerable<RedirectedFileInfo> Enumerate(string directoryPath);

    /// <summary>
    /// Reads at most <paramref name="count"/> bytes starting at
    /// <paramref name="offset"/>, returning how many were actually read.
    ///
    /// Reading past the end is not an error: it returns fewer bytes, and DOS
    /// takes a short read as end of file. That convention is why a program can
    /// loop on "read 512, stop when you get less" and terminate correctly.
    /// </summary>
    int Read(string path, long offset, Span<byte> destination);

    /// <summary>
    /// Free and total space, in bytes. DOS asks for this whenever something
    /// runs CHKDSK or a DIR, and an installer asks before unpacking.
    /// </summary>
    (long free, long total) GetDiskSpace();

    // --- writing ------------------------------------------------------------
    // A read-only backend may throw NotSupportedException from all of these:
    // the redirector never calls them for a filesystem that reports itself
    // read-only, and DOS refuses the operation first anyway.

    /// <summary>Creates or truncates a file. </summary>
    void Create(string path, RedirectedFileAttributes attributes);

    /// <summary>Writes at an offset, extending the file if need be.</summary>
    void Write(string path, long offset, ReadOnlySpan<byte> data);

    /// <summary>Truncates or extends a file to an exact length.</summary>
    void SetLength(string path, long length);

    void Delete(string path);
    void Rename(string fromPath, string toPath);
    void CreateDirectory(string path);
    void RemoveDirectory(string path);
}

/// <summary>
/// The DOS attribute byte. The values are the ones baked into every program
/// written for DOS, so they are not ours to choose.
/// </summary>
[Flags]
public enum RedirectedFileAttributes : byte
{
    None = 0x00,
    ReadOnly = 0x01,
    Hidden = 0x02,
    System = 0x04,

    /// <summary>
    /// The volume label. Not a file at all: it is a directory entry in the
    /// root whose name is the label, a hack from the days when there was
    /// nowhere else in a FAT filesystem to put one.
    /// </summary>
    VolumeLabel = 0x08,

    Directory = 0x10,

    /// <summary>Set by DOS on every write; backup programs clear it.</summary>
    Archive = 0x20,
}

/// <summary>
/// One entry as DOS wants to see it: an 8.3 name, an attribute byte, a size
/// and a timestamp in the packed FAT format.
/// </summary>
public sealed class RedirectedFileInfo
{
    /// <summary>The 8.3 name, uppercase, without a path.</summary>
    public required string Name { get; init; }

    public RedirectedFileAttributes Attributes { get; init; }

    /// <summary>
    /// Size in bytes. DOS carries this as a 32-bit value, so a file larger
    /// than 4 GB cannot be described at all -- which no medium of the era
    /// could hold anyway.
    /// </summary>
    public long Size { get; init; }

    public DateTime Modified { get; init; } = new DateTime(1980, 1, 1);

    public bool IsDirectory => (Attributes & RedirectedFileAttributes.Directory) != 0;

    /// <summary>
    /// The time in FAT's packed form: seconds in two-second units in bits
    /// 0-4, minutes in 5-10, hours in 11-15. The two-second granularity is
    /// not an oversight -- it bought the one bit needed to fit a whole
    /// timestamp into two bytes, and file times on DOS are even-secondsforever
    /// as a result.
    /// </summary>
    public ushort PackedTime
        => (ushort)((Modified.Hour << 11) | (Modified.Minute << 5) | (Modified.Second / 2));

    /// <summary>
    /// The date in FAT's packed form: day in bits 0-4, month in 5-8, and the
    /// year counted from 1980 in bits 9-15. 1980 was chosen because it was
    /// the year before the IBM PC shipped, and it means no DOS file can be
    /// dated earlier.
    /// </summary>
    public ushort PackedDate
    {
        get
        {
            int year = Math.Clamp(Modified.Year - 1980, 0, 127);
            return (ushort)((year << 9) | (Modified.Month << 5) | Modified.Day);
        }
    }
}
