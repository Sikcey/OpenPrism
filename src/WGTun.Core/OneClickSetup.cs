using System.Runtime.InteropServices;
using System.Security.Principal;
namespace WGTun.Core;
public sealed partial class AppBackend
{
    private static string IdentitySid => WindowsIdentity.GetCurrent().User!.Value;
    private static string ScExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
    public static bool HasPreparedWarpProfile => File.Exists(FileAt("warp-profile.dpapi"));
    public static bool HasWarpAccount => File.Exists(FileAt("wgcf-account.dpapi"));
    private static uint? ReadService(string name)
    {
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new IOException("Cannot open Service Control Manager: " + Marshal.GetLastWin32Error());
        try {
            var service = OpenService(manager, name, 4);
            if (service == IntPtr.Zero) { if (Marshal.GetLastWin32Error() == 1060) return null; throw new IOException("Cannot open service: " + name); }
            try { if (!QueryServiceStatus(service, out var state)) throw new IOException("Cannot query service: " + name); return state.State; }
            finally { CloseServiceHandle(service); }
        } finally { CloseServiceHandle(manager); }
    }
    public static bool EngineDependenciesReady()
    {
        try { ValidateEngineDependencies(); return true; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
    public static void ValidateEngineDependencies()
    {
        SingBoxDependencies.Validate();
        SingBoxDependencies.ValidateComponent(SingBoxDependencies.Root, "selective-tun");
        SingBoxDependencies.ValidateComponent(SingBoxDependencies.Root, "capture");
    }
}
