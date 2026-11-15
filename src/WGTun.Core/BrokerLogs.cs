using System.Text;
namespace WGTun.Core;
public sealed record LogSnapshot(bool Success, string Message, string[] Lines);
public sealed partial class AppBackend
{
    public async Task<OperationResult> ClearLogsAsync() {
        var result = await BrokerCallAsync(new("clear-logs"));
        return new(result.Success, result.Message);
    }
    private static BrokerReply ClearBrokerLogs() {
        lock (ServiceLogGate) {
            foreach (string name in new[] { "service.log", "engine.log" }) {
                string path = Path.Combine(BrokerData, name);
                if (File.Exists(path)) using (var stream = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
            }
        }
        return new(true, "Kayıtlar temizlendi.");
    }
    private static readonly object ServiceLogGate = new();
    private static void LogService(string message) {
        try {
            lock (ServiceLogGate) {
                string path = Path.Combine(BrokerData, "service.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message[..Math.Min(message.Length, 512)]}\n", Encoding.UTF8);
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    internal static string[] ReadLogTail(string path, int budget = 3800) {
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        bool clipped = stream.Length > 16384;
        if (clipped) stream.Seek(-16384, SeekOrigin.End);
        byte[] buffer = new byte[16384];
        int used = 0, count;
        while (used < buffer.Length && (count = stream.Read(buffer, used, buffer.Length - used)) > 0) used += count;
        string text = Encoding.UTF8.GetString(buffer, 0, used);
        if (clipped) { int newline = text.IndexOf('\n'); text = newline < 0 ? "" : text[(newline + 1)..]; }
        if (text.Length > budget) text = text[^budget..];
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(80).Select(line => line.TrimEnd('\r')).ToArray();
    }
    private static BrokerReply ReadBrokerLogs() {
        var lines = ReadLogTail(Path.Combine(BrokerData, "service.log")).Select(line => "[Hizmet] " + line)
            .Concat(ReadLogTail(Path.Combine(BrokerData, "engine.log")).Select(line => "[Motor] " + line))
            .Concat(ReadLogTail(Path.Combine(BrokerData, "capture.log")).Select(line => "[capture] " + line))
            .Concat(ReadLogTail(Path.Combine(SingBoxDependencies.Root, "proxifyre", "logs", "logfile.txt")).Select(line => "[capture-engine] " + line)).ToArray();
        return new(true, lines.Length == 0 ? "Henüz log yok." : "Son hizmet ve motor kayıtları.", Lines: lines);
    }
}

// Diagnostics deliberately records only fixed event identifiers and exception types;
// exception messages, paths, profiles and engine output may contain secrets.
public static class LifecycleDiagnostics
{
    public static void Record(string directory, string eventName, Exception? error = null)
    {
        if (eventName is not ("setup-started" or "setup-failed" or "recovery-failed" or "unhandled-error"))
            throw new ArgumentException("Unknown diagnostic event.", nameof(eventName));
        try {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "lifecycle.log");
            string type = error == null ? "none" : error.GetType().Name;
            if (type.Length > 80 || type.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) type = "Exception";
            lock (Gate) {
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {eventName} {type}\n", Encoding.UTF8);
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    private static readonly object Gate = new();
}
