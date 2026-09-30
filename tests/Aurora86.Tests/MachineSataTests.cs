using Aurora86.Core;
using Aurora86.Core.Configuration;
using Aurora86.Core.Devices.Cards;
using Aurora86.Core.Devices.Storage;

namespace Aurora86.Tests;

/// <summary>
/// The AHCI controller fitted to a machine (plans/piattaforma-moderna.md M4):
/// where it appears on the PCI bus, the interrupt the POST routes to it, the
/// disk a guest's driver finds on a port, its snapshot section, and
/// <c>vm.json</c>'s <c>hardware.sata</c>. The register engine itself is
/// AhciControllerTests' subject.
/// </summary>
public class MachineSataTests
{
    private const ushort AhciFunction = 6 << 3;          // 00:06.0
    private const uint AhciIdentity = 0x29228086;         // device 2922h, vendor 8086h
    private const uint MassStorageSataAhci = 0x010601;    // class, subclass, programming interface
    private const int InterruptLineOffset = 0x3C;
    private const int CommandOffset = 0x04;
    private const int Bar5Offset = 0x24;
    private const uint MemorySpaceEnable = 1u << 1;
    private const uint AbarAddress = 0xFEB00000;
    private const uint Port0SataStatus = 0x100 + 0x28;    // P0SSTS
    private const uint DeviceDetectedAndLinkUp = 3;       // SSTS.DET = 3

    [Fact]
    public void TheControllerAppearsAtPciDevice6WithTheAhciClassCode()
    {
        using Machine machine = SataMachine();

        Assert.Equal(AhciIdentity, machine.Pci.ReadConfiguration(AhciFunction, 0, 4));
        Assert.Equal(MassStorageSataAhci, machine.Pci.ReadConfiguration(AhciFunction, 8, 4) >> 8);
    }

    /// <summary>
    /// The POST routes INTA of device 6 through PIRQA (the rotation device 2
    /// also uses) and writes the IRQ into the Interrupt Line register (3Ch).
    /// </summary>
    [Fact]
    public void ThePostRoutesTheInterruptAndRecordsItInTheInterruptLine()
    {
        const uint PirqaDefaultIrq = 11;
        using Machine machine = SataMachine();

        Assert.Equal(PirqaDefaultIrq, machine.Pci.ReadConfiguration(AhciFunction, InterruptLineOffset, 1));
    }

    /// <summary>A disk attached to port 0 reads SSTS.DET = 3 through the ABAR once the guest maps BAR5 and enables memory decode.</summary>
    [Fact]
    public void AnAttachedDiskShowsAsALinkOnItsPort()
    {
        using Machine machine = SataMachine();
        machine.AttachSataDisk(0, DiskImage.CreateInMemory(0x80, 20, 16, 63));
        MapAbar(machine);

        Assert.Equal(DeviceDetectedAndLinkUp, machine.Memory.ReadDword(AbarAddress + Port0SataStatus) & 0xF);
    }

    [Fact]
    public void AttachingASataDiskWithoutAControllerIsRefused()
    {
        using var machine = new Machine();

        Assert.Throws<InvalidOperationException>(
            () => machine.AttachSataDisk(0, DiskImage.CreateInMemory(0x80, 20, 16, 63)));
    }

    /// <summary>The controller's registers travel in the snapshot under section "AHCI".</summary>
    [Fact]
    public void ASnapshotCarriesTheControllerRegisters()
    {
        const uint GlobalHostControl = 0x04;
        const uint InterruptEnable = 1u << 1;
        string path = Path.GetTempFileName();
        try
        {
            using (Machine source = SataMachine())
            {
                MapAbar(source);
                source.Memory.WriteDword(AbarAddress + GlobalHostControl,
                    source.Memory.ReadDword(AbarAddress + GlobalHostControl) | InterruptEnable);
                MachineSnapshot.Capture(source).Save(path);
            }

            using Machine target = SataMachine();
            MachineSnapshot.Load(path).RestoreTo(target);
            MapAbar(target);

            const uint AhciEnable = 1u << 31;   // GHC.AE: reads 1 on an AHCI-only controller
            Assert.Equal(AhciEnable | InterruptEnable, target.Memory.ReadDword(AbarAddress + GlobalHostControl));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void VmJsonHardwareSataBuildsTheControllerWithItsDisks()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"aurora-sata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string diskPath = Path.Combine(folder, "sata.img");
            using (DiskImage.CreateHardDisk(diskPath, 8 * 1024 * 1024))
            {
            }

            var configuration = new VmConfiguration
            {
                Hardware =
                {
                    Sata = new VmConfiguration.SataSettings
                    {
                        Disks = { new VmConfiguration.SataDiskEntry { Port = 2, Path = diskPath } },
                    },
                },
            };
            configuration.Save(folder);

            using Machine machine = VmConfiguration.Load(folder).BuildMachine();
            machine.RunInstructions(200);
            MapAbar(machine);

            const uint Port2SataStatus = 0x100 + 2 * 0x80 + 0x28;
            Assert.NotNull(machine.Sata);
            Assert.Equal(DeviceDetectedAndLinkUp, machine.Memory.ReadDword(AbarAddress + Port2SataStatus) & 0xF);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void VmJsonNamingASoundCardAsTheSataControllerIsRejected()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"aurora-sata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var configuration = new VmConfiguration
            {
                Hardware = { Sata = new VmConfiguration.SataSettings { Card = "sb16" } },
            };
            configuration.Save(folder);

            Assert.Throws<InvalidDataException>(() => VmConfiguration.Load(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AttachingTwoDisksToOnePortIsRefused()
    {
        using Machine machine = SataMachine();
        machine.AttachSataDisk(1, DiskImage.CreateInMemory(0x80, 20, 16, 63));

        Assert.Throws<InvalidOperationException>(
            () => machine.AttachSataDisk(1, DiskImage.CreateInMemory(0x80, 20, 16, 63)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    public void APortOutsideZeroToFiveIsRefused(int port)
    {
        using Machine machine = SataMachine();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => machine.AttachSataDisk(port, DiskImage.CreateInMemory(0x80, 20, 16, 63)));
    }

    /// <summary>
    /// Disposing the machine closes the SATA images, so the same VM can be
    /// built again in the same process: the image file opens a second time.
    /// </summary>
    [Fact]
    public void DisposingTheMachineReleasesTheSataImage()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"aurora-sata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string diskPath = Path.Combine(folder, "sata.img");
            using (DiskImage.CreateHardDisk(diskPath, 8 * 1024 * 1024))
            {
            }

            using (Machine first = SataMachine())
            {
                first.AttachSataDisk(0, DiskImage.OpenHardDisk(diskPath));
            }

            using FileStream reopened = File.Open(diskPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(reopened.CanWrite);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void VmJsonWithTwoDisksOnOnePortIsRejected()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"aurora-sata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var configuration = new VmConfiguration
            {
                Hardware =
                {
                    Sata = new VmConfiguration.SataSettings
                    {
                        Disks =
                        {
                            new VmConfiguration.SataDiskEntry { Port = 0, Path = "a.img" },
                            new VmConfiguration.SataDiskEntry { Port = 0, Path = "b.img" },
                        },
                    },
                },
            };
            configuration.Save(folder);

            Assert.Throws<InvalidDataException>(() => VmConfiguration.Load(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static Machine SataMachine()
    {
        var hardware = new HardwareConfiguration
        {
            Sata = new CardSettings { Card = "ahci-ich9" },
        };

        var machine = new Machine(16 * 1024 * 1024, hardware);
        machine.RunInstructions(200);   // the POST, which routes the PCI interrupts
        return machine;
    }

    /// <summary>What a driver does first: place BAR5 (ABAR) and turn on memory decode in the command register.</summary>
    private static void MapAbar(Machine machine)
    {
        // A protected-mode OS runs with A20 open; with it closed ABAR
        // (FEB00000h, bit 20 set) would alias FEA00000h.
        machine.Memory.A20Enabled = true;
        machine.Pci.WriteConfiguration(AhciFunction, Bar5Offset, 4, AbarAddress);
        machine.Pci.WriteConfiguration(AhciFunction, CommandOffset, 2, MemorySpaceEnable);
    }
}
