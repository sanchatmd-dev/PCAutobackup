using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace PCAutobackup.Core;

public static class DesktopRuntime
{
    public static string ConfigurationPath(string? path) => Path.GetFullPath(path ?? AppPaths.ConfigurationFile);

    public static void EnsureTray(string applicationDirectory, string configurationPath)
    {
        var executable = Path.Combine(applicationDirectory, "Tray", "PCAutobackup.Tray.exe");
        if (!File.Exists(executable)) return; // source-only development builds may not have a packaged tray
        using var process = Start(executable, "--background", "--config", configurationPath);
    }

    public static Process? Start(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info);
    }

    public static async Task<bool> StartPendingBackupAsync(string applicationDirectory, string configurationPath)
    {
        try
        {
            if (!File.Exists(configurationPath)) return false;
            var app = new ApplicationService(configurationPath);
            var config = await app.LoadConfigurationAsync();
            if (!config.ScheduleEnabled) return false;
            var attempt = await app.GetLastRunAsync();
            if (attempt?.Status == "Running") return false;
            if (!BackupSchedule.ShouldRun(config, DateTimeOffset.UtcNow, TimeZoneInfo.Local, await app.GetLastSuccessAsync(), attempt)) return false;
            var executable = Path.Combine(applicationDirectory, "Worker", "PCAutobackup.Worker.exe");
            if (!File.Exists(executable)) return false;
            using var worker = Start(executable, "--scheduled", "--config", configurationPath);
            return worker is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException or Win32Exception)
        {
            // Keep the tray/settings available for repair. The next scheduled trigger can retry.
            return false;
        }
    }
}
