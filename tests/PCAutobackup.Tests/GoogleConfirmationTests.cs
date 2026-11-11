using System.Net;
using System.Text.Json;
using PCAutobackup.Core.Google;

namespace PCAutobackup.Tests;

public sealed partial class GoogleTests
{
    [Theory]
    [InlineData("search", false)]
    [InlineData("search", true)]
    [InlineData("version", false)]
    [InlineData("missing", false)]
    [InlineData("foreign", false)]
    [InlineData("duplicate", false)]
    [InlineData("cancel", false)]
    public async Task DelayedConfirmationNeverReuploadsOrCreatesAnotherObject(string mode, bool overwrite)
    {
        string? id = overwrite ? "existing" : null, uploadId = null;
        bool uploaded = false;
        int creates = 0, puts = 0, searches = 0, directGets = 0;
        using var cts = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                if (request.RequestUri!.AbsolutePath != "/drive/v3/files")
                {
                    directGets++;
                    Assert.EndsWith("/" + id, request.RequestUri.AbsolutePath);
                    if (mode == "missing") return new HttpResponseMessage(HttpStatusCode.NotFound);
                    if (mode == "foreign") return Json(new { id, name = "foreign", mimeType = "application/octet-stream", parents = new[] { "foreign-parent" } });
                    return Json(FileObject(id!, "chunks/delayed", 3, uploadId));
                }
                if (!uploaded) return Json(new { files = overwrite ? new[] { FileObject(id!, "chunks/delayed", 1, "old") } : Array.Empty<object>() });
                searches++;
                if (mode == "duplicate" && searches > 1)
                    return Json(new { files = new[] { FileObject(id!, "chunks/delayed", 3, uploadId), FileObject("other-writer", "chunks/delayed", 3, "other") } });
                if (mode == "missing" || searches < 3)
                    return Json(new { files = mode == "version" ? new[] { FileObject(id!, "chunks/delayed", 1, "old") } : Array.Empty<object>() });
                return Json(new { files = new[] { FileObject(id!, "chunks/delayed", 3, uploadId) } });
            }
            if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Patch)
            {
                creates++;
                Assert.Equal(overwrite ? HttpMethod.Patch : HttpMethod.Post, request.Method);
                using var metadata = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                if (!overwrite) id = metadata.RootElement.GetProperty("id").GetString();
                uploadId = metadata.RootElement.GetProperty("appProperties").GetProperty("uploadId").GetString();
                var response = Json(new { }); response.Headers.Location = new("https://www.googleapis.com/upload/delayed"); return response;
            }
            puts++; uploaded = true;
            Assert.Equal(new byte[] { 1, 2, 3 }, await request.Content!.ReadAsByteArrayAsync());
            return Json(new { id });
        }));
        var session = new GoogleSession(Config(), () => Task.CompletedTask, http)
        {
            DelayAsync = (_, ct) => { if (mode == "cancel") cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        };
        await using var store = new GoogleDriveBackupStore(session, "synthetic-repo", true);
        using var data = new MemoryStream([1, 2, 3]);
        var put = store.PutAsync("chunks/delayed", data, overwrite, cts.Token);
        if (mode == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => put);
        else if (mode is "missing" or "foreign" or "duplicate") await Assert.ThrowsAsync<IOException>(() => put);
        else
        {
            await put;
            Assert.Equal(3, searches); Assert.Equal(2, directGets);
            Assert.True(await store.ExistsAsync("chunks/delayed"));
        }
        Assert.Equal(1, creates); Assert.Equal(1, puts);
        if (mode == "missing") Assert.Equal(GoogleDriveBackupStore.ConfirmationAttempts, searches);
    }
}
