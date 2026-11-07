using System.IO;
using System.Windows;

namespace PCAutobackup.Desktop;

public partial class App : Application
{
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
        MainWindow = new MainWindow(configurationPath);
        MainWindow.Show();
    }
}
