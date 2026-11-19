using System.Net.Sockets;

namespace WGTun.Core.Experimental;

public readonly record struct ZapretPortRange(int First, int Last);

public static class ZapretFilterBuilder
{
    // Compatibility overload for existing callers: identical remote ports for both protocols.
    public static string Build(uint interfaceIndex, IEnumerable<WindowsPortLease> leases, params int[] destinationPorts) =>
        Build(interfaceIndex, leases, destinationPorts.Select(p => new ZapretPortRange(p, p)).ToArray(),
            destinationPorts.Select(p => new ZapretPortRange(p, p)).ToArray());

    public static string Build(uint interfaceIndex, IEnumerable<WindowsPortLease> leases,
        IReadOnlyList<ZapretPortRange> tcpPorts, IReadOnlyList<ZapretPortRange> udpPorts)
    {
        if (interfaceIndex == 0) throw new ArgumentException("Invalid interface index.");
        Validate(tcpPorts); Validate(udpPorts);
        var clauses = new List<string>();
        foreach (var lease in leases) {
            string family = lease.Family switch { AddressFamily.InterNetwork => "ip", AddressFamily.InterNetworkV6 => "ipv6", _ => throw new ArgumentException("Invalid lease family.") };
            string protocol = lease.Protocol switch { ProtocolType.Tcp => "tcp", ProtocolType.Udp => "udp", _ => throw new ArgumentException("Invalid lease protocol.") };
            var ports = lease.Protocol == ProtocolType.Tcp ? tcpPorts : udpPorts;
            if (ports.Count == 0) continue;
            int start = lease.StartPort, end = start + lease.Count - 1;
            if (start < 1 || lease.Count < 1 || end > 65535) throw new ArgumentException("Invalid lease.");
            string destinations = Match(protocol, "DstPort", ports);
            string sources = Match(protocol, "SrcPort", ports);
            clauses.Add($"({family} and {protocol} and ((outbound and {protocol}.SrcPort >= {start} and {protocol}.SrcPort <= {end} and ({destinations})) or (inbound and {protocol}.DstPort >= {start} and {protocol}.DstPort <= {end} and ({sources}))))");
        }
        if (clauses.Count == 0) throw new ArgumentException("A live socket reservation is required.");
        return $"!impostor and !loopback and ifIdx == {interfaceIndex} and ({string.Join(" or ", clauses)})";
    }

    private static void Validate(IReadOnlyList<ZapretPortRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        if (ranges.Count > 32 || ranges.Any(r => r.First < 1 || r.Last > 65535 || r.First > r.Last))
            throw new ArgumentException("Invalid destination port ranges.");
    }

    private static string Match(string protocol, string field, IReadOnlyList<ZapretPortRange> ranges) =>
        string.Join(" or ", ranges.Distinct().Select(r => r.First == r.Last
            ? $"{protocol}.{field} == {r.First}"
            : $"({protocol}.{field} >= {r.First} and {protocol}.{field} <= {r.Last})"));
}
