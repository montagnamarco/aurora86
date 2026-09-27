using Aurora386.Core;
using Aurora386.Core.Configuration;
using Aurora386.Core.Devices.Cards;
using Aurora386.Core.Devices.Usb;

namespace Aurora386.Tests;

/// <summary>
/// USB as a card in the expansion-card registry: <see cref="UhciCard"/>, its
/// place in <see cref="HardwareConfiguration"/> and in <c>vm.json</c>'s
/// <c>hardware.usb</c>, and its snapshot section. See
/// <c>plans/specs/device-registry.md</c>, <see cref="CardRegistryTests"/> and
/// <see cref="Ne2000CardTests"/> for the same shape applied to the other two
/// cards, and <c>UhciTests.cs</c>/<c>UsbHidTests.cs</c>/
/// <c>UsbMassStorageTests.cs</c> for the controller and device behaviour this
/// does not repeat.
/// </summary>
public class UhciCardTests
{
    private static string TempFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"aurora-uhci-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void ADefaultMachineHasNoUsbCardAtAll()
    {
        // new Machine() must build exactly today's hardware: USB was off
        // until EnableUsb was called by hand, and stays off the same way
        // now that it is a card.
        using var machine = new Machine();

        Assert.DoesNotContain(machine.Cards, c => c.Kind == "usb");
        Assert.Null(machine.Usb);
        Assert.Null(machine.UsbKeyboard);
        Assert.Null(machine.UsbMouse);
        Assert.Null(machine.UsbStorageController);
        Assert.Null(machine.UsbStorage);
    }

    [Fact]
    public void AConfigurationWithUsbFitsBothControllersAtTodaysPciAddressesAndAcceptsStorage()
    {
        var hardware = new HardwareConfiguration { Usb = new CardSettings { Card = "uhci-piix" } };
        using var machine = new Machine(4 * 1024 * 1024, hardware);

        IExpansionCard card = Assert.Single(machine.Cards, c => c.Kind == "usb");
        Assert.Equal("uhci-piix", card.Model);
        Assert.Equal("UHCI", card.SnapshotId);

        Assert.NotNull(machine.Usb);
        Assert.NotNull(machine.UsbKeyboard);
        Assert.NotNull(machine.UsbMouse);
        Assert.NotNull(machine.UsbStorageController);

        // Device 0x10 (keyboard/mouse) and device 0x18 (storage), exactly
        // where Machine.Usb.cs put them by hand before this registry existed.
        Assert.Equal(0x70208086u, machine.Pci.ReadConfiguration(0x10, 0, 4));
        Assert.Equal(0x70208086u, machine.Pci.ReadConfiguration(0x18, 0, 4));

        var device = new UsbMassStorageDevice(new MemoryStream(new byte[4096]));
        machine.AttachUsbStorage(device);

        Assert.Same(device, machine.UsbStorage);
        Assert.Same(device, ((UhciCard)card).Storage);
    }

    [Fact]
    public void EnableUsbAppendsTheCardAtRunTimeForAMachineBuiltWithNone()
    {
        // This is how the GUI's "enable USB" toggle and its "insert image"
        // menu keep working on a machine started with no USB card fitted --
        // see EnableUsb's own summary for the decision.
        using var machine = new Machine();
        Assert.DoesNotContain(machine.Cards, c => c.Kind == "usb");

        machine.EnableUsb();

        IExpansionCard card = Assert.Single(machine.Cards, c => c.Kind == "usb");
        Assert.Equal("UHCI", card.SnapshotId);
        Assert.NotNull(machine.Usb);

        // A second call is a no-op rather than fitting a second card, which
        // would collide on the same snapshot section.
        machine.EnableUsb();
        Assert.Single(machine.Cards, c => c.Kind == "usb");
    }

    [Fact]
    public void AVersion1FilesUsbSettingsConvertIntoHardwareUsb()
    {
        string folder = TempFolder();
        try
        {
            string diskPath = Path.Combine(folder, "stick.img");
            File.WriteAllBytes(diskPath, new byte[4096]);

            File.WriteAllText(Path.Combine(folder, VmConfiguration.FileName), $$"""
                {
                  "version": 1,
                  "usbEnabled": true,
                  "usbStorage": { "path": "stick.img", "readOnly": true }
                }
                """);

            VmConfiguration loaded = VmConfiguration.Load(folder);

            Assert.Equal(2, loaded.Version);
            Assert.NotNull(loaded.Hardware.Usb);
            Assert.Equal("uhci-piix", loaded.Hardware.Usb!.Card);
            Assert.Equal(diskPath, loaded.Hardware.Usb.Storage!.Path);
            Assert.True(loaded.Hardware.Usb.Storage.ReadOnly);

            using Machine machine = loaded.BuildMachine();
            Assert.NotNull(machine.Usb);
            Assert.NotNull(machine.UsbStorage);
            Assert.True(machine.UsbStorage!.ReadOnly);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void AVersion1FileWithUsbNeverEnabledBuildsAMachineWithNoUsbCard()
    {
        string folder = TempFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, VmConfiguration.FileName),
                              """{ "version": 1, "name": "Old" }""");

            VmConfiguration loaded = VmConfiguration.Load(folder);

            Assert.Equal(2, loaded.Version);
            Assert.Null(loaded.Hardware.Usb);

            using Machine machine = loaded.BuildMachine();
            Assert.Null(machine.Usb);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void TheUsbCardsControllerRegistersSurviveASnapshotFile()
    {
        const ushort Base = 0xC100;
        var hardware = new HardwareConfiguration { Usb = new CardSettings { Card = "uhci-piix" } };
        using var machine = new Machine(4 * 1024 * 1024, hardware);

        machine.Io.WriteWord(Base + 6, 0x123); // FRNUM, while the controller is stopped

        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(machine).Save(path);

            // Disturb the value so the restore, not a coincidence, is what
            // the assertion below is checking.
            machine.Io.WriteWord(Base + 6, 0);

            MachineSnapshot.Load(path).RestoreTo(machine);

            Assert.Equal(0x123, machine.Io.ReadWord(Base + 6));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ASnapshotTakenWithNoUsbCardRestoresAMachineWithNoneEither()
    {
        using var machine = new Machine(4 * 1024 * 1024, new HardwareConfiguration());

        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(machine).Save(path);
            MachineSnapshot.Load(path).RestoreTo(machine);

            Assert.DoesNotContain(machine.Cards, c => c.Kind == "usb");
            Assert.Null(machine.Usb);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ValidateRefusesAUsbCardSectionNewerThanThisBuildBeforeRamIsRestored()
    {
        var hardware = new HardwareConfiguration { Usb = new CardSettings { Card = "uhci-piix" } };
        using var machine = new Machine(4 * 1024 * 1024, hardware);

        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(machine).Save(path);
            BumpSectionVersion(path, "UHCI", (ushort)(UhciCard.CurrentSnapshotVersion + 1));

            Assert.Throws<InvalidDataException>(() => MachineSnapshot.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AnOldSnapshotWithAUsbSectionStillRestoresOntoTheCard()
    {
        const ushort Base = 0xC100;
        var hardware = new HardwareConfiguration { Usb = new CardSettings { Card = "uhci-piix" } };
        using var machine = new Machine(4 * 1024 * 1024, hardware);
        machine.Io.WriteWord(Base + 6, 0x123);

        string path = Path.GetTempFileName();
        try
        {
            // The layout before F1.4c: USB's own section, named "USB ",
            // inside the container rather than under the card's own name.
            LegacySnapshotFile.Write(machine, path, LegacySnapshotEnd.AfterUsb);

            machine.Io.WriteWord(Base + 6, 0);

            MachineSnapshot.Load(path).RestoreTo(machine);

            Assert.Equal(0x123, machine.Io.ReadWord(Base + 6));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AnOldSnapshotWithAnEmptyUsbSectionStillRestoresAMachineWithNoUsbCard()
    {
        using var machine = new Machine();

        string path = Path.GetTempFileName();
        try
        {
            LegacySnapshotFile.Write(machine, path, LegacySnapshotEnd.AfterUsb);

            MachineSnapshot.Load(path).RestoreTo(machine);

            Assert.Null(machine.Usb);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Rewrites one section's version field in a saved snapshot file, leaving
    /// the magic word, every other section and every payload exactly as they
    /// were. Mirrors <see cref="Ne2000CardTests"/>'s own private copy of this
    /// helper.
    /// </summary>
    private static void BumpSectionVersion(string path, string sectionName, ushort newVersion)
    {
        byte[] bytes = File.ReadAllBytes(path);
        const int magicLength = 8;
        int position = magicLength;

        while (position < bytes.Length)
        {
            string name = System.Text.Encoding.ASCII.GetString(bytes, position, 4);
            int versionAt = position + 4;
            int lengthAt = versionAt + 2;
            int length = BitConverter.ToInt32(bytes, lengthAt);
            int payloadAt = lengthAt + 4;

            if (name == sectionName)
            {
                bytes[versionAt] = (byte)(newVersion & 0xFF);
                bytes[versionAt + 1] = (byte)(newVersion >> 8);
                File.WriteAllBytes(path, bytes);
                return;
            }

            position = payloadAt + length;
        }

        throw new InvalidOperationException($"Snapshot has no '{sectionName}' section.");
    }
}
