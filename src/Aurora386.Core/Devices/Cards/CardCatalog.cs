using Aurora386.Core.Devices.Audio;
using Aurora386.Core.Devices.Network;

namespace Aurora386.Core.Devices.Cards;

/// <summary>
/// Builds an unattached card of the given settings, against the bus it will
/// (separately) be attached to. The bus is passed at construction time
/// rather than left for <see cref="IExpansionCard.Attach"/> alone because a
/// PCI card's factory needs it to claim a device number before anything
/// about the card exists; a card that does not need the bus this early, like
/// the Sound Blaster, simply ignores it here.
/// </summary>
public delegate IExpansionCard CardFactory(MachineBus bus, CardSettings settings);

/// <summary>
/// Name to factory, one line per card: the only place a new card type joins
/// the machine. See <c>plans/specs/device-registry.md</c>.
/// </summary>
public static class CardCatalog
{
    private static readonly Dictionary<string, CardFactory> Factories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sb16"] = (_, settings) => new SoundBlaster16Card(settings),
            ["es1371-pci"] = (_, settings) => new Es1371Card(settings),
            ["mpu401-uart"] = (_, settings) => new Mpu401Card(settings),
            ["ne2000-isa"] = (_, settings) => new Ne2000Card(settings),
            ["rtl8029-pci"] = (_, settings) => new Rtl8029Card(settings),
            ["rtl8139-pci"] = (_, settings) => new Rtl8139Card(settings),
            ["uhci-piix"] = (_, _) => new UhciCard(),
            ["ahci-ich9"] = (_, _) => new AhciCard(),
            ["hda-ich9"] = (_, _) => new HdaCard(),
        };

    /// <summary>
    /// The <see cref="IExpansionCard.Kind"/> a catalog name would build,
    /// without building it -- for validating a configuration (a sound card
    /// named in the network list, say) before anything is constructed. Kept
    /// as its own table rather than derived from an instance because Kind is
    /// fixed per card type and does not depend on <see cref="CardSettings"/>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> KindByName =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sb16"] = "sound",
            ["es1371-pci"] = "sound",
            ["mpu401-uart"] = "sound",
            ["ne2000-isa"] = "network",
            ["rtl8029-pci"] = "network",
            ["rtl8139-pci"] = "network",
            ["uhci-piix"] = "usb",
            ["ahci-ich9"] = "storage",
            ["hda-ich9"] = "sound",
        };

    /// <summary>
    /// The newest version of each card's snapshot section this build can
    /// restore, keyed by <see cref="IExpansionCard.SnapshotId"/> rather than
    /// by catalog name -- the same key a snapshot file stores its section
    /// under. A snapshot section is validated the same way
    /// <see cref="MachineSnapshot"/>'s own sections are; skipping it the way
    /// an unrecognised section is skipped would let a snapshot newer than
    /// this build understands restore silently wrong data into the card.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> NewestSnapshotVersions = new Dictionary<string, int>
    {
        ["SB16"] = SoundBlaster16.SnapshotVersion,
        [Es1371Card.SnapshotSectionId] = Es1371Card.CurrentSnapshotVersion,
        [Mpu401Card.SnapshotSectionId] = Mpu401Uart.SnapshotVersion,
        ["NE2K"] = Ne2000.SnapshotVersion,
        [Rtl8029Card.SnapshotSectionId] = Rtl8029Card.CurrentSnapshotVersion,
        [Rtl8139Card.SnapshotSectionId] = Rtl8139Card.CurrentSnapshotVersion,
        [UhciCard.SnapshotSectionId] = UhciCard.CurrentSnapshotVersion,
        [AhciCard.SnapshotSectionId] = AhciCard.CurrentSnapshotVersion,
        [HdaCard.SnapshotSectionId] = HdaCard.CurrentSnapshotVersion,
    };

    /// <summary>
    /// Payload lengths that are fixed by version, for a card whose section is
    /// not free-form -- the same reasoning as <see cref="MachineSnapshot"/>'s
    /// own fixed-size sections: a short payload is caught here rather than
    /// partway through restoring the card's memory.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> FixedSnapshotLengths = new Dictionary<string, int>
    {
        ["NE2K"] = Ne2000.SnapshotLength,
        [Es1371Card.SnapshotSectionId] = Es1371.SnapshotLength,
        [Mpu401Card.SnapshotSectionId] = Mpu401Uart.SnapshotLength,
        [Rtl8029Card.SnapshotSectionId] = Rtl8029.SnapshotLength,
        [Rtl8139Card.SnapshotSectionId] = Rtl8139.SnapshotLength,
        [AhciCard.SnapshotSectionId] = Storage.AhciController.SnapshotLength,
        [HdaCard.SnapshotSectionId] = HdaController.SnapshotLength,
    };

    /// <summary>Whether a card of this name is registered.</summary>
    public static bool IsKnown(string card) => Factories.ContainsKey(card);

    /// <summary>The kind ("sound", "network", "usb", "storage") a catalog name builds, or null if the name is not registered.</summary>
    public static string? KindOf(string card) => KindByName.TryGetValue(card, out string? kind) ? kind : null;

    /// <summary>
    /// Builds the named card without attaching it: the factory step alone,
    /// which claims nothing yet -- no port, no PCI function, no host socket.
    /// Callers that need to inspect several cards before committing to any of
    /// them (a duplicate <see cref="IExpansionCard.SnapshotId"/> among them,
    /// say) build them all this way first, so that finding a problem costs
    /// nothing to undo. Throws if no card of that name is registered -- a
    /// typo in a vm.json should be loud, not a machine that silently came up
    /// without its sound card.
    /// </summary>
    public static IExpansionCard Build(MachineBus bus, CardSettings settings)
    {
        if (!Factories.TryGetValue(settings.Card, out CardFactory? factory))
        {
            throw new ArgumentException(
                $"No card named '{settings.Card}' is registered.", nameof(settings));
        }

        return factory(bus, settings);
    }

    /// <summary>Builds the named card and attaches it to the bus in one step. See <see cref="Build"/>.</summary>
    public static IExpansionCard Create(MachineBus bus, CardSettings settings)
    {
        IExpansionCard card = Build(bus, settings);
        card.Attach(bus);
        return card;
    }
}
