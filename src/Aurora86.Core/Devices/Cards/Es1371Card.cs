using Aurora86.Core.Devices.Audio;
using Aurora86.Core.Devices.Pci;

namespace Aurora86.Core.Devices.Cards;

/// <summary>
/// Creative/Ensoniq's 1997 AudioPCI ES1371 as a pluggable card: an AC'97
/// codec behind Ensoniq's own bus-mastering DMA engine, a PCI sound card an
/// in-box Windows 98/2000/XP driver already knows. See
/// <see cref="Audio.Es1371"/> for the register engine this wraps, and
/// <c>plans/specs/es1371.md</c> for the register map and the coordinator's
/// decisions (E1-E7) this implements.
/// </summary>
public sealed class Es1371Card : IExpansionCard
{
    public const int CurrentSnapshotVersion = Es1371.SnapshotVersion;
    public const string SnapshotSectionId = "E137";

    /// <summary>
    /// PCI device 2, function 0 -- the same address the primary UHCI
    /// controller uses by default on an i430FX board (see
    /// <see cref="UhciCard"/>). It is free whenever no USB card is fitted at
    /// all, and on an i430TX board even when one is, because PIIX4
    /// integrates the primary UHCI controller into its own southbridge
    /// function (00:01.2) instead of this slot. It collides only with an
    /// explicitly configured <c>"uhci-piix"</c> card on an i430FX machine --
    /// <see cref="Attach"/> refuses that combination with a clear message
    /// rather than letting the two PCI functions silently contend for the
    /// same address.
    ///
    /// This is a fixed slot, not a scanned "first free" one, for the same
    /// reason <see cref="Rtl8139Card"/>'s own slot is fixed: this board's
    /// PIRQ rotation (<see cref="Pci.PciInterruptRouter.Link"/>) only ever
    /// routes PCI devices 2-5, four slots in total. With the always-present
    /// S3 video card fixed at device 4 and a network card, if any, fixed at
    /// device 5, there is no fifth number to hand a sound card unconditionally;
    /// device 2 is the choice that survives every configuration this
    /// project's own tests and images exercise.
    /// </summary>
    private const ushort FunctionAddress = 0x0010;

    /// <summary>
    /// 64 bytes, clear of every other fixed port on this machine: the
    /// bus-master IDE engine at C000h, both UHCI controllers at C100h/C120h,
    /// and the RTL8139's own default window at C200h-C2FFh.
    /// </summary>
    private const ushort DefaultPort = 0xC400;

    private const int IoWindowLength = 64;

    private readonly CardSettings _settings;
    private Es1371? _device;

    public string Kind => "sound";
    public string Model => "es1371-pci";
    public string SnapshotId => SnapshotSectionId;
    public int SnapshotVersion => CurrentSnapshotVersion;

    public Es1371 Device => _device
        ?? throw new InvalidOperationException("The ES1371 card has not been attached yet.");

    public Es1371Card(CardSettings settings)
    {
        _settings = settings;
    }

    public void Attach(MachineBus bus)
    {
        ushort port = _settings.Port == 0 ? DefaultPort : _settings.Port;
        if ((port & (IoWindowLength - 1)) != 0 || !bus.Io.IsFree(port, IoWindowLength, null!))
        {
            throw new InvalidOperationException("ES1371 requires a free 64-byte aligned I/O range.");
        }

        if (bus.Pci.InterruptRouter is null || PciInterruptRouter.Link(FunctionAddress, 1) < 0 ||
            bus.Pci.Functions.Any(function => function.Address == FunctionAddress))
        {
            throw new InvalidOperationException(
                "ES1371 requires free routable PCI function 00:02.0 -- an explicitly configured UHCI card " +
                "on an i430FX machine would otherwise contend for the same slot. See FunctionAddress's own summary.");
        }

        _device = new Es1371(bus.Pci, bus.Io, FunctionAddress, port);

        // Joins the host mix (F1.6), the same registration a Sound Blaster
        // makes the moment it claims its own ports.
        bus.Audio.AddSource(_device);
    }

    public void Reset() => _device?.Reset();
    public void Advance(double microseconds) => _device?.Advance(microseconds);
    public byte[] Capture() => Device.Capture();
    public void Restore(byte[] payload, int version) => Device.Restore(payload, version);
}
