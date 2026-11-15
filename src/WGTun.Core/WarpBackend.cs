using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace WGTun.Core;

public sealed partial class AppBackend
{
    internal const string WgcfHash = "e222fd9443d7cb3264d825a3e86967797fc9c938507c5d93e3cc21e4aed11dcc";
    private static void CreatePrivateDirectory(string path, bool adminOnly = false)
    {
        for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Protected storage cannot use reparse points.");
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { adminOnly ? new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) : WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(path);
        if (!directory.Exists) directory.Create(acl); else directory.SetAccessControl(acl);
    }
    internal static string? ValidateAccount(string content)
    {
        if (content.Length > 65536) return "Account exceeds 64 KiB.";
        var fields = new Dictionary<string, string>();
        foreach (var raw in content.Split('\n')) {
            var line = raw.Trim(); if (line.Length == 0 || line.StartsWith('#')) continue;
            var match = Regex.Match(line, "^([a-z_]+)\\s*=\\s*([\"'])([^\"'\\r\\n\\x00-\\x1f]*)\\2\\s*$");
            if (!match.Success || !new[] { "device_id", "access_token", "private_key", "license_key" }.Contains(match.Groups[1].Value) || !fields.TryAdd(match.Groups[1].Value, match.Groups[3].Value)) return "Expected unique quoted wgcf account fields.";
        }
        if (!fields.TryGetValue("device_id", out var id) || string.IsNullOrWhiteSpace(id) || !fields.TryGetValue("access_token", out var token) || string.IsNullOrWhiteSpace(token) || !fields.TryGetValue("private_key", out var key)) return "Account requires device_id, access_token and private_key.";
        try { return Convert.FromBase64String(key).Length == 32 ? null : "Invalid account private key."; } catch (FormatException) { return "Invalid account private key."; }
    }
    public Task<OperationResult> EnsureWarpAccountAsync(bool acceptCloudflareTerms = false)
    {
        if (!File.Exists(FileAt("wgcf-account.dpapi")) && !acceptCloudflareTerms) return Task.FromResult(new OperationResult(false, "Accept Cloudflare application terms explicitly before creating an account: https://www.cloudflare.com/application/terms/"));
        return WarpOperationAsync(acceptCloudflareTerms, null, null);
    }
    public async Task<OperationResult> ImportWarpAccountAsync(string path)
    {
        try {
            var info = new FileInfo(path); if (!info.Exists || info.Length > 65536) return new(false, "Account file missing or too large.");
            var content = await File.ReadAllTextAsync(path); var error = ValidateAccount(content);
            if (error != null) return new(false, error);
            return await WarpOperationAsync(false, content, null);
        } catch { return new(false, "Account import failed. Check file permissions and account format."); }
    }
    public Task<OperationResult> ApplyWarpLicenseAsync(string license)
    {
        if (!Regex.IsMatch(license, @"\A[A-Za-z0-9]{8}-[A-Za-z0-9]{8}-[A-Za-z0-9]{8}\z")) return Task.FromResult(new OperationResult(false, "Expected a WARP license in XXXXXXXX-XXXXXXXX-XXXXXXXX format."));
        return WarpOperationAsync(false, null, license);
    }
    private static async Task<int> RunWgcfAsync(string exe, string work, params string[] args)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(exe) { WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true } };
        foreach (var key in process.StartInfo.Environment.Keys.Where(k => k.StartsWith("WGCF_", StringComparison.OrdinalIgnoreCase)).ToArray()) process.StartInfo.Environment.Remove(key);
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start(); process.StandardInput.Close();
        async Task DrainAsync(StreamReader reader) { var buffer = new char[4096]; while (await reader.ReadAsync(buffer) != 0) { } }
        var output = DrainAsync(process.StandardOutput); var error = DrainAsync(process.StandardError);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); throw new TimeoutException(); }
        finally { await Task.WhenAll(output, error); }
        return process.ExitCode;
    }
    private static async Task<OperationResult> WarpOperationAsync(bool consent, string? imported, string? license)
    {
        await Gate.WaitAsync(); string? work = null;
        async Task<OperationResult> ExecuteAsync()
        {
            try {
                CreatePrivateDirectory(Root);
                if (imported == null && license == null && File.Exists(FileAt("wgcf-account.dpapi")) && File.Exists(FileAt("warp-profile.dpapi"))) {
                    if (ValidateAccount(Encoding.UTF8.GetString(ReadSecret("wgcf-account.dpapi"))) != null || ValidateConfig(Encoding.UTF8.GetString(ReadSecret("warp-profile.dpapi"))) != null) return new(false, "Stored WARP material failed validation. Import a complete account to recover.");
                    return new(true, "Stored WARP account and profile ready; no registration performed.");
                }
                var bundled = await DependencyDownloads.EnsureAsync(DependencyDownloads.Wgcf, message => ConnectionProgress?.Invoke(message));
                if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return new(false, "The pinned wgcf package currently supports x64 processes only.");
                var binary = await File.ReadAllBytesAsync(bundled);
                if (!Convert.ToHexString(SHA256.HashData(binary)).Equals(WgcfHash, StringComparison.OrdinalIgnoreCase)) return new(false, "wgcf integrity verification failed; restore the official pinned package.");
                bool elevated = IsAdmin();
                work = Path.Combine(elevated ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) : Root, "WGTun-warp-" + Guid.NewGuid().ToString("N")); CreatePrivateDirectory(work, elevated);
                var exe = Path.Combine(work, "wgcf.exe"); await File.WriteAllBytesAsync(exe, binary);
                string account = Path.Combine(work, "wgcf-account.toml");
                if (imported != null) { await StoreSecretAsync("wgcf-account.dpapi", Encoding.UTF8.GetBytes(imported)); File.Delete(FileAt("warp-profile.dpapi")); }
                if (File.Exists(FileAt("wgcf-account.dpapi"))) await File.WriteAllBytesAsync(account, ReadSecret("wgcf-account.dpapi"));
                else {
                    if (!consent) return new(false, "Create or import an account before applying a license.");
                    await AtomicWriteAsync(FileAt("warp-consent.json"), Encoding.UTF8.GetBytes("{\"terms\":\"https://www.cloudflare.com/application/terms/\",\"acceptedUtc\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\"}"));
                    int code;
                    try { code = await RunWgcfAsync(exe, work, "register", "--accept-tos"); }
                    finally {
                        // wgcf writes credentials before subsequent account-detail requests; retain them even on timeout.
                        if (File.Exists(account) && ValidateAccount(await File.ReadAllTextAsync(account)) == null) await StoreSecretAsync("wgcf-account.dpapi", await File.ReadAllBytesAsync(account));
                    }
                    if (code != 0) return new(false, "WARP registration failed at Cloudflare; any completed account was retained. Retry later.");
                }
                var content = await File.ReadAllTextAsync(account); var validation = ValidateAccount(content); if (validation != null) return new(false, validation);
                if (license != null) {
                    content = Regex.Replace(content, @"(?m)^\s*license_key\s*=.*$", "").TrimEnd() + "\nlicense_key = \"" + license + "\"\n";
                    await File.WriteAllTextAsync(account, content, new UTF8Encoding(false)); File.Delete(FileAt("warp-profile.dpapi"));
                    if (await RunWgcfAsync(exe, work, "update") != 0) return new(false, "Cloudflare license update failed. Account retained; retry the license operation.");
                    await StoreSecretAsync("wgcf-account.dpapi", await File.ReadAllBytesAsync(account));
                }
                if (await RunWgcfAsync(exe, work, "generate") != 0) return new(false, "Account retained. Cloudflare profile generation failed; retry later.");
                var config = await File.ReadAllTextAsync(Path.Combine(work, "wgcf-profile.conf"));
                if (ValidateConfig(config) != null) return new(false, "Generated WARP profile failed validation; account retained.");
                await StoreSecretAsync("warp-profile.dpapi", Encoding.UTF8.GetBytes(config));
                return new(true, "WARP account and WireGuard profile encrypted for this Windows user. Ready for all-traffic service activation.");
            } catch { return new(false, "WARP operation failed. Check the pinned package, storage permissions and Cloudflare availability. Credentials and command output are withheld."); }
        }
        OperationResult result;
        try {
            result = await ExecuteAsync();
            if (work != null) {
                try { Directory.Delete(work, true); }
                catch { result = new(false, result.Message + " Temporary credential cleanup failed. Close WGTun and remove its ACL-protected WGTun-warp-* work directory before continuing."); }
            }
            return result;
        } finally { Gate.Release(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceState { public uint Type, State, Controls, Win32Exit, SpecificExit, CheckPoint, WaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(IntPtr service, out ServiceState status);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
    private static uint QueryServiceState()
    {
        var manager = OpenSCManager(null, null, 1); if (manager == IntPtr.Zero) return 0;
        try { var service = OpenService(manager, "WireGuardTunnel$" + Tunnel, 4); if (service == IntPtr.Zero) return 0;
            try { return QueryServiceStatus(service, out var state) ? state.State : 0; } finally { CloseServiceHandle(service); }
        } finally { CloseServiceHandle(manager); }
    }
}
