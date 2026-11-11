using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCAutobackup.Installer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // A second launch while the first is still unpacking must not copy into the same installation directory concurrently.
        using var single = new Mutex(false, "Local\\PCAutobackup-Setup");
        try { if (!single.WaitOne(0)) return 1; }
        catch (AbandonedMutexException) { }
        try { return Run(args); }
        finally { single.ReleaseMutex(); }
    }

    private static int Run(string[] args)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var silent = args.Contains("--silent");
        var status = new TextBlock { Text = "กำลังติดตั้ง PC AutoBackup… / Installing…", TextWrapping = TextWrapping.Wrap, FontSize = 16, Margin = new Thickness(0, 0, 0, 20) };
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(status);
        panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 6 });
        var window = new Window { Title = "PC AutoBackup Setup", Width = 480, Height = 180, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = panel, Background = new SolidColorBrush(Color.FromRgb(245, 247, 246)), Icon = new BitmapImage(new Uri("pack://application:,,,/app.ico")) };
        var finished = false;
        window.Closing += (_, e) => { if (!finished) e.Cancel = true; };
        application.Startup += async (_, _) =>
        {
            if (!silent) window.Show();
            var exitCode = 0;
            try
            {
                await Task.Run(Install);
                if (!args.Contains("--no-launch"))
                {
                    var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PCAutobackup", "PCAutobackup.exe");
                    using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)! });
                }
            }
            catch (Exception error)
            {
                exitCode = 1;
                if (!silent) MessageBox.Show(window, "ติดตั้งไม่สำเร็จ กรุณาปิดหน้าตั้งค่าและรอให้งานสำรองเสร็จก่อนลองใหม่\nInstallation failed. Close settings and wait for backups to finish, then retry.\n\n" + error.Message, "PC AutoBackup Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { finished = true; application.Shutdown(exitCode); }
        };
        return application.Run();
    }

    private static void Install()
    {
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var staging = Path.GetFullPath(Path.Combine(temporaryRoot, "PCAutobackup-Setup-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(staging);
        try
        {
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("PCAutobackup.Payload.zip") ?? throw new IOException("Installation payload is missing."))
            using (var archive = new ZipArchive(resource, ZipArchiveMode.Read)) archive.ExtractToDirectory(staging);
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(staging, "install.ps1"), "-NoLaunch" }) info.ArgumentList.Add(argument);
            using var installer = Process.Start(info) ?? throw new IOException("Cannot start the installation helper.");
            var output = installer.StandardOutput.ReadToEndAsync();
            var error = installer.StandardError.ReadToEndAsync();
            installer.WaitForExit();
            Task.WaitAll(output, error);
            if (installer.ExitCode != 0) throw new IOException("Installation helper returned " + installer.ExitCode + ". " + error.Result);
        }
        finally
        {
            // Delete only this invocation's generated extraction directory, never an installation/data directory.
            if (staging.StartsWith(Path.TrimEndingDirectorySeparator(temporaryRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(staging).StartsWith("PCAutobackup-Setup-", StringComparison.Ordinal))
            {
                try { Directory.Delete(staging, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
