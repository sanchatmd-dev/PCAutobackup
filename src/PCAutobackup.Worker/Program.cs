using PCAutobackup.Core;

var arguments = args.ToList();
if (arguments.Contains("--help"))
{
    Console.WriteLine("PCAutobackup.Worker [--config <path>] [--scheduled | --refresh-schedule]\nRuns one backup, records status and exits. --refresh-schedule only updates an enabled Windows task to this installed Worker. Configure sources, destination and recovery key using Desktop first.");
    return 0;
}
string? configurationPath = null;
var configIndex = arguments.IndexOf("--config");
if (configIndex >= 0)
{
    if (configIndex + 1 >= arguments.Count) { Console.Error.WriteLine("--config requires a path"); return 2; }
    configurationPath = arguments[configIndex + 1];
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var application = new ApplicationService(configurationPath);
    if (arguments.Contains("--refresh-schedule"))
    {
        var config = await application.LoadConfigurationAsync();
        if (config.ScheduleEnabled) await WindowsScheduler.RegisterAsync(config, DesktopRuntime.ConfigurationPath(configurationPath), cancellation.Token);
        return 0;
    }
    var result = arguments.Contains("--scheduled")
        ? await application.BackupScheduledAsync(cancellation.Token)
        : await application.BackupAsync(ct: cancellation.Token);
    if (result is null)
    {
        if (application.ScheduledSkip is { } skipped) Console.WriteLine(skipped.Message);
        return 0;
    }
    Console.WriteLine($"Backup complete: {result.FileCount} files, {result.TotalBytes} bytes. Snapshot {result.SnapshotId}");
    foreach (var warning in result.Warnings) Console.WriteLine("Warning: " + warning);
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Backup cancelled; previous successful snapshot retained."); return 3; }
catch (Exception e) { Console.Error.WriteLine(ApplicationService.SafeError(e)); return 1; }
