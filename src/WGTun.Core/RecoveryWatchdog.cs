namespace WGTun.Core;
internal sealed class RecoveryWatchdog
{
    private long? last;
    internal bool Observe(long monotonicMilliseconds)
    {
        bool interrupted = last.HasValue && monotonicMilliseconds - last.Value > 20_000;
        last = monotonicMilliseconds;
        return interrupted;
    }
    internal void Reset(long monotonicMilliseconds) => last = monotonicMilliseconds;
}
