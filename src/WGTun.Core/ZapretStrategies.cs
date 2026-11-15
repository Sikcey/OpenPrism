namespace WGTun.Core;

// Arguments are broker-owned; callers select an ID, never a command line.
public static class ZapretStrategies
{
    public static IReadOnlyList<DpiPreset> All { get; } = [
        new("zapret-standard", "zapret", "Multisplit + UDP fake", ["--filter-tcp=80,443", "--dpi-desync=multisplit", "--dpi-desync-split-pos=1", "--new", "--filter-udp=443", "--dpi-desync=fake", "--dpi-desync-repeats=2"], "WGTun", "Local candidate; effectiveness depends on the network and destination."),
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

    public static string[] Resolve(DpiSettings settings)
    {
        if (settings == null || !string.IsNullOrWhiteSpace(settings.CustomArguments))
            throw new InvalidOperationException("Invalid Zapret settings.");
        // Existing Zapret profiles used the default ByeDPI ID and had no selector.
        string id = settings.PresetId == "byedpi-standard" ? "zapret-standard" : settings.PresetId;
        return (All.FirstOrDefault(p => p.Id == id)
            ?? throw new InvalidOperationException("Unknown Zapret preset.")).Arguments.ToArray();
    }
}
