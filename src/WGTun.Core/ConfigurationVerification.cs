namespace WGTun.Core;
public sealed partial class AppBackend
{
    public async Task<OperationResult> VerifyGeneratedConfigurationsAsync(string directory)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "OpenPrism-config-check-" + Guid.NewGuid().ToString("N"));
        try {
            var asset = DependencyDownloads.Engines.Single(a => a.Name == "sing-box");
            await DependencyDownloads.EnsureAsync(asset);
            CreatePrivateDirectory(temporary);
            using (var archive = File.OpenRead(DependencyDownloads.Cached(asset))) DependencyDownloads.ExtractVerified(archive, asset.Hash, temporary);
            string executable = Path.Combine(temporary, "sing-box-" + SingBoxDependencies.SingBoxVersion + "-windows-amd64", "sing-box.exe");
            string[] files = Directory.GetFiles(Path.GetFullPath(directory), "*.json");
            if (files.Length == 0) return new(false, "No generated configurations found.");
            foreach (string file in files) {
                var result = await RunAsync(executable, "check", "-c", file);
                if (result.Code != 0) return new(false, "Configuration rejected: " + Path.GetFileName(file));
            }
            return new(true, $"Pinned sing-box accepted {files.Length} generated configurations.");
        } catch (Exception e) when (e is IOException or HttpRequestException or UnauthorizedAccessException) { return new(false, "Configuration verification could not complete."); }
        finally {
            string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(temporary).StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
    }
}
