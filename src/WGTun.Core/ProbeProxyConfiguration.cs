using System.Text.Json.Nodes;
namespace WGTun.Core;
public sealed partial class AppBackend
{
    public static string BuildProbeProxyConfiguration(string config, UserSettings settings, int port, ZapretEgress? egress = null, int? enginePort = null)
    {
        CaptureBoundary.EnsureSupported(settings);
        // The capture engine has already classified the application. All accepted SOCKS
        // connections use the selected transport, without creating a TUN or native routes.
        var transport = new UserSettings { Profile = settings.Profile, Scope = TrafficScope.All, Dpi = settings.Dpi, ZapretDpi = settings.ZapretDpi, DetailedLogs = settings.DetailedLogs };
        var root = JsonNode.Parse(BuildSingBoxConfiguration(config, transport, enginePort ?? port, egress))!;
        root["inbounds"] = new JsonArray(new JsonObject { ["type"] = "socks", ["tag"] = "capture-in", ["listen"] = "127.0.0.1", ["listen_port"] = port });
        root["route"]!["rules"] = settings.Profile is ProfileKind.Dpi or ProfileKind.Zapret ? new JsonArray(new JsonObject { ["action"] = "resolve", ["server"] = "dns-upstream" }) : new JsonArray();
        root["route"]!["final"] = "active";
        return root.ToJsonString();
    }
}
