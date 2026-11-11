using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace PCAutobackup.Core;

public static class DesktopRuntime
{
    public static string ConfigurationPath(string? path) => Path.GetFullPath(path ?? AppPaths.ConfigurationFile);

    /// <param name="skipLaunchCheck">True when the caller checks for a pending round itself, so the tray does not start a second Worker for it.</param>
    public static void EnsureTray(string applicationDirectory, string configurationPath, bool skipLaunchCheck = false)
    {
        var executable = Path.Combine(applicationDirectory, "Tray", "PCAutobackup.Tray.exe");
        if (!File.Exists(executable)) return; // source-only development builds may not have a packaged tray
        string[] arguments = skipLaunchCheck ? ["--background", "--skip-launch-check", "--config", configurationPath] : ["--background", "--config", configurationPath];
        // Settings and recovery must still open when a damaged or blocked tray cannot start.
        try { using var process = Start(executable, arguments); }
        catch (Win32Exception) { }
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException or Win32Exception or InvalidDataException)
        {
            // Keep the tray/settings available for repair. The next scheduled trigger can retry.
            return false;
        }
    }
}
