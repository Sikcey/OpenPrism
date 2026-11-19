using System.Net;
using System.Text.RegularExpressions;
namespace WGTun.Core;
public sealed partial class AppBackend
{
    private static List<Dictionary<string, object>> CompileSingBoxRules(UserSettings settings)
    {
        if (!Enum.IsDefined(settings.Scope)) throw new InvalidOperationException("Unknown traffic scope.");
        var result = new List<Dictionary<string, object>> {
            new() { ["process_name"] = new[] { "WGTun.Tun.exe", "sing-box.exe", "ciadpi.exe", "winws.exe", "WGTun.Service.exe", "WGTun.Desktop.exe", "wgtun.exe" }, ["action"] = "route", ["outbound"] = "direct" },
            new() { ["ip_cidr"] = new[] { "127.0.0.0/8", "::1/128" }, ["action"] = "route", ["outbound"] = "direct" }
        };
        var rules = settings.Rules.Where(r => r.Enabled && (settings.Scope != TrafficScope.All || r.Action == RuleAction.Direct)).OrderByDescending(r => r.Action == RuleAction.Direct).ToList();
        if (settings.Profile == ProfileKind.Zapret) result.Add(new() { ["ip_version"] = 6, ["action"] = "route", ["outbound"] = "direct" });
        if (settings.Scope != TrafficScope.All && settings.RuleSchemaVersion == 0 && rules.Count == 0)
            rules.AddRange(settings.Applications.Where(a => a.Selected).Select(a => new RoutingRule { Type = RuleType.ProcessPath, Value = a.Path }));
        if ((rules.Count < 1 && settings.Scope != TrafficScope.All) || rules.Count > 256) throw new InvalidOperationException("Select between 1 and 256 rules.");
        foreach (var rule in rules) {
            if (RulePolicy.Validate(rule) is string error) throw new InvalidOperationException(error);
            if (!Enum.IsDefined(rule.Action)) throw new InvalidOperationException("Unknown rule action.");
            var item = new Dictionary<string, object> { ["action"] = "route", ["outbound"] = settings.Scope == TrafficScope.ExceptSelected || rule.Action == RuleAction.Direct ? "direct" : "active" };
            string value = rule.Value.Trim();
            switch (rule.Type) {
                case RuleType.ProcessName: item["process_name"] = new[] { value }; break;
                case RuleType.ProcessPath: item["process_path"] = new[] { Path.GetFullPath(value) }; break;
                case RuleType.FolderPath: item["process_path_regex"] = new[] { "(?i)^" + Regex.Escape(Path.GetFullPath(value).TrimEnd('\\', '/') + Path.DirectorySeparatorChar) }; break;
                case RuleType.IPv4Address: case RuleType.IPv6Address: case RuleType.IPv4Range: case RuleType.IPv6Range:
                    var parts = value.Split('/'); var ip = IPAddress.Parse(parts[0]);
                    var bytes = ip.GetAddressBytes(); int prefix = parts.Length == 2 ? int.Parse(parts[1]) : bytes.Length * 8;
                    for (int i = 0; i < bytes.Length; i++) bytes[i] &= (byte)(0xff << Math.Clamp(8 - (prefix - i * 8), 0, 8));
                    item["ip_cidr"] = new[] { new IPNetwork(new IPAddress(bytes), prefix).ToString() }; break;
                default: throw new InvalidOperationException("Migrate legacy rules before connecting.");
            }
            if (rule.Protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase)) item["network"] = "tcp";
            else if (rule.Protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase)) item["network"] = "udp";
            else if (rule.Protocol != "TCP + UDP") throw new InvalidOperationException("Unsupported rule protocol.");
            result.Add(item);
        }
        // Exclusions must precede DNS hijacking and overlapping active rules.
        int firstActive = result.FindIndex(r => r.TryGetValue("outbound", out var outbound) && Equals(outbound, "active"));
        result.Insert(firstActive < 0 ? result.Count : firstActive,
            new() { ["port"] = 53, ["action"] = "route", ["outbound"] = "direct" });
        return result;
    }
}
