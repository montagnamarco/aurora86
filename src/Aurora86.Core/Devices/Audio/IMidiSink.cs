namespace Aurora86.Core.Devices.Audio;

/// <summary>
/// The raw MIDI-byte stream behind an MPU-401 UART port. The guest writes
/// one byte at a time, so a sink owns message framing and any host endpoint
/// lifetime outside the emulated device.
/// </summary>
public interface IMidiSink
{
    /// <summary>
    /// Tries to accept one byte without blocking. A successful call takes
    /// ownership of exactly <paramref name="value"/>; a failed call changes
    /// no sink state, so the UART can retry it later.
    /// </summary>
    bool TryWriteByte(byte value);

    /// <summary>
    /// Invalidates the old byte stream and incomplete host-side MIDI framing
    /// without blocking when the guest resets or restores a snapshot. Native
    /// cleanup belongs on the sink's worker; expected host failures must not
    /// escape onto the emulation thread.
    /// </summary>
    void Reset();
}
