namespace WGTun.Core;
public sealed partial class AppBackend
{
    public async Task<OperationResult> PrepareComponentsAsync(string component)
    {
        if (component is not ("core" or "byedpi" or "zapret")) return new(false, "Unknown component.");
        try {
            var assets = DependencyDownloads.Engines.Where(a => a.Name == "sing-box");
            assets = assets.Concat(DependencyDownloads.CaptureFiles);
            if (component == "byedpi") assets = assets.Concat(DependencyDownloads.Engines.Where(a => a.Name == "byedpi"));
            if (component == "zapret") assets = assets.Concat(DependencyDownloads.ZapretFiles);
            foreach (var asset in assets) await DependencyDownloads.EnsureAsync(asset, text => ConnectionProgress?.Invoke(text));
            return new(true, "Dependencies downloaded and verified. No installation or elevation performed.");
        } catch (Exception e) when (e is IOException or HttpRequestException or TaskCanceledException or UnauthorizedAccessException) { return new(false, "Could not prepare verified dependencies."); }
    }
}
