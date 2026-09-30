using Aurora386.Core.Devices.Pci;
using Aurora386.Core.Devices.Usb;

namespace Aurora386.Core.Devices.Cards;

/// <summary>
/// A USB 3 host controller as a pluggable card: the xHCI 1.x interface
/// Intel published in 2010 and every PC since about 2012 carries, under the
/// identity of QEMU's qemu-xhci (1B36:000D), which Linux's xhci-hcd and the
/// inbox usbxhci.sys of Windows 8 and later bind by class code. See
/// <see cref="XhciController"/> and plans/usb3-xhci.md. This first stage
/// exposes USB 2 ports only; SuperSpeed comes later.
///
/// It is independent of the UHCI/EHCI card: its own PCI device, its own
/// ports. A USB stick configured for it is plugged into port 0 and owned by
/// the machine, like the other removable images.
/// </summary>
public sealed class XhciCard : IExpansionCard
{
    public const int CurrentSnapshotVersion = XhciController.SnapshotVersion;
    public const string SnapshotSectionId = "XHCI";

    /// <summary>
    /// PCI device 8, function 0: the slot added to the board's PIRQ rotation
    /// for this controller (<see cref="PciInterruptRouter.LastSlotDevice"/>).
    /// INTA of device 8 reaches PIRQC, the link device 4 also uses.
    /// </summary>
    private const ushort FunctionAddress = 8 << 3;

    private XhciController? _controller;

    public string Kind => "usb";
    public string Model => "qemu-xhci";
    public string SnapshotId => SnapshotSectionId;
    public int SnapshotVersion => CurrentSnapshotVersion;

    public XhciController Controller => _controller
        ?? throw new InvalidOperationException("The xHCI card has not been attached yet.");

    public void Attach(MachineBus bus)
    {
        if (bus.Pci.Functions.Any(function => function.Address == FunctionAddress))
        {
            throw new InvalidOperationException("xHCI requires free PCI function 00:08.0.");
        }

        _controller = new XhciController(bus.Pci, bus.Memory, FunctionAddress);
    }

    public void Reset() => _controller?.Reset();

    public void Advance(double microseconds) => _controller?.Advance(microseconds);

    public byte[] Capture() => Controller.Capture();

    public void Restore(byte[] payload, int version) => Controller.Restore(payload, version);
}
