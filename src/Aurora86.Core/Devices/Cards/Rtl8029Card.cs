using Aurora86.Core.Devices.Network;
using Aurora86.Core.Devices.Network.Nat;
using Aurora86.Core.Devices.Pci;

namespace Aurora86.Core.Devices.Cards;

/// <summary>
/// A 1997 Realtek RTL8029AS PCI Ethernet expansion card. DOS packet drivers
/// and early PCI-aware operating systems see an NE2000-compatible DP8390
/// behind its BAR, while the machine routes its INTA through PIRQD.
/// </summary>
public sealed class Rtl8029Card : IExpansionCard
{
    public const int CurrentSnapshotVersion = Rtl8029.SnapshotVersion;
    public const string SnapshotSectionId = "R829";
    private const ushort FunctionAddress = 0x0028; // Slot five, INTA -> PIRQD.
    private const ushort DefaultPort = 0x0300;
    private const int WindowLength = 32;
    private readonly CardSettings _settings;
    private Rtl8029? _device;

    public Rtl8029Card(CardSettings settings)
    {
        _settings = settings;
    }

    public string Kind => "network";
    public string Model => "rtl8029-pci";
    public string SnapshotId => SnapshotSectionId;
    public int SnapshotVersion => CurrentSnapshotVersion;
    public Rtl8029 Device => _device
        ?? throw new InvalidOperationException("The RTL8029 card has not been attached yet.");

    /// <summary>Validates resources before opening host sockets, then attaches the PCI controller.</summary>
    public void Attach(MachineBus bus)
    {
        ushort port = _settings.Port == 0 ? DefaultPort : _settings.Port;
        if ((port & (WindowLength - 1)) != 0 || !bus.Io.IsFree(port, WindowLength, null!))
        {
            throw new InvalidOperationException("RTL8029 requires a free 32-byte aligned I/O range.");
        }

        if (bus.Pci.InterruptRouter is null || PciInterruptRouter.Link(FunctionAddress, 1) < 0 ||
            bus.Pci.Functions.Any(function => function.Address == FunctionAddress))
        {
            throw new InvalidOperationException("RTL8029 requires free routable PCI function 00:05.0.");
        }

        INetworkBackend backend = BuildBackend();
        try
        {
            _device = new Rtl8029(bus.Pci, bus.Io, backend, ioAddress: port);
        }
        catch
        {
            if (backend is IDisposable disposable)
            {
                disposable.Dispose();
            }

            throw;
        }
    }

    public void Reset()
    {
        _device?.Reset();
    }

    public void Advance(double microseconds)
    {
        _device?.Poll();
    }

    public byte[] Capture()
    {
        return Device.Capture();
    }

    public void Restore(byte[] payload, int version)
    {
        Device.Restore(payload, version);
    }

    /// <summary>Opens the configured wire and closes partial NAT forwards on failure.</summary>
    private INetworkBackend BuildBackend()
    {
        if (_settings.Mode != NetworkMode.Nat)
        {
            return new LoopbackBackend();
        }

        var backend = new NatBackend();
        try
        {
            foreach (PortForward forward in _settings.Forwards)
            {
                backend.Forward(forward.Tcp, forward.HostPort, forward.GuestPort);
            }
        }
        catch
        {
            backend.Dispose();
            throw;
        }

        return backend;
    }
}
