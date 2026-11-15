namespace WGTun.Core;

public enum ProfileKind { Cloudflare, CustomWireGuard, Dpi, Zapret }
public enum ConnectionMethod { WireGuard, ByeDPI, Zapret }
public enum TrafficScope { All, SelectedOnly, ExceptSelected }
public enum RuleAction { ActiveConnection, Direct }
public enum RuleType { Legacy, IPv4Address, IPv6Address, IPv4Range, IPv6Range, ProcessName, ProcessPath, FolderPath }
public sealed class AppEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public bool Running { get; set; }
    public bool Selected { get; set; }
}
public sealed class RoutingRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public RuleType Type { get; set; }
    public string Value { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public string Application { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Protocol { get; set; } = "TCP + UDP";
    public RuleAction Action { get; set; }
}
public sealed class DpiSettings
{
    public string Engine { get; set; } = "byedpi";
    public string PresetId { get; set; } = "byedpi-standard";
    public string CustomArguments { get; set; } = "";
    public string Dns { get; set; } = "";
}
public sealed class UserSettings
{
    private string language = "tr";
    public string Language { get => language; set => language = value is "tr" or "en" or "ru" or "zh" ? value : "tr"; }
    public ProfileKind Profile { get; set; }
    // Profile remains the legacy JSON discriminator. Both setters select one method.
    [System.Text.Json.Serialization.JsonIgnore]
    public ConnectionMethod Method {
        get => Profile == ProfileKind.Zapret ? WGTun.Core.ConnectionMethod.Zapret : Profile == ProfileKind.Dpi ? WGTun.Core.ConnectionMethod.ByeDPI : WGTun.Core.ConnectionMethod.WireGuard;
        set {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (value == WGTun.Core.ConnectionMethod.Zapret) Profile = ProfileKind.Zapret;
            else if (value == WGTun.Core.ConnectionMethod.ByeDPI) Profile = ProfileKind.Dpi;
            else if (Profile is ProfileKind.Dpi or ProfileKind.Zapret) Profile = ProfileKind.Cloudflare;
        }
    }
    public ConnectionMethod ConnectionMethod { get => Method; set => Method = value; }
    public TrafficScope Scope { get; set; } = TrafficScope.SelectedOnly;
    public string WireGuardProfileName { get; set; } = "";
    public DpiSettings Dpi { get; set; } = new();
    public DpiSettings? ZapretDpi { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public DpiSettings ActiveDpi => Profile == ProfileKind.Zapret ? ZapretDpi ?? Dpi : Dpi;
    public List<AppEntry> Applications { get; set; } = [];
    public List<RoutingRule> Rules { get; set; } = [];
    public bool CloseToTray { get; set; } = true;
    public bool DetailedLogs { get; set; }
    public bool AutoConnect { get; set; } = true;
    public int RuleSchemaVersion { get; set; }
}
public sealed record OperationResult(bool Success, string Message);
public sealed record EngineStatus(bool Connected, string Message, string Engine = "");
public sealed record DpiPreset(string Id, string Engine, string Name, string[] Arguments, string Source, string Notes);

// Implementations must never report success for unsupported traffic routing.
public interface IAppBackend
{
    Task<SetupSnapshot> ScanSetupAsync();
    Task<OperationResult> InstallServiceAsync();
    Task<OperationResult> RemoveServiceAsync();
    Task<OperationResult> ResumeLastProfileAsync();
    Task<OperationResult> SetStartupAsync(bool enabled);
    Task<UserSettings> LoadAsync();
    Task SaveAsync(UserSettings settings);
    IReadOnlyList<AppEntry> DetectApplications();
    IReadOnlyList<DpiPreset> GetDpiPresets();
    Task<OperationResult> ImportWireGuardAsync(string path);
    Task<OperationResult> EnsureWarpAccountAsync(bool acceptCloudflareTerms = false);
    Task<OperationResult> ApplyWarpLicenseAsync(string license);
    Task<OperationResult> ImportWarpAccountAsync(string path);
    Task<OperationResult> ConnectAsync(UserSettings settings);
    Task<OperationResult> DisconnectAsync();
    Task<EngineStatus> GetStatusAsync();
}
