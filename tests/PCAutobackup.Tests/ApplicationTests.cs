using System.Security.Cryptography;
using PCAutobackup.Core;

namespace PCAutobackup.Tests;

public sealed class ApplicationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PCAutobackup-tests-" + Guid.NewGuid().ToString("N"));
    public ApplicationTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task ExportKeyBackupAndRecoverOnNewConfiguration()
    {
        string source = Path.Combine(root, "source"), repository = Path.Combine(root, "repository"), destination = Path.Combine(root, "restored");
        Directory.CreateDirectory(source);
        var original = RandomNumberGenerator.GetBytes(5 * 1024 * 1024 + 23);
        await File.WriteAllBytesAsync(Path.Combine(source, "รายงาน.bin"), original);
        var writer = new ApplicationService(Path.Combine(root, "original-config.json"));
        await writer.LoadConfigurationAsync();
        writer.Configuration.Destination = DestinationKind.LocalFolder;
        writer.Configuration.LocalDestinationPath = repository;
        writer.Configuration.IncludeApplicationInventory = false;
        writer.Configuration.Sources.Add(new BackupSource { Path = source, Label = "งาน" });
        var recoveryFile = Path.Combine(root, "recovery.pca-key");
        await writer.ExportRecoveryKeyAsync(recoveryFile);
        Assert.True(writer.Configuration.RecoveryKeyAcknowledged);
        var saved = await File.ReadAllTextAsync(writer.ConfigurationPath);
        var rawKey = (await File.ReadAllTextAsync(recoveryFile)).Trim()[5..];
        Assert.DoesNotContain(rawKey, saved);
        var result = await writer.BackupAsync();
        Assert.Equal(1, result.FileCount);
        Assert.Equal("Succeeded", (await writer.GetLastRunAsync())!.Status);
        Assert.NotNull((await writer.GetLastSuccessAsync())!.SnapshotId);

        var reader = new ApplicationService(Path.Combine(root, "new-config.json"));
        await reader.LoadConfigurationAsync();
        await reader.ImportRecoveryKeyAsync(recoveryFile);
        await reader.SelectLocalRepositoryAsync(repository);
        var snapshots = await reader.ListSnapshotsAsync();
        Assert.Single(snapshots);
        await reader.RestoreAsync(snapshots[0].Id, destination);
        var restored = Directory.GetFiles(destination, "รายงาน.bin", SearchOption.AllDirectories).Single();
        Assert.Equal(original, await File.ReadAllBytesAsync(restored));
        Assert.NotEqual(writer.Configuration.DeviceId, reader.Configuration.DeviceId);
        Assert.Equal(DestinationKind.GoogleDrive, reader.Configuration.Destination); // restore selection never changes active backup target
    }

    [Fact]
    public async Task MissingKeyDoesNotLeaveOperationGateLocked()
    {
        var app = new ApplicationService(Path.Combine(root, "config.json"));
        await app.LoadConfigurationAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.ListSnapshotsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.ListSnapshotsAsync().WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task InvalidKeyImportDoesNotOverwriteExistingKey()
    {
        var app = new ApplicationService(Path.Combine(root, "config.json"));
        await app.LoadConfigurationAsync();
        await app.ExportRecoveryKeyAsync(Path.Combine(root, "valid.pca-key"));
        var before = app.Configuration.ProtectedRecoveryKey;
        var bad = Path.Combine(root, "bad.pca-key");
        await File.WriteAllTextAsync(bad, "PCA1-AAAA");
        await Assert.ThrowsAsync<InvalidDataException>(() => app.ImportRecoveryKeyAsync(bad));
        Assert.Equal(before, app.Configuration.ProtectedRecoveryKey);
    }

    [Fact]
    public async Task ConfigurationPersistsRetentionQuotaAndDefaults()
    {
        var app = new ApplicationService(Path.Combine(root, "config.json"));
        await app.LoadConfigurationAsync();
        Assert.Equal(7, app.Configuration.RetentionDays);
        Assert.Null(app.Configuration.QuotaBytes);
        Assert.Equal(DestinationKind.GoogleDrive, app.Configuration.Destination);
        app.Configuration.RetentionDays = 0;
        app.Configuration.QuotaBytes = 50_000_000_000;
        app.Configuration.DailyTime = "23:45";
        await app.SaveConfigurationAsync();
        var loaded = await new ApplicationService(app.ConfigurationPath).LoadConfigurationAsync();
        Assert.Equal(0, loaded.RetentionDays);
        Assert.Equal(50_000_000_000, loaded.QuotaBytes);
        Assert.Equal("23:45", loaded.DailyTime);
        app.Configuration.QuotaBytes = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => app.SaveConfigurationAsync());
        app.Configuration.QuotaBytes = null;
        app.Configuration.RetentionDays = -1;
        await Assert.ThrowsAsync<ArgumentException>(() => app.SaveConfigurationAsync());
    }

    [Fact]
    public async Task StaleConfigurationCannotOverwriteOtherWriter()
    {
        string path = Path.Combine(root, "config.json");
        var first = new ApplicationService(path); await first.LoadConfigurationAsync();
        await first.SaveConfigurationAsync();
        var second = new ApplicationService(path); await second.LoadConfigurationAsync();
        first.Configuration.DailyTime = "08:30"; await first.SaveConfigurationAsync();
        second.Configuration.DailyTime = "09:30";
        await Assert.ThrowsAsync<IOException>(() => second.SaveConfigurationAsync());
        var saved = await ConfigurationStore.LoadAsync(path);
        Assert.Equal("08:30", saved.DailyTime);
        Assert.Equal(2, saved.Revision);
    }

    [Fact]
    public async Task ScheduledMissingConfigFailsAndRecordsFailure()
    {
        var app = new ApplicationService(Path.Combine(root, "missing.json"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => app.BackupScheduledAsync());
        Assert.Equal("Failed", (await app.GetLastRunAsync())!.Status);
    }

    [Fact]
    public async Task PreflightFailureIsReported()
    {
        var app = new ApplicationService(Path.Combine(root, "config.json"));
        await app.LoadConfigurationAsync(); await app.SaveConfigurationAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.BackupAsync());
        Assert.Equal("Failed", (await app.GetLastRunAsync())!.Status);
    }

    [Fact]
    public async Task RecoveryImportDoesNotReplaceActiveBackupKey()
    {
        string source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "fixture.txt"), "fixture");
        var a = new ApplicationService(Path.Combine(root, "a.json")); await a.LoadConfigurationAsync();
        a.Configuration.Destination = DestinationKind.LocalFolder; a.Configuration.LocalDestinationPath = Path.Combine(root, "repo-a");
        a.Configuration.IncludeApplicationInventory = false; a.Configuration.Sources.Add(new BackupSource { Path = source });
        await a.ExportRecoveryKeyAsync(Path.Combine(root, "a.key")); await a.BackupAsync();
        var b = new ApplicationService(Path.Combine(root, "b.json")); await b.LoadConfigurationAsync();
        b.Configuration.Destination = DestinationKind.LocalFolder; b.Configuration.LocalDestinationPath = Path.Combine(root, "repo-b");
        b.Configuration.IncludeApplicationInventory = false; b.Configuration.Sources.Add(new BackupSource { Path = source });
        string bKey = Path.Combine(root, "b.key"); await b.ExportRecoveryKeyAsync(bKey); await b.BackupAsync();
        string original = a.Configuration.ProtectedRecoveryKey!;
        await a.ImportRecoveryKeyAsync(bKey);
        Assert.Equal(original, (await ConfigurationStore.LoadAsync(a.ConfigurationPath)).ProtectedRecoveryKey);
        await a.SelectLocalRepositoryAsync(Path.Combine(root, "repo-b"));
        var foreign = Assert.Single(await a.ListSnapshotsAsync());
        await a.RestoreAsync(foreign.Id, Path.Combine(root, "restore"));
        Assert.Equal("fixture", await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(root, "restore"), "fixture.txt", SearchOption.AllDirectories).Single()));
        Assert.Equal(1, (await a.BackupAsync()).FileCount);
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    [Fact]
    public async Task StatusWriteFailureAfterCommitReturnsWarning()
    {
        string source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "a"), "fixture");
        var app = new ApplicationService(Path.Combine(root, "config.json")); await app.LoadConfigurationAsync();
        app.Configuration.Destination = DestinationKind.LocalFolder; app.Configuration.LocalDestinationPath = Path.Combine(root, "repo");
        app.Configuration.IncludeApplicationInventory = false; app.Configuration.Sources.Add(new BackupSource { Path = source });
        await app.ExportRecoveryKeyAsync(Path.Combine(root, "recovery.key"));
        string successPath = app.ConfigurationPath + ".success.json";
        var progress = new InlineProgress<BackupProgress>(_ => Directory.CreateDirectory(successPath));
        var result = await app.BackupAsync(progress);
        Assert.NotEmpty(result.Warnings);
        Assert.Single(await app.ListSnapshotsAsync());
    }
    [Fact]
    public void LocalRepositoryLeaseRejectsConcurrentHolder()
    {
        string repository = Path.Combine(root, "repo");
        var a = new LocalBackupStore(repository); var b = new LocalBackupStore(repository);
        using (a.AcquireLease()) Assert.Throws<IOException>(() => b.AcquireLease());
        using var next = b.AcquireLease();
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
