using Aurora86.Core;
using Aurora86.Core.Configuration;
using Aurora86.Core.Devices.Audio;
using Aurora86.Core.Devices.Cards;

namespace Aurora86.Tests;

/// <summary>
/// A standalone early-1990s MPU UART fitted beside a Sound Blaster, with
/// fixed ISA resources and a host-owned output stream across snapshots.
/// </summary>
public class Mpu401MachineTests
{
    private const uint RamSize = 4 * 1024 * 1024;

    [Fact]
    public void DefaultHardwareHasNoMpuAndExplicitConfigurationAddsOneBesideTheSoundBlaster()
    {
        using var defaultMachine = new Machine(RamSize);
        Assert.Null(defaultMachine.Midi);
        Assert.Equal(0xFF, defaultMachine.Io.ReadByte(0x331));

        var configuration = new VmConfiguration();
        configuration.Hardware.Sound.Add(new VmConfiguration.SoundCardEntry
        {
            Card = "mpu401-uart",
            Port = "330",
            Irq = 9,
        });
        using Machine machine = configuration.BuildMachine();
        Assert.NotNull(machine.SoundBlaster);
        Assert.Equal((ushort)0x330, machine.Midi!.BasePort);
        Assert.Equal(9, machine.Midi.Irq);
        machine.Io.WriteByte(0x331, 0xFF);
        Assert.Equal(0xFE, machine.Io.ReadByte(0x330));
    }

    [Theory]
    [InlineData(false, 0x220)]
    [InlineData(true, 0x220)]
    [InlineData(false, 0x388)]
    [InlineData(true, 0x388)]
    public void MpuCannotOverlapSoundBlasterDspOrOplRegardlessOfAttachmentOrder(bool mpuFirst, int port)
    {
        HardwareConfiguration hardware = Configuration((ushort)port);
        hardware.Sound.Insert(mpuFirst ? 1 : 0, new CardSettings
        {
            Card = "sb16", Port = 0x220, Irq = 5, Dma = 1, HighDma = 5,
        });

        Assert.Throws<InvalidOperationException>(() => new Machine(RamSize, hardware));
    }

    [Fact]
    public void LaterIsaNetworkRegistrationCannotOverwriteTheMpuWindow()
    {
        HardwareConfiguration hardware = Configuration(0x300);
        hardware.Network.Add(new CardSettings { Card = "ne2000-isa", Port = 0x300, Irq = 3 });

        Assert.Throws<InvalidOperationException>(() => new Machine(RamSize, hardware));
    }

    [Fact]
    public void DuplicateMpuCardsCannotShareOneSnapshotSection()
    {
        HardwareConfiguration hardware = Configuration();
        hardware.Sound.Add(new CardSettings { Card = "mpu401-uart", Port = 0x332, Irq = 7 });

        ArgumentException error = Assert.Throws<ArgumentException>(() => new Machine(RamSize, hardware));
        Assert.Contains("MPU4", error.Message);
    }

    [Fact]
    public void MachineOwnsSinkReplacementAndShutdown()
    {
        var machine = new Machine(RamSize, Configuration());
        var first = new RecordingSink();
        var second = new RecordingSink();
        machine.ConnectMidi(first);
        machine.ConnectMidi(first);
        Assert.Equal(1, first.Resets);

        machine.ConnectMidi(second);
        Assert.Equal(2, first.Resets);
        Assert.Equal(1, first.Disposals);
        Assert.Same(second, machine.Midi!.Sink);
        machine.Dispose();
        Assert.Equal(2, second.Resets);
        Assert.Equal(1, second.Disposals);
    }

    [Fact]
    public void ConnectingWithoutAnMpuClosesTheUnusedSink()
    {
        using var machine = new Machine(RamSize, new HardwareConfiguration());
        var sink = new RecordingSink();

        machine.ConnectMidi(sink);

        Assert.Equal(1, sink.Disposals);
    }

    [Fact]
    public void SnapshotKeepsUnacceptedBytesAndTheTargetsSinkWithoutSendingDuringRestore()
    {
        using var source = new Machine(RamSize, Configuration());
        var sourceSink = new RecordingSink { Accept = false };
        source.ConnectMidi(sourceSink);
        source.Io.WriteByte(0x331, 0xFF);
        source.Io.WriteByte(0x331, 0x3F);
        source.Io.WriteByte(0x330, 0x90); // Note-on status and its two data bytes.
        source.Io.WriteByte(0x330, 60);
        source.Io.WriteByte(0x330, 100);
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        using var target = new Machine(RamSize, Configuration());
        var targetSink = new RecordingSink();
        target.ConnectMidi(targetSink);

        snapshot.RestoreTo(target);

        Assert.Same(targetSink, target.Midi!.Sink);
        Assert.Empty(targetSink.Bytes);
        Assert.Equal(2, targetSink.Resets);
        Assert.Equal(0xFE, target.Io.ReadByte(0x330));
        Assert.Equal(0xFE, target.Io.ReadByte(0x330));
        Mpu401Card card = Assert.Single(target.Cards.OfType<Mpu401Card>());
        card.Advance(1);
        Assert.Equal(new byte[] { 0x90, 60, 100 }, targetSink.Bytes);
    }

    [Theory]
    [InlineData(0x332, 9)]
    [InlineData(0x330, 7)]
    public void SnapshotResourceMismatchFailsBeforeRamOrTheTargetSinkChanges(int port, int irq)
    {
        using var source = new Machine(RamSize, Configuration());
        source.Memory.WriteByte(0x1000, 0xAA);
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        using var target = new Machine(RamSize, Configuration((ushort)port, irq));
        target.Memory.WriteByte(0x1000, 0x55);
        var sink = new RecordingSink();
        target.ConnectMidi(sink);
        int resets = sink.Resets;

        Assert.Throws<InvalidDataException>(() => snapshot.RestoreTo(target));

        Assert.Equal(0x55, target.Memory.ReadByte(0x1000));
        Assert.Equal(resets, sink.Resets);
    }

    [Fact]
    public void MalformedModeInASavedFileIsRejectedBeforeRestoration()
    {
        using var machine = new Machine(RamSize, Configuration());
        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(machine).Save(path);
            byte[] bytes = File.ReadAllBytes(path);
            int position = 8; // Snapshot magic.
            bool found = false;
            while (position < bytes.Length)
            {
                string name = System.Text.Encoding.ASCII.GetString(bytes, position, 4);
                int length = BitConverter.ToInt32(bytes, position + 6);
                int payload = position + 10;
                if (name == Mpu401Card.SnapshotSectionId)
                {
                    bytes[payload + 3] = 2; // UART mode must be canonical zero or one.
                    found = true;
                    break;
                }

                position = payload + length;
            }

            Assert.True(found);
            File.WriteAllBytes(path, bytes);
            Assert.Throws<InvalidDataException>(() => MachineSnapshot.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Uses explicit resources; a card entry never borrows the Sound Blaster defaults.</summary>
    private static HardwareConfiguration Configuration(ushort port = 0x330, int irq = 9)
    {
        return new HardwareConfiguration
        {
            Sound = { new CardSettings { Card = "mpu401-uart", Port = port, Irq = irq } },
        };
    }

    /// <summary>Records byte ownership and host lifetime without a native MIDI endpoint.</summary>
    private sealed class RecordingSink : IMidiSink, IDisposable
    {
        public bool Accept { get; set; } = true;
        public List<byte> Bytes { get; } = [];
        public int Resets { get; private set; }
        public int Disposals { get; private set; }

        public bool TryWriteByte(byte value)
        {
            if (!Accept)
            {
                return false;
            }

            Bytes.Add(value);
            return true;
        }

        public void Reset()
        {
            Resets++;
        }

        public void Dispose()
        {
            Disposals++;
        }
    }
}
