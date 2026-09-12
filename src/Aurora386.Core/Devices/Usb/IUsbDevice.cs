namespace Aurora386.Core.Devices.Usb;

public enum UsbHandshake { Ack, Nak, Stall, Timeout }
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
