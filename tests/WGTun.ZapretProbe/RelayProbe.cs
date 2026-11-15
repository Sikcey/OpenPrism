using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using WGTun.Core;
using WGTun.Core.Experimental;

internal static class RelayProbe
{
    public static async Task<object> RunAsync(string? winwsDirectory)
    {
        var egress = ZapretEgress.Detect();
        await using var relay = new ZapretRelay(egress, message => Console.Error.WriteLine(message));
        using var winws = winwsDirectory == null ? null : await WinwsProbeSession.StartAsync(winwsDirectory,
            ZapretFilterBuilder.Build(egress.Index, [relay.TcpPorts, relay.UdpPorts], 80, 443), Path.Combine(winwsDirectory, "relay-winws.log"));
        using var handler = new SocketsHttpHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{relay.Port}"), UseProxy = true, AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await http.GetAsync("https://1.1.1.1/", HttpCompletionOption.ResponseHeadersRead);
        using var control = new TcpClient();
        await control.ConnectAsync(IPAddress.Loopback, relay.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var stream = control.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
        byte[] greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, timeout.Token);
        if (!greeting.SequenceEqual(new byte[] { 5, 0 })) throw new IOException("Relay greeting failed.");
        await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, timeout.Token);
        byte[] reply = new byte[10]; await stream.ReadExactlyAsync(reply, timeout.Token);
        if (reply[1] != 0) throw new IOException("UDP association failed.");
        var local = new IPEndPoint(new IPAddress(reply.AsSpan(4, 4)), BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(8)));
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] query = [0, 0, 0, 1, 1, 1, 1, 1, 0, 53,
            0x65, 0x43, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0,
            10, (byte)'c', (byte)'l', (byte)'o', (byte)'u', (byte)'d', (byte)'f', (byte)'l', (byte)'a', (byte)'r', (byte)'e',
            3, (byte)'c', (byte)'o', (byte)'m', 0, 0, 1, 0, 1];
        await udp.SendAsync(query, local, timeout.Token);
        var answer = await udp.ReceiveAsync(timeout.Token);
        bool udpPassed = answer.Buffer.Length >= 22 && answer.Buffer[10] == 0x65 && answer.Buffer[11] == 0x43 && (answer.Buffer[12] & 128) != 0;
        return new { success = udpPassed && relay.IsRunning && (winws?.IsRunning ?? true), relayPort = relay.Port,
            httpStatus = (int)response.StatusCode, socksUdpDns = udpPassed, winws = winws?.IsRunning ?? false,
            tcpPool = new { relay.TcpPorts.StartPort, relay.TcpPorts.Count }, udpPool = new { relay.UdpPorts.StartPort, relay.UdpPorts.Count } };
    }
}
