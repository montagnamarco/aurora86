using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Aurora386.Core.Devices.Network.Nat;

/// <summary>
/// The host's network reached through ordinary sockets, which is what a
/// running emulator uses.
///
/// Nothing here needs privileges: a user-mode NAT looks to the host like any
/// other program opening connections, which is the whole reason for building
/// one rather than bridging the card onto the physical network. The price is
/// that the host cannot reach the guest unless a port is forwarded, and the
/// guest sees every remote address through the host's own.
/// </summary>
public sealed class SocketHostNetwork : IHostNetwork
{
    private IReadOnlyList<IPAddress>? _dnsServers;

    public IHostTcpStream ConnectTcp(IPAddress address, int port) => new TcpStream(address, port);

    public IHostUdpSocket OpenUdp(int localPort = 0) => new UdpSocket(localPort);

    public IDisposable ListenTcp(int port, Action<IHostTcpStream, IPEndPoint> accepted)
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Bind(new IPEndPoint(IPAddress.Any, port));
        listener.Listen(8);

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    Socket client = await listener.AcceptAsync().ConfigureAwait(false);
                    var remote = client.RemoteEndPoint as IPEndPoint ?? new IPEndPoint(IPAddress.Loopback, 0);
                    accepted(new TcpStream(client), remote);
                }
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        });

        return listener;
    }

    public void Ping(IPAddress address, ReadOnlySpan<byte> payload, Action<bool> completed)
    {
        byte[] copy = payload.ToArray();
        _ = Task.Run(async () =>
        {
            bool ok;
            try
            {
                using var ping = new global::System.Net.NetworkInformation.Ping();
                PingReply reply = await ping.SendPingAsync(address, 2000, copy).ConfigureAwait(false);
                ok = reply.Status == IPStatus.Success;
            }
            catch (Exception)
            {
                // A host that refuses unprivileged ICMP, or has no route: the
                // guest sees the request time out, which is the truth.
                ok = false;
            }
            completed(ok);
        });
    }

    public IReadOnlyList<IPAddress> DnsServers
    {
        get
        {
            if (_dnsServers is not null) return _dnsServers;

            var servers = new List<IPAddress>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (IPAddress server in nic.GetIPProperties().DnsAddresses)
                        if (server.AddressFamily == AddressFamily.InterNetwork && !servers.Contains(server))
                            servers.Add(server);
                }
            }
            catch (Exception)
            {
                // Some platforms cannot enumerate; the fallback below serves.
            }

            // A host with no configured resolver still deserves a working
            // guest, so a public one stands in.
            if (servers.Count == 0) servers.Add(IPAddress.Parse("1.1.1.1"));
            return _dnsServers = servers;
        }
    }

    /// <summary>
    /// One TCP connection. Sends are chained so that they leave in order
    /// without ever blocking the emulation thread that asked for them.
    /// </summary>
    private sealed class TcpStream : IHostTcpStream
    {
        private readonly Socket _socket;
        private readonly IPEndPoint? _remote;
        private readonly object _sendLock = new();
        private Task _sendChain = Task.CompletedTask;
        private IHostTcpSink? _sink;

        public TcpStream(IPAddress address, int port)
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _socket.NoDelay = true;
            _remote = new IPEndPoint(address, port);
        }

        public TcpStream(Socket accepted)
        {
            _socket = accepted;
            _socket.NoDelay = true;
        }

        public void Start(IHostTcpSink sink)
        {
            _sink = sink;
            _ = Task.Run(async () =>
            {
                if (_remote is not null)
                {
                    try
                    {
                        await _socket.ConnectAsync(_remote).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        sink.ConnectFailed();
                        return;
                    }
                    sink.Connected();
                }

                byte[] buffer = new byte[16 * 1024];
                try
                {
                    while (true)
                    {
                        int count = await _socket.ReceiveAsync(buffer, SocketFlags.None).ConfigureAwait(false);
                        if (count == 0)
                        {
                            sink.RemoteClosed();
                            return;
                        }
                        sink.Received(buffer[..count]);
                    }
                }
                catch (Exception)
                {
                    sink.Failed();
                }
            });
        }

        public void Send(ReadOnlySpan<byte> data)
        {
            byte[] copy = data.ToArray();
            lock (_sendLock)
            {
                _sendChain = _sendChain.ContinueWith(async _ =>
                {
                    try
                    {
                        int sent = 0;
                        while (sent < copy.Length)
                            sent += await _socket.SendAsync(copy.AsMemory(sent), SocketFlags.None).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        _sink?.Failed();
                    }
                }).Unwrap();
            }
        }

        public void ShutdownSend()
        {
            lock (_sendLock)
            {
                _sendChain = _sendChain.ContinueWith(_ =>
                {
                    try { _socket.Shutdown(SocketShutdown.Send); }
                    catch (Exception) { }
                });
            }
        }

        public void Close()
        {
            try { _socket.Close(); }
            catch (Exception) { }
        }
    }

    private sealed class UdpSocket : IHostUdpSocket
    {
        private readonly Socket _socket;

        public UdpSocket(int localPort)
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(IPAddress.Any, localPort));
        }

        public void Start(IHostUdpSink sink)
        {
            _ = Task.Run(async () =>
            {
                byte[] buffer = new byte[65536];
                try
                {
                    while (true)
                    {
                        SocketReceiveFromResult result = await _socket
                            .ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0))
                            .ConfigureAwait(false);
                        if (result.RemoteEndPoint is IPEndPoint from)
                            sink.Received(buffer[..result.ReceivedBytes], from);
                    }
                }
                catch (Exception)
                {
                    // The socket was closed under us, which is how a mapping
                    // expires; nothing to report.
                }
            });
        }

        public void Send(ReadOnlySpan<byte> data, IPEndPoint destination)
        {
            try { _socket.SendTo(data, SocketFlags.None, destination); }
            catch (Exception) { }
        }

        public void Close()
        {
            try { _socket.Close(); }
            catch (Exception) { }
        }
    }
}
