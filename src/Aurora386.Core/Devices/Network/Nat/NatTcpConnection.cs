namespace Aurora386.Core.Devices.Network.Nat;

/// <summary>
/// One TCP connection as the guest sees it, spliced onto one host socket.
///
/// The NAT does not forward TCP segments: it terminates them. The guest
/// talks TCP to what it believes is the remote host, and this object plays
/// that host -- sequence numbers, acknowledgements, windows, retransmission
/// -- while the bytes themselves go through an ordinary connected socket on
/// the other side. It is the same trick slirp plays, and it is what makes
/// the whole thing work without privileges: the host's own stack handles
/// the real network, and this one only has to be good enough for a guest
/// on a wire with no loss and no reordering.
///
/// That last point is why this is small. Loss happens only when the card's
/// ring buffer is full, and reordering never happens, so a single
/// retransmission timer and in-order acceptance cover everything that can
/// actually occur.
///
/// Every method here runs on the emulation thread. The socket reports on
/// its own threads through <see cref="IHostTcpSink"/>, and those calls do
/// nothing but hand a closure to the backend's queue.
/// </summary>
internal sealed class NatTcpConnection : IHostTcpSink
{
    /// <summary>What the guest is told it may send without waiting for an acknowledgement.</summary>
    private const ushort ReceiveWindow = 16384;

    /// <summary>
    /// How much may be in flight towards the guest regardless of the window
    /// it advertises. The card's ring buffer holds a few frames, not
    /// sixty-four kilobytes, and a driver that advertises a large window
    /// while draining that ring at DOS speed would see most of a burst
    /// dropped and then retransmitted, which is slower than never sending
    /// it in the first place.
    /// </summary>
    private const int MaxSegmentsInFlight = 8;

    private const int RetransmitMilliseconds = 1000;
    private const int MaxRetries = 6;
    private const int DefaultSegmentSize = 1460;

    private sealed class Segment
    {
        public required uint Sequence;
        public required byte Flags;
        public required byte[] Data;
        public required long SentAt;
        public int Retries;

        /// <summary>SYN and FIN each take one sequence number of their own.</summary>
        public uint Length => (uint)Data.Length
            + ((Flags & TcpFlags.Syn) != 0 ? 1u : 0)
            + ((Flags & TcpFlags.Fin) != 0 ? 1u : 0);
    }

    private readonly NatBackend _nat;
    private readonly uint _remoteIp;
    private readonly ushort _remotePort;
    private readonly ushort _guestPort;
    private readonly uint _guestIp;
    private readonly Queue<byte[]> _outgoing = new();
    private readonly List<Segment> _unacked = new();

    private IHostTcpStream? _stream;
    private int _outgoingOffset;
    private int _segmentSize = DefaultSegmentSize;
    private uint _initialSequence;
    private uint _sendUnacknowledged;
    private uint _sendNext;
    private uint _receiveNext;
    private ushort _guestWindow;
    private bool _openedByHost;
    private bool _established;
    private bool _guestClosed;
    private bool _hostClosed;
    private bool _finSent;
    private bool _finAcknowledged;

    public bool Closed { get; private set; }

    public NatTcpConnection(NatBackend nat, uint guestIp, ushort guestPort, uint remoteIp, ushort remotePort)
    {
        _nat = nat;
        _guestIp = guestIp;
        _guestPort = guestPort;
        _remoteIp = remoteIp;
        _remotePort = remotePort;
        _initialSequence = (uint)Random.Shared.Next();
        _sendUnacknowledged = _sendNext = _initialSequence;
    }

    // --- opening ------------------------------------------------------------

    /// <summary>
    /// The guest sent a SYN. The host socket starts connecting; the guest
    /// hears nothing until that succeeds or fails, which is exactly what a
    /// real remote host would do -- a SYN-ACK before the server exists
    /// would be a lie the guest could act on.
    /// </summary>
    public void OpenFromGuest(IHostTcpStream stream, uint guestSequence, ushort guestWindow, int? maximumSegment)
    {
        _receiveNext = guestSequence + 1;
        _guestWindow = guestWindow;
        if (maximumSegment is > 0)
        {
            _segmentSize = Math.Min(maximumSegment.Value, DefaultSegmentSize);
        }
        _stream = stream;
        stream.Start(this);
    }

    /// <summary>
    /// A host program connected to a forwarded port. The guest is sent a
    /// SYN as though the connection came in from outside.
    /// </summary>
    public void OpenFromHost(IHostTcpStream stream)
    {
        _openedByHost = true;
        _stream = stream;
        Transmit(TcpFlags.Syn, ReadOnlySpan<byte>.Empty, track: true, maximumSegment: DefaultSegmentSize);
        stream.Start(this);
    }

    // --- from the host socket, on its threads --------------------------------

    void IHostTcpSink.Connected() => _nat.Post(() =>
    {
        if (Closed)
        {
            return;
        }
        Transmit(TcpFlags.Syn | TcpFlags.Ack, ReadOnlySpan<byte>.Empty, track: true, maximumSegment: DefaultSegmentSize);
        _established = true;
    });

    void IHostTcpSink.ConnectFailed() => _nat.Post(() =>
    {
        if (Closed)
        {
            return;
        }
        // Refused: the guest sees a reset, just as it would from a port with
        // nothing listening. Anything subtler (unreachable, timed out) is
        // reported the same way, because the guest can do nothing different
        // with the distinction and a reset ends its wait at once.
        Reset();
    });

    void IHostTcpSink.Received(byte[] data) => _nat.Post(() =>
    {
        if (Closed)
        {
            return;
        }
        _outgoing.Enqueue(data);
        TrySend();
    });

    void IHostTcpSink.RemoteClosed() => _nat.Post(() =>
    {
        if (Closed)
        {
            return;
        }
        _hostClosed = true;
        TrySend();
    });

    void IHostTcpSink.Failed() => _nat.Post(() =>
    {
        if (Closed)
        {
            return;
        }
        Reset();
    });

    // --- from the guest, on the emulation thread -----------------------------

    public void HandleSegment(uint sequence, uint acknowledgement, byte flags, ushort window, ReadOnlySpan<byte> payload)
    {
        if ((flags & TcpFlags.Rst) != 0)
        {
            Close();
            return;
        }

        if (!_established)
        {
            // Two cases. A connection the host opened is waiting for the
            // guest's SYN-ACK; one the guest opened is waiting for the host
            // socket, and a repeated SYN from an impatient guest changes
            // nothing.
            if (_openedByHost && (flags & (TcpFlags.Syn | TcpFlags.Ack)) == (TcpFlags.Syn | TcpFlags.Ack)
                && acknowledgement == _sendNext)
            {
                _receiveNext = sequence + 1;
                _sendUnacknowledged = acknowledgement;
                _guestWindow = window;
                _unacked.Clear();
                _established = true;
                SendAck();
                TrySend();
            }
            return;
        }

        if ((flags & TcpFlags.Ack) != 0)
        {
            if (Compare(acknowledgement, _sendUnacknowledged) > 0 && Compare(acknowledgement, _sendNext) <= 0)
            {
                _sendUnacknowledged = acknowledgement;
                _unacked.RemoveAll(s => Compare(s.Sequence + s.Length, acknowledgement) <= 0);
                if (_finSent && acknowledgement == _sendNext)
                {
                    _finAcknowledged = true;
                }
            }
            _guestWindow = window;
        }

        bool fin = (flags & TcpFlags.Fin) != 0;
        int length = payload.Length;
        if (length > 0 || fin)
        {
            uint end = sequence + (uint)length;
            if (sequence == _receiveNext)
            {
                Accept(payload, fin);
            }
            else if (Compare(sequence, _receiveNext) < 0 && Compare(end, _receiveNext) > 0)
            {
                // A retransmission that overlaps what we already have: only
                // the new tail counts.
                Accept(payload[(int)(_receiveNext - sequence)..], fin);
            }
            else
            {
                // Old, or ahead of a gap. Either way an acknowledgement of
                // where we are is what makes the guest do the right thing.
                SendAck();
            }
        }

        TrySend();
        CheckFinished();
    }

    private void Accept(ReadOnlySpan<byte> data, bool fin)
    {
        if (data.Length > 0)
        {
            _receiveNext += (uint)data.Length;
            _stream?.Send(data);
        }
        if (fin && !_guestClosed)
        {
            _receiveNext++;
            _guestClosed = true;
            _stream?.ShutdownSend();
        }
        SendAck();
    }

    // --- sending towards the guest -------------------------------------------

    private void TrySend()
    {
        if (!_established || Closed)
        {
            return;
        }

        while (_outgoing.Count > 0)
        {
            int inFlight = (int)(_sendNext - _sendUnacknowledged);
            int room = Math.Min(_guestWindow, MaxSegmentsInFlight * _segmentSize) - inFlight;
            if (room <= 0)
            {
                break;
            }

            byte[] chunk = _outgoing.Peek();
            int take = Math.Min(Math.Min(_segmentSize, chunk.Length - _outgoingOffset), room);
            Transmit(TcpFlags.Psh | TcpFlags.Ack, chunk.AsSpan(_outgoingOffset, take), track: true);
            _outgoingOffset += take;
            if (_outgoingOffset == chunk.Length)
            {
                _outgoing.Dequeue();
                _outgoingOffset = 0;
            }
        }

        if (_outgoing.Count == 0 && _hostClosed && !_finSent)
        {
            Transmit(TcpFlags.Fin | TcpFlags.Ack, ReadOnlySpan<byte>.Empty, track: true);
            _finSent = true;
        }
    }

    /// <summary>Retransmits whatever has waited too long, and gives up on a guest that never answers.</summary>
    public void Tick(long now)
    {
        if (Closed || _unacked.Count == 0)
        {
            return;
        }

        Segment oldest = _unacked[0];
        if (now - oldest.SentAt < (RetransmitMilliseconds << oldest.Retries))
        {
            return;
        }

        if (oldest.Retries >= MaxRetries)
        {
            Reset();
            return;
        }

        oldest.Retries++;
        oldest.SentAt = now;
        Emit(oldest.Sequence, oldest.Flags, oldest.Data, (oldest.Flags & TcpFlags.Syn) != 0 ? DefaultSegmentSize : null);
    }

    private void Transmit(byte flags, ReadOnlySpan<byte> data, bool track, int? maximumSegment = null)
    {
        uint sequence = _sendNext;
        Emit(sequence, flags, data, maximumSegment);

        var segment = new Segment { Sequence = sequence, Flags = flags, Data = data.ToArray(), SentAt = _nat.Now };
        _sendNext += segment.Length;
        if (track && segment.Length > 0)
        {
            _unacked.Add(segment);
        }
    }

    private void Emit(uint sequence, byte flags, ReadOnlySpan<byte> data, int? maximumSegment)
    {
        // A SYN of our own has nothing to acknowledge yet; every other
        // segment carries where we are, because it costs nothing and lets
        // the guest drop its own retransmissions sooner.
        uint acknowledgement = flags == TcpFlags.Syn ? 0 : _receiveNext;
        byte[] segment = Packet.BuildTcp(_remoteIp, _guestIp, _remotePort, _guestPort,
                                         sequence, acknowledgement, flags, ReceiveWindow, data,
                                         maximumSegment.HasValue ? (ushort)maximumSegment.Value : null);
        _nat.InjectIp(_remoteIp, _guestIp, Packet.ProtocolTcp, segment);
    }

    private void SendAck() => Emit(_sendNext, TcpFlags.Ack, ReadOnlySpan<byte>.Empty, null);

    private void Reset()
    {
        Emit(_sendNext, TcpFlags.Rst | TcpFlags.Ack, ReadOnlySpan<byte>.Empty, null);
        Close();
    }

    private void CheckFinished()
    {
        if (_guestClosed && _finSent && _finAcknowledged)
        {
            Close();
        }
    }

    public void Close()
    {
        if (Closed)
        {
            return;
        }
        Closed = true;
        _stream?.Close();
    }

    /// <summary>Sequence-number comparison that survives wrapping at 2^32.</summary>
    private static int Compare(uint a, uint b) => (int)(a - b);
}
