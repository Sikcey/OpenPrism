using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;

namespace WGTun.Core.Experimental;

// Explicit opt-in prototype, not registered as an application connection mode.
public sealed class WinwsProbeSession : IDisposable
{
    private readonly SingBoxJob job = new();
    private Process? child;
    public bool IsRunning => child is { HasExited: false };
    public static IReadOnlyDictionary<string, string> Pins { get; } = new Dictionary<string, string> {
        ["winws.exe"] = "2DA71E80878DC270AC83F5893ECBB841F9752A57F1DA8FF9325636B4346BC632",
        ["WinDivert.dll"] = "16ABD6A029E65557C6A309BEA7B13BF81FFF4E193567582E1CDDBF6719F323E0",
        ["WinDivert64.sys"] = "8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2",
        ["cygwin1.dll"] = "103104A52E5293CE418944725DF19E2BF81AD9269B9A120D71D39028E821499B"
    };
    public static async Task<WinwsProbeSession> StartAsync(string directory, string filter, string log)
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("The winws prototype requires an elevated test process.");
        directory = Path.GetFullPath(directory);
        foreach (var (name, expected) in Pins) {
            using var stream = File.OpenRead(Path.Combine(directory, name));
            if (Convert.ToHexString(SHA256.HashData(stream)) != expected) throw new IOException("Incomplete or modified Zapret payload: " + name);
        }
        var session = new WinwsProbeSession();
        try {
            session.child = session.job.Start(Path.Combine(directory, "winws.exe"), [
                "--wf-raw=" + filter, "--filter-tcp=443", "--dpi-desync=multisplit", "--dpi-desync-split-pos=1",
                "--debug=@" + Path.GetFullPath(log).Replace('\\', '/')
            ], directory);
            await Task.Delay(1000);
            if (!session.IsRunning) {
                string detail = File.Exists(log) ? File.ReadAllText(log) : "No log.";
                throw new IOException("winws startup failed, exit " + session.child.ExitCode + ". " + detail[..Math.Min(1200, detail.Length)]);
            }
            return session;
        } catch { session.Dispose(); throw; }
    }
    public void Dispose()
    {
        job.Dispose();
        if (child != null) { child.WaitForExit(5000); child.Dispose(); child = null; }
    }
}
