using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCAutobackup.Core;
using PCAutobackup.Core.Google;

namespace PCAutobackup.Tests;

// Regression tests from the Google Drive audit. They share the synthetic HTTP helpers of GoogleTests.
public sealed partial class GoogleTests
{
    // Models Drive for a repository folder that sits in the trash: files.get still returns the
    // folder and every child by ID, but each `trashed=false` listing is empty.
    private sealed class TrashedRepositoryDrive
    {
        private readonly Dictionary<string, (string Name, string UploadId, long Length)> objects = [];
        private readonly Dictionary<string, (string Id, string Name, string UploadId, long Length)> sessions = [];
        public int Mutations, Listings, FolderChecks;

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/drive/v3/files/synthetic-repo")
            {
                FolderChecks++;
                return Json(new
                {
                    id = "synthetic-repo", name = "Synthetic repository", mimeType = "application/vnd.google-apps.folder", trashed = true,
                    appProperties = new Dictionary<string, string> { ["pcabRole"] = "repository-v1" }
                });
            }
            if (request.Method == HttpMethod.Get && path == "/drive/v3/files")
            {
                Assert.Contains("trashed=false", Uri.UnescapeDataString(request.RequestUri.Query));
                Listings++;
                return Json(new { files = Array.Empty<object>() });
            }
            if (request.Method == HttpMethod.Get)
            {
                var id = path[(path.LastIndexOf('/') + 1)..];
                if (!objects.TryGetValue(id, out var item)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                var file = JsonSerializer.SerializeToNode(FileObject(id, item.Name, item.Length, item.UploadId))!;
                file["trashed"] = true;
                return Json(file);
            }
            Mutations++;
            if (request.Method == HttpMethod.Post)
            {
                using var metadata = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var id = metadata.RootElement.GetProperty("id").GetString()!;
                var name = GoogleDriveBackupStore.DecodeName(metadata.RootElement.GetProperty("name").GetString()!)!;
                var uploadId = metadata.RootElement.GetProperty("appProperties").GetProperty("uploadId").GetString()!;
                var length = long.Parse(request.Headers.GetValues("X-Upload-Content-Length").Single(), System.Globalization.CultureInfo.InvariantCulture);
                sessions["/upload/trashed-" + id] = (id, name, uploadId, length);
                var response = Json(new { }); response.Headers.Location = new("https://www.googleapis.com/upload/trashed-" + id); return response;
            }
            Assert.Equal(HttpMethod.Put, request.Method);
            var upload = sessions[path];
            objects[upload.Id] = (upload.Name, upload.UploadId, upload.Length);
            return Json(new { id = upload.Id });
        }
    }

    [Fact]
    public async Task TrashedRepositoryFolderFailsClosedBeforeAnyWrite()
    {
        var drive = new TrashedRepositoryDrive();
        using var http = new HttpClient(new Handler(drive.SendAsync));
        var config = Config();
        config.GoogleRootFolderId = "synthetic-repo";
        var session = new GoogleSession(config, () => Task.CompletedTask, http) { DelayAsync = (_, _) => Task.CompletedTask };
        // Without this check the listing looks like an empty repository: the engine uploads a
        // fresh repository/owner into the trashed folder on every run and never confirms it.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GoogleDriveBackupStore.CreateAsync(config, () => Task.CompletedTask, session));
        Assert.Contains("ถังขยะ", error.Message);
        Assert.Equal(1, drive.FolderChecks);
        Assert.Equal(0, drive.Listings);
        Assert.Equal(0, drive.Mutations);
    }

    [Fact]
    public async Task TrashedRepositoryIsNotMistakenForAnEmptyRepositoryByTheEngine()
    {
        // The same synthetic Drive, driven through a store that skips the folder check, shows what
        // the check prevents: no snapshots are listed and each run leaves another repository/owner.
        var root = Path.Combine(Path.GetTempPath(), "PCAutobackup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "synthetic");
            var drive = new TrashedRepositoryDrive();
            using var http = new HttpClient(new Handler(drive.SendAsync));
            var config = Config();
            config.DeviceId = "device-1"; config.DeviceName = "fixture"; config.Sources = [new BackupSource { Path = root }];
            var session = new GoogleSession(config, () => Task.CompletedTask, http) { DelayAsync = (_, _) => Task.CompletedTask };
            await using var bypassing = new GoogleDriveBackupStore(session, "synthetic-repo", true);
            var engine = new BackupEngine(bypassing, RandomNumberGenerator.GetBytes(32));
            Assert.Empty(await engine.ListSnapshotsAsync());
            for (var run = 1; run <= 2; run++)
            {
                var before = drive.Mutations;
                await Assert.ThrowsAsync<IOException>(() => engine.BackupAsync(config));
                Assert.True(drive.Mutations > before);
            }
            var written = drive.Mutations;
            config.GoogleRootFolderId = "synthetic-repo";
            await Assert.ThrowsAsync<InvalidOperationException>(() => GoogleDriveBackupStore.CreateAsync(config, () => Task.CompletedTask, session));
            Assert.Equal(written, drive.Mutations);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class StallingStream(byte[] head, bool stall = true) : Stream
    {
        private int position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (position < head.Length)
            {
                var count = Math.Min(buffer.Length, head.Length - position);
                head.AsMemory(position, count).CopyTo(buffer); position += count;
                return count;
            }
            // The peer went silent without closing the connection.
            if (stall) await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    [Fact]
    public async Task StreamedErrorBodyServesBothRetryClassificationAndTheFinalMessage()
    {
        var downloads = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (!request.RequestUri!.Query.Contains("alt=media")) return Task.FromResult(Json(new { files = new[] { FileObject("one", "chunks/denied") } }));
            downloads++;
            // Forward-only, like a response fetched with ResponseHeadersRead: readable once unless buffered.
            var body = JsonSerializer.SerializeToUtf8Bytes(new { error = new { errors = new[] { new { reason = "storageQuotaExceeded", message = "secret-body" } } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StreamContent(new StallingStream(body, stall: false)) });
        }));
        var session = new GoogleSession(Config(), () => Task.CompletedTask, http) { DelayAsync = (_, _) => Task.CompletedTask };
        await using var store = new GoogleDriveBackupStore(session, "synthetic-repo");
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => store.OpenReadAsync("chunks/denied"));
        Assert.EndsWith("[storageQuotaExceeded]", error.Message);
        Assert.DoesNotContain("secret-body", error.Message);
        Assert.Equal(1, downloads);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("duplicate")]
    [InlineData("cancel")]
    public async Task StalledDownloadFailsInsteadOfHangingForever(string mode)
    {
        string name = "chunks/" + new string('d', 64) + ".chunk";
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            if (request.RequestUri!.Query.Contains("alt=media"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream([1, 2, 3])) });
            var files = new List<object> { FileObject("one", name, 64) };
            if (mode == "duplicate") files.Add(FileObject("two", name, 64));
            return Task.FromResult(Json(new { files }));
        }));
        var session = new GoogleSession(Config(), () => Task.CompletedTask, http) { ReadIdleTimeout = mode == "cancel" ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(200) }; // the cancel case must not race the idle timer
        await using var store = new GoogleDriveBackupStore(session, "synthetic-repo");
        ((IAuthenticatedChunkStore)store).SetChunkVerifier((_, _) => true);
        if (mode == "duplicate")
        {
            await Assert.ThrowsAsync<IOException>(() => store.ExistsAsync(name).WaitAsync(TimeSpan.FromSeconds(5)));
            return;
        }
        await using var stream = await store.OpenReadAsync(name);
        var buffer = new byte[16];
        Assert.Equal(3, await stream.ReadAsync(buffer));
        if (mode == "cancel")
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ReadAsync(buffer, cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        else await Assert.ThrowsAsync<IOException>(() => stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TokenWithReducedScopeIsNeverUsedAfterTheFirstRejection(bool stored)
    {
        var config = Config();
        config.ProtectedGoogleTokens = GoogleSession.Protect(stored
            ? new("reduced-access", "synthetic-refresh", DateTimeOffset.UtcNow.AddHours(1), "openid email")
            : new("synthetic-access", "synthetic-refresh", DateTimeOffset.UtcNow.AddHours(-1), Scopes));
        var before = config.ProtectedGoogleTokens;
        var bearers = new List<string?>(); var saves = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/token")
                return Task.FromResult(Json(new { access_token = "reduced-access", expires_in = 3600, token_type = "Bearer", scope = "openid email" }));
            bearers.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(Json(new { files = Array.Empty<object>() }));
        }));
        var session = new GoogleSession(config, () => { saves++; return Task.CompletedTask; }, http) { DelayAsync = (_, _) => Task.CompletedTask };
        for (var call = 0; call < 2; call++)
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendAsync(() => new(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files"), default));
        Assert.Empty(bearers);
        Assert.Equal(0, saves);
        Assert.Equal(before, config.ProtectedGoogleTokens);
    }

    [Fact]
    public async Task RevokedRefreshTokenAsksTheUserToReconnect()
    {
        var config = Config();
        config.ProtectedGoogleTokens = GoogleSession.Protect(new("synthetic-access", "synthetic-refresh", DateTimeOffset.UtcNow.AddHours(-1), Scopes));
        var refreshes = 0; var driveCalls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/token") { driveCalls++; return Task.FromResult(Json(new { })); }
            refreshes++;
            var response = Json(new { error = "invalid_grant", error_description = "secret-description" });
            response.StatusCode = HttpStatusCode.BadRequest;
            return Task.FromResult(response);
        }));
        var session = new GoogleSession(config, () => Task.CompletedTask, http) { DelayAsync = (_, _) => Task.CompletedTask };
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => session.SendAsync(() => new(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files"), default));
        Assert.Contains("กรุณาเชื่อมต่อบัญชีใหม่", error.Message);
        Assert.DoesNotContain("secret-description", error.Message);
        Assert.Equal(1, refreshes); Assert.Equal(0, driveCalls);
    }

    [Fact]
    public async Task FailedDataRequestsKeepTheirFullRetryBudgetWhenProbesReportNoProgress()
    {
        int dataPuts = 0, probes = 0, creates = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Json(new { files = Array.Empty<object>() }));
            if (request.Method == HttpMethod.Post)
            {
                creates++;
                var created = Json(new { }); created.Headers.Location = new("https://www.googleapis.com/upload/budget"); return Task.FromResult(created);
            }
            if (request.Content!.Headers.ContentRange!.From is null) { probes++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)308)); }
            dataPuts++;
            var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); busy.Headers.RetryAfter = new(TimeSpan.Zero);
            return Task.FromResult(busy);
        }));
        var session = new GoogleSession(Config(), () => Task.CompletedTask, http) { DelayAsync = (_, _) => Task.CompletedTask };
        await using var store = new GoogleDriveBackupStore(session, "synthetic-repo", true);
        using var content = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<IOException>(() => store.PutAsync("chunks/budget", content));
        // One failed request is one unit: the recovery probe that reports no progress is not a second stall.
        Assert.Equal(5, dataPuts); Assert.Equal(4, probes); Assert.Equal(1, creates);
    }

    [Fact]
    public async Task ProbeWithoutRangeAfterAcknowledgedBytesFailsClosedWithoutResending()
    {
        var length = 1024 * 1024 + 17; int dataPuts = 0, probes = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Json(new { files = Array.Empty<object>() }));
            if (request.Method == HttpMethod.Post)
            {
                var created = Json(new { }); created.Headers.Location = new("https://www.googleapis.com/upload/regressed"); return Task.FromResult(created);
            }
            if (request.Content!.Headers.ContentRange!.From is null) { probes++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)308)); }
            if (++dataPuts == 2)
            {
                var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); busy.Headers.RetryAfter = new(TimeSpan.Zero);
                return Task.FromResult(busy);
            }
            var partial = new HttpResponseMessage((HttpStatusCode)308);
            partial.Headers.TryAddWithoutValidation("Range", "bytes=0-1048575"); return Task.FromResult(partial);
        }));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo");
        using var content = new MemoryStream(new byte[length]);
        await Assert.ThrowsAsync<IOException>(() => store.PutAsync("chunks/regressed", content));
        Assert.Equal(2, dataPuts); Assert.Equal(1, probes);
    }

    [Fact]
    public async Task TrustedInventoryNeverTrustsIneligibleForeignOrUnrequestedChunks()
    {
        string Id(char c) => new(c, 64);
        string Name(string id) => "chunks/" + id + ".chunk";
        JsonNode Entry(string fileId, string name, long size, Action<JsonNode>? change = null)
        {
            var node = JsonSerializer.SerializeToNode(FileObject(fileId, name, size))!;
            change?.Invoke(node);
            return node;
        }
        var requested = "0123456789abc".Select(Id).ToHashSet();
        using var http = new HttpClient(new Handler(request =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            Assert.Contains("'synthetic-repo' in parents", query); Assert.Contains("trashed=false", query);
            Assert.Contains("value='object-v1'", query); Assert.Contains("name contains 'pcab1_Y2h1bmtz'", query);
            return Task.FromResult(Json(new
            {
                files = new[]
                {
                    Entry("ok", Name(Id('0')), 64),
                    Entry("reclassified", Name(Id('1')), 64, n => n["mimeType"] = "image/jp2"),
                    Entry("workspace", Name(Id('2')), 64, n => n["mimeType"] = "application/vnd.google-apps.document"),
                    Entry("parent", Name(Id('3')), 64, n => n["parents"] = new JsonArray("other-repo")),
                    Entry("marker", Name(Id('4')), 64, n => n["appProperties"]!["pcabRole"] = "other"),
                    Entry("short", Name(Id('5')), 31),
                    Entry("long", Name(Id('6')), 4 * 1024 * 1024 + 33),
                    Entry("same-id", Name(Id('7')), 64), Entry("same-id", Name(Id('7')), 64),
                    Entry("invalid-first", Name(Id('8')), 64, n => n["mimeType"] = "application/vnd.google-apps.shortcut"), Entry("valid-second", Name(Id('8')), 64),
                    Entry("untrusted", Name(Id('f')), 64),
                    Entry("manifest", "snapshots/20261110120000000-0123456789abcdef0123456789abcdef.manifest", 64),
                    Entry("not-canonical", "chunks/" + Id('c').ToUpperInvariant() + ".chunk", 64),
                    Entry("nested", "chunks/x/" + Id('a') + ".chunk", 64),
                    Entry("foreign-name", Name(Id('b')), 64, n => n["name"] = "report.pdf"),
                }
            }));
        }));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo");
        var sizes = await store.ReadTrustedChunkSizesAsync(requested, default);
        Assert.Equal(64, sizes[Id('0')]);
        Assert.Equal(64, sizes[Id('1')]);
        // Zero can never equal an encrypted chunk length, so the engine falls back to the exact-name path.
        foreach (var c in "2345678") Assert.Equal(0, sizes[Id(c)]);
        Assert.Equal(9, sizes.Count);
        Assert.DoesNotContain(Id('f'), sizes.Keys);
    }
}
