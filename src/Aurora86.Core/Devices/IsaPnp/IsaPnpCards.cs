using Aurora86.Core.Devices.Audio;
using Aurora86.Core.Devices.Network;

namespace Aurora86.Core.Devices.IsaPnp;

/// <summary>
/// The Plug and Play faces of the emulated cards: what each says it is,
/// what it can be given, and how the chip behind it moves when told to.
///
/// The identifiers are the ones Windows 95 has drivers for out of the box,
/// so that finding the card and installing it are the same step.
/// </summary>
public static class IsaPnpCards
{
    /// <summary>
    /// A Sound Blaster 16 Plug and Play (a CT2940-style card): one audio
    /// device, with the FM synthesiser fixed at 388h as it always was.
    /// </summary>
    public static IsaPnpCard ForSoundBlaster16(SoundBlaster16 card)
    {
        var audio = new IsaPnpLogicalDevice("CTL0031", "PNPB003", r =>
        {
            r.Io(0x220, 0x280, 0x20, 16);
            r.FixedIo(0x388, 4);
            r.Irq(5, 7, 9, 10);
            r.Dma([0, 1, 3], sixteenBit: false);
            r.Dma([5, 6, 7], sixteenBit: true);
        });
        audio.SetDefaults(active: true, [card.BasePort, 0x388], [card.Irq], [card.Dma8, card.Dma16]);
        audio.Configured = device =>
            card.Relocate(device.IoBase(0), device.Irq(0), device.Dma(0), device.Dma(1), device.Active);

        return new IsaPnpCard("CTL0044", 0x00000001, "Creative SB16 PnP", [audio]);
    }

    /// <summary>An NE2000-compatible card of the Plug and Play generation, as the RTL8019 was.</summary>
    public static IsaPnpCard ForNe2000(Ne2000 card)
    {
        var ethernet = new IsaPnpLogicalDevice("RTL8019", "PNP80D6", r =>
        {
            r.Io(0x240, 0x360, 0x20, 32);
            r.Irq(3, 4, 5, 9, 10, 11, 12, 15);
        });
        ethernet.SetDefaults(active: true, [card.BasePort], [card.IrqLine], []);
        ethernet.Configured = device => card.Relocate(device.IoBase(0), device.Irq(0), device.Active);

        return new IsaPnpCard("RTL8019", 0x00000001, "Realtek RTL8019 Ethernet", [ethernet]);
    }
}
