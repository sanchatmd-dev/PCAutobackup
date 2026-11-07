namespace PCAutobackup.Core;

public enum DestinationKind { GoogleDrive, ExternalDrive, LocalFolder }

public sealed class AppConfiguration
{
    public int Version { get; set; } = 1;
    public long Revision { get; set; }
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string DeviceName { get; set; } = Environment.MachineName;
    public DestinationKind Destination { get; set; } = DestinationKind.GoogleDrive;
    public string LocalDestinationPath { get; set; } = "";
    public string? ExternalVolumeId { get; set; }
    public List<BackupSource> Sources { get; set; } = [];
    public int RetentionDays { get; set; } = 7;
    public long? QuotaBytes { get; set; }
    public string DailyTime { get; set; } = "21:00";
    public bool ScheduleEnabled { get; set; }
    public string GoogleClientId { get; set; } = "";
    public string GoogleClientSecret { get; set; } = "";
    public string? GoogleEmail { get; set; }
    public string? GoogleSubject { get; set; }
    public string? GoogleRootFolderId { get; set; }
    public string? ProtectedRecoveryKey { get; set; }
    public string? ProtectedGoogleTokens { get; set; }
    public bool RecoveryKeyAcknowledged { get; set; }
    public bool IncludeApplicationInventory { get; set; } = true;
}

public sealed class BackupSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed record StoredObject(string Name, long Size, DateTimeOffset ModifiedUtc);

// All names are repository-relative forward-slash names, never arbitrary local paths.
// Put is atomic from a reader's perspective; overwrite=false must preserve an existing object.
public interface IBackupStore : IAsyncDisposable
{
    Task<bool> ExistsAsync(string name, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string name, CancellationToken ct = default);
    Task PutAsync(string name, Stream content, bool overwrite = false, CancellationToken ct = default);
    Task DeleteAsync(string name, CancellationToken ct = default);
    IAsyncEnumerable<StoredObject> ListAsync(string prefix, CancellationToken ct = default);
}

public sealed record BackupProgress(string Stage, string? CurrentFile = null, long FilesProcessed = 0, long BytesProcessed = 0);
public sealed record BackupResult(string SnapshotId, long FileCount, long TotalBytes, long UploadedBytes, IReadOnlyList<string> Warnings);
public sealed record SnapshotInfo(string Id, DateTimeOffset CreatedUtc, string DeviceName, long FileCount, long TotalBytes);
public sealed record RestoreResult(long FilesRestored, long BytesRestored, IReadOnlyList<string> Warnings);

// Root implements Windows integration; Core agent implements BackupEngine and LocalBackupStore.
// Expected public API:
// new BackupEngine(IBackupStore store, byte[] recoveryKey)
// Task<BackupResult> BackupAsync(AppConfiguration config, IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
// Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(CancellationToken ct = default)
// Task<RestoreResult> RestoreAsync(string snapshotId, string destinationDirectory, IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
// new LocalBackupStore(string rootDirectory)
