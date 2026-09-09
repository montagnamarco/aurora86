using System.Net;
using System.Net.Sockets;
using Aurora386.Core.Devices.Network.Nat;

namespace Aurora386.Tests;

/// <summary>
/// The real socket layer, against servers on this machine's loopback: the
/// one part of the NAT the fake host cannot vouch for.
/// </summary>
public class SocketHostNetworkTests
{
    private sealed class Sink : IHostTcpSink
    {
        public ManualResetEventSlim ConnectedEvent { get; } = new();
        public ManualResetEventSlim ClosedEvent { get; } = new();
        public ManualResetEventSlim FailedEvent { get; } = new();
        public List<byte> Data { get; } = new();
        public ManualResetEventSlim DataEvent { get; } = new();

        public void Connected() => ConnectedEvent.Set();
        public void ConnectFailed() => FailedEvent.Set();
        public void Received(byte[] data) { lock (Data) Data.AddRange(data); DataEvent.Set(); }
        public void RemoteClosed() => ClosedEvent.Set();
        public void Failed() => FailedEvent.Set();
    }

    [Fact]
    public void TcpConnectionExchangesDataAndSeesTheRemoteClose()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[64];
            int count = await stream.ReadAsync(buffer);
            await stream.WriteAsync(buffer.AsMemory(0, count));
            client.Client.Shutdown(SocketShutdown.Send);
        });

        var host = new SocketHostNetwork();
        IHostTcpStream connection = host.ConnectTcp(IPAddress.Loopback, port);
        var sink = new Sink();
        connection.Start(sink);
        Assert.True(sink.ConnectedEvent.Wait(5000));
        connection.Send("echo"u8);
        Assert.True(sink.DataEvent.Wait(5000));
        Assert.True(sink.ClosedEvent.Wait(5000));
        Assert.Equal("echo"u8.ToArray(), sink.Data.ToArray());
        connection.Close();
        listener.Stop();
        Assert.True(server.Wait(5000));
    }

    [Fact]
    public void ConnectionToAClosedPortFails()
    {
        // A port nothing listens on: bind one, learn its number, release it.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var sink = new Sink();
        new SocketHostNetwork().ConnectTcp(IPAddress.Loopback, port).Start(sink);
        Assert.True(sink.FailedEvent.Wait(5000));
        Assert.False(sink.ConnectedEvent.IsSet);
    }

    [Fact]
    public void UdpSocketSendsAndReceives()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;

        var received = new ManualResetEventSlim();
        byte[]? got = null;
        IPEndPoint? from = null;
        var sink = new UdpSink(data => { got = data; received.Set(); }, f => from = f);

        var host = new SocketHostNetwork();
        IHostUdpSocket socket = host.OpenUdp();
        socket.Start(sink);
        socket.Send("ping"u8, new IPEndPoint(IPAddress.Loopback, port));

        IPEndPoint clientEnd = new(IPAddress.Any, 0);
        byte[] request = server.Receive(ref clientEnd);
        Assert.Equal("ping"u8.ToArray(), request);
        server.Send("pong"u8.ToArray(), clientEnd);

        Assert.True(received.Wait(5000));
        Assert.Equal("pong"u8.ToArray(), got);
        Assert.Equal(port, from!.Port);
        socket.Close();
    }

    [Fact]
    public void ListenerHandsOverAcceptedConnections()
    {
        var host = new SocketHostNetwork();
        var accepted = new ManualResetEventSlim();
        IHostTcpStream? stream = null;
        // Pick a free port the same way as above.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using IDisposable listener = host.ListenTcp(port, (s, _) => { stream = s; accepted.Set(); });
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        Assert.True(accepted.Wait(5000));

        var sink = new Sink();
        stream!.Start(sink);
        client.GetStream().Write("hello"u8);
        Assert.True(sink.DataEvent.Wait(5000));
        Assert.Equal("hello"u8.ToArray(), sink.Data.ToArray());
        stream.Close();
    }

    [Fact]
    public void HostHasAtLeastOneNameServer()
    {
        Assert.NotEmpty(new SocketHostNetwork().DnsServers);
    }

    private sealed class UdpSink(Action<byte[]> onData, Action<IPEndPoint> onFrom) : IHostUdpSink
    {
        public void Received(byte[] data, IPEndPoint from) { onFrom(from); onData(data); }
    }
}
