using System.Net;
using System.Text;
using System.Text.Json;
using PCAutobackup.Core;
using PCAutobackup.Core.Google;

namespace PCAutobackup.Tests;

public sealed class GoogleTests
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

    [Fact]
    public async Task AmbiguousCompletedUploadQueriesStatusWithoutReplayingPayload()
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
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo");
        using var content = new MemoryStream([1, 2, 3]);
        await store.PutAsync("chunks/a", content);
        Assert.Equal(1, payloads); Assert.Equal(1, probes);
    }

    [Fact]
    public async Task AmbiguousPartialUploadResumesFromServerAcknowledgedOffset()
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
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo");
        using var content = new MemoryStream(new byte[expected]);
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

    [Fact]
    public async Task AmbiguousCreateDoesNotRestartSession()
    {
        var creates = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Json(new { files = Array.Empty<object>() }));
            creates++;
            throw new HttpRequestException("Synthetic uncertain create");
        }));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo");
        using var content = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<HttpRequestException>(() => store.PutAsync("chunks/a", content));
        Assert.Equal(1, creates);
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}
