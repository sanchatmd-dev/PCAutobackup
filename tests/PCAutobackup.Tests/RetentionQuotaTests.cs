using System.Security.Cryptography;
using System.Text;
using PCAutobackup.Core;

namespace PCAutobackup.Tests;

public sealed class RetentionQuotaTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PCAutobackup-tests", Guid.NewGuid().ToString("N"));
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    private readonly ManualClock clock = new(DateTimeOffset.Parse("2026-10-30T12:00:00Z"));
    private string Source => Path.Combine(root, "source");
    private string Repo => Path.Combine(root, "repository");
    private BackupEngine Engine => new(new LocalBackupStore(Repo), key, clock);
    public RetentionQuotaTests() => Directory.CreateDirectory(Source);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private AppConfiguration Config() => new() { Sources = [new BackupSource { Path = Source, Label = "Synthetic" }], RetentionDays = 7 };
    private string Chunk(byte[] bytes)
    {
        var namingKey = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("PCAutobackup chunk names v1"));
        return Path.Combine(Repo, "chunks", Convert.ToHexStringLower(HMACSHA256.HashData(namingKey, bytes)) + ".chunk");
    }
    private async Task AssertRestores(string id, string name, params (string File, byte[] Bytes)[] expected)
    {
        var target = Path.Combine(root, name);
        await Engine.RestoreAsync(id, target);
        var source = Assert.Single(Directory.GetDirectories(target));
        Assert.Equal(expected.Length, Directory.GetFiles(source, "*", SearchOption.AllDirectories).Length);
        foreach (var (file, bytes) in expected)
            Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(source, file))));
    }

    [Fact]
    public async Task SevenDaysDropsExpiredExclusiveChunksButKeepsBoundarySharedChunksAndLatest()
    {
        var config = Config();
        var shared = RandomNumberGenerator.GetBytes(8192);
        var expired = RandomNumberGenerator.GetBytes(8192);
        var boundary = RandomNumberGenerator.GetBytes(8192);
        var latest = RandomNumberGenerator.GetBytes(8192);
        await File.WriteAllBytesAsync(Path.Combine(Source, "shared.bin"), shared);
        await File.WriteAllBytesAsync(Path.Combine(Source, "expired.bin"), expired);
        var oldest = await Engine.BackupAsync(config);
        clock.Now = clock.Now.AddDays(1); // exactly seven days before the final snapshot
        File.Delete(Path.Combine(Source, "expired.bin"));
        await File.WriteAllBytesAsync(Path.Combine(Source, "boundary.bin"), boundary);
        var atBoundary = await Engine.BackupAsync(config);
        clock.Now = clock.Now.AddDays(4); // three days before final
        File.Delete(Path.Combine(Source, "boundary.bin"));
        var recent = await Engine.BackupAsync(config);
        var foreign = Path.Combine(Repo, "unrelated.txt");
        var foreignChunk = Path.Combine(Repo, "chunks", "unrelated.chunk");
        await File.WriteAllTextAsync(foreign, "leave untouched");
        await File.WriteAllTextAsync(foreignChunk, "leave untouched");
        Assert.True(File.Exists(Chunk(expired)));
        clock.Now = clock.Now.AddDays(3);
        await File.WriteAllBytesAsync(Path.Combine(Source, "latest.bin"), latest);
        var current = await Engine.BackupAsync(config);
        var ids = (await Engine.ListSnapshotsAsync()).Select(x => x.Id).ToArray();
        Assert.Equal(3, ids.Length);
        Assert.DoesNotContain(oldest.SnapshotId, ids);
        Assert.Contains(atBoundary.SnapshotId, ids);
        Assert.Contains(recent.SnapshotId, ids);
        Assert.Contains(current.SnapshotId, ids);
        Assert.False(File.Exists(Chunk(expired)));
        Assert.True(File.Exists(Chunk(boundary))); // still used by the exactly-seven-day snapshot
        Assert.True(File.Exists(Chunk(shared)));
        Assert.True(File.Exists(Chunk(latest)));
        Assert.Equal("leave untouched", await File.ReadAllTextAsync(foreign));
        Assert.Equal("leave untouched", await File.ReadAllTextAsync(foreignChunk));
        await AssertRestores(atBoundary.SnapshotId, "restore-boundary", ("shared.bin", shared), ("boundary.bin", boundary));
        await AssertRestores(recent.SnapshotId, "restore-recent", ("shared.bin", shared));
        await AssertRestores(current.SnapshotId, "restore-latest", ("shared.bin", shared), ("latest.bin", latest));
    }

    [Fact]
    public async Task QuotaPrunesOldVersionsKeepsSharedDataAndRejectsOversizedNextSnapshot()
    {
        var config = Config();
        var shared = RandomNumberGenerator.GetBytes(8192);
        var old = RandomNumberGenerator.GetBytes(8192);
        var middle = RandomNumberGenerator.GetBytes(8192);
        var newest = RandomNumberGenerator.GetBytes(8192);
        await File.WriteAllBytesAsync(Path.Combine(Source, "shared.bin"), shared);
        await File.WriteAllBytesAsync(Path.Combine(Source, "version.bin"), old);
        await Engine.BackupAsync(config);
        clock.Now = clock.Now.AddHours(1);
        await File.WriteAllBytesAsync(Path.Combine(Source, "version.bin"), middle);
        await Engine.BackupAsync(config);
        var unknown = Path.Combine(Repo, "personal-unrelated.bin");
        await File.WriteAllBytesAsync(unknown, RandomNumberGenerator.GetBytes(100_000));
        var unknownHash = SHA256.HashData(await File.ReadAllBytesAsync(unknown));
        config.QuotaBytes = 20_000;
        clock.Now = clock.Now.AddHours(1);
        await File.WriteAllBytesAsync(Path.Combine(Source, "version.bin"), newest);
        var latest = await Engine.BackupAsync(config);
        Assert.Empty(latest.Warnings);
        Assert.Equal(latest.SnapshotId, Assert.Single(await Engine.ListSnapshotsAsync()).Id);
        Assert.False(File.Exists(Chunk(old)));
        Assert.False(File.Exists(Chunk(middle)));
        Assert.True(File.Exists(Chunk(shared)));
        Assert.True(File.Exists(Chunk(newest)));
        var committedBytes = Directory.GetFiles(Path.Combine(Repo, "chunks")).Sum(p => new FileInfo(p).Length)
            + Directory.GetFiles(Path.Combine(Repo, "snapshots")).Sum(p => new FileInfo(p).Length);
        Assert.InRange(committedBytes, 1, config.QuotaBytes.Value);
        Assert.Equal(unknownHash, SHA256.HashData(await File.ReadAllBytesAsync(unknown)));
        await AssertRestores(latest.SnapshotId, "restore-quota", ("shared.bin", shared), ("version.bin", newest));

        var chunkPathsBefore = Directory.GetFiles(Path.Combine(Repo, "chunks")).Order().ToArray();
        config.QuotaBytes = 1; // even the last restorable snapshot does not fit
        clock.Now = clock.Now.AddHours(1);
        await File.WriteAllBytesAsync(Path.Combine(Source, "version.bin"), RandomNumberGenerator.GetBytes(8192));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Engine.BackupAsync(config));
        Assert.All(chunkPathsBefore, path => Assert.True(File.Exists(path)));
        Assert.Equal(chunkPathsBefore.Length + 1, Directory.GetFiles(Path.Combine(Repo, "chunks")).Length); // Resume checkpoint survives quota rejection.
        Assert.Equal(latest.SnapshotId, Assert.Single(await Engine.ListSnapshotsAsync()).Id);
        await AssertRestores(latest.SnapshotId, "restore-after-rejection", ("shared.bin", shared), ("version.bin", newest));
        Assert.Equal(unknownHash, SHA256.HashData(await File.ReadAllBytesAsync(unknown)));
    }

    [Fact]
    public async Task MultipleFoldersWithSameNameKeepTheirOwnFilesWhenReorderedAndDeleted()
    {
        var first = Path.Combine(Source, "a", "Documents");
        var second = Path.Combine(Source, "b", "Documents");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        await File.WriteAllTextAsync(Path.Combine(first, "same.txt"), "first folder");
        await File.WriteAllTextAsync(Path.Combine(second, "same.txt"), "second folder");
        var config = new AppConfiguration { RetentionDays = 0, Sources = [new BackupSource { Label = "Documents", Path = first }, new BackupSource { Label = "Documents", Path = second }] };
        var old = await Engine.BackupAsync(config);
        var oldRestore = Path.Combine(root, "before");
        await Engine.RestoreAsync(old.SnapshotId, oldRestore);
        var roots = Directory.GetDirectories(oldRestore);
        Assert.Equal(2, roots.Length);
        var firstRoot = roots.Single(p => File.ReadAllText(Path.Combine(p, "same.txt")) == "first folder");
        var secondRoot = roots.Single(p => p != firstRoot);
        config.Sources.Reverse();
        File.Delete(Path.Combine(first, "same.txt"));
        clock.Now = clock.Now.AddHours(1);
        var latest = await Engine.BackupAsync(config);
        var destination = Path.Combine(root, "after");
        await Engine.RestoreAsync(latest.SnapshotId, destination);
        Assert.Empty(Directory.GetFiles(Path.Combine(destination, Path.GetFileName(firstRoot))));
        Assert.Equal("second folder", await File.ReadAllTextAsync(Path.Combine(destination, Path.GetFileName(secondRoot), "same.txt")));
    }
}
