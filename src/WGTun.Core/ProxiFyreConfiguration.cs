using System.Text.Json;
using System.Text.Json.Nodes;
namespace WGTun.Core;

public static class ProxiFyreConfiguration
{
    public static void Validate(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(settings.Scope) || !Enum.IsDefined(settings.Profile)) throw new ArgumentException("Unknown capture mode.");
        var rules = settings.Rules.Where(r => r.Enabled && (settings.Scope != TrafficScope.All || r.Action == RuleAction.Direct)).ToArray();
        if (rules.Length > 256 || (settings.Scope != TrafficScope.All && rules.Length == 0)) throw new ArgumentException("Select between 1 and 256 application rules.");
        foreach (var rule in rules) {
            if (RulePolicy.Validate(rule) is string error) throw new ArgumentException(error);
            if (rule.Type is not (RuleType.FolderPath or RuleType.ProcessName)) throw new NotSupportedException("Native process capture supports application folders and executable names. Destination IP and executable-path rules require a different capture backend.");
            bool exclude = settings.Scope == TrafficScope.ExceptSelected || rule.Action == RuleAction.Direct;
            if (exclude && (rule.Type != RuleType.FolderPath || rule.Protocol != "TCP + UDP")) throw new NotSupportedException("Use an application folder and TCP + UDP for a native exclusion. Name-substring and protocol-only exclusions cannot preserve the requested boundary.");
            if (rule.Protocol is not ("TCP" or "UDP" or "TCP + UDP")) throw new ArgumentException("Unknown rule protocol.");
            if (rule.Type == RuleType.FolderPath && (File.GetAttributes(rule.Value) & FileAttributes.ReparsePoint) != 0) throw new NotSupportedException("Capture rules cannot use linked application folders.");
        }
    }

    public static string Build(UserSettings settings, int port)
    {
        Validate(settings);
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var excludes = new List<string> { SingBoxDependencies.ProxiFyreExe, SingBoxDependencies.SingBoxExe, SingBoxDependencies.ByeDpiExe, SingBoxDependencies.ZapretExe, Path.Combine(AppContext.BaseDirectory, "WGTun.Service.exe") };
        var active = new List<RoutingRule>();
        foreach (var rule in settings.Rules.Where(r => r.Enabled)) {
            if (settings.Scope == TrafficScope.ExceptSelected || rule.Action == RuleAction.Direct) excludes.Add(Application(rule));
            else if (settings.Scope == TrafficScope.SelectedOnly) active.Add(rule);
        }
        var proxies = new List<object>();
        if (settings.Scope == TrafficScope.SelectedOnly) {
            foreach (var group in active.GroupBy(r => r.Protocol)) proxies.Add(Proxy(group.Select(Application).Distinct().ToArray(), group.Key));
            // A direct-only selected scope has nothing to intercept. Never substitute catch-all.
            if (proxies.Count == 0) throw new ArgumentException("Select an active application in include mode.");
        } else proxies.Add(Proxy([""], "TCP + UDP"));
        return JsonSerializer.Serialize(new { logLevel = settings.DetailedLogs ? "Info" : "Error", bypassLan = true, proxies, excludes = excludes.Distinct(StringComparer.OrdinalIgnoreCase), dns = new { mode = "Direct" } });

        object Proxy(string[] apps, string protocol) => new {
            appNames = apps, socks5ProxyEndpoint = $"127.0.0.1:{port}",
            supportedProtocols = protocol == "TCP + UDP" ? new[] { "TCP", "UDP" } : new[] { protocol },
            supportedAddressFamilies = settings.Profile is ProfileKind.Dpi or ProfileKind.Zapret ? new[] { "IPv4" } : new[] { "IPv4", "IPv6" }
        };
    }

    private static string Application(RoutingRule rule) => rule.Type == RuleType.FolderPath
        ? Path.GetFullPath(rule.Value).TrimEnd('\\', '/') + Path.DirectorySeparatorChar
        : Path.GetFileNameWithoutExtension(rule.Value);
}

public sealed partial class AppBackend
{
    public static string BuildProcessProxyConfiguration(string config, UserSettings settings, int port, ZapretEgress? egress = null)
    {
        CaptureBoundary.EnsureSupported(settings);
        if (settings.Profile is ProfileKind.Dpi or ProfileKind.Zapret) throw new InvalidOperationException("DPI methods use their own SOCKS endpoints, not a sing-box transport.");
        // The capture engine has already classified the application. All accepted SOCKS
        // connections use the selected transport, without creating a TUN or native routes.
        var transport = new UserSettings { Profile = settings.Profile, Scope = TrafficScope.All, Dpi = settings.Dpi, ZapretDpi = settings.ZapretDpi, DetailedLogs = settings.DetailedLogs };
        var root = JsonNode.Parse(BuildSingBoxConfiguration(config, transport, port, egress))!;
        root["inbounds"] = new JsonArray(new JsonObject { ["type"] = "socks", ["tag"] = "capture-in", ["listen"] = "127.0.0.1", ["listen_port"] = port });
        root["route"]!["rules"] = new JsonArray();
        root["route"]!["final"] = "active";
        return root.ToJsonString();
    }
}
