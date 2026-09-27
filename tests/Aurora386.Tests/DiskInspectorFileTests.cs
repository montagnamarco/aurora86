using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Diagnostics;

namespace Aurora386.Tests;

/// <summary>
/// Going from a file's name to its sectors, which is the direction a person
/// brings to a disk editor. A disposable FAT16 volume gives the tests known
/// files and chains without relying on an installed DOS disk image.
/// </summary>
public class DiskInspectorFileTests
{
    /// <summary>Locates the fixture's partition before testing volume-relative file lookup.</summary>
    private static (DiskInspector Inspector, long Volume) Open(DiskImage image)
    {
        var inspector = new DiskInspector(image);
        return (inspector, inspector.ReadPartitions()[0].FirstSector);
    }

    [Fact]
    public void AFileIsFoundByItsPathAndReadInFull()
    {
        using var disk = new SyntheticFatDisk();
        using DiskImage image = disk.OpenImage();
        (DiskInspector inspector, long volume) = Open(image);

        DiskInspector.FileLocation? found = inspector.FindFile(volume, @"\COMMAND.COM");
        Assert.NotNull(found);
        Assert.False(found.Value.IsDirectory);
        Assert.Equal((uint)disk.CommandContent.Length, found.Value.Size);

        byte[]? content = inspector.ReadFile(volume, @"\COMMAND.COM");
        Assert.NotNull(content);
        Assert.Equal(disk.CommandContent, content);

        // The file begins where its first cluster begins.
        byte[] firstSector = inspector.ReadSector(
            inspector.ClusterToSector(volume, found.Value.FirstCluster));
        Assert.Equal(firstSector.Take(64), content.Take(64));
    }

    [Fact]
    public void AFileInsideADirectoryIsFoundThroughIt()
    {
        using var disk = new SyntheticFatDisk();
        using DiskImage image = disk.OpenImage();
        (DiskInspector inspector, long volume) = Open(image);

        DiskInspector.FileLocation? directory = inspector.FindFile(volume, @"\DOS");
        Assert.NotNull(directory);
        Assert.True(directory.Value.IsDirectory);

        DiskInspector.FileLocation? file = inspector.FindFile(volume, @"\DOS\SMARTDRV.EXE");
        Assert.NotNull(file);
        Assert.False(file.Value.IsDirectory);
        Assert.True(file.Value.Size > 0);
    }

    [Fact]
    public void AMissingFileIsNullAndSoIsAFileBelowAFile()
    {
        using var disk = new SyntheticFatDisk();
        using DiskImage image = disk.OpenImage();
        (DiskInspector inspector, long volume) = Open(image);

        Assert.Null(inspector.FindFile(volume, @"\NOPE.TXT"));
        Assert.Null(inspector.FindFile(volume, @"\DOS\NOPE.TXT"));
        Assert.Null(inspector.FindFile(volume, @"\COMMAND.COM\NOPE.TXT"));
        Assert.Null(inspector.ReadFile(volume, @"\DOS"));           // a directory is not a file
    }
}
