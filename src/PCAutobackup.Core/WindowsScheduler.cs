using System.Diagnostics;
using System.Security;
using System.Security.Principal;

namespace PCAutobackup.Core;

public static class WindowsScheduler
{
    public static string TaskName(AppConfiguration config) => "PCAutobackup-" + config.DeviceId;

    public static string FindWorker()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Worker", "PCAutobackup.Worker.exe"),
            Path.Combine(AppContext.BaseDirectory, "PCAutobackup.Worker.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "PCAutobackup.Worker", "bin", "Debug", "net10.0-windows", "PCAutobackup.Worker.exe"))
        };
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("ไม่พบตัวสำรองเบื้องหลัง กรุณาใช้ชุดแอปที่สร้างด้วย scripts/build.ps1");
    }

    public static async Task RegisterAsync(AppConfiguration config, string configurationPath, CancellationToken ct = default)
    {
        ConfigurationStore.Validate(config);
        var worker = FindWorker();
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("ไม่พบผู้ใช้ Windows");
        var start = DateTime.Today.ToString("yyyy-MM-dd") + "T" + config.DailyTime + ":00";
        static string E(string value) => SecurityElement.Escape(value)!;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>PC AutoBackup daily encrypted backup; exits when complete.</Description></RegistrationInfo>
              <Triggers>
                <CalendarTrigger><StartBoundary>{start}</StartBoundary><Enabled>true</Enabled><ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay></CalendarTrigger>
                <LogonTrigger><Enabled>true</Enabled><UserId>{E(sid)}</UserId><Delay>PT3M</Delay></LogonTrigger>
              </Triggers>
              <Principals><Principal id="Author"><UserId>{E(sid)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable><RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>true</Hidden><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority><RestartOnFailure><Interval>PT1H</Interval><Count>3</Count></RestartOnFailure></Settings>
              <Actions Context="Author"><Exec><Command>{E(worker)}</Command><Arguments>--scheduled --config {E('"' + Path.GetFullPath(configurationPath) + '"')}</Arguments><WorkingDirectory>{E(Path.GetDirectoryName(worker)!)}</WorkingDirectory></Exec></Actions>
            </Task>
            """;
        var temporary = Path.Combine(Path.GetTempPath(), "PCAutobackup-task-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            await File.WriteAllTextAsync(temporary, xml, System.Text.Encoding.Unicode, ct);
            await RunAsync(["/Create", "/TN", TaskName(config), "/XML", temporary, "/F"], ct);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static Task RemoveAsync(AppConfiguration config, CancellationToken ct = default) => RunAsync(["/Delete", "/TN", TaskName(config), "/F"], ct);

    private static async Task RunAsync(string[] args, CancellationToken ct)
    {
        var info = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("เปิด Task Scheduler ไม่ได้");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new IOException("ตั้งเวลาสำรองไม่สำเร็จ: " + await error + " " + await output);
    }
}
