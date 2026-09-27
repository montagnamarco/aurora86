namespace Aurora386.Core.Devices.Network;

/// <summary>
/// A backend that hands every transmitted frame straight back as a received
/// one, as if the card were talking to a mirror.
///
/// This is more useful than it sounds. A packet driver's install-time self
/// test typically sends a frame to its own address and waits to see it come
/// back, so a loopback wire exercises the entire path -- remote DMA write,
/// transmit command, ring insertion, interrupt, remote DMA read -- without
/// needing a host network interface, root privileges, or a second machine.
/// If a driver works against this backend, the register emulation is right;
/// only the outside world is missing.
///
/// Frames are queued rather than swapped in place because transmission and
/// reception are separate events in the guest's timeline: the driver sends,
/// gets its PTX interrupt, returns to whatever it was doing, and only later
/// polls or is interrupted for the incoming copy.
/// </summary>
public sealed class LoopbackBackend : INetworkBackend
{
    private readonly Queue<byte[]> _pending = new();

    /// <summary>
    /// Every frame that has been sent, in order. Tests assert on this to
    /// check that a transmit command really reached the wire.
    /// </summary>
    public List<byte[]> Sent { get; } = [];

    /// <summary>
    /// When false, sent frames are recorded but not looped back. Handy for
    /// testing transmission on its own without the receive path interfering.
    /// </summary>
    public bool LoopBack { get; set; } = true;

    public void Send(ReadOnlySpan<byte> frame)
    {
        byte[] copy = frame.ToArray();
        Sent.Add(copy);
        if (LoopBack)
        {
            _pending.Enqueue(copy);
        }
    }

    /// <summary>Injects a frame as though it had arrived from the network.</summary>
    public void Inject(ReadOnlySpan<byte> frame) => _pending.Enqueue(frame.ToArray());

    public bool TryReceive(Span<byte> buffer, out int length)
    {
        if (_pending.Count == 0)
        {
            length = 0;
            return false;
        }

        byte[] frame = _pending.Peek();

        // A frame that does not fit is dropped rather than truncated. Half an
        // Ethernet frame is not a smaller frame, it is corruption, and a real
        // card in this situation reports an overflow instead of delivering
        // nonsense to the driver.
        _pending.Dequeue();
        if (frame.Length > buffer.Length)
        {
            length = 0;
            return false;
        }

        frame.CopyTo(buffer);
        length = frame.Length;
        return true;
    }

    /// <summary>Frames waiting to be picked up by the card.</summary>
    public int PendingCount => _pending.Count;
}
