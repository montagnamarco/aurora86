using Aurora86.Core.Dos;

namespace Aurora86.Tests;

/// <summary>
/// DOS R/H/S/A belong to FAT directory entries, not host permission bits.
/// Shared folders retain them in owned metadata so ATTRIB works across a new
/// machine without changing permissions or exposing the metadata as a DOS file.
/// </summary>
public sealed class HostDosAttributesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aurora-attributes-" + Guid.NewGuid().ToString("N"));
    private string MetadataPath => Path.Combine(_root, HostDosAttributeStore.FileName);

    public HostDosAttributesTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "DATA.TXT"), "host content");
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x02)]
    [InlineData(0x04)]
    [InlineData(0x20)]
    [InlineData(0x27)]
    public void MutableDosFlagsPersistWithoutChangingHostContentsOrPermissions(int flags)
    {
        string path = Path.Combine(_root, "DATA.TXT");
        FileAttributes original = File.GetAttributes(path);
        UnixFileMode? unixMode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path);
        var share = new HostDirectoryFileSystem(_root);
        share.SetAttributes("\\DATA.TXT", (RedirectedFileAttributes)flags);

        var restored = new HostDirectoryFileSystem(_root);
        Assert.Equal((RedirectedFileAttributes)flags, restored.GetInfo("\\DATA.TXT")!.Attributes);
        Assert.Equal((RedirectedFileAttributes)flags, Assert.Single(restored.Enumerate("\\")).Attributes);
        Assert.Equal("host content", File.ReadAllText(path));
        Assert.Equal(original, File.GetAttributes(path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(unixMode!.Value, File.GetUnixFileMode(path));
        }
        Assert.Empty(Directory.GetFiles(_root, HostDosAttributeStore.FileName + ".*.tmp"));
    }

    [Fact]
    public void ListingAndReadingAReadOnlyMountNeverCreateMetadata()
    {
        var share = new HostDirectoryFileSystem(_root) { IsReadOnly = true };
        Assert.Single(share.Enumerate("\\"));
        Assert.NotNull(share.GetInfo("\\DATA.TXT"));
        Assert.Equal(12, share.Read("\\DATA.TXT", 0, new byte[32]));
        Assert.Throws<NotSupportedException>(() => share.SetAttributes("\\DATA.TXT", RedirectedFileAttributes.Hidden));
        Assert.False(File.Exists(MetadataPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACollidingUserFileOrDirectoryIsNeitherHiddenNorOverwritten(bool directory)
    {
        if (directory)
        {
            Directory.CreateDirectory(MetadataPath);
        }
        else
        {
            File.WriteAllText(MetadataPath, "user-owned bytes, not emulator metadata");
        }

        var share = new HostDirectoryFileSystem(_root);
        Assert.Equal(2, share.Enumerate("\\").Count());
        Assert.Throws<IOException>(() => share.SetAttributes("\\DATA.TXT", RedirectedFileAttributes.ReadOnly));
        Assert.Equal(RedirectedFileAttributes.Archive, share.GetInfo("\\DATA.TXT")!.Attributes);
        if (directory)
        {
            Assert.True(Directory.Exists(MetadataPath));
        }
        else
        {
            Assert.Equal("user-owned bytes, not emulator metadata", File.ReadAllText(MetadataPath));
        }
    }

    [Fact]
    public void OwnedMetadataCannotBeListedReadOrChangedThroughGuestPaths()
    {
        var share = new HostDirectoryFileSystem(_root);
        share.SetAttributes("\\DATA.TXT", RedirectedFileAttributes.Hidden);
        byte[] original = File.ReadAllBytes(MetadataPath);
        string path = "\\" + HostDosAttributeStore.FileName;
        Assert.Single(share.Enumerate("\\"));
        Assert.Null(share.GetInfo(path));
        Assert.Null(share.GetInfo("\\AURORA~1.JSO"));
        Assert.Equal(0, share.Read(path, 0, new byte[1024]));
        Assert.Throws<UnauthorizedAccessException>(() => share.Create(path, RedirectedFileAttributes.Archive));
        Assert.Throws<UnauthorizedAccessException>(() => share.Write(path, 0, new byte[] { 0 }));
        Assert.Throws<UnauthorizedAccessException>(() => share.Rename("\\DATA.TXT", path));
        share.Delete(path);
        share.SetLength(path, 0);
        Assert.Equal(original, File.ReadAllBytes(MetadataPath));
    }

    [Fact]
    public void AttributesFollowActualHostNamesAcrossShortAliasesAndDirectoryRenames()
    {
        Directory.CreateDirectory(Path.Combine(_root, "DIR"));
        File.WriteAllText(Path.Combine(_root, "DIR", "Long file name.txt"), "nested");
        var share = new HostDirectoryFileSystem(_root);
        string alias = Assert.Single(share.Enumerate("\\DIR")).Name;
        share.SetAttributes("\\DIR\\" + alias, RedirectedFileAttributes.Hidden | RedirectedFileAttributes.System);
        share.SetAttributes("\\DIR", RedirectedFileAttributes.Hidden);
        share.Rename("\\DIR", "\\RENAMED");

        var restored = new HostDirectoryFileSystem(_root);
        Assert.Equal(RedirectedFileAttributes.Directory | RedirectedFileAttributes.Hidden,
            restored.GetInfo("\\RENAMED")!.Attributes);
        Assert.Equal(RedirectedFileAttributes.Hidden | RedirectedFileAttributes.System,
            restored.GetInfo("\\RENAMED\\" + alias)!.Attributes);
    }

    [Fact]
    public void DeletingAndReusingANameDoesNotReuseItsOldAttributes()
    {
        var share = new HostDirectoryFileSystem(_root);
        share.SetAttributes("\\DATA.TXT", RedirectedFileAttributes.Hidden);
        share.Delete("\\DATA.TXT");
        File.WriteAllText(Path.Combine(_root, "DATA.TXT"), "new host file");
        Assert.Equal(RedirectedFileAttributes.Archive,
            new HostDirectoryFileSystem(_root).GetInfo("\\DATA.TXT")!.Attributes);
    }

    [Fact]
    public void DosReadOnlyProtectsPathMutationsButNotAnAlreadyOpenWrite()
    {
        var share = new HostDirectoryFileSystem(_root);
        share.SetAttributes("\\DATA.TXT", RedirectedFileAttributes.ReadOnly);
        Assert.Throws<UnauthorizedAccessException>(() => share.Create("\\DATA.TXT", RedirectedFileAttributes.Archive));
        Assert.Throws<UnauthorizedAccessException>(() => share.Delete("\\DATA.TXT"));

        // The redirector calls these for an existing SFT. A real DOS 7 FAT
        // handle remains writable after ATTRIB changes the directory entry.
        share.Write("\\DATA.TXT", 0, new byte[] { (byte)'X' });
        share.SetLength("\\DATA.TXT", 1);
        Assert.Equal("X", File.ReadAllText(Path.Combine(_root, "DATA.TXT")));
        share.SetAttributes("\\DATA.TXT", RedirectedFileAttributes.None);
        share.Delete("\\DATA.TXT");
        Assert.False(File.Exists(Path.Combine(_root, "DATA.TXT")));
    }

    [Theory]
    [InlineData(0x10)]
    [InlineData(0x40)]
    [InlineData(0x80)]
    [InlineData(0x3F)]
    public void DirectoryAndReservedRequestsAreRefusedLikeDos7(int flags)
    {
        var share = new HostDirectoryFileSystem(_root);
        Assert.Throws<UnauthorizedAccessException>(() => share.SetAttributes("\\DATA.TXT", (RedirectedFileAttributes)flags));
        Assert.False(File.Exists(MetadataPath));
    }

    [Fact]
    public void VolumeLabelRequestsAreIgnoredAndExistingDirectoryIdentityIsPreserved()
    {
        Directory.CreateDirectory(Path.Combine(_root, "DIR"));
        var share = new HostDirectoryFileSystem(_root);
        share.SetAttributes("\\DATA.TXT", RedirectedFileAttributes.VolumeLabel);
        share.SetAttributes("\\DIR", RedirectedFileAttributes.VolumeLabel | RedirectedFileAttributes.ReadOnly);
        Assert.Equal(RedirectedFileAttributes.None, share.GetInfo("\\DATA.TXT")!.Attributes);
        Assert.Equal(RedirectedFileAttributes.Directory | RedirectedFileAttributes.ReadOnly,
            share.GetInfo("\\DIR")!.Attributes);
    }

    [Fact]
    public void CreatingAFileRetainsItsRequestedDosAttributes()
    {
        var share = new HostDirectoryFileSystem(_root);
        share.Create("\\NEW.TXT", RedirectedFileAttributes.ReadOnly | RedirectedFileAttributes.System);
        Assert.Equal(RedirectedFileAttributes.ReadOnly | RedirectedFileAttributes.System,
            new HostDirectoryFileSystem(_root).GetInfo("\\NEW.TXT")!.Attributes);
    }

    [Fact]
    public void AUserSidecarCollisionCannotTruncateOrCreateGuestFiles()
    {
        File.WriteAllText(MetadataPath, "user file");
        var share = new HostDirectoryFileSystem(_root);
        Assert.Throws<IOException>(() => share.Create("\\DATA.TXT", RedirectedFileAttributes.Archive));
        Assert.Throws<IOException>(() => share.Create("\\NEW.TXT", RedirectedFileAttributes.Archive));
        Assert.Equal("host content", File.ReadAllText(Path.Combine(_root, "DATA.TXT")));
        Assert.False(File.Exists(Path.Combine(_root, "NEW.TXT")));
        Assert.Equal("user file", File.ReadAllText(MetadataPath));
    }

    [Fact]
    public void AFailedHostRenameRestoresBothFilesAttributeRecords()
    {
        File.WriteAllText(Path.Combine(_root, "TARGET.TXT"), "target content");
        var share = new HostDirectoryFileSystem(_root);
        share.SetAttributes("\\DATA.TXT", RedirectedFileAttributes.Hidden);
        share.SetAttributes("\\TARGET.TXT", RedirectedFileAttributes.System);
        Assert.Throws<IOException>(() => share.Rename("\\DATA.TXT", "\\TARGET.TXT"));

        var restored = new HostDirectoryFileSystem(_root);
        Assert.Equal(RedirectedFileAttributes.Hidden, restored.GetInfo("\\DATA.TXT")!.Attributes);
        Assert.Equal(RedirectedFileAttributes.System, restored.GetInfo("\\TARGET.TXT")!.Attributes);
        Assert.Equal("host content", File.ReadAllText(Path.Combine(_root, "DATA.TXT")));
        Assert.Equal("target content", File.ReadAllText(Path.Combine(_root, "TARGET.TXT")));
    }

    [Fact]
    public void AFailedHostCreateRemovesTheMetadataItWouldHaveIntroduced()
    {
        Directory.CreateDirectory(Path.Combine(_root, "DIR"));
        var share = new HostDirectoryFileSystem(_root);
        Assert.Throws<UnauthorizedAccessException>(() => share.Create("\\DIR", RedirectedFileAttributes.Hidden));
        Assert.True(Directory.Exists(Path.Combine(_root, "DIR")));
        Assert.False(File.Exists(MetadataPath));
    }

    [Fact]
    public void AnUnwritableMetadataDirectoryCannotTruncateAnExistingWritableFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var share = new HostDirectoryFileSystem(_root);
        share.SetAttributes("\\DATA.TXT", RedirectedFileAttributes.Hidden);
        UnixFileMode mode = File.GetUnixFileMode(_root);
        File.SetUnixFileMode(_root, mode & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => share.Create("\\DATA.TXT", RedirectedFileAttributes.Archive));
            Assert.Equal("host content", File.ReadAllText(Path.Combine(_root, "DATA.TXT")));
            Assert.Equal(RedirectedFileAttributes.Hidden,
                new HostDirectoryFileSystem(_root).GetInfo("\\DATA.TXT")!.Attributes);
        }
        finally
        {
            File.SetUnixFileMode(_root, mode);
        }
    }

    [Fact]
    public void ASeparateCaseVariantUserFileIsNotHiddenByOwnedMetadata()
    {
        var share = new HostDirectoryFileSystem(_root);
        share.SetAttributes("\\DATA.TXT", RedirectedFileAttributes.Hidden);
        string upperName = HostDosAttributeStore.FileName.ToUpperInvariant();
        string upperPath = Path.Combine(_root, upperName);
        if (File.Exists(upperPath))
        {
            // This filesystem folds case: the spelling reaches our owned file.
            Assert.Null(share.GetInfo("\\" + upperName));
            return;
        }

        File.WriteAllText(upperPath, "separate user file");
        Assert.Equal(2, share.Enumerate("\\").Count());
        RedirectedFileInfo info = share.GetInfo("\\" + upperName)!;
        Assert.NotNull(info);
        var bytes = new byte[64];
        Assert.Equal(18, share.Read("\\" + info.Name, 0, bytes));
        Assert.Equal("separate user file", File.ReadAllText(upperPath));
    }

}
