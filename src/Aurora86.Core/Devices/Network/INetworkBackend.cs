namespace Aurora86.Core.Devices.Network;

/// <summary>
/// The wire, as far as the emulated card is concerned.
///
/// A real NE2000 sits between two very different worlds: on one side a driver
/// poking registers, on the other a coaxial or twisted-pair segment carrying
/// raw Ethernet frames. Everything the card does with registers, ring buffers
/// and interrupts is emulated faithfully because DOS drivers can observe it;
/// what happens once a frame leaves the card is not observable from inside the
/// guest at all, so it is deliberately abstracted away behind this interface.
///
/// That split is what lets the same <see cref="Ne2000"/> serve a unit test
/// (<see cref="LoopbackBackend"/>), a host TAP device, or a user-mode NAT/slirp
/// stack without a single line of the register emulation changing.
///
/// Frames handed across this boundary are complete Ethernet frames -- 6 bytes
/// of destination MAC, 6 of source, a 2-byte type/length field, then payload.
/// They carry no CRC: the real chip appends the frame check sequence in
/// hardware on transmit and strips it on receive before the driver ever sees
/// it, so passing it around here would only invite double-counting.
/// </summary>
public interface INetworkBackend
{
    /// <summary>
    /// Puts a frame on the wire. The card has already finished with the
    /// buffer by the time this returns, so implementations must copy anything
    /// they intend to keep.
    /// </summary>
    void Send(ReadOnlySpan<byte> frame);

    /// <summary>
    /// Collects the next frame addressed to us, if one is waiting.
    ///
    /// This is polled rather than pushed because the card must decide *when*
    /// to accept a frame: it can only do so if there is room in its ring
    /// buffer. A push model would force the backend to queue on the card's
    /// behalf and would blur who is responsible for dropping frames -- which
    /// on real hardware is unambiguously the card.
    /// </summary>
    /// <returns>True if a frame was written into <paramref name="buffer"/>.</returns>
    bool TryReceive(Span<byte> buffer, out int length);
}
