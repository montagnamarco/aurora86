using System.Net;

namespace Aurora86.Core.Devices.Network.Nat;

/// <summary>
/// The host's network, as the NAT sees it: a way to open connections and
/// datagram sockets on behalf of the guest, and to learn where the host
/// resolves names.
///
/// The NAT is a small TCP/IP stack talking to an emulated card on one side
/// and to ordinary sockets on the other. The socket side is put behind this
/// interface so that the stack can be tested without touching the network
/// at all: a test plays the part of the remote end and checks every segment
/// the guest is handed, byte by byte, which no real server would let it do.
///
/// Everything here reports back through a sink, from whatever thread the
/// operating system chooses. The NAT never runs its own logic on that
/// thread: it queues what it is told and deals with it the next time the
/// card polls, so that the whole stack stays single-threaded.
/// </summary>
public interface IHostNetwork
{
    /// <summary>
    /// Prepares a connection to a remote address. Nothing happens on the wire
    /// until <see cref="IHostTcpStream.Start"/> is called with a sink.
    /// </summary>
    IHostTcpStream ConnectTcp(IPAddress address, int port);

    /// <summary>
    /// Opens a datagram socket. Port zero means any free one, which is what
    /// outgoing traffic wants; a fixed port is for forwarding inbound.
    /// </summary>
    IHostUdpSocket OpenUdp(int localPort = 0);

    /// <summary>
    /// Accepts connections on a host port. Each one arrives already
    /// connected, and starts delivering data once it is given a sink.
    /// </summary>
    IDisposable ListenTcp(int port, Action<IHostTcpStream, IPEndPoint> accepted);

    /// <summary>Sends an echo request and says whether a reply came back.</summary>
    void Ping(IPAddress address, ReadOnlySpan<byte> payload, Action<bool> completed);

    /// <summary>The name servers the host itself uses, IPv4 only.</summary>
    IReadOnlyList<IPAddress> DnsServers { get; }
}

public interface IHostTcpStream
{
    /// <summary>
    /// Connects, if not yet connected, and begins delivering to the sink.
    /// The sink hears <see cref="IHostTcpSink.Connected"/> only for a
    /// connection this side opened; an accepted one is connected already.
    /// </summary>
    void Start(IHostTcpSink sink);

    /// <summary>Queues data for the remote end. Never blocks the caller.</summary>
    void Send(ReadOnlySpan<byte> data);

    /// <summary>Tells the remote end no more data is coming, keeping the read side open.</summary>
    void ShutdownSend();

    void Close();
}

public interface IHostTcpSink
{
    void Connected();
    void ConnectFailed();
    void Received(byte[] data);

    /// <summary>The remote end has sent everything it is going to send.</summary>
    void RemoteClosed();

    /// <summary>The connection broke: a reset, a timeout, a cable pulled.</summary>
    void Failed();
}

public interface IHostUdpSocket
{
    void Start(IHostUdpSink sink);
    void Send(ReadOnlySpan<byte> data, IPEndPoint destination);
    void Close();
}

public interface IHostUdpSink
{
    void Received(byte[] data, IPEndPoint from);
}
