using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Text.Json;
namespace WGTun.Core;

public static class SignedPackage
{
    // Set by a release build. Development binaries cannot apply an unsigned update.
    public static string PublisherThumbprint { get; } = typeof(SignedPackage).Assembly
        .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "ReleasePublisherThumbprint")?.Value ?? "";

    public static void Verify(string directory, string expectedThumbprint)
    {
        if (string.IsNullOrWhiteSpace(expectedThumbprint)) throw new InvalidOperationException("This build has no trusted release publisher. Automatic updates are disabled.");
        directory = Path.GetFullPath(directory);
        NoLinks(directory);
        byte[] manifest = File.ReadAllBytes(Path.Combine(directory, "package-manifest.json"));
        if (manifest.Length > 1024 * 1024) throw new IOException("Package manifest too large.");
        var cms = new SignedCms(new ContentInfo(manifest), true);
        cms.Decode(File.ReadAllBytes(Path.Combine(directory, "package-manifest.p7s")));
        cms.CheckSignature(false);
        if (cms.SignerInfos.Count != 1 || !string.Equals(cms.SignerInfos[0].Certificate?.Thumbprint, expectedThumbprint, StringComparison.OrdinalIgnoreCase)) throw new IOException("Untrusted package publisher.");
        ReadOnlyMemory<byte> body = manifest;
        if (body.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) body = body[3..];
        using var json = JsonDocument.Parse(body);
        if (json.RootElement.GetProperty("product").GetString() != "OpenPrism") throw new IOException("Wrong product.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in json.RootElement.GetProperty("files").EnumerateArray()) {
            string relative = file.GetProperty("path").GetString() ?? "";
            if (relative.Contains(':') || relative.Replace('\\', '/').Split('/').Any(segment => segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' '))) throw new IOException("Unsafe package path.");
            string path = Path.GetFullPath(Path.Combine(directory, relative));
            if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !paths.Add(relative) || Path.IsPathFullyQualified(relative)) throw new IOException("Unsafe package path.");
            NoLinks(path);
            using var input = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(input)).Equals(file.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw new IOException("Package file hash mismatch.");
        }
        if (!paths.Contains("WGTun.exe") || !paths.Contains("backend/WGTun.Service.exe") || !paths.Contains("backend/WGTun.Core.dll") || !paths.Contains("backend/WGTun.Tun.exe") || !paths.Contains("install-openprism.ps1") || !paths.Contains("uninstall-openprism.ps1")) throw new IOException("A paired app/service package is required.");
    }

    private static void NoLinks(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory != null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked package paths are not supported.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked package files are not supported.");
    }
}
