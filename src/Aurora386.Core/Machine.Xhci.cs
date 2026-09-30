using Aurora386.Core.Devices.Cards;
using Aurora386.Core.Devices.Usb;

namespace Aurora386.Core;

public sealed partial class Machine
{
    /// <summary>The USB 3 (xHCI) controller, or null on a machine whose configuration did not fit one (<c>vm.json</c>'s <c>hardware.xhci</c>).</summary>
    public XhciController? Xhci => Cards.OfType<XhciCard>().FirstOrDefault()?.Controller;

    /// <summary>The USB stick on the xHCI controller's port 0, owned by the machine and closed by <see cref="Dispose"/>.</summary>
    private UsbMassStorageDevice? _xhciStorage;

    /// <summary>
    /// Plugs a USB stick into the xHCI controller's first port, as if it
    /// were inserted before power-on; the guest's driver finds it on its
    /// first port scan. A second stick replaces nothing: it is refused.
    /// </summary>
    public void AttachXhciStorage(UsbMassStorageDevice device)
    {
        XhciController controller = Xhci
            ?? throw new InvalidOperationException("This machine has no xHCI controller: add hardware.xhci to its configuration.");

        if (_xhciStorage is not null)
        {
            throw new InvalidOperationException("The xHCI controller's first port already holds a USB stick.");
        }

        controller.Attach(0, device);
        _xhciStorage = device;
    }

    private void DisposeXhciStorage()
    {
        _xhciStorage?.Dispose();
    }
}
