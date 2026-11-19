using System.Text.Json;
using WGTun.Core;

var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
if (args.FirstOrDefault() == "--uninstall-app") {
    try { var removed = await AppInstallLifecycle.UninstallAsync(); Console.WriteLine(JsonSerializer.Serialize(removed, json)); Environment.ExitCode = removed.Success ? 0 : 1; }
    catch { Environment.ExitCode = 1; }
    return;
}
if (args.FirstOrDefault() == "--broker-service") { AppBackend.RunBrokerService(args.ElementAtOrDefault(1) ?? ""); return; }
if (args.FirstOrDefault() == "--install-broker") { Environment.ExitCode = await AppBackend.InstallBrokerElevatedAsync(); return; }
if (args.FirstOrDefault() == "--remove-broker") { Environment.ExitCode = await AppBackend.RemoveBrokerElevatedAsync(); return; }
AppBackend.ConnectionProgress += message => Console.Error.WriteLine(JsonSerializer.Serialize(new { progress = message }, json));
var backend = new AppBackend();
AppDomain.CurrentDomain.UnhandledException += (_, e) => LifecycleDiagnostics.Record(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WGTun"), "unhandled-error", e.ExceptionObject as Exception);
async Task<T> Input<T>() {
    var input = new System.Text.StringBuilder(); var buffer = new char[4096];
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    int count;
    while ((count = await Console.In.ReadAsync(buffer.AsMemory(), timeout.Token)) != 0) {
        if (input.Length + count > 262144) throw new InvalidOperationException("İstek çok büyük.");
        input.Append(buffer, 0, count);
    }
    return JsonSerializer.Deserialize<T>(input.ToString(), json) ?? throw new InvalidOperationException("JSON isteği eksik.");
}
object result;
try {
    switch (args.FirstOrDefault()) {
        case "verify-packet-engine": SingBoxDependencies.ValidateSelectiveTun(); result = new OperationResult(true, "Packet engine matches the broker build."); break;
        case "prepare-components": result = await backend.PrepareComponentsAsync(args.ElementAtOrDefault(1) ?? "core"); break;
        case "verify-configs": result = await backend.VerifyGeneratedConfigurationsAsync(args.ElementAtOrDefault(1) ?? ""); break;
        case "scan": result = await backend.ScanSetupAsync(!args.Contains("--status-only")); break;
        case "apply-installer-rules": result = await backend.ApplyInstallerRulesAsync(await Input<AppEntry[]>()); break;
        case "load-settings": result = await backend.LoadAsync(); break;
        case "save-profile": result = await backend.StoreSavedProfileAsync(await Input<UserSettings>()); break;
        case "save-settings": await backend.SaveAsync(await Input<UserSettings>()); result = new OperationResult(true, "Ayarlar kaydedildi."); break;
        case "status": result = await backend.GetStatusAsync(); break;
        case "logs": result = await backend.GetLogsAsync(); break;
        case "clear-logs": result = await backend.ClearLogsAsync(); break;
        case "connect": result = await backend.ConnectAsync(await Input<UserSettings>()); break;
        case "disconnect": result = await backend.DisconnectAsync(); break;
        case "resume": result = await backend.ResumeLastProfileAsync(); break;
        case "startup": result = await backend.SetStartupAsync((await Input<JsonElement>()).GetProperty("enabled").GetBoolean()); break;
        case "install-service":
            result = await backend.InstallSelectedServiceAsync(args.ElementAtOrDefault(1) ?? ""); break;
        case "install-component": result = await backend.InstallComponentAsync(args.ElementAtOrDefault(1) ?? ""); break;
    case "remove-component": result = await backend.RemoveComponentAsync(args.ElementAtOrDefault(1) ?? ""); break;
    case "diagnostics-export": result = await backend.ExportDiagnosticsAsync(args.ElementAtOrDefault(1) ?? ""); break;
    case "remove-service": result = await backend.RemoveServiceAsync(); break;
        case "prepare-warp": result = await backend.EnsureWarpAccountAsync(args.Contains("--accept-cloudflare-terms")); break;
        case "import-wireguard": result = await backend.ImportWireGuardAsync(args.ElementAtOrDefault(1) ?? ""); break;
        case "import-warp": result = await backend.ImportWarpAccountAsync(args.ElementAtOrDefault(1) ?? ""); break;
        case "warp-license": result = await backend.ApplyWarpLicenseAsync((await Input<JsonElement>()).GetProperty("license").GetString() ?? ""); break;
        case "apply-update": result = await backend.ApplySignedUpdateAsync(args.ElementAtOrDefault(1) ?? ""); break;
        case "check-updates": result = await UpdateChecker.CheckAsync(); break;
case "goodcheck": result = await backend.ScanGoodCheckAsync(await Input<GoodCheckRequest>()); break;
        case "goodcheck-cancel": result = await backend.CancelGoodCheckAsync(); break;
        case "verify-recovery": result = await backend.VerifyRecoveryAsync(); break;
        case "presets": result = backend.GetDpiPresets(); break;
        case "detect": result = backend.DetectApplications(); break;
        case "validate-rule": var error = RulePolicy.Validate(await Input<RoutingRule>()); result = new OperationResult(error == null, error ?? "Kural geçerli."); break;
        default: result = new OperationResult(false, "Bilinmeyen komut."); break;
    }
} catch (Exception e) { result = new OperationResult(false, "İşlem tamamlanamadı: " + e.Message); }
if (args.FirstOrDefault() is "verify-configs" or "verify-packet-engine" && result is OperationResult { Success: false }) Environment.ExitCode = 1;
Console.WriteLine(JsonSerializer.Serialize(result, json));
