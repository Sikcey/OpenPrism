namespace WGTun.Core;

internal sealed class RecoveryBackoff
{
    private int failures;
    private DateTimeOffset next;
    internal bool Ready(DateTimeOffset now) => now >= next;
    internal void Failed(DateTimeOffset now)
    {
        failures = Math.Min(failures + 1, 6);
        next = now.AddSeconds(Math.Min(30, 1 << (failures - 1)));
    }
    internal void Reset() { failures = 0; next = default; }
}
