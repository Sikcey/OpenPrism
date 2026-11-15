using System.Text;
namespace WGTun.Core;
public sealed partial class AppBackend
{
    public async Task<OperationResult> StoreSavedProfileAsync(UserSettings settings)
    {
        try {
            CaptureBoundary.EnsureSupported(settings);
            string config = settings.Profile is ProfileKind.Dpi or ProfileKind.Zapret ? "" : Encoding.UTF8.GetString(ReadSecret(settings.Profile == ProfileKind.Cloudflare ? "warp-profile.dpapi" : "wireguard.dpapi"));
            var reply = await BrokerCallAsync(new("save-profile", Settings: settings, Config: config));
            return new(reply.Success, reply.Message);
        } catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or NotSupportedException) { return new(false, "Could not store the profile without connecting: " + e.Message); }
    }
}
