using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace WGTun.Core;

public sealed partial class AppBackend
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ServiceMainCallback(int argc, IntPtr argv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ServiceControlCallback(uint control);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ServiceTableEntry { [MarshalAs(UnmanagedType.LPWStr)] public string? Name; public ServiceMainCallback? Main; }
    [StructLayout(LayoutKind.Sequential)] private struct BrokerServiceStatus { public uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr RegisterServiceCtrlHandler(string name, ServiceControlCallback handler);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetServiceStatus(IntPtr handle, ref BrokerServiceStatus status);
    private static ServiceMainCallback? serviceMain;
    private static ServiceControlCallback? serviceHandler;
    private static IntPtr serviceHandle;
    private static CancellationTokenSource? serviceStop;
    private static string brokerOwner = "";
    public static void RunBrokerService(string ownerSid)
    {
        _ = new SecurityIdentifier(ownerSid);
        _ = typeof(System.Security.Claims.ClaimsIdentity).Assembly;
        using (var identity = WindowsIdentity.GetCurrent()) { _ = identity.User; }
        brokerOwner = ownerSid;
        serviceMain = BrokerServiceMain;
        serviceHandler = control => { if (control is 1 or 5) { ReportServiceState(3, 0, 30000); serviceStop?.Cancel(); } };
        if (!StartServiceCtrlDispatcher([new() { Name = BrokerName, Main = serviceMain }, new()])) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        GC.KeepAlive(serviceMain); GC.KeepAlive(serviceHandler);
    }
    private static void ReportServiceState(uint state, uint error = 0, uint wait = 0)
    {
        var status = new BrokerServiceStatus { Type = 0x10, State = state, Accepted = state == 4 ? 5u : 0u, Win32Exit = error, WaitHint = wait, Checkpoint = state is 2 or 3 ? 1u : 0u };
        SetServiceStatus(serviceHandle, ref status);
    }
    private static void BrokerServiceMain(int argc, IntPtr argv)
    {
        serviceHandle = RegisterServiceCtrlHandler(BrokerName, serviceHandler!);
        if (serviceHandle == IntPtr.Zero) return;
        using var cancellation = new CancellationTokenSource(); serviceStop = cancellation;
        ReportServiceState(2, 0, 30000);
        try { VerifyBrokerOwnership(brokerOwner); ValidateEngineDependencies(); ReportServiceState(4); RunBrokerLoopAsync(cancellation.Token).GetAwaiter().GetResult(); ReportServiceState(1); }
        catch (Exception e) { LifecycleDiagnostics.Record(BrokerData, "unhandled-error", e); try { File.WriteAllText(Path.Combine(BrokerRoot, "service-error.log"), e.ToString()); } catch { } ReportServiceState(1, 1066); }
        finally { serviceStop = null; }
    }
    private sealed class BrokerEngine : IAsyncDisposable
    {
        private SingBoxSession? session;
        private SavedTunnel? desired;
        private readonly RecoveryBackoff recovery = new();
        private readonly RecoveryWatchdog watchdog = new();
        private string message = "WGTun disconnected.";
        public ComponentStatus[] Components() {
            var result = new List<ComponentStatus> { new("WGTun", UpdateChecker.CurrentVersion, true, true, "Windows hizmeti çalışıyor.") };
            foreach (var (name, folder, version, process) in new[] {
                ("sing-box", "sing-box", SingBoxDependencies.SingBoxVersion, "sing-box"),
                ("Capture", "proxifyre", "2.7.0", "ProxiFyre"),
                ("ByeDPI", "byedpi", SingBoxDependencies.ByeDpiVersion, "ciadpi"),
                ("Zapret", "zapret", "trial", "winws") }) {
                bool installed = false; string detail;
                try { SingBoxDependencies.ValidateComponent(SingBoxDependencies.Root, folder); installed = true; detail = "Dosyalar doğrulandı."; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { detail = "Kurulum / onarım gerekli: " + e.Message; }
                bool running = session?.IsChildRunning(process) == true;
                result.Add(new(name, version, installed, running, running ? "Bağlantı motoru çalışıyor." : detail));
            }
            uint? driver = ReadService("ndisrd");
            result.Add(new("Windows Packet Filter", "", driver != null, driver == 4, driver == null ? "Install the official Windows Packet Filter prerequisite before using application capture." : "Shared driver; preserved during removal."));
            return result.ToArray();
        }
        public Task<BrokerReply> StatusAsync() => Task.FromResult(new BrokerReply(true,
            session?.IsRunning == true ? "Motor çalışıyor; internet erişimi doğrulanmadı." : message, session?.IsRunning == true));

        public async Task<BrokerReply> ConnectAsync(SavedTunnel saved, CancellationToken token)
        {
            ValidateBrokerSettings(saved.Settings);
            if (saved.Config.Length > 65536) throw new IOException("WireGuard profile exceeds 64 KiB.");
            CaptureBoundary.EnsureSupported(saved.Settings);
            _ = ProxiFyreConfiguration.Build(saved.Settings, 10809);
            await StopSessionAsync();
            desired = saved;
            session = await SingBoxSession.StartAsync(saved.Config, saved.Settings, BrokerData, token);
            if (!session.IsRunning) { await StopSessionAsync(); return new(false, "Tunnel engine exited during startup."); }
            try { SaveBrokerTunnel(saved); }
            catch { desired = null; await StopSessionAsync(); throw; }
            desired = saved;
            recovery.Reset();
            watchdog.Reset(Environment.TickCount64);
            message = "Tunnel engine running.";
            LogService("Bağlantı motoru başlatıldı: " + saved.Settings.Method);
            return await StatusAsync();
        }
        public async Task<BrokerReply> VerifyRecoveryAsync(CancellationToken token)
        {
            if ((await StatusAsync()).Connected) return new(false, "Disconnect before running the recovery check.");
            var prior = LoadBrokerTunnel();
            try {
                var test = new UserSettings { Profile = ProfileKind.Dpi, Scope = TrafficScope.SelectedOnly, RuleSchemaVersion = 2, AutoConnect = false,
                    Rules = [new() { Type = RuleType.ProcessName, Value = "openprism-recovery-probe.exe" }] };
                await ConnectAsync(new(test, "", false), token);
                await session!.CrashCaptureForCheckAsync();
                if (session.IsRunning) return new(false, "Child failure was not detected.");
                await RecoverAsync(token);
                if (session?.IsRunning != true) return new(false, "Child recovery failed.");
                session.RequestRebind();
                await RecoverAsync(token);
                return new(session?.IsRunning == true && !session.NeedsRebind, "Owned child crash and network-rebind recovery checks completed.");
            } finally {
                await StopAsync();
                if (prior != null) SaveBrokerTunnel(prior); else File.Delete(SavedTunnelFile);
            }
        }
        public async Task RecoverAsync(CancellationToken token)
        {
            if (watchdog.Observe(Environment.TickCount64)) session?.RequestRebind();
            if (desired == null || (session?.IsRunning == true && !session.NeedsRebind) || !recovery.Ready(DateTimeOffset.UtcNow)) return;
            var retry = desired;
            try { await ConnectAsync(retry, token); }
            catch (Exception e) when (!token.IsCancellationRequested) { desired = retry; recovery.Failed(DateTimeOffset.UtcNow); message = "Engine recovery pending; retrying with backoff."; LifecycleDiagnostics.Record(BrokerData, "recovery-failed", e); LogService(message); }
        }
        private async Task StopSessionAsync()
        {
            if (session != null) { var current = session; session = null; await current.DisposeAsync(); }
        }
        public async Task<BrokerReply> StopAsync()
        {
            desired = null; recovery.Reset(); await StopSessionAsync(); message = "Bağlantı kapatıldı."; LogService(message); return new(true, message);
        }
        public void SetMessage(string text) { message = text; LogService(text); }
        public void SetStartup(bool enabled) { if (desired != null) desired = desired with { Startup = enabled }; }
        public async ValueTask DisposeAsync() { await StopAsync(); }
    }
    private static string SavedTunnelFile => Path.Combine(BrokerData, "last-profile.dpapi");
    private static void SaveBrokerTunnel(SavedTunnel saved)
    {
        string temp = Path.Combine(BrokerData, Guid.NewGuid().ToString("N") + ".tmp");
        try { File.WriteAllBytes(temp, Protect(JsonSerializer.SerializeToUtf8Bytes(saved), false, true)); File.Move(temp, SavedTunnelFile, true); }
        finally { File.Delete(temp); }
    }
    private static SavedTunnel? LoadBrokerTunnel()
    {
        if (!File.Exists(SavedTunnelFile)) return null;
        if (new FileInfo(SavedTunnelFile).Length > 1048576) throw new IOException("Saved profile exceeds limit.");
        return JsonSerializer.Deserialize<SavedTunnel>(Protect(File.ReadAllBytes(SavedTunnelFile), true, true));
    }
    private static async Task RunBrokerLoopAsync(CancellationToken stop)
    {
        LogService("WGTun hizmeti başlatıldı.");
        await using var engine = new BrokerEngine();
        using var engineGate = new SemaphoreSlim(1, 1);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        Task<GoodCheckResult>? goodcheck = null;
        CancellationTokenSource? goodcheckCancel = null;
        Task recovery = Task.Run(async () => {
            try {
                await engineGate.WaitAsync(lifetime.Token);
                try {
                    var saved = LoadBrokerTunnel();
                    if (saved?.Startup == true) {
                        try { await engine.ConnectAsync(saved, lifetime.Token); }
                        catch (Exception e) when (!lifetime.IsCancellationRequested) { engine.SetMessage("Startup failed: " + e.Message); }
                    }
                } catch (Exception e) when (!lifetime.IsCancellationRequested) { engine.SetMessage("Saved profile unavailable: " + e.Message); }
                finally { engineGate.Release(); }
                while (true) {
                    await Task.Delay(TimeSpan.FromSeconds(15), lifetime.Token);
                    await engineGate.WaitAsync(lifetime.Token);
                    try { if (goodcheck == null || goodcheck.IsCompleted) await engine.RecoverAsync(lifetime.Token); } finally { engineGate.Release(); }
                }
            } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        });
        try {
            while (!stop.IsCancellationRequested) {
                var acl = new PipeSecurity(); acl.SetAccessRuleProtection(true, false);
                acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
                acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(brokerOwner), PipeAccessRights.ReadWrite, AccessControlType.Allow));
                acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
                using var pipe = NamedPipeServerStreamAcl.Create(BrokerPipe(brokerOwner), PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, acl);
                try { await pipe.WaitForConnectionAsync(stop); } catch (OperationCanceledException) { break; }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop); timeout.CancelAfter(TimeSpan.FromSeconds(90));
                try {
                    // Authenticate before reading or parsing any client-controlled data.
                    pipe.RunAsClient(() => {
                        using var identity = WindowsIdentity.GetCurrent(true);
                        if (identity?.User?.Value != brokerOwner || identity.ImpersonationLevel < TokenImpersonationLevel.Impersonation)
                            throw new UnauthorizedAccessException("Unauthorized pipe client.");
                    });
                    using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, true);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                    BrokerReply result;
                    try {
                        var request = JsonSerializer.Deserialize<BrokerRequest>(await ReadBoundedLineAsync(reader, 262144, timeout.Token)) ?? throw new IOException("Invalid request.");
                        await engineGate.WaitAsync(timeout.Token);
                        try {
                            if (request.Protocol != BrokerProtocol) throw new IOException("Incompatible broker protocol; update the paired app and service.");
switch (request.Command) {
case "handshake": result = new(true, "Compatible broker.", Protocol: BrokerProtocol, Capabilities: ["core-v3", "goodcheck-v2", "zapret-profiles", "capture-native-v1"]); break;
                                case "status": result = await engine.StatusAsync(); break;
                            case "verify-recovery": result = await engine.VerifyRecoveryAsync(timeout.Token); break;
                        case "logs": result = ReadBrokerLogs(); break;
                            case "diagnostic-events": result = new(true, "Sanitized lifecycle events.", Lines: ReadDiagnosticEvents(Path.Combine(BrokerData, "lifecycle.log")).Select(e => $"{e.Utc:O} {e.Event} {e.ErrorType}").ToArray()); break;
                        case "clear-logs": result = ClearBrokerLogs(); break;
                        case "health":
                            var components = engine.Components();
                            bool ready = components.Where(component => component.Name is "WGTun" or "sing-box" or "Capture" or "Windows Packet Filter").All(component => component.Installed);
                            result = new(ready, ready ? "Kurulu hizmet ve motor dosyaları doğrulandı." : "Bazı motor dosyaları doğrulanamadı; onarım gerekli.", Components: components); break;
                                case "goodcheck-start":
                                    if ((await engine.StatusAsync()).Connected) { result = new(false, "Disconnect before scanning.", GoodCheck: new(false, "engine-running", "Disconnect before scanning.", [])); break; }
                                    if (goodcheck is { IsCompleted: false }) { result = new(false, "A GoodCheck scan is already running."); break; }
                                    var scanHosts = ValidateGoodCheckHosts(request.Hosts);
                                    var scanEngine = ValidateGoodCheckEngine(request.Settings?.Dpi.Engine ?? "byedpi");
                            SingBoxDependencies.ValidateComponent(Path.Combine(BrokerRoot, "dependencies"), scanEngine);
                                    goodcheckCancel?.Dispose(); goodcheckCancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                                    var scanToken = goodcheckCancel.Token;
                                    GoodCheckProgress = "GoodCheck scan in progress.";
                                    goodcheck = Task.Run(() => ScanGoodCheckCoreAsync(scanHosts, scanEngine, scanToken));
                                    result = new(true, "GoodCheck started."); break;
                                case "goodcheck-state":
                                    result = new(true, "GoodCheck status.", GoodCheck: goodcheck == null ? new(false, "error", "No scan.", []) : goodcheck.IsCompleted ? await goodcheck : new(false, "running", GoodCheckProgress, [])); break;
                                case "goodcheck-cancel":
                                    goodcheckCancel?.Cancel(); result = new(true, "GoodCheck cancellation requested."); break;
                                case "save-profile":
                                if ((await engine.StatusAsync()).Connected) throw new IOException("Disconnect before replacing the stored startup profile.");
                                if (request.Settings == null || request.Config == null) throw new IOException("Missing profile.");
                                ValidateBrokerSettings(request.Settings);
                                CaptureBoundary.EnsureSupported(request.Settings);
                                _ = ProxiFyreConfiguration.Build(request.Settings, 10809);
                                if (request.Settings.Profile is ProfileKind.Cloudflare or ProfileKind.CustomWireGuard && ValidateConfig(request.Config) is string invalid) throw new IOException(invalid);
                                SaveBrokerTunnel(new(request.Settings, request.Config, request.Settings.AutoConnect));
                                result = new(true, "Profile stored without activating capture."); break;
                            case "connect":
                                    if (goodcheck is { IsCompleted: false }) throw new IOException("Stop the GoodCheck scan before connecting.");
                                    if (request.Settings == null || request.Config == null) throw new IOException("Missing settings/profile.");
                                    result = await engine.ConnectAsync(new(request.Settings, request.Config, request.Settings.AutoConnect), timeout.Token); break;
                                case "disconnect": result = await engine.StopAsync(); break;
                                case "resume":
                                    if (goodcheck is { IsCompleted: false }) throw new IOException("Stop the GoodCheck scan before connecting.");
                                    var last = LoadBrokerTunnel(); result = last == null ? new(false, "No saved profile.") : await engine.ConnectAsync(last, timeout.Token); break;
                                case "startup":
                                    var current = LoadBrokerTunnel();
                                    if (current != null) { SaveBrokerTunnel(current with { Startup = request.Enabled }); engine.SetStartup(request.Enabled); }
                                    result = new(current != null, current == null ? "Connect a profile first." : "Startup preference saved."); break;
                                default: result = new(false, "Unsupported service command."); break;
                            }
                        } finally { engineGate.Release(); }
                    } catch (Exception e) { LogService("İşlem başarısız: " + e.Message); result = new(false, e is OperationCanceledException ? "Service request timed out." : e.Message); }
                    await writer.WriteLineAsync(JsonSerializer.Serialize(result).AsMemory(), timeout.Token);
                } catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException or System.Text.DecoderFallbackException) { }
            }
        } finally { lifetime.Cancel(); goodcheckCancel?.Cancel(); if (goodcheck != null) await goodcheck; goodcheckCancel?.Dispose(); await recovery; }
    }
}
