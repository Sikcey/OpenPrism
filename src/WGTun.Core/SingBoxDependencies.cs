using System.Security.Cryptography;
namespace WGTun.Core;
public static class SingBoxDependencies
{
    public const string SingBoxVersion = "1.14.4";
    public const string ByeDpiVersion = "0.17.3";
    public static string Root => Path.Combine(AppContext.BaseDirectory, "dependencies");
    public static string SingBoxExe => Path.Combine(Root, "sing-box", "sing-box-1.14.4-windows-amd64", "sing-box.exe");
    public static string ByeDpiExe => Path.Combine(Root, "byedpi", "ciadpi.exe");
    public static string SelectiveTunExe => Path.Combine(AppContext.BaseDirectory, "WGTun.Tun.exe");
    public static string ZapretExe => Path.Combine(Root, "zapret", "winws.exe");
    public static void Validate() => Validate(Root);
    public static void ValidateSelectiveTun() => ValidateComponent(Root, "selective-tun");
    internal static void Validate(string root) => ValidateComponent(root, "sing-box");
    internal static void ValidateComponent(string root, string component)
    {
        if (component == "selective-tun") {
            string executable = SelectiveTunExe;
            if (!File.Exists(executable) || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0) throw new IOException("Missing or unsafe WGTun.Tun.exe; build a matched application package.");
            using var payload = File.OpenRead(executable);
            if (!Convert.ToHexString(SHA256.HashData(payload)).Equals(TunBuildIdentity.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("WGTun.Tun.exe does not match this backend build.");
            return;
        }
        if (!Environment.Is64BitOperatingSystem || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new IOException("The pinned engines require Windows x64.");
        foreach (var entry in Pins) {
            if (component.Length > 0 && !entry.Key.StartsWith(component + "/", StringComparison.Ordinal)) continue;
            string path = Path.Combine(root, entry.Key);
            for (var dir = new DirectoryInfo(Path.GetDirectoryName(path)!); dir != null; dir = dir.Parent)
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Engine dependencies cannot use reparse points.");
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Missing or unsafe engine dependency: " + entry.Key);
            using var stream = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(entry.Value, StringComparison.OrdinalIgnoreCase)) throw new IOException("Engine SHA256 mismatch: " + entry.Key);
        }
    }
    internal static IEnumerable<string> PinnedRelativePaths => Pins.Keys;
    internal static bool IsPinnedFile(string root, string relative)
    {
        relative = relative.Replace('\\', '/');
        if (!Pins.TryGetValue(relative, out string? expected)) return false;
        string path = Path.Combine(root, relative);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
    private static readonly Dictionary<string,string> Pins = MakePins();
    private static Dictionary<string,string> MakePins()
    {
        var pins = new Dictionary<string,string>(EnginePins);
        foreach (var asset in DependencyDownloads.CaptureFiles) pins["capture/" + asset.Name] = asset.Hash;

        foreach (var asset in DependencyDownloads.ZapretFiles) pins["zapret/" + asset.Name] = asset.Hash;
        return pins;
    }
    private static Dictionary<string,string> EnginePins => new()
    {
        // Filled from the downloaded, upstream-digest-verified release assets.
        ["sing-box/sing-box-1.14.4-windows-amd64/sing-box.exe"] = "8F30682EE198855440D2A7FE070D5D3653793B2D01BB8C85EEA88F93BBE7C8B9",
        ["sing-box/sing-box-1.14.4-windows-amd64/libcronet.dll"] = "3217C6260FBCA5F16072E0B79735742F40109A63BB0FF88FD6B96DD6B54A2928",
        ["goodcheck/goodcheckgogo.exe"] = "1A1A8DC7A33210377D972335AF2E7CBC349A94045D59E6A4D04AE5590D2ABB6A",
        ["byedpi/ciadpi.exe"] = "EB53CEEEB981CC6735AC24BB1E51E725280B86630E80FDF19DDC4EE4A5B54EF4"
    };
}
