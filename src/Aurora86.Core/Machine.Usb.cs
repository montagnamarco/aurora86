using Aurora86.Core.Devices.Cards;
using Aurora86.Core.Devices.Usb;

namespace Aurora86.Core;

public sealed partial class Machine
{
    /// <summary>
    /// The USB card fitted, or null on a machine built and never told to
    /// enable one. Everything below reads through it; see <see
    /// cref="UhciCard"/> for the two controllers it wraps.
    /// </summary>
    private UhciCard? _usbCard;

    /// <summary>The controller carrying the keyboard and mouse, or null with no USB card fitted.</summary>
    public UhciController? Usb => _usbCard?.Controller;

    public UsbHidDevice? UsbKeyboard => _usbCard?.Keyboard;
    public UsbHidDevice? UsbMouse => _usbCard?.Mouse;

    /// <summary>The controller kept free for removable storage, or null with no USB card fitted.</summary>
    public UhciController? UsbStorageController => _usbCard?.StorageController;

    public UsbMassStorageDevice? UsbStorage => _usbCard?.Storage;

    /// <summary>
    /// The hub between <see cref="Usb"/>'s root ports and the keyboard and
    /// mouse, or null on a machine that never asked for one -- see <see
    /// cref="InstallUsbHub"/> and <c>vm.json</c>'s <c>hardware.usb.hub</c>.
    /// </summary>
    public UsbHubDevice? UsbHub => _usbCard?.Hub;

    /// <summary>
    /// Fits the USB card (catalog name "uhci-piix") if the machine does not
    /// already have one, and connects USB input while retaining legacy input
    /// until configuration -- exactly what this method always did, now built
    /// through <see cref="Devices.Cards.CardCatalog"/> so the card ends up in
    /// <see cref="Cards"/> and its state in the snapshot alongside every
    /// other card.
    ///
    /// A machine whose <c>vm.json</c> already asked for USB has the card
    /// fitted from construction, and this is then a no-op; a machine that
    /// did not -- every default machine, and every one built before F1.4c --
    /// gets it appended here instead, which is how enabling USB from the GUI
    /// and <see cref="AttachUsbStorage"/> both keep working without the
    /// configuration having named the card up front.
    /// </summary>
    public void EnableUsb()
    {
        if (_usbCard is not null)
        {
            return;
        }

        var card = (UhciCard)CardCatalog.Create(_bus, new CardSettings { Card = "uhci-piix" });
        Cards = [.. Cards, card];
        WireUsbCard(card);
    }

    /// <summary>
    /// Points the keyboard and mouse's interceptors at the card just fitted,
    /// whether that happened during construction or from <see
    /// cref="EnableUsb"/> afterwards.
    /// </summary>
    private void WireUsbCard(UhciCard card)
    {
        _usbCard = card;
        UsbHidDevice keyboard = card.Keyboard;
        UsbHidDevice mouse = card.Mouse;

        Keyboard.InputInterceptor = scanCode =>
        {
            if (keyboard.Configuration == 0)
            {
                return false;
            }

            keyboard.PushScanCode(scanCode);
            return true;
        };
        _usbMotionRoute = (deltaX, deltaY) =>
        {
            if (mouse.Configuration == 0)
            {
                return false;
            }

            mouse.Move(deltaX, deltaY);
            return true;
        };
        _usbButtonRoute = (button, pressed) =>
        {
            if (mouse.Configuration == 0)
            {
                return false;
            }

            mouse.SetButton((byte)button, pressed);
            return true;
        };
    }

    public void AttachUsbStorage(UsbMassStorageDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        EnableUsb();
        _usbCard!.AttachStorage(device);
    }

    public void EjectUsbStorage() => _usbCard?.EjectStorage();

    /// <summary>
    /// Puts a USB 1.1 hub between the keyboard/mouse controller's root ports
    /// and the keyboard and mouse, fitting the USB card first if the machine
    /// does not already have one -- the runtime counterpart of <c>vm.json</c>'s
    /// <c>hardware.usb.hub</c>, and usable from a front end the same way
    /// <see cref="AttachUsbStorage"/> is. See <see cref="UhciCard.InstallHub"/>.
    /// </summary>
    public UsbHubDevice InstallUsbHub(int ports = UsbHubDevice.DefaultPortCount)
    {
        EnableUsb();
        return _usbCard!.InstallHub(ports);
    }
}
