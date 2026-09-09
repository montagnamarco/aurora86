using System.Text;
using Aurora386.Core.Firmware;
using Aurora386.Core.Firmware.Fpu;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Diagnostics;
using Aurora386.Core.Dos;

namespace Aurora386.Tests;

/// <summary>
/// Tests for the two things that can sit behind a redirected drive letter: a
/// folder on the host and an ISO image.
///
/// The interesting failures here are all about names. DOS has 8.3 names in
/// one case; a host folder has neither constraint, and every mismatch shows
/// up as a file the guest cannot open even though DIR listed it.
/// </summary>
public class RedirectedFileSystemTests : IDisposable
{
    /// <summary>Where ISO 9660 puts the root directory record in the PVD.</summary>
    private const int RootDirectoryRecordOffset = 156;

    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "aurora-share-" + Guid.NewGuid().ToString("N"));

    public RedirectedFileSystemTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private HostDirectoryFileSystem Share() => new(_root);

    private void WriteHostFile(string relativePath, string contents)
    {
        string full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
    }

    // --- host folder ------------------------------------------------------------

    [Fact]
    public void AHostNameThatAlreadyFitsEightPointThreeIsNotMangled()
    {
        // A share exists to be used from DOS, so most of what is put in it is
        // already named for DOS. Turning "README.TXT" into "READ~1.TXT"
        // because the shortener ran unconditionally would break every batch
        // file that referred to it by name.
        WriteHostFile("README.TXT", "hello");

        RedirectedFileInfo entry = Share().Enumerate("\\").Single();
        Assert.Equal("README.TXT", entry.Name);
    }

    [Fact]
    public void ALongHostNameIsShortenedWithATildeSuffix()
    {
        // Eight characters is the limit, and there is nowhere in a DOS
        // directory entry to record what the name really was.
        WriteHostFile("verylongfilename.text", "x");

        RedirectedFileInfo entry = Share().Enumerate("\\").Single();
        Assert.Equal("VERYLO~1.TEX", entry.Name);
    }

    [Fact]
    public void TwoHostNamesThatShortenAlikeGetDistinctNumbers()
    {
        // "Report January.doc" and "Report February.doc" both want to be
        // "REPORT~1.DOC". Without the numbering one of them becomes invisible
        // -- and worse, opening the visible one might reach the other.
        WriteHostFile("Report January.doc", "a");
        WriteHostFile("Report February.doc", "b");

        List<string> names = Share().Enumerate("\\").Select(e => e.Name).OrderBy(n => n).ToList();

        Assert.Equal(2, names.Count);
        Assert.Equal(2, names.Distinct().Count());
        Assert.All(names, n => Assert.StartsWith("REPORT", n));
    }

    [Fact]
    public void AShortenedNameCanBeOpenedAgainByTheNameThatWasShown()
    {
        // The whole point of the mangling: DOS is shown a name, and DOS then
        // asks for that name back. If the mapping is not reversible the file
        // appears in a listing and cannot be opened, which is the single most
        // baffling symptom a redirector can produce.
        WriteHostFile("configuration.json", "{}");

        HostDirectoryFileSystem share = Share();
        string shown = share.Enumerate("\\").Single().Name;

        Assert.NotNull(share.GetInfo("\\" + shown));
        Assert.Equal(2, share.GetInfo("\\" + shown)!.Size);
    }

    [Fact]
    public void OnlyTheLastDotSeparatesTheExtension()
    {
        // "archive.tar.gz" is one name with extension "gz", not "tar.gz" and
        // not "archive" with extension "tar". Splitting on the first dot
        // produces "ARCHIVE.TAR", losing the distinction between that file
        // and a genuine "archive.tar" beside it.
        WriteHostFile("archive.tar.gz", "x");

        Assert.Equal("ARCHIV~1.GZ", Share().Enumerate("\\").Single().Name);
    }

    [Fact]
    public void ADirectoryIsReportedWithTheDirectoryAttribute()
    {
        // Without the bit DOS treats the entry as a file: CD into it fails,
        // and DIR shows a size instead of <DIR>.
        Directory.CreateDirectory(Path.Combine(_root, "GAMES"));

        RedirectedFileInfo entry = Share().Enumerate("\\").Single();
        Assert.True(entry.IsDirectory);
        Assert.Equal(RedirectedFileAttributes.Directory, entry.Attributes);
    }

    [Fact]
    public void AFileInASubdirectoryIsReachedThroughItsPath()
    {
        WriteHostFile(Path.Combine("GAMES", "DOOM.EXE"), "MZ");

        Assert.NotNull(Share().GetInfo("\\GAMES\\DOOM.EXE"));
        Assert.Null(Share().GetInfo("\\GAMES\\MISSING.EXE"));
    }

    [Fact]
    public void APathThroughAShortenedDirectoryStillResolves()
    {
        // Each component of a path may itself have been shortened, so a
        // lookup cannot simply concatenate: it has to resolve level by level.
        WriteHostFile(Path.Combine("My Documents", "NOTES.TXT"), "text");

        HostDirectoryFileSystem share = Share();
        string directory = share.Enumerate("\\").Single().Name;

        Assert.NotNull(share.GetInfo($"\\{directory}\\NOTES.TXT"));
    }

    [Fact]
    public void ReadingReturnsTheBytesAtTheRequestedOffset()
    {
        // DOS reads a file in chunks and tracks the position itself, so every
        // read after the first arrives with a non-zero offset. A backend that
        // ignores it returns the start of the file forever, and the guest
        // reads the same block until it runs out of patience.
        WriteHostFile("DATA.BIN", "0123456789");

        var buffer = new byte[4];
        int read = Share().Read("\\DATA.BIN", 3, buffer);

        Assert.Equal(4, read);
        Assert.Equal("3456", Encoding.ASCII.GetString(buffer));
    }

    [Fact]
    public void ReadingPastTheEndReturnsAShortCount()
    {
        // A short read is how DOS learns it has reached the end of a file.
        // Padding the buffer instead would make every file look infinitely
        // long.
        WriteHostFile("SHORT.TXT", "abc");

        var buffer = new byte[16];
        Assert.Equal(3, Share().Read("\\SHORT.TXT", 0, buffer));
        Assert.Equal(0, Share().Read("\\SHORT.TXT", 3, buffer));
    }

    [Fact]
    public void HiddenHostFilesAreMarkedHiddenRatherThanShownPlainly()
    {
        // A dot-file is the Unix convention for hidden; DOS uses an attribute
        // bit. Translating keeps ".DS_Store" and ".git" out of every DIR.
        //
        // The ordinary file beside it is what makes this test discriminating:
        // without it, a backend that marked nothing hidden could not be told
        // apart from one that marked everything hidden.
        WriteHostFile(".hidden", "x");
        WriteHostFile("VISIBLE.TXT", "y");

        List<RedirectedFileInfo> entries = Share().Enumerate("\\").ToList();

        Assert.True(entries.Count(e => (e.Attributes & RedirectedFileAttributes.Hidden) != 0) == 1);
        Assert.Equal(RedirectedFileAttributes.None,
                     entries.Single(e => e.Name == "VISIBLE.TXT").Attributes
                         & RedirectedFileAttributes.Hidden);
    }

    [Fact]
    public void WritingCreatesTheFileOnTheHost()
    {
        HostDirectoryFileSystem share = Share();
        share.Create("\\NEW.TXT", RedirectedFileAttributes.Archive);
        share.Write("\\NEW.TXT", 0, "hello"u8);

        Assert.Equal("hello", File.ReadAllText(Path.Combine(_root, "NEW.TXT")));
    }

    [Fact]
    public void WritingPastTheEndLeavesZeroesInTheGap()
    {
        // DOS grows a file by seeking beyond the end and writing, and the
        // space in between is defined to read back as zeroes. A backend that
        // silently moved the write to the end instead would corrupt any file
        // written that way.
        HostDirectoryFileSystem share = Share();
        share.Create("\\SPARSE.BIN", RedirectedFileAttributes.Archive);
        share.Write("\\SPARSE.BIN", 4, [0xFF]);

        byte[] contents = File.ReadAllBytes(Path.Combine(_root, "SPARSE.BIN"));
        Assert.Equal([0, 0, 0, 0, 0xFF], contents);
    }

    [Fact]
    public void AReadOnlyShareRefusesEveryWrite()
    {
        WriteHostFile("FILE.TXT", "x");
        var share = new HostDirectoryFileSystem(_root) { IsReadOnly = true };

        Assert.Throws<NotSupportedException>(() => share.Delete("\\FILE.TXT"));
        Assert.Throws<NotSupportedException>(() => share.CreateDirectory("\\NEW"));
    }

    [Fact]
    public void RemovingADirectoryDoesNotRemoveWhatIsInside()
    {
        // DOS's RMDIR removes an empty directory and nothing else. A
        // redirector that quietly deleted a whole tree would be a trap that
        // costs the user their files.
        WriteHostFile(Path.Combine("STUFF", "FILE.TXT"), "important");

        HostDirectoryFileSystem share = Share();
        Assert.Throws<IOException>(() => share.RemoveDirectory("\\STUFF"));
        Assert.True(File.Exists(Path.Combine(_root, "STUFF", "FILE.TXT")));
    }

    // --- ISO image ---------------------------------------------------------------

    /// <summary>
    /// Builds a disc with a root directory holding one file and one
    /// subdirectory, and the subdirectory holding a file of its own.
    /// </summary>
    private static byte[] BuildDisc()
    {
        var image = new byte[40 * IsoImage.SectorSize];

        int pvd = IsoImage.PrimaryVolumeDescriptorSector * IsoImage.SectorSize;
        image[pvd] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(image, pvd + 1);
        Encoding.ASCII.GetBytes("GAMEDISC".PadRight(32)).CopyTo(image, pvd + 40);

        // 156 spelled out rather than taken from the class under test:
        // building the fixture from the implementation's own constant would
        // make the test pass whatever offset it used.
        WriteRecord(image, pvd + RootDirectoryRecordOffset,
                    [0x00], directory: true, sector: 18, length: IsoImage.SectorSize);

        int offset = 18 * IsoImage.SectorSize;
        offset += WriteRecord(image, offset, [0x00], true, 18, IsoImage.SectorSize);
        offset += WriteRecord(image, offset, [0x01], true, 18, IsoImage.SectorSize);
        offset += WriteRecord(image, offset, Encoding.ASCII.GetBytes("SETUP.EXE;1"),
                              false, 22, 9);
        WriteRecord(image, offset, Encoding.ASCII.GetBytes("DATA"), true, 20,
                    IsoImage.SectorSize);

        offset = 20 * IsoImage.SectorSize;
        offset += WriteRecord(image, offset, [0x00], true, 20, IsoImage.SectorSize);
        offset += WriteRecord(image, offset, [0x01], true, 18, IsoImage.SectorSize);
        WriteRecord(image, offset, Encoding.ASCII.GetBytes("LEVEL1.DAT;1"), false, 24, 6);

        Encoding.ASCII.GetBytes("MZexecute").CopyTo(image, 22 * IsoImage.SectorSize);
        Encoding.ASCII.GetBytes("LEVELS").CopyTo(image, 24 * IsoImage.SectorSize);

        return image;
    }

    private static int WriteRecord(byte[] image, int offset, byte[] name,
                                   bool directory, int sector, int length)
    {
        int recordLength = 33 + name.Length;
        if (recordLength % 2 != 0) recordLength++;

        image[offset] = (byte)recordLength;
        for (int i = 0; i < 4; i++)
        {
            image[offset + 2 + i] = (byte)(sector >> (i * 8));
            image[offset + 10 + i] = (byte)(length >> (i * 8));
            image[offset + 5 + i] = (byte)(sector >> ((3 - i) * 8));
            image[offset + 13 + i] = (byte)(length >> ((3 - i) * 8));
        }

        image[offset + 18] = 95;
        image[offset + 19] = 3;
        image[offset + 20] = 12;
        image[offset + 25] = (byte)(directory ? 0x02 : 0x00);
        image[offset + 32] = (byte)name.Length;
        name.CopyTo(image, offset + 33);

        return recordLength;
    }

    private static IsoFileSystem OpenDisc()
        => new(new IsoImage(new MemoryStream(BuildDisc())));

    [Fact]
    public void ADiscListsItsRootWithoutTheDotEntries()
    {
        // ISO 9660's self and parent records are structure, not files: their
        // "names" are the single bytes 0x00 and 0x01. DOS wants "." and ".."
        // in its own form, which the redirector supplies, so these two have
        // to be dropped rather than shown.
        //
        // The check goes through IsoImage rather than only through the DOS
        // listing, because a name of "." would also vanish by accident when
        // it failed to shorten into a legal 8.3 name -- and a test that only
        // looked at the DOS side could not tell a deliberate skip from that
        // accident.
        using var image = new IsoImage(new MemoryStream(BuildDisc()));

        Assert.Equal(2, image.ReadRootDirectory().Count(e => e.IsSpecial));

        List<string> names = new IsoFileSystem(image).Enumerate("\\").Select(e => e.Name).ToList();

        Assert.Equal(["SETUP.EXE", "DATA"], names);
        Assert.DoesNotContain(names, n => n is "." or "..");
    }

    [Fact]
    public void EverythingOnADiscIsReadOnly()
    {
        // A pressed disc cannot be written to, and DOS needs to be told so
        // before it lets a program try -- otherwise a failed write surfaces
        // as data loss rather than as a refusal.
        IsoFileSystem disc = OpenDisc();

        Assert.True(disc.IsReadOnly);
        Assert.All(disc.Enumerate("\\"),
                   e => Assert.True((e.Attributes & RedirectedFileAttributes.ReadOnly) != 0));
    }

    [Fact]
    public void AFileInASubdirectoryOfADiscIsFound()
    {
        RedirectedFileInfo? entry = OpenDisc().GetInfo("\\DATA\\LEVEL1.DAT");

        Assert.NotNull(entry);
        Assert.Equal(6, entry.Size);
    }

    [Fact]
    public void ReadingAFileFromADiscReturnsItsBytes()
    {
        var buffer = new byte[9];
        int read = OpenDisc().Read("\\SETUP.EXE", 0, buffer);

        Assert.Equal(9, read);
        Assert.Equal("MZexecute", Encoding.ASCII.GetString(buffer));
    }

    [Fact]
    public void ReadingAtAnOffsetIntoADiscFileWorks()
    {
        // A file's blocks are contiguous on a disc, so an offset is a block
        // number plus a remainder -- but getting the remainder wrong hands
        // the guest the start of a block instead of the byte it asked for.
        var buffer = new byte[7];
        int read = OpenDisc().Read("\\SETUP.EXE", 2, buffer);

        Assert.Equal(7, read);
        Assert.Equal("execute", Encoding.ASCII.GetString(buffer));
    }

    [Fact]
    public void ReadingPastTheEndOfADiscFileStops()
    {
        // The file is nine bytes inside a 2048-byte block, so a reader that
        // ignores the recorded length happily hands back 2039 bytes of the
        // zero padding that follows.
        var buffer = new byte[64];
        Assert.Equal(9, OpenDisc().Read("\\SETUP.EXE", 0, buffer));
        Assert.Equal(0, OpenDisc().Read("\\SETUP.EXE", 9, buffer));
    }

    [Fact]
    public void ADiscReportsNoFreeSpace()
    {
        // Truthful, and what MSCDEX reported. An installer that asked would
        // otherwise be told it could unpack onto a CD.
        (long free, long total) = OpenDisc().GetDiskSpace();

        Assert.Equal(0, free);
        Assert.True(total > 0);
    }

    [Fact]
    public void TheVolumeLabelComesFromTheDisc()
        => Assert.Equal("GAMEDISC", OpenDisc().VolumeLabel);
}

public class SharedFolderLettersTests
{
    private static Aurora386.Core.Devices.Storage.IsoImage Disc()
    {
        var bytes = new byte[100 * Aurora386.Core.Devices.Storage.IsoImage.SectorSize];
        int pvd = Aurora386.Core.Devices.Storage.IsoImage.PrimaryVolumeDescriptorSector * Aurora386.Core.Devices.Storage.IsoImage.SectorSize;
        bytes[pvd] = 1;
        System.Text.Encoding.ASCII.GetBytes("CD001").CopyTo(bytes, pvd + 1);
        return new Aurora386.Core.Devices.Storage.IsoImage(new MemoryStream(bytes));
    }

    [Fact]
    public void TheFirstFolderTakesEOnAOneDiskMachine()
    {
        using var machine = new Aurora386.Core.Machine();
        machine.AttachDisk(Aurora386.Core.Devices.Storage.DiskImage.CreateInMemory(0x80, 4, 16, 63));
        Assert.Equal(4, machine.NextSharedFolderDrive());
    }

    [Fact]
    public void FoldersStepPastTheCdRomAndEachOther()
    {
        using var machine = new Aurora386.Core.Machine();
        machine.AttachDisk(Aurora386.Core.Devices.Storage.DiskImage.CreateInMemory(0x80, 4, 16, 63));
        machine.AttachDisk(Aurora386.Core.Devices.Storage.DiskImage.CreateInMemory(0x81, 4, 16, 63));
        machine.AttachCompactDisc(Disc());                  // two disks: the disc is E:
        Assert.Equal(4, machine.CompactDiscServices.DriveLetter);

        Assert.Equal(5, machine.NextSharedFolderDrive());   // F:
        string root = Path.Combine(Path.GetTempPath(), "aurora-share-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            machine.Redirector.Mount(5, new Aurora386.Core.Dos.HostDirectoryFileSystem(root));
            Assert.Equal(6, machine.NextSharedFolderDrive());   // G:
        }
        finally { Directory.Delete(root, true); }
    }
}
