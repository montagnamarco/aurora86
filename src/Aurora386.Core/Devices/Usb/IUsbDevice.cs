namespace Aurora386.Core.Devices.Usb;

/// <summary>
/// The four handshake packets a USB 1.1 endpoint can return for a
/// transaction: ACK (accepted), NAK (busy, retry later), STALL (endpoint
/// halted, needs a CLEAR_FEATURE) or a timeout (nothing answered, as when no
/// device sits at the addressed port).
/// </summary>
public enum UsbHandshake
{
    Ack,
    Nak,
    Stall,
    Timeout,
}

/// <summary>
/// What a device answers to one <see cref="IUsbDevice.Transfer"/> call: the
/// handshake, and for IN transactions how many bytes it placed into the
/// buffer the controller offered.
/// </summary>
public readonly record struct UsbTransactionResult(UsbHandshake Handshake, int Length = 0);

/// <summary>
/// One packet on the emulated USB wire. SETUP/OUT supply data; IN supplies
/// writable capacity. Devices implement protocol state, while the controller
/// owns DMA descriptors, retries and interrupt delivery.
/// </summary>
public interface IUsbDevice
{
    byte Address { get; }

    bool LowSpeed { get; }

    /// <summary>
    /// Whether the device can run at USB 2.0 high speed (480 Mbit/s): true
    /// only for a device that answers the high-speed detection handshake
    /// (the chirp during port reset, USB 2.0 §7.1.7.5) and carries the
    /// high-speed descriptors. An EHCI port keeps such a device after reset;
    /// any other device reads as not enabled there, and the driver hands the
    /// port to the companion UHCI controller. False for every USB 1.1 device.
    /// </summary>
    bool HighSpeedCapable => false;

    /// <summary>
    /// Tells a <see cref="HighSpeedCapable"/> device which speed its port
    /// reset negotiated: true on an EHCI port, false on a UHCI one. The
    /// device then reports the matching descriptors (bulk endpoints of 512
    /// bytes at high speed, 64 at full speed; USB 2.0 §9.6.6). Ignored by
    /// devices that only have one speed.
    /// </summary>
    void SelectSpeed(bool highSpeed)
    {
    }

    void Reset();

    void Advance(double microseconds) { }

    UsbTransactionResult Transfer(byte pid, byte endpoint, bool data1, Span<byte> data);

    byte[] Capture();

    void Restore(ReadOnlySpan<byte> data);

    /// <summary>
    /// The device that should answer a transaction addressed to
    /// <paramref name="address"/>: this device itself when its own address
    /// matches, or null otherwise. A hub overrides this to also search its
    /// enabled downstream ports, which is how <see
    /// cref="UhciController.ExecuteTd"/> reaches a device several tiers deep
    /// without needing to know hubs exist at all -- it just calls this on
    /// whatever sits on its own two root ports. <paramref name="depth"/> is
    /// the number of hub tiers already crossed, so a hub can refuse to
    /// recurse past USB 1.1's own five-tier limit instead of looping forever
    /// on a cyclic or misconfigured tree.
    /// </summary>
    IUsbDevice? Find(byte address, int depth = 0) => Address == address ? this : null;
}
