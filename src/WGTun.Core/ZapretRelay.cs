using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using WGTun.Core.Experimental;

namespace WGTun.Core;

// Loopback SOCKS5 egress. Every external socket consumes a process-local reserved
// source port; no error path falls back to an ordinary unmarked socket.
public sealed class ZapretRelay : IAsyncDisposable
{
    private readonly ZapretEgress egress;
    private readonly Action<string>? trace;
    private readonly CancellationTokenSource stop = new();
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly SemaphoreSlim slots = new(128);
    private readonly ConcurrentDictionary<long, Task> clients = new();
    private readonly Task accept;
    private long sequence;
    public WindowsPortLease TcpPorts { get; }
    public WindowsPortLease UdpPorts { get; }
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public bool IsRunning => !stop.IsCancellationRequested && !accept.IsCompleted;

    public ZapretRelay(ZapretEgress egress, Action<string>? trace = null)
    {
        this.egress = egress; this.trace = trace;
        TcpPorts = new(AddressFamily.InterNetwork, ProtocolType.Tcp, 1024);
        try { UdpPorts = new(AddressFamily.InterNetwork, ProtocolType.Udp, 1024); }
        catch { TcpPorts.Dispose(); throw; }
        try { listener.Start(128); accept = AcceptAsync(); }
        catch { TcpPorts.Dispose(); UdpPorts.Dispose(); listener.Stop(); throw; }
    }
    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested) {
            await slots.WaitAsync(stop.Token);
            Socket socket;
            try { socket = await listener.AcceptSocketAsync(stop.Token); }
            catch { slots.Release(); throw; }
            long id = Interlocked.Increment(ref sequence);
            async Task Work() { try { await ServeAsync(socket); } finally { slots.Release(); } }
            var work = Work();
            clients[id] = work;
            _ = work.ContinueWith(_ => { clients.TryRemove(id, out var ignored); }, TaskScheduler.Default);
        }
    }
    private async Task ServeAsync(Socket client)
    {
        using (client)
        using (var control = new NetworkStream(client, false))
        using (var kill = stop.Token.Register(client.Dispose)) {
            try {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                handshake.CancelAfter(TimeSpan.FromSeconds(10));
                byte[] greeting = new byte[2]; await control.ReadExactlyAsync(greeting, handshake.Token);
                if (greeting[0] != 5 || greeting[1] == 0) return;
                byte[] methods = new byte[greeting[1]]; await control.ReadExactlyAsync(methods, handshake.Token);
                if (!methods.Contains((byte)0)) { await control.WriteAsync(new byte[] { 5, 255 }, handshake.Token); return; }
                await control.WriteAsync(new byte[] { 5, 0 }, handshake.Token);
                byte[] header = new byte[4]; await control.ReadExactlyAsync(header, handshake.Token);
                if (header[0] != 5 || header[2] != 0) return;
                var destination = await ReadTargetAsync(control, header[3], handshake.Token);
                if (header[1] == 1) {
                    var endpoint = await ResolveAsync(destination.Host, destination.Port, handshake.Token);
                    using var outgoing = TcpPorts.CreateBoundSocket(egress.Address);
                    egress.BindInterface(outgoing);
                    await outgoing.ConnectAsync(endpoint, handshake.Token);
                    await ReplyAsync(control, (IPEndPoint)outgoing.LocalEndPoint!, handshake.Token);
                    using var remote = new NetworkStream(outgoing, false);
                    using var killOutgoing = stop.Token.Register(outgoing.Dispose);
                    var send = PumpAsync(control, remote, outgoing, stop.Token);
                    var receive = PumpAsync(remote, control, client, stop.Token);
                    var first = await Task.WhenAny(send, receive);
                    if (first.IsFaulted || first.IsCanceled) { outgoing.Dispose(); client.Dispose(); }
                    await Task.WhenAll(send, receive);
                } else if (header[1] == 3) {
                    await AssociateUdpAsync(control, stop.Token);
                } else { await control.WriteAsync(new byte[] { 5, 7, 0, 1, 0, 0, 0, 0, 0, 0 }, handshake.Token); }
            } catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or ArgumentException) {
                trace?.Invoke("SOCKS: " + e.Message);
                // A failed/unsupported target closes only its association. Never route it direct.
            }
        }
    }
    private static async Task PumpAsync(Stream from, Stream to, Socket destination, CancellationToken token)
    {
        await from.CopyToAsync(to, token);
        try { destination.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
    }
    private static async Task<(string Host, int Port)> ReadTargetAsync(Stream stream, byte type, CancellationToken token)
    {
        string host;
        if (type == 1) { byte[] ip = new byte[4]; await stream.ReadExactlyAsync(ip, token); host = new IPAddress(ip).ToString(); }
        else if (type == 3) {
            byte[] length = new byte[1]; await stream.ReadExactlyAsync(length, token);
            if (length[0] == 0) throw new IOException("Empty SOCKS target.");
            byte[] bytes = new byte[length[0]]; await stream.ReadExactlyAsync(bytes, token);
            host = System.Text.Encoding.ASCII.GetString(bytes);
        } else throw new IOException("The Zapret trial supports IPv4 targets only.");
        byte[] port = new byte[2]; await stream.ReadExactlyAsync(port, token);
        return (host, BinaryPrimitives.ReadUInt16BigEndian(port));
    }
    private static async Task<IPEndPoint> ResolveAsync(string host, int port, CancellationToken token)
    {
        if (port is < 1 or > 65535) throw new IOException("Invalid destination port.");
        var ips = IPAddress.TryParse(host, out var ip) ? new[] { ip } : await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, token);
        var chosen = ips.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)
            && !ip.Equals(IPAddress.Any) && ip.GetAddressBytes()[0] < 224);
        return chosen == null ? throw new IOException("No usable IPv4 destination.") : new(chosen, port);
    }
    private static async Task ReplyAsync(Stream stream, IPEndPoint endpoint, CancellationToken token)
    {
        byte[] reply = new byte[10]; reply[0] = 5; reply[3] = 1;
        endpoint.Address.GetAddressBytes().CopyTo(reply, 4);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(8), (ushort)endpoint.Port);
        await stream.WriteAsync(reply, token);
    }
    private async Task AssociateUdpAsync(NetworkStream control, CancellationToken token)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var local = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        local.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var remote = UdpPorts.CreateBoundSocket(egress.Address);
        egress.BindInterface(remote);
        await ReplyAsync(control, (IPEndPoint)local.LocalEndPoint!, token);
        var allowed = new ConcurrentDictionary<IPEndPoint, byte>();
        int clientPort = 0;
        async Task Requests() {
            byte[] bytes = new byte[65535];
            while (!cancel.IsCancellationRequested) {
                var received = await local.ReceiveFromAsync(bytes, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancel.Token);
                var peer = (IPEndPoint)received.RemoteEndPoint;
                trace?.Invoke("UDP local received " + received.ReceivedBytes);
                if (!IPAddress.IsLoopback(peer.Address)) continue;
                Interlocked.CompareExchange(ref clientPort, peer.Port, 0);
                if (peer.Port != clientPort || received.ReceivedBytes < 10 || bytes[0] != 0 || bytes[1] != 0 || bytes[2] != 0) continue;
                try {
                    using var packet = new MemoryStream(bytes, 4, received.ReceivedBytes - 4, false);
                    var target = await ReadTargetAsync(packet, bytes[3], cancel.Token);
                    var endpoint = await ResolveAsync(target.Host, target.Port, cancel.Token);
                    if (allowed.Count >= 256 && !allowed.ContainsKey(endpoint)) continue;
                    allowed[endpoint] = 0;
                    int payloadStart = 4 + (int)packet.Position;
                    trace?.Invoke("UDP send " + endpoint + " bytes=" + (received.ReceivedBytes - payloadStart));
                    await remote.SendToAsync(bytes.AsMemory(payloadStart, received.ReceivedBytes - payloadStart), SocketFlags.None, endpoint, cancel.Token);
                } catch (Exception e) when (e is IOException or SocketException or ArgumentException) { trace?.Invoke("UDP request: " + e.Message); }
            }
        }
        async Task Responses() {
            byte[] bytes = new byte[65535];
            while (!cancel.IsCancellationRequested) {
                var received = await remote.ReceiveFromAsync(bytes.AsMemory(10), SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancel.Token);
                var source = (IPEndPoint)received.RemoteEndPoint;
                trace?.Invoke("UDP remote received " + received.ReceivedBytes + " from " + source);
                if (clientPort == 0 || !allowed.ContainsKey(source)) continue;
                bytes[0] = bytes[1] = bytes[2] = 0; bytes[3] = 1;
                source.Address.GetAddressBytes().CopyTo(bytes, 4);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), (ushort)source.Port);
                await local.SendToAsync(bytes.AsMemory(0, received.ReceivedBytes + 10), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, clientPort), cancel.Token);
            }
        }
        Task requests = Requests(), responses = Responses(), lifetime = control.ReadAsync(new byte[1], cancel.Token).AsTask();
        try { await await Task.WhenAny(requests, responses, lifetime); }
        finally {
            cancel.Cancel(); local.Dispose(); remote.Dispose();
            try { await Task.WhenAll(requests, responses, lifetime); }
            catch (Exception e) when (e is OperationCanceledException or SocketException or IOException or ObjectDisposedException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop();
        try { await accept; } catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
        try { await Task.WhenAll(clients.Values); }
        finally { TcpPorts.Dispose(); UdpPorts.Dispose(); slots.Dispose(); stop.Dispose(); }
    }
}
