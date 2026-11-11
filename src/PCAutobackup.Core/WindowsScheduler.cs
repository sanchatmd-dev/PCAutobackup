using System.Diagnostics;
using System.Globalization;
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
        var xml = BuildTaskXml(config, configurationPath, worker, sid, DateTime.Today);
        var temporary = Path.Combine(Path.GetTempPath(), "PCAutobackup-task-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            await File.WriteAllTextAsync(temporary, xml, System.Text.Encoding.Unicode, ct);
            var (created, message) = await RunAsync(["/Create", "/TN", TaskName(config), "/XML", temporary, "/F"], ct);
            if (!created) throw new IOException(message);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static string BuildTaskXml(AppConfiguration config, string configurationPath, string worker, string sid, DateTime today)
    {
        ConfigurationStore.Validate(config);
        static string E(string value) => SecurityElement.Escape(value)!;
        var date = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var calendarTriggers = string.Join(Environment.NewLine, BackupSchedule.GetTimes(config).Select(time =>
            $"<CalendarTrigger><StartBoundary>{date}T{time}:00</StartBoundary><Enabled>true</Enabled><ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay></CalendarTrigger>"));
        const string resumeQuery = "<QueryList><Query Id='0' Path='System'><Select Path='System'>*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]</Select></Query></QueryList>";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>PC AutoBackup daily encrypted backup; exits when complete.</Description></RegistrationInfo>
              <Triggers>
                {calendarTriggers}
                <LogonTrigger><Enabled>true</Enabled><UserId>{E(sid)}</UserId><Delay>PT1M</Delay></LogonTrigger>
                <SessionStateChangeTrigger><Enabled>true</Enabled><Delay>PT1M</Delay><UserId>{E(sid)}</UserId><StateChange>SessionUnlock</StateChange></SessionStateChangeTrigger>
                <EventTrigger><Enabled>true</Enabled><Subscription>{E(resumeQuery)}</Subscription><Delay>PT1M</Delay></EventTrigger>
              </Triggers>
              <Principals><Principal id="Author"><UserId>{E(sid)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable><RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>true</Hidden><WakeToRun>false</WakeToRun><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority><RestartOnFailure><Interval>PT5M</Interval><Count>3</Count></RestartOnFailure></Settings>
              <Actions Context="Author"><Exec><Command>{E(worker)}</Command><Arguments>--scheduled --config {E('"' + Path.GetFullPath(configurationPath) + '"')}</Arguments><WorkingDirectory>{E(Path.GetDirectoryName(worker)!)}</WorkingDirectory></Exec></Actions>
            </Task>
            """;
    }

    public static async Task RemoveAsync(AppConfiguration config, CancellationToken ct = default)
    {
        var (deleted, message) = await RunAsync(["/Delete", "/TN", TaskName(config), "/F"], ct);
        if (deleted) return;
        // A task that was already removed outside the app is the requested end state. Otherwise
        // the schedule could never be switched off again from the settings window.
        // A failed schtasks /Query also means access denied or a stopped service, not only
        // a missing task. Accept only the explicit COM file-not-found result.
        object? service = null, folder = null, task = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!);
            ((dynamic)service!).Connect();
            folder = ((dynamic)service).GetFolder("\\");
            task = ((dynamic)folder).GetTask(TaskName(config));
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or FileNotFoundException && IsMissingTask(e.HResult)) { return; }
        finally
        {
            foreach (var value in new[] { task, folder, service })
                if (value is not null && System.Runtime.InteropServices.Marshal.IsComObject(value))
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(value);
        }
        throw new IOException(message);
    }

    internal static bool IsMissingTask(int hresult) => hresult == unchecked((int)0x80070002);

    private static async Task<(bool Succeeded, string Message)> RunAsync(string[] args, CancellationToken ct)
    {
        var info = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("เปิด Task Scheduler ไม่ได้");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode == 0, "ตั้งเวลาสำรองไม่สำเร็จ: " + await error + " " + await output);
    }
}
