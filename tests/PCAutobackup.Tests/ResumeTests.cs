using System.Security.Cryptography;
using PCAutobackup.Core;

namespace PCAutobackup.Tests;

public sealed class ResumeTests : IDisposable
{
    private const int ChunkSize = 4 * 1024 * 1024;
    private readonly string root = Path.Combine(Path.GetTempPath(), "PCAutobackup-tests", Guid.NewGuid().ToString("N"));
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    private string Source => Path.Combine(root, "source");
    private string Repo => Path.Combine(root, "repo");
    public ResumeTests() => Directory.CreateDirectory(Source);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private AppConfiguration Config() => new() { DeviceId = "resume-device", Sources = [new BackupSource { Id = "source", Path = Source }] };
    private BackupEngine Engine(IBackupStore? store = null) => new(store ?? new LocalBackupStore(Repo), key);

    private sealed class InstrumentedStore(IBackupStore inner) : IBackupStore
    {
        public int ChunkPuts { get; private set; }
        public int ChunkReads { get; private set; }
        public bool FailAfterFirstChunk { get; init; }
        public CancellationTokenSource? CancelAfterFirstChunk { get; init; }
        public Task<bool> ExistsAsync(string name, CancellationToken ct = default) => inner.ExistsAsync(name, ct);
        public Task<Stream> OpenReadAsync(string name, CancellationToken ct = default)
        {
            if (name.StartsWith("chunks/", StringComparison.Ordinal)) ChunkReads++;
            return inner.OpenReadAsync(name, ct);
        }
        public async Task PutAsync(string name, Stream content, bool overwrite = false, CancellationToken ct = default)
        {
            await inner.PutAsync(name, content, overwrite, ct);
            if (name.StartsWith("chunks/", StringComparison.Ordinal))
            {
                ChunkPuts++;
                if (ChunkPuts == 1) CancelAfterFirstChunk?.Cancel();
                if (FailAfterFirstChunk && ChunkPuts == 1) throw new IOException("Simulated lost acknowledgement");
            }
        }
        public Task DeleteAsync(string name, CancellationToken ct = default) => inner.DeleteAsync(name, ct);
        public IAsyncEnumerable<StoredObject> ListAsync(string prefix, CancellationToken ct = default) => inner.ListAsync(prefix, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Fact]
    public async Task InterruptedUploadResumesDurableChunksAndRestores()
    {
        byte[] contents = RandomNumberGenerator.GetBytes(ChunkSize * 2 + 71);
        await File.WriteAllBytesAsync(Path.Combine(Source, "file.bin"), contents);
        var interrupted = new InstrumentedStore(new LocalBackupStore(Repo)) { FailAfterFirstChunk = true };
        await Assert.ThrowsAsync<IOException>(() => Engine(interrupted).BackupAsync(Config()));
        Assert.Empty(await Engine().ListSnapshotsAsync());
        Assert.Single(Directory.GetFiles(Path.Combine(Repo, "chunks")));
        var resumed = new InstrumentedStore(new LocalBackupStore(Repo));
        var result = await Engine(resumed).BackupAsync(Config());
        Assert.Equal(2, resumed.ChunkPuts);
        Assert.Equal(1, resumed.ChunkReads);
        string restore = Path.Combine(root, "restore");
        await Engine().RestoreAsync(result.SnapshotId, restore);
        Assert.Equal(contents, await File.ReadAllBytesAsync(Path.Combine(Assert.Single(Directory.GetDirectories(restore)), "file.bin")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSourceOrCorruptedOrphanDoesNotPublishStaleBytes(bool corrupt)
    {
        byte[] original = RandomNumberGenerator.GetBytes(ChunkSize + 12);
        string path = Path.Combine(Source, "file.bin");
        await File.WriteAllBytesAsync(path, original);
        DateTime timestamp = File.GetLastWriteTimeUtc(path);
        await Assert.ThrowsAsync<IOException>(() => Engine(new InstrumentedStore(new LocalBackupStore(Repo)) { FailAfterFirstChunk = true }).BackupAsync(Config()));
        string orphan = Assert.Single(Directory.GetFiles(Path.Combine(Repo, "chunks")));
        byte[] expected = original.ToArray();
        if (corrupt)
        {
            byte[] encrypted = await File.ReadAllBytesAsync(orphan); encrypted[^1] ^= 1;
            await File.WriteAllBytesAsync(orphan, encrypted);
        }
        else
        {
            expected[0] ^= 1;
            await File.WriteAllBytesAsync(path, expected);
            File.SetLastWriteTimeUtc(path, timestamp);
        }
        var result = await Engine().BackupAsync(Config());
        string restore = Path.Combine(root, "restore");
        await Engine().RestoreAsync(result.SnapshotId, restore);
        Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(Assert.Single(Directory.GetDirectories(restore)), "file.bin")));
        if (!corrupt) Assert.False(File.Exists(orphan));
    }
    [Fact]
    public async Task CancellationPreservesPreviousSnapshotAndNewEngineResumes()
    {
        string path = Path.Combine(Source, "file.bin");
        byte[] old = RandomNumberGenerator.GetBytes(1024);
        byte[] next = RandomNumberGenerator.GetBytes(ChunkSize + 97);
        var config = Config();
        await File.WriteAllBytesAsync(path, old);
        var previous = await Engine().BackupAsync(config);
        await File.WriteAllBytesAsync(path, next);
        using var cancellation = new CancellationTokenSource();
        var interrupted = new InstrumentedStore(new LocalBackupStore(Repo)) { CancelAfterFirstChunk = cancellation };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine(interrupted).BackupAsync(config, ct: cancellation.Token));
        Assert.Equal(previous.SnapshotId, Assert.Single(await Engine().ListSnapshotsAsync()).Id);
        string oldRestore = Path.Combine(root, "restore-old");
        await Engine().RestoreAsync(previous.SnapshotId, oldRestore);
        Assert.Equal(old, await File.ReadAllBytesAsync(Path.Combine(Assert.Single(Directory.GetDirectories(oldRestore)), "file.bin")));
        var resumed = new InstrumentedStore(new LocalBackupStore(Repo));
        var current = await Engine(resumed).BackupAsync(config);
        Assert.Equal(1, resumed.ChunkReads);
        Assert.Equal(1, resumed.ChunkPuts);
        string newRestore = Path.Combine(root, "restore-new");
        await Engine().RestoreAsync(current.SnapshotId, newRestore);
        Assert.Equal(next, await File.ReadAllBytesAsync(Path.Combine(Assert.Single(Directory.GetDirectories(newRestore)), "file.bin")));
    }

    [Fact]
    public async Task QuotaFailureKeepsCheckpointAndPreviousSnapshotUntilSuccessfulRetry()
    {
        string path = Path.Combine(Source, "file.bin");
        byte[] old = RandomNumberGenerator.GetBytes(1024);
        byte[] next = RandomNumberGenerator.GetBytes(ChunkSize + 97);
        var config = Config();
        await File.WriteAllBytesAsync(path, old);
        var previous = await Engine().BackupAsync(config);
        await File.WriteAllBytesAsync(path, next);
        config.RetentionDays = 0;
        config.QuotaBytes = 1;
        var interrupted = new InstrumentedStore(new LocalBackupStore(Repo));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Engine(interrupted).BackupAsync(config));
        Assert.Equal(2, interrupted.ChunkPuts);
        Assert.Equal(previous.SnapshotId, Assert.Single(await Engine().ListSnapshotsAsync()).Id);
        string oldRestore = Path.Combine(root, "restore-old");
        await Engine().RestoreAsync(previous.SnapshotId, oldRestore);
        Assert.Equal(old, await File.ReadAllBytesAsync(Path.Combine(Assert.Single(Directory.GetDirectories(oldRestore)), "file.bin")));
        config.QuotaBytes = null;
        var resumed = new InstrumentedStore(new LocalBackupStore(Repo));
        var current = await Engine(resumed).BackupAsync(config);
        Assert.Equal(0, resumed.ChunkPuts);
        Assert.Equal(2, resumed.ChunkReads);
        Assert.Equal(current.SnapshotId, Assert.Single(await Engine().ListSnapshotsAsync()).Id);
        string newRestore = Path.Combine(root, "restore-new");
        await Engine().RestoreAsync(current.SnapshotId, newRestore);
        Assert.Equal(next, await File.ReadAllBytesAsync(Path.Combine(Assert.Single(Directory.GetDirectories(newRestore)), "file.bin")));
    }

}
