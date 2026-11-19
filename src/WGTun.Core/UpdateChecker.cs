using System.Net;
using System.Text.Json;
namespace WGTun.Core;
public sealed record UpdateCheckResult(bool Success, string Message, string CurrentVersion, string? LatestVersion, bool UpdateAvailable, string? ReleaseUrl);
public static class UpdateChecker
{
    public const string CurrentVersion = "0.3.0";
    public static async Task<UpdateCheckResult> CheckAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenPrism/" + CurrentVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        try {
            using var response = await client.GetAsync("https://api.github.com/repos/Sikcey/OpenPrism/releases/latest");
            if (response.StatusCode == HttpStatusCode.NotFound) return new(false, "No published release is available.", CurrentVersion, null, false, null);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var latest = document.RootElement.GetProperty("tag_name").GetString() ?? "";
            var url = document.RootElement.GetProperty("html_url").GetString();
            if (!Version.TryParse(latest.TrimStart('v', 'V'), out var version)) return new(false, "Release version could not be compared.", CurrentVersion, latest, false, url);
            bool available = version > Version.Parse(CurrentVersion);
            return new(true, available ? "An update is available." : "You are up to date.", CurrentVersion, latest, available, url);
        } catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException) {
            return new(false, "Update check failed: " + e.Message, CurrentVersion, null, false, null);
        }
    }
}
