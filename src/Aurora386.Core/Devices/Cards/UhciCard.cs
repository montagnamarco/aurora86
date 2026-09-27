using Aurora386.Core.Devices.Usb;

namespace Aurora386.Core.Devices.Cards;

/// <summary>
/// The USB host adapter as a pluggable card: two UHCI controllers on the
/// PIIX's USB function numbers -- one carrying the keyboard and mouse, the
/// other free for removable mass storage -- built and attached exactly as
/// <c>Machine.EnableUsb</c> built them by hand before this registry existed.
/// See <c>plans/specs/device-registry.md</c>.
///
/// A real i430FX/PIIX3 board has one USB function. This emulator gives the
/// storage controller a second PCI function of its own (device 0x18, I/O
/// C120h) rather than a second port on the first controller, so that
/// inserting a USB stick does not require the guest to notice a hot-plugged
/// root hub port -- exactly the shape <c>Machine.Usb.cs</c> built before,
/// kept unchanged here down to both PCI device numbers.
/// On an i430TX motherboard the first controller instead occupies PIIX4's
/// integrated function 00:01.2 (7112h); the second remains an expansion slot.
///
/// Unlike the sound and network cards, this one is not necessarily built at
/// construction time: <see cref="Machine.EnableUsb"/> adds it to <see
/// cref="Machine.Cards"/> at run time too, which is how the GUI's "enable
/// USB" toggle and its "insert image" menu keep working on a machine that
/// started with no USB card fitted at all -- see that method's own summary.
/// </summary>
public sealed class UhciCard : IExpansionCard
{
    /// <summary>
    /// The version <see cref="Capture"/> produces today. The payload's shape
    /// -- the two controllers' own state, length-prefixed, one after the
    /// other -- has not changed since USB was captured as the container's
    /// own "USB " section, so this is still version 0; see
    /// <see cref="MachineSnapshot"/> for how a file with that older section
    /// name is mapped onto this card's "UHCI" one.
    /// </summary>
    public const int CurrentSnapshotVersion = 0;

    /// <summary>
    /// The section name this card's state is kept under, as a constant <see
    /// cref="MachineSnapshot"/> can reference without an instance -- it needs
    /// the name to map a file's old "USB " section onto this one before any
    /// card exists to ask.
    /// </summary>
    public const string SnapshotSectionId = "UHCI";

    public string Kind => "usb";
    public string Model => "uhci-piix";
    public string SnapshotId => SnapshotSectionId;
    public int SnapshotVersion => CurrentSnapshotVersion;

    private UhciController? _controller;
    private UhciController? _storageController;
    private UsbHidDevice? _keyboard;
    private UsbHidDevice? _mouse;
    private UsbMassStorageDevice? _storage;
    private UsbHubDevice? _hub;

    /// <summary>The controller carrying the keyboard and mouse, PCI device 0x10, I/O C100h.</summary>
    public UhciController Controller => _controller
        ?? throw new InvalidOperationException("The USB card has not been attached yet.");

    /// <summary>The controller kept free for removable storage, PCI device 0x18, I/O C120h.</summary>
    public UhciController StorageController => _storageController
        ?? throw new InvalidOperationException("The USB card has not been attached yet.");

    /// <summary>The keyboard permanently plugged into <see cref="Controller"/>'s port 0.</summary>
    public UsbHidDevice Keyboard => _keyboard
        ?? throw new InvalidOperationException("The USB card has not been attached yet.");

    /// <summary>The mouse permanently plugged into <see cref="Controller"/>'s port 1.</summary>
    public UsbHidDevice Mouse => _mouse
        ?? throw new InvalidOperationException("The USB card has not been attached yet.");

    /// <summary>Whatever mass storage device is inserted into <see cref="StorageController"/>'s port 0, or null with nothing plugged in.</summary>
    public UsbMassStorageDevice? Storage => _storage;

    /// <summary>
    /// The hub installed between <see cref="Controller"/>'s root ports and
    /// <see cref="Keyboard"/>/<see cref="Mouse"/>, or null on a machine that
    /// never asked for one -- see <see cref="InstallHub"/>.
    /// </summary>
    public UsbHubDevice? Hub => _hub;

    public void Attach(MachineBus bus)
    {
        // Keep the storage controller present even with an empty port, so
        // inserting an image does not require PCI hot-plug enumeration.
        bool integrated = bus.Chipset == Chipset.Chipset.I430Tx;
        ushort functionAddress = integrated ? (ushort)0x0A : (ushort)0x10;
        ushort deviceId = integrated ? (ushort)0x7112 : (ushort)0x7020;
        _controller = new UhciController(
            bus.Memory, bus.Io, bus.Pci, functionAddress, deviceId: deviceId);
        _storageController = new UhciController(bus.Memory, bus.Io, bus.Pci, 0x18, 0xC120);
        _keyboard = new UsbHidDevice(true);
        _mouse = new UsbHidDevice(false);
        _controller.Attach(0, _keyboard);
        _controller.Attach(1, _mouse);
    }

    public void Reset()
    {
        _controller?.Reset();
        _storageController?.Reset();
    }

    public void Advance(double microseconds)
    {
        _controller?.Advance(microseconds);
        _storageController?.Advance(microseconds);
    }

    /// <summary>
    /// Plugs a mass storage device into <see cref="StorageController"/>'s
    /// port, ejecting and disposing whatever was there before. The same
    /// device already inserted is left alone rather than reset.
    /// </summary>
    public void AttachStorage(UsbMassStorageDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (ReferenceEquals(device, _storage))
        {
            return;
        }

        EjectStorage();
        _storage = device;
        StorageController.Attach(0, device);
    }

    /// <summary>Unplugs and disposes whatever mass storage device is inserted, if any.</summary>
    public void EjectStorage()
    {
        _storageController?.Attach(0, null);
        _storage?.Dispose();
        _storage = null;
    }

    /// <summary>
    /// Rewires <see cref="Keyboard"/> and <see cref="Mouse"/> from
    /// <see cref="Controller"/>'s own two root ports onto a hub plugged
    /// into port 0, freeing port 1 for whatever a front end or a
    /// <c>vm.json</c> "hardware.usb.hub" wants to attach behind it too -- a
    /// tree, exactly as USB 1.1 chapter 11 describes one, in place of the
    /// two flat root ports every machine had before this existed.
    ///
    /// Idempotent, like <see cref="EnableUsb"/>'s own card-level guard: a
    /// second call returns the already-installed hub rather than building a
    /// second one and losing the first hub's downstream devices.
    /// </summary>
    public UsbHubDevice InstallHub(int ports = UsbHubDevice.DefaultPortCount)
    {
        if (_hub is not null)
        {
            return _hub;
        }

        _hub = new UsbHubDevice(ports);
        Controller.Attach(1, null);
        Controller.Attach(0, _hub);
        _hub.Attach(0, _keyboard);
        _hub.Attach(1, _mouse);
        return _hub;
    }

    public byte[] Capture()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (UhciController controller in new[] { Controller, StorageController })
        {
            byte[] state = controller.Capture();
            writer.Write(state.Length);
            writer.Write(state);
        }
        return stream.ToArray();
    }

    public void Restore(byte[] payload, int version)
    {
        if (payload.Length == 0)
        {
            Reset();
            return;
        }

        using var reader = new BinaryReader(new MemoryStream(payload));
        foreach (UhciController controller in new[] { Controller, StorageController })
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
