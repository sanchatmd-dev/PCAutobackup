using System.Text.Json;
using PCAutobackup.Core;

namespace PCAutobackup.Tests;

/// <summary>Regression tests from the application-service / scheduler / worker audit. Synthetic fixtures only.</summary>
public sealed class AuditApplicationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PCAutobackup-tests", Guid.NewGuid().ToString("N"));
    private static readonly TimeZoneInfo Bangkok = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
    public AuditApplicationTests() => Directory.CreateDirectory(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public async Task RestoreReportNeverOverwritesExistingFileAndDoesNotHideFailure()
    {
        var path = Path.Combine(root, ApplicationService.UnrestoredReportName);
        await File.WriteAllTextAsync(path, "existing data");
        await Assert.ThrowsAsync<IOException>(() => ApplicationService.WriteRestoreReportAsync(root, ["missing file"]));
        Assert.Equal("existing data", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task RestoreReportWritesAllWarnings()
    {
        await ApplicationService.WriteRestoreReportAsync(root, ["missing file", "damaged file"]);
        Assert.Equal(new[] { "missing file", "damaged file" }, await File.ReadAllLinesAsync(Path.Combine(root, ApplicationService.UnrestoredReportName)));
    }

    [Fact]
    public async Task RemovingScheduledTaskThatIsAlreadyGoneSucceeds()
    {
        // Switching the schedule off must work when the task was deleted outside the app.
        // A fresh random device ID names a task that cannot exist, so no real task is touched.
        await WindowsScheduler.RemoveAsync(new AppConfiguration());
    }

    [Theory]
    [InlineData(unchecked((int)0x80070002), true)]
    [InlineData(unchecked((int)0x80070005), false)]
    [InlineData(unchecked((int)0x800706BA), false)]
    public void SchedulerDoesNotTreatAccessOrServiceFailureAsMissing(int error, bool missing)
        => Assert.Equal(missing, WindowsScheduler.IsMissingTask(error));

    // ---- Atomic state writes versus concurrent readers (tray, settings window, launch check) ----

    [Fact]
    public async Task AtomicWriteWaitsForBriefConcurrentReader()
    {
        var path = Path.Combine(root, "state.json");
        await ConfigurationStore.WriteJsonAtomicAsync(path, new RunStatus { Status = "Running" });
        // Same sharing mode as ConfigurationStore.LoadAsync and ApplicationService.ReadStatusAsync.
        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var release = Task.Run(async () => { await Task.Delay(100); reader.Dispose(); });
        await ConfigurationStore.WriteJsonAtomicAsync(path, new RunStatus { Status = "Succeeded" });
        await release;
        Assert.Equal("Succeeded", JsonSerializer.Deserialize<RunStatus>(await File.ReadAllTextAsync(path), ConfigurationStore.JsonOptions)!.Status);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public async Task SuccessMarkerReaderDuringCommitDoesNotLoseTheRound()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-07T21:00:00+07:00"), Bangkok);
        var app = await MakeApp(clock);
        Assert.NotNull(await app.BackupScheduledAsync());
        clock.Now = DateTimeOffset.Parse("2026-11-08T09:00:00+07:00");
        // A settings window or launch check reads the marker exactly like this while the worker finishes.
        using var reader = new FileStream(app.ConfigurationPath + ".success.json", FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var release = Task.Run(async () =>
        {
            // Stay open until the writer has staged its replacement, then let go as a real reader would.
            for (var i = 0; i < 2000 && Directory.GetFiles(root, "configuration.json.success.json.*.tmp").Length == 0; i++) await Task.Delay(5);
            await Task.Delay(150);
            reader.Dispose();
        });
        var result = await app.BackupScheduledAsync();
        await release;
        Assert.NotNull(result);
        Assert.Empty(result.Warnings);
        Assert.Equal(result.SnapshotId, (await app.GetLastSuccessAsync())?.SnapshotId);
        Assert.Null(await app.BackupScheduledAsync()); // the round is recorded as done, not replayed
    }

    // ---- Recovery from a repository copy that cannot be written ----

    [Fact]
    public async Task RecoveryWorksFromReadOnlyRepositoryCopy()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-07T21:00:00+07:00"), Bangkok);
        var app = await MakeApp(clock);
        var snapshot = (await app.BackupScheduledAsync())!.SnapshotId;
        var repository = Path.Combine(root, "repository");
        // As copied from write-protected media or read through a read-only share: nothing can be opened for writing.
        var files = Directory.GetFiles(repository, "*", SearchOption.AllDirectories);
        Assert.Contains(Path.Combine(repository, "repository", "lease.lock"), files);
        foreach (var file in files) File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            var reader = new ApplicationService(Path.Combine(root, "new-pc.json"));
            await reader.LoadConfigurationAsync();
            await reader.ImportRecoveryKeyAsync(Path.Combine(root, "recovery.key"));
            await reader.SelectLocalRepositoryAsync(repository);
            Assert.Equal(snapshot, Assert.Single(await reader.ListSnapshotsAsync()).Id);
            await reader.RestoreAsync(snapshot, Path.Combine(root, "restored"));
            Assert.Equal("synthetic", await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(root, "restored"), "sample.txt", SearchOption.AllDirectories).Single()));
            // A backup still needs the exclusive lease and must not run against a copy it cannot lock.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => app.BackupAsync());
        }
        finally { foreach (var file in files) File.SetAttributes(file, FileAttributes.Normal); }
    }

    [Fact]
    public async Task RecoveryStillRefusesRepositoryLeasedByRunningJob()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-07T21:00:00+07:00"), Bangkok);
        var app = await MakeApp(clock);
        var snapshot = (await app.BackupScheduledAsync())!.SnapshotId;
        var reader = new ApplicationService(Path.Combine(root, "new-pc.json"));
        await reader.LoadConfigurationAsync();
        await reader.ImportRecoveryKeyAsync(Path.Combine(root, "recovery.key"));
        await reader.SelectLocalRepositoryAsync(Path.Combine(root, "repository"));
        using (new LocalBackupStore(Path.Combine(root, "repository")).AcquireLease())
        {
            await Assert.ThrowsAsync<IOException>(() => reader.ListSnapshotsAsync());
            await Assert.ThrowsAsync<IOException>(() => reader.RestoreAsync(snapshot, Path.Combine(root, "restored")));
        }
        Assert.Single(await reader.ListSnapshotsAsync());
    }

    // ---- Schedule decisions ----

    [Theory]
    // success, attempt state, attempt start, expected
    [InlineData("2026-11-07T13:00:05+07:00", "Cancelled", "2026-11-07T21:10:00+07:00", false)] // cancel declines the 21:00 round
    [InlineData("2026-11-07T13:00:05+07:00", "Cancelled", "2026-11-07T20:50:00+07:00", true)]  // cancel before 21:00 does not cover it
    [InlineData(null, "Cancelled", "2026-11-07T21:10:00+07:00", false)]
    [InlineData(null, "Cancelled", "2026-11-09T08:00:00+07:00", true)]                         // a future-dated cancel proves nothing
    [InlineData("2026-11-07T13:00:05+07:00", "Failed", "2026-11-07T21:10:00+07:00", true)]
    [InlineData("2026-11-07T21:00:05+07:00", "Succeeded", "2026-11-07T21:00:05+07:00", false)]
    [InlineData("2026-12-07T21:00:05+07:00", null, null, true)]                                // clock moved back after a success
    public void CancellationCoversOnlyItsRoundAndFutureMarkersCoverNothing(string? success, string? state, string? attemptStart, bool expected)
    {
        var now = DateTimeOffset.Parse("2026-11-07T21:30:00+07:00");
        var config = new AppConfiguration { ScheduleEnabled = true, DailyTimes = ["09:00", "13:00", "21:00"] };
        var lastSuccess = success is null ? null : new RunStatus { Status = "Succeeded", StartedUtc = DateTimeOffset.Parse(success) };
        var lastAttempt = state is null ? null : new RunStatus { Status = state, StartedUtc = DateTimeOffset.Parse(attemptStart!) };
        Assert.Equal(expected, BackupSchedule.ShouldRun(config, now, Bangkok, lastSuccess, lastAttempt));
    }

    [Fact]
    public async Task UserCancelledBackupIsNotRestartedByCatchUpBeforeNextRound()
    {
        // 21:00 round is still unsatisfied when the user starts and then cancels a backup at 21:30.
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-07T21:30:00+07:00"), Bangkok);
        var app = await MakeApp(clock);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<BackupProgress>(_ => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.BackupAsync(progress, cancellation.Token));
        Assert.Equal("Cancelled", (await app.GetLastRunAsync())!.Status);
        clock.Now = clock.Now.AddMinutes(5); // unlock / resume / logon / tray launch trigger
        Assert.Null(await app.BackupScheduledAsync());
        Assert.Equal("Cancelled", (await app.GetLastRunAsync())!.Status);
        clock.Now = DateTimeOffset.Parse("2026-11-08T09:00:00+07:00"); // next round is still honoured
        Assert.NotNull(await app.BackupScheduledAsync());
        Assert.Single(await app.ListSnapshotsAsync());
    }

    [Fact]
    public async Task InternalCancellationWithoutUserRequestIsRecordedAsRetryableFailure()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-07T21:30:00+07:00"), Bangkok);
        var app = await MakeApp(clock);
        // Stands in for a timeout-style OperationCanceledException that the caller's token did not request.
        var progress = new InlineProgress<BackupProgress>(_ => throw new OperationCanceledException("simulated internal timeout"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.BackupAsync(progress));
        var status = await app.GetLastRunAsync();
        Assert.Equal("Failed", status!.Status);
        Assert.DoesNotContain("ยกเลิกงานแล้ว", status.Message);
        clock.Now = clock.Now.AddMinutes(5);
        Assert.NotNull(await app.BackupScheduledAsync());
    }

    [Fact]
    public async Task SuccessMarkerFromTheFutureDoesNotSuppressScheduledBackups()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-07T21:30:00+07:00"), Bangkok);
        var app = await MakeApp(clock);
        // A backup completed while the system clock was a month ahead; the clock has since been corrected.
        await ConfigurationStore.WriteJsonAtomicAsync(app.ConfigurationPath + ".success.json",
            new RunStatus { Status = "Succeeded", StartedUtc = clock.Now.AddDays(30), CompletedUtc = clock.Now.AddDays(30) });
        Assert.NotNull(await app.BackupScheduledAsync());
        Assert.Null(await app.BackupScheduledAsync()); // heals after one run; no loop
        Assert.Single(await app.ListSnapshotsAsync());
    }

    // ---- External volume identity ----

    [Fact]
    public void ExternalVolumePathIsResolvedFromNormalizedConfiguredPath()
    {
        var expected = Path.GetFullPath(Path.Combine(root, "repository"));
        var volume = ExternalVolume.GetIdentity(root); // read-only volume GUID lookup of the temp drive
        var driveRoot = Path.GetPathRoot(expected)!;
        Assert.Equal(expected, ExternalVolume.ResolvePath(expected, volume), ignoreCase: true);
        // Hand-edited or legacy spellings must still land on the identified volume, never on a rooted remainder.
        var doubled = driveRoot + Path.DirectorySeparatorChar + expected[driveRoot.Length..];
        Assert.Equal(expected, ExternalVolume.ResolvePath(doubled, volume), ignoreCase: true);
        Assert.Equal(expected, ExternalVolume.ResolvePath(expected.Replace('\\', '/'), volume), ignoreCase: true);
        Assert.Equal(expected, ExternalVolume.ResolvePath(Path.Combine(root, ".", "unused", "..", "repository"), volume), ignoreCase: true);
    }

    // ---- Desktop/Tray launch helpers ----

    [Theory]
    [InlineData("{\"Version\":2,\"ScheduleEnabled\":true}")] // written by a newer release
    [InlineData("null")]
    [InlineData("")]                                          // truncated
    public async Task PendingLaunchCheckToleratesUnreadableConfiguration(string content)
    {
        var path = Path.Combine(root, "configuration.json");
        await File.WriteAllTextAsync(path, content);
        Assert.False(await DesktopRuntime.StartPendingBackupAsync(root, path));
        Assert.Equal(content, await File.ReadAllTextAsync(path)); // never replaced by a default configuration
    }

    [Fact]
    public void SettingsStillOpenWhenTrayExecutableCannotStart()
    {
        var tray = Path.Combine(root, "Tray");
        Directory.CreateDirectory(tray);
        File.WriteAllText(Path.Combine(tray, "PCAutobackup.Tray.exe"), "not an executable"); // damaged or blocked install
        DesktopRuntime.EnsureTray(root, Path.Combine(root, "configuration.json"));
    }

    // ---- Characterization of audited behaviour (these pass before and after the audit fixes) ----

    [Theory]
    [InlineData("")]                                   // zero-byte file after an abrupt stop
    [InlineData("{\"Version\":1,\"Revision\":7,\"Dev")] // truncated
    public async Task UnreadableConfigurationIsNeverReplacedByFreshDefaults(string content)
    {
        var path = Path.Combine(root, "configuration.json");
        await File.WriteAllTextAsync(path, content);
        var app = new ApplicationService(path);
        await Assert.ThrowsAsync<JsonException>(() => app.LoadConfigurationAsync());
        // Every writer re-reads the stored file first, so a new DeviceId/recovery key can never silently replace it.
        await Assert.ThrowsAsync<JsonException>(() => app.SaveConfigurationAsync());
        await Assert.ThrowsAsync<JsonException>(() => app.ExportRecoveryKeyAsync(Path.Combine(root, "new.key")));
        await Assert.ThrowsAsync<JsonException>(() => app.SetScheduleAsync(false)); // fails before Task Scheduler is touched
        await Assert.ThrowsAsync<JsonException>(() => app.BackupAsync());
        await Assert.ThrowsAsync<JsonException>(() => app.BackupScheduledAsync());
        await Assert.ThrowsAsync<JsonException>(() => app.ListSnapshotsAsync());
        Assert.Equal(content, await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(Path.Combine(root, "new.key")));
    }

    [Fact]
    public async Task SettingsAndRecoveryAreRejectedWhileAnotherProcessOwnsTheConfigurationLock()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-07T21:00:00+07:00"), Bangkok);
        var app = await MakeApp(clock);
        var snapshot = (await app.BackupScheduledAsync())!.SnapshotId;
        var before = await File.ReadAllBytesAsync(app.ConfigurationPath);
        using (OperationLock.Acquire(app.ConfigurationPath)) // stands in for a multi-hour scheduled Worker
        {
            app.Configuration.RetentionDays = 30;
            await Assert.ThrowsAsync<IOException>(() => app.SaveConfigurationAsync());
            await Assert.ThrowsAsync<IOException>(() => app.ListSnapshotsAsync());
            await Assert.ThrowsAsync<IOException>(() => app.RestoreAsync(snapshot, Path.Combine(root, "restored")));
            Assert.Equal(before, await File.ReadAllBytesAsync(app.ConfigurationPath));
        }
        await app.SaveConfigurationAsync();
        Assert.Equal(30, (await ConfigurationStore.LoadAsync(app.ConfigurationPath)).RetentionDays);
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
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
