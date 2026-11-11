using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCAutobackup.Core;

public static class AppPaths
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCAutobackup");
    public static string ConfigurationFile => Path.Combine(DataDirectory, "configuration.json");
}

public static class ConfigurationStore
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static async Task<AppConfiguration> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return new AppConfiguration();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 16384, true);
        var config = await JsonSerializer.DeserializeAsync<AppConfiguration>(stream, JsonOptions, ct)
            ?? throw new InvalidDataException("ไฟล์ตั้งค่าว่างหรือไม่ถูกต้อง");
        if (config.Version != 1) throw new InvalidDataException("เวอร์ชันไฟล์ตั้งค่านี้ยังไม่รองรับ");
        config.Language = AppLocalization.NormalizeLanguage(config.Language);
        config.PendingSourceRemovals ??= [];
        return config;
    }

    public static async Task SaveAsync(string path, AppConfiguration config, CancellationToken ct = default)
    {
        Validate(config);
        await WriteJsonAtomicAsync(path, config, ct);
    }

    public static void Validate(AppConfiguration config)
    {
        config.Language = AppLocalization.NormalizeLanguage(config.Language);
        if (config.RetentionDays < 0) throw new ArgumentException("จำนวนวันต้องไม่ติดลบ");
        if (config.QuotaBytes is <= 0) throw new ArgumentException("เพดานพื้นที่ต้องมากกว่า 0 หรือไม่กำหนด");
        if (!TimeOnly.TryParseExact(config.DailyTime, "HH:mm", out _)) throw new ArgumentException("เวลาต้องอยู่ในรูปแบบ HH:mm");
        _ = BackupSchedule.GetTimes(config);
        if (!Guid.TryParseExact(config.DeviceId, "N", out _)) throw new ArgumentException("รหัสเครื่องไม่ถูกต้อง");
        if (!Enum.IsDefined(config.Destination)) throw new ArgumentException("ปลายทางไม่ถูกต้อง");
        if (config.Sources is null) throw new ArgumentException("รายการโฟลเดอร์ไม่ถูกต้อง");
        if (config.PendingSourceRemovals is null) throw new ArgumentException("รายการโฟลเดอร์ที่นำออกไม่ถูกต้อง");
        foreach (var entry in config.PendingSourceRemovals)
        {
            if (entry is null || entry.Source is null || entry.OriginalIndex < 0 || string.IsNullOrWhiteSpace(entry.Source.Id)
                || !Path.IsPathFullyQualified(entry.Source.Path)) throw new ArgumentException("รายการโฟลเดอร์ที่นำออกไม่ถูกต้อง");
        }
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in config.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id) || !ids.Add(source.Id)) throw new ArgumentException("รหัสโฟลเดอร์ซ้ำหรือว่าง");
            if (!Path.IsPathFullyQualified(source.Path)) throw new ArgumentException("กรุณาเลือกโฟลเดอร์ต้นทางแบบ absolute path");
        }
    }

    public static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken ct = default)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct);
                await stream.FlushAsync(ct);
                stream.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            // Windows refuses to replace a file while any reader still has it open, even one that
            // shares delete access. Readers hold these files for milliseconds, so wait briefly.
            for (var attempt = 1; ; attempt++)
            {
                try { File.Move(temporary, path, true); break; }
                catch (Exception e) when (attempt < 20 && !Directory.Exists(path)
                    && (e is UnauthorizedAccessException || e is IOException && (e.HResult & 0xffff) is 32 or 33))
                { await Task.Delay(50, CancellationToken.None); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string ProtectKey(byte[] key) => Convert.ToBase64String(ProtectedData.Protect(key, Encoding.UTF8.GetBytes("PCAutobackup.RecoveryKey.v1"), DataProtectionScope.CurrentUser));
    public static byte[] UnprotectKey(string value)
    {
        var key = ProtectedData.Unprotect(Convert.FromBase64String(value), Encoding.UTF8.GetBytes("PCAutobackup.RecoveryKey.v1"), DataProtectionScope.CurrentUser);
        if (key.Length != 32) throw new CryptographicException("กุญแจกู้คืนไม่ถูกต้อง");
        return key;
    }
}

public sealed class RunStatus
{
    public string Status { get; set; } = "Idle";
    public string? Message { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string? SnapshotId { get; set; }
    public long FilesProcessed { get; set; }
    public long BytesProcessed { get; set; }
    public long PeakWorkingSetBytes { get; set; }
}

internal sealed class OperationLock : IDisposable
{
    private readonly FileStream stream;
    private OperationLock(FileStream stream) => this.stream = stream;
    public static OperationLock Acquire(string identity)
        => TryAcquire(identity) ?? throw new IOException("มีงานสำรองหรือกู้คืนกำลังทำงานอยู่ กรุณารอให้เสร็จก่อน");

    // Only sharing/lock violations mean busy. Disk and permission errors must still fail.
    public static OperationLock? TryAcquire(string identity)
    {
        var directory = Path.Combine(AppPaths.DataDirectory, "locks");
        Directory.CreateDirectory(directory);
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant())));
        try { return new OperationLock(new FileStream(Path.Combine(directory, name + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { return null; }
    }
    public void Dispose() => stream.Dispose();
}
