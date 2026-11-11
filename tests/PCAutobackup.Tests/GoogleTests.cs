using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using PCAutobackup.Core;
using PCAutobackup.Core.Google;

namespace PCAutobackup.Tests;

public sealed partial class GoogleTests
{
    private const string Scopes = "openid email https://www.googleapis.com/auth/drive.file";

    [Theory]
    [InlineData("openid email")]
    [InlineData("email https://www.googleapis.com/auth/drive.file")]
    [InlineData("openid https://www.googleapis.com/auth/drive.file")]
    public void OAuthRejectsMissingScopes(string scopes) => Assert.Throws<InvalidOperationException>(() => GoogleAccountService.ValidateGrantedScopes(scopes));

    [Fact]
    public void OAuthChecksStateAndCompleteScopes()
    {
        GoogleAccountService.ValidateGrantedScopes(Scopes);
        GoogleAccountService.ValidateGrantedScopes(Scopes.Replace("email", "https://www.googleapis.com/auth/userinfo.email"));
        Assert.True(GoogleAccountService.IsValidState("expected", "expected"));
        Assert.False(GoogleAccountService.IsValidState("expected", "Expected"));
        Assert.False(GoogleAccountService.IsValidState("expected", null));
    }

    [Theory]
    [InlineData("chunks/ab/cdef")]
    [InlineData("snapshots/สวัสดี.json")]
    public void ObjectNamesRoundTrip(string name) => Assert.Equal(name, GoogleDriveBackupStore.DecodeName(GoogleDriveBackupStore.EncodeName(name)));

    [Theory]
    [InlineData("../outside")]
    [InlineData("/root")]
    [InlineData("C:/file")]
    [InlineData("a\\b")]
    [InlineData("a//b")]
    public void RejectsUnsafeNames(string name) => Assert.Throws<ArgumentException>(() => GoogleDriveBackupStore.EncodeName(name));

    [Fact]
    public async Task MissingCredentialsDoNotInventAccount()
    {
        var config = new AppConfiguration();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GoogleAccountService().SignInAsync(config, "hint@example.invalid"));
        Assert.Null(config.GoogleEmail);
        Assert.Null(config.ProtectedGoogleTokens);
    }

    [Fact]
    public async Task RefreshAfterUnauthorizedUsesProtectedTokenAndPersists()
    {
        var config = Config();
        var requests = 0; var saves = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            requests++;
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                var form = await request.Content!.ReadAsStringAsync();
                Assert.Contains("refresh_token=synthetic-refresh", form);
                return Json(new { access_token = "new-access", expires_in = 3600, token_type = "Bearer" });
            }
            if (request.Headers.Authorization!.Parameter == "synthetic-access") return new(HttpStatusCode.Unauthorized);
            Assert.Equal("new-access", request.Headers.Authorization.Parameter);
            return Json(new { files = Array.Empty<object>() });
        }));
        var session = new GoogleSession(config, () => { saves++; return Task.CompletedTask; }, http);
        using var result = await session.SendAsync(() => new(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files"), default);
        Assert.True(result.IsSuccessStatusCode);
        Assert.Equal(3, requests);
        Assert.Equal(1, saves);
        Assert.DoesNotContain("new-access", config.ProtectedGoogleTokens!);
        Assert.DoesNotContain("synthetic-refresh", config.ProtectedGoogleTokens!);
    }

    [Fact]
    public async Task PaginationFiltersForeignObjectsAndDeleteIsScoped()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            Assert.Contains("'synthetic-repo' in parents", query);
            Assert.Contains("pcabRole", query);
            calls++;
            return Task.FromResult(calls == 1
                ? Json(new { nextPageToken = "page-two", files = new[] { FileObject("first", "chunks/a") } })
                : Json(new { files = new[] { FileObject("second", "snapshots/b") } }));
        }));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo");
        var names = new List<string>();
        await foreach (var item in store.ListAsync("chunks/")) names.Add(item.Name);
        Assert.Equal(new[] { "chunks/a" }, names);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DuplicatesFailClosedAndExistingObjectIsNotOverwritten()
    {
        var mutations = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method != HttpMethod.Get) mutations++;
            return Task.FromResult(Json(new { files = new[] { FileObject("one", "chunks/a"), FileObject("two", "chunks/a") } }));
        }));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo");
        await Assert.ThrowsAsync<IOException>(() => store.DeleteAsync("chunks/a"));
        using var content = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<IOException>(() => store.PutAsync("chunks/a", content));
        Assert.Equal(0, mutations);
    }

    [Fact]
    public async Task ResumableUploadStreamsChunksAndCommitsAfterLastByte()
    {
        var committed = false; var uploads = 0; long received = 0; string? uploadId = null;
        var expected = 1024 * 1024 + 17;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return committed ? Json(new { files = new[] { FileObject("uploaded", "chunks/new", expected, uploadId) } }) : Json(new { files = Array.Empty<object>() });
            if (request.Method == HttpMethod.Post)
            {
                using var metadata = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                uploadId = metadata.RootElement.GetProperty("appProperties").GetProperty("uploadId").GetString();
                var result = Json(new { });
                result.Headers.Location = new("https://www.googleapis.com/upload/synthetic-session");
                return result;
            }
            Assert.Equal(HttpMethod.Put, request.Method);
            uploads++;
            var payload = await request.Content!.ReadAsByteArrayAsync();
            Assert.InRange(payload.Length, 1, 1024 * 1024);
            Assert.Equal(received, request.Content.Headers.ContentRange!.From);
            received += payload.Length;
            if (received == expected) { committed = true; return Json(new { id = "uploaded" }); }
            var partial = new HttpResponseMessage((HttpStatusCode)308);
            partial.Headers.TryAddWithoutValidation("Range", $"bytes=0-{received - 1}");
            return partial;
        }));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo");
        using var content = new MemoryStream(new byte[expected]);
        await store.PutAsync("chunks/new", content);
        Assert.True(committed);
        Assert.Equal(expected, received);
        Assert.Equal(2, uploads);
    }

    [Fact]
    public async Task CancellationDoesNotStartUpload()
    {
        using var http = new HttpClient(new Handler(_ => throw new Exception("No request expected")));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        using var content = new MemoryStream([1]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PutAsync("chunks/a", content, ct: cts.Token));
    }

    [Theory]
    [InlineData("https://attacker.invalid/upload")]
    [InlineData("http://www.googleapis.com/upload")]
    [InlineData("https://www.googleapis.com.attacker.invalid/upload")]
    [InlineData("https://www.googleapis.com:8443/upload")]
    [InlineData("https://user@www.googleapis.com/upload")]
    public async Task UploadNeverSendsBearerToUntrustedSession(string endpoint)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            Assert.Equal("www.googleapis.com", request.RequestUri!.Host);
            if (request.Method == HttpMethod.Get) return Task.FromResult(Json(new { files = Array.Empty<object>() }));
            Assert.Equal(HttpMethod.Post, request.Method);
            var response = Json(new { }); response.Headers.Location = new(endpoint); return Task.FromResult(response);
        }));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo");
        using var content = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<IOException>(() => store.PutAsync("chunks/a", content));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousCompletedUploadQueriesStatusWithoutReplayingPayload(bool optimize)
    {
        string? uploadId = null; var payloads = 0; var probes = 0; var complete = false;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return complete ? Json(new { files = new[] { FileObject("uploaded", "chunks/a", 3, uploadId) } }) : Json(new { files = Array.Empty<object>() });
            if (request.Method == HttpMethod.Post)
            {
                using var metadata = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                uploadId = metadata.RootElement.GetProperty("appProperties").GetProperty("uploadId").GetString();
                var response = Json(new { }); response.Headers.Location = new("https://www.googleapis.com/upload/session"); return response;
            }
            if (request.Content!.Headers.ContentRange!.From is not null)
            {
                payloads++; complete = true;
                throw new HttpRequestException("Synthetic lost acknowledgement");
            }
            probes++;
            Assert.Empty(await request.Content.ReadAsByteArrayAsync());
            return Json(new { id = "uploaded" });
        }));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo", optimizeChunkUploads: optimize);
        using var content = new MemoryStream(new byte[] { 9, 8, 7, 6, 1, 2, 3 }, 2, 5, false, true);
        content.Position = 2;
        await store.PutAsync("chunks/a", content);
        Assert.Equal(1, payloads); Assert.Equal(1, probes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousPartialUploadResumesFromServerAcknowledgedOffset(bool optimize)
    {
        var expected = 1024 * 1024 + 17; long received = 0; string? uploadId = null;
        var payloads = 0; var probes = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return received == expected ? Json(new { files = new[] { FileObject("uploaded", "chunks/a", expected, uploadId) } }) : Json(new { files = Array.Empty<object>() });
            if (request.Method == HttpMethod.Post)
            {
                using var metadata = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                uploadId = metadata.RootElement.GetProperty("appProperties").GetProperty("uploadId").GetString();
                var response = Json(new { }); response.Headers.Location = new("https://www.googleapis.com/upload/session"); return response;
            }
            if (request.Content!.Headers.ContentRange!.From is null)
            {
                probes++;
                var response = new HttpResponseMessage((HttpStatusCode)308); response.Headers.TryAddWithoutValidation("Range", $"bytes=0-{received - 1}"); return response;
            }
            payloads++;
            Assert.Equal(received, request.Content.Headers.ContentRange.From);
            if (payloads == 1) { received = 512 * 1024; throw new HttpRequestException("Synthetic partial transfer"); }
            received += (await request.Content.ReadAsByteArrayAsync()).Length;
            return Json(new { id = "uploaded" });
        }));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo", optimizeChunkUploads: optimize);
        using var content = new MemoryStream(new byte[expected + 7], 3, expected + 4, false, true);
        content.Position = 4;
        await store.PutAsync("chunks/a", content);
        Assert.Equal(expected, received); Assert.Equal(2, payloads); Assert.Equal(1, probes);
    }

    [Fact]
    public async Task CachedReadsSaveLookupButDeleteRechecksScope()
    {
        var queries = 0; var deletes = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Delete) { deletes++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
            queries++;
            return Task.FromResult(Json(new { files = new[] { FileObject("one", "chunks/a") } }));
        }));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo");
        Assert.True(await store.ExistsAsync("chunks/a"));
        Assert.True(await store.ExistsAsync("chunks/a"));
        Assert.Equal(1, queries);
        await store.DeleteAsync("chunks/a");
        Assert.Equal(2, queries); Assert.Equal(1, deletes);
        Assert.True(await store.ExistsAsync("chunks/a"));
        Assert.Equal(3, queries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousCreateExhaustsBoundedRetriesWithStableId(bool optimize)
    {
        var creates = 0; string? id = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get) return Json(new { files = Array.Empty<object>() });
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var nextId = body.RootElement.GetProperty("id").GetString();
            id ??= nextId; Assert.Equal(id, nextId);
            creates++;
            throw new HttpRequestException("Synthetic uncertain create");
        }));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http) { DelayAsync = (_, _) => Task.CompletedTask }, "synthetic-repo", optimizeChunkUploads: optimize);
        using var content = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<HttpRequestException>(() => store.PutAsync("chunks/a", content));
        Assert.Equal(GoogleSession.MaxAttempts, creates);
    }

    [Fact]
    public async Task CachedExistenceDoesNotHideConflictingWriterOnMutation()
    {
        var queries = 0; var mutations = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method != HttpMethod.Get) mutations++;
            queries++;
            return Task.FromResult(queries == 1
                ? Json(new { files = new[] { FileObject("one", "chunks/a") } })
                : Json(new { files = new[] { FileObject("one", "chunks/a"), FileObject("two", "chunks/a") } }));
        }));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo");
        Assert.True(await store.ExistsAsync("chunks/a"));
        using var content = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<IOException>(() => store.PutAsync("chunks/a", content, overwrite: true));
        Assert.Equal(2, queries); Assert.Equal(0, mutations);
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Gone, true)]
    [InlineData(HttpStatusCode.NotFound, true)]
    public async Task ExpiredPartialSessionRestartsOnlyCurrentObjectFromItsOriginalStreamOffset(HttpStatusCode expired, bool optimize)
    {
        var expected = new byte[1024 * 1024 + 17];
        Random.Shared.NextBytes(expected);
        var creates = 0; long received = 0; var committed = false; string? uploadId = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return committed ? Json(new { files = new[] { FileObject("uploaded", "chunks/a", expected.Length, uploadId) } }) : Json(new { files = Array.Empty<object>() });
            if (request.Method == HttpMethod.Post)
            {
                creates++;
                using var metadata = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var id = metadata.RootElement.GetProperty("appProperties").GetProperty("uploadId").GetString();
                if (uploadId is null) uploadId = id; else Assert.Equal(uploadId, id);
                received = 0;
                var response = Json(new { }); response.Headers.Location = new("https://www.googleapis.com/upload/session-" + creates); return response;
            }
            Assert.Equal(received, request.Content!.Headers.ContentRange!.From);
            if (creates == 1 && received > 0) return new(expired);
            var payload = await request.Content.ReadAsByteArrayAsync();
            Assert.Equal(expected.AsSpan((int)received, payload.Length).ToArray(), payload);
            received += creates == 1 ? 512 * 1024 : payload.Length;
            if (received == expected.Length) { committed = true; return Json(new { id = "uploaded" }); }
            var partial = new HttpResponseMessage((HttpStatusCode)308);
            partial.Headers.TryAddWithoutValidation("Range", $"bytes=0-{received - 1}"); return partial;
        }));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo", optimizeChunkUploads: optimize);
        using var content = new MemoryStream(new byte[] { 9, 8, 7, 6, 5, 4 }.Concat(expected).ToArray(), 3, expected.Length + 3, false, true);
        content.Position = 3;
        await store.PutAsync("chunks/a", content);
        Assert.Equal(2, creates); Assert.True(committed); Assert.Equal(expected.Length, received);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ExpiredProbeVerifiesBytesAndReusesOwnObjectWhenRepairIsNeeded(bool storedBytesMatch, bool optimize)
    {
        var creates = 0; var payloads = 0; var reads = 0; var complete = false; string? uploadId = null;
        byte[] expected = [4, 5, 6];
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                if (request.RequestUri!.Query == "?alt=media") { reads++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(storedBytesMatch ? expected : [9, 9, 9]) }; }
                return complete ? Json(new { files = new[] { FileObject("uploaded", "chunks/a", 3, uploadId) } }) : Json(new { files = Array.Empty<object>() });
            }
            if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Patch)
            {
                creates++;
                if (creates == 2) { Assert.Equal(HttpMethod.Patch, request.Method); Assert.Contains("/uploaded?", request.RequestUri!.AbsoluteUri); }
                using var metadata = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                uploadId = metadata.RootElement.GetProperty("appProperties").GetProperty("uploadId").GetString();
                var response = Json(new { }); response.Headers.Location = new("https://www.googleapis.com/upload/session"); return response;
            }
            if (request.Content!.Headers.ContentRange!.From is null) return new(HttpStatusCode.Gone);
            payloads++; complete = true;
            if (payloads == 2) return Json(new { id = "uploaded" });
            throw new HttpRequestException("Synthetic lost final acknowledgement");
        }));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo", optimizeChunkUploads: optimize);
        using var content = new MemoryStream(expected, 0, expected.Length, false, true);
        await store.PutAsync("chunks/a", content);
        Assert.Equal(storedBytesMatch ? 1 : 2, creates); Assert.Equal(storedBytesMatch ? 1 : 2, payloads); Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredSessionDoesNotOverwriteAnotherWritersObject(bool overwrite)
    {
        var expired = false; var creates = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(expired ? Json(new { files = new[] { FileObject("other", "chunks/a", 3, "other-upload") } }) :
                    overwrite ? Json(new { files = new[] { FileObject("other", "chunks/a", 3, "original-upload") } }) : Json(new { files = Array.Empty<object>() }));
            if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Patch)
            {
                creates++; var response = Json(new { }); response.Headers.Location = new("https://www.googleapis.com/upload/session"); return Task.FromResult(response);
            }
            expired = true; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone));
        }));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo");
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        await Assert.ThrowsAsync<IOException>(() => store.PutAsync("chunks/a", content, overwrite: overwrite));
        Assert.Equal(1, creates);
    }

    [Fact]
    public async Task RepeatedSessionExpirationHasBoundedRestarts()
    {
        var creates = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Json(new { files = Array.Empty<object>() }));
            if (request.Method == HttpMethod.Post)
            {
                creates++; var response = Json(new { }); response.Headers.Location = new("https://www.googleapis.com/upload/session"); return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone));
        }));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo");
        using var content = new MemoryStream(new byte[] { 1 });
        await Assert.ThrowsAsync<IOException>(() => store.PutAsync("chunks/a", content));
        Assert.Equal(3, creates);
    }

    [Theory]
    [InlineData(true, true, 4 * 1024 * 1024 + 32, 1)]
    [InlineData(true, false, 4 * 1024 * 1024 + 32, 1)]
    [InlineData(true, true, 4 * 1024 * 1024 + 33, 5)]
    [InlineData(false, true, 4 * 1024 * 1024 + 32, 5)]
    public async Task OptimizedChunkUsesOneFinalRequestAndPreservesStreamSlice(bool optimize, bool exposed, int length, int expectedPuts)
    {
        var bytes = new byte[length + 11];
        Random.Shared.NextBytes(bytes);
        var expected = bytes.AsSpan(11).ToArray();
        var drive = new SyntheticDrive();
        drive.OnData = (_, offset, payload) =>
        {
            Assert.Equal(expected.AsSpan((int)offset, payload.Length).ToArray(), payload);
            return Task.CompletedTask;
        };
        using var http = new HttpClient(new Handler(drive.SendAsync));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo", optimizeChunkUploads: optimize);
        // Exercise both a nonzero array origin and a nonzero current position.
        using var content = new TrackingMemoryStream(bytes, 7, bytes.Length - 7, exposed);
        content.Position = 4;
        await store.PutAsync("chunks/slice", content);
        Assert.Equal(expectedPuts, drive.DataPuts);
        Assert.Equal(content.Length, content.Position);
        if (optimize && exposed && length <= 4 * 1024 * 1024 + 32) Assert.Equal(0, content.Reads);
        else Assert.True(content.Reads > 0);
        if (expectedPuts > 1) Assert.InRange(drive.LargestPayload, 1, 1024 * 1024);
    }

    [Fact]
    public async Task ApplicationFactoryEnablesSixteenConcurrentOptimizedChunkUploads()
    {
        const int slots = 16;
        const int length = 1024 * 1024 + 17;
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var folderChecks = 0;
        var drive = new SyntheticDrive
        {
            OnData = async (_, offset, payload) =>
            {
                Assert.Equal(0L, offset);
                Assert.Equal(length, payload.Length);
                if (Interlocked.Increment(ref entered) == slots) allEntered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        };
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/drive/v3/files/synthetic-repo")
            {
                Interlocked.Increment(ref folderChecks);
                return Task.FromResult(Json(new
                {
                    id = "synthetic-repo", name = "Synthetic repository", mimeType = "application/vnd.google-apps.folder",
                    appProperties = new Dictionary<string, string> { ["pcabRole"] = "repository-v1" }
                }));
            }
            return drive.SendAsync(request);
        }));
        var config = Config();
        config.GoogleRootFolderId = "synthetic-repo";
        var saves = 0;
        Task SaveConfiguration() { saves++; return Task.CompletedTask; }
        await using var store = await GoogleDriveBackupStore.CreateAsync(config, SaveConfiguration, new GoogleSession(config, SaveConfiguration, http));
        Assert.Equal(1, folderChecks);
        Assert.Equal(0, saves);
        var names = DistinctChunkStripes(slots + 1);
        var contents = Enumerable.Range(0, slots).Select(_ => new MemoryStream(new byte[length])).ToArray();
        using var waitingContent = new MemoryStream(new byte[length]);
        var running = contents.Select((content, i) => store.PutAsync(names[i], content)).ToArray();
        try
        {
            await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cts = new CancellationTokenSource();
            var waiting = store.PutAsync(names[slots], waitingContent, ct: cts.Token);
            Assert.False(waiting.IsCompleted);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal(slots, drive.DataPuts);
            Assert.Equal(slots, drive.PeakDataPuts);
        }
        finally
        {
            release.TrySetResult();
            try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { foreach (var content in contents) content.Dispose(); }
        }
        Assert.Equal(slots, drive.Creates);
        Assert.Equal(slots, drive.DataPuts);
        Assert.Equal(length, drive.LargestPayload);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public async Task ParallelChunksUseConfiguredSlotsAndCancelledWaiterReleasesItsLocks(int slots)
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var drive = new SyntheticDrive
        {
            OnData = async (_, _, _) =>
            {
                var count = Interlocked.Increment(ref entered);
                if (count == 1) firstEntered.TrySetResult();
                if (count == slots) allEntered.TrySetResult();
                if (count <= slots) await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        };
        using var http = new HttpClient(new Handler(drive.SendAsync));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo", true, slots);
        var names = DistinctChunkStripes(slots + 1);
        var uploads = Enumerable.Range(0, slots).Select(_ => new MemoryStream([1, 2, 3])).ToArray();
        using var waitingContent = new MemoryStream([7, 8, 9]);
        var first = store.PutAsync(names[0], uploads[0]);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var running = new[] { first }.Concat(Enumerable.Range(1, slots - 1).Select(i => store.PutAsync(names[i], uploads[i]))).ToArray();
        try
        {
            await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cts = new CancellationTokenSource();
            var waiting = store.PutAsync(names[slots], waitingContent, ct: cts.Token);
            Assert.False(waiting.IsCompleted);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal(slots, drive.DataPuts);
            Assert.Equal(slots, drive.PeakDataPuts);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var content in uploads) content.Dispose();
        await store.PutAsync(names[slots], waitingContent).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(slots + 1, drive.DataPuts);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public async Task SameNameChunkConflictSerializesAndCancelledNameWaiterReleasesItsLock(int slots)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drive = new SyntheticDrive { OnData = async (_, _, _) => { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10)); } };
        using var http = new HttpClient(new Handler(drive.SendAsync));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo", true, slots);
        using var a = new MemoryStream([1, 2, 3]);
        using var b = new MemoryStream([4, 5, 6]);
        var first = store.PutAsync("chunks/same", a);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();
        var cancelled = store.PutAsync("chunks/same", b, ct: cts.Token);
        Assert.False(cancelled.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var conflicting = store.PutAsync("chunks/same", b);
        Assert.False(conflicting.IsCompleted);
        Assert.Equal(1, drive.Creates);
        release.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => conflicting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, drive.Creates);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    [InlineData(false, 8)]
    [InlineData(true, 8)]
    [InlineData(false, 16)]
    [InlineData(true, 16)]
    [InlineData(false, 32)]
    [InlineData(true, 32)]
    public async Task DeleteAndSnapshotMutationsWaitForChunksAndCancelWithoutLeakingSlots(bool snapshot, int slots)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allChunksEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exclusiveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExclusive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chunks = 0;
        var drive = new SyntheticDrive
        {
            OnData = async (name, _, _) =>
            {
                if (name.StartsWith("chunks/", StringComparison.Ordinal))
                {
                    entered.TrySetResult();
                    if (Interlocked.Increment(ref chunks) == slots) allChunksEntered.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
                else
                {
                    exclusiveEntered.TrySetResult();
                    await releaseExclusive.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
            },
            OnDelete = async () =>
            {
                exclusiveEntered.TrySetResult();
                await releaseExclusive.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        };
        using var http = new HttpClient(new Handler(drive.SendAsync));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo", true, slots);
        var names = DistinctChunkStripes(slots + 1);
        using var a = new MemoryStream([1, 2, 3]);
        using var manifest = new MemoryStream([4, 5, 6]);
        using var lateContent = new MemoryStream([7, 8, 9]);
        var first = store.PutAsync(names[0], a);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var requests = drive.Requests;
        using var cts = new CancellationTokenSource();
        var waiting = snapshot ? store.PutAsync("snapshots/test", manifest, ct: cts.Token) : store.DeleteAsync(names[0], cts.Token);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(requests, drive.Requests);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        // The exclusive waiter acquired every currently free slot before cancellation.
        // Filling the store while the first upload remains blocked proves all were returned.
        var contents = Enumerable.Range(1, slots - 1).Select(_ => new MemoryStream([1, 2, 3])).ToArray();
        var fillers = contents.Select((content, i) => store.PutAsync(names[i + 1], content)).ToArray();
        try
        {
            await allChunksEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(slots, drive.PeakDataPuts);
            var exclusive = snapshot ? store.PutAsync("snapshots/test", manifest) : store.DeleteAsync(names[0]);
            var anotherDelete = store.DeleteAsync("chunks/missing");
            Assert.False(exclusive.IsCompleted);
            Assert.False(anotherDelete.IsCompleted);
            release.TrySetResult();
            await Task.WhenAll(fillers.Prepend(first)).WaitAsync(TimeSpan.FromSeconds(5));
            await exclusiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            requests = drive.Requests;
            var lateChunk = store.PutAsync(names[slots], lateContent);
            Assert.False(lateChunk.IsCompleted);
            Assert.Equal(requests, drive.Requests);
            releaseExclusive.TrySetResult();
            await Task.WhenAll(exclusive, anotherDelete, lateChunk).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            releaseExclusive.TrySetResult();
            foreach (var content in contents) content.Dispose();
        }
        Assert.Equal(snapshot ? slots + 2 : slots + 1, drive.Creates);
        Assert.Equal(snapshot ? 0 : 1, drive.Deletes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public void MutationConcurrencyRejectsValuesOutsideExperimentalBound(int slots)
    {
        var session = new GoogleSession(Config(), () => Task.CompletedTask);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GoogleDriveBackupStore(session, "synthetic-repo", maxConcurrentMutations: slots));
    }

    private static string[] DistinctChunkStripes(int count)
    {
        var stripes = new HashSet<uint>();
        return Enumerable.Range(0, 10000).Select(i => "chunks/parallel-" + i)
            .Where(name => stripes.Add((uint)StringComparer.Ordinal.GetHashCode(name) % 64)).Take(count).ToArray();
    }

    private sealed class TrackingMemoryStream(byte[] bytes, int offset, int length, bool exposed) : MemoryStream(bytes, offset, length, false, exposed)
    {
        public int Reads { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class SyntheticDrive
    {
        private sealed record Upload(string Id, string Name, string UploadId, long Length);
        private readonly ConcurrentDictionary<string, Upload> sessions = new();
        private readonly ConcurrentDictionary<string, Upload> objects = new();
        private int creates, deletes, dataPuts, requests, activeDataPuts, peakDataPuts, largestPayload;
        public int Creates => creates;
        public int Deletes => deletes;
        public int DataPuts => dataPuts;
        public int Requests => requests;
        public int PeakDataPuts => peakDataPuts;
        public int LargestPayload => largestPayload;
        public Func<string, long, byte[], Task> OnData { get; set; } = (_, _, _) => Task.CompletedTask;
        public Func<Task> OnDelete { get; set; } = () => Task.CompletedTask;

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            Interlocked.Increment(ref requests);
            if (request.Method == HttpMethod.Get)
            {
                var query = Uri.UnescapeDataString(request.RequestUri!.Query);
                // Match Drive's exact-name predicate, including its quotes; encoded names can share prefixes.
                return Json(new { files = objects.Values.Where(x => query.Contains("name='" + GoogleDriveBackupStore.EncodeName(x.Name) + "'", StringComparison.Ordinal)).Select(x => FileObject(x.Id, x.Name, x.Length, x.UploadId)).ToArray() });
            }
            if (request.Method == HttpMethod.Delete)
            {
                Interlocked.Increment(ref deletes);
                await OnDelete();
                foreach (var item in objects.Where(x => request.RequestUri!.AbsolutePath.EndsWith("/" + x.Value.Id))) objects.TryRemove(item.Key, out _);
                return new(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Post)
            {
                var id = Interlocked.Increment(ref creates).ToString(System.Globalization.CultureInfo.InvariantCulture);
                using var metadata = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var name = GoogleDriveBackupStore.DecodeName(metadata.RootElement.GetProperty("name").GetString()!)!;
                var uploadId = metadata.RootElement.GetProperty("appProperties").GetProperty("uploadId").GetString()!;
                var length = long.Parse(request.Headers.GetValues("X-Upload-Content-Length").Single(), System.Globalization.CultureInfo.InvariantCulture);
                var path = "/upload/synthetic-" + id;
                sessions[path] = new(id, name, uploadId, length);
                var response = Json(new { }); response.Headers.Location = new("https://www.googleapis.com" + path); return response;
            }
            Assert.Equal(HttpMethod.Put, request.Method);
            Interlocked.Increment(ref dataPuts);
            var active = Interlocked.Increment(ref activeDataPuts);
            UpdateMaximum(ref peakDataPuts, active);
            try
            {
                var upload = sessions[request.RequestUri!.AbsolutePath];
                var payload = await request.Content!.ReadAsByteArrayAsync();
                UpdateMaximum(ref largestPayload, payload.Length);
                var offset = request.Content.Headers.ContentRange!.From!.Value;
                await OnData(upload.Name, offset, payload);
                var received = offset + payload.Length;
                if (received == upload.Length)
                {
                    objects[upload.Name] = upload;
                    return Json(new { id = upload.Id });
                }
                var response = new HttpResponseMessage((HttpStatusCode)308);
                response.Headers.TryAddWithoutValidation("Range", "bytes=0-" + (received - 1));
                return response;
            }
            finally { Interlocked.Decrement(ref activeDataPuts); }
        }

        private static void UpdateMaximum(ref int target, int value)
        {
            int old;
            do { old = Volatile.Read(ref target); if (old >= value) return; }
            while (Interlocked.CompareExchange(ref target, value, old) != old);
        }
    }

    private static AppConfiguration Config() => new()
    {
        GoogleClientId = "synthetic-client", GoogleClientSecret = "synthetic-secret",
        ProtectedGoogleTokens = GoogleSession.Protect(new("synthetic-access", "synthetic-refresh", DateTimeOffset.UtcNow.AddHours(1), Scopes))
    };
    private static object FileObject(string id, string name, long length = 3, string? uploadId = null) => new
    {
        id, name = GoogleDriveBackupStore.EncodeName(name), size = length.ToString(System.Globalization.CultureInfo.InvariantCulture), modifiedTime = "2026-01-01T00:00:00Z",
        appProperties = new Dictionary<string, string> { ["pcabRole"] = "object-v1", ["uploadId"] = uploadId ?? "" },
        parents = new[] { "synthetic-repo" }, mimeType = "application/octet-stream"
    };
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.AbsolutePath.EndsWith("/generateIds")
                ? Task.FromResult(Json(new { ids = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid().ToString("N")).ToArray() }))
                : handler(request);
    }
}
