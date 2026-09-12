using Aurora386.Core.Devices.Usb;

namespace Aurora386.Core;

public sealed partial class Machine
{
    public UhciController? Usb { get; private set; }
    public UsbHidDevice? UsbKeyboard { get; private set; }
    public UsbHidDevice? UsbMouse { get; private set; }
    public UhciController? UsbStorageController { get; private set; }
    public UsbMassStorageDevice? UsbStorage { get; private set; }

    /// <summary>Connects USB input devices while retaining legacy input until configuration.</summary>
    public void EnableUsb()
    {
        if (Usb is not null)
        {
            return;
        }

        Usb = new UhciController(Memory, Io, Pci);
        // Keep the storage controller present even with an empty port, so
        // inserting an image does not require PCI hot-plug enumeration.
        UsbStorageController = new UhciController(Memory, Io, Pci, 0x18, 0xC120);
        UsbKeyboard = new UsbHidDevice(true);
        UsbMouse = new UsbHidDevice(false);
        Usb.Attach(0, UsbKeyboard);
        Usb.Attach(1, UsbMouse);
        Keyboard.InputInterceptor = scanCode =>
        {
            if (UsbKeyboard.Configuration == 0)
            {
                return false;
            }

            UsbKeyboard.PushScanCode(scanCode);
            return true;
        };
        Mouse.MotionInterceptor = (deltaX, deltaY) =>
        {
            if (UsbMouse.Configuration == 0)
            {
                return false;
            }

            UsbMouse.Move(deltaX, deltaY);
            return true;
        };
        Mouse.ButtonInterceptor = (button, pressed) =>
        {
            if (UsbMouse.Configuration == 0)
            {
                return false;
            }

            UsbMouse.SetButton((byte)button, pressed);
            return true;
        };
    }

    public void AttachUsbStorage(UsbMassStorageDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (ReferenceEquals(device, UsbStorage))
        {
            return;
        }

        EnableUsb();
        EjectUsbStorage();
        UsbStorage = device;
        UsbStorageController!.Attach(0, device);
    }

    public void EjectUsbStorage()
    {
        UsbStorageController?.Attach(0, null);
        UsbStorage?.Dispose();
        UsbStorage = null;
    }

    internal byte[] CaptureUsbState()
    {
        if (Usb is null)
        {
            return [];
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (UhciController controller in new[] { Usb, UsbStorageController! })
        {
            byte[] state = controller.Capture();
            writer.Write(state.Length);
            writer.Write(state);
        }
        return stream.ToArray();
    }

    internal void RestoreUsbState(byte[] state)
    {
        if (state.Length == 0)
        {
            Usb?.Reset();
            UsbStorageController?.Reset();
            return;
        }

        if (Usb is null)
        {
            throw new InvalidDataException("Snapshot requires USB to be enabled.");
        }

        using var reader = new BinaryReader(new MemoryStream(state));
        foreach (UhciController controller in new[] { Usb, UsbStorageController! })
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            {
                throw new InvalidDataException("Invalid USB snapshot length.");
            }

            controller.Restore(reader.ReadBytes(length));
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length)
        {
            throw new InvalidDataException("Invalid USB snapshot tail.");
        }
    }
}
