using Aurora386.Core.Devices.Network;
using Aurora386.Core.Devices.Network.Nat;
using Aurora386.Core.Devices.Pci;

namespace Aurora386.Core.Devices.Cards;

/// <summary>
/// A 1997 Realtek RTL8139(C) PCI Fast Ethernet card: the network adapter an
/// in-box Windows 98SE/2000/XP driver or a Linux <c>8139too</c> kernel finds
/// already fitted, bus-mastering its own receive ring and four transmit
/// descriptors directly against guest RAM. See <see cref="Network.Rtl8139"/>
/// for the register engine this wraps.
/// </summary>
public sealed class Rtl8139Card : IExpansionCard
{
    public const int CurrentSnapshotVersion = Rtl8139.SnapshotVersion;
    public const string SnapshotSectionId = "R139";

    /// <summary>
    /// Slot five, INTA -> PIRQD: the same PCI network slot the RTL8029
    /// occupies, since this machine has one physical network expansion slot
    /// and the two cards are alternatives for it, never both fitted at once.
    /// </summary>
    private const ushort FunctionAddress = 0x0028;

    /// <summary>
    /// Unlike the RTL8029's 32-byte window, 256 bytes at the NE2000's
    /// customary 300h would swallow half of the VGA CRTC/DAC ports
    /// (3C0h-3DFh), the IDE control block (3F6h) and COM1 (3F8h) along with
    /// it. A real PCI BIOS would never place a BAR there; this default
    /// mimics that by picking a range the rest of this machine's fixed I/O
    /// devices do not reach -- clear of the bus-master IDE engine at C000h
    /// and both UHCI controllers at C100h/C120h.
    /// </summary>
    private const ushort DefaultPort = 0xC200;

    private const int IoWindowLength = 256;
    private readonly CardSettings _settings;
    private Rtl8139? _device;

    public Rtl8139Card(CardSettings settings)
    {
        _settings = settings;
    }

    public string Kind => "network";
    public string Model => "rtl8139-pci";
    public string SnapshotId => SnapshotSectionId;
    public int SnapshotVersion => CurrentSnapshotVersion;

    public Rtl8139 Device => _device
        ?? throw new InvalidOperationException("The RTL8139 card has not been attached yet.");

    /// <summary>The wire this card is plugged into -- used only so <see cref="Machine"/> can close it once, without depending on the NE2000-shaped legacy network service.</summary>
    public INetworkBackend Backend => Device.Backend;

    /// <summary>Validates resources before opening host sockets, then attaches the PCI controller.</summary>
    public void Attach(MachineBus bus)
    {
        ushort port = _settings.Port == 0 ? DefaultPort : _settings.Port;
        if ((port & (IoWindowLength - 1)) != 0 || !bus.Io.IsFree(port, IoWindowLength, null!))
        {
            throw new InvalidOperationException("RTL8139 requires a free 256-byte aligned I/O range.");
        }

        if (bus.Pci.InterruptRouter is null || PciInterruptRouter.Link(FunctionAddress, 1) < 0 ||
            bus.Pci.Functions.Any(function => function.Address == FunctionAddress))
        {
            throw new InvalidOperationException("RTL8139 requires free routable PCI function 00:05.0.");
        }

        INetworkBackend backend = BuildBackend();
        try
        {
            _device = new Rtl8139(bus.Pci, bus.Io, bus.Memory, backend, FunctionAddress, port);
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
