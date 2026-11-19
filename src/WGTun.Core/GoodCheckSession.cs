using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using WGTun.Core.Experimental;

namespace WGTun.Core;

// Scan-only SOCKS endpoint: no TUN, routes, proxy registry or global process cleanup.
internal sealed class GoodCheckSession : IAsyncDisposable
{
    private readonly SingBoxJob job = new();
    private Process? child;
    private ZapretRelay? relay;
    private ZapretEgress? egress;
    public int Port { get; private set; }

    public static async Task<GoodCheckSession> StartAsync(DpiPreset preset, string work, CancellationToken token)
    {
        var session = new GoodCheckSession();
        try {
            SingBoxDependencies.ValidateComponent(SingBoxDependencies.Root, preset.Engine);
            if (preset.Engine == "zapret") {
                session.egress = ZapretEgress.Detect();
                session.relay = new ZapretRelay(session.egress);
                session.Port = session.relay.Port;
                string filter = ZapretStrategies.BuildFilter(session.egress.Index, [session.relay.TcpPorts, session.relay.UdpPorts], new() { Engine = "zapret", PresetId = preset.Id });
                session.child = session.job.Start(SingBoxDependencies.ZapretExe,
                    ["--wf-raw=" + filter, .. ZapretStrategies.Resolve(new() { Engine = "zapret", PresetId = preset.Id })], work);
                await Task.Delay(750, token);
            } else {
                var reservation = new TcpListener(IPAddress.Loopback, 0);
                reservation.Start(); session.Port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
                session.child = session.job.Start(SingBoxDependencies.ByeDpiExe,
                    ["--ip", "127.0.0.1", "--port", session.Port.ToString(), .. ByeDpiStrategies.Resolve(new() { PresetId = preset.Id })], work);
                bool ready = false;
                for (int i = 0; i < 30 && !ready; i++) {
                    token.ThrowIfCancellationRequested(); session.EnsureRunning();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(250);
                    try {
                        using var client = new TcpClient();
                        await client.ConnectAsync(IPAddress.Loopback, session.Port, timeout.Token);
                        var stream = client.GetStream();
                        await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
                        byte[] response = new byte[2]; await stream.ReadExactlyAsync(response, timeout.Token);
                        ready = response[0] == 5 && response[1] == 0;
                    } catch (Exception e) when (e is SocketException or IOException or OperationCanceledException) { token.ThrowIfCancellationRequested(); }
                    if (!ready) await Task.Delay(100, token);
                }
                if (!ready) throw new IOException("Scan SOCKS endpoint not ready.");
            }
            session.EnsureRunning();
            return session;
        } catch { await session.DisposeAsync(); throw; }
    }

    public void EnsureRunning()
    {
        if (child is not { HasExited: false } || relay is { IsRunning: false }) throw new IOException("Scan engine stopped.");
        if (egress != null && ZapretEgress.Detect() != egress) throw new IOException("Scan network changed.");
    }

    public async ValueTask DisposeAsync()
    {
        job.Dispose();
        if (child != null) { await child.WaitForExitAsync(); child.Dispose(); }
        if (relay != null) await relay.DisposeAsync();
    }
}
