using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace WGTun.Core;

// Session-owned SOCKS endpoint. It never sends a DNS packet to the requested address.
public sealed class DnsSocksRelay : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim slots = new(64);
    private readonly SemaphoreSlim queries = new(64);
    private readonly HttpClient http;
    private readonly Uri endpoint;
    private readonly TimeSpan timeout;
    private readonly Task accept, health;
    private int failed;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public bool IsRunning => !stop.IsCancellationRequested && !accept.IsCompleted && !health.IsCompleted && Volatile.Read(ref failed) == 0;
    public DnsSocksRelay(Uri? endpoint = null, HttpMessageHandler? handler = null, TimeSpan? timeout = null, TimeSpan? healthInterval = null)
    {
        this.endpoint = endpoint ?? new Uri("https://cloudflare-dns.com/dns-query");
        this.timeout = timeout ?? TimeSpan.FromSeconds(4);
        if (handler == null) {
            var sockets = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = this.timeout };
            sockets.ConnectCallback = async (_, token) => { var socket = new Socket(SocketType.Stream, ProtocolType.Tcp); try { await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 443), token); return new NetworkStream(socket, true); } catch { socket.Dispose(); throw; } };
            handler = sockets;
        }
        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        listener.Start();
        accept = AcceptAsync();
        health = HealthAsync(healthInterval ?? TimeSpan.FromSeconds(10));
    }
    // Probe independent of traffic so an idle, broken upstream cannot retain capture indefinitely.
    private static readonly byte[] Probe = [0x57,0x47,1,0,0,1,0,0,0,0,0,0,1,97,0,0,1,0,1];
    public async Task CheckReadyAsync(CancellationToken token = default) => _ = await QueryAsync(Probe, token);
    private async Task HealthAsync(TimeSpan interval)
    {
        int failures = 0;
        try { while (true) { await Task.Delay(interval, stop.Token); try { await QueryAsync(Probe, stop.Token); failures = 0; } catch when (!stop.IsCancellationRequested) { if (++failures >= 3) { Interlocked.Exchange(ref failed, 1); return; } } } }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    private async Task<byte[]> QueryAsync(byte[] query, CancellationToken token)
    {
        if (query.Length < 12 || query.Length > 65535 || (query[2] & 0x80) != 0) throw new IOException("Invalid DNS query");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token); deadline.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(query) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/dns-message" || response.Content.Headers.ContentLength > 65535) throw new IOException("Invalid DoH response");
        using var body = await response.Content.ReadAsStreamAsync(deadline.Token); using var result = new MemoryStream(); byte[] buffer = new byte[4096]; int n;
        while ((n = await body.ReadAsync(buffer, deadline.Token)) != 0) { if (result.Length + n > 65535) throw new IOException("DoH response too large"); result.Write(buffer, 0, n); }
        byte[] answer = result.ToArray();
        if (answer.Length < 12 || (answer[2] & 0x80) == 0 || answer[0] != query[0] || answer[1] != query[1]) throw new IOException("Malformed DoH response");
        return answer;
    }
    private async Task AcceptAsync()
    {
        try { while (true) { var client = await listener.AcceptTcpClientAsync(stop.Token); if (!slots.Wait(0)) { client.Dispose(); continue; } _ = HandleAsync(client); } }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
    }
    private async Task HandleAsync(TcpClient client)
    {
        try {
            using (client) using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token)) {
                handshake.CancelAfter(TimeSpan.FromSeconds(10)); var stream = client.GetStream(); byte[] head = new byte[2]; await stream.ReadExactlyAsync(head, handshake.Token);
                if (head[0] != 5 || head[1] == 0) return; byte[] methods = new byte[head[1]]; await stream.ReadExactlyAsync(methods, handshake.Token);
                if (!methods.Contains((byte)0)) { await stream.WriteAsync(new byte[] {5,255}, handshake.Token); return; }
                await stream.WriteAsync(new byte[] {5,0}, handshake.Token); byte[] command = new byte[4]; await stream.ReadExactlyAsync(command, handshake.Token);
                if (command[0] != 5 || command[2] != 0) return;
                await ReadAddressAsync(stream, command[3], handshake.Token);
                if (command[1] == 1) {
                    await stream.WriteAsync(new byte[] {5,0,0,1,127,0,0,1,0,0}, handshake.Token);
                    while (!stop.IsCancellationRequested) { byte[] len = new byte[2]; await stream.ReadExactlyAsync(len, stop.Token); int size = BinaryPrimitives.ReadUInt16BigEndian(len); if (size < 12) return; byte[] query = new byte[size]; await stream.ReadExactlyAsync(query, stop.Token); var answer = await QueryAsync(query, stop.Token); BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)answer.Length); await stream.WriteAsync(len, stop.Token); await stream.WriteAsync(answer, stop.Token); }
                } else if (command[1] == 3) {
                    using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); var ep = (IPEndPoint)udp.Client.LocalEndPoint!; byte[] reply = [5,0,0,1,127,0,0,1,(byte)(ep.Port >> 8),(byte)ep.Port]; await stream.WriteAsync(reply, handshake.Token);
                    using var association = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); var control = stream.ReadAsync(new byte[1], association.Token).AsTask();
                    try { while (!control.IsCompleted) { var receive = udp.ReceiveAsync(association.Token).AsTask(); if (await Task.WhenAny(receive, control) == control) break; var packet = await receive; if (!IPAddress.IsLoopback(packet.RemoteEndPoint.Address)) continue; if (queries.Wait(0)) _ = ReplyUdpAsync(udp, packet, association.Token); } } finally { association.Cancel(); }
                } else await stream.WriteAsync(new byte[] {5,7,0,1,0,0,0,0,0,0}, handshake.Token);
            }
        } catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or HttpRequestException) { } finally { slots.Release(); }
    }
    private async Task ReplyUdpAsync(UdpClient udp, UdpReceiveResult packet, CancellationToken token)
    {
        try { byte[] data = packet.Buffer; if (data.Length < 10 || data[0] != 0 || data[1] != 0 || data[2] != 0) return; int header = data[3] switch { 1 => 10, 4 => 22, 3 when data.Length > 4 => 7 + data[4], _ => 0 }; if (header == 0 || data.Length < header + 12) return; var answer = await QueryAsync(data[header..], token); byte[] result = new byte[header + answer.Length]; data.AsSpan(0, header).CopyTo(result); answer.CopyTo(result, header); await udp.SendAsync(result, packet.RemoteEndPoint, token); } catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or HttpRequestException) { } finally { queries.Release(); }
    }
    private static async Task ReadAddressAsync(Stream stream, byte type, CancellationToken token)
    {
        int length = type switch { 1 => 4, 4 => 16, 3 => (await ReadByteAsync(stream, token)), _ => throw new IOException("Invalid SOCKS address") }; byte[] address = new byte[length + 2]; await stream.ReadExactlyAsync(address, token);
    }
    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken token) { byte[] b = new byte[1]; await stream.ReadExactlyAsync(b, token); return b[0]; }
    public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); http.Dispose(); try { await Task.WhenAll(accept, health); } catch (OperationCanceledException) { } }
}
