using System.Reflection;
using System.Text.Json.Nodes;
using WGTun.Core;
void Assert(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
void Reject(Action action, string name) { try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { Assert(true, name); return; } throw new Exception("Accepted: " + name); }
string key = Convert.ToBase64String(new byte[32]);
string config = $"[Interface]\nPrivateKey = {key}\nAddress = 10.0.0.2/32\nDNS = 1.1.1.1\n[Peer]\nPublicKey = {key}\nEndpoint = 192.0.2.1:51820\nAllowedIPs = 0.0.0.0/0, ::/0\n";
var validate = typeof(AppBackend).GetMethod("ValidateConfig", BindingFlags.NonPublic | BindingFlags.Static)!;
Assert(validate.Invoke(null, [config]) == null, "valid WireGuard configuration");
Assert(validate.Invoke(null, [config + "PostUp = calc.exe\n"]) != null, "configuration hooks rejected");
Assert(RulePolicy.Validate(new() { Type = RuleType.IPv4Range, Value = "192.0.2.0/33" }) != null, "invalid CIDR rejected");
var policy = new UserSettings { Profile = ProfileKind.CustomWireGuard, Scope = TrafficScope.All, RuleSchemaVersion = 1 };
JsonObject Build() => JsonNode.Parse(AppBackend.BuildSingBoxConfiguration(config, policy))!.AsObject();
var wg = Build();
var generated = Path.GetFullPath("artifacts/config-checks");
Directory.CreateDirectory(generated);
File.WriteAllText(Path.Combine(generated, "wireguard.json"), wg.ToJsonString());
Assert(wg["endpoints"]!.AsArray().Any(x => x?["type"]?.GetValue<string>() == "wireguard"), "WireGuard uses endpoint schema");
Assert(!wg["outbounds"]!.AsArray().Any(x => x?["type"]?.GetValue<string>() == "socks"), "WireGuard has no ByeDPI proxy");
policy.Scope = TrafficScope.SelectedOnly;
policy.Rules = [new() { Type = RuleType.ProcessName, Value = "Discord.exe" }, new() { Type = RuleType.IPv4Range, Value = "192.0.2.0/24" }];
var selected = Build();
Assert(selected.ToJsonString().Contains("Discord.exe") && selected.ToJsonString().Contains("192.0.2.0/24"), "process and IP selection coexist");
string selectedFinal = selected["route"]!["final"]!.GetValue<string>();
policy.Scope = TrafficScope.ExceptSelected;
Assert(Build()["route"]!["final"]!.GetValue<string>() != selectedFinal, "exclude mode reverses default route");
policy.Profile = ProfileKind.Dpi;
var dpi = Build();
Assert(dpi["dns"]!["servers"]![0]!["type"]!.GetValue<string>() == "https", "default DPI DNS uses DoH");
Assert(dpi["dns"]!["servers"]!.AsArray().All(x => x?["detour"]?.GetValue<string>() != "direct"), "DNS avoids invalid empty direct detours");
policy.Dpi.Dns = "192.0.2.53";
var customDns = Build()["dns"]!["servers"]![0]!;
Assert(customDns["type"]!.GetValue<string>() == "https" && customDns["server"]!.GetValue<string>() == "192.0.2.53" && customDns["tls"]!["server_name"]!.GetValue<string>() == "192.0.2.53" && customDns["detour"]!.GetValue<string>() == "active", "custom DPI DNS retains authenticated DoH through engine");
policy.Dpi.Dns = "invalid.example";
Reject(() => Build(), "custom DPI DNS requires an IP address");
policy.Dpi.Dns = "";
File.WriteAllText(Path.Combine(generated, "byedpi.json"), dpi.ToJsonString());
Assert(dpi["endpoints"] is not JsonArray endpoints || endpoints.Count == 0, "ByeDPI has no WireGuard endpoint");
Assert(dpi["outbounds"]!.AsArray().Any(x => x?["type"]?.GetValue<string>() == "socks"), "ByeDPI uses SOCKS outbound");
policy.Scope = TrafficScope.SelectedOnly; policy.Rules.Clear();
Reject(() => Build(), "empty selection rejected");
policy.Rules = [new() { Type = RuleType.IPv4Range, Value = "192.0.2.0/99" }];
Reject(() => Build(), "invalid rules rejected before execution");
var migration = new UserSettings { Applications = [new() { Path = Environment.ProcessPath!, Selected = true }] };
RulePolicy.Migrate(migration); RulePolicy.Migrate(migration);
Assert(migration.Rules.Count == 1, "legacy app selection migrates once");
Console.WriteLine("No service installation, account registration or tunnel activation performed.");
string logFixture = Path.Combine(Path.GetTempPath(), "WGTun-log-check-" + Guid.NewGuid().ToString("N") + ".log");
try {
    File.WriteAllText(logFixture, new string('x', 100000) + "\nlast diagnostic event\n");
    var tail = AppBackend.ReadLogTail(logFixture);
    Assert(tail.Contains("last diagnostic event") && tail.Sum(x => x.Length) <= 3800, "large logs are read with a bounded tail");
} finally { File.Delete(logFixture); }
string discordRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord");
string discordOne = Path.Combine(discordRoot, "app-1.0.1", "Discord.exe");
string discordTwo = Path.Combine(discordRoot, "app-1.0.2", "Discord.exe");
var folderMigration = new UserSettings {
    RuleSchemaVersion = 1,
    Applications = [new() { Name = "Discord", Path = discordOne }, new() { Name = "Discord", Path = discordTwo }],
    Rules = [new() { Name = "Discord", Type = RuleType.ProcessPath, Value = discordOne },
        new() { Name = "Discord", Type = RuleType.ProcessPath, Value = discordTwo, Enabled = true },
        new() { Type = RuleType.ProcessName, Value = "curl.exe" }]
};
RulePolicy.Migrate(folderMigration);
Assert(folderMigration.Rules.Count == 1 && folderMigration.Rules[0].Type == RuleType.FolderPath
    && folderMigration.Rules[0].Value == discordRoot && folderMigration.Rules[0].Enabled, "Discord versions merge into enabled root folder and test curl is removed");
Assert(ApplicationDiscovery.CanonicalRoot(Path.Combine(discordRoot, "Update.exe")) == discordRoot, "Discord updater shares versioned executable root");
var language = new UserSettings { Language = "en" };
Assert(language.Language == "en", "English preference retained");
language.Language = "unsupported";
Assert(language.Language == "tr", "unsupported language falls back to Turkish");
foreach (var code in new[] { "ru", "zh" }) { language.Language = code; Assert(language.Language == code, code + " preference retained"); }
Assert(ByeDpiStrategies.Resolve(new DpiSettings()).SequenceEqual(new[] { "--tlsrec", "1" }), "setup keeps the standard ByeDPI strategy");
foreach (var preset in ByeDpiStrategies.All) {
    var resolved = ByeDpiStrategies.Resolve(new DpiSettings { PresetId = preset.Id });
    Assert(resolved.SequenceEqual(preset.Arguments) && !resolved.Contains("--ip") && !resolved.Contains("--port"), "validated strategy " + preset.Id);
    var strategyConfig = AppBackend.BuildSingBoxConfiguration("", new UserSettings {
        Profile = ProfileKind.Dpi, Scope = TrafficScope.All, Dpi = new() { PresetId = preset.Id }
    });
    Assert(strategyConfig.Contains("127.0.0.1") && !strategyConfig.Contains("0.0.0.0\""), "strategy uses the local SOCKS outbound " + preset.Id);
}
try { ByeDpiStrategies.Resolve(new DpiSettings { PresetId = "arbitrary-command" }); throw new Exception("Unknown strategy accepted"); }
catch (InvalidOperationException) { Console.WriteLine("PASS unknown strategy rejected"); }
try { ByeDpiStrategies.Resolve(new DpiSettings { CustomArguments = "--ip 0.0.0.0" }); throw new Exception("Custom listener accepted"); }
catch (InvalidOperationException) { Console.WriteLine("PASS custom ByeDPI arguments rejected"); }
Assert(AppBackend.ValidateGoodCheckHosts(["Discord.com", "discord.com", "youtube.com"]).SequenceEqual(new[] { "discord.com", "youtube.com" }), "GoodCheck hosts normalized and deduplicated");
foreach (var host in new[] { "https://discord.com/path", "127.0.0.1", "host.local", "discord.com;cmd" }) {
    try { AppBackend.ValidateGoodCheckHosts([host]); throw new Exception("Invalid host accepted"); }
    catch (ArgumentException) { Console.WriteLine("PASS invalid GoodCheck host rejected"); }
}
var scanLog = new System.Text.StringBuilder();
for (int i = 0; i < ByeDpiStrategies.All.Count; i++) {
    scanLog.AppendLine($"Launching 'ByeDPI', strategy {i + 1}/{ByeDpiStrategies.All.Count}: [ciadpi.exe --ip 127.0.0.1]");
    scanLog.AppendLine("Successes: 2/2"); scanLog.AppendLine($"Successes: {(i == 2 ? 2 : 1)}/2");
}
scanLog.AppendLine("All Done");
var scores = AppBackend.ParseGoodCheckResults(scanLog.ToString(), 2);
Assert(scores[0].PresetId == ByeDpiStrategies.All[2].Id && scores[0].Successes == 2 && scores[^1].Successes == 1, "GoodCheck ranks by worst pass and resolves fixed strategy IDs");
try { AppBackend.ParseGoodCheckResults(scanLog.ToString().Replace("All Done", ""), 2); throw new Exception("Incomplete results accepted"); }
catch (IOException) { Console.WriteLine("PASS incomplete GoodCheck results rejected"); }
var zapret = new UserSettings { Profile = ProfileKind.Zapret, Scope = TrafficScope.SelectedOnly, RuleSchemaVersion = 2,
    Rules = [new() { Type = RuleType.ProcessName, Value = "curl.exe" }] };
var testEgress = new ZapretEgress("Ethernet", 8, System.Net.IPAddress.Parse("192.0.2.20"));
var zapretConfig = JsonNode.Parse(AppBackend.BuildSingBoxConfiguration("", zapret, 12080, testEgress))!;
Assert(zapret.Method == ConnectionMethod.Zapret, "Zapret method roundtrip");
Assert(zapretConfig["outbounds"]!.AsArray().Any(o => o?["tag"]?.GetValue<string>() == "active" && o?["type"]?.GetValue<string>() == "socks"), "Zapret selected traffic uses isolated SOCKS relay");
Assert(zapretConfig["outbounds"]!.AsArray().Any(o => o?["tag"]?.GetValue<string>() == "direct" && o?["bind_interface"]?.GetValue<string>() == "Ethernet"), "Zapret direct output binds the physical adapter name");
Assert(zapretConfig["route"]!["final"]!.GetValue<string>() == "direct" && zapretConfig["route"]!["rules"]!.AsArray().Any(r => r?["process_name"]?.ToJsonString().Contains("curl.exe") == true && r?["outbound"]?.GetValue<string>() == "active"), "Zapret selected-only policy isolates matching processes");
Assert(zapretConfig["dns"]!["servers"]![0]!["detour"]!.GetValue<string>() == "active", "Zapret DoH uses the selected engine");
File.WriteAllText(Path.Combine(generated, "zapret.json"), zapretConfig.ToJsonString());
zapret.Scope = TrafficScope.ExceptSelected;
var exceptZapret = JsonNode.Parse(AppBackend.BuildSingBoxConfiguration("", zapret, 12080, testEgress))!;
Assert(exceptZapret["route"]!["final"]!.GetValue<string>() == "active" && exceptZapret["route"]!["rules"]!.AsArray().Any(r => r?["process_name"]?.ToJsonString().Contains("curl.exe") == true && r?["outbound"]?.GetValue<string>() == "direct"), "Zapret except-selected policy keeps exclusions direct");
var serializedZapret = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(System.Text.Json.JsonSerializer.Serialize(zapret))!;
Assert(serializedZapret.Profile == ProfileKind.Zapret && serializedZapret.Method == ConnectionMethod.Zapret, "Zapret JSON does not fall back to WireGuard");

// Engine contracts and lifecycle tests never install services or alter routes.
foreach (var preset in ZapretStrategies.All) {
    var zapretArgs = ZapretStrategies.Resolve(new DpiSettings { Engine = "zapret", PresetId = preset.Id });
    Assert(zapretArgs.SequenceEqual(preset.Arguments) && zapretArgs.All(a => !a.StartsWith("--wf-") && !a.Contains("--debug") && !a.Contains("hostlist")), "Zapret preset cannot override capture or write files " + preset.Id);
}
var separate = new UserSettings { Profile = ProfileKind.Zapret, Dpi = new() { PresetId = "goodcheck-oob-sni" }, ZapretDpi = new() { Engine = "zapret", PresetId = "zapret-tr-sol" } };
Assert(separate.ActiveDpi.PresetId == "zapret-tr-sol", "Zapret uses its own strategy");
separate.Profile = ProfileKind.Dpi;
Assert(separate.ActiveDpi.PresetId == "goodcheck-oob-sni", "ByeDPI survives Zapret edits");
Assert(AppBackend.ValidateGoodCheckEngine("zapret") == "zapret", "Zapret scan selector accepted");
try { AppBackend.ValidateGoodCheckEngine("unknown"); throw new Exception("unknown scan engine accepted"); } catch (ArgumentException) { Console.WriteLine("PASS unknown scan engine rejected"); }
var backoff = new RecoveryBackoff(); var now = DateTimeOffset.UtcNow;
Assert(backoff.Ready(now), "initial recovery is immediate");
backoff.Failed(now); Assert(!backoff.Ready(now) && backoff.Ready(now.AddSeconds(1)), "failure delays retry");
for (int i = 0; i < 20; i++) backoff.Failed(now);
Assert(!backoff.Ready(now.AddSeconds(29)) && backoff.Ready(now.AddSeconds(30)), "recovery backoff is capped");
backoff.Reset(); Assert(backoff.Ready(now), "disconnect or successful recovery resets backoff");
var scratch = Path.Combine(Path.GetTempPath(), "OpenPrism-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(scratch, "byedpi"));
try {
    File.WriteAllText(Path.Combine(scratch, "byedpi", "ciadpi.exe"), "foreign file");
    Assert(!SingBoxDependencies.IsPinnedFile(scratch, "byedpi/ciadpi.exe"), "foreign component filename is not owned");
    Assert(!SingBoxDependencies.IsPinnedFile(scratch, "../foreign.exe"), "unknown cleanup path is not owned");
    LifecycleDiagnostics.Record(scratch, "unhandled-error", new IOException("secret-key=must-not-appear"));
    Assert(!File.ReadAllText(Path.Combine(scratch, "lifecycle.log")).Contains("must-not-appear"), "crash diagnostics exclude exception messages");
} finally { Directory.Delete(scratch, true); }
if (OperatingSystem.IsWindows()) {
    var job = new SingBoxJob();
    using var process = job.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), ["/c", "ping -n 30 127.0.0.1 >nul"], Path.GetTempPath());
    Assert(!process.HasExited, "isolated engine child starts");
    job.Dispose();
    Assert(process.WaitForExit(5000), "engine job teardown kills owned process without elevation");
}

var stremioSplit = new UserSettings { Profile = ProfileKind.Dpi, Scope = TrafficScope.SelectedOnly, RuleSchemaVersion = 2,
    Rules = [new() { Type = RuleType.ProcessName, Value = "stremio-runtime.exe", Action = RuleAction.ActiveConnection }, new() { Type = RuleType.ProcessName, Value = "stremio-runtime.exe", Action = RuleAction.Direct }] };
var stremioJson = JsonNode.Parse(AppBackend.BuildSingBoxConfiguration("", stremioSplit))!;
Assert(stremioJson["inbounds"]!.AsArray().Count == 0, "selective ingress has no OS adapter");
var stremioRules = stremioJson["route"]!["rules"]!.AsArray();
int stremioDirect = stremioRules.ToList().FindIndex(r => r?["process_name"]?.ToJsonString().Contains("stremio-runtime.exe") == true && r?["outbound"]?.GetValue<string>() == "direct");
int stremioActive = stremioRules.ToList().FindIndex(r => r?["process_name"]?.ToJsonString().Contains("stremio-runtime.exe") == true && r?["outbound"]?.GetValue<string>() == "active");
int dnsHijack = stremioRules.ToList().FindIndex(r => r?["action"]?.GetValue<string>() == "hijack-dns");
Assert(stremioDirect >= 0 && stremioActive > stremioDirect && dnsHijack < 0, "direct exemptions precede active; no system DNS hijack");
stremioSplit.Scope = TrafficScope.All;
var allWithExclusion = JsonNode.Parse(AppBackend.BuildSingBoxConfiguration("", stremioSplit))!;
Assert(allWithExclusion["route"]!["rules"]!.AsArray().Any(r => r?["process_name"]?.ToJsonString().Contains("stremio-runtime.exe") == true && r?["outbound"]?.GetValue<string>() == "direct"), "all-traffic mode respects explicit exclusions");
Assert(allWithExclusion["inbounds"]!.AsArray().Count == 0, "all-traffic still has no global TUN routes");

foreach (var connectionProfile in Enum.GetValues<ProfileKind>()) {
    foreach (var splitScope in new[] { TrafficScope.SelectedOnly, TrafficScope.ExceptSelected }) {
        var regression = new UserSettings { Profile = connectionProfile, Scope = splitScope, RuleSchemaVersion = 2,
            Rules = [new() { Type = RuleType.FolderPath, Value = Path.GetTempPath(), Action = RuleAction.Direct }] };
        var built = JsonNode.Parse(AppBackend.BuildSingBoxConfiguration(config, regression, 12080, testEgress))!;
    Assert(built["inbounds"]!.AsArray().Count == 0, "split routing preserves native Windows DNS and routes");
        Assert(!built["route"]!["rules"]!.AsArray().Any(r => r?["action"]?.GetValue<string>() == "hijack-dns"), "split routing does not hijack excluded application DNS / " + connectionProfile + " / " + splitScope);
        File.WriteAllText(Path.Combine(generated, $"split-{connectionProfile}-{splitScope}.json"), built.ToJsonString());
    }
}

try { SignedPackage.Verify(Path.GetTempPath(), ""); throw new Exception("unsigned updates accepted"); }
catch (InvalidOperationException) { Console.WriteLine("PASS unsigned update blocked without publisher trust"); }

var watchdog = new RecoveryWatchdog();
Assert(!watchdog.Observe(1000) && !watchdog.Observe(3000), "normal recovery cadence does not restart capture");
Assert(watchdog.Observe(45000), "wake-sized scheduling gap requests a fresh capture binding");
watchdog.Reset(50000); Assert(!watchdog.Observe(51000), "manual connection resets wake detection");

string eventFixture = Path.Combine(Path.GetTempPath(), "OpenPrism-events-" + Guid.NewGuid().ToString("N") + ".log");
try {
    File.WriteAllText(eventFixture, "2026-11-15T00:00:00Z unhandled-error IOException\n2026-11-15T00:00:00Z secret-key private-data\n2026-11-15T00:00:00Z unhandled-error PrivateKeyValue123\n2026-11-15T00:00:00Z unhandled-error C:\\private\n");
    var events = AppBackend.ReadDiagnosticEvents(eventFixture);
    Assert(events.Length == 1 && events[0].ErrorType == "IOException", "diagnostic export admits only structured events and exception type names");
} finally { File.Delete(eventFixture); }
