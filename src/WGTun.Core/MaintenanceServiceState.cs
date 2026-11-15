namespace WGTun.Core;
public sealed partial class AppBackend
{
    private static async Task StopBrokerForMaintenanceAsync()
    {
        // SCM can reject a stop while startup/recovery is still pending. Retry the
        // transition rather than treating that transient state as a broken install.
        for (int attempt = 0; attempt < 100; attempt++) {
            uint? state = ReadService(BrokerName);
            if (state == null || state == 1) return;
            if (state is 4 or 7) {
                var stopped = await RunAsync(ScExe, "stop", BrokerName);
                if (stopped.Code is not (0 or 1061 or 1062)) throw new IOException("Service stop failed: " + stopped.Code);
            }
            await Task.Delay(200);
        }
        throw new IOException("Service did not finish stopping; installation files preserved.");
    }
}
