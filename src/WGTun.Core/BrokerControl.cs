using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace WGTun.Core;

public sealed record ComponentStatus(string Name, string Version, bool Installed, bool Running, string Message, bool Known = true);
public sealed record SetupSnapshot(bool ServiceInstalled, bool ServiceRunning, bool DependencyReady, string DependencyName, IReadOnlyList<AppEntry> Applications, string Message, bool HasWarpAccount, bool HasPreparedWarpProfile, bool ServiceReady, IReadOnlyList<ComponentStatus>? Components = null);

public sealed partial class AppBackend
{
    private const string BrokerName = "WGTunBroker";
    private static string BrokerRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WGTun", "Broker");
    private static string BrokerExe => Path.Combine(BrokerRoot, "WGTun.Service.exe");
    private static string BrokerData => Path.Combine(BrokerRoot, "data");
    private static string BrokerImage(string sid) => "\"" + BrokerExe + "\" --broker-service " + sid;
    private static string BrokerPipe(string sid) => "WGTun.Control." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..24];
    private const int BrokerProtocol = 4;
    private sealed record BrokerRequest(string Command, UserSettings? Settings = null, string? Config = null, bool Enabled = false, string[]? Hosts = null, int Protocol = 0);
    private sealed record SavedTunnel(UserSettings Settings, string Config, bool Startup);
    private sealed record BrokerReply(bool Success, string Message, bool Connected = false, string[]? Lines = null, ComponentStatus[]? Components = null, GoodCheckResult? GoodCheck = null, int Protocol = 0, string[]? Capabilities = null);
    public async Task<LogSnapshot> GetLogsAsync() {
        var result = await BrokerCallAsync(new("logs"));
        if (!result.Success && result.Message.Contains("Unsupported service command", StringComparison.OrdinalIgnoreCase))
            return new(false, "Kurulu hizmet log desteğinden önceki sürümde. Hizmetler sayfasından onarımı çalıştır.", []);
        return new(result.Success, result.Message, result.Lines ?? []);
    }
    public Task<SetupSnapshot> ScanSetupAsync() => ScanSetupAsync(includeApplications: true);
    public async Task<SetupSnapshot> ScanSetupAsync(bool includeApplications)
    {
        uint? state = ReadService(BrokerName);
        var health = state == 4 ? await BrokerCallAsync(new("health")) : new BrokerReply(false, "");
        bool ready = state == 4 && health.Success;
        bool dependencyReady = ready || EngineDependenciesReady();
        string message = ready ? "OpenPrism hizmeti hazır." : state == null
            ? "Bir kerelik yönetici onaylı kurulum gerekli."
            : state != 4 ? "OpenPrism hizmeti kurulu ancak durmuş. Hizmeti başlat / onar düğmesini kullan."
            : "Hizmet çalışıyor ancak motor doğrulaması başarısız. Onarımı çalıştır. " + health.Message;
        var components = health.Components?.Where(c => c.Name is "WGTun" or "OpenPrism" or "ByeDPI" or "Zapret")
            .Select(c => c.Name == "WGTun" ? c with { Name = "OpenPrism" } : c).ToArray() ?? [
            new("OpenPrism", "", state != null, state == 4, message),
            new("ByeDPI", SingBoxDependencies.ByeDpiVersion, false, false, "Ayrıntılı durum için hizmeti güncelle / onar.", false),
            new("Zapret", "", false, false, "Service status unavailable.", false)
        ];
        return new(state != null, state == 4, dependencyReady, "OpenPrism", includeApplications ? DetectApplications() : [], message, HasWarpAccount, HasPreparedWarpProfile, ready, components);
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)] private struct ServiceProcessStatus { public uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint, ProcessId, Flags; }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceProcessStatus status, int size, out int needed);
    private static void VerifyBrokerServer(NamedPipeClientStream pipe)
    {
        if (!string.Equals(RegisteredBrokerImage(), BrokerImage(IdentitySid), StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("OpenPrism hizmeti bu kullanıcıya ait değil.");
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)) throw new UnauthorizedAccessException("Hizmet kimliği doğrulanamadı.");
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new UnauthorizedAccessException("Service manager unavailable.");
        try {
            var service = OpenService(manager, BrokerName, 4);
            if (service == IntPtr.Zero) throw new UnauthorizedAccessException("Service identity unavailable.");
            try {
                if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceProcessStatus>(), out _) || status.State != 4 || status.ProcessId != pid)
                    throw new UnauthorizedAccessException("IPC server does not match the registered Windows service.");
            } finally { CloseServiceHandle(service); }
        } finally { CloseServiceHandle(manager); }
    }
    private static async Task<BrokerReply> BrokerCallAsync(BrokerRequest request)
    {
        try {
            if (request.Command != "handshake") {
                var handshake = await BrokerCallAsync(new("handshake"));
                if (!handshake.Success || handshake.Protocol != BrokerProtocol || handshake.Capabilities?.Contains("core-v4") != true)
                    return new(false, "OpenPrism service is incompatible; update or repair the paired service and app.");
            }
            using var pipe = new NamedPipeClientStream(".", BrokerPipe(IdentitySid), PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(request.Command is "status" or "health" or "logs" ? 4 : 90));
            using (var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2))) await pipe.ConnectAsync(connectTimeout.Token);
            VerifyBrokerServer(pipe);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            string json = JsonSerializer.Serialize(request with { Protocol = BrokerProtocol }); if (json.Length > 262144) return new(false, "İstek boyutu sınırı aşıldı.");
            await writer.WriteLineAsync(json.AsMemory(), timeout.Token);
            return JsonSerializer.Deserialize<BrokerReply>(await ReadBoundedLineAsync(reader, request.Command is "goodcheck-state" or "logs" ? 262144 : 16384, timeout.Token)) ?? new(false, "Hizmet yanıtı geçersiz.");
        } catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException or JsonException) { return new(false, "OpenPrism hizmetine ulaşılamadı. İlk kurulum tamamlanmalı; kuruluysa hizmet durumunu kontrol et. " + e.Message); }
    }
    private static async Task<string> ReadBoundedLineAsync(StreamReader reader, int maximum, CancellationToken token)
    {
        var text = new StringBuilder(); char[] character = new char[1];
        while (text.Length <= maximum) { if (await reader.ReadAsync(character.AsMemory(), token) == 0) throw new IOException("Eksik IPC isteği."); if (character[0] == '\n') return text.ToString().TrimEnd('\r'); text.Append(character[0]); }
        throw new IOException("IPC istek sınırı aşıldı.");
    }
    private async Task<OperationResult> BrokerConnectAsync(UserSettings settings)
    {
        ValidateBrokerSettings(settings);
        if (settings.Profile == ProfileKind.Zapret) {
            var capabilities = await BrokerCallAsync(new("health"));
            if (!capabilities.Success || capabilities.Components?.Any(c => c.Name == "Zapret" && c.Installed) != true)
                return new(false, "Zapret desteği için Hizmetler ekranından hizmeti kur / onar. Kurulu hizmet eski veya Zapret dosyaları eksik.");
        }
        string file = settings.Profile == ProfileKind.Cloudflare ? "warp-profile.dpapi" : "wireguard.dpapi";
        if (settings.Profile is not (ProfileKind.Dpi or ProfileKind.Zapret) && !File.Exists(FileAt(file))) return new(false, settings.Profile == ProfileKind.Cloudflare ? "Önce Cloudflare koşullarını kabul ederek WARP profilini hazırla." : "Önce WireGuard profilini içe aktar.");
        string config = settings.Profile is ProfileKind.Dpi or ProfileKind.Zapret ? "" : Encoding.UTF8.GetString(ReadSecret(file));
        try { BuildSingBoxConfiguration(config, settings); } catch (Exception e) { return new(false, e.Message); }
        if (ReadService(BrokerName) == null) return new(false, "Önce ilk kurulum ekranındaki OpenPrism hizmetini kur düğmesini kullan.");
        await SaveAsync(settings);
        ConnectionProgress?.Invoke("Profil hizmete gönderiliyor ve bağlantı başlatılıyor…");
        var reply = await BrokerCallAsync(new("connect", settings, config)); return new(reply.Success, reply.Message);
    }
    public async Task<OperationResult> ResumeLastProfileAsync() { var r = await BrokerCallAsync(new("resume")); return new(r.Success, r.Message); }
    public async Task<OperationResult> SetStartupAsync(bool enabled) { var r = await BrokerCallAsync(new("startup", Enabled: enabled)); return new(r.Success, r.Message); }
    public async Task<OperationResult> InstallServiceAsync()
    {
        return await RunSetupAsync("--install-broker");
    }
    public Task<OperationResult> InstallSelectedServiceAsync(string component) => component is "byedpi" or "zapret" or "byedpi,zapret" ? RunSetupAsync("--install-broker", "bootstrap:" + component) : component is "" or "core" ? InstallServiceAsync() : Task.FromResult(new OperationResult(false, "Unsupported component."));
    public async Task<OperationResult> VerifyRecoveryAsync() { var reply = await BrokerCallAsync(new("verify-recovery")); return new(reply.Success, reply.Message); }
    public Task<OperationResult> RemoveServiceAsync() => RunSetupAsync("--remove-broker");
    public Task<OperationResult> InstallComponentAsync(string component) => ComponentSetupAsync(component, true);
    public Task<OperationResult> RemoveComponentAsync(string component) => ComponentSetupAsync(component, false);
    private static Task<OperationResult> ComponentSetupAsync(string component, bool install) =>
        component is "byedpi" or "zapret" ? RunSetupAsync("--install-broker", (install && ReadService(BrokerName) == null ? "bootstrap:" : install ? "install-component:" : "remove-component:") + component)
        : Task.FromResult(new OperationResult(false, "Unsupported component."));
    internal static ProcessStartInfo SetupStartInfo(string executable, string operation, bool elevated)
    {
        if (operation is not ("--install-broker" or "--remove-broker")) throw new ArgumentException("Desteklenmeyen kurulum işlemi.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = elevated ? "" : "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add(operation); return start;
    }
    private static async Task<OperationResult> RunSetupAsync(string argument, string? componentOperation = null)
    {
        try {
            CreatePrivateDirectory(Root);
            LifecycleDiagnostics.Record(Root, "setup-started");
            if (argument == "--install-broker" && (componentOperation == null || !componentOperation.StartsWith("remove-component:", StringComparison.Ordinal))) {
                bool bootstrap = componentOperation?.StartsWith("bootstrap:", StringComparison.Ordinal) == true;
                var assets = componentOperation == null || bootstrap ? DependencyDownloads.Engines.Where(a => a.Name == "sing-box") : Enumerable.Empty<DependencyDownloads.Asset>();
                var selected = componentOperation?.Split(':', 2).Last().Split(',') ?? [];
                if (selected.Contains("byedpi")) assets = assets.Concat(DependencyDownloads.Engines.Where(a => a.Name == "byedpi"));
                if (selected.Contains("zapret")) assets = assets.Concat(DependencyDownloads.ZapretFiles);
                if (componentOperation == null || bootstrap) assets = assets.Concat(DependencyDownloads.CaptureFiles);
                foreach (var asset in assets) await DependencyDownloads.EnsureAsync(asset, message => ConnectionProgress?.Invoke(message));
            }
            string exe = Path.Combine(AppContext.BaseDirectory, "WGTun.Service.exe");
            if (!File.Exists(exe)) return new(false, "Yayımlanmış WGTun.Service.exe bulunamadı.");
            ConnectionProgress?.Invoke(argument == "--install-broker" ? "One-time administrator approval: installing OpenPrism service." : "Removing OpenPrism service.");
            File.Delete(FileAt("setup-result.json"));
            var startInfo = SetupStartInfo(exe, argument, IsAdmin());
            if (componentOperation != null) startInfo.ArgumentList.Add(componentOperation);
            using var process = Process.Start(startInfo) ?? throw new IOException("Kurulum başlatılamadı.");
            await process.WaitForExitAsync();
            if (File.Exists(FileAt("setup-result.json"))) {
                var result = JsonSerializer.Deserialize<OperationResult>(await File.ReadAllTextAsync(FileAt("setup-result.json"))) ?? new(false, "Kurulum yanıtı okunamadı.");
                if (process.ExitCode != 0) return new(false, result.Message);
                if (!result.Success) return result;
                if (argument == "--remove-broker") return ReadService(BrokerName) == null ? result : new(false, "Hizmet kaydı hâlâ mevcut; kaldırma doğrulanamadı.");
                var health = await BrokerCallAsync(new("health"));
                return health.Success ? result : new(false, "Kurulumdan sonra hizmet doğrulanamadı: " + health.Message);
            }
            return new(false, "Kurulum tamamlanmadı (" + process.ExitCode + ").");
        } catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { return new(false, "Yönetici izni iptal edildi. Kurulum yapılmadı."); }
        catch (Exception e) { LifecycleDiagnostics.Record(Root, "setup-failed", e); return new(false, "Kurulum: " + e.Message); }
    }
    private static string? RegisteredBrokerImage() { using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + BrokerName); return key?.GetValue("ImagePath") as string; }
    private static void VerifyBrokerOwnership(string sid)
    {
        if (!string.Equals(RegisteredBrokerImage(), BrokerImage(sid), StringComparison.OrdinalIgnoreCase)) throw new IOException("OpenPrism hizmeti beklenen korumalı yola / kullanıcıya ait değil; değiştirilmedi.");
        string owner = File.ReadAllText(Path.Combine(BrokerRoot, "owner.txt")); if (owner != sid) throw new UnauthorizedAccessException("Bu OpenPrism kurulumu başka kullanıcıya ait.");
        VerifyNoReparse(BrokerRoot);
    }
    private static void VerifyNoReparse(string path)
    {
        for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent) if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Kurulum yolunda bağlantı noktası kullanılamaz.");
        if (Directory.Exists(path)) foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)) if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Kurulum dosyaları bağlantı noktası içeremez.");
    }
    public static async Task<int> InstallBrokerElevatedAsync()
    {
        OperationResult result;
        try {
            if (!IsAdmin()) throw new UnauthorizedAccessException("Kurulum için bir kerelik yönetici onayı gerekli.");
            string? componentOperation = Environment.GetCommandLineArgs().Skip(2).SingleOrDefault();
            if (componentOperation != null && !componentOperation.StartsWith("bootstrap:", StringComparison.Ordinal)) return await ComponentElevatedAsync(componentOperation);
            string? selected = componentOperation?[10..];
            if (selected is not (null or "byedpi" or "zapret" or "byedpi,zapret")) throw new ArgumentException("Unsupported component.");
            using var byedpiStage = selected is "byedpi" or "byedpi,zapret" ? PrepareProtectedDependencies("byedpi") : null;
            using var zapretStage = selected is "zapret" or "byedpi,zapret" ? PrepareProtectedDependencies("zapret") : null;
            using var staged = PrepareProtectedDependencies();
            using var captureStage = PrepareProtectedDependencies("capture");
            if (ReadService(BrokerName) != null) {
                VerifyBrokerOwnership(IdentitySid);
                string source = Path.GetFullPath(AppContext.BaseDirectory);
                VerifyNoReparse(source);
                if (!string.Equals(source.TrimEnd(Path.DirectorySeparatorChar), BrokerRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) {
                    var priorProcesses = Process.GetProcessesByName("WGTun.Service").Where(p => {
                        try { return string.Equals(p.MainModule?.FileName, BrokerExe, StringComparison.OrdinalIgnoreCase); }
                        catch { return false; }
                    }).ToArray();
                    await StopBrokerForMaintenanceAsync();
                    using (var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20))) {
                        try { foreach (var prior in priorProcesses) using (prior) await prior.WaitForExitAsync(exitTimeout.Token); }
                        catch (OperationCanceledException) { throw new IOException("Hizmet süreci henüz kapanmadı; dosyalar değiştirilmedi. Tekrar onarımı çalıştır."); }
                    }
                    foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
                        string relative = Path.GetRelativePath(source, file);
                        if (SkipBrokerPayload(relative)) continue;
                        if (relative.StartsWith("data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || relative.Equals("owner.txt", StringComparison.OrdinalIgnoreCase)) continue;
                        string destination = Path.Combine(BrokerRoot, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination, true);
                    }
                }
                CopyStagedDependencies(staged.Path);
                CopyStagedDependencies(captureStage.Path, "capture");
                if (byedpiStage != null) CopyStagedDependencies(byedpiStage.Path, "byedpi");
                if (zapretStage != null) CopyStagedDependencies(zapretStage.Path, "zapret");
                ValidateProtectedEngine();
                if (ReadService(BrokerName) != 4) { var started = await RunAsync(ScExe, "start", BrokerName); if (started.Code is not (0 or 1056)) throw new IOException("OpenPrism hizmeti başlatılamadı (Windows " + started.Code + ")."); }
                result = new(true, "OpenPrism hizmeti hazır.");
            }
            else {
                SingBoxDependencies.ValidateComponent(staged.Path, "sing-box");
                string source = Path.GetFullPath(AppContext.BaseDirectory);
                VerifyNoReparse(source); VerifyNoReparse(BrokerRoot);
                CleanupOrphanedEngineLogs();
                if (Directory.Exists(BrokerRoot)) throw new IOException("Previous installation directory contains preserved files: " + string.Join(", ", Directory.EnumerateFiles(BrokerRoot, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(BrokerRoot, path)).Take(20)));
                CreatePrivateDirectory(BrokerRoot, true);
                foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
                    string relative = Path.GetRelativePath(source, file); if (SkipBrokerPayload(relative)) continue; string destination = Path.Combine(BrokerRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
                }
                CopyStagedDependencies(staged.Path);
                CopyStagedDependencies(captureStage.Path, "capture");
                if (byedpiStage != null) CopyStagedDependencies(byedpiStage.Path, "byedpi");
                if (zapretStage != null) CopyStagedDependencies(zapretStage.Path, "zapret");
                CreatePrivateDirectory(BrokerData, true); ValidateProtectedEngine(); await File.WriteAllTextAsync(Path.Combine(BrokerRoot, "owner.txt"), IdentitySid);
                var created = await RunAsync(ScExe, "create", BrokerName, "binPath=", BrokerImage(IdentitySid), "start=", "auto", "DisplayName=", "OpenPrism bağlantı hizmeti");
                if (created.Code != 0) throw new IOException("OpenPrism hizmeti kaydedilemedi (Windows " + created.Code + ").");
                await RunAsync(ScExe, "description", BrokerName, "OpenPrism kullanıcısının doğrulanmış son profilini başlatır; yalnızca OpenPrism'a ait tüneli yönetir.");
                await RunAsync(ScExe, "failure", BrokerName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000");
                var started = await RunAsync(ScExe, "start", BrokerName);
                if (started.Code != 0) throw new IOException("OpenPrism hizmeti başlatılamadı (Windows " + started.Code + ").");
                result = new(true, "Bir kerelik kurulum tamamlandı. Bundan sonra Bağlan ve Windows başlangıcı yönetici izni istemez.");
            }
            if (result.Success) {
                RecordBrokerPayload(Path.GetFullPath(AppContext.BaseDirectory));
                InventoryLegacyGoodCheck();
                ValidateProtectedEngine();
                // Selective packet ingress opens no exposed listener or firewall exception.
                var recovery = await RunAsync(ScExe, "failure", BrokerName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000");
                if (recovery.Code != 0) throw new IOException("Service recovery registration failed.");
                var failureFlag = await RunAsync(ScExe, "failureflag", BrokerName, "1");
                if (failureFlag.Code != 0) throw new IOException("Service failure recovery registration failed.");
                BrokerReply health = new(false, "Service did not become ready.");
                for (int attempt = 0; attempt < 10; attempt++) {
                    health = await BrokerCallAsync(new("health"));
                    if (health.Success) break;
                    await Task.Delay(500);
                }
                if (!health.Success) result = new(false, health.Message);
            }
        } catch (Exception e) { result = new(false, "Kurulum tamamlanamadı: " + e.Message); }
        await AtomicWriteAsync(FileAt("setup-result.json"), JsonSerializer.SerializeToUtf8Bytes(result)); return result.Success ? 0 : 1;
    }
    public static async Task<int> RemoveBrokerElevatedAsync()
    {
        OperationResult result;
        try {
            if (!IsAdmin()) throw new UnauthorizedAccessException("Hizmet kaldırma için yönetici onayı gerekli.");
            if (ReadService(BrokerName) == null) {
                if (Directory.Exists(BrokerRoot)) {
                    VerifyNoReparse(BrokerRoot);
                    if (Directory.Exists(BrokerData)) CleanupBrokerData();
                    foreach (string directory in Directory.EnumerateDirectories(BrokerRoot, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
                        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
                    if (!Directory.EnumerateFileSystemEntries(BrokerRoot).Any()) Directory.Delete(BrokerRoot);
                }
                var absent = new OperationResult(true, "Service is absent; recognized legacy temporary files cleaned. Unknown files preserved.");
                await AtomicWriteAsync(FileAt("setup-result.json"), JsonSerializer.SerializeToUtf8Bytes(absent));
                return 0;
            }
            VerifyBrokerOwnership(IdentitySid);
            var serviceProcesses = Process.GetProcessesByName("WGTun.Service").Where(p => {
                try { return string.Equals(p.MainModule?.FileName, BrokerExe, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            }).ToArray();
            await StopBrokerForMaintenanceAsync();
            using (var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15))) {
                try { foreach (var process in serviceProcesses) { using (process) await process.WaitForExitAsync(exitTimeout.Token); } }
                catch (OperationCanceledException) { throw new IOException("OpenPrism hizmet süreci henüz kapanmadı; dosyalar korunuyor."); }
            }
            await RemoveCaptureFirewallAsync();
            await RemoveOwnedWinDivertAsync();
            var deleted = await RunAsync(ScExe, "delete", BrokerName); if (deleted.Code != 0) throw new IOException("Hizmet silinemedi (Windows " + deleted.Code + ").");
            VerifyNoReparse(BrokerRoot);
            string target = Path.GetFullPath(BrokerRoot); string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WGTun", "Broker");
            if (!string.Equals(target, expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Kurulum kaldırma yolu doğrulanamadı.");
            // Remove only files matching the published application payload and pinned components.
            // Unknown files are retained, including any third-party files in dependency folders.
            string source = Path.GetFullPath(AppContext.BaseDirectory);
            if (!string.Equals(source.TrimEnd(Path.DirectorySeparatorChar), target.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) {
                VerifyNoReparse(source);
                foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
                    string relative = Path.GetRelativePath(source, file);
                    if (SkipBrokerPayload(relative)) continue;
                    string owned = Path.Combine(target, relative);
                    if (File.Exists(owned) && SameContent(owned, file)) File.Delete(owned);
                }
            }
            foreach (string component in new[] { "sing-box", "capture", "byedpi", "zapret", "goodcheck" }) {
                if (!RemoveComponentInventory(component)) foreach (string relative in SingBoxDependencies.PinnedRelativePaths.Where(path => path.StartsWith(component + "/", StringComparison.Ordinal))) DeletePinnedDependency(Path.Combine(target, "dependencies"), relative);
            }
            File.Delete(Path.Combine(target, "service-error.log"));
            RemoveInventoriedPayload();
            CleanupBrokerData();
            File.Delete(Path.Combine(target, "owner.txt"));
            foreach (string dir in Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            if (!Directory.EnumerateFileSystemEntries(target).Any()) Directory.Delete(target);
            result = new(true, "OpenPrism service, engine files and saved service profile removed. Unknown files preserved.");
        } catch (Exception e) { result = new(false, "Kaldırma tamamlanamadı: " + e.Message); }
        await AtomicWriteAsync(FileAt("setup-result.json"), JsonSerializer.SerializeToUtf8Bytes(result)); return result.Success ? 0 : 1;
    }
}
