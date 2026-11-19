using System.Net.Sockets;
using WGTun.Core.Experimental;
using WGTun.Core;

var tcp = new WindowsPortLease(AddressFamily.InterNetwork, ProtocolType.Tcp, 30000, 4);
var udp = new WindowsPortLease(AddressFamily.InterNetwork, ProtocolType.Udp, 40000, 4);
var filter = ZapretFilterBuilder.Build(8, [tcp, udp], [new(80, 80), new(443, 443)], [new(443, 443), new(50000, 50100)]);
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected rejection"); }
Check(filter.StartsWith("!impostor and !loopback and ifIdx == 8 and ("), "capture confinement");
Check(filter.Contains("outbound and tcp.SrcPort >= 30000 and tcp.SrcPort <= 30003"), "TCP outbound lease");
Check(filter.Contains("inbound and tcp.DstPort >= 30000 and tcp.DstPort <= 30003"), "TCP inbound lease");
Check(filter.Contains("outbound and udp.SrcPort >= 40000 and udp.SrcPort <= 40003"), "UDP outbound lease");
Check(filter.Contains("inbound and udp.DstPort >= 40000 and udp.DstPort <= 40003"), "UDP inbound lease");
Check(filter.Contains("udp.DstPort >= 50000 and udp.DstPort <= 50100"), "UDP destination boundaries");
Check(filter.Contains("udp.SrcPort >= 50000 and udp.SrcPort <= 50100"), "UDP source boundaries");
Check(!filter.Contains("tcp.DstPort >= 50000") && !filter.Contains("tcp.SrcPort >= 50000"), "TCP range isolation");
Check(!filter.Contains("udp.DstPort == 80") && !filter.Contains("udp.SrcPort == 80"), "UDP exclusion");
Reject(() => ZapretFilterBuilder.Build(0, [tcp], [new(80, 80)], []));
Reject(() => ZapretFilterBuilder.Build(8, [tcp], [new(0, 1)], []));
Reject(() => ZapretFilterBuilder.Build(8, [tcp], [new(80, 65536)], []));
Reject(() => ZapretFilterBuilder.Build(8, [tcp], [new(81, 80)], []));
Reject(() => ZapretFilterBuilder.Build(8, [tcp], [], [new(443, 443)]));
var discord = new DpiSettings { PresetId = "zapret-discord-media-stun" };
var production = ZapretStrategies.BuildFilter(8, [tcp, udp], discord);
var goodCheck = ZapretStrategies.BuildFilter(8, [tcp, udp], new DpiSettings { Engine = "zapret", PresetId = "zapret-discord-media-stun" });
Check(production == goodCheck, "production and GoodCheck policy parity");
Check(production.Contains("udp.DstPort >= 19294 and udp.DstPort <= 19344") && production.Contains("udp.SrcPort >= 50000 and udp.SrcPort <= 50099"), "Discord ranges");
Check(!production.Contains("tcp.DstPort >= 19294"), "Discord ranges are UDP only");
var standard = ZapretStrategies.BuildFilter(8, [tcp, udp], new DpiSettings());
Check(!standard.Contains("50000") && !standard.Contains("udp.DstPort == 80"), "standard policy unchanged");
Check(ZapretStrategies.Resolve(new DpiSettings { CustomArguments = "--dpi-desync=fake" }).SequenceEqual(["--dpi-desync=fake"]), "custom strategy resolution");
try { ZapretStrategies.Resolve(new DpiSettings { CustomArguments = "--wf-tcp=1-65535" }); throw new Exception("unsafe custom accepted"); } catch (InvalidOperationException) { }
Console.WriteLine("Offline filter construction checks passed; no driver or packet evaluation used.");

namespace WGTun.Core.Experimental
{
    // Test-only lease data; production accepts WindowsPortLease reservations only.
    public sealed record WindowsPortLease(AddressFamily Family, ProtocolType Protocol, int StartPort, int Count);
}

namespace WGTun.Core
{
    public sealed class DpiSettings { public string Engine { get; set; } = "zapret"; public string PresetId { get; set; } = "zapret-standard"; public string CustomArguments { get; set; } = ""; }
    public sealed record DpiPreset(string Id, string Engine, string Name, string[] Arguments, string Source, string Notes);
}
