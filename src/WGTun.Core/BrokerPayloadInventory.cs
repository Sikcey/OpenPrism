using System.Security.Cryptography;
using System.Text.Json;
namespace WGTun.Core;
public sealed partial class AppBackend
{
    private static void RecordBrokerPayload(string source)
    {
        var files = new Dictionary<string, string>();
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
            string relative = Path.GetRelativePath(source, file);
            if (SkipBrokerPayload(relative) || relative == "payload-inventory.json") continue;
            using var input = File.OpenRead(file);
            files[relative] = Convert.ToHexString(SHA256.HashData(input));
        }
        File.WriteAllText(Path.Combine(BrokerRoot, "payload-inventory.json"), JsonSerializer.Serialize(files));
    }
    private static void RemoveInventoriedPayload()
    {
        string inventory = Path.Combine(BrokerRoot, "payload-inventory.json");
        if (!File.Exists(inventory)) return;
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(inventory)) ?? throw new IOException("Invalid payload inventory.");
        foreach (var entry in entries) {
            string path = Path.GetFullPath(Path.Combine(BrokerRoot, entry.Key));
            if (!path.StartsWith(BrokerRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || SkipBrokerPayload(entry.Key)) throw new IOException("Unsafe inventory path.");
            VerifyNoReparse(path);
            if (!File.Exists(path)) continue;
            using (var input = File.OpenRead(path)) {
                if (!Convert.ToHexString(SHA256.HashData(input)).Equals(entry.Value, StringComparison.OrdinalIgnoreCase)) continue;
            }
            File.Delete(path);
        }
        File.Delete(inventory);
    }
}
