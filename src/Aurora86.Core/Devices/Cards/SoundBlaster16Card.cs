using Aurora86.Core.Devices.Audio;
using Aurora86.Core.Devices.IsaPnp;

namespace Aurora86.Core.Devices.Cards;

/// <summary>
/// The Sound Blaster 16 as a pluggable card: the same <see
/// cref="SoundBlaster16"/> as before -- DSP, CT1745 mixer, OPL3 -- now built
/// from <see cref="CardSettings"/> and attached through <see
/// cref="MachineBus"/> rather than wired into <see cref="Machine"/> by hand.
/// </summary>
public sealed class SoundBlaster16Card : IExpansionCard
{
    private readonly CardSettings _settings;
    private SoundBlaster16? _device;

    public string Kind => "sound";
    public string Model => "sb16";
    /// <summary>The snapshot section this card keeps its state under.</summary>
    public const string SnapshotSectionId = "SB16";

    public string SnapshotId => SnapshotSectionId;
    public int SnapshotVersion => SoundBlaster16.SnapshotVersion;

    /// <summary>The underlying device. Set once <see cref="Attach"/> has run.</summary>
    public SoundBlaster16 Device => _device
        ?? throw new InvalidOperationException("The Sound Blaster card has not been attached yet.");

    public SoundBlaster16Card(CardSettings settings) => _settings = settings;

    public void Attach(MachineBus bus)
    {
        _device = new SoundBlaster16(bus.Interrupts, bus.Dma, _settings.Port)
        {
            Irq = _settings.Irq,
            Dma8 = _settings.Dma,
            Dma16 = _settings.HighDma,
        };
        _device.Mixer.Irq = _settings.Irq;
        _device.Mixer.Dma8 = _settings.Dma;
        _device.Mixer.Dma16 = _settings.HighDma;

        // Registering the card also claims the OPL3's own ports at 0x388.
        _device.Register(bus.Io);

        // Plug and Play as well as being at its usual address: DOS finds it
        // where it expects, and Windows 95 finds it by asking the bus, and
        // may move it.
        bus.IsaPlugAndPlay.Add(IsaPnpCards.ForSoundBlaster16(_device));

        // Joins the host mix (F1.6): DMA-driven digital audio and the OPL3
        // both leave the card through the same ring buffer (see
        // SoundBlaster16.ReadSamples), so one registration carries both.
        bus.Audio.AddSource(_device);
    }

    public void Reset() => _device?.Reset();
    public void Advance(double microseconds) => _device?.Advance(microseconds);
    public byte[] Capture() => Device.Capture();
    public void Restore(byte[] payload, int version) => Device.Restore(payload, version);
}
