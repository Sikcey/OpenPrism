using System.Text.Json.Nodes;
namespace WGTun.Core;
public sealed partial class AppBackend
{
    public static string BuildSelectiveTunConfiguration(string config, UserSettings settings, int enginePort = 10809, ZapretEgress? egress = null)
    {
        CaptureBoundary.EnsureSupported(settings);
        var root = JsonNode.Parse(BuildSingBoxConfiguration(config, settings, enginePort, egress))!;
        // No OS adapter, auto-route, system resolver update or proxy listener.
        // WGTun.Tun supplies policy-selected packets to the embedded sing-tun stack.
        root["inbounds"] = new JsonArray();
        return root.ToJsonString();
    }
}
