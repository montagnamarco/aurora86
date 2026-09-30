using System.Text.Json;
using Aurora86.Core;
using Aurora86.Core.Configuration;

namespace Aurora86.Tests;

/// <summary>
/// vm.json version 2: the <c>hardware</c> object that carries the expansion
/// cards, and the conversion from version 1. See
/// <c>plans/specs/device-registry.md</c>.
/// </summary>
public class VmConfigurationV2Tests
{
    private static string TempFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"aurora-vm2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void AVersion1FileWithNoHardwareObjectBuildsTodaysMachine()
    {
        string folder = TempFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, VmConfiguration.FileName),
                              """{ "version": 1, "name": "Old" }""");

            VmConfiguration loaded = VmConfiguration.Load(folder);

            Assert.Equal(2, loaded.Version);
            Assert.Single(loaded.Hardware.Sound);
            Assert.Equal("sb16", loaded.Hardware.Sound[0].Card);
            Assert.Equal("i430fx", loaded.Hardware.Chipset);

            using Machine machine = loaded.BuildMachine();
            Assert.NotNull(machine.SoundBlaster);
            Assert.Equal(0x220, machine.SoundBlaster!.BasePort);
            Assert.Equal(5, machine.SoundBlaster.Irq);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void SavingAlwaysWritesVersionTwoWithAHardwareObject()
    {
        string folder = TempFolder();
        try
        {
            new VmConfiguration().Save(folder);

            string json = File.ReadAllText(Path.Combine(folder, VmConfiguration.FileName));
            using JsonDocument document = JsonDocument.Parse(json);

            Assert.Equal(2, document.RootElement.GetProperty("version").GetInt32());

            JsonElement hardware = document.RootElement.GetProperty("hardware");
            Assert.Equal("i430fx", hardware.GetProperty("chipset").GetString());

            JsonElement sound = hardware.GetProperty("sound");
            Assert.Equal(1, sound.GetArrayLength());
            Assert.Equal("sb16", sound[0].GetProperty("card").GetString());
            Assert.Equal("220", sound[0].GetProperty("port").GetString());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ASoundCardAtANonDefaultAddressInTheHardwareObjectReachesBuildMachine()
    {
        string folder = TempFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, VmConfiguration.FileName), """
                {
                  "version": 2,
                  "hardware": {
                    "chipset": "i430fx",
                    "sound": [ { "card": "sb16", "port": "240", "irq": 7, "dma": 1, "highDma": 5 } ]
                  }
                }
                """);

            using Machine machine = VmConfiguration.Load(folder).BuildMachine();

            Assert.Equal(0x240, machine.SoundBlaster!.BasePort);
            Assert.Equal(7, machine.SoundBlaster.Irq);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void AnEmptySoundListBuildsAMachineWithNoSoundCard()
    {
        string folder = TempFolder();
        try
        {
            // The network list still defaults to today's NE2000 when the
            // file does not mention it, so this leaves only sound empty --
            // see AnEmptySoundAndNetworkListBuildsAMachineWithNoCardsAtAll
            // for both lists cleared at once.
            File.WriteAllText(Path.Combine(folder, VmConfiguration.FileName),
                """{ "version": 2, "hardware": { "chipset": "i430fx", "sound": [] } }""");

            using Machine machine = VmConfiguration.Load(folder).BuildMachine();

            Assert.Null(machine.SoundBlaster);
            Assert.NotNull(machine.Network);
            Assert.Single(machine.Cards);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void AnEmptySoundAndNetworkListBuildsAMachineWithNoCardsAtAll()
    {
        string folder = TempFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, VmConfiguration.FileName),
                """{ "version": 2, "hardware": { "chipset": "i430fx", "sound": [], "network": [] } }""");

            using Machine machine = VmConfiguration.Load(folder).BuildMachine();

            Assert.Null(machine.SoundBlaster);
            Assert.Null(machine.Network);
            Assert.Empty(machine.Cards);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void AnUnknownChipsetIsRefused()
    {
        string folder = TempFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, VmConfiguration.FileName),
                """{ "version": 2, "hardware": { "chipset": "nForce2" } }""");

            Assert.Throws<InvalidDataException>(() => VmConfiguration.Load(folder));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void AVersion1FilesNetworkSettingsConvertIntoHardwareNetwork()
    {
        string folder = TempFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, VmConfiguration.FileName), """
                {
                  "version": 1,
                  "network": {
                    "mode": "Nat",
                    "forwards": [ { "protocol": "tcp", "hostPort": 8080, "guestPort": 80 } ]
                  }
                }
                """);

            VmConfiguration loaded = VmConfiguration.Load(folder);

            Assert.Equal(2, loaded.Version);
            VmConfiguration.NetworkCardEntry card = Assert.Single(loaded.Hardware.Network);
            Assert.Equal("ne2000-isa", card.Card);
            Assert.Equal("300", card.Port);
            Assert.Equal(3, card.Irq);
            Assert.Equal(VmConfiguration.NetworkMode.Nat, card.Mode);
            VmConfiguration.PortForward forward = Assert.Single(card.Forwards);
            Assert.Equal(8080, forward.HostPort);
            Assert.Equal(80, forward.GuestPort);

            using Machine machine = loaded.BuildMachine();
            Assert.NotNull(machine.Network);
            Assert.IsType<Aurora86.Core.Devices.Network.Nat.NatBackend>(machine.Network!.Backend);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ASavedVersion2FileLoadsBackToTheSameMachine()
    {
        string folder = TempFolder();
        try
        {
            var original = new VmConfiguration();
            original.Hardware.Sound.Clear();
            original.Hardware.Sound.Add(new VmConfiguration.SoundCardEntry
            {
                Card = "sb16",
                Port = "240",
                Irq = 7,
                Dma = 1,
                HighDma = 5,
            });
            original.Save(folder);

            VmConfiguration loaded = VmConfiguration.Load(folder);
            Assert.Equal(2, loaded.Version);

            using Machine machine = loaded.BuildMachine();
            Assert.Equal(0x240, machine.SoundBlaster!.BasePort);
            Assert.Equal(7, machine.SoundBlaster.Irq);
        }
        finally { Directory.Delete(folder, true); }
    }

    // ---- F1.4 review: D1, a failing BuildMachine must not leak host resources ----

    [Fact]
    public void ABuildMachineFailureAfterTheNatCardIsAttachedClosesItsListeningSocketAgain()
    {
        string folder = TempFolder();
        try
        {
            int hostPort = FindFreeTcpPort();
            File.WriteAllText(Path.Combine(folder, VmConfiguration.FileName), $$"""
                {
                  "version": 2,
                  "disks": [ { "path": "no-such-disk.img", "drive": 128 } ],
                  "hardware": {
                    "chipset": "i430fx",
                    "network": [
                      {
                        "card": "ne2000-isa", "port": "300", "irq": 3, "mode": "Nat",
                        "forwards": [ { "protocol": "tcp", "hostPort": {{hostPort}}, "guestPort": 80 } ]
                      }
                    ]
                  }
                }
                """);

            VmConfiguration configuration = VmConfiguration.Load(folder);

            // The NAT card attaches (and starts listening on hostPort) as
            // part of building the machine; the missing disk image is only
            // discovered afterwards, while BuildMachine is still running.
            Assert.Throws<FileNotFoundException>(() => configuration.BuildMachine());

            // The half-built machine must have been disposed along with it:
            // the same host port can be bound again immediately, rather than
            // staying occupied by a machine nobody holds a reference to.
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, hostPort);
            listener.Start();
            listener.Stop();
        }
        finally { Directory.Delete(folder, true); }
    }

    private static int FindFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // ---- F1.4 review: D6, configuration robustness ----

    [Fact]
    public void ANullSoundAndNetworkListInAVersion2FileMeanNoCardOfThatKind()
    {
        string folder = TempFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, VmConfiguration.FileName),
                """{ "version": 2, "hardware": { "chipset": "i430fx", "sound": null, "network": null } }""");

            VmConfiguration loaded = VmConfiguration.Load(folder);

            Assert.Empty(loaded.Hardware.Sound);
            Assert.Empty(loaded.Hardware.Network);

            using Machine machine = loaded.BuildMachine();
            Assert.Null(machine.SoundBlaster);
            Assert.Null(machine.Network);
            Assert.Empty(machine.Cards);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void AnUnknownCardNameInHardwareSoundIsRefusedAtLoad()
    {
        string folder = TempFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, VmConfiguration.FileName),
                """{ "version": 2, "hardware": { "chipset": "i430fx", "sound": [ { "card": "rtl-fake", "port": "220" } ] } }""");

            Assert.Throws<InvalidDataException>(() => VmConfiguration.Load(folder));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ANetworkCardNamedInHardwareSoundIsRefusedAsTheWrongKindAtLoad()
    {
        string folder = TempFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, VmConfiguration.FileName),
                """{ "version": 2, "hardware": { "chipset": "i430fx", "sound": [ { "card": "ne2000-isa", "port": "300" } ] } }""");

            Assert.Throws<InvalidDataException>(() => VmConfiguration.Load(folder));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void AVersion2FilesTopLevelLegacyFieldsAreIgnored()
    {
        string folder = TempFolder();
        try
        {
            // A version-2 file whose "hardware.usb" explicitly says none, but
            // whose leftover top-level "usbEnabled" (every file round-trips
            // it, since Save always writes every property) still says true.
            // The hardware object is the source of truth from version 2 on.
            File.WriteAllText(Path.Combine(folder, VmConfiguration.FileName),
                """{ "version": 2, "usbEnabled": true, "hardware": { "chipset": "i430fx" } }""");

            VmConfiguration loaded = VmConfiguration.Load(folder);

            Assert.Null(loaded.Hardware.Usb);

            using Machine machine = loaded.BuildMachine();
            Assert.Null(machine.Usb);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void AConfigurationBuiltInCodeWithOnlyTheLegacyUsbFieldStillEnablesUsbThroughBuildMachine()
    {
        // Never loaded -- Migrate never runs -- so BuildMachine itself must
        // apply the same conversion for code that only ever set the
        // pre-registry field, the way it always could.
        var configuration = new VmConfiguration { UsbEnabled = true };

        using Machine machine = configuration.BuildMachine();

        Assert.NotNull(machine.Usb);
    }
}
