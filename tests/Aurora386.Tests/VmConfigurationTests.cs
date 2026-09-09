using Aurora386.Core;
using Aurora386.Core.Configuration;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Input;
using Aurora386.Core.Devices.Storage;

namespace Aurora386.Tests;

/// <summary>
/// A machine described in a folder, and what has to survive the trip
/// through the file: every setting, and the paths -- relative when they
/// point inside the folder, so that moving the folder moves the machine.
/// </summary>
public class VmConfigurationTests
{
    private static string TempFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"aurora-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void ARoundTripKeepsEverything()
    {
        string folder = TempFolder();
        try
        {
            var original = new VmConfiguration
            {
                Name = "DOS 6.22",
                Cpu = CpuModel.Intel386,
                RamMegabytes = 8,
                Disks = { new() { Path = "/somewhere/else/hdd.img", Drive = 0x81, ReadOnly = true } },
                Floppies = { "/somewhere/a.img" },
                CompactDisc = "/somewhere/cd.iso",
                SharedFolders = { new() { Path = "/somewhere/shared", Drive = 5 } },
                Mouse = MouseInterface.Ps2,
                BootOrder = { 0x00, 0x80 },
            };
            original.Save(folder);

            VmConfiguration loaded = VmConfiguration.Load(folder);

            Assert.Equal("DOS 6.22", loaded.Name);
            Assert.Equal(CpuModel.Intel386, loaded.Cpu);
            Assert.Equal(8u, loaded.RamMegabytes);
            Assert.Single(loaded.Disks);
            Assert.Equal("/somewhere/else/hdd.img", loaded.Disks[0].Path);
            Assert.Equal(0x81, loaded.Disks[0].Drive);
            Assert.True(loaded.Disks[0].ReadOnly);
            Assert.Equal(new[] { "/somewhere/a.img" }, loaded.Floppies);
            Assert.Equal("/somewhere/cd.iso", loaded.CompactDisc);
            Assert.Equal(5, loaded.SharedFolders[0].Drive);
            Assert.Equal(MouseInterface.Ps2, loaded.Mouse);
            Assert.Equal(new byte[] { 0x00, 0x80 }, loaded.BootOrder);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void PathsInsideTheFolderAreStoredRelativeAndComeBackAbsolute()
    {
        string folder = TempFolder();
        string moved = folder + "-moved";
        try
        {
            string inside = Path.Combine(folder, "hdd0.img");
            File.WriteAllBytes(inside, new byte[1]);

            new VmConfiguration { Disks = { new() { Path = inside } } }.Save(folder);

            string json = File.ReadAllText(Path.Combine(folder, VmConfiguration.FileName));
            Assert.Contains("\"hdd0.img\"", json);
            Assert.DoesNotContain(folder, json);

            // Move the folder: the machine moves with it.
            Directory.Move(folder, moved);
            VmConfiguration loaded = VmConfiguration.Load(moved);
            Assert.Equal(Path.Combine(moved, "hdd0.img"), loaded.Disks[0].Path);
        }
        finally
        {
            if (Directory.Exists(moved)) Directory.Delete(moved, true);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void AnOlderFileIsBroughtForwardWithDefaultsFilledIn()
    {
        string folder = TempFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, VmConfiguration.FileName),
                              """{ "version": 0, "name": "Old" }""");

            VmConfiguration loaded = VmConfiguration.Load(folder);

            Assert.Equal(VmConfiguration.CurrentVersion, loaded.Version);
            Assert.Equal("Old", loaded.Name);
            Assert.Equal(16u, loaded.RamMegabytes);
            Assert.Empty(loaded.Disks);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void AFieldThisVersionDoesNotKnowIsIgnored()
    {
        string folder = TempFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, VmConfiguration.FileName),
                              """{ "name": "New", "colour": "beige" }""");

            Assert.Equal("New", VmConfiguration.Load(folder).Name);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void BuildMachineAttachesWhatTheFileSays()
    {
        string folder = TempFolder();
        try
        {
            string disk = Path.Combine(folder, "hdd0.img");
            DiskImageBuilder.CreateFat16(disk, 20_000_000);

            var configuration = new VmConfiguration
            {
                Cpu = CpuModel.Intel386,
                RamMegabytes = 4,
                Disks = { new() { Path = disk } },
                Mouse = MouseInterface.Ps2,
                BootOrder = { 0x80 },
            };

            using Machine machine = configuration.BuildMachine();

            Assert.Equal(CpuModel.Intel386, machine.CpuModel);
            Assert.Equal(1, machine.DiskServices.HardDiskCount);
            Assert.Equal(MouseInterface.Ps2, machine.MouseInterface);
            Assert.Equal(new byte[] { 0x80 }, machine.BootDrives);
        }
        finally { Directory.Delete(folder, true); }
    }
}
