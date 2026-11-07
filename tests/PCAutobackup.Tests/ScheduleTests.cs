using System.Xml.Linq;
using PCAutobackup.Core;

namespace PCAutobackup.Tests;

public sealed class ScheduleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PCAutobackup-tests", Guid.NewGuid().ToString("N"));
    private static readonly TimeZoneInfo Bangkok = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
    public ScheduleTests() => Directory.CreateDirectory(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public async Task LegacySingleTimeAndMultipleTimesSurviveReload()
    {
        var path = Path.Combine(root, "config.json");
        await File.WriteAllTextAsync(path, "{\"Version\":1,\"DailyTime\":\"22:15\"}");
        var old = await ConfigurationStore.LoadAsync(path);
        Assert.Equal(["22:15"], BackupSchedule.GetTimes(old));
        Assert.Equal(["21:00"], BackupSchedule.GetTimes(new AppConfiguration()));
        old.DailyTimes = BackupSchedule.ParseTimes("21:00, 08:15, 13:30");
        await ConfigurationStore.SaveAsync(path, old);
        Assert.Equal(["08:15", "13:30", "21:00"], BackupSchedule.GetTimes(await ConfigurationStore.LoadAsync(path)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("21:00,21:00")]
    [InlineData("24:00")]
    [InlineData("9:00")]
    [InlineData("21:00,")]
    [InlineData("09:00, wrong")]
    public void InvalidTimesCannotSilentlyDisableOrChangeSchedule(string value) =>
        Assert.Throws<ArgumentException>(() => BackupSchedule.ParseTimes(value));

    [Fact]
    public void RejectsTooManyTimesAndExplicitEmptyList()
    {
        Assert.Throws<ArgumentException>(() => BackupSchedule.ParseTimes(string.Join(',', Enumerable.Range(0, 25).Select(i => $"00:{i:00}"))));
        Assert.Throws<ArgumentException>(() => ConfigurationStore.Validate(new AppConfiguration { DailyTimes = [] }));
    }

    [Theory]
    [InlineData("2026-11-07T07:00:00+07:00", "2026-11-06T21:00:00+07:00")]
    [InlineData("2026-11-07T09:00:00+07:00", "2026-11-07T09:00:00+07:00")]
    [InlineData("2026-11-07T14:20:00+07:00", "2026-11-07T13:00:00+07:00")]
    [InlineData("2026-11-09T08:59:00+07:00", "2026-11-08T21:00:00+07:00")]
    public void MostRecentRoundHandlesMidnightAndMultipleMissedDays(string now, string due)
    {
        var config = new AppConfiguration { DailyTimes = ["09:00", "13:00", "21:00"] };
        Assert.Equal(DateTimeOffset.Parse(due), BackupSchedule.MostRecentDue(config, DateTimeOffset.Parse(now), Bangkok));
    }

    [Fact]
    public void DaylightSavingSkippedAndRepeatedHoursProduceOneDueRound()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        Assert.Equal(DateTimeOffset.Parse("2026-03-08T03:00:00-04:00"), BackupSchedule.MostRecentDue(new AppConfiguration { DailyTimes = ["02:30"] }, DateTimeOffset.Parse("2026-03-08T03:10:00-04:00"), zone));
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T01:30:00-04:00"), BackupSchedule.MostRecentDue(new AppConfiguration { DailyTimes = ["01:30"] }, DateTimeOffset.Parse("2026-11-01T01:40:00-05:00"), zone));
    }

    [Fact]
    public void TaskXmlIncludesAllTimesAndCatchUpTriggersWithoutWakeOrPassword()
    {
        var config = new AppConfiguration { DailyTimes = ["21:00", "09:00", "13:00"] };
        var worker = Path.Combine(root, "A & B", "worker.exe");
        var configPath = Path.Combine(root, "A & B.json");
        var doc = XDocument.Parse(WindowsScheduler.BuildTaskXml(config, configPath, worker, "S-1-5-21-1234", new DateTime(2026, 11, 7)));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal(["2026-11-07T09:00:00", "2026-11-07T13:00:00", "2026-11-07T21:00:00"], doc.Descendants(ns + "CalendarTrigger").Select(x => x.Element(ns + "StartBoundary")!.Value));
        Assert.Equal("SessionUnlock", doc.Descendants(ns + "StateChange").Single().Value);
        Assert.Contains("Power-Troubleshooter", doc.Descendants(ns + "Subscription").Single().Value);
        Assert.Single(doc.Descendants(ns + "LogonTrigger"));
        Assert.All(doc.Descendants(ns + "Delay"), x => Assert.Equal("PT1M", x.Value));
        Assert.Equal("true", doc.Descendants(ns + "StartWhenAvailable").Single().Value);
        Assert.Equal("false", doc.Descendants(ns + "WakeToRun").Single().Value);
        Assert.Equal("InteractiveToken", doc.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal(worker, doc.Descendants(ns + "Command").Single().Value);
        Assert.Contains(configPath, doc.Descendants(ns + "Arguments").Single().Value);
    }

    [Fact]
    public void ThaiWindowsCultureStillRegistersGregorianCalendarDates()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("th-TH");
            var xml = WindowsScheduler.BuildTaskXml(new AppConfiguration(), Path.Combine(root, "config.json"), Path.Combine(root, "worker.exe"), "S-1-5-21-1234", new DateTime(2026, 11, 7));
            Assert.Contains("2026-11-07T21:00:00", xml);
            Assert.DoesNotContain("2569-", xml);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task CatchUpRunsOnceAfterMissedRoundsAndRunsAgainAtNextTime()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-07T09:00:00+07:00"), Bangkok);
        var app = await MakeApp(clock);
        var first = await app.BackupScheduledAsync();
        Assert.NotNull(first);
        Assert.Null(await app.BackupScheduledAsync()); // repeated resume/unlock callbacks
        clock.Now = DateTimeOffset.Parse("2026-11-07T14:10:00+07:00"); // missed 13:00 while asleep
        Assert.NotNull(await app.BackupScheduledAsync());
        Assert.Null(await app.BackupScheduledAsync());
        clock.Now = DateTimeOffset.Parse("2026-11-10T10:00:00+07:00"); // multiple days powered off
        Assert.NotNull(await app.BackupScheduledAsync());
        Assert.Null(await app.BackupScheduledAsync());
        clock.Now = DateTimeOffset.Parse("2026-11-10T13:00:00+07:00");
        var latest = await app.BackupScheduledAsync();
        Assert.NotNull(latest);
        Assert.Equal(4, (await app.ListSnapshotsAsync()).Count);
        await app.RestoreAsync(latest!.SnapshotId, Path.Combine(root, "restored"));
        Assert.Equal("synthetic", await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(root, "restored"), "sample.txt", SearchOption.AllDirectories).Single()));
    }

    [Fact]
    public async Task BackupEndingAfterNextTimeDoesNotHideThatRound()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-07T12:59:00+07:00"), Bangkok);
        var app = await MakeApp(clock);
        await ConfigurationStore.WriteJsonAtomicAsync(app.ConfigurationPath + ".success.json", new RunStatus { Status = "Succeeded", StartedUtc = clock.Now, CompletedUtc = clock.Now.AddMinutes(2) });
        clock.Now = clock.Now.AddMinutes(3);
        Assert.NotNull(await app.BackupScheduledAsync());
    }

    [Fact]
    public async Task FailedCatchUpDoesNotMarkRoundCompleteAndDisabledScheduleDoesNothing()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-07T22:00:00+07:00"), Bangkok);
        var app = await MakeApp(clock);
        var source = app.Configuration.Sources[0].Path;
        Directory.Move(source, source + "-offline");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => app.BackupScheduledAsync());
        Assert.Null(await app.GetLastSuccessAsync());
        Directory.Move(source + "-offline", source);
        Assert.NotNull(await app.BackupScheduledAsync());
        app.Configuration.ScheduleEnabled = false;
        await app.SaveConfigurationAsync();
        clock.Now = clock.Now.AddDays(1);
        Assert.Null(await app.BackupScheduledAsync());
        Assert.Single(await app.ListSnapshotsAsync());
    }

    private async Task<ApplicationService> MakeApp(ManualClock clock)
    {
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "sample.txt"), "synthetic");
        var app = new ApplicationService(Path.Combine(root, "configuration.json"), clock);
        await app.LoadConfigurationAsync();
        app.Configuration.Destination = DestinationKind.LocalFolder;
        app.Configuration.LocalDestinationPath = Path.Combine(root, "repository");
        app.Configuration.IncludeApplicationInventory = false;
        app.Configuration.Sources.Add(new BackupSource { Path = source });
        app.Configuration.DailyTimes = ["09:00", "13:00", "21:00"];
        app.Configuration.ScheduleEnabled = true;
        await app.ExportRecoveryKeyAsync(Path.Combine(root, "recovery.key"));
        return app;
    }
}
