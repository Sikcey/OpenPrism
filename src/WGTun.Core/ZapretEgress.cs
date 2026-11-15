using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WGTun.Core;

public sealed record ZapretEgress(string Name, uint Index, IPAddress Address)
{
    [DllImport("iphlpapi.dll")] private static extern uint GetBestInterfaceEx(byte[] address, out uint index);
    public static ZapretEgress Detect()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up
            && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .Where(n => !n.Description.Contains("wintun", StringComparison.OrdinalIgnoreCase)
                && !n.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase)
                && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .Select(n => new { Adapter = n, Source = n.GetIPProperties().UnicastAddresses.FirstOrDefault(a =>
                a.Address.AddressFamily == AddressFamily.InterNetwork && a.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred
                && !a.Address.ToString().StartsWith("169.254."))?.Address }).Where(n => n.Source != null).ToList();
        byte[] target = new byte[16]; target[0] = 2; IPAddress.Parse("1.1.1.1").GetAddressBytes().CopyTo(target, 4);
        uint status = GetBestInterfaceEx(target, out uint best);
        var chosen = candidates.FirstOrDefault(n => n.Adapter.GetIPProperties().GetIPv4Properties()?.Index == best);
        // An active WGTun may own the best route while a rebind is being prepared.
        // Multiple remaining physical gateways require a fresh route decision, not a guess.
        if (chosen == null && candidates.Count == 1) chosen = candidates[0];
        if (chosen == null) throw new IOException("Zapret: physical IPv4 egress is unavailable or ambiguous (route status " + status + ").");
        return new(chosen.Adapter.Name, (uint)chosen.Adapter.GetIPProperties().GetIPv4Properties()!.Index, chosen.Source!);
    }
    public void BindInterface(Socket socket) => socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder((int)Index));
}
