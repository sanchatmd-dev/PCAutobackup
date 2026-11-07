using System.IO;
using System.Windows;
using PCAutobackup.Core;

namespace PCAutobackup.Desktop;

public partial class App : Application
{
    private AppInstance? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? configurationPath = null;
        if (e.Args.Length != 0)
        {
            if (e.Args.Length != 2 || e.Args[0] != "--config" || !Path.IsPathFullyQualified(e.Args[1]))
            {
                MessageBox.Show("ใช้รูปแบบ PCAutobackup.exe --config <absolute-path> หรือเปิดแอปโดยไม่ระบุพารามิเตอร์", "พารามิเตอร์ไม่ถูกต้อง", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(2);
                return;
            }
            configurationPath = e.Args[1];
        }
        configurationPath = DesktopRuntime.ConfigurationPath(configurationPath);
        _instance = new AppInstance("desktop", configurationPath);
        if (!_instance.IsPrimary)
        {
            _instance.ActivatePrimary();
            Shutdown();
            return;
        }
        DesktopRuntime.EnsureTray(AppContext.BaseDirectory, configurationPath);
        _instance.Listen(() => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowSettings()));
        MainWindow = new MainWindow(configurationPath);
        MainWindow.Show();
        _ = DesktopRuntime.StartPendingBackupAsync(AppContext.BaseDirectory, configurationPath);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
