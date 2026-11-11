using System.Security.Cryptography;
using PCAutobackup.Core;

namespace PCAutobackup.Tests;

// Regression tests from the 2026-11-11 audit. Synthetic fixtures in a temporary directory only.
public sealed class AuditEngineTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PCAutobackup-tests", Guid.NewGuid().ToString("N"));
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    private string Source => Path.Combine(root, "source");
    private string Repo => Path.Combine(root, "repo");
    public AuditEngineTests() { Directory.CreateDirectory(Source); Directory.CreateDirectory(Repo); }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private AppConfiguration Config(string? source = null) => new() { DeviceId = "audit-device", DeviceName = "fixture", Sources = [new BackupSource { Id = "source", Path = source ?? Source }] };
    private BackupEngine Engine(IBackupStore? store = null) => new(store ?? new LocalBackupStore(Repo), key);
    private static string SourceFolder(string restore) => Assert.Single(Directory.GetDirectories(restore));

    // NTFS keeps these two names apart, while the manifest validator folds them together.
    // Georgian Mtavruli capitals were added to Unicode after the NTFS upcase table was fixed.
    private const string LowerName = "ა.txt", UpperName = "Ა.txt";

    [Fact]
    public async Task NamesThatCollideOnlyInTheManifestValidatorNeverCommitAnUnreadableSnapshot()
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "keep.txt"), "healthy");
        var engine = Engine(); var config = Config();
        var healthy = await engine.BackupAsync(config);
        await File.WriteAllTextAsync(Path.Combine(Source, LowerName), "lower");
        await File.WriteAllTextAsync(Path.Combine(Source, UpperName), "upper");
        if (Directory.GetFiles(Source).Length != 3) return; // This volume folds the two names itself.
        Assert.Equal(0, string.Compare(LowerName, UpperName, StringComparison.OrdinalIgnoreCase));

        // The run must fail before commit and name the file, not publish a manifest it cannot read back.
        var error = await Assert.ThrowsAsync<IOException>(() => engine.BackupAsync(config));
        Assert.Contains(Source, error.Message);
        Assert.Equal(healthy.SnapshotId, Assert.Single(await engine.ListSnapshotsAsync()).Id);

        // The repository stays usable: remove one of the names, back up and restore.
        File.Delete(Path.Combine(Source, UpperName));
        var next = await engine.BackupAsync(config);
        string restore = Path.Combine(root, "restore");
        await engine.RestoreAsync(next.SnapshotId, restore);
        Assert.Equal("lower", await File.ReadAllTextAsync(Path.Combine(SourceFolder(restore), LowerName)));
        Assert.Equal("healthy", await File.ReadAllTextAsync(Path.Combine(SourceFolder(restore), "keep.txt")));
    }

    [Fact]
    public async Task OneDamagedManifestDoesNotHideHealthySnapshotsFromRestore()
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "a.txt"), "first");
        var engine = Engine(); var config = Config();
        var first = await engine.BackupAsync(config);
        await File.WriteAllTextAsync(Path.Combine(Source, "a.txt"), "second");
        var second = await engine.BackupAsync(config);
        string damaged = Path.Combine(Repo, "snapshots", first.SnapshotId + ".manifest");
        byte[] bytes = await File.ReadAllBytesAsync(damaged); bytes[^1] ^= 1; await File.WriteAllBytesAsync(damaged, bytes);

        var listed = await engine.ListSnapshotsAsync();
        Assert.Equal(2, listed.Count);
        var readable = Assert.Single(listed, s => s.Id == second.SnapshotId);
        Assert.Equal(1, readable.FileCount);
        var marked = Assert.Single(listed, s => s.Id == first.SnapshotId);
        Assert.Equal(BackupEngine.DamagedSnapshotLabel, marked.DeviceName);
        Assert.Equal(0, marked.FileCount);
        Assert.InRange(marked.CreatedUtc, DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(1));

        string restore = Path.Combine(root, "restore");
        await engine.RestoreAsync(second.SnapshotId, restore);
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(SourceFolder(restore), "a.txt")));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => engine.RestoreAsync(first.SnapshotId, Path.Combine(root, "restore-damaged")));

        // A wrong key reads nothing at all and must still be reported as an error, not an empty or damaged list.
        var wrong = new BackupEngine(new LocalBackupStore(Repo), RandomNumberGenerator.GetBytes(32));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => wrong.ListSnapshotsAsync());
        // Backup keeps failing closed while a manifest cannot be authenticated; nothing is pruned.
        config.RetentionDays = 0;
        var blocked = await Assert.ThrowsAnyAsync<CryptographicException>(() => engine.BackupAsync(config));
        Assert.Contains(first.SnapshotId, blocked.Message); // the user must be told which snapshot is damaged
        Assert.Equal(2, Directory.GetFiles(Path.Combine(Repo, "snapshots")).Length);
    }

    private sealed class NoProgress : IProgress<BackupProgress> { public void Report(BackupProgress value) { } }

    // Delegating store with one overridable fault per operation.
    private sealed class FaultStore(IBackupStore inner) : IBackupStore
    {
        public Func<string, bool> FailPut { get; init; } = _ => false;
        public Func<string, bool> IgnoreDelete { get; init; } = _ => false;
        public Func<string, bool> FailDelete { get; init; } = _ => false;
        public Func<string, Exception?> DeleteError { get; init; } = _ => null;
        public Task<bool> ExistsAsync(string name, CancellationToken ct = default) => inner.ExistsAsync(name, ct);
        public Task<Stream> OpenReadAsync(string name, CancellationToken ct = default) => inner.OpenReadAsync(name, ct);
        public Task PutAsync(string name, Stream content, bool overwrite = false, CancellationToken ct = default) =>
            FailPut(name) ? throw new IOException("Simulated write failure") : inner.PutAsync(name, content, overwrite, ct);
        public Task DeleteAsync(string name, CancellationToken ct = default) =>
            DeleteError(name) is { } error ? throw error
            : FailDelete(name) ? throw new IOException("Simulated delete failure") : IgnoreDelete(name) ? Task.CompletedTask : inner.DeleteAsync(name, ct);
        public IAsyncEnumerable<StoredObject> ListAsync(string prefix, CancellationToken ct = default) => inner.ListAsync(prefix, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private string ChunkFile(byte[] content)
    {
        byte[] namingKey = HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes("PCAutobackup chunk names v1"));
        return Path.Combine(Repo, "chunks", Convert.ToHexStringLower(HMACSHA256.HashData(namingKey, content)) + ".chunk");
    }
    private static async Task FlipLastByte(string file)
    { byte[] bytes = await File.ReadAllBytesAsync(file); bytes[^1] ^= 1; await File.WriteAllBytesAsync(file, bytes); }

    [Theory]
    [InlineData(true)]  // two names that become the same manifest path
    [InlineData(false)] // one name that would be renamed on restore
    public async Task NamesWithUnpairedSurrogatesAreRefusedBeforeCommit(bool pair)
    {
        // Built at run time: attribute metadata is UTF-8 and cannot carry an unpaired surrogate.
        string first = "a" + (char)0xD800 + ".txt";
        string? second = pair ? "a" + (char)0xD801 + ".txt" : null;
        await File.WriteAllTextAsync(Path.Combine(Source, "keep.txt"), "healthy");
        var config = Config(); config.RetentionDays = 0;
        var healthy = await Engine().BackupAsync(config);
        await File.WriteAllTextAsync(Path.Combine(Source, first), "one");
        if (second is not null) await File.WriteAllTextAsync(Path.Combine(Source, second), "two");
        if (Directory.GetFiles(Source).Length != (second is null ? 2 : 3)) return; // This volume does not keep the names apart.

        // JSON writes each unpaired surrogate as U+FFFD. Before the fix this run reported success,
        // published a manifest with two identical paths and pruned the healthy snapshot.
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Engine().BackupAsync(config));
        Assert.Contains(Source, error.Message);
        Assert.Equal(healthy.SnapshotId, Assert.Single(await Engine().ListSnapshotsAsync()).Id);
        string restore = Path.Combine(root, "restore");
        await Engine().RestoreAsync(healthy.SnapshotId, restore);
        Assert.Equal("healthy", await File.ReadAllTextAsync(Path.Combine(SourceFolder(restore), "keep.txt")));
    }

    [Fact]
    public async Task OrphanRepairThatLeavesTheOldObjectNeverCommitsAnUnrestorableSnapshot()
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "keep.txt"), "healthy");
        var config = Config(); config.RetentionDays = 0;
        var healthy = await Engine().BackupAsync(config);
        // An interrupted run leaves an orphan chunk, which then gets damaged.
        byte[] added = RandomNumberGenerator.GetBytes(8192);
        await File.WriteAllBytesAsync(Path.Combine(Source, "added.bin"), added);
        var interrupted = new FaultStore(new LocalBackupStore(Repo)) { FailPut = name => name.StartsWith("snapshots/", StringComparison.Ordinal) };
        await Assert.ThrowsAsync<IOException>(() => Engine(interrupted).BackupAsync(config));
        await FlipLastByte(ChunkFile(added));
        // The delete reports success but the name stays (delete-pending file, sync tool), so the
        // local store keeps the damaged object when the replacement is moved into place.
        var stale = new FaultStore(new LocalBackupStore(Repo)) { IgnoreDelete = name => name.StartsWith("chunks/", StringComparison.Ordinal) };

        await Assert.ThrowsAnyAsync<CryptographicException>(() => Engine(stale).BackupAsync(config));

        Assert.Equal(healthy.SnapshotId, Assert.Single(await Engine().ListSnapshotsAsync()).Id);
        // With a store that really deletes, the same resume repairs the chunk and restores.
        var repaired = await Engine().BackupAsync(config);
        string restore = Path.Combine(root, "restore");
        await Engine().RestoreAsync(repaired.SnapshotId, restore);
        Assert.Equal(added, await File.ReadAllBytesAsync(Path.Combine(SourceFolder(restore), "added.bin")));
    }

    [Fact]
    public async Task OneDamagedOrMissingChunkCostsOnlyItsOwnFileOnRestore()
    {
        byte[] top = RandomNumberGenerator.GetBytes(8192), sibling = RandomNumberGenerator.GetBytes(8192),
            gone = RandomNumberGenerator.GetBytes(8192), deep = RandomNumberGenerator.GetBytes(8192);
        Directory.CreateDirectory(Path.Combine(Source, "sub"));
        await File.WriteAllBytesAsync(Path.Combine(Source, "a-top.bin"), top);
        await File.WriteAllBytesAsync(Path.Combine(Source, "m-gone.bin"), gone);
        await File.WriteAllBytesAsync(Path.Combine(Source, "z-sibling.bin"), sibling);
        await File.WriteAllBytesAsync(Path.Combine(Source, "sub", "deep.bin"), deep);
        var snapshot = await Engine().BackupAsync(Config());
        await FlipLastByte(ChunkFile(top));
        File.Delete(ChunkFile(gone));

        string restore = Path.Combine(root, "restore");
        var result = await Engine().RestoreAsync(snapshot.SnapshotId, restore);

        // The damaged file sorts first; before the fix nothing after it was written.
        Assert.Equal(2, result.FilesRestored);
        Assert.Equal(2, result.Warnings.Count);
        Assert.Contains(result.Warnings, w => w.Contains("a-top.bin"));
        Assert.Contains(result.Warnings, w => w.Contains("m-gone.bin"));
        string folder = SourceFolder(restore);
        Assert.Equal(sibling, await File.ReadAllBytesAsync(Path.Combine(folder, "z-sibling.bin")));
        Assert.Equal(deep, await File.ReadAllBytesAsync(Path.Combine(folder, "sub", "deep.bin")));
        Assert.False(File.Exists(Path.Combine(folder, "a-top.bin"))); // no partial or unauthenticated output
        Assert.False(File.Exists(Path.Combine(folder, "m-gone.bin")));
        // Each line is a fixed prefix plus the path, so both languages can translate it in full.
        Assert.Contains(result.Warnings, w => AppLocalization.TranslateMessage(w, "th").StartsWith("กู้คืนไฟล์นี้ไม่ได้ เพราะข้อมูลสำรองของไฟล์เสียหาย: ", StringComparison.Ordinal) && w.EndsWith("/a-top.bin", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => AppLocalization.TranslateMessage(w, "th").StartsWith("กู้คืนไฟล์นี้ไม่ได้ เพราะข้อมูลสำรองของไฟล์หายไป: ", StringComparison.Ordinal) && w.EndsWith("/m-gone.bin", StringComparison.Ordinal));

        // When no file at all can be restored the restore still fails with the cause.
        await FlipLastByte(ChunkFile(sibling)); await FlipLastByte(ChunkFile(deep));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Engine().RestoreAsync(snapshot.SnapshotId, Path.Combine(root, "restore-none")));
    }

    [Fact]
    public async Task ChunkCleanupContinuesPastAnObjectThatCannotBeDeleted()
    {
        byte[] oldX = RandomNumberGenerator.GetBytes(8192), oldY = RandomNumberGenerator.GetBytes(8192);
        await File.WriteAllBytesAsync(Path.Combine(Source, "x.bin"), oldX);
        await File.WriteAllBytesAsync(Path.Combine(Source, "y.bin"), oldY);
        var config = Config(); config.RetentionDays = 0;
        await Engine().BackupAsync(config);
        await File.WriteAllBytesAsync(Path.Combine(Source, "x.bin"), RandomNumberGenerator.GetBytes(8192));
        await File.WriteAllBytesAsync(Path.Combine(Source, "y.bin"), RandomNumberGenerator.GetBytes(8192));
        string stuck = "chunks/" + Path.GetFileName(ChunkFile(oldX));
        var store = new FaultStore(new LocalBackupStore(Repo)) { FailDelete = name => name == stuck };

        var result = await Engine(store).BackupAsync(config);

        Assert.Contains(result.Warnings, w => w.Contains("could not be deleted", StringComparison.Ordinal) && w.EndsWith(": 1", StringComparison.Ordinal));
        Assert.True(File.Exists(ChunkFile(oldX)));
        Assert.False(File.Exists(ChunkFile(oldY))); // not held hostage by the stuck object
        Assert.Single(await Engine().ListSnapshotsAsync());
        var later = await Engine().BackupAsync(config);
        Assert.Empty(later.Warnings);
        Assert.False(File.Exists(ChunkFile(oldX)));
    }

    [Fact]
    public async Task ChunkCleanupStopsAtATransportFailureInsteadOfTryingEveryObject()
    {
        // Each failed delete on Google Drive has already used the whole retry budget. Going on
        // to the next object during an outage would repeat that for every unreferenced chunk.
        for (int i = 0; i < 4; i++) await File.WriteAllBytesAsync(Path.Combine(Source, i + ".bin"), RandomNumberGenerator.GetBytes(4096));
        var config = Config(); config.RetentionDays = 0;
        await Engine().BackupAsync(config);
        for (int i = 0; i < 4; i++) await File.WriteAllBytesAsync(Path.Combine(Source, i + ".bin"), RandomNumberGenerator.GetBytes(4096));
        int attempts = 0;
        var store = new FaultStore(new LocalBackupStore(Repo))
        {
            DeleteError = name => name.StartsWith("chunks/", StringComparison.Ordinal) && Interlocked.Increment(ref attempts) > 0 ? new HttpRequestException("Simulated outage") : null
        };

        var result = await Engine(store).BackupAsync(config);

        Assert.Contains(result.Warnings, w => w.StartsWith("Snapshot committed; cleanup failed: ", StringComparison.Ordinal));
        Assert.Equal(1, attempts);
        Assert.Empty((await Engine().BackupAsync(config)).Warnings); // the next run collects them
    }

    [Fact]
    public async Task SourceFolderWhoseOwnNameHasAnUnpairedSurrogateStillBacksUp()
    {
        // The top-level folder name is only a display prefix; earlier versions stored it with U+FFFD.
        string odd = Path.Combine(root, "odd" + (char)0xD800);
        Directory.CreateDirectory(odd);
        if (!Directory.Exists(odd)) return;
        await File.WriteAllTextAsync(Path.Combine(odd, "a.txt"), "data");
        var snapshot = await Engine().BackupAsync(Config(odd));
        string restore = Path.Combine(root, "restore");
        await Engine().RestoreAsync(snapshot.SnapshotId, restore);
        Assert.Equal("data", await File.ReadAllTextAsync(Path.Combine(SourceFolder(restore), "a.txt")));
    }

    [Fact]
    public async Task IncompleteSnapshotBlocksCleanupOnlyUntilItExpires()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-01T12:00:00Z"));
        BackupEngine Timed() => new(new LocalBackupStore(Repo), key, clock);
        var config = Config(); config.RetentionDays = 7;
        byte[] lost = RandomNumberGenerator.GetBytes(8192);
        await File.WriteAllBytesAsync(Path.Combine(Source, "a.bin"), lost);
        var incomplete = await Timed().BackupAsync(config);
        await File.WriteAllBytesAsync(Path.Combine(Source, "a.bin"), RandomNumberGenerator.GetBytes(8192));
        clock.Now = clock.Now.AddDays(1);
        await Timed().BackupAsync(config);
        File.Delete(ChunkFile(lost)); // the older snapshot can no longer be restored completely

        // While it is inside the retention window nothing older may be deleted.
        clock.Now = clock.Now.AddDays(1);
        var blocked = await Timed().BackupAsync(config);
        Assert.Contains(blocked.Warnings, w => w.Contains("missing chunk", StringComparison.Ordinal));
        Assert.Equal(3, (await Timed().ListSnapshotsAsync()).Count);

        // Once it is older than the retention it is removed like any other snapshot, and cleanup
        // works again. Before the fix it stopped cleanup on every later run and never aged out.
        clock.Now = clock.Now.AddDays(7);
        var expired = await Timed().BackupAsync(config);
        Assert.Empty(expired.Warnings);
        Assert.DoesNotContain(await Timed().ListSnapshotsAsync(), s => s.Id == incomplete.SnapshotId);
        clock.Now = clock.Now.AddDays(1);
        Assert.Empty((await Timed().BackupAsync(config)).Warnings);
    }

    [Fact]
    public async Task VeryLongRetentionKeepsEverythingWithoutACleanupFailure()
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "a.txt"), "one");
        var config = Config(); config.RetentionDays = int.MaxValue;
        await Engine().BackupAsync(config);
        await File.WriteAllTextAsync(Path.Combine(Source, "a.txt"), "two");
        var second = await Engine().BackupAsync(config);
        Assert.Empty(second.Warnings);
        Assert.Equal(2, (await Engine().ListSnapshotsAsync()).Count);
    }

    [Fact]
    public async Task LockedSourceFileFailsTheRunAndKeepsThePreviousSnapshot()
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "a.txt"), "original");
        var config = Config(); config.RetentionDays = 0;
        var first = await Engine().BackupAsync(config);
        await File.WriteAllTextAsync(Path.Combine(Source, "a.txt"), "changed");
        using (new FileStream(Path.Combine(Source, "a.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => Engine().BackupAsync(config));
        Assert.Equal(first.SnapshotId, Assert.Single(await Engine().ListSnapshotsAsync()).Id);
        string restore = Path.Combine(root, "restore");
        await Engine().RestoreAsync(first.SnapshotId, restore);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(SourceFolder(restore), "a.txt")));
    }

    [Fact]
    public async Task NonAsciiPathsRoundTripAndEscapedManifestsFromOlderVersionsStillRead()
    {
        string thaiFolder = "เอกสาร งานสำคัญ", thaiFile = "รายงาน ๒๕๖๙.txt";
        Directory.CreateDirectory(Path.Combine(Source, thaiFolder));
        await File.WriteAllTextAsync(Path.Combine(Source, thaiFolder, thaiFile), "เนื้อหา");
        var snapshot = await Engine().BackupAsync(Config());
        string restore = Path.Combine(root, "restore");
        await Engine().RestoreAsync(snapshot.SnapshotId, restore);
        Assert.Equal("เนื้อหา", await File.ReadAllTextAsync(Path.Combine(SourceFolder(restore), thaiFolder, thaiFile)));

        // Older versions wrote every non-ASCII character as a \uXXXX escape.
        string id = "20261101000000000-" + Guid.NewGuid().ToString("N");
        string escaped = string.Concat("ก".Select(c => "\\u" + ((int)c).ToString("x4")));
        byte[] plain = System.Text.Encoding.UTF8.GetBytes("{\"Version\":1,\"Id\":\"" + id + "\",\"CreatedUtc\":\"2026-11-01T00:00:00+00:00\",\"DeviceName\":\"fixture\",\"Entries\":[{\"Path\":\"old-" + escaped + "\",\"Directory\":true,\"Length\":0,\"ModifiedUtc\":\"0001-01-01T00:00:00+00:00\",\"Chunks\":[]}]}");
        byte[] encrypted = new byte[32 + plain.Length];
        "PAB1"u8.CopyTo(encrypted); RandomNumberGenerator.Fill(encrypted.AsSpan(4, 12));
        byte[] encryptionKey = HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes("PCAutobackup encryption v1"));
        using (var aes = new AesGcm(encryptionKey, 16))
            aes.Encrypt(encrypted.AsSpan(4, 12), plain, encrypted.AsSpan(32), encrypted.AsSpan(16, 16), System.Text.Encoding.UTF8.GetBytes("manifest:" + id));
        await File.WriteAllBytesAsync(Path.Combine(Repo, "snapshots", id + ".manifest"), encrypted);
        string restoreOld = Path.Combine(root, "restore-old");
        await Engine().RestoreAsync(id, restoreOld);
        Assert.True(Directory.Exists(Path.Combine(restoreOld, "old-ก")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // the settings window and the Worker always pass progress, which runs the estimate walk first
    public async Task UnsupportedSourceNameFailsTheRunAndNamesTheFile(bool withProgress)
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "ok.txt"), "fine");
        var first = await Engine().BackupAsync(Config());
        // Only the \\?\ prefix can create a trailing-dot name; ordinary tools cannot open it.
        string bad = Path.Combine(Source, "trailing-dot.");
        await File.WriteAllTextAsync(@"\\?\" + bad, "x");
        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Engine().BackupAsync(Config(), withProgress ? new NoProgress() : null));
            Assert.Contains(bad, error.Message);
            Assert.Equal("ชื่อในต้นทางนี้กู้คืนบน Windows ไม่ได้: \"" + bad + "\"", AppLocalization.TranslateMessage(error.Message, "th"));
            Assert.Equal(first.SnapshotId, Assert.Single(await Engine().ListSnapshotsAsync()).Id);
        }
        finally { File.Delete(@"\\?\" + bad); }
    }

    [Fact]
    public async Task RestoreWorksFromARepositoryThatCannotBeWritten()
    {
        // A copy of the repository on read-only media or a read-only share: the lease file
        // cannot be opened for writing, and restore needs no write access to the repository.
        byte[] content = RandomNumberGenerator.GetBytes(8192);
        await File.WriteAllBytesAsync(Path.Combine(Source, "a.bin"), content);
        var snapshot = await Engine().BackupAsync(Config());
        string leaseFile = Path.Combine(Repo, "repository", "lease.lock");
        using (new LocalBackupStore(Repo).AcquireLease()) { }
        File.SetAttributes(leaseFile, FileAttributes.ReadOnly);
        try
        {
            var store = new LocalBackupStore(Repo);
            Assert.Throws<UnauthorizedAccessException>(() => store.AcquireLease()); // a backup still needs the exclusive lease
            using (store.AcquireLease(readOnly: true))
            using (new LocalBackupStore(Repo).AcquireLease(readOnly: true)) // readers may share
            {
                string restore = Path.Combine(root, "restore-readonly");
                await Engine(store).RestoreAsync(snapshot.SnapshotId, restore);
                Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(SourceFolder(restore), "a.bin")));
            }
        }
        finally { File.SetAttributes(leaseFile, FileAttributes.Normal); }
        // A writable repository keeps the exclusive lease.
        using var exclusive = new LocalBackupStore(Repo).AcquireLease();
        Assert.Throws<IOException>(() => new LocalBackupStore(Repo).AcquireLease());
    }

    // Models an eventually consistent listing: the first "snapshots/" listing of a run
    // misses one committed manifest, later listings return it.
    private sealed class LateSnapshotListing(IBackupStore inner, string hiddenSnapshotId) : IBackupStore
    {
        private int snapshotListings;
        public Task<bool> ExistsAsync(string name, CancellationToken ct = default) => inner.ExistsAsync(name, ct);
        public Task<Stream> OpenReadAsync(string name, CancellationToken ct = default) => inner.OpenReadAsync(name, ct);
        public Task PutAsync(string name, Stream content, bool overwrite = false, CancellationToken ct = default) => inner.PutAsync(name, content, overwrite, ct);
        public Task DeleteAsync(string name, CancellationToken ct = default) => inner.DeleteAsync(name, ct);
        public async IAsyncEnumerable<StoredObject> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            bool hide = prefix == "snapshots/" && Interlocked.Increment(ref snapshotListings) == 1;
            await foreach (var item in inner.ListAsync(prefix, ct))
                if (!hide || item.Name != "snapshots/" + hiddenSnapshotId + ".manifest") yield return item;
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Fact]
    public async Task SnapshotMissedByTheInitialListingIsNotGarbageCollected()
    {
        // The earlier snapshot holds a version of the file that the next run no longer has.
        byte[] oldVersion = RandomNumberGenerator.GetBytes(8192);
        await File.WriteAllBytesAsync(Path.Combine(Source, "a.bin"), oldVersion);
        var config = Config();
        var earlier = await Engine().BackupAsync(config);
        await File.WriteAllBytesAsync(Path.Combine(Source, "a.bin"), RandomNumberGenerator.GetBytes(8192));

        var result = await Engine(new LateSnapshotListing(new LocalBackupStore(Repo), earlier.SnapshotId)).BackupAsync(config);

        // The run cannot prove which chunks are unreferenced, so it must not delete any.
        Assert.Contains(result.Warnings, w => w.Contains("cleanup", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, (await Engine().ListSnapshotsAsync()).Count);
        string restore = Path.Combine(root, "restore-earlier");
        await Engine().RestoreAsync(earlier.SnapshotId, restore);
        Assert.Equal(oldVersion, await File.ReadAllBytesAsync(Path.Combine(SourceFolder(restore), "a.bin")));
        // Once every snapshot is listed at the start of a run, cleanup works again.
        var later = await Engine().BackupAsync(config);
        Assert.Empty(later.Warnings);
    }
}
