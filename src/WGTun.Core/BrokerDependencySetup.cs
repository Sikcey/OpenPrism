namespace WGTun.Core;

public sealed partial class AppBackend
{
    private sealed class ProtectedDependencySnapshot(string path) : IDisposable
    {
        internal string Path => path;
        public void Dispose() { Directory.Delete(path, true); }
    }

    private static ProtectedDependencySnapshot PrepareProtectedDependencies(string component = "sing-box")
    {
        string parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WGTun");
        VerifyNoReparse(parent);
        string path = Path.Combine(parent, "dependency-stage-" + Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(path, true);
        var snapshot = new ProtectedDependencySnapshot(path);
        try {
            foreach (var asset in DependencyDownloads.Engines.Where(a => a.Name == component)) {
                // The fixed cache path is never supplied by IPC/arguments. Hash the same
                // open handle that is extracted, with writes/deletion excluded.
                using var archive = new FileStream(DependencyDownloads.Cached(asset), FileMode.Open, FileAccess.Read, FileShare.Read);
                DependencyDownloads.ExtractVerified(archive, asset.Hash, Path.Combine(path, asset.Name));
            }
            if (component == "zapret") {
            Directory.CreateDirectory(Path.Combine(path, "zapret"));
            foreach (var asset in DependencyDownloads.ZapretFiles) {
                using var input = new FileStream(DependencyDownloads.Cached(asset), FileMode.Open, FileAccess.Read, FileShare.Read);
                DependencyDownloads.Verify(input, asset.Hash);
                using var output = new FileStream(Path.Combine(path, "zapret", asset.Name), FileMode.CreateNew);
                input.CopyTo(output);
            }
            }
            if (component == "capture") {
                Directory.CreateDirectory(Path.Combine(path, "capture"));
                foreach (var asset in DependencyDownloads.CaptureFiles) {
                    using var input = new FileStream(DependencyDownloads.Cached(asset), FileMode.Open, FileAccess.Read, FileShare.Read);
                    DependencyDownloads.Verify(input, asset.Hash);
                    using var output = new FileStream(Path.Combine(path, "capture", asset.Name), FileMode.CreateNew);
                    input.CopyTo(output);
                }
            }
            SingBoxDependencies.ValidateComponent(path, component);
            return snapshot;
        } catch { snapshot.Dispose(); throw; }
    }

    private static void CopyStagedDependencies(string staged, string component = "sing-box")
    {
        VerifyNoReparse(BrokerRoot);
        foreach (var file in Directory.EnumerateFiles(staged, "*", SearchOption.AllDirectories)) {
            string target = Path.Combine(BrokerRoot, "dependencies", Path.GetRelativePath(staged, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target)) {
                if (!SameContent(file, target)) throw new IOException("Unrecognized existing component file preserved: " + Path.GetFileName(target));
                continue; // A loaded, unchanged driver must never be overwritten.
            }
            File.Copy(file, target, true);
        }
        SingBoxDependencies.ValidateComponent(Path.Combine(BrokerRoot, "dependencies"), component);
        RecordComponentInventory(staged, component);
    }
private static async Task<int> ComponentElevatedAsync(string operation)
{
    OperationResult result;
    bool stopped = false;
    try {
        string[] parts = operation.Split(':');
        if (parts.Length != 2 || parts[0] is not ("install-component" or "remove-component") || parts[1] is not ("byedpi" or "zapret"))
            throw new ArgumentException("Unsupported component operation.");
        VerifyBrokerOwnership(IdentitySid);
        using var staged = parts[0] == "install-component" ? PrepareProtectedDependencies(parts[1]) : null;
        var processes = System.Diagnostics.Process.GetProcessesByName("WGTun.Service").Where(p => {
            try { return string.Equals(p.MainModule?.FileName, BrokerExe, StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }).ToArray();
        await StopBrokerForMaintenanceAsync();
        stopped = true;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            foreach (var process in processes) using (process) await process.WaitForExitAsync(timeout.Token);
        VerifyBrokerOwnership(IdentitySid);
        string root = Path.Combine(BrokerRoot, "dependencies", parts[1]);
        VerifyNoReparse(root);
        if (staged != null) { CopyStagedDependencies(staged.Path, parts[1]); ValidateProtectedEngine(); }
        else {
            string[] files = parts[1] == "byedpi" ? ["ciadpi.exe"] : DependencyDownloads.ZapretFiles.Select(a => a.Name).ToArray();
            if (parts[1] == "zapret") await RemoveOwnedWinDivertAsync();
            if (!RemoveComponentInventory(parts[1])) foreach (string file in files) DeletePinnedDependency(Path.Combine(BrokerRoot, "dependencies"), parts[1] + "/" + file);
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        var start = await RunAsync(ScExe, "start", BrokerName);
        if (start.Code is not (0 or 1056)) throw new IOException("Service restart failed: " + start.Code);
        bool ready = false;
        for (int attempt = 0; attempt < 30; attempt++) {
            if (ReadService(BrokerName) == 4 && (await BrokerCallAsync(new("health"))).Success) { ready = true; break; }
            await Task.Delay(200);
        }
        if (!ready) throw new IOException("Service did not become ready after the component operation.");
        stopped = false;
        result = new(true, "Component operation completed.");
    } catch (Exception e) { result = new(false, "Component operation failed: " + e.Message); }
    if (stopped) await RunAsync(ScExe, "start", BrokerName);
    await AtomicWriteAsync(FileAt("setup-result.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result));
    return result.Success ? 0 : 1;
}
}
