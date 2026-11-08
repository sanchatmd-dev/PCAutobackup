using PCAutobackup.Core;
using System.Security.Cryptography;

namespace PCAutobackup.Tests;

public sealed class EngineTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PCAutobackup-tests", Guid.NewGuid().ToString("N"));
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    private string Source => Path.Combine(root, "source");
    private string Repo => Path.Combine(root, "repo");
    private string Restore => Path.Combine(root, "restore");
    public EngineTests() { Directory.CreateDirectory(Source); Directory.CreateDirectory(Repo); }
    private AppConfiguration Config() => new() { DeviceId = "device-1", DeviceName = "fixture", Sources = [new BackupSource { Path = Source }] };
    private BackupEngine Engine() => new(new LocalBackupStore(Repo), key);
    private static string SourceFolder(string restore) => Assert.Single(Directory.GetDirectories(restore));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public async Task RoundTripChangedAndDeletedFilesAndEmptyFolders()
    {
        byte[] content = RandomNumberGenerator.GetBytes(4 * 1024 * 1024 + 93);
        Directory.CreateDirectory(Path.Combine(Source, "empty"));
        await File.WriteAllBytesAsync(Path.Combine(Source, "data.bin"), content);
        await File.WriteAllTextAsync(Path.Combine(Source, "removed.txt"), "before");
        var engine = Engine(); var config = Config();
        var first = await engine.BackupAsync(config);
        await File.WriteAllTextAsync(Path.Combine(Source, "data.bin"), "changed");
        File.Delete(Path.Combine(Source, "removed.txt"));
        var second = await engine.BackupAsync(config);
        await engine.RestoreAsync(first.SnapshotId, Restore);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(SourceFolder(Restore), "data.bin")));
        Assert.True(Directory.Exists(Path.Combine(SourceFolder(Restore), "empty")));
        Assert.True(File.Exists(Path.Combine(SourceFolder(Restore), "removed.txt")));
        string restore2 = Path.Combine(root, "restore2");
        await engine.RestoreAsync(second.SnapshotId, restore2);
        Assert.Equal("changed", await File.ReadAllTextAsync(Path.Combine(SourceFolder(restore2), "data.bin")));
        Assert.False(File.Exists(Path.Combine(SourceFolder(restore2), "removed.txt")));
    }

    [Fact]
    public async Task MissingSourceAndCancellationLeaveSnapshotUntouched()
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "a"), "original");
        var config = Config(); var engine = Engine(); var first = await engine.BackupAsync(config);
        Directory.Move(Source, Source + "-missing");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => engine.BackupAsync(config));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.BackupAsync(config, ct: cts.Token));
        Assert.Equal(first.SnapshotId, Assert.Single(await engine.ListSnapshotsAsync()).Id);
        await engine.RestoreAsync(first.SnapshotId, Restore);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(SourceFolder(Restore), "a")));
    }

    [Fact]
    public async Task ZeroRetentionAndQuotaKeepLatestAndOtherFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "a"), "first");
        var config = Config(); var engine = Engine();
        var first = await engine.BackupAsync(config);
        await File.WriteAllTextAsync(Path.Combine(Source, "a"), "second");
        config.RetentionDays = 0; config.QuotaBytes = 1;
        await File.WriteAllTextAsync(Path.Combine(Repo, "unrelated.txt"), "untouched");
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.BackupAsync(config));
        Assert.Equal(first.SnapshotId, Assert.Single(await engine.ListSnapshotsAsync()).Id);
        config.QuotaBytes = null;
        var second = await engine.BackupAsync(config);
        Assert.Equal(second.SnapshotId, Assert.Single(await engine.ListSnapshotsAsync()).Id);
        Assert.True(File.Exists(Path.Combine(Repo, "unrelated.txt")));
        await engine.RestoreAsync(second.SnapshotId, Restore);
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(SourceFolder(Restore), "a")));
    }

    private async Task AddHistoricalEmptySnapshot(DateTimeOffset created)
    {
        string id = created.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        byte[] plain = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Id = id, CreatedUtc = created, DeviceName = "fixture", Entries = Array.Empty<object>() });
        byte[] encrypted = new byte[32 + plain.Length];
        "PAB1"u8.CopyTo(encrypted); RandomNumberGenerator.Fill(encrypted.AsSpan(4, 12));
        byte[] encryptionKey = HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes("PCAutobackup encryption v1"));
        using (var aes = new AesGcm(encryptionKey, 16))
            aes.Encrypt(encrypted.AsSpan(4, 12), plain, encrypted.AsSpan(32), encrypted.AsSpan(16, 16), System.Text.Encoding.UTF8.GetBytes("manifest:" + id));
        await File.WriteAllBytesAsync(Path.Combine(Repo, "snapshots", id + ".manifest"), encrypted);
    }

    [Fact]
    public async Task PositiveRetentionRemovesOldSnapshotsAndPreservesRecentAndUnknownObjects()
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "a"), "data");
        var engine = Engine(); var config = Config(); config.RetentionDays = 7;
        await engine.BackupAsync(config);
        await AddHistoricalEmptySnapshot(DateTimeOffset.UtcNow.AddDays(-8));
        await AddHistoricalEmptySnapshot(DateTimeOffset.UtcNow.AddDays(-3));
        await File.WriteAllTextAsync(Path.Combine(Repo, "chunks", "not-owned.chunk"), "keep");
        await File.WriteAllTextAsync(Path.Combine(Repo, "snapshots", "not-owned.manifest"), "keep");
        await engine.BackupAsync(config);
        var remaining = await engine.ListSnapshotsAsync();
        Assert.Equal(3, remaining.Count);
        Assert.All(remaining, x => Assert.True(x.CreatedUtc > DateTimeOffset.UtcNow.AddDays(-7)));
        Assert.True(File.Exists(Path.Combine(Repo, "chunks", "not-owned.chunk")));
        Assert.True(File.Exists(Path.Combine(Repo, "snapshots", "not-owned.manifest")));
    }
    [Fact]
    public async Task WrongKeyCorruptionAndWrongOwnerCannotPrune()
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "a"), "valuable");
        var config = Config(); var engine = Engine(); var first = await engine.BackupAsync(config);
        config.RetentionDays = 0;
        var wrong = new BackupEngine(new LocalBackupStore(Repo), RandomNumberGenerator.GetBytes(32));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => wrong.BackupAsync(config));
        config.DeviceId = "different";
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.BackupAsync(config));
        string chunk = Assert.Single(Directory.GetFiles(Path.Combine(Repo, "chunks")));
        byte[] contents = await File.ReadAllBytesAsync(chunk); contents[^1] ^= 1; await File.WriteAllBytesAsync(chunk, contents);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => engine.RestoreAsync(first.SnapshotId, Restore));
        Assert.Single(await engine.ListSnapshotsAsync());
    }

    private sealed class CountChunkReads(IBackupStore inner) : IBackupStore
    {
        public int ChunkReads { get; private set; }
        public Task<bool> ExistsAsync(string name, CancellationToken ct = default) => inner.ExistsAsync(name, ct);
        public Task<Stream> OpenReadAsync(string name, CancellationToken ct = default)
        {
            if (name.StartsWith("chunks/", StringComparison.Ordinal)) ChunkReads++;
            return inner.OpenReadAsync(name, ct);
        }
        public Task PutAsync(string name, Stream content, bool overwrite = false, CancellationToken ct = default) => inner.PutAsync(name, content, overwrite, ct);
        public Task DeleteAsync(string name, CancellationToken ct = default) => inner.DeleteAsync(name, ct);
        public IAsyncEnumerable<StoredObject> ListAsync(string prefix, CancellationToken ct = default) => inner.ListAsync(prefix, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Fact]
    public async Task UnchangedChunksAreReusedAndSourceFoldersSurviveReordering()
    {
        string other = Path.Combine(root, "other"); Directory.CreateDirectory(other);
        await File.WriteAllTextAsync(Path.Combine(Source, "a"), "one");
        await File.WriteAllTextAsync(Path.Combine(other, "b"), "two");
        var config = Config(); config.Sources[0].Label = "Documents";
        config.Sources.Add(new BackupSource { Path = other, Label = "Pictures" });
        await Engine().BackupAsync(config);
        config.Sources.Reverse();
        var counting = new CountChunkReads(new LocalBackupStore(Repo));
        var engine = new BackupEngine(counting, key); var snapshot = await engine.BackupAsync(config);
        Assert.Equal(0, counting.ChunkReads);
        await engine.RestoreAsync(snapshot.SnapshotId, Restore);
        Assert.Single(Directory.GetDirectories(Restore, "Documents-*"));
        Assert.Single(Directory.GetDirectories(Restore, "Pictures-*"));
        Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(Assert.Single(Directory.GetDirectories(Restore, "Documents-*")), "a")));
        Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(Assert.Single(Directory.GetDirectories(Restore, "Pictures-*")), "b")));
    }
    private sealed class CancelAfterChunk(IBackupStore inner, CancellationTokenSource source) : IBackupStore
    {
        public Task<bool> ExistsAsync(string name, CancellationToken ct = default) => inner.ExistsAsync(name, ct);
        public Task<Stream> OpenReadAsync(string name, CancellationToken ct = default) => inner.OpenReadAsync(name, ct);
        public async Task PutAsync(string name, Stream content, bool overwrite = false, CancellationToken ct = default)
        {
            await inner.PutAsync(name, content, overwrite, ct);
            if (name.StartsWith("chunks/", StringComparison.Ordinal)) source.Cancel();
        }
        public Task DeleteAsync(string name, CancellationToken ct = default) => inner.DeleteAsync(name, ct);
        public IAsyncEnumerable<StoredObject> ListAsync(string prefix, CancellationToken ct = default) => inner.ListAsync(prefix, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Fact]
    public async Task CancellationAfterStagingChunkKeepsPreviousSnapshot()
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "a"), "original");
        var config = Config(); var first = await Engine().BackupAsync(config);
        await File.WriteAllBytesAsync(Path.Combine(Source, "a"), RandomNumberGenerator.GetBytes(5 * 1024 * 1024));
        using var cts = new CancellationTokenSource();
        var interrupted = new BackupEngine(new CancelAfterChunk(new LocalBackupStore(Repo), cts), key);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted.BackupAsync(config, ct: cts.Token));
        Assert.Equal(first.SnapshotId, Assert.Single(await Engine().ListSnapshotsAsync()).Id);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(Repo, "chunks")).Length); // Retain the durable resume checkpoint.
        await Engine().RestoreAsync(first.SnapshotId, Restore);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(SourceFolder(Restore), "a")));
    }
    [Fact]
    public async Task AuthenticatedTraversalManifestIsRejected()
    {
        string id = "20261107000000000-" + Guid.NewGuid().ToString("N");
        byte[] plain = System.Text.Encoding.UTF8.GetBytes("{\"Version\":1,\"Id\":\"" + id + "\",\"CreatedUtc\":\"2026-11-07T00:00:00Z\",\"DeviceName\":\"fixture\",\"Entries\":[{\"Path\":\"../escaped\",\"Directory\":false,\"Length\":0,\"Chunks\":[]}]}");
        byte[] encrypted = new byte[32 + plain.Length];
        "PAB1"u8.CopyTo(encrypted); RandomNumberGenerator.Fill(encrypted.AsSpan(4, 12));
        byte[] encryptionKey = HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes("PCAutobackup encryption v1"));
        using (var aes = new AesGcm(encryptionKey, 16))
            aes.Encrypt(encrypted.AsSpan(4, 12), plain, encrypted.AsSpan(32), encrypted.AsSpan(16, 16), System.Text.Encoding.UTF8.GetBytes("manifest:" + id));
        Directory.CreateDirectory(Path.Combine(Repo, "snapshots"));
        await File.WriteAllBytesAsync(Path.Combine(Repo, "snapshots", id + ".manifest"), encrypted);
        await Assert.ThrowsAsync<InvalidDataException>(() => Engine().RestoreAsync(id, Restore));
        Assert.False(File.Exists(Path.Combine(root, "escaped")));
    }
    [Fact]
    public async Task RefusesExistingRestoreContentsAndRepositoryOverlap()
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "a"), "data");
        var engine = Engine(); var config = Config(); var first = await engine.BackupAsync(config);
        Directory.CreateDirectory(Restore); await File.WriteAllTextAsync(Path.Combine(Restore, "keep"), "keep");
        await Assert.ThrowsAsync<IOException>(() => engine.RestoreAsync(first.SnapshotId, Restore));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(Restore, "keep")));
        config.Sources[0].Path = root;
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.BackupAsync(config));
    }
}
