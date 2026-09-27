namespace Aurora386.Core.Devices.Cards;

/// <summary>How a network card reaches outside the machine.</summary>
public enum NetworkMode
{
    /// <summary>The card is there but its frames go nowhere real: a loopback wire, as if unplugged.</summary>
    None,

    /// <summary>A private network behind the emulator, reaching out through the host's sockets.</summary>
    Nat,
}

/// <summary>
/// One host port forwarded to a guest port, for a card connected through
/// <see cref="NetworkMode.Nat"/>. Mirrors <see
/// cref="Aurora386.Core.Configuration.VmConfiguration.PortForward"/>, which is
/// where a vm.json's own shape is decided; this is the settings layer's own
/// copy, so that <c>Devices.Cards</c> does not have to depend on
/// <c>Configuration</c> to describe what a card needs.
/// </summary>
public sealed class PortForward
{
    /// <summary>True for TCP, false for UDP.</summary>
    public bool Tcp { get; set; } = true;
    public int HostPort { get; set; }
    public int GuestPort { get; set; }
}

/// <summary>
/// What a slot is given when a card is built from the catalog: the name that
/// picks the factory, and the resources -- port, IRQ, DMA channels -- it
/// should come up claiming. Not every field applies to every card; a
/// network card, for instance, has no DMA channel of its own, and only a
/// network card looks at <see cref="Mode"/> and <see cref="Forwards"/>.
/// </summary>
public sealed class CardSettings
{
    /// <summary>The catalog name, e.g. "sb16".</summary>
    public string Card { get; set; } = "";

    /// <summary>Base I/O port.</summary>
    public ushort Port { get; set; }

    public int Irq { get; set; }

    /// <summary>8-bit (or the only) DMA channel.</summary>
    public int Dma { get; set; }

    /// <summary>16-bit DMA channel, for a card that has one.</summary>
    public int HighDma { get; set; }

    /// <summary>How a network card reaches outside the machine.</summary>
    public NetworkMode Mode { get; set; } = NetworkMode.None;

    /// <summary>Host ports forwarded to the guest, when <see cref="Mode"/> is <see cref="NetworkMode.Nat"/>.</summary>
    public List<PortForward> Forwards { get; set; } = new();
}
