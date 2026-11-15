using System.Text.Json;
namespace WGTun.Core;

public sealed partial class AppBackend
{
    public async Task<OperationResult> ExportDiagnosticsAsync(string destination)
    {
        try {
            if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination)) return new(false, "Choose an absolute report path.");
            // Allowlist the report fields instead of attempting to redact arbitrary logs.
            var scan = await ScanSetupAsync();
            var brokerEvents = await BrokerCallAsync(new("diagnostic-events"));
            var report = new {
                format = 1, application = "OpenPrism", version = UpdateChecker.CurrentVersion,
                capturedUtc = DateTimeOffset.UtcNow, osVersion = Environment.OSVersion.Version.ToString(),
                architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                serviceInstalled = scan.ServiceInstalled, serviceReady = scan.ServiceReady,
                serviceRunning = scan.ServiceRunning,
                components = scan.Components?.Select(c => new { c.Name, c.Version, c.Installed, c.Running }),
                events = ReadDiagnosticEvents(FileAt("lifecycle.log")),
                serviceEvents = brokerEvents.Success ? brokerEvents.Lines : [],
                limitations = new[] { "No settings, keys, hosts, process names, file paths or raw logs are included.", "Engine state does not prove internet reachability." }
            };
            // Never silently replace an existing document or follow a selected report symlink.
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(output, report, new JsonSerializerOptions { WriteIndented = true });
            return new(true, "Diagnostic report saved without personal settings or raw logs.");
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) {
            return new(false, "Could not save diagnostic report. Choose a new writable filename.");
        }
    }
}
