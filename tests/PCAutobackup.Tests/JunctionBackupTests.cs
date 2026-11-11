using System.Diagnostics;
using System.Security.Cryptography;
using PCAutobackup.Core;

namespace PCAutobackup.Tests;

public sealed class JunctionBackupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PCAutobackup-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> links = [];
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    private string Source => Path.Combine(root, "Project");
    public JunctionBackupTests() => Directory.CreateDirectory(Source);
    private AppConfiguration Config() => new() { DeviceId = "fixture", Sources = [new BackupSource { Path = Source }], RetentionDays = 0 };
    private BackupEngine Engine() => new(new LocalBackupStore(Path.Combine(root, "repo")), key);

    private void Junction(string name, string target)
    {
        string path = Path.Combine(Source, name);
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add($"$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '{path.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' | Out-Null");
            using var process = Process.Start(start)!;
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
        }
        else Directory.CreateSymbolicLink(path, target);
        links.Add(path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InternalAliasesAndCyclesBackUpOriginalOnce(bool withProgress)
    {
        string data = Directory.CreateDirectory(Path.Combine(Source, "MATBot", "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(data, "fixture.txt"), "unique content");
        Junction("alias-one", data);
        Junction("alias-two", data);
        Junction("cycle", Source);
        var engine = Engine(); var config = Config();
        var progress = new CaptureProgress();
        var first = await engine.BackupAsync(config, withProgress ? progress : null);
        Assert.Equal(1, first.FileCount);
        Assert.Equal(14, first.TotalBytes);
        Assert.Equal(3, first.Warnings.Count);
        Assert.All(first.Warnings, warning => Assert.Contains("original path", warning));
        if (withProgress) Assert.Equal(14L, progress.Last!.TotalBytes);
        string restore = Path.Combine(root, "restore");
        await engine.RestoreAsync(first.SnapshotId, restore);
        string restoredSource = Assert.Single(Directory.GetDirectories(restore));
        Assert.Equal("unique content", await File.ReadAllTextAsync(Path.Combine(restoredSource, "MATBot", "data", "fixture.txt")));
        Assert.False(Directory.Exists(Path.Combine(restoredSource, "alias-one")));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "repo", "chunks"), "*.chunk"));
        await File.WriteAllTextAsync(Path.Combine(data, "fixture.txt"), "changed");
        var second = await engine.BackupAsync(config);
        Assert.Equal(second.SnapshotId, Assert.Single(await engine.ListSnapshotsAsync()).Id);
        File.Delete(Path.Combine(data, "fixture.txt"));
        Assert.Equal(0, (await engine.BackupAsync(config)).FileCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalOrBrokenLinkDoesNotAbortOrPruneHistory(bool broken)
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "fixture.txt"), "old");
        var engine = Engine(); var config = Config();
        var first = await engine.BackupAsync(config);
        string external = Directory.CreateDirectory(Path.Combine(root, "external")).FullName;
        Junction("outside", external);
        if (broken) Directory.Delete(external);
        else await File.WriteAllTextAsync(Path.Combine(external, "excluded.txt"), "not selected");
        await File.WriteAllTextAsync(Path.Combine(Source, "fixture.txt"), "new");
        var second = await engine.BackupAsync(config, new CaptureProgress());
        Assert.Equal(1, second.FileCount);
        Assert.Contains(second.Warnings, warning => warning.Contains("not included"));
        Assert.Contains(second.Warnings, warning => warning.Contains("cleanup were skipped"));
        Assert.Equal(2, (await engine.ListSnapshotsAsync()).Count);
        await engine.RestoreAsync(first.SnapshotId, Path.Combine(root, "restore"));
        Assert.Equal("old", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(Path.Combine(root, "restore"), "fixture.txt", SearchOption.AllDirectories))));
    }

    [Fact]
    public async Task RestoreStillRejectsJunctionDestination()
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "fixture.txt"), "content");
        var engine = Engine();
        var snapshot = await engine.BackupAsync(Config());
        string external = Directory.CreateDirectory(Path.Combine(root, "external")).FullName;
        Junction("restore-link", external);
        await Assert.ThrowsAsync<IOException>(() => engine.RestoreAsync(snapshot.SnapshotId, Path.Combine(Source, "restore-link")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(external));
    }

    private sealed class CaptureProgress : IProgress<BackupProgress>
    {
        public BackupProgress? Last { get; private set; }
        public void Report(BackupProgress value) => Last = value;
    }

    public void Dispose()
    {
        // Remove the synthetic links themselves before recursively removing the fixture.
        foreach (string link in links) Directory.Delete(link);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
