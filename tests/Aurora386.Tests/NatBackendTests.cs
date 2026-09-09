using System.Net;
using Aurora386.Core.Devices.Network;
using Aurora386.Core.Devices.Network.Nat;

namespace Aurora386.Tests;

/// <summary>
/// Tests for the user-mode NAT, played from the guest's side of the wire.
///
/// The guest is a hand-built packet at a time; the host's network is a
/// fake that records what the NAT asked of it and lets the test answer as
/// the remote end. Between them, every frame the guest is handed can be
/// checked byte by byte, which is what makes a TCP implementation
/// trustworthy: not that a transfer worked once, but that the sequence
/// numbers, acknowledgements and flags in each segment are the ones the
/// standard prescribes.
/// </summary>
public class NatBackendTests
{
    private static readonly byte[] GuestMac = [0x02, 0x00, 0x41, 0x55, 0x52, 0x41];
    private static readonly byte[] BroadcastMac = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
    private const uint Remote = 0x5DB8D822; // 93.184.216.34

    // --- the fake host --------------------------------------------------------

    private sealed class FakeHost : IHostNetwork
    {
        public List<FakeStream> Connections { get; } = new();
        public List<FakeUdp> Sockets { get; } = new();
        public Dictionary<int, Action<IHostTcpStream, IPEndPoint>> Listeners { get; } = new();
        public List<(IPAddress Address, byte[] Payload, Action<bool> Completed)> Pings { get; } = new();
        public List<IPAddress> DnsServers { get; } = [IPAddress.Parse("192.168.1.1")];

        IReadOnlyList<IPAddress> IHostNetwork.DnsServers => DnsServers;

        public IHostTcpStream ConnectTcp(IPAddress address, int port)
        {
            var stream = new FakeStream { Address = address, Port = port };
            Connections.Add(stream);
            return stream;
        }

        public IHostUdpSocket OpenUdp(int localPort = 0)
        {
            var socket = new FakeUdp { LocalPort = localPort };
            Sockets.Add(socket);
            return socket;
        }

        public IDisposable ListenTcp(int port, Action<IHostTcpStream, IPEndPoint> accepted)
        {
            Listeners[port] = accepted;
            return new Disposer(() => Listeners.Remove(port));
        }

        public void Ping(IPAddress address, ReadOnlySpan<byte> payload, Action<bool> completed) =>
            Pings.Add((address, payload.ToArray(), completed));

        private sealed class Disposer(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private sealed class FakeStream : IHostTcpStream
    {
        public IPAddress Address { get; init; } = IPAddress.None;
        public int Port { get; init; }
        public IHostTcpSink? Sink { get; private set; }
        public List<byte> Sent { get; } = new();
        public bool ShutdownSent { get; private set; }
        public bool IsClosed { get; private set; }

        public void Start(IHostTcpSink sink) => Sink = sink;
        public void Send(ReadOnlySpan<byte> data) => Sent.AddRange(data.ToArray());
        public void ShutdownSend() => ShutdownSent = true;
        public void Close() => IsClosed = true;
    }

    private sealed class FakeUdp : IHostUdpSocket
    {
        public int LocalPort { get; init; }
        public IHostUdpSink? Sink { get; private set; }
        public List<(byte[] Data, IPEndPoint To)> Sent { get; } = new();
        public bool IsClosed { get; private set; }

        public void Start(IHostUdpSink sink) => Sink = sink;
        public void Send(ReadOnlySpan<byte> data, IPEndPoint destination) => Sent.Add((data.ToArray(), destination));
        public void Close() => IsClosed = true;
    }

    // --- the guest ------------------------------------------------------------

    private sealed class Bench
    {
        public FakeHost Host { get; } = new();
        public NatBackend Nat { get; }
        public long Clock { get; set; } = 1000;

        public Bench()
        {
            Nat = new NatBackend(Host, () => Clock);
        }

        /// <summary>Every frame the NAT has queued for the guest, in order.</summary>
        public List<byte[]> Collect()
        {
            var frames = new List<byte[]>();
            byte[] buffer = new byte[1600];
            while (Nat.TryReceive(buffer, out int length)) frames.Add(buffer[..length]);
            return frames;
        }

        public byte[]? Next()
        {
            byte[] buffer = new byte[1600];
            return Nat.TryReceive(buffer, out int length) ? buffer[..length] : null;
        }

        public void SendIp(uint source, uint destination, byte protocol, byte[] payload) =>
            Nat.Send(Packet.BuildIp(NatBackend.GatewayMac, GuestMac, source, destination, protocol, payload));

        public void SendTcp(ushort guestPort, uint remote, ushort remotePort, uint seq, uint ack, byte flags,
                            byte[]? payload = null, ushort window = 8192, ushort? mss = null) =>
            SendIp(NatBackend.Guest, remote, Packet.ProtocolTcp,
                   Packet.BuildTcp(NatBackend.Guest, remote, guestPort, remotePort, seq, ack, flags, window,
                                   payload ?? [], mss));
    }

    /// <summary>The fields of a TCP segment the guest received, pulled out of the frame.</summary>
    private readonly record struct Tcp(uint Source, uint Destination, ushort SourcePort, ushort DestinationPort,
                                       uint Seq, uint Ack, byte Flags, ushort Window, byte[] Payload)
    {
        public static Tcp Parse(byte[] frame)
        {
            Assert.Equal(Packet.TypeIp, Packet.ReadU16(frame, 12));
            ReadOnlySpan<byte> ip = frame.AsSpan(14);
            Assert.Equal(Packet.ProtocolTcp, ip[9]);
            Assert.Equal(0, Packet.Checksum(ip[..20]));
            int total = Packet.ReadU16(ip, 2);
            ReadOnlySpan<byte> tcp = ip[20..total];
            uint source = Packet.ReadU32(ip, 12), destination = Packet.ReadU32(ip, 16);
            Assert.Equal(0, Packet.Checksum(tcp, Packet.PseudoHeader(source, destination, Packet.ProtocolTcp, tcp.Length)));
            int header = (tcp[12] >> 4) * 4;
            return new Tcp(source, destination, Packet.ReadU16(tcp, 0), Packet.ReadU16(tcp, 2),
                           Packet.ReadU32(tcp, 4), Packet.ReadU32(tcp, 8), tcp[13], Packet.ReadU16(tcp, 14),
                           tcp[header..].ToArray());
        }
    }

    private static (uint Source, uint Destination, ushort SourcePort, ushort DestinationPort, byte[] Payload) ParseUdp(byte[] frame)
    {
        ReadOnlySpan<byte> ip = frame.AsSpan(14);
        Assert.Equal(Packet.ProtocolUdp, ip[9]);
        int total = Packet.ReadU16(ip, 2);
        ReadOnlySpan<byte> udp = ip[20..total];
        uint source = Packet.ReadU32(ip, 12), destination = Packet.ReadU32(ip, 16);
        Assert.Equal(0, Packet.Checksum(udp, Packet.PseudoHeader(source, destination, Packet.ProtocolUdp, udp.Length)));
        return (source, destination, Packet.ReadU16(udp, 0), Packet.ReadU16(udp, 2), udp[8..].ToArray());
    }

    // --- ARP ---------------------------------------------------------------------

    [Fact]
    public void ArpRequestForGatewayIsAnsweredWithGatewayMac()
    {
        var bench = new Bench();
        bench.Nat.Send(Packet.BuildArp(BroadcastMac, GuestMac, 1, GuestMac, NatBackend.Guest, new byte[6], NatBackend.Gateway));

        byte[]? reply = bench.Next();
        Assert.NotNull(reply);
        Assert.Equal(GuestMac, reply[..6]);
        Assert.Equal(NatBackend.GatewayMac, reply[6..12]);
        Assert.Equal(Packet.TypeArp, Packet.ReadU16(reply, 12));
        Assert.Equal(2, Packet.ReadU16(reply, 14 + 6));
        Assert.Equal(NatBackend.GatewayMac, reply[22..28]);
        Assert.Equal(NatBackend.Gateway, Packet.ReadU32(reply, 14 + 14));
        Assert.Equal(NatBackend.Guest, Packet.ReadU32(reply, 14 + 24));
    }

    [Fact]
    public void ArpProbeForGuestsOwnAddressGoesUnanswered()
    {
        var bench = new Bench();
        // A probe: sender 0.0.0.0 asking for the address it wants to take.
        bench.Nat.Send(Packet.BuildArp(BroadcastMac, GuestMac, 1, GuestMac, 0, new byte[6], NatBackend.Guest));
        Assert.Null(bench.Next());
        // And an announcement, sender equal to target.
        bench.Nat.Send(Packet.BuildArp(BroadcastMac, GuestMac, 1, GuestMac, NatBackend.Guest, new byte[6], NatBackend.Guest));
        Assert.Null(bench.Next());
    }

    [Fact]
    public void ArpForAddressOutsideSubnetGoesUnanswered()
    {
        var bench = new Bench();
        bench.Nat.Send(Packet.BuildArp(BroadcastMac, GuestMac, 1, GuestMac, NatBackend.Guest, new byte[6], Remote));
        Assert.Null(bench.Next());
    }

    // --- DHCP --------------------------------------------------------------------

    private static byte[] Dhcp(byte messageType, uint xid = 0x12345678)
    {
        byte[] bootp = new byte[300];
        bootp[0] = 1;
        bootp[1] = 1;
        bootp[2] = 6;
        Packet.WriteU32(bootp, 4, xid);
        GuestMac.CopyTo(bootp, 28);
        Packet.WriteU32(bootp, 236, 0x63825363);
        bootp[240] = 53; bootp[241] = 1; bootp[242] = messageType;
        bootp[243] = 255;
        return bootp;
    }

    [Fact]
    public void DhcpDiscoverGetsAnOfferOfTheGuestAddress()
    {
        var bench = new Bench();
        bench.SendIp(0, NatBackend.Broadcast, Packet.ProtocolUdp, Packet.BuildUdp(0, NatBackend.Broadcast, 68, 67, Dhcp(1)));

        byte[]? frame = bench.Next();
        Assert.NotNull(frame);
        Assert.Equal(BroadcastMac, frame[..6]);
        var (source, destination, sourcePort, destinationPort, bootp) = ParseUdp(frame);
        Assert.Equal(NatBackend.Gateway, source);
        Assert.Equal(NatBackend.Broadcast, destination);
        Assert.Equal(67, sourcePort);
        Assert.Equal(68, destinationPort);

        Assert.Equal(2, bootp[0]);
        Assert.Equal(0x12345678u, Packet.ReadU32(bootp, 4));
        Assert.Equal(NatBackend.Guest, Packet.ReadU32(bootp, 16));
        Assert.Equal(GuestMac, bootp[28..34]);

        var options = ReadDhcpOptions(bootp);
        Assert.Equal([2], options[53]);
        Assert.Equal(NatBackend.Gateway, Packet.ReadU32(options[54], 0));
        Assert.Equal(NatBackend.Gateway, Packet.ReadU32(options[3], 0));
        Assert.Equal(NatBackend.NameServer, Packet.ReadU32(options[6], 0));
        Assert.Equal(0xFFFFFF00u, Packet.ReadU32(options[1], 0));
    }

    [Fact]
    public void DhcpRequestIsAcknowledged()
    {
        var bench = new Bench();
        bench.SendIp(0, NatBackend.Broadcast, Packet.ProtocolUdp, Packet.BuildUdp(0, NatBackend.Broadcast, 68, 67, Dhcp(3)));
        byte[]? frame = bench.Next();
        Assert.NotNull(frame);
        var options = ReadDhcpOptions(ParseUdp(frame).Payload);
        Assert.Equal([5], options[53]);
        Assert.Equal(86400u, Packet.ReadU32(options[51], 0));
    }

    private static Dictionary<byte, byte[]> ReadDhcpOptions(byte[] bootp)
    {
        var options = new Dictionary<byte, byte[]>();
        for (int i = 240; i < bootp.Length && bootp[i] != 255;)
        {
            int length = bootp[i + 1];
            options[bootp[i]] = bootp[(i + 2)..(i + 2 + length)];
            i += 2 + length;
        }
        return options;
    }

    // --- ICMP --------------------------------------------------------------------

    private static byte[] Echo(ushort id, ushort seq, byte[] data)
    {
        byte[] icmp = new byte[8 + data.Length];
        icmp[0] = 8;
        Packet.WriteU16(icmp, 4, id);
        Packet.WriteU16(icmp, 6, seq);
        data.CopyTo(icmp, 8);
        Packet.WriteU16(icmp, 2, Packet.Checksum(icmp));
        return icmp;
    }

    [Fact]
    public void PingToGatewayIsAnsweredAtOnce()
    {
        var bench = new Bench();
        bench.SendIp(NatBackend.Guest, NatBackend.Gateway, Packet.ProtocolIcmp, Echo(0x1234, 7, "ciao"u8.ToArray()));

        byte[]? frame = bench.Next();
        Assert.NotNull(frame);
        ReadOnlySpan<byte> ip = frame.AsSpan(14);
        Assert.Equal(NatBackend.Gateway, Packet.ReadU32(ip, 12));
        Assert.Equal(NatBackend.Guest, Packet.ReadU32(ip, 16));
        ReadOnlySpan<byte> icmp = ip[20..];
        Assert.Equal(0, icmp[0]);
        Assert.Equal(0, Packet.Checksum(icmp));
        Assert.Equal(0x1234, Packet.ReadU16(icmp, 4));
        Assert.Equal(7, Packet.ReadU16(icmp, 6));
        Assert.Equal("ciao"u8.ToArray(), icmp[8..].ToArray());
        Assert.Empty(bench.Host.Pings);
    }

    [Fact]
    public void PingToOutsideGoesThroughTheHostAndComesBackWithTheGuestsIdentifier()
    {
        var bench = new Bench();
        bench.SendIp(NatBackend.Guest, Remote, Packet.ProtocolIcmp, Echo(0x4242, 3, "hello"u8.ToArray()));

        Assert.Null(bench.Next());
        var ping = Assert.Single(bench.Host.Pings);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), ping.Address);
        Assert.Equal("hello"u8.ToArray(), ping.Payload);

        ping.Completed(true);
        byte[]? frame = bench.Next();
        Assert.NotNull(frame);
        ReadOnlySpan<byte> ip = frame.AsSpan(14);
        Assert.Equal(Remote, Packet.ReadU32(ip, 12));
        Assert.Equal(0, ip[20]);
        Assert.Equal(0x4242, Packet.ReadU16(ip, 24));
        Assert.Equal(3, Packet.ReadU16(ip, 26));
    }

    [Fact]
    public void PingThatGetsNoReplyProducesNothing()
    {
        var bench = new Bench();
        bench.SendIp(NatBackend.Guest, Remote, Packet.ProtocolIcmp, Echo(1, 1, []));
        bench.Host.Pings[0].Completed(false);
        Assert.Null(bench.Next());
    }

    // --- UDP ---------------------------------------------------------------------

    [Fact]
    public void UdpDatagramGoesOutThroughAHostSocketAndTheReplyComesBackToTheSamePort()
    {
        var bench = new Bench();
        bench.SendIp(NatBackend.Guest, Remote, Packet.ProtocolUdp,
                     Packet.BuildUdp(NatBackend.Guest, Remote, 4000, 123, "ntp?"u8.ToArray()));

        var socket = Assert.Single(bench.Host.Sockets);
        Assert.Equal(0, socket.LocalPort);
        var (data, to) = Assert.Single(socket.Sent);
        Assert.Equal("ntp?"u8.ToArray(), data);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("93.184.216.34"), 123), to);

        socket.Sink!.Received("time!"u8.ToArray(), to);
        byte[]? frame = bench.Next();
        Assert.NotNull(frame);
        Assert.Equal(GuestMac, frame[..6]);
        var reply = ParseUdp(frame);
        Assert.Equal(Remote, reply.Source);
        Assert.Equal(NatBackend.Guest, reply.Destination);
        Assert.Equal(123, reply.SourcePort);
        Assert.Equal(4000, reply.DestinationPort);
        Assert.Equal("time!"u8.ToArray(), reply.Payload);
    }

    [Fact]
    public void SecondDatagramFromSamePortReusesTheSocket()
    {
        var bench = new Bench();
        for (int i = 0; i < 2; i++)
            bench.SendIp(NatBackend.Guest, Remote, Packet.ProtocolUdp,
                         Packet.BuildUdp(NatBackend.Guest, Remote, 4000, 123, [(byte)i]));
        Assert.Single(bench.Host.Sockets);
        Assert.Equal(2, bench.Host.Sockets[0].Sent.Count);
    }

    [Fact]
    public void IdleUdpMappingIsClosedAfterAMinute()
    {
        var bench = new Bench();
        bench.SendIp(NatBackend.Guest, Remote, Packet.ProtocolUdp, Packet.BuildUdp(NatBackend.Guest, Remote, 4000, 123, [1]));
        bench.Clock += 61_000;
        bench.Next();
        Assert.True(bench.Host.Sockets[0].IsClosed);
    }

    [Fact]
    public void DnsQueryIsForwardedToTheHostsServerAndTheReplyComesFromTheVirtualOne()
    {
        var bench = new Bench();
        bench.SendIp(NatBackend.Guest, NatBackend.NameServer, Packet.ProtocolUdp,
                     Packet.BuildUdp(NatBackend.Guest, NatBackend.NameServer, 1025, 53, "query"u8.ToArray()));

        var socket = Assert.Single(bench.Host.Sockets);
        var (_, to) = Assert.Single(socket.Sent);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.1.1"), 53), to);

        socket.Sink!.Received("answer"u8.ToArray(), to);
        byte[]? frame = bench.Next();
        Assert.NotNull(frame);
        var reply = ParseUdp(frame);
        Assert.Equal(NatBackend.NameServer, reply.Source);
        Assert.Equal(53, reply.SourcePort);
        Assert.Equal(1025, reply.DestinationPort);
        Assert.Equal("answer"u8.ToArray(), reply.Payload);
    }

    [Fact]
    public void UdpToAnUnknownAddressOnTheSubnetIsDropped()
    {
        var bench = new Bench();
        bench.SendIp(NatBackend.Guest, NatBackend.Gateway, Packet.ProtocolUdp,
                     Packet.BuildUdp(NatBackend.Guest, NatBackend.Gateway, 4000, 7, [1]));
        Assert.Empty(bench.Host.Sockets);
        Assert.Null(bench.Next());
    }

    // --- TCP, opened by the guest -----------------------------------------------

    /// <summary>Takes a connection through the handshake and returns the NAT's initial sequence number.</summary>
    private static uint Handshake(Bench bench, ushort guestPort = 3000, uint guestSeq = 1000)
    {
        bench.SendTcp(guestPort, Remote, 80, guestSeq, 0, TcpFlags.Syn, mss: 1460);
        var stream = bench.Host.Connections[^1];
        stream.Sink!.Connected();

        Tcp synAck = Tcp.Parse(bench.Next()!);
        Assert.Equal((byte)(TcpFlags.Syn | TcpFlags.Ack), synAck.Flags);
        Assert.Equal(guestSeq + 1, synAck.Ack);
        Assert.Equal(Remote, synAck.Source);
        Assert.Equal(80, synAck.SourcePort);
        Assert.Equal(guestPort, synAck.DestinationPort);

        bench.SendTcp(guestPort, Remote, 80, guestSeq + 1, synAck.Seq + 1, TcpFlags.Ack);
        Assert.Null(bench.Next());
        return synAck.Seq;
    }

    [Fact]
    public void SynOpensAHostConnectionAndNothingIsSaidUntilItSucceeds()
    {
        var bench = new Bench();
        bench.SendTcp(3000, Remote, 80, 1000, 0, TcpFlags.Syn);

        var stream = Assert.Single(bench.Host.Connections);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), stream.Address);
        Assert.Equal(80, stream.Port);
        Assert.NotNull(stream.Sink);
        Assert.Null(bench.Next());
        Assert.Equal(1, bench.Nat.ConnectionCount);
    }

    [Fact]
    public void RefusedConnectionIsResetForTheGuest()
    {
        var bench = new Bench();
        bench.SendTcp(3000, Remote, 80, 1000, 0, TcpFlags.Syn);
        bench.Host.Connections[0].Sink!.ConnectFailed();

        Tcp reset = Tcp.Parse(bench.Next()!);
        Assert.Equal((byte)(TcpFlags.Rst | TcpFlags.Ack), reset.Flags);
        Assert.Equal(1001u, reset.Ack);
        bench.Next();
        Assert.Equal(0, bench.Nat.ConnectionCount);
    }

    [Fact]
    public void DataFromGuestReachesTheHostSocketAndIsAcknowledged()
    {
        var bench = new Bench();
        uint iss = Handshake(bench);
        byte[] request = "GET / HTTP/1.0\r\n\r\n"u8.ToArray();
        bench.SendTcp(3000, Remote, 80, 1001, iss + 1, TcpFlags.Psh | TcpFlags.Ack, request);

        Assert.Equal(request, bench.Host.Connections[0].Sent.ToArray());
        Tcp ack = Tcp.Parse(bench.Next()!);
        Assert.Equal(TcpFlags.Ack, ack.Flags);
        Assert.Equal(1001u + (uint)request.Length, ack.Ack);
        Assert.Equal(iss + 1, ack.Seq);
        Assert.Empty(ack.Payload);
    }

    [Fact]
    public void RetransmittedDataFromGuestIsNotDeliveredTwice()
    {
        var bench = new Bench();
        uint iss = Handshake(bench);
        bench.SendTcp(3000, Remote, 80, 1001, iss + 1, TcpFlags.Ack, "abc"u8.ToArray());
        bench.SendTcp(3000, Remote, 80, 1001, iss + 1, TcpFlags.Ack, "abc"u8.ToArray());
        Assert.Equal("abc"u8.ToArray(), bench.Host.Connections[0].Sent.ToArray());
        // Both got an acknowledgement, and both say 1004.
        var frames = bench.Collect();
        Assert.Equal(2, frames.Count);
        Assert.All(frames, f => Assert.Equal(1004u, Tcp.Parse(f).Ack));
    }

    [Fact]
    public void DataFromHostIsSegmentedAndCarriesTheRightSequenceNumbers()
    {
        var bench = new Bench();
        uint iss = Handshake(bench);
        byte[] body = new byte[3000];
        for (int i = 0; i < body.Length; i++) body[i] = (byte)i;
        bench.Host.Connections[0].Sink!.Received(body);

        var frames = bench.Collect();
        Assert.Equal(3, frames.Count);
        Tcp first = Tcp.Parse(frames[0]), second = Tcp.Parse(frames[1]), third = Tcp.Parse(frames[2]);
        Assert.Equal(iss + 1, first.Seq);
        Assert.Equal(1460, first.Payload.Length);
        Assert.Equal(iss + 1 + 1460, second.Seq);
        Assert.Equal(1460, second.Payload.Length);
        Assert.Equal(iss + 1 + 2920, third.Seq);
        Assert.Equal(80, third.Payload.Length);
        Assert.Equal(body, first.Payload.Concat(second.Payload).Concat(third.Payload).ToArray());
        Assert.All(frames, f => Assert.True(f.Length <= 1514));
        Assert.All(frames, f => Assert.Equal(1001u, Tcp.Parse(f).Ack));
    }

    [Fact]
    public void HostDataStopsAtTheGuestsWindowAndResumesWhenItOpens()
    {
        var bench = new Bench();
        bench.SendTcp(3000, Remote, 80, 1000, 0, TcpFlags.Syn, window: 2000);
        bench.Host.Connections[0].Sink!.Connected();
        Tcp synAck = Tcp.Parse(bench.Next()!);
        uint iss = synAck.Seq;
        bench.SendTcp(3000, Remote, 80, 1001, iss + 1, TcpFlags.Ack, window: 2000);

        bench.Host.Connections[0].Sink!.Received(new byte[5000]);
        var frames = bench.Collect();
        Assert.Equal(2, frames.Count); // 1460 + 540 fills 2000
        Assert.Equal(540, Tcp.Parse(frames[1]).Payload.Length);

        // The guest acknowledges everything and opens the window wide.
        bench.SendTcp(3000, Remote, 80, 1001, iss + 1 + 2000, TcpFlags.Ack, window: 8192);
        frames = bench.Collect();
        Assert.Equal(3, frames.Count); // 3000 remaining: 1460 + 1460 + 80
        Assert.Equal(iss + 1 + 2000, Tcp.Parse(frames[0]).Seq);
    }

    [Fact]
    public void UnacknowledgedSegmentIsRetransmittedAfterATimeoutAndNotAfterAnAck()
    {
        var bench = new Bench();
        uint iss = Handshake(bench);
        bench.Host.Connections[0].Sink!.Received("data"u8.ToArray());
        Tcp sent = Tcp.Parse(bench.Next()!);

        bench.Clock += 500;
        Assert.Null(bench.Next());
        bench.Clock += 600;
        Tcp again = Tcp.Parse(bench.Next()!);
        Assert.Equal(sent.Seq, again.Seq);
        Assert.Equal(sent.Payload, again.Payload);

        bench.SendTcp(3000, Remote, 80, 1001, iss + 1 + 4, TcpFlags.Ack);
        bench.Clock += 5000;
        Assert.Null(bench.Next());
    }

    [Fact]
    public void GuestThatNeverAcknowledgesIsGivenUpOn()
    {
        var bench = new Bench();
        Handshake(bench);
        bench.Host.Connections[0].Sink!.Received("data"u8.ToArray());
        bench.Next();

        // Backoff doubles: 1, 2, 4, 8, 16, 32 seconds, then a reset.
        for (int i = 0; i < 6; i++)
        {
            bench.Clock += 1000 << i;
            Assert.NotNull(bench.Next());
        }
        bench.Clock += 1000 << 6;
        Tcp last = Tcp.Parse(bench.Next()!);
        Assert.Equal((byte)(TcpFlags.Rst | TcpFlags.Ack), last.Flags);
        Assert.True(bench.Host.Connections[0].IsClosed);
        bench.Next();
        Assert.Equal(0, bench.Nat.ConnectionCount);
    }

    [Fact]
    public void HostClosingSendsAFinAfterItsDataAndGuestFinCompletesTheClose()
    {
        var bench = new Bench();
        uint iss = Handshake(bench);
        var sink = bench.Host.Connections[0].Sink!;
        sink.Received("bye"u8.ToArray());
        sink.RemoteClosed();

        var frames = bench.Collect();
        Assert.Equal(2, frames.Count);
        Tcp data = Tcp.Parse(frames[0]), fin = Tcp.Parse(frames[1]);
        Assert.Equal("bye"u8.ToArray(), data.Payload);
        Assert.Equal((byte)(TcpFlags.Fin | TcpFlags.Ack), fin.Flags);
        Assert.Equal(iss + 4, fin.Seq);

        // The guest acknowledges the FIN and closes its own side.
        bench.SendTcp(3000, Remote, 80, 1001, iss + 5, TcpFlags.Fin | TcpFlags.Ack);
        Tcp ack = Tcp.Parse(bench.Next()!);
        Assert.Equal(TcpFlags.Ack, ack.Flags);
        Assert.Equal(1002u, ack.Ack);
        Assert.True(bench.Host.Connections[0].ShutdownSent);
        Assert.True(bench.Host.Connections[0].IsClosed);
        Assert.Equal(0, bench.Nat.ConnectionCount);
    }

    [Fact]
    public void GuestFinShutsDownTheHostSendSideOnly()
    {
        var bench = new Bench();
        uint iss = Handshake(bench);
        bench.SendTcp(3000, Remote, 80, 1001, iss + 1, TcpFlags.Fin | TcpFlags.Ack);
        Tcp ack = Tcp.Parse(bench.Next()!);
        Assert.Equal(1002u, ack.Ack);
        var stream = bench.Host.Connections[0];
        Assert.True(stream.ShutdownSent);
        Assert.False(stream.IsClosed);

        // The host can still speak, and the guest still acknowledges.
        stream.Sink!.Received("late"u8.ToArray());
        Tcp late = Tcp.Parse(bench.Next()!);
        Assert.Equal("late"u8.ToArray(), late.Payload);
        Assert.Equal(1002u, late.Ack);
    }

    [Fact]
    public void GuestResetClosesTheHostSocket()
    {
        var bench = new Bench();
        uint iss = Handshake(bench);
        bench.SendTcp(3000, Remote, 80, 1001, iss + 1, TcpFlags.Rst);
        Assert.True(bench.Host.Connections[0].IsClosed);
        Assert.Equal(0, bench.Nat.ConnectionCount);
        Assert.Null(bench.Next());
    }

    [Fact]
    public void HostFailureResetsTheGuest()
    {
        var bench = new Bench();
        Handshake(bench);
        bench.Host.Connections[0].Sink!.Failed();
        Tcp reset = Tcp.Parse(bench.Next()!);
        Assert.Equal((byte)(TcpFlags.Rst | TcpFlags.Ack), reset.Flags);
    }

    [Fact]
    public void SynToTheGatewayIsRefused()
    {
        var bench = new Bench();
        bench.SendTcp(3000, NatBackend.Gateway, 80, 1000, 0, TcpFlags.Syn);
        Tcp reset = Tcp.Parse(bench.Next()!);
        Assert.Equal((byte)(TcpFlags.Rst | TcpFlags.Ack), reset.Flags);
        Assert.Equal(1001u, reset.Ack);
        Assert.Empty(bench.Host.Connections);
    }

    [Fact]
    public void StraySegmentForNoConnectionIsReset()
    {
        var bench = new Bench();
        bench.SendTcp(3000, Remote, 80, 5000, 7000, TcpFlags.Ack, "x"u8.ToArray());
        Tcp reset = Tcp.Parse(bench.Next()!);
        Assert.Equal((byte)(TcpFlags.Rst | TcpFlags.Ack), reset.Flags);
        Assert.Equal(7000u, reset.Seq);
        Assert.Empty(bench.Host.Connections);
    }

    [Fact]
    public void TwoConnectionsToTheSameServerAreKeptApart()
    {
        var bench = new Bench();
        uint issA = Handshake(bench, 3000, 1000);
        uint issB = Handshake(bench, 3001, 5000);
        bench.Host.Connections[0].Sink!.Received("A"u8.ToArray());
        bench.Host.Connections[1].Sink!.Received("B"u8.ToArray());

        var frames = bench.Collect().Select(Tcp.Parse).ToList();
        Assert.Equal(3000, frames[0].DestinationPort);
        Assert.Equal(issA + 1, frames[0].Seq);
        Assert.Equal(3001, frames[1].DestinationPort);
        Assert.Equal(issB + 1, frames[1].Seq);
    }

    // --- TCP, opened from the host through a forwarded port ---------------------

    [Fact]
    public void ForwardedPortSendsTheGuestASynAndCompletesTheHandshake()
    {
        var bench = new Bench();
        bench.Nat.Forward(tcp: true, hostPort: 8080, guestPort: 80);
        var accept = bench.Host.Listeners[8080];

        var stream = new FakeStream();
        accept(stream, new IPEndPoint(IPAddress.Parse("192.168.1.50"), 40000));

        Tcp syn = Tcp.Parse(bench.Next()!);
        Assert.Equal(TcpFlags.Syn, syn.Flags);
        Assert.Equal(0xC0A80132u, syn.Source);
        Assert.Equal(40000, syn.SourcePort);
        Assert.Equal(80, syn.DestinationPort);
        Assert.Equal(0u, syn.Ack);
        Assert.NotNull(stream.Sink);

        bench.SendTcp(80, 0xC0A80132u, 40000, 9000, syn.Seq + 1, TcpFlags.Syn | TcpFlags.Ack);
        Tcp ack = Tcp.Parse(bench.Next()!);
        Assert.Equal(TcpFlags.Ack, ack.Flags);
        Assert.Equal(9001u, ack.Ack);
        Assert.Equal(syn.Seq + 1, ack.Seq);

        // Data both ways.
        stream.Sink!.Received("GET"u8.ToArray());
        Tcp data = Tcp.Parse(bench.Next()!);
        Assert.Equal("GET"u8.ToArray(), data.Payload);
        Assert.Equal(syn.Seq + 1, data.Seq);

        bench.SendTcp(80, 0xC0A80132u, 40000, 9001, syn.Seq + 4, TcpFlags.Ack, "200"u8.ToArray());
        Assert.Equal("200"u8.ToArray(), stream.Sent.ToArray());
    }

    [Fact]
    public void ConnectionFromTheHostItselfAppearsToComeFromTheGateway()
    {
        var bench = new Bench();
        bench.Nat.Forward(tcp: true, hostPort: 8080, guestPort: 80);
        bench.Host.Listeners[8080](new FakeStream(), new IPEndPoint(IPAddress.Loopback, 40001));
        Tcp syn = Tcp.Parse(bench.Next()!);
        Assert.Equal(NatBackend.Gateway, syn.Source);
    }

    [Fact]
    public void UnansweredForwardedSynIsRetransmitted()
    {
        var bench = new Bench();
        bench.Nat.Forward(tcp: true, hostPort: 8080, guestPort: 80);
        bench.Host.Listeners[8080](new FakeStream(), new IPEndPoint(IPAddress.Loopback, 40001));
        Tcp syn = Tcp.Parse(bench.Next()!);
        bench.Clock += 1100;
        Tcp again = Tcp.Parse(bench.Next()!);
        Assert.Equal(syn.Seq, again.Seq);
        Assert.Equal(TcpFlags.Syn, again.Flags);
    }

    [Fact]
    public void ForwardedUdpPortDeliversToTheGuestAndRepliesGoBackOut()
    {
        var bench = new Bench();
        bench.Nat.Forward(tcp: false, hostPort: 5000, guestPort: 5000);
        var socket = Assert.Single(bench.Host.Sockets);
        Assert.Equal(5000, socket.LocalPort);

        var from = new IPEndPoint(IPAddress.Parse("192.168.1.50"), 6000);
        socket.Sink!.Received("hi"u8.ToArray(), from);
        var arrived = ParseUdp(bench.Next()!);
        Assert.Equal(0xC0A80132u, arrived.Source);
        Assert.Equal(6000, arrived.SourcePort);
        Assert.Equal(5000, arrived.DestinationPort);

        bench.SendIp(NatBackend.Guest, 0xC0A80132u, Packet.ProtocolUdp,
                     Packet.BuildUdp(NatBackend.Guest, 0xC0A80132u, 5000, 6000, "yo"u8.ToArray()));
        var (data, to) = Assert.Single(socket.Sent);
        Assert.Equal("yo"u8.ToArray(), data);
        Assert.Equal(from, to);
    }

    // --- housekeeping ------------------------------------------------------------

    [Fact]
    public void FrameAddressedToItselfComesStraightBack()
    {
        var bench = new Bench();
        byte[] frame = new byte[60];
        GuestMac.CopyTo(frame, 0);
        GuestMac.CopyTo(frame, 6);
        frame[12] = 0x88; frame[13] = 0xB5;
        bench.Nat.Send(frame);
        Assert.Equal(frame, bench.Next());
    }

    [Fact]
    public void RepliesGoToTheMacTheGuestLastUsed()
    {
        var bench = new Bench();
        byte[] otherMac = [0x02, 0x11, 0x22, 0x33, 0x44, 0x55];
        bench.Nat.Send(Packet.BuildIp(NatBackend.GatewayMac, otherMac, NatBackend.Guest, NatBackend.Gateway,
                                      Packet.ProtocolIcmp, Echo(1, 1, [])));
        Assert.Equal(otherMac, bench.Next()![..6]);
    }

    [Fact]
    public void DisposeClosesEverything()
    {
        var bench = new Bench();
        Handshake(bench);
        bench.SendIp(NatBackend.Guest, Remote, Packet.ProtocolUdp, Packet.BuildUdp(NatBackend.Guest, Remote, 4000, 123, [1]));
        bench.Nat.Forward(tcp: true, hostPort: 8080, guestPort: 80);

        bench.Nat.Dispose();
        Assert.True(bench.Host.Connections[0].IsClosed);
        Assert.True(bench.Host.Sockets[0].IsClosed);
        Assert.Empty(bench.Host.Listeners);
    }

    [Fact]
    public void MachineSwapsBackendAndDisposesTheOldOne()
    {
        using var machine = new Aurora386.Core.Machine(1024 * 1024);
        var nat = new NatBackend(new FakeHost(), () => 0);
        machine.ConnectNetwork(nat);
        Assert.Same(nat, machine.Network.Backend);
        var loopback = new LoopbackBackend();
        machine.ConnectNetwork(loopback);
        Assert.Same(loopback, machine.Network.Backend);
    }
}
