using System.Security.Cryptography;
using PCAutobackup.Core;

namespace PCAutobackup.Tests;

public sealed class ProductionConcurrencyTests : IDisposable
{
    private const int Slots = 16;
    private const int ChunkSize = 4 * 1024 * 1024;
    private readonly string root = Path.Combine(Path.GetTempPath(), "PCAutobackup-tests", Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public async Task ProductionFactoryUsesSixteenSlotsForBackupAndRestore()
    {
        string source = Path.Combine(root, "source"), repo = Path.Combine(root, "repo"), destination = Path.Combine(root, "restore");
        Directory.CreateDirectory(source);
        string file = Path.Combine(source, "data.bin");
        await using (var output = File.Create(file))
            for (int i = 0; i < Slots; i++) await output.WriteAsync(RandomNumberGenerator.GetBytes(ChunkSize));

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await using var store = new GatedStore(new LocalBackupStore(repo));
        var app = new ApplicationService(Path.Combine(root, "config.json"));
        var engine = app.CreateProductionEngine(store, RandomNumberGenerator.GetBytes(32));
        var snapshot = await engine.BackupAsync(new AppConfiguration
        {
            DeviceId = "production-policy-test",
            Sources = [new BackupSource { Id = "source", Path = source }]
        }, ct: cancellation.Token);
        Assert.Equal(Slots, store.MaximumUploads);
        await engine.RestoreAsync(snapshot.SnapshotId, destination, ct: cancellation.Token);
        Assert.Equal(Slots, store.MaximumReads);
        string restored = Assert.Single(Directory.GetFiles(destination, "*", SearchOption.AllDirectories));
        await using var originalStream = File.OpenRead(file);
        await using var restoredStream = File.OpenRead(restored);
        Assert.Equal(await SHA256.HashDataAsync(originalStream), await SHA256.HashDataAsync(restoredStream));
    }

    private sealed class GatedStore(IBackupStore inner) : IBackupStore
    {
        private readonly TaskCompletionSource uploadsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource readsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int uploads, reads, activeUploads, activeReads, maximumUploads, maximumReads;
        public int MaximumUploads => Volatile.Read(ref maximumUploads);
        public int MaximumReads => Volatile.Read(ref maximumReads);

        private static void UpdateMaximum(ref int maximum, int current)
        {
            int observed;
            while ((observed = Volatile.Read(ref maximum)) < current && Interlocked.CompareExchange(ref maximum, current, observed) != observed) { }
        }
        public Task<bool> ExistsAsync(string name, CancellationToken ct = default) => inner.ExistsAsync(name, ct);
        public async Task PutAsync(string name, Stream content, bool overwrite = false, CancellationToken ct = default)
        {
            if (!name.StartsWith("chunks/", StringComparison.Ordinal)) { await inner.PutAsync(name, content, overwrite, ct); return; }
            UpdateMaximum(ref maximumUploads, Interlocked.Increment(ref activeUploads));
            try
            {
                if (Interlocked.Increment(ref uploads) == Slots) uploadsReady.TrySetResult();
                await uploadsReady.Task.WaitAsync(ct);
                await inner.PutAsync(name, content, overwrite, ct);
            }
            finally { Interlocked.Decrement(ref activeUploads); }
        }
        public async Task<Stream> OpenReadAsync(string name, CancellationToken ct = default)
        {
            if (!name.StartsWith("chunks/", StringComparison.Ordinal)) return await inner.OpenReadAsync(name, ct);
            UpdateMaximum(ref maximumReads, Interlocked.Increment(ref activeReads));
            try
            {
                if (Interlocked.Increment(ref reads) == Slots) readsReady.TrySetResult();
                await readsReady.Task.WaitAsync(ct);
                return await inner.OpenReadAsync(name, ct);
            }
            finally { Interlocked.Decrement(ref activeReads); }
        }
        public Task DeleteAsync(string name, CancellationToken ct = default) => inner.DeleteAsync(name, ct);
        public IAsyncEnumerable<StoredObject> ListAsync(string prefix, CancellationToken ct = default) => inner.ListAsync(prefix, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
