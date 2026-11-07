using PCAutobackup.Core;

namespace PCAutobackup.Tests;

public sealed class RecoveryLifecycleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PCAutobackup-tests", Guid.NewGuid().ToString("N"));
    public RecoveryLifecycleTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData("Interrupted", true)]
    [InlineData("Running", true)]
    [InlineData("Failed", true)]
    [InlineData("Cancelled", false)]
    [InlineData("Succeeded", false)]
    public void PendingAttemptRetriesBeforeNextRoundButExplicitCancellationDoesNot(string state, bool expected)
    {
        var now = DateTimeOffset.Parse("2026-11-07T10:00:00Z");
        var config = new AppConfiguration { ScheduleEnabled = true, DailyTime = "21:00" };
        var success = new RunStatus { Status = "Succeeded", StartedUtc = now.AddHours(-1) };
        var attempt = new RunStatus { Status = state, StartedUtc = now.AddMinutes(-5) };
        Assert.Equal(expected, BackupSchedule.ShouldRun(config, now, TimeZoneInfo.Utc, success, attempt));
        config.ScheduleEnabled = false;
        Assert.False(BackupSchedule.ShouldRun(config, now, TimeZoneInfo.Utc, success, attempt));
        config.ScheduleEnabled = true;
        attempt.StartedUtc = success.StartedUtc.AddMinutes(-1);
        Assert.False(BackupSchedule.ShouldRun(config, now, TimeZoneInfo.Utc, success, attempt));
    }

    [Fact]
    public async Task AbandonedRunningStatusBecomesInterruptedAndRetryPreservesRecoverability()
    {
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "test.txt"), "original");
        var app = new ApplicationService(Path.Combine(root, "config.json"));
        await app.LoadConfigurationAsync();
        app.Configuration.Destination = DestinationKind.LocalFolder;
        app.Configuration.LocalDestinationPath = Path.Combine(root, "repository");
        app.Configuration.IncludeApplicationInventory = false;
        app.Configuration.ScheduleEnabled = true;
        app.Configuration.Sources.Add(new BackupSource { Path = source, Label = "test" });
        await app.ExportRecoveryKeyAsync(Path.Combine(root, "recovery.pca-key"));
        var baseline = await app.BackupAsync();
        var previousSuccess = await File.ReadAllBytesAsync(app.ConfigurationPath + ".success.json");
        await File.WriteAllTextAsync(Path.Combine(source, "test.txt"), "changed");
        await ConfigurationStore.WriteJsonAtomicAsync(app.ConfigurationPath + ".status.json", new RunStatus { Status = "Running", StartedUtc = DateTimeOffset.UtcNow });
        Assert.Equal("Interrupted", (await app.GetLastRunAsync())!.Status);
        Assert.Equal(previousSuccess, await File.ReadAllBytesAsync(app.ConfigurationPath + ".success.json"));
        var latest = await app.BackupScheduledAsync();
        Assert.NotNull(latest);
        await app.RestoreAsync(baseline.SnapshotId, Path.Combine(root, "old"));
        await app.RestoreAsync(latest.SnapshotId, Path.Combine(root, "new"));
        Assert.Equal("original", await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(root, "old"), "test.txt", SearchOption.AllDirectories).Single()));
        Assert.Equal("changed", await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(root, "new"), "test.txt", SearchOption.AllDirectories).Single()));
        Assert.Null(await app.BackupScheduledAsync());
    }

    [Fact]
    public async Task LiveBackupLockDoesNotBecomeInterrupted()
    {
        var path = Path.Combine(root, "live.json");
        await ConfigurationStore.WriteJsonAtomicAsync(path + ".status.json", new RunStatus { Status = "Running", StartedUtc = DateTimeOffset.UtcNow });
        using (OperationLock.Acquire(path))
            Assert.Equal("Running", (await new ApplicationService(path).GetLastRunAsync())!.Status);
        Assert.Equal("Interrupted", (await new ApplicationService(path).GetLastRunAsync())!.Status);
    }

    [Fact]
    public void InstanceActivationAndAbandonedOwnerAllowCleanRelaunch()
    {
        var path = Path.Combine(root, "config.json");
        using var ready = new ManualResetEventSlim();
        using var leave = new ManualResetEventSlim();
        using var activated = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            using var owner = new AppInstance("test", path);
            owner.Listen(activated.Set);
            ready.Set();
            leave.Wait();

        });
        thread.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
        using (var duplicate = new AppInstance("test", path))
        {
            Assert.False(duplicate.IsPrimary);
            duplicate.ActivatePrimary();
            Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));
        }
        leave.Set();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        using var abandoned = new Mutex(false, AppInstance.Identity("test", path));
        var killedOwner = new Thread(() => abandoned.WaitOne());
        killedOwner.Start();
        Assert.True(killedOwner.Join(TimeSpan.FromSeconds(5)));
        using var restarted = new AppInstance("test", path);
        Assert.True(restarted.IsPrimary);
        GC.KeepAlive(abandoned);
    }
}
