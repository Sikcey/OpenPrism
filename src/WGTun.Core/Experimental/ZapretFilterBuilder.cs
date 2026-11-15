using System.Net.Sockets;

namespace WGTun.Core.Experimental;

public static class ZapretFilterBuilder
{
    public static string Build(uint interfaceIndex, IEnumerable<WindowsPortLease> leases, params int[] destinationPorts)
    {
        if (interfaceIndex == 0 || destinationPorts.Length is < 1 or > 32 || destinationPorts.Any(p => p is < 1 or > 65535))
            throw new ArgumentException("Invalid interface or destination port set.");
        var clauses = new List<string>();
        foreach (var lease in leases) {
            string family = lease.Family == AddressFamily.InterNetwork ? "ip" : "ipv6";
            string protocol = lease.Protocol == ProtocolType.Tcp ? "tcp" : "udp";
            int start = lease.StartPort, end = start + lease.Count - 1;
            string destinations = string.Join(" or ", destinationPorts.Distinct().Select(p => $"{protocol}.DstPort == {p}"));
            string sources = string.Join(" or ", destinationPorts.Distinct().Select(p => $"{protocol}.SrcPort == {p}"));
            clauses.Add($"({family} and {protocol} and ((outbound and {protocol}.SrcPort >= {start} and {protocol}.SrcPort <= {end} and ({destinations})) or (inbound and {protocol}.DstPort >= {start} and {protocol}.DstPort <= {end} and ({sources}))))");
        }
        if (clauses.Count == 0) throw new ArgumentException("A live socket reservation is required.");
        return $"!impostor and !loopback and ifIdx == {interfaceIndex} and ({string.Join(" or ", clauses)})";
    }
}
