using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WGTun.Core;

// A session-owned loopback SOCKS bridge. CONNECT is acknowledged before inspecting
// client bytes, so a poisoned IP is never dialed before TLS SNI can be recovered.
public sealed class IngressSocksRelay : IAsyncDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim slots = new(128);
    readonly int upstreamPort;
    readonly Task accept;
    readonly System.Collections.Concurrent.ConcurrentDictionary<long, Task> connections = new();
    long nextConnection;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public bool IsRunning => !accept.IsCompleted && !stop.IsCancellationRequested;
    public IngressSocksRelay(int upstreamPort) { this.upstreamPort = upstreamPort; listener.Start(); accept = Accept(); }
    async Task Accept() {
        try { while (true) {
            var c = await listener.AcceptTcpClientAsync(stop.Token);
            if (!slots.Wait(0)) { c.Dispose(); continue; }
            long id = Interlocked.Increment(ref nextConnection);
            var task = Handle(c); connections[id] = task;
            _ = task.ContinueWith(completed => { connections.TryRemove(id, out _); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        } }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { } catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
    }
    static async Task<byte[]> Read(Stream s, int n, CancellationToken t) { var b = new byte[n]; await s.ReadExactlyAsync(b,t); return b; }
    static async Task<byte[]> Address(Stream s, byte type, CancellationToken t) {
        int n = type switch { 1 => 4, 4 => 16, 3 => (await Read(s,1,t))[0], _ => throw new IOException("SOCKS address") };
        var b = await Read(s,n+2,t); return type == 3 ? [(byte)n,..b] : b;
    }
    async Task Handle(TcpClient client) {
        try { using (client) using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token)) {
            deadline.CancelAfter(TimeSpan.FromSeconds(10)); var t = deadline.Token; var input = client.GetStream();
            var h = await Read(input,2,t); if (h[0] != 5 || h[1] == 0) return;
            var methods = await Read(input,h[1],t); if (!methods.Contains((byte)0)) { await input.WriteAsync(new byte[]{5,255},t); return; }
            await input.WriteAsync(new byte[]{5,0},t); h = await Read(input,4,t); if (h[0] != 5 || h[2] != 0 || (h[1] != 1 && h[1] != 3)) return;
            byte cmd = h[1], type = h[3]; byte[] address = await Address(input,type,t);
            using var upstream = new TcpClient(); await upstream.ConnectAsync(IPAddress.Loopback, upstreamPort,t); var output = upstream.GetStream();
            await output.WriteAsync(new byte[]{5,1,0},t); var greeting = await Read(output,2,t); if (greeting[0] != 5 || greeting[1] != 0) return;
            byte[] buffered = [];
            if (cmd == 1) {
                await input.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,0,0},t);
                if (type is 1 or 4) {
                    using var sniff = CancellationTokenSource.CreateLinkedTokenSource(t); sniff.CancelAfter(TimeSpan.FromMilliseconds(750));
                    using var data = new MemoryStream(); byte[] chunk = new byte[4096];
                    try { while (data.Length < 65536) { int n = await input.ReadAsync(chunk.AsMemory(0,Math.Min(chunk.Length,65536-(int)data.Length)),sniff.Token); if (n == 0) break; data.Write(chunk,0,n); var state = Inspect(data.GetBuffer().AsSpan(0, (int)data.Length),out string? host); if (host != null) { byte[] name = Encoding.ASCII.GetBytes(host); address = [(byte)name.Length,..name,address[^2],address[^1]]; type = 3; break; } if (state) break; } } catch (OperationCanceledException) when (!t.IsCancellationRequested) { }
                    buffered = data.ToArray();
                }
            }
            await output.WriteAsync(new byte[]{5,cmd,0,type},t); await output.WriteAsync(address,t);
            var reply = await Read(output,4,t); var bound = await Address(output,reply[3],t); if (cmd == 3) { await input.WriteAsync(reply,t); await input.WriteAsync(bound,t); } if (reply[1] != 0) return;
            if (buffered.Length > 0) await output.WriteAsync(buffered,t);
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var a = input.CopyToAsync(output,16 * 1024,linked.Token); var b = output.CopyToAsync(input,16 * 1024,linked.Token); await Task.WhenAny(a,b); linked.Cancel(); try { await Task.WhenAll(a,b); } catch (OperationCanceledException) { }
        } } catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or ArgumentException) { } finally { slots.Release(); }
    }
    // Returns true when no more bytes can improve the result.
    static bool Inspect(ReadOnlySpan<byte> data, out string? host) {
        host = null; if (data.Length == 0) return false;
        if (data[0] != 22) {
            int end = Encoding.ASCII.GetString(data).IndexOf("\r\n\r\n",StringComparison.Ordinal); if (end < 0) return data.Length >= 16384 || data[0] < 65 || data[0] > 90;
            var lines = Encoding.ASCII.GetString(data[..end]).Split("\r\n"); foreach (var line in lines.Skip(1)) if (line.StartsWith("Host:",StringComparison.OrdinalIgnoreCase)) { string value = line[5..].Trim(); if (value.Count(c => c == ':') == 1) value = value[..value.IndexOf(':')]; host = Valid(value); break; } return true;
        }
        using var payload = new MemoryStream(); int pos = 0;
        while (pos + 5 <= data.Length) { if (data[pos] != 22) return true; int length = data[pos+3]*256+data[pos+4]; if (length > 18432) return true; if (pos+5+length > data.Length) return false; payload.Write(data.Slice(pos+5,length)); pos += 5+length; var p = payload.GetBuffer().AsSpan(0, (int)payload.Length); if (p.Length < 4) continue; int total = 4+(p[1]<<16)+(p[2]<<8)+p[3]; if (total > 65536 || p[0] != 1) return true; if (p.Length < total) continue;
            try { int i=4+2+32; i += 1+p[i]; int n=(p[i]<<8)+p[i+1]; i+=2+n; i+=1+p[i]; int ext=(p[i]<<8)+p[i+1]; i+=2; int limit=i+ext; if (limit>total) return true; while (i+4<=limit) { int kind=(p[i]<<8)+p[i+1], size=(p[i+2]<<8)+p[i+3]; i+=4; if (i+size>limit) return true; if (kind==0 && size>=5) { int list=(p[i]<<8)+p[i+1]; if (list+2>size || p[i+2]!=0) return true; int len=(p[i+3]<<8)+p[i+4]; if (len+5>size || len+3>list) return true; host=Valid(Encoding.ASCII.GetString(p.Slice(i+5,len))); return true; } i+=size; } } catch (IndexOutOfRangeException) { } return true;
        } return false;
    }
    static string? Valid(string s) { if (s.Length is < 1 or > 253 || IPAddress.TryParse(s,out _) || s.Any(c => c > 127) || Uri.CheckHostName(s) != UriHostNameType.Dns) return null; return s; }
    public async ValueTask DisposeAsync() {
        stop.Cancel(); listener.Stop();
        try { await accept; } catch (OperationCanceledException) { }
        // Return only after accepted clients and upstream control sockets have closed.
        await Task.WhenAll(connections.Values);
    }
}
