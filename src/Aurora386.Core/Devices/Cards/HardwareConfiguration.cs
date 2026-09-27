namespace Aurora386.Core.Devices.Cards;

/// <summary>
/// The expansion cards a machine is built with, in the stable order that
/// fixes their PCI device numbers and Plug and Play select numbers. Nothing
/// else lives here yet: video, network and USB have not moved off the
/// motherboard, and the chipset and processor stay parameters of their own
/// -- see <c>plans/specs/device-registry.md</c> for the plan this is the
/// first step of.
/// </summary>
public sealed class HardwareConfiguration
{
    public List<CardSettings> Sound { get; set; } = new();

    /// <summary>Network cards, in the same stable order. See <see cref="Sound"/>.</summary>
    public List<CardSettings> Network { get; set; } = new();

    /// <summary>
    /// The USB host adapter, or null for none -- a real board of this era
    /// often shipped without one. Unlike <see cref="Sound"/> and <see
    /// cref="Network"/> this is a single card rather than a list: only one
    /// entry, <c>"uhci-piix"</c>, is registered, and its resources (PCI
    /// device 0x10 and 0x18, I/O C100h and C120h) are fixed by the chipset
    /// rather than read from <see cref="CardSettings"/>. Null here is what
    /// keeps <c>new Machine()</c> USB-less exactly as it was before this
    /// registry existed; <see cref="Machine.EnableUsb"/> adds the same card
    /// to <see cref="Machine.Cards"/> at run time for a machine built
    /// without one, which is how enabling USB from the GUI keeps working.
    /// </summary>
    public CardSettings? Usb { get; set; }

    /// <summary>
    /// Today's machine, unchanged: one Sound Blaster 16 at 220h, IRQ 5, DMA
    /// channels 1 and 5, and one NE2000 at 300h, IRQ 3, on a loopback wire --
    /// exactly what <see cref="Machine"/> wired by hand before this registry
    /// existed. <c>new Machine()</c> uses this, so every existing test and
    /// bench sees the same hardware as before. USB stays off, as it always
    /// was for a machine built with no configuration at all.
    /// </summary>
    public static HardwareConfiguration Default => new()
    {
        Sound = { new CardSettings { Card = "sb16", Port = 0x220, Irq = 5, Dma = 1, HighDma = 5 } },
        Network = { new CardSettings { Card = "ne2000-isa", Port = 0x300, Irq = 3 } },
    };
}
