using WGTun.Core.Experimental;

namespace WGTun.Core;

// Arguments are broker-owned; callers select an ID, never a command line.
public static class ZapretStrategies
{
    public static IReadOnlyList<DpiPreset> All { get; } = [
        new("zapret-standard", "zapret", "Multisplit + UDP fake", ["--filter-tcp=80,443", "--dpi-desync=multisplit", "--dpi-desync-split-pos=1", "--new", "--filter-udp=443", "--dpi-desync=fake", "--dpi-desync-repeats=2"], "WGTun", "Local candidate; effectiveness depends on the network and destination."),
        new("zapret-discord-media-stun", "zapret", "Discord media / STUN (candidate)", ["--filter-tcp=80,443", "--dpi-desync=multisplit", "--dpi-desync-split-pos=1", "--new", "--filter-udp=443", "--dpi-desync=fake", "--dpi-desync-repeats=2", "--new", "--filter-udp=19294-19344,50000-50099", "--filter-l7=discord,stun", "--dpi-desync=fake"], "bol-van/zapret; cagritaskn/SplitWire-Turkey", "Optional bounded Discord media/STUN candidate; effectiveness and voice coverage depend on the network."),
        Carrier("zapret-tr-tt", "T?rk Telekom / Kablonet", "--dpi-desync=fake", "--dpi-desync-ttl=4"),
        Carrier("zapret-tr-tt-alt", "T?rk Telekom alternative", "--dpi-desync=fake", "--dpi-desync-ttl=3"),
        Carrier("zapret-tr-sol", "Superonline", "--dpi-desync=fake", "--dpi-desync-fooling=md5sig"),
        Carrier("zapret-tr-sol-alt", "Superonline alternative", "--dpi-desync=fake", "--dpi-desync-fooling=md5sig", "--dpi-desync-ttl=3"),
        Carrier("zapret-tr-turkcell", "Turkcell hotspot", "--dpi-desync=fake", "--dpi-desync-ttl=1", "--dpi-desync-autottl=3"),
        Carrier("zapret-tr-vodafone", "Vodafone hotspot", "--dpi-desync=multisplit", "--dpi-desync-split-pos=2")
    ];

    private static DpiPreset Carrier(string id, string name, params string[] strategy) =>
        new(id, "zapret", "TR / " + name + " (candidate)",
            ["--filter-tcp=80,443", .. strategy, "--new", "--filter-udp=443", "--dpi-desync=fake", "--dpi-desync-repeats=2"],
            "cagritaskn/SplitWire-Turkey", "MIT; legacy winws presets adapted to confined TCP 80/443 and UDP 443. No global capture or STUN ports. Not ISP-verified here.");

    public static string BuildFilter(uint interfaceIndex, IEnumerable<WindowsPortLease> leases, DpiSettings settings)
    {
        ZapretPortRange[] tcp = [new(80, 80), new(443, 443)];
        ZapretPortRange[] udp = settings.PresetId == "zapret-discord-media-stun"
            ? [new(443, 443), new(19294, 19344), new(50000, 50099)] : [new(443, 443)];
        return ZapretFilterBuilder.Build(interfaceIndex, leases, tcp, udp);
    }

    public static string[] Resolve(DpiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!string.IsNullOrWhiteSpace(settings.CustomArguments))
            return DpiCustomArguments.Parse("zapret", settings.CustomArguments);
        // Existing Zapret profiles used the default ByeDPI ID and had no selector.
        string id = settings.PresetId == "byedpi-standard" ? "zapret-standard" : settings.PresetId;
        return (All.FirstOrDefault(p => p.Id == id)
            ?? throw new InvalidOperationException("Unknown Zapret preset.")).Arguments.ToArray();
    }
}
