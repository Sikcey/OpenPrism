using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("WGTun.Checks")]
[assembly: InternalsVisibleTo("WGTun.DependencyChecks")]
namespace WGTun.Core;

internal static class DependencyDownloads
{
    internal sealed record Asset(string Name, string Url, string Hash);
    internal static readonly Asset[] Engines = [
        new("proxifyre", "https://github.com/wiresock/proxifyre/releases/download/v2.7.0/ProxiFyre-v2.7.0-x64.zip", "29a41f6f1d6e79225274ad30450da4cdbcdc0652fa4f0819f35157541978764f"),
        new("sing-box", "https://github.com/SagerNet/sing-box/releases/download/v1.14.4/sing-box-1.14.4-windows-amd64.zip", "99185d75d314a07dbba4a130c8ad986cbeb629a07927975636b341ca61f991df"),
        new("goodcheck", "https://github.com/Storik4pro/GoodCheckGoGo/releases/download/0.8.5/bin.zip", "e140c49eee8e84e66d2188e51829d21ac8f4f4b466fed26a5412f49425ae7cc9"),
        new("byedpi", "https://github.com/hufrea/byedpi/releases/download/v0.17.3/byedpi-17.3-x86_64-w64.zip", "70d2c94147193cb915f9c6eb5144b8d404dacbcfa90bda2383b6b211afafa456")];
    internal static readonly Asset Wgcf = new("wgcf", "https://github.com/ViRb3/wgcf/releases/download/v2.3.1/wgcf_2.3.1_windows_amd64.exe", AppBackend.WgcfHash);
    internal static readonly Asset[] ZapretFiles = [
        new("winws.exe", "https://raw.githubusercontent.com/bol-van/zapret-win-bundle/6b4375fa04f6bd7017eec379e16e462d3ea8df66/zapret-winws/winws.exe", "2da71e80878dc270ac83f5893ecbb841f9752a57f1da8ff9325636b4346bc632"),
        new("WinDivert.dll", "https://raw.githubusercontent.com/bol-van/zapret-win-bundle/6b4375fa04f6bd7017eec379e16e462d3ea8df66/zapret-winws/WinDivert.dll", "16abd6a029e65557c6a309bea7b13bf81fff4e193567582e1cddbf6719f323e0"),
        new("WinDivert64.sys", "https://raw.githubusercontent.com/bol-van/zapret-win-bundle/6b4375fa04f6bd7017eec379e16e462d3ea8df66/zapret-winws/WinDivert64.sys", "8da085332782708d8767bcace5327a6ec7283c17cfb85e40b03cd2323a90ddc2"),
        new("cygwin1.dll", "https://raw.githubusercontent.com/bol-van/zapret-win-bundle/6b4375fa04f6bd7017eec379e16e462d3ea8df66/zapret-winws/cygwin1.dll", "103104a52e5293ce418944725df19e2bf81ad9269b9a120d71d39028e821499b")
    ];
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(4) };
    private static readonly SemaphoreSlim Gate = new(1);
    internal static string Cache => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WGTun", "downloads-v1");
    internal static string Cached(Asset asset) => Path.Combine(Cache, asset.Hash + (asset == Wgcf ? ".exe" : ".zip"));
    internal static void Verify(Stream stream, string hash)
    {
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Dependency SHA256 mismatch.");
        stream.Position = 0;
    }
    internal static async Task<string> EnsureAsync(Asset asset, Action<string>? progress = null, CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        string? partial = null;
        try {
            Directory.CreateDirectory(Cache);
            string target = Cached(asset);
            if (File.Exists(target)) {
                try { using var existing = File.OpenRead(target); Verify(existing, asset.Hash); return target; }
                catch (IOException) { File.Delete(target); }
            }
            partial = Path.Combine(Cache, Guid.NewGuid().ToString("N") + ".partial");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(4));
            progress?.Invoke("Downloading " + asset.Name + "…");
            using var response = await Client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true)) {
                await CopyBoundedAsync(input, output, 256L * 1024 * 1024, progress, asset.Name, timeout.Token);
                output.Position = 0; Verify(output, asset.Hash);
            }
            File.Move(partial, target, true); partial = null;
            progress?.Invoke(asset.Name + " verified."); return target;
        } finally { if (partial != null) File.Delete(partial); Gate.Release(); }
    }
    internal static async Task CopyBoundedAsync(Stream input, Stream output, long limit, Action<string>? progress, string name, CancellationToken token)
    {
        byte[] buffer = new byte[81920]; long total = 0, reported = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0) {
            total += count; if (total > limit) throw new IOException("Dependency size limit exceeded.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            if (total - reported >= 1024 * 1024) { progress?.Invoke($"Downloading {name}: {total / 1024 / 1024} MiB"); reported = total; }
        }
    }
    internal static void ExtractVerified(Stream archive, string hash, string destination)
    {
        Verify(archive, hash);
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, true);
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        long size = 0; var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Validate every entry before creating any files, including unused entries.
        foreach (var entry in zip.Entries) {
            string name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(p => p is ".." or "." || p.EndsWith(' ') || p.EndsWith('.')) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new IOException("Unsafe ZIP entry.");
            string path = Path.GetFullPath(Path.Combine(destination, name));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !paths.Add(path) || (size += entry.Length) > 512L * 1024 * 1024 || zip.Entries.Count > 4096)
                throw new IOException("Unsafe ZIP path or size.");
        }
        foreach (var entry in zip.Entries) {
            string path = Path.Combine(destination, entry.FullName.Replace('\\', '/'));
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open(); using var output = new FileStream(path, FileMode.CreateNew);
            input.CopyTo(output);
        }
    }
}
