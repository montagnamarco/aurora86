using Aurora386.Core.Devices.Pci;
using Aurora386.Core.Devices.Storage;

namespace Aurora386.Core.Devices.Cards;

/// <summary>
/// Intel's ICH9 AHCI SATA controller (2007, PCI 8086:2922) as a pluggable
/// card: six SATA ports behind the AHCI 1.x register interface that Linux's
/// ahci driver, Windows Vista and later (msahci, storahci) and every UEFI
/// firmware drive without a vendor driver. See <see cref="AhciController"/>
/// for the register engine and docs/AHCI.md for the register map.
///
/// Disks are attached after construction (<see cref="Machine.AttachSataDisk"/>),
/// the way IDE disks are, because the snapshot carries the controller's
/// registers but never the disks' contents or which images were fitted.
/// </summary>
public sealed class AhciCard : IExpansionCard
{
    public const int CurrentSnapshotVersion = AhciController.SnapshotVersion;
    public const string SnapshotSectionId = "AHCI";

    /// <summary>
    /// PCI device 6, function 0: one of the two slots added to the board's
    /// PIRQ rotation for the modern-platform controllers
    /// (<see cref="PciInterruptRouter.LastSlotDevice"/>). INTA of device 6
    /// reaches PIRQA, the link device 2 also uses.
    /// </summary>
    private const ushort FunctionAddress = 6 << 3;

    private AhciController? _controller;

    public string Kind => "storage";
    public string Model => "ahci-ich9";
    public string SnapshotId => SnapshotSectionId;
    public int SnapshotVersion => CurrentSnapshotVersion;

    public AhciController Controller => _controller
        ?? throw new InvalidOperationException("The AHCI card has not been attached yet.");

    public void Attach(MachineBus bus)
    {
        if (bus.Pci.Functions.Any(function => function.Address == FunctionAddress))
        {
            throw new InvalidOperationException("AHCI requires free PCI function 00:06.0.");
        }

        _controller = new AhciController(bus.Pci, bus.Memory, FunctionAddress);
    }

    public void Reset() => _controller?.Reset();

    /// <summary>Commands complete synchronously when issued (see <see cref="AhciController"/>): nothing advances with time.</summary>
    public void Advance(double microseconds)
    {
    }

    public byte[] Capture() => Controller.Capture();

    public void Restore(byte[] payload, int version) => Controller.Restore(payload, version);
}
