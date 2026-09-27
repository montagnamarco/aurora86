using System.Collections.Concurrent;
using System.Net;

namespace Aurora386.Core.Devices.Network.Nat;

/// <summary>
/// A user-mode NAT: the guest's whole network, made of nothing but sockets.
///
/// To the card this is a wire with a router on it. The router answers ARP
/// for the gateway and name server, hands out 10.0.2.15 by DHCP, answers pings to itself,
/// resolves names by forwarding to the host's own name server, and turns
/// every TCP connection and UDP exchange the guest starts into a socket the
/// host opens on its behalf. The guest sees a private network with one
/// gateway; the host's network sees the emulator, and nothing else.
///
/// The addresses are the ones slirp made customary, so that anything
/// written for QEMU's user networking works here unchanged:
/// 10.0.2.2 is the gateway, 10.0.2.3 the name server, 10.0.2.15 the guest.
///
/// Everything runs on the emulation thread, inside the card's calls. The
/// host sockets complete on threads of their own and queue what they have
/// to say; the queue is drained before any frame is handled, so that the
/// stack never has two threads in it at once and needs no locks.
/// </summary>
public sealed class NatBackend : INetworkBackend, IDisposable
{
    public static readonly IPAddress GatewayAddress = IPAddress.Parse("10.0.2.2");
    public static readonly IPAddress NameServerAddress = IPAddress.Parse("10.0.2.3");
    public static readonly IPAddress GuestAddress = IPAddress.Parse("10.0.2.15");

    internal const uint Subnet = 0x0A000200;
    internal const uint SubnetMask = 0xFFFFFF00;
    internal const uint Gateway = 0x0A000202;
    internal const uint NameServer = 0x0A000203;
    internal const uint Guest = 0x0A00020F;
    internal const uint Broadcast = 0xFFFFFFFF;

    /// <summary>A locally administered address, as the card's own is.</summary>
    internal static readonly byte[] GatewayMac = [0x52, 0x55, 0x0A, 0x00, 0x02, 0x02];
    private static readonly byte[] BroadcastMac = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    private const int UdpIdleMilliseconds = 60_000;
    private const int MaintenanceIntervalMilliseconds = 100;

    // Ethernet II fields carried by every frame the card supplies.
    private const int EthernetDestinationMacOffset = 0;
    private const int EthernetSourceMacOffset = 6;
    private const int EthernetTypeOffset = 12;

    // ARP requests for Ethernet and IPv4 have a fixed 28-byte payload.
    private const int ArpPacketLength = 28;
    private const int ArpHardwareTypeOffset = 0;
    private const int ArpProtocolTypeOffset = 2;
    private const int ArpOperationOffset = 6;
    private const int ArpSenderMacOffset = 8;
    private const int ArpSenderIpOffset = 14;
    private const int ArpTargetIpOffset = 24;
    private const ushort ArpEthernetHardwareType = 1;
    private const ushort ArpRequestOperation = 1;
    private const ushort ArpReplyOperation = 2;

    private const byte IcmpEchoRequest = 8;
    private const byte IcmpEchoReply = 0;
    private const byte IcmpCodeZero = 0;

    private const ushort DhcpServerPort = 67;
    private const ushort DhcpClientPort = 68;
    private const ushort DnsPort = 53;

    // BOOTP fixed header and DHCP options (RFC 2131).
    private const uint DhcpMagic = 0x63825363;
    private const int DhcpMinimumPacketLength = 240;
    private const int BootpOperationOffset = 0;
    private const int DhcpMagicOffset = 236;
    private const int DhcpOptionsOffset = 240;
    private const byte DhcpBootRequest = 1;
    private const byte DhcpBootReply = 2;
    private const byte DhcpOptionPad = 0;
    private const byte DhcpOptionSubnetMask = 1;
    private const byte DhcpOptionRouter = 3;
    private const byte DhcpOptionDomainNameServer = 6;
    private const byte DhcpOptionLeaseTime = 51;
    private const byte DhcpOptionMessageType = 53;
    private const byte DhcpOptionServerIdentifier = 54;
    private const byte DhcpOptionBroadcastAddress = 28;
    private const byte DhcpOptionEnd = 255;
    private const byte DhcpDiscover = 1;
    private const byte DhcpOffer = 2;
    private const byte DhcpRequest = 3;
    private const byte DhcpAcknowledge = 5;
    private const byte DhcpInform = 8;
    private const uint DhcpLeaseSeconds = 86_400;

    private readonly IHostNetwork _host;
    private readonly Func<long> _clock;
    private readonly ConcurrentQueue<Action> _events = new();
    private readonly Queue<byte[]> _toGuest = new();
    private readonly Dictionary<(ushort GuestPort, uint RemoteIp, ushort RemotePort), NatTcpConnection> _tcp = new();
    private readonly Dictionary<ushort, UdpMapping> _udp = new();
    private readonly List<IDisposable> _listeners = new();

    private byte[] _guestMac = BroadcastMac;
    private uint _guestIp = Guest;
    private ushort _nextIpId;
    private long _lastTick;

    /// <param name="host">The host's sockets; the real ones unless a test says otherwise.</param>
    /// <param name="clock">Milliseconds, for retransmission timers. Tests supply their own.</param>
    public NatBackend(IHostNetwork? host = null, Func<long>? clock = null)
    {
        _host = host ?? new SocketHostNetwork();
        _clock = clock ?? (() => Environment.TickCount64);
    }

    internal long Now => _clock();

    /// <summary>TCP connections open at the moment, for the interface to show.</summary>
    public int ConnectionCount => _tcp.Count;

    /// <summary>Frames waiting for the card, for tests.</summary>
    public int PendingCount => _toGuest.Count;

    /// <summary>
    /// Makes a host port reach a guest port. Without this nothing outside can
    /// open a connection to the guest, which is the one thing a NAT costs.
    /// </summary>
    public void Forward(bool tcp, int hostPort, int guestPort)
    {
        if (tcp)
        {
            _listeners.Add(_host.ListenTcp(hostPort, (stream, remote) => Post(() =>
            {
                uint remoteIp = VirtualAddressFor(remote.Address);
                var key = ((ushort)guestPort, remoteIp, (ushort)remote.Port);
                if (_tcp.ContainsKey(key))
                {
                    stream.Close();
                    return;
                }

                var connection = new NatTcpConnection(this, _guestIp, (ushort)guestPort, remoteIp, (ushort)remote.Port);
                _tcp[key] = connection;
                connection.OpenFromHost(stream);
            })));
        }
        else
        {
            var mapping = new UdpMapping(this, (ushort)guestPort, _host.OpenUdp(hostPort), fixedPort: true);
            _udp[(ushort)guestPort] = mapping;
            mapping.Start();
        }
    }

    /// <summary>
    /// A host program reaching the guest from the host itself shows up as
    /// 127.0.0.1, which the guest's stack would refuse to answer -- that
    /// address means "me" to every host. It is shown as the gateway instead.
    /// </summary>
    private static uint VirtualAddressFor(IPAddress address)
    {
        if (address.AddressFamily != global::System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return Gateway;
        }

        uint value = Packet.ToUInt32(address);
        return IPAddress.IsLoopback(address) ? Gateway : value;
    }

    // --- the wire ----------------------------------------------------------

    public void Send(ReadOnlySpan<byte> frame)
    {
        Drain();
        HandleFrame(frame);
    }

    public bool TryReceive(Span<byte> buffer, out int length)
    {
        Drain();
        Tick();

        if (_toGuest.Count == 0)
        {
            length = 0;
            return false;
        }

        byte[] frame = _toGuest.Dequeue();
        if (frame.Length > buffer.Length)
        {
            length = 0;
            return false;
        }
        frame.CopyTo(buffer);
        length = frame.Length;
        return true;
    }

    /// <summary>Queues work from a socket thread to run on the emulation thread.</summary>
    internal void Post(Action action)
    {
        _events.Enqueue(action);
    }

    /// <summary>Runs socket completions while the emulation thread owns the NAT state.</summary>
    private void Drain()
    {
        while (_events.TryDequeue(out Action? action))
        {
            action();
        }
    }

    /// <summary>Advances connection retransmission and UDP idle timers.</summary>
    private void Tick()
    {
        long now = _clock();
        if (now - _lastTick < MaintenanceIntervalMilliseconds)
        {
            return;
        }

        _lastTick = now;

        if (_tcp.Count > 0)
        {
            foreach (NatTcpConnection connection in _tcp.Values)
            {
                connection.Tick(now);
            }

            RemoveClosedConnections();
        }

        if (_udp.Count > 0)
        {
            var expired = new List<ushort>();
            foreach ((ushort port, UdpMapping mapping) in _udp)
            {
                if (!mapping.FixedPort && now - mapping.LastUsed > UdpIdleMilliseconds)
                {
                    expired.Add(port);
                }
            }

            foreach (ushort port in expired)
            {
                _udp[port].Close();
                _udp.Remove(port);
            }
        }
    }

    /// <summary>Removes connections after their final TCP state has been observed.</summary>
    private void RemoveClosedConnections()
    {
        var closed = new List<(ushort, uint, ushort)>();
        foreach ((var key, NatTcpConnection connection) in _tcp)
        {
            if (connection.Closed)
            {
                closed.Add(key);
            }
        }

        foreach (var key in closed)
        {
            _tcp.Remove(key);
        }
    }

    /// <summary>Hands the guest a frame carrying an IP packet from the given address.</summary>
    internal void InjectIp(uint source, uint destination, byte protocol, ReadOnlySpan<byte> payload)
    {
        byte[] mac = destination == Broadcast ? BroadcastMac : _guestMac;
        _toGuest.Enqueue(Packet.BuildIp(mac, GatewayMac, source, destination, protocol, payload, _nextIpId++));
    }

    // --- Ethernet ------------------------------------------------------------

    /// <summary>Accepts the Ethernet frames that the guest transmits through the card.</summary>
    private void HandleFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < Packet.EthernetHeader)
        {
            return;
        }

        // A frame the guest addresses to itself is a driver testing the
        // wire: it goes straight back, as it would through a real hub.
        if (frame[EthernetDestinationMacOffset..EthernetSourceMacOffset].SequenceEqual(
                frame[EthernetSourceMacOffset..EthernetTypeOffset]))
        {
            _toGuest.Enqueue(frame.ToArray());
            return;
        }

        // Whoever sends is the guest, and its address is where replies go.
        if (!frame[EthernetSourceMacOffset..EthernetTypeOffset].SequenceEqual(_guestMac))
        {
            _guestMac = frame[EthernetSourceMacOffset..EthernetTypeOffset].ToArray();
        }

        switch (Packet.ReadU16(frame, EthernetTypeOffset))
        {
            case Packet.TypeArp:
                HandleArp(frame[Packet.EthernetHeader..]);
                break;
            case Packet.TypeIp:
                HandleIp(frame[Packet.EthernetHeader..]);
                break;
        }
    }

    /// <summary>Answers the ARP requests for the router and its virtual name server.</summary>
    private void HandleArp(ReadOnlySpan<byte> arp)
    {
        if (arp.Length < ArpPacketLength)
        {
            return;
        }

        if (Packet.ReadU16(arp, ArpHardwareTypeOffset) != ArpEthernetHardwareType ||
            Packet.ReadU16(arp, ArpProtocolTypeOffset) != Packet.TypeIp)
        {
            return;
        }

        if (Packet.ReadU16(arp, ArpOperationOffset) != ArpRequestOperation)
        {
            return;
        }

        ReadOnlySpan<byte> senderMac = arp[ArpSenderMacOffset..ArpSenderIpOffset];
        uint senderIp = Packet.ReadU32(arp, ArpSenderIpOffset);
        uint targetIp = Packet.ReadU32(arp, ArpTargetIpOffset);

        if (senderIp != 0 && InSubnet(senderIp))
        {
            _guestIp = senderIp;
        }

        // The router answers for itself and for the name server, and for
        // nothing else: the guest reaches the outside through the router,
        // so it asks for the router's address, not the destination's. It
        // must not answer for whatever the guest is probing before taking
        // an address -- an answer means "taken", and the guest would go
        // looking for another.
        if (targetIp is not (Gateway or NameServer) || targetIp == senderIp)
        {
            return;
        }

        _toGuest.Enqueue(Packet.BuildArp(senderMac, GatewayMac, ArpReplyOperation, GatewayMac, targetIp, senderMac, senderIp));
    }

    private static bool InSubnet(uint address)
    {
        return (address & SubnetMask) == Subnet;
    }

    // --- IP ------------------------------------------------------------------

    /// <summary>Validates an IPv4 datagram before passing its transport payload onward.</summary>
    private void HandleIp(ReadOnlySpan<byte> ip)
    {
        if (ip.Length < Packet.IpHeader || (ip[0] >> 4) != 4)
        {
            return;
        }

        int headerLength = (ip[0] & 0x0F) * 4;
        int totalLength = Math.Min(Packet.ReadU16(ip, 2), ip.Length);
        if (headerLength < Packet.IpHeader || totalLength < headerLength)
        {
            return;
        }

        if (Packet.Checksum(ip[..headerLength]) != 0)
        {
            return;
        }

        // Fragments are not reassembled: nothing the guest can usefully do
        // needs them, and half a packet is best dropped whole.
        if ((Packet.ReadU16(ip, 6) & 0x3FFF) != 0)
        {
            return;
        }

        uint source = Packet.ReadU32(ip, 12);
        uint destination = Packet.ReadU32(ip, 16);
        if (source != 0 && InSubnet(source))
        {
            _guestIp = source;
        }

        ReadOnlySpan<byte> payload = ip[headerLength..totalLength];
        switch (ip[9])
        {
            case Packet.ProtocolIcmp:
                HandleIcmp(source, destination, payload);
                break;
            case Packet.ProtocolUdp:
                HandleUdp(source, destination, payload);
                break;
            case Packet.ProtocolTcp:
                HandleTcp(source, destination, payload);
                break;
        }
    }

    // --- ICMP ----------------------------------------------------------------

    /// <summary>Replies locally to router pings and forwards external echo requests to the host.</summary>
    private void HandleIcmp(uint source, uint destination, ReadOnlySpan<byte> icmp)
    {
        if (icmp.Length < 8 || icmp[0] != IcmpEchoRequest || icmp[1] != IcmpCodeZero)
        {
            return;
        }

        if (destination is Gateway or NameServer)
        {
            InjectIp(destination, source, Packet.ProtocolIcmp, EchoReply(icmp));
            return;
        }
        if (InSubnet(destination))
        {
            return; // nobody else lives here
        }

        // The host pings on the guest's behalf and the answer, if any, is
        // rebuilt with the guest's own identifier and payload: the host's
        // stack chooses its own, and the guest matches replies by them.
        byte[] request = icmp.ToArray();
        _host.Ping(Packet.ToAddress(destination), icmp[8..], ok => Post(() =>
        {
            if (ok)
            {
                InjectIp(destination, source, Packet.ProtocolIcmp, EchoReply(request));
            }
        }));
    }

    private static byte[] EchoReply(ReadOnlySpan<byte> request)
    {
        byte[] reply = request.ToArray();
        reply[0] = IcmpEchoReply;
        reply[2] = 0;
        reply[3] = 0;
        Packet.WriteU16(reply, 2, Packet.Checksum(reply));
        return reply;
    }

    // --- UDP -----------------------------------------------------------------

    /// <summary>Maps a guest UDP datagram to the host endpoint that carries it.</summary>
    private void HandleUdp(uint source, uint destination, ReadOnlySpan<byte> udp)
    {
        if (udp.Length < Packet.UdpHeader)
        {
            return;
        }

        ushort sourcePort = Packet.ReadU16(udp, 0);
        ushort destinationPort = Packet.ReadU16(udp, 2);
        int length = Math.Min(Packet.ReadU16(udp, 4), udp.Length);
        if (length < Packet.UdpHeader)
        {
            return;
        }

        ReadOnlySpan<byte> payload = udp[Packet.UdpHeader..length];

        if (destinationPort == DhcpServerPort && sourcePort == DhcpClientPort)
        {
            HandleDhcp(payload);
            return;
        }

        IPEndPoint target;
        if (destination == NameServer && destinationPort == DnsPort)
        {
            if (_host.DnsServers.Count == 0)
            {
                return;
            }

            target = new IPEndPoint(_host.DnsServers[0], DnsPort);
        }
        else if (InSubnet(destination) || destination == Broadcast)
        {
            // Something on the private network: only an endpoint that has
            // already talked to the guest through a mapping is reachable.
            if (!_udp.TryGetValue(sourcePort, out UdpMapping? existing))
            {
                return;
            }

            if (!existing.TryResolveReal(destination, destinationPort, out target))
            {
                return;
            }
        }
        else
        {
            target = new IPEndPoint(Packet.ToAddress(destination), destinationPort);
        }

        if (!_udp.TryGetValue(sourcePort, out UdpMapping? mapping))
        {
            mapping = new UdpMapping(this, sourcePort, _host.OpenUdp(), fixedPort: false);
            _udp[sourcePort] = mapping;
            mapping.Start();
        }
        mapping.RememberAlias(target, destination, destinationPort);
        mapping.LastUsed = _clock();
        mapping.Socket.Send(payload, target);
    }

    /// <summary>
    /// One guest port's view of the outside: a host socket, and the
    /// addresses it has been told to present some remote ends as. A name
    /// server the guest knows as 10.0.2.3 must answer as 10.0.2.3, or the
    /// guest's resolver throws the reply away as an impostor's.
    /// </summary>
    private sealed class UdpMapping : IHostUdpSink
    {
        private readonly NatBackend _nat;
        private readonly ushort _guestPort;
        private readonly Dictionary<IPEndPoint, uint> _toVirtual = new();
        private readonly Dictionary<(uint, ushort), IPEndPoint> _toReal = new();

        public IHostUdpSocket Socket { get; }
        public bool FixedPort { get; }
        public long LastUsed { get; set; }

        public UdpMapping(NatBackend nat, ushort guestPort, IHostUdpSocket socket, bool fixedPort)
        {
            _nat = nat;
            _guestPort = guestPort;
            Socket = socket;
            FixedPort = fixedPort;
            LastUsed = nat.Now;
        }

        public void Start()
        {
            Socket.Start(this);
        }

        public void Close()
        {
            Socket.Close();
        }

        public void RememberAlias(IPEndPoint real, uint virtualAddress, ushort port)
        {
            if (Packet.ToUInt32(real.Address) == virtualAddress)
            {
                return;
            }

            _toVirtual[real] = virtualAddress;
            _toReal[(virtualAddress, port)] = real;
        }

        public bool TryResolveReal(uint virtualAddress, ushort port, out IPEndPoint real)
        {
            return _toReal.TryGetValue((virtualAddress, port), out real!);
        }

        void IHostUdpSink.Received(byte[] data, IPEndPoint from)
        {
            _nat.Post(() =>
            {
                // Bigger than a frame would need fragmenting, which this stack
                // does not do; a datagram that size is dropped as the network
                // would drop it with the don't-fragment bit set.
                if (data.Length > Packet.Mtu - Packet.IpHeader - Packet.UdpHeader)
                {
                    return;
                }

                uint source;
                if (!_toVirtual.TryGetValue(from, out source))
                {
                    source = VirtualAddressFor(from.Address);
                    if (source == Gateway)
                    {
                        RememberAlias(from, Gateway, (ushort)from.Port);
                    }
                }

                LastUsed = _nat.Now;
                byte[] datagram = Packet.BuildUdp(source, _nat._guestIp, (ushort)from.Port, _guestPort, data);
                _nat.InjectIp(source, _nat._guestIp, Packet.ProtocolUdp, datagram);
            });
        }
    }

    // --- DHCP ----------------------------------------------------------------

    /// <summary>
    /// The smallest DHCP server there is: one address, offered to whoever
    /// asks. A guest that configures itself statically as 10.0.2.15 with
    /// the gateway at 10.0.2.2 works just as well without ever asking.
    /// </summary>
    private void HandleDhcp(ReadOnlySpan<byte> bootp)
    {
        if (bootp.Length < DhcpMinimumPacketLength ||
            bootp[BootpOperationOffset] != DhcpBootRequest ||
            Packet.ReadU32(bootp, DhcpMagicOffset) != DhcpMagic)
        {
            return;
        }

        byte messageType = 0;
        ReadOnlySpan<byte> options = bootp[DhcpOptionsOffset..];
        for (int i = 0; i < options.Length;)
        {
            byte kind = options[i];
            if (kind == DhcpOptionEnd)
            {
                break;
            }

            if (kind == DhcpOptionPad)
            {
                i++;
                continue;
            }

            if (i + 1 >= options.Length)
            {
                break;
            }

            int length = options[i + 1];
            if (i + 2 + length > options.Length)
            {
                break;
            }

            if (kind == DhcpOptionMessageType && length == 1)
            {
                messageType = options[i + 2];
            }

            i += 2 + length;
        }

        byte replyType = messageType switch
        {
            DhcpDiscover => DhcpOffer,
            DhcpRequest => DhcpAcknowledge,
            DhcpInform => DhcpAcknowledge,
            _ => 0,
        };
        if (replyType == 0)
        {
            return;
        }

        byte[] reply = new byte[300];
        Span<byte> span = reply;
        span[BootpOperationOffset] = DhcpBootReply;
        span[1] = 1; // Ethernet
        span[2] = 6;
        bootp[4..8].CopyTo(span[4..]);   // transaction id
        bootp[10..12].CopyTo(span[10..]); // flags
        Packet.WriteU32(span, 16, Guest);   // your address
        Packet.WriteU32(span, 20, Gateway); // server address
        bootp[28..44].CopyTo(span[28..]);   // client hardware address
        Packet.WriteU32(span, DhcpMagicOffset, DhcpMagic);

        int optionOffset = DhcpOptionsOffset;
        span[optionOffset++] = DhcpOptionMessageType;
        span[optionOffset++] = 1;
        span[optionOffset++] = replyType;
        span[optionOffset++] = DhcpOptionServerIdentifier;
        span[optionOffset++] = 4;
        Packet.WriteU32(span, optionOffset, Gateway);
        optionOffset += 4;
        span[optionOffset++] = DhcpOptionLeaseTime;
        span[optionOffset++] = 4;
        Packet.WriteU32(span, optionOffset, DhcpLeaseSeconds);
        optionOffset += 4;
        span[optionOffset++] = DhcpOptionSubnetMask;
        span[optionOffset++] = 4;
        Packet.WriteU32(span, optionOffset, SubnetMask);
        optionOffset += 4;
        span[optionOffset++] = DhcpOptionRouter;
        span[optionOffset++] = 4;
        Packet.WriteU32(span, optionOffset, Gateway);
        optionOffset += 4;
        span[optionOffset++] = DhcpOptionDomainNameServer;
        span[optionOffset++] = 4;
        Packet.WriteU32(span, optionOffset, NameServer);
        optionOffset += 4;
        span[optionOffset++] = DhcpOptionBroadcastAddress;
        span[optionOffset++] = 4;
        Packet.WriteU32(span, optionOffset, Subnet | 0xFF);
        optionOffset += 4;
        span[optionOffset++] = DhcpOptionEnd;

        // Broadcast at every layer: the client does not have an address yet,
        // and may not accept a packet addressed to the one it is about to
        // get. That is how slirp does it, and every client copes.
        byte[] datagram = Packet.BuildUdp(Gateway, Broadcast, DhcpServerPort, DhcpClientPort, reply);
        InjectIp(Gateway, Broadcast, Packet.ProtocolUdp, datagram);
    }

    // --- TCP -----------------------------------------------------------------

    /// <summary>Maps a guest TCP segment to its corresponding host connection.</summary>
    private void HandleTcp(uint source, uint destination, ReadOnlySpan<byte> tcp)
    {
        if (tcp.Length < Packet.TcpHeader)
        {
            return;
        }

        ushort sourcePort = Packet.ReadU16(tcp, 0);
        ushort destinationPort = Packet.ReadU16(tcp, 2);
        uint sequence = Packet.ReadU32(tcp, 4);
        uint acknowledgement = Packet.ReadU32(tcp, 8);
        int headerLength = (tcp[12] >> 4) * 4;
        byte flags = tcp[13];
        ushort window = Packet.ReadU16(tcp, 14);
        if (headerLength < Packet.TcpHeader || headerLength > tcp.Length)
        {
            return;
        }

        ReadOnlySpan<byte> payload = tcp[headerLength..];

        var key = (sourcePort, destination, destinationPort);
        if (_tcp.TryGetValue(key, out NatTcpConnection? connection))
        {
            connection.HandleSegment(sequence, acknowledgement, flags, window, payload);
            if (connection.Closed)
            {
                _tcp.Remove(key);
            }

            return;
        }

        if ((flags & (TcpFlags.Syn | TcpFlags.Ack | TcpFlags.Rst)) == TcpFlags.Syn)
        {
            if (InSubnet(destination) || destination == Broadcast)
            {
                // Nothing listens on the router. A reset says so at once,
                // where silence would leave the guest waiting a minute.
                RejectSegment(source, destination, sourcePort, destinationPort, sequence, acknowledgement, flags, payload.Length);
                return;
            }

            int? maximumSegment = ReadMaximumSegment(tcp[Packet.TcpHeader..headerLength]);
            connection = new NatTcpConnection(this, source, sourcePort, destination, destinationPort);
            _tcp[key] = connection;
            connection.OpenFromGuest(_host.ConnectTcp(Packet.ToAddress(destination), destinationPort),
                                     sequence, window, maximumSegment);
            return;
        }

        // A segment for a connection this side knows nothing about: left
        // over from before a reset, most likely. The reply that ends it.
        if ((flags & TcpFlags.Rst) == 0)
        {
            RejectSegment(source, destination, sourcePort, destinationPort, sequence, acknowledgement, flags, payload.Length);
        }
    }

    /// <summary>Sends the reset that terminates a segment without a known local connection.</summary>
    private void RejectSegment(uint source, uint destination, ushort sourcePort, ushort destinationPort,
                               uint sequence, uint acknowledgement, byte flags, int payloadLength)
    {
        uint ack = sequence + (uint)payloadLength
                 + ((flags & TcpFlags.Syn) != 0 ? 1u : 0) + ((flags & TcpFlags.Fin) != 0 ? 1u : 0);
        uint seq = (flags & TcpFlags.Ack) != 0 ? acknowledgement : 0;
        byte[] reset = Packet.BuildTcp(destination, source, destinationPort, sourcePort,
                                       seq, ack, (byte)(TcpFlags.Rst | TcpFlags.Ack), 0, ReadOnlySpan<byte>.Empty);
        InjectIp(destination, source, Packet.ProtocolTcp, reset);
    }

    /// <summary>Reads the peer's TCP maximum-segment option from an otherwise opaque option area.</summary>
    private static int? ReadMaximumSegment(ReadOnlySpan<byte> options)
    {
        for (int i = 0; i < options.Length;)
        {
            byte kind = options[i];
            if (kind == 0)
            {
                break;
            }

            if (kind == 1)
            {
                i++;
                continue;
            }

            if (i + 1 >= options.Length)
            {
                break;
            }

            int length = options[i + 1];
            if (length < 2 || i + length > options.Length)
            {
                break;
            }

            if (kind == 2 && length == 4)
            {
                return Packet.ReadU16(options, i + 2);
            }

            i += length;
        }

        return null;
    }

    public void Dispose()
    {
        Drain();
        foreach (NatTcpConnection connection in _tcp.Values)
        {
            connection.Close();
        }

        _tcp.Clear();
        foreach (UdpMapping mapping in _udp.Values)
        {
            mapping.Close();
        }

        _udp.Clear();
        foreach (IDisposable listener in _listeners)
        {
            listener.Dispose();
        }

        _listeners.Clear();
    }
}
