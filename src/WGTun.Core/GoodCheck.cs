using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace WGTun.Core;

public sealed record GoodCheckRequest(string[] Hosts, string Engine = "byedpi");
public sealed record GoodCheckHostResult(string Host, string Stage, bool Success, int? StatusCode, long ElapsedMs, string Outcome);
public sealed record GoodCheckScore(string PresetId, string Name, int Successes, int Total, string Engine = "byedpi", GoodCheckHostResult[]? Hosts = null, string Message = "");
public sealed record GoodCheckResult(bool Success, string Code, string Message, GoodCheckScore[] Results);

public sealed partial class AppBackend
{
    private static string GoodCheckRoot => Path.Combine(BrokerData, "goodcheck");
    private static volatile string GoodCheckProgress = "GoodCheck scan in progress.";
    public async Task<OperationResult> CancelGoodCheckAsync() {
        var reply = await BrokerCallAsync(new("goodcheck-cancel")); return new(reply.Success, reply.Message);
    }
    public async Task<GoodCheckResult> ScanGoodCheckAsync(GoodCheckRequest request) {
        var hosts = ValidateGoodCheckHosts(request.Hosts);
        ValidateGoodCheckEngine(request.Engine);
        var reply = await BrokerCallAsync(new("goodcheck-start", Settings: new UserSettings { Dpi = new DpiSettings { Engine = request.Engine } }, Hosts: hosts));
        if (!reply.Success) return reply.GoodCheck ?? new(false, "service-update", reply.Message, []);
        while (true) {
            await Task.Delay(400);
            reply = await BrokerCallAsync(new("goodcheck-state"));
            if (!reply.Success || reply.GoodCheck == null) return new(false, "service-update", reply.Message, []);
            if (reply.GoodCheck.Code != "running") return reply.GoodCheck;
            ConnectionProgress?.Invoke(reply.GoodCheck.Message);
        }
    }
    public static string[] ValidateGoodCheckHosts(string[]? hosts)
    {
        if (hosts == null || hosts.Length is < 1 or > 8) throw new ArgumentException("Enter 1 to 8 public hostnames.");
        return hosts.Select(host => {
            host = host.Trim().ToLowerInvariant();
            if (host.Length > 253 || !host.Contains('.') || Uri.CheckHostName(host) != UriHostNameType.Dns
                || host.EndsWith(".local") || host.EndsWith(".localhost") || host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')))
                throw new ArgumentException("Use public hostnames without URLs, paths or ports.");
            return host;
        }).Distinct().ToArray();
    }
    public static GoodCheckScore[] ParseGoodCheckResults(string log, int expectedTotal)
    {
        if (log.Length > 16 * 1024 * 1024 || expectedTotal is < 1 or > 8) throw new IOException("Invalid GoodCheck result size.");
        int index = -1;
        var scores = new Dictionary<int, int>();
        foreach (string line in log.Split('\n')) {
            var launch = Regex.Match(line, @"Launching 'ByeDPI', strategy (\d+)/(\d+):", RegexOptions.CultureInvariant);
            if (launch.Success) {
                int count = int.Parse(launch.Groups[2].Value);
                index = int.Parse(launch.Groups[1].Value) - 1;
                if (count != ByeDpiStrategies.All.Count || index < 0 || index >= count) throw new IOException("Unexpected GoodCheck strategy list.");
            }
            var score = Regex.Match(line, @"^.*Successes: (\d+)/(\d+)\s*$", RegexOptions.CultureInvariant);
            if (!score.Success || index < 0) continue;
            int successes = int.Parse(score.Groups[1].Value), total = int.Parse(score.Groups[2].Value);
            if (total != expectedTotal || successes < 0 || successes > total) throw new IOException("Unexpected GoodCheck test count.");
            scores[index] = scores.TryGetValue(index, out int prior) ? Math.Min(prior, successes) : successes;
        }
        if (!log.Contains("All Done") || scores.Count != ByeDpiStrategies.All.Count) throw new IOException("GoodCheck scan did not complete.");
        return scores.OrderByDescending(p => p.Value).ThenBy(p => p.Key).Select(p =>
            new GoodCheckScore(ByeDpiStrategies.All[p.Key].Id, ByeDpiStrategies.All[p.Key].Name, p.Value, expectedTotal)).ToArray();
    }
    public static string ValidateGoodCheckEngine(string engine) => engine is "byedpi" or "zapret" ? engine : throw new ArgumentException("Unknown scan engine.");
    private static async Task<GoodCheckResult> ScanGoodCheckCoreAsync(string[] hosts, string engine, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromMinutes(12));
        var token = timeout.Token;
        var scores = new List<GoodCheckScore>();
        CreatePrivateDirectory(GoodCheckRoot, true);
        string work = Path.Combine(GoodCheckRoot, Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(work, true);
        try {
            var presets = engine == "zapret" ? ZapretStrategies.All : ByeDpiStrategies.All;
            // Explore every fixed candidate, then retest the best four against every host.
            foreach (var preset in presets) {
                token.ThrowIfCancellationRequested();
                GoodCheckProgress = $"{engine}: explore {scores.Count + 1}/{presets.Count}";
                try { scores.Add(await CheckPresetAsync(preset, hosts, "explore", work, token)); }
                catch (IOException) { scores.Add(new(preset.Id, preset.Name, 0, hosts.Length, engine, hosts.Select(h => new GoodCheckHostResult(h, "explore", false, null, 0, "engine-start-or-network-failed")).ToArray(), "Engine did not stay ready; candidate was not verified.")); }
            }
            var finalists = scores.Where(s => s.Successes > 0).OrderByDescending(s => s.Successes).ThenBy(s => s.Hosts!.Sum(h => h.ElapsedMs)).Take(4).ToArray();
            foreach (var score in finalists) {
                token.ThrowIfCancellationRequested();
                GoodCheckProgress = $"{engine}: confirm {score.Name}";
                var repeated = await CheckPresetAsync(presets.Single(p => p.Id == score.PresetId), hosts, "confirm", work, token);
                scores[scores.IndexOf(score)] = score with { Successes = score.Successes + repeated.Successes, Total = score.Total + repeated.Total, Hosts = [.. score.Hosts!, .. repeated.Hosts!], Message = "Exploration and confirmation; HTTPS TCP only." };
            }
            return new(scores.Any(s => s.Successes > 0), "complete", "HTTPS TCP scan completed. DNS, QUIC, UDP and ISP-wide reachability are not certified.", scores.OrderByDescending(s => (double)s.Successes / s.Total).ThenByDescending(s => s.Total).ToArray());
        } catch (OperationCanceledException) {
            return new(false, stop.IsCancellationRequested ? "cancelled" : "timeout", "Scan stopped; partial results retained.", scores.ToArray());
        } catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) {
            return new(false, "error", "Scan engine could not start or lost its network. Repair the selected component and retry.", scores.ToArray());
        } finally { try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private static async Task<GoodCheckScore> CheckPresetAsync(DpiPreset preset, string[] hosts, string stage, string work, CancellationToken token)
    {
        await using var session = await GoodCheckSession.StartAsync(preset, work, token);
        using var handler = new SocketsHttpHandler {
            Proxy = new WebProxy($"socks5://127.0.0.1:{session.Port}"), UseProxy = true,
            AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(4), UseCookies = false
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var results = new List<GoodCheckHostResult>();
        foreach (string host in hosts) {
            token.ThrowIfCancellationRequested();
            session.EnsureRunning();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(6));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://" + host + "/") { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                int status = (int)response.StatusCode;
                // A TLS handshake alone does not establish useful access (403/451 may be blocks).
                bool success = status is >= 200 and < 400;
                results.Add(new(host, stage, success, status, clock.ElapsedMilliseconds, success ? "https-response" : "http-error"));
            } catch (OperationCanceledException) when (!token.IsCancellationRequested) {
                results.Add(new(host, stage, false, null, clock.ElapsedMilliseconds, "timeout"));
            } catch (HttpRequestException) {
                results.Add(new(host, stage, false, null, clock.ElapsedMilliseconds, "dns-connect-or-tls-failed"));
            }
        }
        return new(preset.Id, preset.Name, results.Count(r => r.Success), results.Count, preset.Engine, results.ToArray(), "HTTPS TCP only; certificate validation enabled.");
    }
}
