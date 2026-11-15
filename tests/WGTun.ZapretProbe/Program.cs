using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using WGTun.Core.Experimental;

var report = new List<object>();
bool passed = true;
if (args.Contains("--relay")) {
    object relayResult;
    try { relayResult = await RelayProbe.RunAsync(args.FirstOrDefault(a => a.StartsWith("--winws-dir="))?[12..]); }
    catch (Exception e) { relayResult = new { success = false, error = e.ToString() }; }
    string relayJson = JsonSerializer.Serialize(relayResult, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(relayJson);
    string? relayOutput = args.FirstOrDefault(a => a.StartsWith("--output="))?[9..];
    if (relayOutput != null) await File.WriteAllTextAsync(Path.GetFullPath(relayOutput), relayJson);
    Environment.ExitCode = JsonDocument.Parse(relayJson).RootElement.GetProperty("success").GetBoolean() ? 0 : 1;
    return;
}
if (args.FirstOrDefault(a => a.StartsWith("--filter-dll=")) is string filterDll) {
    object filterResult;
    try { filterResult = FilterProbe.Run(filterDll[13..]); }
    catch (Exception e) { filterResult = new { success = false, error = e.Message }; }
    string filterJson = JsonSerializer.Serialize(filterResult, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(filterJson);
    string? filterOutput = args.FirstOrDefault(a => a.StartsWith("--output="))?[9..];
    if (filterOutput != null) await File.WriteAllTextAsync(Path.GetFullPath(filterOutput), filterJson);
    Environment.ExitCode = JsonDocument.Parse(filterJson).RootElement.GetProperty("success").GetBoolean() ? 0 : 1;
    return;
}
if (args.Contains("--physical")) {
    object result;
    try { result = await PhysicalTrafficProbe.RunAsync(args.FirstOrDefault(a => a.StartsWith("--capture-dll="))?[14..], args.FirstOrDefault(a => a.StartsWith("--winws-dir="))?[12..]); }
    catch (Exception e) { result = new { success = false, error = e.Message }; }
    string physicalJson = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(physicalJson);
    string? physicalOutput = args.FirstOrDefault(a => a.StartsWith("--output="))?[9..];
    if (physicalOutput != null) await File.WriteAllTextAsync(Path.GetFullPath(physicalOutput), physicalJson);
    Environment.ExitCode = JsonDocument.Parse(physicalJson).RootElement.GetProperty("success").GetBoolean() ? 0 : 1;
    return;
}
foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 }) {
    foreach (var protocol in new[] { ProtocolType.Tcp, ProtocolType.Udp }) {
        try {
            var address = family == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback;
            using var lease = new WindowsPortLease(family, protocol, 32);
            var selected = new List<Socket>();
            try {
                for (int i = 0; i < 12; i++) selected.Add(lease.CreateBoundSocket(address));
                int blocked = 0;
                // Even unoccupied reserved ports must reject unrelated sockets, not just
                // avoid collisions with the relay's currently active sockets.
                for (int p = lease.StartPort; p < lease.StartPort + lease.Count; p++) {
                    using var outsider = new Socket(family, protocol == ProtocolType.Tcp ? SocketType.Stream : SocketType.Dgram, protocol);
                    try { outsider.Bind(new IPEndPoint(address, p)); }
                    catch (SocketException e) when (e.SocketErrorCode is SocketError.AccessDenied or SocketError.AddressAlreadyInUse) { blocked++; }
                }
                bool outside = true;
                for (int i = 0; i < 200; i++) {
                    using var normal = new Socket(family, protocol == ProtocolType.Tcp ? SocketType.Stream : SocketType.Dgram, protocol);
                    normal.Bind(new IPEndPoint(address, 0));
                    outside &= !lease.Contains(((IPEndPoint)normal.LocalEndPoint!).Port);
                }
                if (blocked != lease.Count || !outside) throw new IOException("Ordinary sockets entered the reserved pool.");
                report.Add(new { family = family.ToString(), protocol = protocol.ToString(), lease.StartPort, lease.Count,
                    selected = selected.Select(s => ((IPEndPoint)s.LocalEndPoint!).Port).ToArray(), blocked, ordinaryOutsidePool = outside, success = true });
            } finally { foreach (var socket in selected) socket.Dispose(); }
        } catch (Exception e) {
            passed = false; report.Add(new { family = family.ToString(), protocol = protocol.ToString(), success = false, error = e.Message });
        }
    }
}
string json = JsonSerializer.Serialize(new { success = passed, tests = report }, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);
string? output = args.FirstOrDefault(a => a.StartsWith("--output="))?[9..];
if (output != null) await File.WriteAllTextAsync(Path.GetFullPath(output), json);
Environment.ExitCode = passed ? 0 : 1;
