namespace WGTun.Core;
public static class CaptureBoundary
{
    public static void EnsureSupported(UserSettings settings) => ProxiFyreConfiguration.Validate(settings);
}
