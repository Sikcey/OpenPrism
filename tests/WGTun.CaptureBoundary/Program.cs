using WGTun.Core;
using System.Text.Json.Nodes;
static void Require(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
static void Reject(Action action, string name) { try { action(); } catch (NotSupportedException) { Console.WriteLine("PASS " + name); return; } throw new Exception(name); }
foreach (var profile in Enum.GetValues<ProfileKind>()) foreach (var scope in Enum.GetValues<TrafficScope>()) {
    var settings = new UserSettings { Profile = profile, Scope = scope, RuleSchemaVersion = 2,
        Rules = [new() { Type = RuleType.FolderPath, Value = Path.GetTempPath(), Action = scope == TrafficScope.All ? RuleAction.Direct : RuleAction.ActiveConnection }] };
    var config = JsonNode.Parse(ProxiFyreConfiguration.Build(settings, 12080))!;
    Require(config["dns"]!["mode"]!.GetValue<string>() == "Direct", $"native DNS / {profile} / {scope}");
    Require(config["inbounds"] == null && config["route"] == null, $"no TUN / {profile} / {scope}");
    string folder = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
    if (scope == TrafficScope.SelectedOnly) Require(config["proxies"]![0]!["appNames"]![0]!.GetValue<string>() == folder, "include folder boundary");
    else Require(config["excludes"]!.AsArray().Any(x => x!.GetValue<string>() == folder), "exclude folder boundary");
}
var unsupported = new UserSettings { Profile = ProfileKind.Dpi, Scope = TrafficScope.SelectedOnly,
    Rules = [new() { Type = RuleType.IPv4Address, Value = "192.0.2.1" }] };
Reject(() => CaptureBoundary.EnsureSupported(unsupported), "unsupported IP rule rejected before capture");
unsupported.Rules = [new() { Type = RuleType.ProcessName, Value = "stremio-runtime.exe", Action = RuleAction.Direct }];
Reject(() => CaptureBoundary.EnsureSupported(unsupported), "substring process exclusion rejected");
unsupported.Rules = [new() { Type = RuleType.FolderPath, Value = Path.GetTempPath(), Action = RuleAction.Direct, Protocol = "TCP" }];
Reject(() => CaptureBoundary.EnsureSupported(unsupported), "protocol-specific native exclusion rejected");
string key = Convert.ToBase64String(new byte[32]);
string wg = $"[Interface]\nPrivateKey = {key}\nAddress = 10.0.0.2/32\n[Peer]\nPublicKey = {key}\nEndpoint = 192.0.2.1:51820\nAllowedIPs = 0.0.0.0/0, ::/0\n";
var transport = JsonNode.Parse(AppBackend.BuildProcessProxyConfiguration(wg, new UserSettings { Profile = ProfileKind.CustomWireGuard, Scope = TrafficScope.All }, 12080))!;
Require(transport["inbounds"]![0]!["type"]!.GetValue<string>() == "socks", "WireGuard uses local SOCKS inbound");
Require(!transport.ToJsonString().Contains("auto_route") && !transport.ToJsonString().Contains("strict_route"), "WireGuard never changes native routing");
Directory.CreateDirectory("artifacts/config-checks");
File.WriteAllText("artifacts/config-checks/process-wireguard.json", transport.ToJsonString());
