using Aurora86.Core.Devices.IsaPnp;
using Aurora86.Core.Devices.Network;
using Aurora86.Core.Devices.Network.Nat;

namespace Aurora86.Core.Devices.Cards;

/// <summary>
/// The NE2000 as a pluggable card: the same <see cref="Ne2000"/> as before --
/// remote DMA, the receive ring, the DP8390 register file -- now built from
/// <see cref="CardSettings"/> and attached through <see cref="MachineBus"/>
/// rather than wired into <see cref="Machine"/> by hand.
///
/// The wire it is plugged into follows straight from <see
/// cref="CardSettings.Mode"/>: <see cref="NetworkMode.None"/> gets a <see
/// cref="LoopbackBackend"/>, exactly what a fresh card without a cable
/// plugged in has always answered with here, and <see cref="NetworkMode.Nat"/>
/// gets a <see cref="NatBackend"/> with whatever ports were asked forwarded.
/// </summary>
public sealed class Ne2000Card : IExpansionCard
{
    private readonly CardSettings _settings;
    private Ne2000? _device;

    public string Kind => "network";
    public string Model => "ne2000-isa";
    public string SnapshotId => "NE2K";
    public int SnapshotVersion => Ne2000.SnapshotVersion;

    /// <summary>The underlying device. Set once <see cref="Attach"/> has run.</summary>
    public Ne2000 Device => _device
        ?? throw new InvalidOperationException("The NE2000 card has not been attached yet.");

    public Ne2000Card(CardSettings settings) => _settings = settings;

    public void Attach(MachineBus bus)
    {
        _device = new Ne2000(bus.Interrupts, BuildBackend(), _settings.Port, _settings.Irq);
        _device.Register(bus.Io);

        // Plug and Play as well as being at its usual address: DOS finds it
        // where it expects, and Windows 95 finds it by asking the bus, and
        // may move it.
        bus.IsaPlugAndPlay.Add(IsaPnpCards.ForNe2000(_device));
    }

    private INetworkBackend BuildBackend()
    {
        if (_settings.Mode != NetworkMode.Nat) return new LoopbackBackend();

        var nat = new NatBackend();
        foreach (PortForward forward in _settings.Forwards)
            nat.Forward(forward.Tcp, forward.HostPort, forward.GuestPort);
        return nat;
    }

    public void Reset() => _device?.Reset();

    /// <summary>
    /// Collects anything that arrived on the wire. A real card would act the
    /// instant a frame appeared; polling here, once per batch of executed
    /// instructions, is close enough, because the guest cannot observe the
    /// difference -- it only ever learns about a frame through the interrupt
    /// the card raises when it files one.
    /// </summary>
    public void Advance(double microseconds) => _device?.Poll();

    public byte[] Capture() => Device.Capture();
    public void Restore(byte[] payload, int version) => Device.Restore(payload, version);
}
