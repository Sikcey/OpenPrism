using System.Diagnostics;
using Microsoft.Win32;
namespace WGTun.Core;

public static class ApplicationDiscovery
{
    public static string CanonicalRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var variant in new[] { "Discord", "DiscordCanary", "DiscordPTB", "DiscordDevelopment" }) {
            var root = Path.Combine(local, variant);
            if (folder.Equals(root, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return root;
        }
        if (Path.GetFileName(folder).Equals("Application", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(Path.GetDirectoryName(folder)), "Edge", StringComparison.OrdinalIgnoreCase)) return Path.GetDirectoryName(folder)!;
        return folder;
    }

    public static IReadOnlyList<AppEntry> Detect()
    {
        var found = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string path, bool running = false) {
            if (!File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
            var root = CanonicalRoot(path);
            if (found.TryGetValue(root, out var existing)) { existing.Running |= running; return; }
            found[root] = new() { Name = name, Path = Path.GetFullPath(path), FolderPath = root, Running = running };
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var variant in new[] { "Discord", "DiscordCanary", "DiscordPTB", "DiscordDevelopment" }) {
            var root = Path.Combine(local, variant);
            try {
                if (!Directory.Exists(root)) continue;
                foreach (var version in Directory.EnumerateDirectories(root, "app-*").OrderDescending()) Add(variant, Path.Combine(version, variant + ".exe"));
                Add(variant, Path.Combine(root, "Update.exe"));
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        var names = new[] { "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "zen.exe", "floorp.exe", "waterfox.exe", "librewolf.exe", "mullvadbrowser.exe", "chromium.exe", "thorium.exe" };
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine }) foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 }) {
            try {
                using var key = RegistryKey.OpenBaseKey(hive, view);
                using var apps = key.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                foreach (var exe in names) {
                    using var app = apps?.OpenSubKey(exe);
                    if (app?.GetValue(null) is string path) Add(Path.GetFileNameWithoutExtension(exe), Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
                }
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        // Browsers such as Zen do not always register Windows App Paths.
        foreach (var basePath in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Path.Combine(local, "Programs") }) {
            if (string.IsNullOrWhiteSpace(basePath)) continue;
            foreach (var (folder, exe, label) in new[] {
                ("Zen Browser", "zen.exe", "Zen Browser"), ("Zen", "zen.exe", "Zen Browser"),
                ("Floorp", "floorp.exe", "Floorp"), ("Waterfox", "waterfox.exe", "Waterfox"),
                ("LibreWolf", "librewolf.exe", "LibreWolf"), ("Mullvad Browser", "mullvadbrowser.exe", "Mullvad Browser"),
                ("Chromium\\Application", "chrome.exe", "Chromium"), ("Thorium\\Application", "thorium.exe", "Thorium") })
                Add(label, Path.Combine(basePath, folder, exe));
        }
        foreach (var process in Process.GetProcesses()) {
            using (process) try {
                var path = process.MainModule?.FileName;
                if (path == null) continue;
                var root = CanonicalRoot(path);
                if (found.TryGetValue(root, out var app)) { app.Running = true; continue; }
                if (!names.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    && !new[] { "Discord", "DiscordCanary", "DiscordPTB", "DiscordDevelopment" }.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)) continue;
                Add(process.ProcessName, path, true);
            } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
        }
        return found.Values.OrderBy(a => a.Name).ThenBy(a => a.FolderPath).ToList();
    }
}
