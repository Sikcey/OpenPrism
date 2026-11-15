using System.Security.AccessControl;
using System.Security.Principal;

namespace WGTun.Core;

public sealed partial class AppBackend
{
    private static bool SkipBrokerPayload(string relative) =>
        relative.Replace('\\', '/').StartsWith("dependencies/", StringComparison.OrdinalIgnoreCase) ||
        relative.Replace('\\', '/').StartsWith("data/", StringComparison.OrdinalIgnoreCase) ||
        relative.Equals("owner.txt", StringComparison.OrdinalIgnoreCase) || relative.Equals("payload-inventory.json", StringComparison.OrdinalIgnoreCase);

    private static void ValidateBrokerSettings(UserSettings settings)
    {
        if (!Enum.IsDefined(settings.Profile) || !Enum.IsDefined(settings.Scope)) throw new IOException("Invalid connection mode.");
        if (settings.Rules == null || settings.Rules.Count > 256 || settings.Applications == null || settings.Applications.Count > 512)
            throw new IOException("Settings exceed the supported limits.");
        foreach (var rule in settings.Rules) {
            if (rule == null || rule.Value == null || rule.Value.Length > 4096) throw new IOException("Invalid routing rule.");
            if (rule.Enabled && RulePolicy.Validate(rule) is string error) throw new IOException(error);
        }
        if (settings.ActiveDpi == null || settings.ActiveDpi.CustomArguments == null || settings.ActiveDpi.CustomArguments.Length > 4096 ||
            settings.ActiveDpi.Dns == null || settings.ActiveDpi.Dns.Length > 256) throw new IOException("Invalid ByeDPI settings.");
    }

    private static void ValidateProtectedEngine()
    {
        VerifyNoReparse(BrokerRoot);
        CreatePrivateDirectory(BrokerRoot, true);
        SetProtectedDirectoryOwner(BrokerRoot);
        // A repair also removes explicit or inherited user write grants on old files.
        foreach (var directory in Directory.EnumerateDirectories(BrokerRoot, "*", SearchOption.AllDirectories)) {
            CreatePrivateDirectory(directory, true);
            SetProtectedDirectoryOwner(directory);
        }
        foreach (var file in Directory.EnumerateFiles(BrokerRoot, "*", SearchOption.AllDirectories)) {
            var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false);
            acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(file).SetAccessControl(acl);
        }
    }

    private static void SetProtectedDirectoryOwner(string path)
    {
        var directory = new DirectoryInfo(path);
        var acl = directory.GetAccessControl();
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        directory.SetAccessControl(acl);
    }
}
