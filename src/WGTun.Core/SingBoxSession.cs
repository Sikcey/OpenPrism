using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
namespace WGTun.Core;

// A session belongs to the service. Closing the job kills both engines, including on service failure.
public sealed class SingBoxSession : IAsyncDisposable
{
    private readonly SingBoxJob job = new();
    private readonly List<Process> children = [];
    private string? configPath;
    private string? captureConfigPath;
    private ZapretRelay? zapretRelay;
    private ZapretEgress? zapretEgress;
    private volatile bool needsRebind;
    public bool NeedsRebind => needsRebind;
    internal void RequestRebind() => needsRebind = true;
    internal async Task CrashCaptureForCheckAsync() {
        var process = children[^1]; process.Kill(); await process.WaitForExitAsync();
    }
    private void NetworkChanged(object? sender, EventArgs args) => needsRebind = true;
    public bool IsRunning => children.Count > 0 && children.All(p => !p.HasExited) && (zapretRelay?.IsRunning ?? true);
    internal bool IsChildRunning(string name) {
        foreach (var process in children) {
            try { if (!process.HasExited && process.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)) return true; }
            catch (InvalidOperationException) { }
        }
        return false;
    }
    public static async Task<SingBoxSession> StartAsync(string wgConfig, UserSettings settings, string workingDirectory, CancellationToken cancellationToken = default)
    {
        CaptureBoundary.EnsureSupported(settings);
        SingBoxDependencies.ValidateComponent(SingBoxDependencies.Root, "proxifyre");
        if (settings.Profile is ProfileKind.Cloudflare or ProfileKind.CustomWireGuard) SingBoxDependencies.Validate();
        else SingBoxDependencies.ValidateComponent(SingBoxDependencies.Root, settings.Profile == ProfileKind.Dpi ? "byedpi" : "zapret");
        using (var driver = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ndisrd"))
            if (driver == null) throw new IOException("Windows Packet Filter driver is required for native application capture. Install the official signed driver first; no TUN fallback is used.");
        Directory.CreateDirectory(workingDirectory);
        var session = new SingBoxSession();
        try {
            var reservation = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            reservation.Start(); int port = ((System.Net.IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            if (settings.Profile == ProfileKind.Zapret) {
                session.zapretEgress = ZapretEgress.Detect();
                session.zapretRelay = new ZapretRelay(session.zapretEgress);
                port = session.zapretRelay.Port;
                string filter = Experimental.ZapretFilterBuilder.Build(session.zapretEgress.Index,
                    [session.zapretRelay.TcpPorts, session.zapretRelay.UdpPorts], 80, 443);
                session.children.Add(session.job.Start(SingBoxDependencies.ZapretExe,
                    ["--wf-raw=" + filter, .. ZapretStrategies.Resolve(settings.ActiveDpi)], workingDirectory));
                await Task.Delay(750, cancellationToken);
                if (!session.IsRunning) throw new IOException("Zapret failed to initialize.");
            } else if (settings.Profile == ProfileKind.Dpi) {
                session.children.Add(session.job.Start(SingBoxDependencies.ByeDpiExe,
                    ["--ip", "127.0.0.1", "--port", port.ToString(), .. ByeDpiStrategies.Resolve(settings.ActiveDpi)], workingDirectory));
                await session.WaitForSocksAsync(port, cancellationToken);
            } else {
                session.configPath = Path.Combine(workingDirectory, "sing-box-" + Guid.NewGuid().ToString("N") + ".json");
                var configuration = System.Text.Json.Nodes.JsonNode.Parse(AppBackend.BuildProcessProxyConfiguration(wgConfig, settings, port))!;
                configuration["log"]!["output"] = Path.Combine(workingDirectory, "engine.log");
                await File.WriteAllTextAsync(session.configPath, configuration.ToJsonString(), new UTF8Encoding(false), cancellationToken);
                await CheckConfigurationAsync(session.configPath, cancellationToken);
                session.children.Add(session.job.Start(SingBoxDependencies.SingBoxExe, ["run", "-c", session.configPath], workingDirectory));
                await session.WaitForSocksAsync(port, cancellationToken);
            }
            session.captureConfigPath = Path.Combine(Path.GetDirectoryName(SingBoxDependencies.ProxiFyreExe)!, "app-config.json");
            await File.WriteAllTextAsync(session.captureConfigPath, ProxiFyreConfiguration.Build(settings, port), new UTF8Encoding(false), cancellationToken);
            string captureLog = Path.Combine(workingDirectory, "capture.log");
            session.children.Add(session.job.Start(SingBoxDependencies.ProxiFyreExe, [], Path.GetDirectoryName(SingBoxDependencies.ProxiFyreExe)!, captureLog));
            bool ready = false;
            for (int attempt = 0; attempt < 40; attempt++) {
                cancellationToken.ThrowIfCancellationRequested();
                if (!session.IsRunning) break;
                if (AppBackend.ReadLogTail(captureLog).Any(line => line.Contains("service is now running", StringComparison.OrdinalIgnoreCase))) { ready = true; break; }
                await Task.Delay(200, cancellationToken);
            }
            if (!ready || !session.IsRunning) throw new IOException("Application capture did not initialize: " + string.Join(" ", AppBackend.ReadLogTail(captureLog, 1400)));
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += session.NetworkChanged;
            return session;
        } catch { await session.DisposeAsync(); throw; }
    }
    public static async Task CheckConfigurationAsync(string path, CancellationToken cancellationToken = default)
    {
        using var job = new SingBoxJob();
        using var process = job.Start(SingBoxDependencies.SingBoxExe, ["check", "-c", Path.GetFullPath(path)], Path.GetDirectoryName(SingBoxDependencies.SingBoxExe)!);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { job.Dispose(); await process.WaitForExitAsync(CancellationToken.None); throw; }
        if (process.ExitCode != 0) throw new IOException("sing-box rejected the configuration (exit " + process.ExitCode + "). Run sing-box check on the generated profile for diagnostics.");
    }
    private async Task WaitForSocksAsync(int port, CancellationToken cancellationToken)
    {
        for (int i = 0; i < 30; i++) {
            if (!IsRunning) throw new IOException("ByeDPI exited during startup.");
            try {
                using var client = new System.Net.Sockets.TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(250);
                await client.ConnectAsync("127.0.0.1", port, timeout.Token);
                var stream = client.GetStream(); await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
                byte[] reply = new byte[2]; await stream.ReadExactlyAsync(reply, timeout.Token);
                if (reply[0] == 5 && reply[1] == 0) return;
                throw new IOException("Invalid SOCKS greeting.");
            } catch (Exception e) when (e is System.Net.Sockets.SocketException or OperationCanceledException or IOException) { cancellationToken.ThrowIfCancellationRequested(); }
            await Task.Delay(100, cancellationToken);
        }
        throw new IOException("ByeDPI loopback SOCKS listener was not ready.");
    }
    public async ValueTask DisposeAsync()
    {
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= NetworkChanged;
        job.Dispose();
        foreach (var process in children) { try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { process.Kill(true); } finally { process.Dispose(); } }
        children.Clear();
        if (zapretRelay != null) { await zapretRelay.DisposeAsync(); zapretRelay = null; }
        if (captureConfigPath != null) { try { File.Delete(captureConfigPath); } catch (IOException) { } captureConfigPath = null; }
        if (configPath != null) { try { File.Delete(configPath); } catch (IOException) { } configPath = null; }
    }
}

internal sealed class SingBoxJob : IDisposable
{
    private IntPtr handle;
    public SingBoxJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) throw new Win32Exception();
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) { Dispose(); throw new Win32Exception(); }
    }
    public Process Start(string executable, string[] args, string directory, string? outputPath = null)
    {
        // CREATE_SUSPENDED closes the Process.Start/AssignProcessToJobObject orphan race.
        using var output = outputPath == null ? null : new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        if (output != null) {
            var outputHandle = output.SafeFileHandle.DangerousGetHandle();
            if (!SetHandleInformation(outputHandle, 1, 1)) throw new Win32Exception();
            startup.Flags = 0x100; startup.Output = outputHandle; startup.Error = outputHandle;
        }
        var command = new StringBuilder(Quote(executable) + " " + string.Join(" ", args.Select(Quote)));
        if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, output != null, 0x08000004, IntPtr.Zero, directory, ref startup, out var info)) throw new Win32Exception();
        try {
            if (output != null) SetHandleInformation(output.SafeFileHandle.DangerousGetHandle(), 1, 0);
            if (!AssignProcessToJobObject(handle, info.Process)) throw new Win32Exception();
            var process = Process.GetProcessById((int)info.ProcessId);
            _ = process.Handle; // Retain a queryable handle before a short-lived child exits.
            if (ResumeThread(info.Thread) == uint.MaxValue) { process.Dispose(); throw new Win32Exception(); }
            return process;
        } catch { TerminateProcess(info.Process, 1); throw; }
        finally { CloseHandle(info.Thread); CloseHandle(info.Process); }
    }
    private static string Quote(string value) => "\"" + System.Text.RegularExpressions.Regex.Replace(value, "(\\\\*)\"", "$1$1\\\"").TrimEnd('\\') + new string('\\', value.Reverse().TakeWhile(c => c == '\\').Count() * 2) + "\"";
    public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long PerProcess, PerJob; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int Size; public string? Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags; public ushort ShowWindow, ReservedSize; public IntPtr ReservedData, Input, Output, Error; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int type, ref ExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
