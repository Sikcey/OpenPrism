namespace WGTun.Core;

/// <summary>Entry point for the service host's --uninstall-app command.</summary>
public static class AppInstallLifecycle
{
    public static async Task<OperationResult> UninstallAsync() => await AppBackend.UninstallApplicationAsync();
}
