using System.Security.Cryptography;
using PCAutobackup.Core;

namespace PCAutobackup.Tests;

public sealed class ParallelBackupTests : IDisposable
{
    private const int ChunkSize = 4 * 1024 * 1024;
    private readonly string root = Path.Combine(Path.GetTempPath(), "PCAutobackup-tests", Guid.NewGuid().ToString("N"));
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    private string Source => Path.Combine(root, "source");
    private string Repo => Path.Combine(root, "repo");
    public ParallelBackupTests() => Directory.CreateDirectory(Source);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private AppConfiguration Config() => new() { DeviceId = "parallel-test", Sources = [new BackupSource { Id = "source", Path = Source }] };

    private sealed class GatedStore(IBackupStore inner, bool fail = false, bool sustained = false) : IBackupStore
    {
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource nextPair = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int active, maximum, puts;
        public int Active => Volatile.Read(ref active);
        public int Maximum => Volatile.Read(ref maximum);
        public int Puts => Volatile.Read(ref puts);
        public Task<bool> ExistsAsync(string name, CancellationToken ct = default) => inner.ExistsAsync(name, ct);
        public Task<Stream> OpenReadAsync(string name, CancellationToken ct = default) => inner.OpenReadAsync(name, ct);
        public async Task PutAsync(string name, Stream content, bool overwrite = false, CancellationToken ct = default)
        {
            if (!name.StartsWith("chunks/", StringComparison.Ordinal)) { await inner.PutAsync(name, content, overwrite, ct); return; }
            int current = Interlocked.Increment(ref active);
            Started.TrySetResult();
            InterlockedExtensions.Max(ref maximum, current);
            int number = Interlocked.Increment(ref puts);
            try
            {
                if (number == 2) gate.TrySetResult();
                await gate.Task.WaitAsync(ct);
                if (sustained && number == 4) nextPair.TrySetResult();
                if (sustained && number == 3) await nextPair.Task.WaitAsync(ct);
                if (fail && number == 1) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                if (fail && number == 2) throw new IOException("simulated upload failure");
                await inner.PutAsync(name, content, overwrite, ct);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public Task DeleteAsync(string name, CancellationToken ct = default) => inner.DeleteAsync(name, ct);
        public IAsyncEnumerable<StoredObject> ListAsync(string prefix, CancellationToken ct = default) => inner.ListAsync(prefix, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            int observed;
            while ((observed = Volatile.Read(ref location)) < value && Interlocked.CompareExchange(ref location, value, observed) != observed) { }
        }
    }
    [Fact]
    public async Task SixteenSmallFilesStayConcurrentAndRestore()
    {
        for (int i = 0; i < 48; i++)
            await File.WriteAllBytesAsync(Path.Combine(Source, i + ".bin"), RandomNumberGenerator.GetBytes(512));
        var store = new FourSlotStore(new LocalBackupStore(Repo), "success", 16);
        var engine = new BackupEngine(store, key, maxConcurrentChunks: 16);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await engine.BackupAsync(Config(), ct: cancellation.Token);
        Assert.Equal(16, store.Maximum); Assert.Equal(48, store.Puts); Assert.Equal(0, store.Active);
        string destination = Path.Combine(root, "restore-many-small");
        await engine.RestoreAsync(result.SnapshotId, destination);
        string restored = Assert.Single(Directory.GetDirectories(destination));
        foreach (string file in Directory.GetFiles(Source))
            Assert.Equal(await File.ReadAllBytesAsync(file), await File.ReadAllBytesAsync(Path.Combine(restored, Path.GetFileName(file))));
    }

    [Fact]
    public async Task SmallFilesOverlapAcrossBoundariesAndRestoreWithoutDuplicates()
    {
        byte[] a = RandomNumberGenerator.GetBytes(113), b = RandomNumberGenerator.GetBytes(227);
        await File.WriteAllBytesAsync(Path.Combine(Source, "a"), a);
        await File.WriteAllBytesAsync(Path.Combine(Source, "b"), b);
        await File.WriteAllBytesAsync(Path.Combine(Source, "copy"), a);
        var store = new GatedStore(new LocalBackupStore(Repo));
        var engine = new BackupEngine(store, key, maxConcurrentChunks: 2);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await engine.BackupAsync(Config(), ct: cancellation.Token);
        Assert.Equal(2, store.Maximum); Assert.Equal(2, store.Puts); Assert.Equal(0, store.Active);
        Assert.Equal(3, result.FileCount);
        string destination = Path.Combine(root, "restore-small");
        await engine.RestoreAsync(result.SnapshotId, destination);
        string restored = Assert.Single(Directory.GetDirectories(destination));
        Assert.Equal(a, await File.ReadAllBytesAsync(Path.Combine(restored, "a")));
        Assert.Equal(b, await File.ReadAllBytesAsync(Path.Combine(restored, "b")));
        Assert.Equal(a, await File.ReadAllBytesAsync(Path.Combine(restored, "copy")));
    }

    [Fact]
    public async Task SmallFileUploadFailureDoesNotCommitSnapshot()
    {
        await File.WriteAllBytesAsync(Path.Combine(Source, "a"), RandomNumberGenerator.GetBytes(113));
        await File.WriteAllBytesAsync(Path.Combine(Source, "b"), RandomNumberGenerator.GetBytes(227));
        var store = new GatedStore(new LocalBackupStore(Repo), fail: true);
        var engine = new BackupEngine(store, key, maxConcurrentChunks: 2);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAsync<IOException>(() => engine.BackupAsync(Config(), ct: cancellation.Token));
        Assert.Equal(0, store.Active);
        Assert.Empty(await engine.ListSnapshotsAsync());
    }

    [Fact]
    public async Task TwoChunksRunConcurrentlyDeduplicateAndRestore()
    {
        byte[] a = RandomNumberGenerator.GetBytes(ChunkSize), b = RandomNumberGenerator.GetBytes(ChunkSize);
        string file = Path.Combine(Source, "data.bin");
        await using (var output = File.Create(file)) { await output.WriteAsync(a); await output.WriteAsync(b); await output.WriteAsync(a); }
        var store = new GatedStore(new LocalBackupStore(Repo));
        var engine = new BackupEngine(store, key, maxConcurrentChunks: 2);
        var result = await engine.BackupAsync(Config()).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, store.Maximum); Assert.Equal(2, store.Puts); Assert.Equal(0, store.Active);
        string destination = Path.Combine(root, "restore");
        await engine.RestoreAsync(result.SnapshotId, destination);
        string restored = Path.Combine(Assert.Single(Directory.GetDirectories(destination)), "data.bin");
        Assert.Equal(SHA256.HashData(await File.ReadAllBytesAsync(file)), SHA256.HashData(await File.ReadAllBytesAsync(restored)));
    }
    [Fact]
    public async Task CompletedLaterChunkFreesSlotWhileEarlierChunkWaits()
    {
        await File.WriteAllBytesAsync(Path.Combine(Source, "data.bin"), RandomNumberGenerator.GetBytes(ChunkSize * 6));
        var store = new GatedStore(new LocalBackupStore(Repo), sustained: true);
        var engine = new BackupEngine(store, key, maxConcurrentChunks: 2);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await engine.BackupAsync(Config(), ct: cancellation.Token);
        Assert.Equal(6, store.Puts);
        Assert.Equal(2, store.Maximum);
        Assert.Equal(0, store.Active);
        Assert.Single(await engine.ListSnapshotsAsync());
    }
    [Fact]
    public async Task FailedUploadSettlesAllTasksWithoutPublishingManifest()
    {
        await File.WriteAllBytesAsync(Path.Combine(Source, "data.bin"), RandomNumberGenerator.GetBytes(ChunkSize * 2));
        var store = new GatedStore(new LocalBackupStore(Repo), fail: true);
        var engine = new BackupEngine(store, key, maxConcurrentChunks: 2);
        var error = await Assert.ThrowsAsync<IOException>(() => engine.BackupAsync(Config()).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("simulated upload failure", error.Message);
        Assert.Equal(2, store.Maximum); Assert.Equal(0, store.Active);
        Assert.Empty(await engine.ListSnapshotsAsync());
    }
    [Fact]
    public async Task CancellationSettlesAllTasksWithoutPublishingManifest()
    {
        await File.WriteAllBytesAsync(Path.Combine(Source, "data.bin"), RandomNumberGenerator.GetBytes(ChunkSize));
        var store = new GatedStore(new LocalBackupStore(Repo));
        var engine = new BackupEngine(store, key, maxConcurrentChunks: 2);
        using var cancellation = new CancellationTokenSource();
        Task backup = engine.BackupAsync(Config(), ct: cancellation.Token);
        await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backup.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, store.Active); Assert.Empty(await engine.ListSnapshotsAsync());
    }
    private sealed class FourSlotStore(IBackupStore inner, string mode, int slots = 4) : IBackupStore
    {
        private readonly TaskCompletionSource firstFour = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource seventh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task AllStarted => firstFour.Task;
        private int active, maximum, puts;
        public int Active => Volatile.Read(ref active);
        public int Maximum => Volatile.Read(ref maximum);
        public int Puts => Volatile.Read(ref puts);
        public Task<bool> ExistsAsync(string name, CancellationToken ct = default) => inner.ExistsAsync(name, ct);
        public Task<Stream> OpenReadAsync(string name, CancellationToken ct = default) => inner.OpenReadAsync(name, ct);
        public async Task PutAsync(string name, Stream content, bool overwrite = false, CancellationToken ct = default)
        {
            if (!name.StartsWith("chunks/", StringComparison.Ordinal)) { await inner.PutAsync(name, content, overwrite, ct); return; }
            int current = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maximum, current);
            int number = Interlocked.Increment(ref puts);
            try
            {
                if (number == slots) firstFour.TrySetResult();
                await firstFour.Task.WaitAsync(ct);
                if (mode == "cancel" || (mode == "failure" && number != slots)) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                if (mode == "failure") throw new IOException("simulated upload failure");
                if (number == slots * 2 - 1) seventh.TrySetResult();
                if (number == 1) await seventh.Task.WaitAsync(ct);
                await inner.PutAsync(name, content, overwrite, ct);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public Task DeleteAsync(string name, CancellationToken ct = default) => inner.DeleteAsync(name, ct);
        public IAsyncEnumerable<StoredObject> ListAsync(string prefix, CancellationToken ct = default) => inner.ListAsync(prefix, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Fact]
    public async Task FourChunksStayBusyWhileEarlierChunkWaits()
    {
        await File.WriteAllBytesAsync(Path.Combine(Source, "data.bin"), RandomNumberGenerator.GetBytes(ChunkSize * 12));
        var store = new FourSlotStore(new LocalBackupStore(Repo), "success");
        var engine = new BackupEngine(store, key, maxConcurrentChunks: 4);
        await engine.BackupAsync(Config()).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(12, store.Puts); Assert.Equal(4, store.Maximum); Assert.Equal(0, store.Active);
        Assert.Single(await engine.ListSnapshotsAsync());
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task FourUploadsDrainWithoutPublishingOnFailureOrCancellation(string mode)
    {
        await File.WriteAllBytesAsync(Path.Combine(Source, "data.bin"), RandomNumberGenerator.GetBytes(ChunkSize * 4));
        var store = new FourSlotStore(new LocalBackupStore(Repo), mode);
        var engine = new BackupEngine(store, key, maxConcurrentChunks: 4);
        using var cancellation = new CancellationTokenSource();
        Task backup = engine.BackupAsync(Config(), ct: cancellation.Token);
        await store.AllStarted.WaitAsync(TimeSpan.FromSeconds(60));
        if (mode == "cancel") cancellation.Cancel();
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => backup.WaitAsync(TimeSpan.FromSeconds(60)));
        if (mode == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Equal("simulated upload failure", Assert.IsType<IOException>(error).Message);
        Assert.Equal(4, store.Maximum); Assert.Equal(0, store.Active); Assert.Empty(await engine.ListSnapshotsAsync());
    }
    [Fact]
    public async Task EightChunksStayBusyAndRestoreAcrossThreeWindows()
    {
        string file = Path.Combine(Source, "data.bin");
        await using (var output = File.Create(file))
            for (int i = 0; i < 24; i++) await output.WriteAsync(RandomNumberGenerator.GetBytes(ChunkSize));
        var store = new FourSlotStore(new LocalBackupStore(Repo), "success", 8);
        var engine = new BackupEngine(store, key, maxConcurrentChunks: 8);
        var result = await engine.BackupAsync(Config()).WaitAsync(TimeSpan.FromSeconds(120));
        Assert.Equal(24, store.Puts); Assert.Equal(8, store.Maximum); Assert.Equal(0, store.Active);
        string destination = Path.Combine(root, "restore");
        await engine.RestoreAsync(result.SnapshotId, destination).WaitAsync(TimeSpan.FromSeconds(120));
        string restored = Assert.Single(Directory.GetFiles(destination, "*", SearchOption.AllDirectories));
        await using var originalStream = File.OpenRead(file);
        await using var restoredStream = File.OpenRead(restored);
        Assert.Equal(await SHA256.HashDataAsync(originalStream), await SHA256.HashDataAsync(restoredStream));
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task EightUploadsDrainWithoutPublishingOnFailureOrCancellation(string mode)
    {
        string file = Path.Combine(Source, "data.bin");
        await using (var output = File.Create(file))
            for (int i = 0; i < 8; i++) await output.WriteAsync(RandomNumberGenerator.GetBytes(ChunkSize));
        var store = new FourSlotStore(new LocalBackupStore(Repo), mode, 8);
        var engine = new BackupEngine(store, key, maxConcurrentChunks: 8);
        using var cancellation = new CancellationTokenSource();
        Task backup = engine.BackupAsync(Config(), ct: cancellation.Token);
        await store.AllStarted.WaitAsync(TimeSpan.FromSeconds(60));
        if (mode == "cancel") cancellation.Cancel();
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => backup.WaitAsync(TimeSpan.FromSeconds(60)));
        if (mode == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Equal("simulated upload failure", Assert.IsType<IOException>(error).Message);
        Assert.Equal(8, store.Maximum); Assert.Equal(0, store.Active); Assert.Empty(await engine.ListSnapshotsAsync());
    }
    [Fact]
    public async Task SixteenChunksStayBusyAndRestoreAcrossThreeWindows()
    {
        string file = Path.Combine(Source, "data.bin");
        await using (var output = File.Create(file))
            for (int i = 0; i < 48; i++) await output.WriteAsync(RandomNumberGenerator.GetBytes(ChunkSize));
        var store = new FourSlotStore(new LocalBackupStore(Repo), "success", 16);
        var engine = new BackupEngine(store, key, maxConcurrentChunks: 16);
        var result = await engine.BackupAsync(Config()).WaitAsync(TimeSpan.FromSeconds(120));
        Assert.Equal(48, store.Puts); Assert.Equal(16, store.Maximum); Assert.Equal(0, store.Active);
        string destination = Path.Combine(root, "restore");
        await engine.RestoreAsync(result.SnapshotId, destination).WaitAsync(TimeSpan.FromSeconds(120));
        string restored = Assert.Single(Directory.GetFiles(destination, "*", SearchOption.AllDirectories));
        await using var originalStream = File.OpenRead(file);
        await using var restoredStream = File.OpenRead(restored);
        Assert.Equal(await SHA256.HashDataAsync(originalStream), await SHA256.HashDataAsync(restoredStream));
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task SixteenUploadsDrainWithoutPublishingOnFailureOrCancellation(string mode)
    {
        string file = Path.Combine(Source, "data.bin");
        await using (var output = File.Create(file))
            for (int i = 0; i < 16; i++) await output.WriteAsync(RandomNumberGenerator.GetBytes(ChunkSize));
        var store = new FourSlotStore(new LocalBackupStore(Repo), mode, 16);
        var engine = new BackupEngine(store, key, maxConcurrentChunks: 16);
        using var cancellation = new CancellationTokenSource();
        Task backup = engine.BackupAsync(Config(), ct: cancellation.Token);
        await store.AllStarted.WaitAsync(TimeSpan.FromSeconds(60));
        if (mode == "cancel") cancellation.Cancel();
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => backup.WaitAsync(TimeSpan.FromSeconds(60)));
        if (mode == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Equal("simulated upload failure", Assert.IsType<IOException>(error).Message);
        Assert.Equal(16, store.Maximum); Assert.Equal(0, store.Active); Assert.Empty(await engine.ListSnapshotsAsync());
    }
    [Fact]
    public async Task ThirtyTwoChunksStayBusyAndRestoreAcrossThreeWindows()
    {
        string file = Path.Combine(Source, "data.bin");
        await using (var output = File.Create(file))
            for (int i = 0; i < 96; i++) await output.WriteAsync(RandomNumberGenerator.GetBytes(ChunkSize));
        var store = new FourSlotStore(new LocalBackupStore(Repo), "success", 32);
        var engine = new BackupEngine(store, key, maxConcurrentChunks: 32);
        var result = await engine.BackupAsync(Config()).WaitAsync(TimeSpan.FromSeconds(120));
        Assert.Equal(96, store.Puts); Assert.Equal(32, store.Maximum); Assert.Equal(0, store.Active);
        string destination = Path.Combine(root, "restore");
        await engine.RestoreAsync(result.SnapshotId, destination).WaitAsync(TimeSpan.FromSeconds(120));
        string restored = Assert.Single(Directory.GetFiles(destination, "*", SearchOption.AllDirectories));
        await using var originalStream = File.OpenRead(file);
        await using var restoredStream = File.OpenRead(restored);
        Assert.Equal(await SHA256.HashDataAsync(originalStream), await SHA256.HashDataAsync(restoredStream));
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task ThirtyTwoUploadsDrainWithoutPublishingOnFailureOrCancellation(string mode)
    {
        string file = Path.Combine(Source, "data.bin");
        await using (var output = File.Create(file))
            for (int i = 0; i < 32; i++) await output.WriteAsync(RandomNumberGenerator.GetBytes(ChunkSize));
        var store = new FourSlotStore(new LocalBackupStore(Repo), mode, 32);
        var engine = new BackupEngine(store, key, maxConcurrentChunks: 32);
        using var cancellation = new CancellationTokenSource();
        Task backup = engine.BackupAsync(Config(), ct: cancellation.Token);
        await store.AllStarted.WaitAsync(TimeSpan.FromSeconds(60));
        if (mode == "cancel") cancellation.Cancel();
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => backup.WaitAsync(TimeSpan.FromSeconds(60)));
        if (mode == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Equal("simulated upload failure", Assert.IsType<IOException>(error).Message);
        Assert.Equal(32, store.Maximum); Assert.Equal(0, store.Active); Assert.Empty(await engine.ListSnapshotsAsync());
    }
    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public void RejectsUnsupportedConcurrency(int concurrency) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackupEngine(new LocalBackupStore(Repo), key, maxConcurrentChunks: concurrency));
}
