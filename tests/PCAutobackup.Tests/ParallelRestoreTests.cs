using System.Security.Cryptography;
using PCAutobackup.Core;

namespace PCAutobackup.Tests;

public sealed class ParallelRestoreTests : IDisposable
{
    private const int ChunkSize = 4 * 1024 * 1024;
    private readonly string root = Path.Combine(Path.GetTempPath(), "PCAutobackup-tests", Guid.NewGuid().ToString("N"));
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    private string Source => Path.Combine(root, "source");
    private string Repo => Path.Combine(root, "repo");
    public ParallelRestoreTests() => Directory.CreateDirectory(Source);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class GatedStore(IBackupStore inner, string mode) : IBackupStore
    {
        private readonly TaskCompletionSource firstPair = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource secondPair = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int active, maximum, reads;
        public int Active => Volatile.Read(ref active);
        public int Maximum => Volatile.Read(ref maximum);
        public int Reads => Volatile.Read(ref reads);
        public List<int> Finished { get; } = [];
        public Task<bool> ExistsAsync(string name, CancellationToken ct = default) => inner.ExistsAsync(name, ct);
        public async Task<Stream> OpenReadAsync(string name, CancellationToken ct = default)
        {
            if (!name.StartsWith("chunks/", StringComparison.Ordinal)) return await inner.OpenReadAsync(name, ct);
            int current = Interlocked.Increment(ref active);
            int observed;
            while ((observed = Volatile.Read(ref maximum)) < current && Interlocked.CompareExchange(ref maximum, current, observed) != observed) { }
            int number = Interlocked.Increment(ref reads);
            Started.TrySetResult();
            try
            {
                if (mode == "cancel") await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                if (mode is "failure" or "corrupt")
                {
                    if (number == 1) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    if (number == 2 && mode == "failure") throw new IOException("simulated read failure");
                    if (number == 2)
                    {
                        await using var stream = await inner.OpenReadAsync(name, ct);
                        using var memory = new MemoryStream();
                        await stream.CopyToAsync(memory, ct);
                        byte[] data = memory.ToArray(); data[^1] ^= 1;
                        return new MemoryStream(data, writable: false);
                    }
                }
                if (number == 1) await firstPair.Task.WaitAsync(ct);
                if (number == 3) await secondPair.Task.WaitAsync(ct);
                lock (Finished) Finished.Add(number);
                if (number == 2) firstPair.TrySetResult();
                if (number == 4) secondPair.TrySetResult();
                return await inner.OpenReadAsync(name, ct);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public Task PutAsync(string name, Stream content, bool overwrite = false, CancellationToken ct = default) => inner.PutAsync(name, content, overwrite, ct);
        public Task DeleteAsync(string name, CancellationToken ct = default) => inner.DeleteAsync(name, ct);
        public IAsyncEnumerable<StoredObject> ListAsync(string prefix, CancellationToken ct = default) => inner.ListAsync(prefix, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private async Task<(string Snapshot, string SourceFile)> Prepare(int chunks = 6)
    {
        string file = Path.Combine(Source, "data.bin");
        await using (var output = File.Create(file))
            for (int i = 0; i < chunks; i++) await output.WriteAsync(RandomNumberGenerator.GetBytes(ChunkSize));
        var engine = new BackupEngine(new LocalBackupStore(Repo), key);
        var result = await engine.BackupAsync(new AppConfiguration { DeviceId = "restore-test", Sources = [new BackupSource { Id = "source", Path = Source }] });
        return (result.SnapshotId, file);
    }
    private string Destination => Path.Combine(root, "restore");
    private void AssertNoPartialFile() => Assert.Empty(Directory.GetFiles(Destination, "*", SearchOption.AllDirectories));

    [Fact]
    public async Task TwoChunksPrefetchInOrderWithSustainedConcurrency()
    {
        var fixture = await Prepare();
        var store = new GatedStore(new LocalBackupStore(Repo), "success");
        var engine = new BackupEngine(store, key, maxConcurrentRestoreChunks: 2);
        await engine.RestoreAsync(fixture.Snapshot, Destination).WaitAsync(TimeSpan.FromSeconds(30));
        string restored = Assert.Single(Directory.GetFiles(Destination, "*", SearchOption.AllDirectories));
        Assert.Equal(SHA256.HashData(await File.ReadAllBytesAsync(fixture.SourceFile)), SHA256.HashData(await File.ReadAllBytesAsync(restored)));
        Assert.Equal(6, store.Reads); Assert.Equal(2, store.Maximum); Assert.Equal(0, store.Active);
        Assert.True(store.Finished.IndexOf(2) < store.Finished.IndexOf(1));
        Assert.True(store.Finished.IndexOf(4) < store.Finished.IndexOf(3));
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("corrupt")]
    public async Task FailureCancelsAndDrainsOtherReads(string mode)
    {
        var fixture = await Prepare();
        var store = new GatedStore(new LocalBackupStore(Repo), mode);
        var engine = new BackupEngine(store, key, maxConcurrentRestoreChunks: 2);
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => engine.RestoreAsync(fixture.Snapshot, Destination).WaitAsync(TimeSpan.FromSeconds(30)));
        if (mode == "failure") Assert.Equal("simulated read failure", Assert.IsType<IOException>(error).Message);
        else Assert.IsAssignableFrom<CryptographicException>(error);
        Assert.Equal(2, store.Maximum); Assert.Equal(0, store.Active); AssertNoPartialFile();
    }

    [Fact]
    public async Task CancellationDrainsReadsAndDeletesPartialFile()
    {
        var fixture = await Prepare();
        var store = new GatedStore(new LocalBackupStore(Repo), "cancel");
        var engine = new BackupEngine(store, key, maxConcurrentRestoreChunks: 2);
        using var cancellation = new CancellationTokenSource();
        Task restore = engine.RestoreAsync(fixture.Snapshot, Destination, ct: cancellation.Token);
        await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(30)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, store.Active); AssertNoPartialFile();
    }

    [Fact]
    public async Task ExistingFilesAreNeverOverwritten()
    {
        var fixture = await Prepare();
        Directory.CreateDirectory(Destination);
        string existing = Path.Combine(Destination, "existing.txt");
        await File.WriteAllTextAsync(existing, "untouched");
        var engine = new BackupEngine(new LocalBackupStore(Repo), key, maxConcurrentRestoreChunks: 2);
        await Assert.ThrowsAsync<IOException>(() => engine.RestoreAsync(fixture.Snapshot, Destination));
        Assert.Equal("untouched", await File.ReadAllTextAsync(existing));
    }

    private sealed class FourSlotStore(IBackupStore inner, string mode, int slots = 4) : IBackupStore
    {
        private readonly TaskCompletionSource firstFour = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource eighth = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task AllStarted => firstFour.Task;
        private int active, maximum, reads;
        public int Active => Volatile.Read(ref active);
        public int Maximum => Volatile.Read(ref maximum);
        public int Reads => Volatile.Read(ref reads);
        public List<int> Finished { get; } = [];
        public Task<bool> ExistsAsync(string name, CancellationToken ct = default) => inner.ExistsAsync(name, ct);
        public async Task<Stream> OpenReadAsync(string name, CancellationToken ct = default)
        {
            if (!name.StartsWith("chunks/", StringComparison.Ordinal)) return await inner.OpenReadAsync(name, ct);
            int current = Interlocked.Increment(ref active), observed;
            while ((observed = Volatile.Read(ref maximum)) < current && Interlocked.CompareExchange(ref maximum, current, observed) != observed) { }
            int number = Interlocked.Increment(ref reads);
            try
            {
                if (number == slots) firstFour.TrySetResult();
                await firstFour.Task.WaitAsync(ct);
                if (mode == "cancel" || (mode == "failure" && number != slots)) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                if (mode == "failure") throw new IOException("simulated read failure");
                if (number == slots + 1) await eighth.Task.WaitAsync(ct);
                lock (Finished) Finished.Add(number);
                if (number == slots * 2) eighth.TrySetResult();
                return await inner.OpenReadAsync(name, ct);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public Task PutAsync(string name, Stream content, bool overwrite = false, CancellationToken ct = default) => inner.PutAsync(name, content, overwrite, ct);
        public Task DeleteAsync(string name, CancellationToken ct = default) => inner.DeleteAsync(name, ct);
        public IAsyncEnumerable<StoredObject> ListAsync(string prefix, CancellationToken ct = default) => inner.ListAsync(prefix, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Fact]
    public async Task FourChunksRestoreInOrderAcrossMultipleWindows()
    {
        var fixture = await Prepare(12);
        var store = new FourSlotStore(new LocalBackupStore(Repo), "success");
        var engine = new BackupEngine(store, key, maxConcurrentRestoreChunks: 4);
        await engine.RestoreAsync(fixture.Snapshot, Destination).WaitAsync(TimeSpan.FromSeconds(60));
        string restored = Assert.Single(Directory.GetFiles(Destination, "*", SearchOption.AllDirectories));
        Assert.Equal(SHA256.HashData(await File.ReadAllBytesAsync(fixture.SourceFile)), SHA256.HashData(await File.ReadAllBytesAsync(restored)));
        Assert.Equal(12, store.Reads); Assert.Equal(4, store.Maximum); Assert.Equal(0, store.Active);
        Assert.True(store.Finished.IndexOf(8) < store.Finished.IndexOf(5));
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task FourReadsDrainAndRemovePartialFileOnFailureOrCancellation(string mode)
    {
        var fixture = await Prepare(4);
        var store = new FourSlotStore(new LocalBackupStore(Repo), mode);
        var engine = new BackupEngine(store, key, maxConcurrentRestoreChunks: 4);
        using var cancellation = new CancellationTokenSource();
        Task restore = engine.RestoreAsync(fixture.Snapshot, Destination, ct: cancellation.Token);
        await store.AllStarted.WaitAsync(TimeSpan.FromSeconds(60));
        if (mode == "cancel") cancellation.Cancel();
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => restore.WaitAsync(TimeSpan.FromSeconds(60)));
        if (mode == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Equal("simulated read failure", Assert.IsType<IOException>(error).Message);
        Assert.Equal(4, store.Maximum); Assert.Equal(0, store.Active); AssertNoPartialFile();
    }
    [Fact]
    public async Task EightChunksRestoreInOrderAcrossThreeWindows()
    {
        var fixture = await Prepare(24);
        var store = new FourSlotStore(new LocalBackupStore(Repo), "success", 8);
        var engine = new BackupEngine(store, key, maxConcurrentRestoreChunks: 8);
        await engine.RestoreAsync(fixture.Snapshot, Destination).WaitAsync(TimeSpan.FromSeconds(120));
        string restored = Assert.Single(Directory.GetFiles(Destination, "*", SearchOption.AllDirectories));
        await using var originalStream = File.OpenRead(fixture.SourceFile);
        await using var restoredStream = File.OpenRead(restored);
        Assert.Equal(await SHA256.HashDataAsync(originalStream), await SHA256.HashDataAsync(restoredStream));
        Assert.Equal(24, store.Reads); Assert.Equal(8, store.Maximum); Assert.Equal(0, store.Active);
        Assert.True(store.Finished.IndexOf(16) < store.Finished.IndexOf(9));
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task EightReadsDrainAndRemovePartialFileOnFailureOrCancellation(string mode)
    {
        var fixture = await Prepare(8);
        var store = new FourSlotStore(new LocalBackupStore(Repo), mode, 8);
        var engine = new BackupEngine(store, key, maxConcurrentRestoreChunks: 8);
        using var cancellation = new CancellationTokenSource();
        Task restore = engine.RestoreAsync(fixture.Snapshot, Destination, ct: cancellation.Token);
        await store.AllStarted.WaitAsync(TimeSpan.FromSeconds(60));
        if (mode == "cancel") cancellation.Cancel();
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => restore.WaitAsync(TimeSpan.FromSeconds(60)));
        if (mode == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Equal("simulated read failure", Assert.IsType<IOException>(error).Message);
        Assert.Equal(8, store.Maximum); Assert.Equal(0, store.Active); AssertNoPartialFile();
    }
    [Fact]
    public async Task SixteenChunksRestoreInOrderAcrossThreeWindows()
    {
        var fixture = await Prepare(48);
        var store = new FourSlotStore(new LocalBackupStore(Repo), "success", 16);
        var engine = new BackupEngine(store, key, maxConcurrentRestoreChunks: 16);
        await engine.RestoreAsync(fixture.Snapshot, Destination).WaitAsync(TimeSpan.FromSeconds(120));
        string restored = Assert.Single(Directory.GetFiles(Destination, "*", SearchOption.AllDirectories));
        await using var originalStream = File.OpenRead(fixture.SourceFile);
        await using var restoredStream = File.OpenRead(restored);
        Assert.Equal(await SHA256.HashDataAsync(originalStream), await SHA256.HashDataAsync(restoredStream));
        Assert.Equal(48, store.Reads); Assert.Equal(16, store.Maximum); Assert.Equal(0, store.Active);
        Assert.True(store.Finished.IndexOf(32) < store.Finished.IndexOf(17));
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task SixteenReadsDrainAndRemovePartialFileOnFailureOrCancellation(string mode)
    {
        var fixture = await Prepare(16);
        var store = new FourSlotStore(new LocalBackupStore(Repo), mode, 16);
        var engine = new BackupEngine(store, key, maxConcurrentRestoreChunks: 16);
        using var cancellation = new CancellationTokenSource();
        Task restore = engine.RestoreAsync(fixture.Snapshot, Destination, ct: cancellation.Token);
        await store.AllStarted.WaitAsync(TimeSpan.FromSeconds(60));
        if (mode == "cancel") cancellation.Cancel();
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => restore.WaitAsync(TimeSpan.FromSeconds(60)));
        if (mode == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Equal("simulated read failure", Assert.IsType<IOException>(error).Message);
        Assert.Equal(16, store.Maximum); Assert.Equal(0, store.Active); AssertNoPartialFile();
    }
    [Fact]
    public async Task ThirtyTwoChunksRestoreInOrderAcrossThreeWindows()
    {
        var fixture = await Prepare(96);
        var store = new FourSlotStore(new LocalBackupStore(Repo), "success", 32);
        var engine = new BackupEngine(store, key, maxConcurrentRestoreChunks: 32);
        await engine.RestoreAsync(fixture.Snapshot, Destination).WaitAsync(TimeSpan.FromSeconds(120));
        string restored = Assert.Single(Directory.GetFiles(Destination, "*", SearchOption.AllDirectories));
        await using var originalStream = File.OpenRead(fixture.SourceFile);
        await using var restoredStream = File.OpenRead(restored);
        Assert.Equal(await SHA256.HashDataAsync(originalStream), await SHA256.HashDataAsync(restoredStream));
        Assert.Equal(96, store.Reads); Assert.Equal(32, store.Maximum); Assert.Equal(0, store.Active);
        Assert.True(store.Finished.IndexOf(64) < store.Finished.IndexOf(33));
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task ThirtyTwoReadsDrainAndRemovePartialFileOnFailureOrCancellation(string mode)
    {
        var fixture = await Prepare(32);
        var store = new FourSlotStore(new LocalBackupStore(Repo), mode, 32);
        var engine = new BackupEngine(store, key, maxConcurrentRestoreChunks: 32);
        using var cancellation = new CancellationTokenSource();
        Task restore = engine.RestoreAsync(fixture.Snapshot, Destination, ct: cancellation.Token);
        await store.AllStarted.WaitAsync(TimeSpan.FromSeconds(60));
        if (mode == "cancel") cancellation.Cancel();
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => restore.WaitAsync(TimeSpan.FromSeconds(60)));
        if (mode == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Equal("simulated read failure", Assert.IsType<IOException>(error).Message);
        Assert.Equal(32, store.Maximum); Assert.Equal(0, store.Active); AssertNoPartialFile();
    }
    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public void RejectsUnsupportedConcurrency(int concurrency) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackupEngine(new LocalBackupStore(Repo), key, maxConcurrentRestoreChunks: concurrency));
}
