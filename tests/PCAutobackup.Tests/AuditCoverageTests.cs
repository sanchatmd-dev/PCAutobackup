using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PCAutobackup.Core;
using PCAutobackup.Core.Google;
using Xunit.Abstractions;

namespace PCAutobackup.Tests;

// Recheck of finding 16: behaviours of the audit fixes that the audit tests do not exercise.
// Every test passes when the behaviour is correct. Synthetic fixtures in a temporary directory only.
public sealed class AuditCoverageTests : IDisposable
{
    private const int ChunkSize = 4 * 1024 * 1024;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(180);
    private readonly ITestOutputHelper output;
    private readonly string root = Path.Combine(Path.GetTempPath(), "PCAutobackup-tests", Guid.NewGuid().ToString("N"));
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    private string Source => Path.Combine(root, "source");
    private string Repo => Path.Combine(root, "repo");
    public AuditCoverageTests(ITestOutputHelper output) { this.output = output; Directory.CreateDirectory(Source); Directory.CreateDirectory(Repo); }
    public void Dispose()
    {
        foreach (string link in links) if (Directory.Exists(link)) Directory.Delete(link);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
    private readonly List<string> links = [];

    private AppConfiguration Config() => new() { DeviceId = "recheck-device", DeviceName = "fixture", Sources = [new BackupSource { Id = "source", Path = Source }] };
    private static string SourceFolder(string restore) => Assert.Single(Directory.GetDirectories(restore));
    private static string ChunkIdOf(byte[] recoveryKey, byte[] content)
    {
        byte[] namingKey = HMACSHA256.HashData(recoveryKey, Encoding.UTF8.GetBytes("PCAutobackup chunk names v1"));
        return Convert.ToHexStringLower(HMACSHA256.HashData(namingKey, content));
    }
    private static string ChunkFile(string repository, byte[] recoveryKey, byte[] content) => Path.Combine(repository, "chunks", ChunkIdOf(recoveryKey, content) + ".chunk");
    private static void FlipLastByte(string file) { byte[] bytes = File.ReadAllBytes(file); bytes[^1] ^= 1; File.WriteAllBytes(file, bytes); }
    private static byte[][] Chunks(int count) => Enumerable.Range(0, count).Select(_ => RandomNumberGenerator.GetBytes(ChunkSize)).ToArray();
    private static async Task WriteChunks(string file, byte[][] chunks, byte[]? tail = null)
    {
        await using var stream = File.Create(file);
        foreach (byte[] chunk in chunks) await stream.WriteAsync(chunk);
        if (tail is not null) await stream.WriteAsync(tail);
    }

    // ---------------------------------------------------------------- G5, second half

    private const string State = "expected-state";

    // The request arrives complete, then the connection is reset, so the courtesy page cannot be sent.
    private static async Task SendRequestThenResetAsync(int port, string requestLine, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, port, ct);
        var stream = client.GetStream();
        byte[] request = Encoding.ASCII.GetBytes(requestLine + "\r\nHost: 127.0.0.1\r\n\r\n");
        await stream.WriteAsync(request.AsMemory(0, request.Length - 1), ct);
        await Task.Delay(500, ct); // the listener has consumed everything so far and is parked in its next one-byte read
        await stream.WriteAsync(request.AsMemory(request.Length - 1, 1), ct);
        client.LingerState = new LingerOption(true, 0);
        client.Close();
    }

    [Fact]
    public async Task G5_ValidCallbackIsKeptWhenTheBrowserResetsBeforeTheReply()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var callback = GoogleAccountService.ReadCallbackAsync(listener, port, State, "en", limit.Token, TimeSpan.FromSeconds(30));
            int attempts = 0;
            while (attempts < 5 && !callback.IsCompleted)
            {
                attempts++;
                await SendRequestThenResetAsync(port, $"GET /oauth2callback?state={State}&code=kept-code HTTP/1.1", limit.Token);
                await Task.WhenAny(callback, Task.Delay(TimeSpan.FromSeconds(5), limit.Token));
            }
            output.WriteLine("G5: callback completed after " + attempts + " reset connection(s); status " + callback.Status);
            Assert.True(callback.IsCompleted, "Inconclusive: the reset always arrived before the request was read.");
            Assert.Equal("kept-code", await callback);
        }
        finally { listener.Stop(); }
    }

    // Control for the test above: the released (7c27c05) callback loop, copied verbatim apart from
    // the marker, loses the code under exactly the same client behaviour. This shows the client
    // really makes the reply fail after the whole request was read.
    [Fact]
    public async Task G5_Control_SameClientMakesTheReleasedLoopLoseTheCodeAtTheReply()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        bool lostAtReply = false; int attempts = 0;
        while (attempts < 5 && !lostAtReply)
        {
            attempts++;
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var reached = new StrongBox<bool>();
                var callback = ReleasedReadCallbackAsync(listener, port, State, reached, limit.Token);
                await SendRequestThenResetAsync(port, $"GET /oauth2callback?state={State}&code=kept-code HTTP/1.1", limit.Token);
                await Task.WhenAny(callback, Task.Delay(TimeSpan.FromSeconds(15), limit.Token));
                output.WriteLine($"G5 control attempt {attempts}: status {callback.Status}, reached reply {reached.Value}, error {callback.Exception?.InnerException?.GetType().Name}");
                lostAtReply = callback.IsFaulted && reached.Value && callback.Exception!.InnerException is IOException or SocketException;
            }
            finally { listener.Stop(); }
        }
        Assert.True(lostAtReply, "The reset never made the reply fail on this machine; the G5 test above would then not reach its trigger.");
    }

    private sealed class StrongBox<T> { public T? Value; }

    private static async Task<string> ReleasedReadCallbackAsync(TcpListener listener, int port, string state, StrongBox<bool> reachedReply, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            using var stream = client.GetStream();
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var header = new List<byte>();
            var one = new byte[1];
            while (header.Count < 16384 && await stream.ReadAsync(one, requestTimeout.Token) != 0)
            {
                header.Add(one[0]);
                if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
            }
            var line = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", 2)[0].Split(' ');
            if (line.Length != 3 || line[0] != "GET" || !line[1].StartsWith("/oauth2callback?", StringComparison.Ordinal))
            {
                await ReleasedReplyAsync(stream, false, ct);
                continue;
            }
            var uri = new Uri($"http://127.0.0.1:{port}" + line[1]);
            var pairs = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).ToArray();
            var query = pairs.ToDictionary(x => Uri.UnescapeDataString(x[0]), x => x.Length == 2 ? Uri.UnescapeDataString(x[1].Replace('+', ' ')) : "");
            var valid = GoogleAccountService.IsValidState(state, query.GetValueOrDefault("state"));
            var ok = valid && !query.ContainsKey("error") && !string.IsNullOrWhiteSpace(query.GetValueOrDefault("code"));
            reachedReply.Value = true;
            await ReleasedReplyAsync(stream, ok, ct);
            if (!ok) throw new InvalidOperationException("rejected");
            return query["code"];
        }
    }

    private static async Task ReleasedReplyAsync(Stream stream, bool ok, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(ok ? "Google connected. Return to PC AutoBackup." : "Connection failed. Return to PC AutoBackup and try again.");
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {(ok ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(body, ct);
    }

    // ---------------------------------------------------------------- E6 on the production paths

    private sealed record PartialFixture(string Snapshot, byte[][] Missing, byte[][] Damaged, byte[] Intact);

    // a-missing.bin loses its last chunk, b-damaged.bin has a corrupted middle chunk, z-intact.bin is whole.
    private async Task<PartialFixture> PreparePartial(string repository, byte[] recoveryKey, Func<Task<string>> backup)
    {
        byte[][] missing = Chunks(4), damaged = Chunks(3);
        byte[] intact = RandomNumberGenerator.GetBytes(ChunkSize + 1000);
        await WriteChunks(Path.Combine(Source, "a-missing.bin"), missing);
        await WriteChunks(Path.Combine(Source, "b-damaged.bin"), damaged);
        await File.WriteAllBytesAsync(Path.Combine(Source, "z-intact.bin"), intact);
        string snapshot = await backup();
        File.Delete(ChunkFile(repository, recoveryKey, missing[3]));
        FlipLastByte(ChunkFile(repository, recoveryKey, damaged[1]));
        return new(snapshot, missing, damaged, intact);
    }

    private static async Task AssertPartial(RestoreResult result, string restore, byte[] intact)
    {
        Assert.Equal(1, result.FilesRestored);
        Assert.Equal(2, result.Warnings.Count);
        Assert.Contains(result.Warnings, w => w.Contains("a-missing.bin"));
        Assert.Contains(result.Warnings, w => w.Contains("b-damaged.bin"));
        string folder = Assert.Single(Directory.GetDirectories(restore));
        Assert.Equal(intact, await File.ReadAllBytesAsync(Path.Combine(folder, "z-intact.bin")));
        Assert.False(File.Exists(Path.Combine(folder, "a-missing.bin"))); // no partial output
        Assert.False(File.Exists(Path.Combine(folder, "b-damaged.bin")));
    }

    [Fact]
    public async Task E6_SixteenParallelFetchesSkipOnlyTheMultiChunkFilesWithAMissingOrDamagedChunk()
    {
        var fixture = await PreparePartial(Repo, key, async () => (await new BackupEngine(new LocalBackupStore(Repo), key).BackupAsync(Config()).WaitAsync(Limit)).SnapshotId);
        string restore = Path.Combine(root, "restore");
        var engine = new BackupEngine(new LocalBackupStore(Repo), key, maxConcurrentRestoreChunks: 16);
        var result = await engine.RestoreAsync(fixture.Snapshot, restore).WaitAsync(Limit);
        await AssertPartial(result, restore, fixture.Intact);
    }

    // Forces the order the finding describes: the fetch that is awaited first ends with the
    // sibling-triggered cancellation, and only the stored chunk failure says why.
    private sealed class FirstFetchBlocksStore(IBackupStore inner, string blockedChunk, string missingChunk) : IBackupStore
    {
        private readonly TaskCompletionSource blockedStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int BlockedCancelled;
        public Task<bool> ExistsAsync(string name, CancellationToken ct = default) => inner.ExistsAsync(name, ct);
        public async Task<Stream> OpenReadAsync(string name, CancellationToken ct = default)
        {
            if (name == blockedChunk)
            {
                blockedStarted.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) { Interlocked.Increment(ref BlockedCancelled); throw; }
            }
            if (name == missingChunk)
            {
                await blockedStarted.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
                throw new FileNotFoundException("Simulated missing object", name);
            }
            return await inner.OpenReadAsync(name, ct);
        }
        public Task PutAsync(string name, Stream content, bool overwrite = false, CancellationToken ct = default) => inner.PutAsync(name, content, overwrite, ct);
        public Task DeleteAsync(string name, CancellationToken ct = default) => inner.DeleteAsync(name, ct);
        public IAsyncEnumerable<StoredObject> ListAsync(string prefix, CancellationToken ct = default) => inner.ListAsync(prefix, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Fact]
    public async Task E6_SiblingCancellationAwaitedFirstStillSkipsOnlyTheFileWithTheMissingChunk()
    {
        byte[][] broken = Chunks(3);
        await WriteChunks(Path.Combine(Source, "a-broken.bin"), broken);
        await File.WriteAllTextAsync(Path.Combine(Source, "z-intact.txt"), "intact");
        var snapshot = await new BackupEngine(new LocalBackupStore(Repo), key).BackupAsync(Config()).WaitAsync(Limit);
        var store = new FirstFetchBlocksStore(new LocalBackupStore(Repo),
            "chunks/" + ChunkIdOf(key, broken[0]) + ".chunk", "chunks/" + ChunkIdOf(key, broken[2]) + ".chunk");
        string restore = Path.Combine(root, "restore");
        var result = await new BackupEngine(store, key, maxConcurrentRestoreChunks: 16).RestoreAsync(snapshot.SnapshotId, restore).WaitAsync(Limit);

        Assert.Equal(1, store.BlockedCancelled); // the first awaited fetch really ended as a cancellation
        Assert.Equal(1, result.FilesRestored);
        Assert.Contains("a-broken.bin", Assert.Single(result.Warnings));
        Assert.Equal("intact", await File.ReadAllTextAsync(Path.Combine(SourceFolder(restore), "z-intact.txt")));
        Assert.False(File.Exists(Path.Combine(SourceFolder(restore), "a-broken.bin")));
    }

    [Fact]
    public async Task E6_ApplicationServiceRestoreReturnsThePartialResult()
    {
        string repository = Path.Combine(root, "app-repository"), recoveryFile = Path.Combine(root, "recovery.key");
        var app = new ApplicationService(Path.Combine(root, "configuration.json"));
        await app.LoadConfigurationAsync();
        app.Configuration.Destination = DestinationKind.LocalFolder;
        app.Configuration.LocalDestinationPath = repository;
        app.Configuration.IncludeApplicationInventory = false;
        app.Configuration.Sources.Add(new BackupSource { Path = Source });
        await app.ExportRecoveryKeyAsync(recoveryFile);
        byte[] appKey = Convert.FromBase64String((await File.ReadAllTextAsync(recoveryFile)).Trim()[5..]);
        var fixture = await PreparePartial(repository, appKey, async () => (await app.BackupAsync().WaitAsync(Limit)).SnapshotId);

        string restore = Path.Combine(root, "restore");
        var result = await app.RestoreAsync(fixture.Snapshot, restore).WaitAsync(Limit);
        await AssertPartial(result, restore, fixture.Intact);
    }

    // Read-only Drive served from the files of a local repository. No network.
    private sealed class RepositoryDrive(string repository) : HttpMessageHandler
    {
        public int Downloads;
        private string Local(string name) => Path.Combine(repository, name.Replace('/', Path.DirectorySeparatorChar));
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
            string path = request.RequestUri!.AbsolutePath, query = Uri.UnescapeDataString(request.RequestUri.Query);
            if (path == "/drive/v3/files")
            {
                var match = Regex.Match(query, "name='(pcab1_[A-Za-z0-9_-]+)'");
                var files = new List<object>();
                string? name = match.Success ? GoogleDriveBackupStore.DecodeName(match.Groups[1].Value) : null;
                if (name is not null && File.Exists(Local(name)))
                    files.Add(new
                    {
                        id = match.Groups[1].Value, name = match.Groups[1].Value,
                        size = new FileInfo(Local(name)).Length.ToString(System.Globalization.CultureInfo.InvariantCulture), modifiedTime = "2026-01-01T00:00:00Z",
                        appProperties = new Dictionary<string, string> { ["pcabRole"] = "object-v1", ["uploadId"] = "synthetic" },
                        parents = new[] { "synthetic-repo" }, mimeType = "application/octet-stream"
                    });
                return Task.FromResult(Json(new { files }));
            }
            if (path.StartsWith("/drive/v3/files/", StringComparison.Ordinal) && query.Contains("alt=media", StringComparison.Ordinal))
            {
                string? name = GoogleDriveBackupStore.DecodeName(Uri.UnescapeDataString(path["/drive/v3/files/".Length..]));
                if (name is null || !File.Exists(Local(name))) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                Interlocked.Increment(ref Downloads);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(Local(name))) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task E6_GoogleDriveStoreRestoreSkipsOnlyFilesWithAMissingOrDamagedChunk()
    {
        var fixture = await PreparePartial(Repo, key, async () => (await new BackupEngine(new LocalBackupStore(Repo), key).BackupAsync(Config()).WaitAsync(Limit)).SnapshotId);
        var drive = new RepositoryDrive(Repo);
        using var http = new HttpClient(drive);
        var config = new AppConfiguration
        {
            GoogleClientId = "synthetic-client", GoogleClientSecret = "synthetic-secret",
            ProtectedGoogleTokens = GoogleSession.Protect(new("synthetic-access", "synthetic-refresh", DateTimeOffset.UtcNow.AddHours(1), "openid email https://www.googleapis.com/auth/drive.file"))
        };
        // Same store settings as GoogleDriveBackupStore.CreateAsync and the production restore concurrency.
        await using var store = new GoogleDriveBackupStore(new GoogleSession(config, () => Task.CompletedTask, http), "synthetic-repo", optimizeChunkUploads: true, maxConcurrentMutations: 16, useSmallChunkMultipart: true);
        // The missing object surfaces from this store as FileNotFoundException.
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.OpenReadAsync("chunks/" + ChunkIdOf(key, fixture.Missing[3]) + ".chunk").WaitAsync(Limit));

        string restore = Path.Combine(root, "restore");
        var result = await new BackupEngine(store, key, maxConcurrentRestoreChunks: 16).RestoreAsync(fixture.Snapshot, restore).WaitAsync(Limit);
        await AssertPartial(result, restore, fixture.Intact);
        Assert.True(drive.Downloads > 0);
    }

    // ---------------------------------------------------------------- E7

    private byte[] DecryptObject(string file, string purpose)
    {
        byte[] data = File.ReadAllBytes(file);
        byte[] encryptionKey = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("PCAutobackup encryption v1"));
        byte[] plain = new byte[data.Length - 32];
        using var aes = new AesGcm(encryptionKey, 16);
        aes.Decrypt(data.AsSpan(4, 12), data.AsSpan(32), data.AsSpan(16, 16), plain, Encoding.UTF8.GetBytes(purpose));
        return plain;
    }

    private void WriteManifest(string id, string json)
    {
        byte[] plain = Encoding.UTF8.GetBytes(json);
        byte[] encrypted = new byte[32 + plain.Length];
        "PAB1"u8.CopyTo(encrypted); RandomNumberGenerator.Fill(encrypted.AsSpan(4, 12));
        byte[] encryptionKey = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("PCAutobackup encryption v1"));
        using (var aes = new AesGcm(encryptionKey, 16))
            aes.Encrypt(encrypted.AsSpan(4, 12), plain, encrypted.AsSpan(32), encrypted.AsSpan(16, 16), Encoding.UTF8.GetBytes("manifest:" + id));
        File.WriteAllBytes(Path.Combine(Repo, "snapshots", id + ".manifest"), encrypted);
    }

    [Fact]
    public async Task E7_WrittenManifestHoldsNonAsciiPathsAsRawUtf8()
    {
        string thaiFolder = "เอกสาร งานสำคัญ", thaiFile = "รายงาน ๒๕๖๙.txt";
        Directory.CreateDirectory(Path.Combine(Source, thaiFolder));
        await File.WriteAllTextAsync(Path.Combine(Source, thaiFolder, thaiFile), "เนื้อหา");
        var snapshot = await new BackupEngine(new LocalBackupStore(Repo), key).BackupAsync(Config()).WaitAsync(Limit);

        string manifest = Encoding.UTF8.GetString(DecryptObject(Path.Combine(Repo, "snapshots", snapshot.SnapshotId + ".manifest"), "manifest:" + snapshot.SnapshotId));
        Assert.Contains(thaiFolder + "/" + thaiFile, manifest);
        Assert.DoesNotContain("\\u0E", manifest, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryJunctionWithTrailingSeparator(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.Arguments = $"/d /c mklink /J \"{link}\" \"{target}\\\\\"";
        using var process = Process.Start(start)!;
        string text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30000)) { process.Kill(); return false; }
        if (Directory.Exists(link)) links.Add(link);
        string? stored = Directory.Exists(link) ? new DirectoryInfo(link).LinkTarget : null;
        output.WriteLine($"E7 junction: exit {process.ExitCode}, stored target '{stored}', output '{text.Trim()}'");
        return stored is not null && Path.EndsInDirectorySeparator(stored);
    }

    [Fact]
    public async Task E7_LinkTargetStoredWithATrailingSeparatorIsStillRecognisedAsBackedUp()
    {
        string data = Directory.CreateDirectory(Path.Combine(Source, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(data, "fixture.txt"), "content");
        bool trailing = TryJunctionWithTrailingSeparator(Path.Combine(Source, "alias"), data);
        output.WriteLine("E7 junction: trailing separator reproduced = " + trailing);
        if (!Directory.Exists(Path.Combine(Source, "alias"))) return; // junction could not be created on this volume

        var result = await new BackupEngine(new LocalBackupStore(Repo), key).BackupAsync(Config()).WaitAsync(Limit);
        string warning = Assert.Single(result.Warnings);
        Assert.Contains("original path", warning);
        Assert.Equal(1, result.FileCount);
    }

    // ---------------------------------------------------------------- A1

    private sealed class PumpContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
        public int Posts;
        public override void Post(SendOrPostCallback d, object? state) { Interlocked.Increment(ref Posts); queue.Add((d, state)); }
        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
        public void Pump(Task until, TimeSpan limit)
        {
            var clock = Stopwatch.StartNew();
            while (!until.IsCompleted)
            {
                if (clock.Elapsed > limit) throw new TimeoutException("The pumped operation did not finish.");
                if (queue.TryTake(out var work, 50)) work.Callback(work.State);
            }
        }
    }

    private sealed class InlineProgress(Action<BackupProgress> action) : IProgress<BackupProgress> { public void Report(BackupProgress value) => action(value); }

    private sealed record ContextObservation(List<string> AppReportsOnCallerThread, int BackupReports, int RestoreReports, List<string> DirectEngineReportsOnCallerThread, int Posts);

    [Fact]
    public async Task A1_BackupAndRestoreRunTheEngineOffTheCallersSynchronizationContext()
    {
        await File.WriteAllBytesAsync(Path.Combine(Source, "data.bin"), RandomNumberGenerator.GetBytes(300_000));
        var outcome = new TaskCompletionSource<ContextObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var context = new PumpContext();
                SynchronizationContext.SetSynchronizationContext(context);
                int caller = Environment.CurrentManagedThreadId;
                var onCaller = new ConcurrentQueue<string>(); var directOnCaller = new ConcurrentQueue<string>();
                int backups = 0, restores = 0;
                async Task Body()
                {
                    var app = new ApplicationService(Path.Combine(root, "configuration.json"));
                    await app.LoadConfigurationAsync();
                    app.Configuration.Destination = DestinationKind.LocalFolder;
                    app.Configuration.LocalDestinationPath = Path.Combine(root, "app-repository");
                    app.Configuration.IncludeApplicationInventory = false;
                    app.Configuration.Sources.Add(new BackupSource { Path = Source });
                    await app.ExportRecoveryKeyAsync(Path.Combine(root, "recovery.key"));
                    var result = await app.BackupAsync(new InlineProgress(p =>
                    {
                        Interlocked.Increment(ref backups);
                        if (Environment.CurrentManagedThreadId == caller) onCaller.Enqueue("backup:" + p.Stage);
                    }));
                    await app.RestoreAsync(result.SnapshotId, Path.Combine(root, "restore"), new InlineProgress(p =>
                    {
                        Interlocked.Increment(ref restores);
                        if (Environment.CurrentManagedThreadId == caller) onCaller.Enqueue("restore:" + p.Stage);
                    }));
                    Assert.Single(await app.ListSnapshotsAsync());
                    // Control: the same engine called directly, as the application service did before the fix.
                    var direct = app.CreateProductionEngine(new LocalBackupStore(Repo), key);
                    await direct.BackupAsync(Config(), new InlineProgress(p =>
                    {
                        if (Environment.CurrentManagedThreadId == caller) directOnCaller.Enqueue("backup:" + p.Stage);
                    }));
                }
                var task = Body();
                context.Pump(task, Limit);
                task.GetAwaiter().GetResult();
                outcome.SetResult(new(onCaller.ToList(), backups, restores, directOnCaller.ToList(), context.Posts));
            }
            catch (Exception e) { outcome.SetException(e); }
        }) { IsBackground = true };
        thread.Start();
        var seen = await outcome.Task.WaitAsync(Limit + TimeSpan.FromSeconds(30));

        output.WriteLine($"A1: backup reports {seen.BackupReports}, restore reports {seen.RestoreReports}, posts to caller context {seen.Posts}, app reports on caller thread [{string.Join(",", seen.AppReportsOnCallerThread)}], direct engine reports on caller thread [{string.Join(",", seen.DirectEngineReportsOnCallerThread.Distinct())}]");
        Assert.True(seen.BackupReports > 0 && seen.RestoreReports > 0);
        Assert.NotEmpty(seen.DirectEngineReportsOnCallerThread); // the control proves the probe can see engine work on the caller's thread
        Assert.Empty(seen.AppReportsOnCallerThread);
    }

    // ---------------------------------------------------------------- E3, authentic but invalid manifest

    [Theory]
    [InlineData("version")] // InvalidDataException from the manifest rules
    [InlineData("json")]    // JsonException from a truncated document
    [InlineData("null")]    // InvalidDataException from an empty document
    public async Task E3_AuthenticButInvalidManifestIsListedAsDamagedAndNamedWhenBackupStops(string kind)
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "a.txt"), "healthy");
        var config = Config(); config.RetentionDays = 0;
        BackupEngine Engine() => new(new LocalBackupStore(Repo), key);
        var healthy = await Engine().BackupAsync(config).WaitAsync(Limit);
        string id = "20261101000000000-" + Guid.NewGuid().ToString("N");
        string valid = "{\"Version\":1,\"Id\":\"" + id + "\",\"CreatedUtc\":\"2026-11-01T00:00:00+00:00\",\"DeviceName\":\"fixture\",\"Entries\":[]}";
        WriteManifest(id, kind switch { "version" => valid.Replace("\"Version\":1", "\"Version\":2"), "json" => valid[..(valid.Length / 2)], _ => "null" });

        var listed = await Engine().ListSnapshotsAsync().WaitAsync(Limit);
        Assert.Equal(2, listed.Count);
        Assert.Equal(1, Assert.Single(listed, s => s.Id == healthy.SnapshotId).FileCount);
        Assert.Equal(BackupEngine.DamagedSnapshotLabel, Assert.Single(listed, s => s.Id == id).DeviceName);

        await File.WriteAllTextAsync(Path.Combine(Source, "a.txt"), "changed");
        var blocked = await Assert.ThrowsAsync<InvalidDataException>(() => Engine().BackupAsync(config).WaitAsync(Limit));
        Assert.Contains(id, blocked.Message);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(Repo, "snapshots")).Length); // nothing written, nothing pruned
        string restore = Path.Combine(root, "restore");
        await Engine().RestoreAsync(healthy.SnapshotId, restore).WaitAsync(Limit);
        Assert.Equal("healthy", await File.ReadAllTextAsync(Path.Combine(SourceFolder(restore), "a.txt")));
    }
}
