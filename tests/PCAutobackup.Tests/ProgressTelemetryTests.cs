using System.Security.Cryptography;
using PCAutobackup.Core;

namespace PCAutobackup.Tests;

public sealed class ProgressTelemetryTests : IDisposable
{
    private const int ChunkSize = 4 * 1024 * 1024;
    private readonly string root = Path.Combine(Path.GetTempPath(), "PCAutobackup-tests", Guid.NewGuid().ToString("N"));
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    private string Source => Path.Combine(root, "source");
    private string Repo => Path.Combine(root, "repo");
    public ProgressTelemetryTests() => Directory.CreateDirectory(Source);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private BackupEngine Engine() => new(new LocalBackupStore(Repo), key, maxConcurrentChunks: 16, maxConcurrentRestoreChunks: 16);
    private AppConfiguration Config() => new() { DeviceId = "progress-test", Sources = [new BackupSource { Id = "source", Path = Source }] };
    private sealed class InlineProgress(Action<BackupProgress> action) : IProgress<BackupProgress>
    { public void Report(BackupProgress value) => action(value); }

    [Fact]
    public async Task ReportsChunkProgressAndTotalsForBackupAndRestore()
    {
        string file = Path.Combine(Source, "data.bin");
        await using (var output = File.Create(file))
            for (int i = 0; i < 3; i++) await output.WriteAsync(RandomNumberGenerator.GetBytes(ChunkSize));
        long total = new FileInfo(file).Length;
        var engine = Engine();
        var backupEvents = new List<BackupProgress>();
        var snapshot = await engine.BackupAsync(Config(), new InlineProgress(backupEvents.Add));
        Assert.Equal("scanning", backupEvents[0].Stage);
        Assert.Null(backupEvents[0].TotalBytes);
        Assert.Contains(backupEvents, p => p.Stage == "backup" && p.TotalBytes == total && p.BytesProcessed > 0 && p.BytesProcessed < total);
        Assert.Equal("finalizing", backupEvents[^1].Stage);
        Assert.Equal(total, backupEvents[^1].BytesProcessed);
        Assert.All(backupEvents.Where(p => p.Stage != "scanning"), p => Assert.Equal(total, p.TotalBytes));
        Assert.Equal(backupEvents.Select(p => p.BytesProcessed).Order(), backupEvents.Select(p => p.BytesProcessed));

        var restoreEvents = new List<BackupProgress>();
        await engine.RestoreAsync(snapshot.SnapshotId, Path.Combine(root, "restore"), new InlineProgress(restoreEvents.Add));
        Assert.Contains(restoreEvents, p => p.TotalBytes == total && p.BytesProcessed > 0 && p.BytesProcessed < total);
        Assert.Equal(total, restoreEvents[^1].BytesProcessed);
        Assert.All(restoreEvents, p => Assert.Equal(total, p.TotalBytes));
        Assert.Equal(restoreEvents.Select(p => p.BytesProcessed).Order(), restoreEvents.Select(p => p.BytesProcessed));
    }

    [Fact]
    public async Task EmptySourceHasKnownZeroTotal()
    {
        var engine = Engine();
        var events = new List<BackupProgress>();
        var snapshot = await engine.BackupAsync(Config(), new InlineProgress(events.Add));
        Assert.Null(events[0].TotalBytes);
        Assert.Equal(0, events[^1].TotalBytes);
        events.Clear();
        await engine.RestoreAsync(snapshot.SnapshotId, Path.Combine(root, "restore"), new InlineProgress(events.Add));
        Assert.Single(events);
        Assert.Equal(0, events[0].TotalBytes);
    }

    [Fact]
    public async Task CancellationDuringBackupDoesNotFinalizeOrPublish()
    {
        await using (var output = File.Create(Path.Combine(Source, "data.bin")))
            for (int i = 0; i < 3; i++) await output.WriteAsync(RandomNumberGenerator.GetBytes(ChunkSize));
        using var cancellation = new CancellationTokenSource();
        var events = new List<BackupProgress>();
        var engine = Engine();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.BackupAsync(Config(), new InlineProgress(p =>
        {
            events.Add(p);
            if (p.BytesProcessed > 0) cancellation.Cancel();
        }), cancellation.Token));
        Assert.Contains(events, p => p.BytesProcessed > 0);
        Assert.DoesNotContain(events, p => p.Stage == "finalizing");
        Assert.Empty(await engine.ListSnapshotsAsync());
    }

    [Fact]
    public async Task CancellationDuringRestoreDeletesPartialFile()
    {
        await using (var output = File.Create(Path.Combine(Source, "data.bin")))
            for (int i = 0; i < 3; i++) await output.WriteAsync(RandomNumberGenerator.GetBytes(ChunkSize));
        var engine = Engine();
        var snapshot = await engine.BackupAsync(Config());
        string destination = Path.Combine(root, "restore");
        using var cancellation = new CancellationTokenSource();
        var events = new List<BackupProgress>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RestoreAsync(snapshot.SnapshotId, destination, new InlineProgress(p =>
        {
            events.Add(p);
            if (p.BytesProcessed > 0) cancellation.Cancel();
        }), cancellation.Token));
        Assert.Contains(events, p => p.BytesProcessed > 0 && p.BytesProcessed < p.TotalBytes);
        Assert.Empty(Directory.GetFiles(destination, "*", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task DuplicateFileReportsProgressWhileSourceIsStillOpen()
    {
        string file = Path.Combine(Source, "repeated.bin");
        await using (var stream = File.Create(file)) stream.SetLength(256L * 1024 * 1024);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        bool reportedWhileOpen = false;
        long reported = 0;
        var engine = Engine();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.BackupAsync(Config(), new InlineProgress(p =>
        {
            if (p.BytesProcessed == 0) return;
            reported = p.BytesProcessed;
            try { using var exclusive = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None); }
            catch (IOException) { reportedWhileOpen = true; }
            cancellation.Cancel();
        }), cancellation.Token));
        Assert.True(reportedWhileOpen);
        Assert.InRange(reported, 1, 256L * 1024 * 1024 - 1);
        Assert.Empty(await engine.ListSnapshotsAsync());
    }

    private sealed class BlockedUploadStore(IBackupStore inner) : IBackupStore
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> ExistsAsync(string name, CancellationToken ct = default) => inner.ExistsAsync(name, ct);
        public Task<Stream> OpenReadAsync(string name, CancellationToken ct = default) => inner.OpenReadAsync(name, ct);
        public async Task PutAsync(string name, Stream content, bool overwrite = false, CancellationToken ct = default)
        {
            if (name.StartsWith("chunks/", StringComparison.Ordinal))
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            await inner.PutAsync(name, content, overwrite, ct);
        }
        public Task DeleteAsync(string name, CancellationToken ct = default) => inner.DeleteAsync(name, ct);
        public IAsyncEnumerable<StoredObject> ListAsync(string prefix, CancellationToken ct = default) => inner.ListAsync(prefix, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Fact]
    public async Task PendingDuplicateOccurrencesAreCountedWhenUploadFinishes()
    {
        string file = Path.Combine(Source, "repeated.bin");
        await using (var stream = File.Create(file)) stream.SetLength(4L * ChunkSize);
        await using var store = new BlockedUploadStore(new LocalBackupStore(Repo));
        var engine = new BackupEngine(store, key, maxConcurrentChunks: 16);
        var events = new List<BackupProgress>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Task<BackupResult> backup = engine.BackupAsync(Config(), new InlineProgress(events.Add), cancellation.Token);
        await store.Started.Task.WaitAsync(cancellation.Token);
        // The source handle closes after EOF, while the upload is still blocked.
        while (true)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            try { using var exclusive = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None); break; }
            catch (IOException) { await Task.Delay(10, cancellation.Token); }
        }
        store.Release.TrySetResult();
        await backup;
        var first = events.First(p => p.BytesProcessed > 0);
        Assert.Equal(4L * ChunkSize, first.BytesProcessed);
        Assert.Equal(4L * ChunkSize, first.TotalBytes);
    }

}
