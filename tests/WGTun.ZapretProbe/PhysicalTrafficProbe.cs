using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using WGTun.Core.Experimental;

internal static class PhysicalTrafficProbe
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetBestInterfaceEx(byte[] destination, out uint index);

    public static async Task<object> RunAsync(string? captureDll = null, string? winwsDirectory = null)
    {
        byte[] target = new byte[16]; target[0] = 2;
        IPAddress.Parse("1.1.1.1").GetAddressBytes().CopyTo(target, 4);
        uint error = GetBestInterfaceEx(target, out uint index);
        if (error != 0) throw new IOException("GetBestInterfaceEx: " + error);
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
            n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().GetIPv4Properties()?.Index == index)
            ?? throw new IOException("No active route adapter.");
        if (adapter.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211))
            throw new IOException("Best route is not Ethernet/Wi-Fi; refusing to treat a tunnel as a physical interface.");
        var local = adapter.GetIPProperties().UnicastAddresses.Select(a => a.Address)
            .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
            ?? throw new IOException("No physical IPv4 source.");
        using var tcpLease = new WindowsPortLease(AddressFamily.InterNetwork, ProtocolType.Tcp, 32);
        using var udpLease = new WindowsPortLease(AddressFamily.InterNetwork, ProtocolType.Udp, 32);
        using var selectedTcp = tcpLease.CreateBoundSocket(local);
        using var normalTcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        normalTcp.Bind(new IPEndPoint(local, 0));
        using var selectedUdp = udpLease.CreateBoundSocket(local);
        using var normalUdp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        normalUdp.Bind(new IPEndPoint(local, 0));
        foreach (var socket in new[] { selectedTcp, normalTcp, selectedUdp, normalUdp })
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder((int)index));
        int selectedTcpPort = ((IPEndPoint)selectedTcp.LocalEndPoint!).Port, normalTcpPort = ((IPEndPoint)normalTcp.LocalEndPoint!).Port;
        int selectedUdpPort = ((IPEndPoint)selectedUdp.LocalEndPoint!).Port, normalUdpPort = ((IPEndPoint)normalUdp.LocalEndPoint!).Port;
        if (tcpLease.Contains(normalTcpPort) || udpLease.Contains(normalUdpPort)) throw new IOException("Unselected flow entered selected pool.");
        using var capture = captureDll == null ? null : new LiveCaptureProbe(captureDll, index, tcpLease, udpLease, selectedTcpPort, normalTcpPort, selectedUdpPort, normalUdpPort);
        if (winwsDirectory != null && capture == null) throw new IOException("Live winws test requires successful read-only capture of the existing driver first.");
        using var winws = winwsDirectory == null ? null : await WinwsProbeSession.StartAsync(winwsDirectory,
            ZapretFilterBuilder.Build(index, [tcpLease, udpLease], 443, 53), Path.Combine(winwsDirectory, "probe-winws.log"));
        var tcp = await Task.WhenAll(HttpsAsync(selectedTcp), HttpsAsync(normalTcp));
        var udp = await Task.WhenAll(DnsAsync(selectedUdp, 0x51a1), DnsAsync(normalUdp, 0x51a2));
        var captured = capture == null ? null : await capture.FinishAsync();
        return new {
            success = tcp.All(r => r.Success) && udp.All(r => r.Success) && (captured?.Success ?? true) && (winws?.IsRunning ?? true),
            packetCapture = captured,
            winwsRunningDuringTest = winws?.IsRunning ?? false,
            route = new { adapter.Name, index, source = local.ToString(), adapter.Description },
            selected = new { tcpPort = selectedTcpPort, udpPort = selectedUdpPort, tcp = tcp[0], udp = udp[0] },
            ordinary = new { tcpPort = normalTcpPort, udpPort = normalUdpPort, tcp = tcp[1], udp = udp[1] },
            tcpRange = new { tcpLease.StartPort, tcpLease.Count }, udpRange = new { udpLease.StartPort, udpLease.Count },
            limitation = winws != null ? "Opt-in TCP multisplit probe. This is not an integrated application mode or an ISP bypass effectiveness test."
                : capture == null ? "Socket-path proof only. No WinDivert capture or DPI modification was performed." : "Read-only capture. DPI modification was not performed."
        };
    }
    internal sealed record Result(bool Success, string Detail);
    private static async Task<Result> HttpsAsync(Socket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 443), timeout.Token);
            using var network = new NetworkStream(socket, ownsSocket: false);
            using var tls = new SslStream(network, false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "cloudflare-dns.com" }, timeout.Token);
            await tls.WriteAsync(Encoding.ASCII.GetBytes("HEAD / HTTP/1.1\r\nHost: cloudflare-dns.com\r\nConnection: close\r\n\r\n"), timeout.Token);
            byte[] bytes = new byte[1024]; int count = await tls.ReadAsync(bytes, timeout.Token);
            string status = Encoding.ASCII.GetString(bytes, 0, count).Split('\n')[0].Trim();
            return new(status.StartsWith("HTTP/1."), status);
        } catch (Exception e) { return new(false, e.Message); }
    }
    private static async Task<Result> DnsAsync(Socket socket, ushort id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try {
            byte[] request = new byte[12]; BinaryPrimitives.WriteUInt16BigEndian(request, id);
            request[2] = 1; request[5] = 1;
            var bytes = new List<byte>(request);
            foreach (string label in new[] { "cloudflare", "com" }) { bytes.Add((byte)label.Length); bytes.AddRange(Encoding.ASCII.GetBytes(label)); }
            bytes.AddRange([0, 0, 1, 0, 1]);
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53), timeout.Token);
            await socket.SendAsync(bytes.ToArray(), SocketFlags.None, timeout.Token);
            byte[] reply = new byte[4096]; int count = await socket.ReceiveAsync(reply, SocketFlags.None, timeout.Token);
            bool valid = count >= 12 && BinaryPrimitives.ReadUInt16BigEndian(reply) == id && (reply[2] & 128) != 0 && (reply[3] & 15) == 0;
            return new(valid, valid ? "DNS response with matching transaction ID" : "Unexpected DNS response");
        } catch (Exception e) { return new(false, e.Message); }
    }
}
