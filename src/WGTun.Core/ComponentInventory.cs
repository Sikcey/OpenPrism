using System.Security.Cryptography;
using System.Text.Json;
namespace WGTun.Core;
public sealed partial class AppBackend
{
    private static string ComponentInventoryPath(string component) => Path.Combine(BrokerRoot, "dependencies", component + ".inventory.json");
    private static void RecordComponentInventory(string staged, string component, bool existingOnly = false)
    {
        var files = new Dictionary<string, string>();
        foreach (string source in Directory.EnumerateFiles(staged, "*", SearchOption.AllDirectories)) {
            string relative = Path.GetRelativePath(staged, source);
            if (existingOnly) {
                string target = Path.Combine(BrokerRoot, "dependencies", relative);
                if (!File.Exists(target) || !SameContent(source, target)) continue;
            }
            using var input = File.OpenRead(source);
            files[relative] = Convert.ToHexString(SHA256.HashData(input));
        }
        File.WriteAllText(ComponentInventoryPath(component), JsonSerializer.Serialize(files));
    }
    private static bool RemoveComponentInventory(string component)
    {
        string inventory = ComponentInventoryPath(component);
        if (!File.Exists(inventory)) return false;
        string root = Path.Combine(BrokerRoot, "dependencies");
        var files = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(inventory)) ?? throw new IOException("Invalid component inventory.");
        foreach (var entry in files) {
            string relative = entry.Key.Replace('\\', '/');
            string target = Path.GetFullPath(Path.Combine(root, entry.Key));
            if (!relative.StartsWith(component + "/", StringComparison.Ordinal) || !target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe component inventory.");
            VerifyNoReparse(target);
            if (!File.Exists(target)) continue;
            using (var input = File.OpenRead(target)) if (!Convert.ToHexString(SHA256.HashData(input)).Equals(entry.Value, StringComparison.OrdinalIgnoreCase)) continue;
            for (int attempt = 0; ; attempt++) {
                try { File.Delete(target); break; }
                catch (IOException e) when (attempt < 40 && (e.HResult & 0xffff) is 32 or 33) { Thread.Sleep(250); }
            }
        }
        File.Delete(inventory);
        return true;
    }
    private static void InventoryLegacyGoodCheck()
    {
        if (File.Exists(ComponentInventoryPath("goodcheck")) || !Directory.Exists(Path.Combine(BrokerRoot, "dependencies", "goodcheck"))) return;
        var asset = DependencyDownloads.Engines.Single(a => a.Name == "goodcheck");
        if (!File.Exists(DependencyDownloads.Cached(asset))) return;
        using var staged = PrepareProtectedDependencies("goodcheck");
        RecordComponentInventory(staged.Path, "goodcheck", true);
    }
}
