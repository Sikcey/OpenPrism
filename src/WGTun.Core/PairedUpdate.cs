using System.Diagnostics;
using Microsoft.Win32;
namespace WGTun.Core;
public sealed partial class AppBackend
{
    public async Task<OperationResult> ApplySignedUpdateAsync(string packageDirectory)
    {
        try {
            SignedPackage.Verify(packageDirectory, SignedPackage.PublisherThumbprint);
            using var registration = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\OpenPrism");
            if (registration?.GetValue("InstallLocation") is not string installed) return new(false, "Install OpenPrism before applying a paired update.");
            string script = Path.Combine(Path.GetFullPath(packageDirectory), "install-openprism.ps1");
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-PackageDirectory", Path.GetFullPath(packageDirectory), "-InstallDirectory", installed }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Could not start the paired updater.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(timeout.Token);
            return new(process.ExitCode == 0, process.ExitCode == 0 ? "Paired update installed. Restart OpenPrism." : "Paired update failed; inspect the retained installation.");
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException or OperationCanceledException) {
            return new(false, e is InvalidOperationException ? e.Message : "The signed update could not be verified or applied. No unverified package was executed.");
        }
    }
}
