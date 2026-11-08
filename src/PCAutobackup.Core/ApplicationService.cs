using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using PCAutobackup.Core.Google;

namespace PCAutobackup.Core;

public sealed class ApplicationService
{
    public string ConfigurationPath { get; }
    public AppConfiguration Configuration { get; private set; } = new();
    private const int ProductionChunkConcurrency = 16;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeProvider timeProvider;
    private string? recoveryLocalPath;
    private string? recoveryGoogleFolder;
    private string? recoveryProtectedKey;
    private string StatusPath => ConfigurationPath + ".status.json";
    private string SuccessPath => ConfigurationPath + ".success.json";
    private string ScheduledSkipPath => ConfigurationPath + ".scheduled-skip.json";
    public RunStatus? ScheduledSkip { get; private set; }

    public ApplicationService(string? configPath = null, TimeProvider? timeProvider = null)
    {
        ConfigurationPath = Path.GetFullPath(configPath ?? AppPaths.ConfigurationFile);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal BackupEngine CreateProductionEngine(IBackupStore store, byte[] key) =>
        new(store, key, timeProvider, maxConcurrentChunks: ProductionChunkConcurrency, maxConcurrentRestoreChunks: ProductionChunkConcurrency);

    public async Task<AppConfiguration> LoadConfigurationAsync()
    {
        Configuration = await ConfigurationStore.LoadAsync(ConfigurationPath);
        return Configuration;
    }

    public async Task SaveConfigurationAsync()
    {
        using var configLock = OperationLock.Acquire(ConfigurationPath);
        await SaveConfigurationLockedAsync();
    }

    private async Task SaveConfigurationLockedAsync()
    {
        ConfigurationStore.Validate(Configuration);
        if (Configuration.Destination == DestinationKind.ExternalDrive && !string.IsNullOrWhiteSpace(Configuration.LocalDestinationPath) && Configuration.ExternalVolumeId is null)
            Configuration.ExternalVolumeId = ExternalVolume.GetIdentity(Configuration.LocalDestinationPath);
        if (File.Exists(ConfigurationPath))
        {
            var current = await ConfigurationStore.LoadAsync(ConfigurationPath);
            if (current.Revision != Configuration.Revision)
                throw new IOException("การตั้งค่าถูกเปลี่ยนจากอีกหน้าต่างหรืองานเบื้องหลัง กรุณาโหลดการตั้งค่าใหม่ก่อนบันทึก");
        }
        else if (Configuration.Revision != 0) throw new IOException("ไฟล์ตั้งค่าถูกลบ กรุณาโหลดการตั้งค่าใหม่");
        long oldRevision = Configuration.Revision;
        Configuration.Revision = checked(oldRevision + 1);
        try { await ConfigurationStore.SaveAsync(ConfigurationPath, Configuration); }
        catch { Configuration.Revision = oldRevision; throw; }
    }
    public async Task ImportGoogleClientAsync(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("installed", out var installed))
            throw new InvalidDataException("กรุณาเลือก OAuth client JSON ชนิด Desktop app (installed)");
        var clientId = installed.GetProperty("client_id").GetString();
        if (string.IsNullOrWhiteSpace(clientId) || !clientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
            throw new InvalidDataException("Google client ID ไม่ถูกต้อง");
        // Changing OAuth projects invalidates the old authorization and its selected repository.
        if (!string.Equals(Configuration.GoogleClientId, clientId, StringComparison.Ordinal))
        {
            Configuration.ProtectedGoogleTokens = null;
            Configuration.GoogleEmail = null;
            Configuration.GoogleSubject = null;
            Configuration.GoogleRootFolderId = null;
            recoveryGoogleFolder = null;
        }
        Configuration.GoogleClientId = clientId;
        Configuration.GoogleClientSecret = installed.TryGetProperty("client_secret", out var secret) ? secret.GetString() ?? "" : "";
        await SaveConfigurationAsync();
    }

    public async Task SignInGoogleAsync(string? loginHint = null, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            Configuration = await new GoogleAccountService().SignInAsync(Configuration, loginHint, ct);
            recoveryGoogleFolder = null;
            await SaveConfigurationAsync();
        }
        finally { gate.Release(); }
    }

    public async Task DisconnectGoogleAsync()
    {
        await new GoogleAccountService().DisconnectAsync(Configuration);
        recoveryGoogleFolder = null;
        await SaveConfigurationAsync();
    }

    public async Task<IReadOnlyList<GoogleRepository>> ListGoogleRepositoriesAsync(CancellationToken ct = default)
    {
        var result = await new GoogleAccountService().ListRepositoriesAsync(Configuration, ct);
        await SaveConfigurationAsync();
        return result;
    }

    public async Task SelectGoogleRepositoryAsync(string id, CancellationToken ct = default)
    {
        var list = await ListGoogleRepositoriesAsync(ct);
        if (!list.Any(r => r.Id == id)) throw new InvalidDataException("ไม่พบชุดสำรองนี้ในบัญชีที่เชื่อมต่อ");
        recoveryGoogleFolder = id;
        recoveryLocalPath = null;
    }

    public Task SelectLocalRepositoryAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("ไม่พบโฟลเดอร์ชุดสำรอง");
        recoveryLocalPath = path;
        recoveryGoogleFolder = null;
        return Task.CompletedTask;
    }

    public async Task ExportRecoveryKeyAsync(string path)
    {
        byte[] key;
        if (Configuration.ProtectedRecoveryKey is null)
        {
            key = RandomNumberGenerator.GetBytes(32);
            Configuration.ProtectedRecoveryKey = ConfigurationStore.ProtectKey(key);
            await SaveConfigurationAsync();
        }
        else key = ConfigurationStore.UnprotectKey(Configuration.ProtectedRecoveryKey);
        try
        {
            var text = "PCA1-" + Convert.ToBase64String(key) + Environment.NewLine;
            // Never silently replace an existing recovery file belonging to another repository.
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using var writer = new StreamWriter(file);
            await writer.WriteAsync(text);
            await writer.FlushAsync();
            Configuration.RecoveryKeyAcknowledged = true;
            await SaveConfigurationAsync();
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public async Task ImportRecoveryKeyAsync(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > 4096) throw new InvalidDataException("ไฟล์กุญแจกู้คืนไม่ถูกต้อง");
        var text = (await File.ReadAllTextAsync(path)).Trim();
        if (!text.StartsWith("PCA1-", StringComparison.Ordinal)) throw new InvalidDataException("ไฟล์กุญแจต้องขึ้นต้นด้วย PCA1-");
        byte[] key;
        try { key = Convert.FromBase64String(text[5..]); }
        catch (FormatException) { throw new InvalidDataException("ไฟล์กุญแจไม่ถูกต้อง"); }
        try
        {
            if (key.Length != 32) throw new InvalidDataException("กุญแจต้องมีความยาว 256 บิต");
            var protectedKey = ConfigurationStore.ProtectKey(key);
            if (Configuration.ProtectedRecoveryKey is null)
            {
                Configuration.ProtectedRecoveryKey = protectedKey;
                Configuration.RecoveryKeyAcknowledged = true;
                await SaveConfigurationAsync();
            }
            recoveryProtectedKey = protectedKey;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public async Task<BackupResult> BackupAsync(IProgress<BackupProgress>? progress = null, CancellationToken ct = default) =>
        (await RunBackupAsync(false, progress, ct))!;

    public Task<BackupResult?> BackupScheduledAsync(CancellationToken ct = default) => RunBackupAsync(true, null, ct);

    private async Task<BackupResult?> RunBackupAsync(bool scheduled, IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        byte[]? key = null;
        try
        {
            ScheduledSkip = null;
            using var configLock = scheduled ? OperationLock.TryAcquire(ConfigurationPath) : OperationLock.Acquire(ConfigurationPath);
            if (configLock is null)
            {
                // Never replace the active owner's status, success marker or configuration.
                var now = timeProvider.GetUtcNow();
                ScheduledSkip = new RunStatus
                {
                    Status = "Skipped", Message = "ข้ามเพราะมีงานกำลังทำอยู่",
                    StartedUtc = now, CompletedUtc = now
                };
                await ConfigurationStore.WriteJsonAtomicAsync(ScheduledSkipPath, ScheduledSkip, ct);
                return null;
            }
            var status = new RunStatus { Status = "Running", StartedUtc = timeProvider.GetUtcNow(), Message = "กำลังสำรองข้อมูล" };
            try
            {
                if (!File.Exists(ConfigurationPath)) throw new FileNotFoundException("ไม่พบไฟล์ตั้งค่าสำหรับงานสำรอง", ConfigurationPath);
                Configuration = await ConfigurationStore.LoadAsync(ConfigurationPath, ct);
                ConfigurationStore.Validate(Configuration);
                if (scheduled)
                {
                    if (!Configuration.ScheduleEnabled) return null;
                    var last = await GetLastSuccessAsync();
                    // Holding the config lock proves a previous Running attempt is no longer active.
                    var attempt = await ReadStatusAsync(StatusPath);
                    if (!BackupSchedule.ShouldRun(Configuration, timeProvider.GetUtcNow(), timeProvider.LocalTimeZone, last, attempt)) return null;
                }
                if (Configuration.Sources.Count == 0) throw new InvalidOperationException("กรุณาเลือกโฟลเดอร์ต้นทางก่อนสำรอง");
                if (!Configuration.RecoveryKeyAcknowledged) throw new InvalidOperationException("กรุณาส่งออกและเก็บกุญแจกู้คืนไว้นอกเครื่องก่อนเริ่มสำรอง");
                key = GetKey();
                await ConfigurationStore.WriteJsonAtomicAsync(StatusPath, status, ct);
                await using var store = await OpenStoreAsync(false, ct);
                using var repositoryLock = OperationLock.Acquire(RepositoryIdentity(false));
                using var localLease = (store as LocalBackupStore)?.AcquireLease();
                var engine = CreateProductionEngine(store, key);
                var config = Clone(Configuration);
                if (config.IncludeApplicationInventory)
                {
                    var inventoryPath = Path.Combine(Path.GetDirectoryName(ConfigurationPath)!, "application-inventory");
                    Directory.CreateDirectory(inventoryPath);
                    await WriteApplicationInventoryAsync(Path.Combine(inventoryPath, "installed-applications.json"), ct);
                    config.Sources.Add(new BackupSource { Id = "application-inventory", Label = "Installed applications", Path = inventoryPath });
                }
                var relay = new InlineProgress<BackupProgress>(p =>
                {
                    status.FilesProcessed = p.FilesProcessed;
                    status.BytesProcessed = p.BytesProcessed;
                    progress?.Report(p);
                });
                var result = await engine.BackupAsync(config, relay, ct);
                status.Status = "Succeeded";
                status.Message = result.Warnings.Count > 0 ? string.Join(Environment.NewLine, result.Warnings) : "สำรองข้อมูลสำเร็จ";
                status.SnapshotId = result.SnapshotId;
                status.FilesProcessed = result.FileCount;
                status.BytesProcessed = result.TotalBytes;
                status.CompletedUtc = timeProvider.GetUtcNow();
                status.PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
                try
                {
                    await ConfigurationStore.WriteJsonAtomicAsync(StatusPath, status);
                    await ConfigurationStore.WriteJsonAtomicAsync(SuccessPath, status);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    result = result with { Warnings = result.Warnings.Append("บันทึกสถานะไม่สำเร็จ แต่สร้างชุดสำรองแล้ว: " + SafeError(e)).ToArray() };
                    status.Message = string.Join(Environment.NewLine, result.Warnings);
                    try { await ConfigurationStore.WriteJsonAtomicAsync(StatusPath, status); }
                    catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException) { }
                }
                return result;
            }
            catch (Exception e)
            {
                status.Status = e is OperationCanceledException ? "Cancelled" : "Failed";
                status.Message = SafeError(e);
                status.CompletedUtc = timeProvider.GetUtcNow();
                status.PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
                try { await ConfigurationStore.WriteJsonAtomicAsync(StatusPath, status); }
                catch (Exception statusError) when (statusError is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); gate.Release(); }
    }
    public async Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        byte[]? key = null;
        try
        {
            using var configLock = OperationLock.Acquire(ConfigurationPath);
            if (File.Exists(ConfigurationPath)) Configuration = await ConfigurationStore.LoadAsync(ConfigurationPath, ct);
            key = GetKey(true);
            await using var store = await OpenStoreAsync(true, ct);
            using var repositoryLock = OperationLock.Acquire(RepositoryIdentity(true));
            using var localLease = (store as LocalBackupStore)?.AcquireLease();
            return await new BackupEngine(store, key).ListSnapshotsAsync(ct);
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); gate.Release(); }
    }
    public async Task<RestoreResult> RestoreAsync(string id, string destination, IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        byte[]? key = null;
        try
        {
            using var configLock = OperationLock.Acquire(ConfigurationPath);
            if (File.Exists(ConfigurationPath)) Configuration = await ConfigurationStore.LoadAsync(ConfigurationPath, ct);
            key = GetKey(true);
            await using var store = await OpenStoreAsync(true, ct);
            using var repositoryLock = OperationLock.Acquire(RepositoryIdentity(true));
            using var localLease = (store as LocalBackupStore)?.AcquireLease();
            return await CreateProductionEngine(store, key).RestoreAsync(id, destination, progress, ct);
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); gate.Release(); }
    }

    public async Task SetScheduleAsync(bool enabled)
    {
        using var configLock = OperationLock.Acquire(ConfigurationPath);
        var previous = File.Exists(ConfigurationPath) ? await ConfigurationStore.LoadAsync(ConfigurationPath) : new AppConfiguration();
        if (previous.Revision != Configuration.Revision) throw new IOException("การตั้งค่าถูกเปลี่ยนจากอีกหน้าต่างหรืองานเบื้องหลัง กรุณาโหลดการตั้งค่าใหม่ก่อนบันทึก");
        ConfigurationStore.Validate(Configuration);
        if (enabled)
        {
            if (Configuration.Sources.Count == 0 || !Configuration.RecoveryKeyAcknowledged) throw new InvalidOperationException("เลือกโฟลเดอร์และส่งออกกุญแจกู้คืนก่อนเปิดสำรองอัตโนมัติ");
            if (Configuration.Destination == DestinationKind.GoogleDrive && Configuration.ProtectedGoogleTokens is null) throw new InvalidOperationException("กรุณาเชื่อมต่อ Google Drive ก่อนเปิดสำรองอัตโนมัติ");
            if (Configuration.Destination != DestinationKind.GoogleDrive && string.IsNullOrWhiteSpace(Configuration.LocalDestinationPath)) throw new InvalidOperationException("กรุณาเลือกปลายทางสำรอง");
        }
        bool changedTask = false;
        try
        {
            if (enabled) { await WindowsScheduler.RegisterAsync(Configuration, ConfigurationPath); changedTask = true; }
            else if (previous.ScheduleEnabled) { await WindowsScheduler.RemoveAsync(previous); changedTask = true; }
            Configuration.ScheduleEnabled = enabled;
            await SaveConfigurationLockedAsync();
        }
        catch (Exception original)
        {
            Configuration.ScheduleEnabled = previous.ScheduleEnabled;
            if (changedTask)
            {
                try
                {
                    if (previous.ScheduleEnabled) await WindowsScheduler.RegisterAsync(previous, ConfigurationPath);
                    else await WindowsScheduler.RemoveAsync(Configuration);
                }
                catch (Exception rollback) { throw new IOException("บันทึกการตั้งเวลาไม่สำเร็จ และคืนค่างานเดิมไม่ได้ กรุณาตรวจสอบ Task Scheduler", new AggregateException(original, rollback)); }
            }
            throw;
        }
    }
    public async Task<RunStatus?> GetLastRunAsync()
    {
        var status = await ReadStatusAsync(StatusPath);
        if (status?.Status != "Running") return status;
        try
        {
            using var lease = OperationLock.Acquire(ConfigurationPath);
            status = await ReadStatusAsync(StatusPath);
            if (status?.Status == "Running")
            {
                status.Status = "Interrupted";
                status.Message = "งานก่อนหน้าหยุดกลางคัน เปิดสำรองอัตโนมัติไว้เพื่อเริ่มใหม่ ชุดสำเร็จก่อนหน้ายังอยู่";
                status.CompletedUtc = timeProvider.GetUtcNow();
                await ConfigurationStore.WriteJsonAtomicAsync(StatusPath, status);
            }
        }
        catch (IOException) { /* A live job owns the lock, or status is temporarily unavailable. */ }
        return status;
    }
    public Task<RunStatus?> GetLastSuccessAsync() => ReadStatusAsync(SuccessPath);
    public Task<RunStatus?> GetLastScheduledSkipAsync() => ReadStatusAsync(ScheduledSkipPath);
    private static async Task<RunStatus?> ReadStatusAsync(string path)
    {
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 16384, true);
        return await JsonSerializer.DeserializeAsync<RunStatus>(stream, ConfigurationStore.JsonOptions);
    }

    private async Task<IBackupStore> OpenStoreAsync(bool recovery, CancellationToken ct)
    {
        if (recovery && recoveryLocalPath is not null) return new LocalBackupStore(recoveryLocalPath);
        if (recovery && recoveryGoogleFolder is not null)
        {
            var selected = Clone(Configuration);
            selected.GoogleRootFolderId = recoveryGoogleFolder;
            return await GoogleDriveBackupStore.CreateAsync(selected, async () =>
            {
                Configuration.ProtectedGoogleTokens = selected.ProtectedGoogleTokens;
                await SaveConfigurationLockedAsync();
            }, ct);
        }
        if (Configuration.Destination == DestinationKind.GoogleDrive)
        {
            if (recovery && string.IsNullOrWhiteSpace(Configuration.GoogleRootFolderId)) throw new InvalidOperationException("กรุณาเลือกชุดสำรองจากบัญชี Google ก่อนกู้คืน");
            return await GoogleDriveBackupStore.CreateAsync(Configuration, SaveConfigurationLockedAsync, ct);
        }
        var path = GetLocalDestination();
        if (recovery && !Directory.Exists(path)) throw new DirectoryNotFoundException("ไม่พบชุดสำรองที่เลือก");
        return new LocalBackupStore(path);
    }

    private string GetLocalDestination()
    {
        if (string.IsNullOrWhiteSpace(Configuration.LocalDestinationPath) || !Path.IsPathFullyQualified(Configuration.LocalDestinationPath)) throw new InvalidOperationException("กรุณาเลือกปลายทางสำรอง");
        return Configuration.Destination == DestinationKind.ExternalDrive
            ? ExternalVolume.ResolvePath(Configuration.LocalDestinationPath, Configuration.ExternalVolumeId ?? throw new InvalidOperationException("กรุณาบันทึกปลายทางดิสก์ภายนอกก่อน"))
            : Path.GetFullPath(Configuration.LocalDestinationPath);
    }

    private string RepositoryIdentity(bool recovery)
    {
        if (recovery && recoveryLocalPath is not null) return "local:" + Path.GetFullPath(recoveryLocalPath).TrimEnd(Path.DirectorySeparatorChar);
        if (recovery && recoveryGoogleFolder is not null) return "google:" + recoveryGoogleFolder;
        return Configuration.Destination == DestinationKind.GoogleDrive ? "google:" + Configuration.GoogleRootFolderId : "local:" + GetLocalDestination().TrimEnd(Path.DirectorySeparatorChar);
    }

    private byte[] GetKey(bool recovery = false) => (recovery ? recoveryProtectedKey ?? Configuration.ProtectedRecoveryKey : Configuration.ProtectedRecoveryKey) is string protectedKey ? ConfigurationStore.UnprotectKey(protectedKey) : throw new InvalidOperationException("กรุณาส่งออกกุญแจสำหรับชุดใหม่ หรือนำเข้ากุญแจของชุดที่ต้องการกู้คืน");
    private static AppConfiguration Clone(AppConfiguration value) => JsonSerializer.Deserialize<AppConfiguration>(JsonSerializer.Serialize(value))!;
    public static string SafeError(Exception e) => e is OperationCanceledException ? "ยกเลิกงานแล้ว ชุดสำรองที่สำเร็จก่อนหน้ายังคงอยู่" : e.Message;

    private static async Task WriteApplicationInventoryAsync(string file, CancellationToken ct)
    {
        var apps = new List<object>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) continue;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                using var entry = uninstall.OpenSubKey(name);
                if (entry?.GetValue("DisplayName") is not string displayName) continue;
                apps.Add(new { Name = displayName, Version = entry.GetValue("DisplayVersion") as string, Publisher = entry.GetValue("Publisher") as string });
            }
        }
        await ConfigurationStore.WriteJsonAtomicAsync(file, new { ExportedUtc = DateTimeOffset.UtcNow, Note = "Inventory only. Reinstall compatible software from official publishers; license/sign-in may be required.", Applications = apps }, ct);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
