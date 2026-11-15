using System.Security.Cryptography;
namespace WGTun.Core;
public static class SingBoxDependencies
{
    public const string SingBoxVersion = "1.14.4";
    public const string ByeDpiVersion = "0.17.3";
    public static string Root => Path.Combine(AppContext.BaseDirectory, "dependencies");
    public static string SingBoxExe => Path.Combine(Root, "sing-box", "sing-box-1.14.4-windows-amd64", "sing-box.exe");
    public static string ByeDpiExe => Path.Combine(Root, "byedpi", "ciadpi.exe");
    public static string ProxiFyreExe => Path.Combine(Root, "proxifyre", "ProxiFyre.exe");
    public static string ZapretExe => Path.Combine(Root, "zapret", "winws.exe");
    public static void Validate() => Validate(Root);
    internal static void Validate(string root) => ValidateComponent(root, "sing-box");
    internal static void ValidateComponent(string root, string component)
    {
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
        pins["proxifyre/Newtonsoft.Json.dll"] = "E1E27AF7B07EEEDF5CE71A9255F0422816A6FC5849A483C6714E1B472044FA9D";
        pins["proxifyre/NLog.config"] = "59BAB15613E5F02C878C6BD2360F3169D324E888DE0DC4645F7C5960012E0DA0";
        pins["proxifyre/NLog.dll"] = "4B1D3CF9F1F3C4A6EAD141243069162172E9EF48BA1A9BF4F7CCD618B8194B5C";
        pins["proxifyre/ProxiFyre.Configuration.dll"] = "38DB690FA5E2175869FF92373760000247689CD121AC688FD2BED321CF7039F2";
        pins["proxifyre/ProxiFyre.exe"] = "38CF8E77A42D1C323D6A51503EE01E773A5AA9A72D3F578556265806761496F4";
        pins["proxifyre/ProxiFyre.exe.config"] = "FEB7B159597D51A915D659A56599AA74D8B2104056B9E77F7FD9E1992600EEB4";
        pins["proxifyre/ProxiFyreUI.Managed.dll"] = "573D99DBAD9296EB971B91906D78F009EC8E0B7C9E3EBD3E701B6A0A413AD484";
        pins["proxifyre/socksify.dll"] = "97DAB329E12162F879CC7FB2FA0AE830CE183F4CEA1CB4C28A607BAAF1D3ED16";
        pins["proxifyre/Topshelf.dll"] = "BD70A5832124E36840452FF46E442EFA0A09A4CEBA842AEA72C79B2D322D7FE8";

        foreach (var asset in DependencyDownloads.ZapretFiles) pins["zapret/" + asset.Name] = asset.Hash;
        pins["proxifyre/app-config.sample.json"] = "2374FCE043DAB17E6CD813F83653C4363F22C50E2B6054893361AF59953B8AD8";
        pins["proxifyre/Newtonsoft.Json.xml"] = "79EE87D4EDE8783461DE05B93379D576F6E8575D4AB49359F15897A854B643C4";
        pins["proxifyre/NLog.xml"] = "6871374D682E75AFF17DE2A8626A75E9C75409516F5E7527E9D159C1DE6831BB";
        pins["proxifyre/ProxiFyreUI.exe"] = "4EBB890396FA6B9B8993C9247A93982AF9B269D53441356A7A65A6A9FA55C848";
        pins["proxifyre/ProxiFyreUI.exe.config"] = "104FEA6F8C0821CD27E28F0C0CC5F37B7F822168316AA1AE0FDEABBC1E81BF5D";
        pins["proxifyre/Topshelf.xml"] = "3B2228B3333C4FD86E29020BC2D77A5260DBF03E911829D6226498ADE53C2790";
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
