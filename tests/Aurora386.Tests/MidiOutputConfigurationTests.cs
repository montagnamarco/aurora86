using Aurora386.Core;
using Aurora386.Core.Configuration;
using Aurora386.Core.Devices.Audio;

namespace Aurora386.Tests;

/// <summary>
/// Host MIDI output is an explicit opt-in beside the guest's MPU UART.
/// Invalid machine descriptions must fail before opening any native endpoint.
/// </summary>
public class MidiOutputConfigurationTests
{
    [Fact]
    public void AnMpuWithoutHostOutputKeepsTheSilentSink()
    {
        VmConfiguration configuration = ConfigurationWithMpu();
        using Machine machine = configuration.BuildMachine();

        Assert.Null(configuration.MidiOutput);
        Assert.Same(NullMidiSink.Instance, machine.Midi!.Sink);
    }

    [Fact]
    public void HostOutputRequiresAnExplicitMpuCard()
    {
        var configuration = new VmConfiguration
        {
            RamMegabytes = 4,
            MidiOutput = new VmConfiguration.MidiOutputSettings { DeviceId = 0 },
        };

        InvalidDataException error = Assert.Throws<InvalidDataException>(configuration.BuildMachine);
        Assert.Contains("MPU-401", error.Message);
    }

    [Fact]
    public void HostOutputWithoutAnIdDoesNotChooseTheDefaultSynthesizer()
    {
        VmConfiguration configuration = ConfigurationWithMpu();
        configuration.MidiOutput = new VmConfiguration.MidiOutputSettings();

        InvalidDataException error = Assert.Throws<InvalidDataException>(configuration.BuildMachine);
        Assert.Contains("deviceId", error.Message);
    }

    [Fact]
    public void MissingGuestMediaFailsBeforeHostOutputIsOpened()
    {
        VmConfiguration configuration = ConfigurationWithMpu();
        configuration.MidiOutput = new VmConfiguration.MidiOutputSettings { DeviceId = uint.MaxValue };
        configuration.Disks.Add(new VmConfiguration.DiskEntry
        {
            Path = Path.Combine(Path.GetTempPath(), $"missing-midi-test-{Guid.NewGuid():N}.img"),
            Drive = 0x80,
        });

        Assert.Throws<FileNotFoundException>(configuration.BuildMachine);
    }

    [Fact]
    public void EndpointSelectionSurvivesSavingWithoutOpeningTheEndpoint()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"aurora-midi-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            VmConfiguration configuration = ConfigurationWithMpu();
            configuration.MidiOutput = new VmConfiguration.MidiOutputSettings { DeviceId = 7 };
            configuration.Save(folder);

            VmConfiguration loaded = VmConfiguration.Load(folder);
            Assert.Equal(7u, loaded.MidiOutput!.DeviceId);
            Assert.Contains(loaded.Hardware.Sound, card => card.Card == "mpu401-uart");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    /// <summary>Describes a small guest with the standalone UART and no native output.</summary>
    private static VmConfiguration ConfigurationWithMpu()
    {
        var configuration = new VmConfiguration { RamMegabytes = 4 };
        configuration.Hardware.Sound.Add(new VmConfiguration.SoundCardEntry
        {
            Card = "mpu401-uart",
            Port = "330",
            Irq = 9,
        });
        return configuration;
    }
}
