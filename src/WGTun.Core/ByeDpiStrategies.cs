namespace WGTun.Core;

public static class ByeDpiStrategies
{
    // Fixed arguments on both sides of IPC. A scan result never supplies executable paths,
    // listener options or arbitrary arguments to the elevated broker.
    public static IReadOnlyList<DpiPreset> All { get; } = [
        Preset("byedpi-standard", "Standard", "--tlsrec", "1"),
        Preset("goodcheck-tlsrec-1", "TLS record / 1", "--tlsrec", "1"),
        Preset("goodcheck-split-sni", "Split / SNI", "--split", "1+s"),
        Preset("goodcheck-split-1", "Split / 1", "--split", "1"),
        Preset("goodcheck-disorder-sni", "Disorder / SNI", "--disorder", "1+s"),
        Preset("goodcheck-disorder-1", "Disorder / 1", "--disorder", "1"),
        Preset("goodcheck-oob-sni", "OOB / SNI", "--oob", "1+s"),
        Preset("goodcheck-disoob-sni", "Disorder + OOB / SNI", "--disoob", "1+s"),
        Preset("goodcheck-tlsrec-split", "TLS record + split", "--tlsrec", "1+s", "--split", "2+s"),
        Preset("goodcheck-tlsrec-disorder", "TLS record + disorder", "--tlsrec", "1+s", "--disorder", "2+s"),
        Preset("byedpi-windows-disorder", "Windows split + disorder", "--split", "1+s", "--disorder", "3+s"),
        Preset("byedpi-windows-fake", "Windows disorder + fake", "--disorder", "1", "--fake", "-1"),
        new("tr-tt-windows", "byedpi", "TR / T?rk Telekom (Windows candidate)", ["--proto", "tls", "--split", "1", "--disorder", "1", "--fake", "-1", "--auto", "torst,redirect,ssl_err", "--timeout", "2"], "elrondforwin/byedpi-turkey", "MIT; profiles/turk-telekom.conf adapted: unsupported Windows md5sig omitted. Not ISP-verified here."),
        new("tr-sol-default", "byedpi", "TR / Superonline + T?rk Telekom (TTL 3 candidate)", ["--oob", "1", "--disorder", "3+s", "--split", "3", "--ttl", "3"], "elrondforwin/byedpi-turkey", "MIT; varsayilan-profil.conf. Low fake TTL is network-dependent; not ISP-verified here."),
        new("tr-sol-alternative", "byedpi", "TR / Superonline + T?rk Telekom (OOB candidate)", ["--oob", "1", "--auto", "torst,redirect,ssl_err"], "elrondforwin/byedpi-turkey", "MIT; varsayilan2.conf. Upstream reports both ISPs; validate on your connection.")
    ];
    private static DpiPreset Preset(string id, string name, params string[] args) =>
        new(id, "byedpi", name, args, "GoodCheckGoGo / ByeDPI", "HTTPS TCP strategy; results depend on the network and tested hosts.");
    public static string[] Resolve(DpiSettings settings)
    {
        if (settings == null || !string.Equals(settings.Engine, "byedpi", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(settings.CustomArguments)) throw new InvalidOperationException("Invalid ByeDPI configuration.");
        var preset = All.FirstOrDefault(p => p.Id == settings.PresetId)
            ?? throw new InvalidOperationException("Unknown ByeDPI strategy. Update or repair the WGTun service.");
        return [.. preset.Arguments];
    }
}
