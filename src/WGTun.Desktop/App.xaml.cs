using System.Threading;
using System.Windows;
namespace WGTun.Desktop;
public partial class App : System.Windows.Application
{
    private Mutex? mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        mutex = new Mutex(true, "Local\\WGTun.Desktop", out bool created);
        if (!created) { System.Windows.MessageBox.Show("WGTun zaten açık. Sistem tepsisini kontrol et.", "WGTun"); Shutdown(); return; }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e) { mutex?.Dispose(); base.OnExit(e); }
}
