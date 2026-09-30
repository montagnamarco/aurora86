namespace Aurora86.Core.Devices.Cards;

/// <summary>
/// A card that plugs into a slot rather than being wired into the
/// motherboard: sound, network, a USB host controller -- the lanes that can
/// be built and reasoned about independently of the chipset. See
/// <c>plans/specs/device-registry.md</c> for the two-tier design this is
/// part of.
///
/// A card is built by <see cref="CardCatalog"/> from a name and a <see
/// cref="CardSettings"/>, unattached; <see cref="Attach"/> is the separate
/// step that actually claims ports, an interrupt line, DMA channels, a PCI
/// function or an ISA Plug and Play identity, and it is only after that call
/// that the card answers on the bus at all. Splitting construction from
/// attachment is what lets a test build a card against a bus of its own
/// without going through <see cref="Machine"/>.
/// </summary>
public interface IExpansionCard
{
    /// <summary>What the card is for: "sound", "network", "usb".</summary>
    string Kind { get; }

    /// <summary>The catalog name that built it, e.g. "sb16".</summary>
    string Model { get; }

    /// <summary>
    /// Claims whatever the card needs from the bus -- ports, an IRQ, DMA
    /// channels, a PCI function, an ISA Plug and Play identity, a memory
    /// window -- and starts answering. Called once, right after construction.
    /// </summary>
    void Attach(MachineBus bus);

    /// <summary>Puts the card back to its power-on state, as unplugging and replugging it would.</summary>
    void Reset();

    /// <summary>
    /// Advances the card by a span of emulated time. The caller drives this
    /// from the same batch that moves every other device, until the card
    /// moves to <see cref="MachineBus.Scheduler"/> instead.
    /// </summary>
    void Advance(double microseconds);

    /// <summary>The snapshot section name this card's state is captured under: exactly four ASCII characters.</summary>
    string SnapshotId { get; }

    /// <summary>The version of the payload <see cref="Capture"/> produces today.</summary>
    int SnapshotVersion { get; }

    /// <summary>Captures the card's state as this version's payload.</summary>
    byte[] Capture();

    /// <summary>Restores the card's state from a payload of the given version.</summary>
    void Restore(byte[] payload, int version);
}
