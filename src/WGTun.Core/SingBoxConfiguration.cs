using System.Net;
using System.Text.Json;
namespace WGTun.Core;
public sealed partial class AppBackend
{
    public static string BuildSingBoxConfiguration(string config, UserSettings settings, int socksPort = 10809, ZapretEgress? zapretEgress = null)
    {
        if (!Enum.IsDefined(settings.Profile)) throw new InvalidOperationException("Unknown connection mode.");
        if (socksPort is < 1024 or > 65535) throw new InvalidOperationException("Invalid SOCKS port.");
        bool zapret = settings.Profile == ProfileKind.Zapret;
        bool dpi = settings.Profile is ProfileKind.Dpi or ProfileKind.Zapret;
        if (zapret) zapretEgress ??= ZapretEgress.Detect();
        var root = new Dictionary<string, object>();
        root["log"] = new { level = "warn", timestamp = true };
        root["inbounds"] = Array.Empty<object>();
        var outbounds = new List<object> { new { type = "direct", tag = "direct" } };
        if (zapret) outbounds[0] = new { type = "direct", tag = "direct", bind_interface = zapretEgress!.Name };
        string dns = "1.1.1.1";
        if (dpi) {
            if (!zapret) _ = ByeDpiStrategies.Resolve(settings.ActiveDpi);
            outbounds.Add(new { type = "socks", tag = "active", server = "127.0.0.1", server_port = socksPort, version = "5" });
            if (!string.IsNullOrWhiteSpace(settings.ActiveDpi.Dns)) dns = settings.ActiveDpi.Dns.Trim();
        } else {
            string? error = ValidateConfig(config); if (error != null) throw new InvalidOperationException(error);
            var iface = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var peer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string>? current = null; int peers = 0;
            foreach (string raw in config.Split('\n')) {
                string line = raw.Trim(); if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.Equals("[Interface]", StringComparison.OrdinalIgnoreCase)) { current = iface; continue; }
                if (line.Equals("[Peer]", StringComparison.OrdinalIgnoreCase)) { current = peer; peers++; continue; }
                int equal = line.IndexOf('='); if (equal > 0) current![line[..equal].Trim()] = line[(equal + 1)..].Trim();
            }
            if (peers != 1) throw new InvalidOperationException("Exactly one WireGuard peer is supported.");
            if (!peer.TryGetValue("Endpoint", out string? endpoint) || !Uri.TryCreate("udp://" + endpoint, UriKind.Absolute, out var uri) || uri.Port is < 1 or > 65535)
                throw new InvalidOperationException("WireGuard Endpoint must contain host:port (brackets for IPv6).");
            string endpointHost = uri.Host.Trim('[', ']');
            if (Uri.CheckHostName(endpointHost) == UriHostNameType.Unknown) throw new InvalidOperationException("Invalid WireGuard endpoint host.");
            var p = new Dictionary<string, object> { ["address"] = endpointHost, ["port"] = uri.Port, ["public_key"] = peer["PublicKey"], ["allowed_ips"] = peer["AllowedIPs"].Split(',', StringSplitOptions.TrimEntries) };
            if (peer.TryGetValue("PresharedKey", out var psk)) p["pre_shared_key"] = psk;
            if (peer.TryGetValue("PersistentKeepalive", out var keep)) p["persistent_keepalive_interval"] = int.Parse(keep);
            var ep = new Dictionary<string, object> { ["type"] = "wireguard", ["tag"] = "active", ["system"] = false, ["domain_resolver"] = "dns-bootstrap", ["address"] = iface["Address"].Split(',', StringSplitOptions.TrimEntries), ["private_key"] = iface["PrivateKey"], ["peers"] = new[] { p } };
            if (iface.TryGetValue("MTU", out var mtu)) ep["mtu"] = int.Parse(mtu);
            root["endpoints"] = new[] { ep };
            if (iface.TryGetValue("DNS", out var profileDns)) dns = profileDns.Split(',')[0].Trim();
        }
        if (!IPAddress.TryParse(dns, out _)) throw new InvalidOperationException("DNS must be a literal IPv4 or IPv6 address.");
        root["outbounds"] = outbounds;
        // Windows DNS is shared across applications. Split modes preserve native DNS;
        // only all-traffic mode installs native DNS hijacking and leak prevention.
        // WireGuard DNS uses the peer. DPI DNS always uses authenticated DoH.
        var upstream = new Dictionary<string, object> { ["type"] = "udp", ["tag"] = "dns-upstream", ["server"] = dns, ["server_port"] = 53 };
        if (!dpi) upstream["detour"] = "active";
        else if (string.IsNullOrWhiteSpace(settings.ActiveDpi.Dns)) upstream = new() { ["type"] = "https", ["tag"] = "dns-upstream", ["server"] = "1.1.1.1", ["server_port"] = 443, ["tls"] = new { enabled = true, server_name = "cloudflare-dns.com" } };
        else upstream = new() { ["type"] = "https", ["tag"] = "dns-upstream", ["server"] = dns, ["server_port"] = 443, ["tls"] = new { enabled = true, server_name = dns } };
        var bootstrap = new Dictionary<string, object> { ["type"] = "https", ["tag"] = "dns-bootstrap", ["server"] = "1.1.1.1", ["server_port"] = 443, ["tls"] = new { enabled = true, server_name = "cloudflare-dns.com" } };
        if (dpi) upstream["detour"] = "active";
        if (zapret) bootstrap["detour"] = "direct";
        root["dns"] = new { servers = new object[] { upstream, bootstrap }, final = "dns-upstream", strategy = "prefer_ipv4" };
        root["route"] = new { rules = CompileSingBoxRules(settings), final = settings.Scope == TrafficScope.SelectedOnly ? "direct" : "active", auto_detect_interface = true, default_domain_resolver = "dns-bootstrap" };
        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }
}
