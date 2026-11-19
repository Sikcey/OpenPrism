using System.Diagnostics;
using System.Text;
namespace WGTun.Core;

// A broker-owned packet session. Only selected packets reach embedded sing-tun.
public sealed class SingBoxSession : IAsyncDisposable
{
    private readonly SingBoxJob job = new();
    private readonly List<Process> children = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<string> files = [];
    private Task? heartbeat;
    private Process? capture;
    private ZapretRelay? zapretRelay;
    private string? leasePath, readyPath;
    private bool started;
    private int disposed;
    private volatile bool needsRebind;
    public bool NeedsRebind => needsRebind;
    internal void RequestRebind() => needsRebind = true;
    private void NetworkChanged(object? sender, EventArgs args) => needsRebind = true;
    public bool IsRunning => disposed == 0 && children.Count > 0 && children.All(p => !p.HasExited) && !(heartbeat?.IsFaulted ?? false) && (zapretRelay?.IsRunning ?? true) && (!started || File.Exists(readyPath));
    internal bool IsChildRunning(string name) => children.Any(p => !p.HasExited && p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase));
    internal async Task CrashCaptureForCheckAsync() { if (capture != null) { capture.Kill(true); await capture.WaitForExitAsync(); } }

    public static async Task<SingBoxSession> StartAsync(string wgConfig, UserSettings settings, string workingDirectory, CancellationToken cancellationToken = default)
    {
        CaptureBoundary.EnsureSupported(settings);
        SingBoxDependencies.ValidateComponent(SingBoxDependencies.Root, "selective-tun");
        SingBoxDependencies.ValidateComponent(SingBoxDependencies.Root, "capture");
        if (settings.Profile is ProfileKind.Dpi or ProfileKind.Zapret)
            SingBoxDependencies.ValidateComponent(SingBoxDependencies.Root, settings.Profile == ProfileKind.Dpi ? "byedpi" : "zapret");
        Directory.CreateDirectory(workingDirectory);
        var session = new SingBoxSession();
        try {
            int port = 10809;
            ZapretEgress? egress = null;
            if (settings.Profile == ProfileKind.Zapret) {
                egress = ZapretEgress.Detect();
                session.zapretRelay = new ZapretRelay(egress); port = session.zapretRelay.Port;
                string filter = ZapretStrategies.BuildFilter(egress.Index, [session.zapretRelay.TcpPorts, session.zapretRelay.UdpPorts], settings.ActiveDpi);
                session.children.Add(session.job.Start(SingBoxDependencies.ZapretExe, ["--wf-raw=" + filter, .. ZapretStrategies.Resolve(settings.ActiveDpi)], SingBoxDependencies.Root, Path.Combine(workingDirectory, "zapret.log")));
                await Task.Delay(750, cancellationToken);
                if (!session.IsRunning) throw new IOException("Zapret exited during startup.");
            } else if (settings.Profile == ProfileKind.Dpi) {
                var reservation = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                reservation.Start(); port = ((System.Net.IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
                session.children.Add(session.job.Start(SingBoxDependencies.ByeDpiExe, ["--ip", "127.0.0.1", "--port", port.ToString(), .. ByeDpiStrategies.Resolve(settings.ActiveDpi)], SingBoxDependencies.Root, Path.Combine(workingDirectory, "byedpi.log")));
                await session.WaitForSocksAsync(port, cancellationToken);
            }
            string prefix = Path.Combine(workingDirectory, "tun-" + Guid.NewGuid().ToString("N"));
            string config = prefix + ".json";
            session.leasePath = prefix + ".lease"; session.readyPath = prefix + ".ready";
            session.files.AddRange([config, session.leasePath, session.readyPath, session.leasePath + ".stop"]);
            await File.WriteAllTextAsync(config, AppBackend.BuildSelectiveTunConfiguration(wgConfig, settings, port, egress), new UTF8Encoding(false), cancellationToken);
            await CheckConfigurationAsync(config, cancellationToken);
            await File.WriteAllTextAsync(session.leasePath, "active", cancellationToken);
            session.heartbeat = Task.Run(async () => {
                while (!session.lifetime.IsCancellationRequested) {
                    File.SetLastWriteTimeUtc(session.leasePath, DateTime.UtcNow);
                    await Task.Delay(1000, session.lifetime.Token);
                }
            });
            string[] packetArgs = ["run", "-c", config, "-driver", Path.Combine(SingBoxDependencies.Root, "capture", "WinDivert.dll"), "-lease", session.leasePath, "-ready", session.readyPath, .. settings.Profile is ProfileKind.Dpi or ProfileKind.Zapret ? new[] { "-sniff" } : Array.Empty<string>()];
            session.capture = session.job.Start(SingBoxDependencies.SelectiveTunExe, packetArgs, AppContext.BaseDirectory, Path.Combine(workingDirectory, "engine.log"));
            session.children.Add(session.capture);
            for (int attempt = 0; attempt < 100; attempt++) {
                cancellationToken.ThrowIfCancellationRequested();
                if (session.capture.HasExited) throw new IOException("Selective TUN engine exited: " + string.Join(" ", AppBackend.ReadLogTail(Path.Combine(workingDirectory, "engine.log"), 1800)));
                if (File.Exists(session.readyPath)) { session.started = true; break; }
                await Task.Delay(100, cancellationToken);
            }
            if (!session.started || !session.IsRunning) throw new IOException("Selective TUN engine was not ready within 10 seconds.");
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += session.NetworkChanged;
            return session;
        } catch { await session.DisposeAsync(); throw; }
    }
    public static async Task CheckConfigurationAsync(string path, CancellationToken cancellationToken = default)
    {
        using var job = new SingBoxJob();
        using var process = job.Start(SingBoxDependencies.SelectiveTunExe, ["check", "-c", Path.GetFullPath(path)], AppContext.BaseDirectory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); } catch { job.Dispose(); throw; }
        if (process.ExitCode != 0) throw new IOException("Embedded sing-box rejected selective TUN configuration.");
    }
    private async Task WaitForSocksAsync(int port, CancellationToken token)
    {
        for (int i = 0; i < 30; i++) {
            if (!IsRunning) throw new IOException("Engine exited during startup.");
            try {
                using var client = new System.Net.Sockets.TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(250);
                await client.ConnectAsync("127.0.0.1", port, timeout.Token);
                var stream = client.GetStream(); await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
                byte[] reply = new byte[2]; await stream.ReadExactlyAsync(reply, timeout.Token);
                if (reply[0] == 5 && reply[1] == 0) return;
            } catch (Exception e) when (e is System.Net.Sockets.SocketException or OperationCanceledException or IOException) { token.ThrowIfCancellationRequested(); }
            await Task.Delay(100, token);
        }
        throw new IOException("SOCKS engine did not initialize.");
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= NetworkChanged;
        if (leasePath != null) { try { await File.WriteAllTextAsync(leasePath + ".stop", "stop"); } catch (IOException) { } }
        if (capture != null && !capture.HasExited) {
            try { await capture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { job.Dispose(); }
        }
        await lifetime.CancelAsync();
        if (heartbeat != null) {
            try { await heartbeat; }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        job.Dispose();
        foreach (var process in children) {
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
            process.Dispose();
        }
        if (zapretRelay != null) await zapretRelay.DisposeAsync();
        foreach (string file in files) { try { File.Delete(file); } catch (IOException) { } }
        lifetime.Dispose();
    }
}
