using System.Security.Cryptography;
using Microsoft.Win32;
namespace WGTun.Core;

public sealed partial class AppBackend
{
    private static bool SameContent(string left, string right)
    {
        using var a = File.OpenRead(left); using var b = File.OpenRead(right);
        return a.Length == b.Length && SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));
    }
    private static void DeletePinnedDependency(string root, string relative)
    {
        VerifyNoReparse(root);
        string path = Path.Combine(root, relative);
        if (!File.Exists(path)) return;
        if (!SingBoxDependencies.IsPinnedFile(root, relative)) throw new IOException("Modified dependency preserved; cleanup needs manual inspection: " + relative);
        for (int attempt = 0; ; attempt++) {
            try { File.Delete(path); break; }
            catch (IOException e) when (attempt < 40 && (e.HResult & 0xffff) is 32 or 33) { Thread.Sleep(250); }
        }
    }
    private static void CleanupBrokerData()
    {
        VerifyNoReparse(BrokerData);
        foreach (string name in new[] { "last-profile.dpapi", "engine.log", "service.log", "service.log.previous", "lifecycle.log", "lifecycle.log.previous", "capture.log", "service-error.log" })
            File.Delete(Path.Combine(BrokerData, name));
        string captureRoot = Path.Combine(BrokerRoot, "dependencies", "proxifyre");
        if (Directory.Exists(captureRoot)) File.Delete(Path.Combine(captureRoot, "app-config.json"));
        if (Directory.Exists(captureRoot)) File.Delete(Path.Combine(captureRoot, "proxifyre_log.pcap"));
        string captureLogs = Path.Combine(captureRoot, "logs");
        VerifyNoReparse(captureLogs);
        if (Directory.Exists(captureLogs)) foreach (string log in Directory.EnumerateFiles(captureLogs, "*", SearchOption.AllDirectories)) {
            string name = Path.GetFileName(log);
            if (name == "logfile.txt" || System.Text.RegularExpressions.Regex.IsMatch(name, @"^logfile_\d+\.txt$")) File.Delete(log);
        }
        if (Directory.Exists(GoodCheckRoot)) foreach (string run in Directory.EnumerateDirectories(GoodCheckRoot)) {
            string name = Path.GetFileName(run);
            string id = name.StartsWith("run-") ? name[4..] : name;
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            foreach (string file in Directory.EnumerateFiles(run, "*", SearchOption.AllDirectories)) {
                string relative = Path.GetRelativePath(run, file).Replace('\\', '/');
                if (relative is "config.ini" or "CheckLists/hosts.txt" or "StrategiesGoGo/ByeDPI/strategies.txt"
                    || relative.StartsWith("Logs/", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(file).Equals(".log", StringComparison.OrdinalIgnoreCase)) File.Delete(file);
            }
        }
        if (Directory.Exists(BrokerData)) foreach (string file in Directory.EnumerateFiles(BrokerData, "tun-*")) {
            string name = Path.GetFileName(file)[4..]; int suffix = name.IndexOf('.');
            if (suffix > 0 && Guid.TryParseExact(name[..suffix], "N", out _)
                && name[suffix..] is ".json" or ".lease" or ".ready" or ".lease.stop") File.Delete(file);
        }
        // Legacy session files remain eligible for owned cleanup during migration.
        if (Directory.Exists(BrokerData)) foreach (string file in Directory.EnumerateFiles(BrokerData, "sing-box-*.json")) {
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file)[9..], "N", out _)) File.Delete(file);
        }
    }
    private static async Task RemoveOwnedWinDivertAsync()
    {
        var expected = new[] { "capture", "zapret" }.Select(component => Path.GetFullPath(Path.Combine(BrokerRoot, "dependencies", component, "WinDivert64.sys"))).ToArray();
        foreach (string name in new[] { "WinDivert", "WinDivert1.4", "WinDivert2.2" }) {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
            if (key?.GetValue("ImagePath") is not string image) continue;
            image = Environment.ExpandEnvironmentVariables(image.Trim('"'));
            if (image.StartsWith(@"\??\")) image = image[4..];
            if (!Path.IsPathFullyQualified(image) || !expected.Contains(Path.GetFullPath(image), StringComparer.OrdinalIgnoreCase)) continue;
            // Do not touch drivers whose registration points to another product.
            var stopped = await RunAsync(ScExe, "stop", name);
            if (stopped.Code is not (0 or 1062 or 1060)) throw new IOException("Owned WinDivert driver is still in use; files preserved.");
            var removed = await RunAsync(ScExe, "delete", name);
            if (removed.Code is not (0 or 1060 or 1072)) throw new IOException("Owned WinDivert driver registration could not be removed.");
        }
    }
}

public sealed partial class AppBackend
{
    internal static async Task<OperationResult> UninstallApplicationAsync()
    {
        try {
            if (ReadService(BrokerName) != null) {
                var result = await new AppBackend().RemoveServiceAsync();
                if (!result.Success) return result;
            }
            VerifyNoReparse(Root);
            foreach (string name in new[] { "settings.json", "wireguard.dpapi", "wgcf-account.dpapi", "warp-profile.dpapi", "warp-consent.json", "setup-result.json", "lifecycle.log", "lifecycle.log.previous" })
                File.Delete(FileAt(name));
            string cache = DependencyDownloads.Cache;
            VerifyNoReparse(cache);
            foreach (var asset in DependencyDownloads.Engines.Concat(DependencyDownloads.ZapretFiles).Append(DependencyDownloads.Wgcf)) {
                string file = DependencyDownloads.Cached(asset);
                if (File.Exists(file)) {
                    using (var input = File.OpenRead(file)) DependencyDownloads.Verify(input, asset.Hash);
                    File.Delete(file);
                }
            }
            if (Directory.Exists(cache) && !Directory.EnumerateFileSystemEntries(cache).Any()) Directory.Delete(cache);
            if (Directory.Exists(Root) && !Directory.EnumerateFileSystemEntries(Root).Any()) Directory.Delete(Root);
            return new(true, "Application data removed.");
        } catch (Exception e) { return new(false, "Application cleanup failed: " + e.Message); }
    }
}
