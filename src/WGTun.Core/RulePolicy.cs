using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace WGTun.Core;

public static class RulePolicy
{
    public static string TypeName(RuleType type) => type switch {
        RuleType.IPv4Address => "IPv4 adresi", RuleType.IPv6Address => "IPv6 adresi",
        RuleType.IPv4Range => "IPv4 ağı / CIDR", RuleType.IPv6Range => "IPv6 ağı / CIDR",
        RuleType.ProcessName => "İşlem adı", RuleType.ProcessPath => "Uygulama yolu",
        RuleType.FolderPath => "Klasör yolu", _ => "Eski kural" };
    public static string? Validate(RoutingRule rule)
    {
        var value = rule.Value.Trim();
        if (value.Length == 0 || value.Length > 2048 || value.Any(c => char.IsControl(c) || ",;#\"=|".Contains(c))) return "Geçerli bir değer gir; satır sonu veya yapılandırma ayırıcıları kullanılamaz.";
        if (rule.Type is RuleType.IPv4Address or RuleType.IPv6Address or RuleType.IPv4Range or RuleType.IPv6Range) {
            bool v6 = rule.Type is RuleType.IPv6Address or RuleType.IPv6Range;
            bool range = rule.Type is RuleType.IPv4Range or RuleType.IPv6Range;
            var parts = value.Split('/');
            if (parts.Length != (range ? 2 : 1) || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != (v6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork) || parts[0].Contains('%')) return "Seçilen türle uyumlu bir IP adresi veya CIDR ağı gir.";
            if (!v6 && !Regex.IsMatch(parts[0], @"^\d{1,3}(\.\d{1,3}){3}$")) return "IPv4 adresini dört bölümle gir.";
            if (range && (!int.TryParse(parts[1], out int prefix) || prefix < 0 || prefix > (v6 ? 128 : 32))) return "Geçerli bir CIDR ön eki gir.";
            return null;
        }
        return rule.Type switch {
            RuleType.ProcessName => Regex.IsMatch(value, @"^[\p{L}\p{N}_ .-]+$") && !value.EndsWith('.') ? null : "Yalnızca işlem adını gir; örneğin Discord.exe.",
            RuleType.ProcessPath => Path.IsPathFullyQualified(value) && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(value) ? null : "Mevcut bir .exe dosyasının tam yolunu seç.",
            RuleType.FolderPath => Path.IsPathFullyQualified(value) && Directory.Exists(value) ? null : "Mevcut bir klasörün tam yolunu seç.",
            _ => "Eski kuralı yeni tür/değer yapısıyla düzenle." };
    }
    public static void Migrate(UserSettings settings)
    {
        if (settings.RuleSchemaVersion >= 2) return;
        bool legacy = settings.RuleSchemaVersion < 1;
        foreach (var rule in settings.Rules) {
            if (!legacy || rule.Type != RuleType.Legacy) continue;
            bool app = !string.IsNullOrWhiteSpace(rule.Application), ip = !string.IsNullOrWhiteSpace(rule.Destination);
            if (app != ip && rule.Action == RuleAction.ActiveConnection && rule.Protocol == "TCP + UDP") {
                rule.Value = app ? rule.Application : rule.Destination;
                if (app) rule.Type = Path.IsPathFullyQualified(rule.Value) ? RuleType.ProcessPath : RuleType.ProcessName;
                else if (IPAddress.TryParse(rule.Value.Split('/')[0], out var address)) rule.Type = (address.AddressFamily == AddressFamily.InterNetworkV6, rule.Value.Contains('/')) switch { (true,true) => RuleType.IPv6Range, (true,false) => RuleType.IPv6Address, (false,true) => RuleType.IPv4Range, _ => RuleType.IPv4Address };
            }
            // Preserve incompatible old exceptions for editing without silently changing their meaning.
            if (rule.Type == RuleType.Legacy) rule.Enabled = false;
        }
        settings.Rules.RemoveAll(r => r.Type == RuleType.ProcessName && r.Value.Equals("curl.exe", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(r.Name) || r.Name.Equals("curl.exe", StringComparison.OrdinalIgnoreCase) || r.Name.Equals("test", StringComparison.OrdinalIgnoreCase))
            && r.Action == RuleAction.ActiveConnection && r.Protocol == "TCP + UDP"
            && string.IsNullOrEmpty(r.Application) && string.IsNullOrEmpty(r.Destination));
        foreach (var app in settings.Applications) {
            if (string.IsNullOrWhiteSpace(app.FolderPath)) app.FolderPath = ApplicationDiscovery.CanonicalRoot(app.Path);
            foreach (var rule in settings.Rules.Where(r => r.Type == RuleType.ProcessPath
                && string.Equals(r.Value, app.Path, StringComparison.OrdinalIgnoreCase)
                && r.Name == app.Name && r.Action == RuleAction.ActiveConnection && r.Protocol == "TCP + UDP"
                && string.IsNullOrEmpty(r.Application) && string.IsNullOrEmpty(r.Destination))) {
                rule.Type = RuleType.FolderPath; rule.Value = app.FolderPath;
            }
            if (legacy && !settings.Rules.Any(r => (r.Type == RuleType.FolderPath && string.Equals(r.Value, app.FolderPath, StringComparison.OrdinalIgnoreCase)) || (r.Type == RuleType.ProcessPath && string.Equals(r.Value, app.Path, StringComparison.OrdinalIgnoreCase))))
                settings.Rules.Add(new() { Name = app.Name, Type = RuleType.FolderPath, Value = app.FolderPath, Enabled = app.Selected });
        }
        settings.Applications = settings.Applications.GroupBy(a => a.FolderPath, StringComparer.OrdinalIgnoreCase).Select(g => {
            var app = g.First(); app.Selected = g.Any(a => a.Selected); app.Running = g.Any(a => a.Running); return app;
        }).ToList();
        settings.Rules = settings.Rules.GroupBy(r => r.Type == RuleType.FolderPath
            ? $"{r.Type}|{r.Value.ToUpperInvariant()}|{r.Name}|{r.Action}|{r.Protocol}|{r.Application}|{r.Destination}"
            : r.Id.ToString()).Select(g => { var rule = g.First(); rule.Enabled = g.Any(r => r.Enabled); return rule; }).ToList();
        settings.RuleSchemaVersion = 2;
    }
}
