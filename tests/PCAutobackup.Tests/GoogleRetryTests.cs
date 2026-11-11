using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PCAutobackup.Core.Google;

namespace PCAutobackup.Tests;

public sealed partial class GoogleTests
{
    [Fact]
    public async Task ConflictAfterLostInitReusesOwnedPlaceholderWithoutNewId()
    {
        string? id = null, uploadId = null; var posts = 0; var patches = 0; byte[] stored = [];
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(new { files = id is null ? Array.Empty<object>() : new[] { FileObject(id, "chunks/conflict", stored.Length, uploadId) } });
            if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Patch)
            {
                using var metadata = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var nextUploadId = metadata.RootElement.GetProperty("appProperties").GetProperty("uploadId").GetString();
                if (request.Method == HttpMethod.Post)
                {
                    var nextId = metadata.RootElement.GetProperty("id").GetString();
                    if (++posts == 1) { id = nextId; uploadId = nextUploadId; throw new HttpRequestException(); }
                    Assert.Equal(id, nextId); return new HttpResponseMessage(HttpStatusCode.Conflict);
                }
                patches++; Assert.EndsWith("/" + id, request.RequestUri!.AbsolutePath); Assert.Equal(uploadId, nextUploadId);
                Assert.False(metadata.RootElement.TryGetProperty("id", out _));
                var response = Json(new { }); response.Headers.Location = new("https://www.googleapis.com/upload/conflict-session"); return response;
            }
            stored = await request.Content!.ReadAsByteArrayAsync(); return Json(new { id });
        }));
        var session = new GoogleSession(Config(), () => Task.CompletedTask, http) { DelayAsync = (_, _) => Task.CompletedTask };
        await using var store = new GoogleDriveBackupStore(session, "synthetic-repo", true);
        using var bytes = new MemoryStream([1, 2, 3]);
        await store.PutAsync("chunks/conflict", bytes);
        Assert.Equal(2, posts); Assert.Equal(1, patches); Assert.Equal(new byte[] { 1, 2, 3 }, stored);
    }

    [Fact]
    public async Task DataRateLimitProbesSessionBeforeResending()
    {
        var puts = 0; var probes = 0; string? uploadId = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(new { files = puts < 2 ? Array.Empty<object>() : new[] { FileObject("one", "chunks/rate", 3, uploadId) } });
            if (request.Method == HttpMethod.Post)
            {
                using var metadata = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                uploadId = metadata.RootElement.GetProperty("appProperties").GetProperty("uploadId").GetString();
                var response = Json(new { }); response.Headers.Location = new("https://www.googleapis.com/upload/rate-session"); return response;
            }
            if (request.Content!.Headers.ContentRange!.From is null) { probes++; return new HttpResponseMessage((HttpStatusCode)308); }
            if (++puts == 1)
            {
                var response = Json(new { error = new { errors = new[] { new { reason = "userRateLimitExceeded" } } } });
                response.StatusCode = HttpStatusCode.Forbidden; response.Headers.RetryAfter = new(TimeSpan.Zero); return response;
            }
            Assert.Equal(1, probes); return Json(new { id = "one" });
        }));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo", true);
        using var bytes = new MemoryStream([1, 2, 3]); await store.PutAsync("chunks/rate", bytes);
        Assert.Equal(2, puts); Assert.Equal(1, probes);
    }

    [Theory]
    [InlineData("rateLimitExceeded", 2)]
    [InlineData("userRateLimitExceeded", 2)]
    [InlineData("storageQuotaExceeded", 1)]
    [InlineData("insufficientFilePermissions", 1)]
    public async Task OnlyTransientForbiddenReasonsAreRetried(string reason, int expected)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            var response = Json(new { error = new { errors = new[] { new { reason, message = "secret-body" } } } });
            response.StatusCode = ++calls == 1 ? HttpStatusCode.Forbidden : HttpStatusCode.OK;
            return Task.FromResult(response);
        }));
        var session = new GoogleSession(Config(), () => Task.CompletedTask, http) { DelayAsync = (_, _) => Task.CompletedTask };
        using var response = await session.SendAsync(() => new(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files"), default);
        Assert.Equal(expected, calls);
        if (expected == 1)
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => GoogleSession.EnsureSuccessAsync(response, default));
            Assert.Contains(reason, error.Message); Assert.DoesNotContain("secret-body", error.Message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportAndTimeoutRetryIsBoundedAndLogsRedactedCauses(bool timeout)
    {
        var attempts = 0; var delays = new List<TimeSpan>();
        using var http = new HttpClient(new Handler(_ =>
        {
            attempts++;
            if (timeout) throw new TaskCanceledException("secret-timeout-url");
            throw new HttpRequestException("secret-access-token", new SocketException((int)SocketError.ConnectionReset));
        }));
        var session = new GoogleSession(Config(), () => Task.CompletedTask, http)
        { DelayAsync = (delay, _) => { delays.Add(delay); return Task.CompletedTask; } };
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => session.SendAsync(() => new(HttpMethod.Get,
            "https://www.googleapis.com/drive/v3/files?secret-query"), default));
        Assert.Equal(GoogleSession.MaxAttempts, attempts);
        Assert.Equal(attempts - 1, delays.Count);
        Assert.All(delays, delay => Assert.InRange(delay.TotalSeconds, 1, 60));
        Assert.Contains("drive-read", error.Message);
        var log = await File.ReadAllTextAsync(GoogleDiagnostics.LogPath);
        Assert.DoesNotContain("secret-access-token", log);
        Assert.DoesNotContain("secret-timeout-url", log);
        Assert.DoesNotContain("secret-query", log);
        Assert.Contains(timeout ? "TaskCanceledException" : "ConnectionReset", log);
    }

    [Fact]
    public async Task CallerCancellationDuringBackoffStopsImmediately()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; throw new HttpRequestException(); }));
        var session = new GoogleSession(Config(), () => Task.CompletedTask, http)
        { DelayAsync = (_, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendAsync(() => new(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files"), cts.Token));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(429, 3)]
    [InlineData(503, 3)]
    [InlineData(408, 3)]
    [InlineData(403, 1)]
    public async Task HttpRetryRecoversButPermissionFailureDoesNotLoop(int status, int expectedCalls)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(++calls < 3 ? (HttpStatusCode)status : HttpStatusCode.OK))));
        var session = new GoogleSession(Config(), () => Task.CompletedTask, http) { DelayAsync = (_, _) => Task.CompletedTask };
        using var response = await session.SendAsync(() => new(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files"), default);
        Assert.Equal(expectedCalls, calls);
        Assert.Equal(status != 403, response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task ExpiredTokenRefreshRetriesTransportWithoutLosingRefreshToken()
    {
        var config = Config();
        config.ProtectedGoogleTokens = GoogleSession.Protect(new("synthetic-access", "synthetic-refresh", DateTimeOffset.UtcNow.AddHours(-1), Scopes));
        var refreshes = 0; var saves = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/token") return Task.FromResult(Json(new { }));
            if (++refreshes == 1) throw new HttpRequestException("synthetic-reset");
            return Task.FromResult(Json(new { access_token = "fresh-access", expires_in = 3600, token_type = "Bearer" }));
        }));
        var session = new GoogleSession(config, () => { saves++; return Task.CompletedTask; }, http) { DelayAsync = (_, _) => Task.CompletedTask };
        using var response = await session.SendAsync(() => new(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files"), default);
        Assert.Equal(2, refreshes); Assert.Equal(1, saves);
        Assert.True(response.IsSuccessStatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostSessionCreationAcknowledgementRetriesSameReservedIdAndRestoresBytes(bool timeout)
    {
        string? id = null, uploadId = null; var creates = 0; byte[]? stored = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(new { files = stored is null ? Array.Empty<object>() : new[] { FileObject(id!, "chunks/retry", stored.Length, uploadId) } });
            if (request.Method == HttpMethod.Post)
            {
                using var metadata = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var nextId = metadata.RootElement.GetProperty("id").GetString();
                var nextUploadId = metadata.RootElement.GetProperty("appProperties").GetProperty("uploadId").GetString();
                if (++creates == 1)
                {
                    id = nextId; uploadId = nextUploadId;
                    if (timeout) throw new TaskCanceledException();
                    throw new HttpRequestException("Lost acknowledgement");
                }
                Assert.Equal(id, nextId); Assert.Equal(uploadId, nextUploadId);
                var response = Json(new { }); response.Headers.Location = new("https://www.googleapis.com/upload/retry-session"); return response;
            }
            stored = await request.Content!.ReadAsByteArrayAsync();
            return Json(new { id });
        }));
        var session = new GoogleSession(Config(), () => Task.CompletedTask, http) { DelayAsync = (_, _) => Task.CompletedTask };
        await using var store = new GoogleDriveBackupStore(session, "synthetic-repo", true);
        using var bytes = new MemoryStream([1, 2, 3]);
        await store.PutAsync("chunks/retry", bytes);
        Assert.Equal(2, creates); Assert.Equal(new byte[] { 1, 2, 3 }, stored);
    }
}
