using Aurora86.Core.Devices.Audio;

namespace Aurora86.Core.Devices.Cards;

/// <summary>
/// A standalone MPU-401 UART expansion card, the small MIDI interface DOS
/// sequencers and early Windows drivers configured at 330h with an IRQ.
/// The card exposes raw UART bytes only; host MIDI endpoints remain outside
/// the guest device and are attached through <see cref="Mpu401Uart.SetSink"/>.
/// </summary>
public sealed class Mpu401Card : IExpansionCard
{
    public const int CurrentSnapshotVersion = Mpu401Uart.SnapshotVersion;
    public const string SnapshotSectionId = "MPU4";

    private readonly CardSettings _settings;
    private Mpu401Uart? _device;

    /// <summary>Builds an unattached card whose configured port and IRQ are used literally.</summary>
    public Mpu401Card(CardSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public string Kind => "sound";
    public string Model => "mpu401-uart";
    public string SnapshotId => SnapshotSectionId;
    public int SnapshotVersion => CurrentSnapshotVersion;

    /// <summary>The UART after it has claimed the card's configured I/O window.</summary>
    public Mpu401Uart Device => _device
        ?? throw new InvalidOperationException("The MPU-401 card has not been attached yet.");

    /// <summary>Attaches the UART with the exact port and IRQ stored in this card's passport.</summary>
    public void Attach(MachineBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);

        var device = new Mpu401Uart(bus.Interrupts, _settings.Port, _settings.Irq);
        device.Register(bus.Io);
        _device = device;
    }

    /// <summary>Returns an attached UART to its power-on state.</summary>
    public void Reset()
    {
        _device?.Reset();
    }

    /// <summary>Retries a sink that previously applied backpressure; UART serial timing is not modeled.</summary>
    public void Advance(double microseconds)
    {
        _device?.DrainOutput();
    }

    /// <summary>Captures the attached UART's fixed guest-visible state.</summary>
    public byte[] Capture()
    {
        return Device.Capture();
    }

    /// <summary>Restores a validated UART snapshot through the attached device.</summary>
    public void Restore(byte[] payload, int version)
    {
        Device.Restore(payload, version);
    }
}
