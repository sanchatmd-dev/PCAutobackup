using PCAutobackup.Core;

var arguments = args.ToList();
if (arguments.Contains("--help"))
{
    Console.WriteLine("PCAutobackup.Worker [--config <path>] [--scheduled]\nRuns one backup, records status and exits. Configure sources, destination and recovery key using Desktop first.");
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
    var result = arguments.Contains("--scheduled")
        ? await application.BackupScheduledAsync(cancellation.Token)
        : await application.BackupAsync(ct: cancellation.Token);
    if (result is null) return 0;
    Console.WriteLine($"Backup complete: {result.FileCount} files, {result.TotalBytes} bytes. Snapshot {result.SnapshotId}");
    foreach (var warning in result.Warnings) Console.WriteLine("Warning: " + warning);
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Backup cancelled; previous successful snapshot retained."); return 3; }
catch (Exception e) { Console.Error.WriteLine(ApplicationService.SafeError(e)); return 1; }
