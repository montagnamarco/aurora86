using Aurora86.Core.Devices.Audio;
using Aurora86.Core.Devices.Pci;

namespace Aurora86.Core.Devices.Cards;

/// <summary>
/// Intel High Definition Audio on the ICH9 (2007, PCI 8086:293E) as a
/// pluggable sound card: the HD Audio controller with one output codec,
/// which Linux's snd-hda-intel, Windows Vista and later (hdaudio.sys and the
/// generic class function driver) and macOS drive without a vendor driver.
/// See <see cref="HdaController"/> for the register engine and
/// docs/HD-AUDIO.md for the register map and node graph.
/// </summary>
public sealed class HdaCard : IExpansionCard
{
    public const int CurrentSnapshotVersion = HdaController.SnapshotVersion;
    public const string SnapshotSectionId = "HDA ";

    /// <summary>
    /// PCI device 7, function 0: the second slot added to the board's PIRQ
    /// rotation for the modern-platform controllers
    /// (<see cref="PciInterruptRouter.LastSlotDevice"/>). INTA of device 7
    /// reaches PIRQB, the link device 3 also uses.
    /// </summary>
    private const ushort FunctionAddress = 7 << 3;

    private HdaController? _controller;

    public string Kind => "sound";
    public string Model => "hda-ich9";
    public string SnapshotId => SnapshotSectionId;
    public int SnapshotVersion => CurrentSnapshotVersion;

    public HdaController Controller => _controller
        ?? throw new InvalidOperationException("The HD Audio card has not been attached yet.");

    public void Attach(MachineBus bus)
    {
        if (bus.Pci.Functions.Any(function => function.Address == FunctionAddress))
        {
            throw new InvalidOperationException("HD Audio requires free PCI function 00:07.0.");
        }

        _controller = new HdaController(bus.Pci, bus.Memory, FunctionAddress);

        // Joins the host mix, the same registration every other sound card makes.
        bus.Audio.AddSource(_controller);
    }

    public void Reset() => _controller?.Reset();

    public void Advance(double microseconds) => _controller?.Advance(microseconds);

    public byte[] Capture() => Controller.Capture();

    public void Restore(byte[] payload, int version) => Controller.Restore(payload, version);
}
