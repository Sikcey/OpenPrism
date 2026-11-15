using System.IO.Compression;
using System.Security.Cryptography;
using WGTun.Core;

static MemoryStream Zip(params string[] names)
{
    var stream = new MemoryStream();
    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        foreach (string name in names) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("fixture"); }
    stream.Position = 0; return stream;
}
static string Hash(MemoryStream stream) => Convert.ToHexString(SHA256.HashData(stream.ToArray()));
static void Reject(Action action)
{
    try { action(); } catch (IOException) { return; }
    throw new Exception("Unsafe fixture accepted");
}
string root = Path.Combine(Path.GetTempPath(), "WGTun-download-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try {
    using (var zip = Zip("nested/runtime.exe", "LICENSE")) {
        DependencyDownloads.ExtractVerified(zip, Hash(zip), Path.Combine(root, "valid"));
        if (File.ReadAllText(Path.Combine(root, "valid/nested/runtime.exe")) != "fixture") throw new Exception("Extraction failed");
        zip.Position = 0;
        Reject(() => DependencyDownloads.ExtractVerified(zip, new string('0', 64), Path.Combine(root, "tampered")));
        if (Directory.Exists(Path.Combine(root, "tampered"))) throw new Exception("Hash failure wrote files");
    }
    foreach (string name in new[] { "../escape.exe", "..\\escape.exe", "/absolute.exe", "C:/escape.exe", "runtime.exe:payload", "dir./runtime.exe" }) {
        using var zip = Zip("safe.exe", name);
        string target = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Reject(() => DependencyDownloads.ExtractVerified(zip, Hash(zip), target));
        if (Directory.Exists(target)) throw new Exception("Traversal rejection wrote files");
    }
    using (var zip = Zip("runtime.exe", "RUNTIME.EXE")) Reject(() => DependencyDownloads.ExtractVerified(zip, Hash(zip), Path.Combine(root, "duplicate")));
    using var source = new MemoryStream(new byte[33]); using var output = new MemoryStream();
    try { await DependencyDownloads.CopyBoundedAsync(source, output, 32, null, "fixture", default); throw new Exception("Size limit ignored"); } catch (IOException) { }
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); source.Position = 0;
    try { await DependencyDownloads.CopyBoundedAsync(source, output, 64, null, "fixture", cancelled.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
    Console.WriteLine("PASS verified ZIP, tampered hash, traversal/ADS/alias/duplicate rejection, bounded download and cancellation fixtures");
} finally { Directory.Delete(root, true); }

var discord = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord");
var migration = new UserSettings { RuleSchemaVersion = 1,
    Applications = [new() { Name = "Discord", Path = Path.Combine(discord, "app-1", "Discord.exe") }, new() { Name = "Discord", Path = Path.Combine(discord, "app-2", "Discord.exe"), Selected = true }],
    Rules = [new() { Name = "Discord", Type = RuleType.ProcessPath, Value = Path.Combine(discord, "app-1", "Discord.exe"), Enabled = false },
        new() { Name = "Discord", Type = RuleType.ProcessPath, Value = Path.Combine(discord, "app-2", "Discord.exe"), Enabled = true },
        new() { Name = "custom", Type = RuleType.ProcessPath, Value = Path.Combine(discord, "app-2", "Discord.exe"), Action = RuleAction.Direct },
        new() { Name = "test", Type = RuleType.ProcessName, Value = "curl.exe" },
        new() { Name = "my curl", Type = RuleType.ProcessName, Value = "curl.exe" }] };
RulePolicy.Migrate(migration);
RulePolicy.Migrate(migration);
if (migration.RuleSchemaVersion != 2 || migration.Applications.Count != 1 || !migration.Applications[0].Selected
    || migration.Rules.Count != 3 || !migration.Rules.Single(r => r.Type == RuleType.FolderPath).Enabled
    || !migration.Rules.Any(r => r.Name == "custom" && r.Type == RuleType.ProcessPath && r.Action == RuleAction.Direct)
    || !migration.Rules.Any(r => r.Name == "my curl")) throw new Exception("Folder migration lost rule semantics");
if (ApplicationDiscovery.CanonicalRoot(Path.Combine(discord, "Update.exe")) != discord) throw new Exception("Discord updater root mismatch");
var language = new UserSettings(); language.Language = "en";
var restored = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(System.Text.Json.JsonSerializer.Serialize(language))!;
if (restored.Language != "en") throw new Exception("Language persistence failed");
restored.Language = "invalid";
if (restored.Language != "tr") throw new Exception("Language fallback failed");
Console.WriteLine("PASS folder migration, custom rules, Discord updater and language persistence");
