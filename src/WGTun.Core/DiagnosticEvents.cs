using System.Globalization;
namespace WGTun.Core;
public sealed record DiagnosticEvent(DateTimeOffset Utc, string Event, string ErrorType);
public sealed partial class AppBackend
{
    internal static DiagnosticEvent[] ReadDiagnosticEvents(string path)
    {
        var result = new List<DiagnosticEvent>();
        foreach (string line in ReadLogTail(path, 8000)) {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var utc)) continue;
            if (parts[1] is not ("setup-started" or "setup-failed" or "recovery-failed" or "unhandled-error")) continue;
            if (parts[2].Length > 80 || parts[2].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) continue;
            if (parts[2] != "none" && !parts[2].EndsWith("Exception", StringComparison.Ordinal)) continue;
            result.Add(new(utc, parts[1], parts[2]));
        }
        return result.TakeLast(80).ToArray();
    }
}
