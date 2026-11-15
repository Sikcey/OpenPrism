using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WGTun.Core;

public sealed partial class AppBackend : IAppBackend
{
    public static event Action<string>? ConnectionProgress;
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WGTun");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private const string Tunnel = "WGTun";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string FileAt(string name) => Path.Combine(Root, name);

    public async Task<UserSettings> LoadAsync()
    {
        UserSettings settings;
        if (!File.Exists(FileAt("settings.json"))) settings = new();
        else { await using var stream = File.OpenRead(FileAt("settings.json")); settings = await JsonSerializer.DeserializeAsync<UserSettings>(stream, Json) ?? new(); }
        settings.Applications ??= []; settings.Rules ??= []; settings.Dpi ??= new();
        settings.ZapretDpi ??= settings.Dpi.Engine == "zapret" ? settings.Dpi : new DpiSettings { Engine = "zapret", PresetId = "zapret-standard" };
        RulePolicy.Migrate(settings);
        foreach (var app in DetectApplications()) {
            var existing = settings.Applications.FirstOrDefault(a => string.Equals(a.FolderPath, app.FolderPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { existing.Running = app.Running; existing.Path = app.Path; continue; }
            settings.Applications.Add(app);
            if (!settings.Rules.Any(r => r.Type == RuleType.FolderPath && string.Equals(r.Value, app.FolderPath, StringComparison.OrdinalIgnoreCase)))
                settings.Rules.Add(new RoutingRule { Name = app.Name, Type = RuleType.FolderPath, Value = app.FolderPath, Enabled = false });
        }
        return settings;
    }
    public async Task SaveAsync(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await Gate.WaitAsync();
        try { await AtomicWriteAsync(FileAt("settings.json"), JsonSerializer.SerializeToUtf8Bytes(settings, Json)); }
        finally { Gate.Release(); }
    }
    private static async Task AtomicWriteAsync(string path, byte[] data)
    {
        CreatePrivateDirectory(Root);
        string temp = Path.Combine(Root, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try { await File.WriteAllBytesAsync(temp, data); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static async Task StoreSecretAsync(string name, byte[] data) => await AtomicWriteAsync(FileAt(name), Protect(data, false));
    private static byte[] ReadSecret(string name)
    {
        CreatePrivateDirectory(Root);
        if ((File.GetAttributes(FileAt(name)) & FileAttributes.ReparsePoint) != 0) throw new IOException("Secret storage cannot use reparse points.");
        return Protect(File.ReadAllBytes(FileAt(name)), true);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);
    private static byte[] Protect(byte[] bytes, bool decrypt, bool machine = false)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            Blob output;
            bool ok = decrypt ? CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, machine ? 5 : 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, output.Size); return result; }
            finally { for (int i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
        } finally { for (int i = 0; i < input.Size; i++) Marshal.WriteByte(input.Data, i, 0); Marshal.FreeHGlobal(input.Data); }
    }
    public async Task<OperationResult> ImportWireGuardAsync(string path)
    {
        try {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 1024 * 1024) return new(false, "Configuration missing or larger than 1 MiB.");
            string config = await File.ReadAllTextAsync(path);
            string? error = ValidateConfig(config);
            if (error != null) return new(false, error);
            await Gate.WaitAsync();
            try { await StoreSecretAsync("wireguard.dpapi", Encoding.UTF8.GetBytes(config)); }
            finally { Gate.Release(); }
            var settings = await LoadAsync();
            settings.WireGuardProfileName = Path.GetFileName(path);
            await SaveAsync(settings);
            return new(true, "WireGuard configuration imported and encrypted for this Windows user.");
        } catch (Exception e) { return new(false, "Import failed: " + e.Message); }
    }
    internal static string? ValidateConfig(string config)
    {
        if (config.Length == 0 || config.IndexOf('\0') >= 0) return "Empty or invalid configuration.";
        string section = ""; int interfaces = 0, peers = 0; bool key = false, address = false, peerKey = false, allowed = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in config.Split('\n')) {
            string line = raw.Trim(); if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) {
                if (section == "Peer" && (!peerKey || !allowed)) return "Each peer requires PublicKey and AllowedIPs.";
                section = line[1..^1]; seen.Clear();
                if (section.Equals("Interface", StringComparison.OrdinalIgnoreCase)) { section = "Interface"; if (++interfaces > 1 || peers > 0) return "Exactly one Interface must precede peers."; }
                else if (section.Equals("Peer", StringComparison.OrdinalIgnoreCase)) { section = "Peer"; peers++; peerKey = allowed = false; }
                else return "Unknown configuration section.";
                continue;
            }
            int pos = line.IndexOf('='); if (pos < 1 || section == "") return "Malformed configuration line.";
            string name = line[..pos].Trim(), value = line[(pos + 1)..].Trim();
            string[] permitted = section == "Interface" ? ["PrivateKey", "Address", "DNS", "MTU", "ListenPort"] : ["PublicKey", "PresharedKey", "AllowedIPs", "Endpoint", "PersistentKeepalive"];
            if (!permitted.Contains(name, StringComparer.OrdinalIgnoreCase) || !seen.Add(name) || value.Length == 0 || value.Any(char.IsControl) || value.IndexOfAny([';', '|', '&', '`', '$', '"', '\'', '<', '>']) >= 0) return "Unsupported, duplicate, or unsafe configuration directive: " + name;
            if (name.Equals("PrivateKey", StringComparison.OrdinalIgnoreCase) || name.Equals("PublicKey", StringComparison.OrdinalIgnoreCase) || name.Equals("PresharedKey", StringComparison.OrdinalIgnoreCase)) {
                try { if (Convert.FromBase64String(value).Length != 32) return "WireGuard keys must be 32-byte base64 values."; } catch (FormatException) { return "Invalid WireGuard key."; }
            }
            if (section == "Interface" && name.Equals("PrivateKey", StringComparison.OrdinalIgnoreCase)) key = true;
            if (section == "Interface" && name.Equals("Address", StringComparison.OrdinalIgnoreCase)) address = true;
            if (section == "Peer" && name.Equals("PublicKey", StringComparison.OrdinalIgnoreCase)) peerKey = true;
            if (section == "Peer" && name.Equals("AllowedIPs", StringComparison.OrdinalIgnoreCase)) allowed = true;
        }
        return interfaces == 1 && peers > 0 && key && address && peerKey && allowed ? null : "Configuration requires an interface, private key, address, and at least one complete peer.";
    }
    public IReadOnlyList<AppEntry> DetectApplications() => ApplicationDiscovery.Detect();
    internal static string[] StandardByeDpiArguments => ["--tlsrec", "1"];
    public IReadOnlyList<DpiPreset> GetDpiPresets() => ByeDpiStrategies.All.Concat(ZapretStrategies.All).ToArray();
    private static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private static string? WireGuardExe() { string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard", "wireguard.exe"); return File.Exists(path) ? path : null; }
    private static async Task<(int Code, string Output)> RunAsync(string exe, params string[] args)
    {
        using var p = new Process { StartInfo = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (string arg in args) p.StartInfo.ArgumentList.Add(arg);
        p.Start(); var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { p.Kill(true); await p.WaitForExitAsync(); throw new TimeoutException("Service command timed out."); }
        return (p.ExitCode, (await stdout) + (await stderr));
    }
    public async Task<OperationResult> ConnectAsync(UserSettings settings)
    {
        if (settings == null) return new(false, "Settings required.");
        return await BrokerConnectAsync(settings);
    }
    public async Task<OperationResult> DisconnectAsync()
    {
        var reply = await BrokerCallAsync(new("disconnect")); return new(reply.Success, reply.Message);
    }
    public async Task<EngineStatus> GetStatusAsync()
    {
        if (ReadService(BrokerName) == null) return new(false, "WGTun hizmeti henüz kurulmadı. İlk kurulumdan devam et.", "wgtun-service");
        var reply = await BrokerCallAsync(new("status")); return new(reply.Connected, reply.Message, "wgtun-service");
    }
}
