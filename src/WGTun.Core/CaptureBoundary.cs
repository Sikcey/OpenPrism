namespace WGTun.Core;
public static class CaptureBoundary
{
    public static void EnsureSupported(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(settings.Scope) || !Enum.IsDefined(settings.Profile)) throw new ArgumentException("Unknown routing mode.");
        if (settings.Rules.Count > 256) throw new ArgumentException("At most 256 rules are supported.");
        if (settings.Scope != TrafficScope.All && !settings.Rules.Any(r => r.Enabled)
            && !(settings.RuleSchemaVersion == 0 && settings.Applications.Any(a => a.Selected))) throw new ArgumentException("Select at least one application or routing rule.");
        foreach (var rule in settings.Rules.Where(r => r.Enabled)) {
            if (!Enum.IsDefined(rule.Action) || !(rule.Protocol == "TCP + UDP" || rule.Protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase) || rule.Protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Unsupported rule action or protocol.");
            if (RulePolicy.Validate(rule) is string error) throw new ArgumentException(error);
        }
    }
}
