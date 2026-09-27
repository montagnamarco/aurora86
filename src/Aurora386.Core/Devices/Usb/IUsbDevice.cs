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

    void Reset();

    void Advance(double microseconds) { }

    UsbTransactionResult Transfer(byte pid, byte endpoint, bool data1, Span<byte> data);

    byte[] Capture();

    void Restore(ReadOnlySpan<byte> data);
}
