using Microsoft.Win32;
namespace WGTun.Core;
public sealed partial class AppBackend
{
    private static string NetshExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");
    private static string ProtectedCaptureExe => Path.Combine(BrokerRoot, "dependencies", "proxifyre", "ProxiFyre.exe");
    private static bool CaptureFirewallRuleExists(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules");
        if (key == null) return false;
        bool found = false;
        foreach (string valueName in key.GetValueNames()) {
            string value = key.GetValue(valueName) as string ?? "";
            if (!value.Contains("|Name=" + name + "|", StringComparison.OrdinalIgnoreCase)) continue;
            if (!value.Contains("|App=" + ProtectedCaptureExe + "|", StringComparison.OrdinalIgnoreCase)
                || !value.Contains("|Desc=OpenPrism capture v1|", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Foreign firewall rule preserved: " + name);
            found = true;
        }
        return found;
    }
    private static async Task EnsureCaptureFirewallAsync()
    {
        foreach (string protocol in new[] { "TCP", "UDP" }) {
            string name = "OpenPrism Capture " + protocol;
            if (CaptureFirewallRuleExists(name)) continue;
            var result = await RunAsync(NetshExe, "advfirewall", "firewall", "add", "rule", "name=" + name,
                "description=OpenPrism capture v1", "dir=in", "action=allow", "program=" + ProtectedCaptureExe,
                "protocol=" + protocol, "profile=any", "enable=yes");
            if (result.Code != 0) throw new IOException("Capture firewall rule could not be installed: " + protocol);
        }
    }
    private static async Task RemoveCaptureFirewallAsync()
    {
        foreach (string protocol in new[] { "TCP", "UDP" }) {
            string name = "OpenPrism Capture " + protocol;
            if (!CaptureFirewallRuleExists(name)) continue;
            var result = await RunAsync(NetshExe, "advfirewall", "firewall", "delete", "rule", "name=" + name,
                "program=" + ProtectedCaptureExe, "dir=in", "protocol=" + protocol);
            if (result.Code != 0) throw new IOException("Owned capture firewall rule could not be removed.");
        }
    }
}
