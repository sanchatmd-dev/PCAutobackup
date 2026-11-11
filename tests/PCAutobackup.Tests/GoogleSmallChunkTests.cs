using System.Net;
using System.Text.Json;
using PCAutobackup.Core.Google;

namespace PCAutobackup.Tests;

public sealed partial class GoogleTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("lost-acknowledgement")]
    [InlineData("wrong-id")]
    [InlineData("existing")]
    public async Task MultipartCreateUsesOneLookupAndConfirmsTheReservedObject(string scenario)
    {
        string name = "chunks/" + new string('a', 64) + ".chunk";
        byte[] payload = [.. Enumerable.Range(0, 64).Select(n => (byte)n)];
        string? id = null, uploadId = null;
        int reads = 0, posts = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                reads++;
                Assert.Contains("name='" + GoogleDriveBackupStore.EncodeName(name) + "'", Uri.UnescapeDataString(request.RequestUri!.Query));
                var visible = id is not null || scenario == "existing";
                string visibleId = scenario == "wrong-id" ? "unrelated" : id ?? "previous";
                return Json(new { files = visible ? new[] { FileObject(visibleId, name, payload.Length, uploadId) } : [] });
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("uploadType=multipart", request.RequestUri!.Query);
            Assert.Equal("multipart/related", request.Content!.Headers.ContentType!.MediaType);
            var parts = Assert.IsType<MultipartContent>(request.Content).ToArray();
            Assert.Equal(2, parts.Length);
            using var metadata = JsonDocument.Parse(await parts[0].ReadAsByteArrayAsync());
            var requestId = metadata.RootElement.GetProperty("id").GetString();
            var requestUploadId = metadata.RootElement.GetProperty("appProperties").GetProperty("uploadId").GetString();
            Assert.Equal(payload, await parts[1].ReadAsByteArrayAsync());
            if (++posts == 2)
            {
                Assert.Equal(id, requestId);
                Assert.Equal(uploadId, requestUploadId);
                return new HttpResponseMessage(HttpStatusCode.Conflict);
            }
            id = requestId;
            uploadId = requestUploadId;
            if (scenario == "lost-acknowledgement") throw new HttpRequestException("Synthetic lost response");
            return Json(new { id });
        }));
        var session = new GoogleSession(Config(), () => Task.CompletedTask, http) { DelayAsync = (_, _) => Task.CompletedTask };
        await using var store = new GoogleDriveBackupStore(session, "synthetic-repo", useSmallChunkMultipart: true);
        var create = store.TryCreateSmallChunkAsync(name, () => scenario == "existing" ? throw new InvalidOperationException() : payload, default);
        if (scenario == "wrong-id")
            Assert.EndsWith(": id", (await Assert.ThrowsAsync<IOException>(() => create)).Message);
        else if (scenario == "existing") Assert.Null(await create);
        else Assert.Equal(payload.Length, await create);
        Assert.Equal(scenario == "existing" ? 1 : 2, reads);
        Assert.Equal(scenario switch { "existing" => 0, "lost-acknowledgement" => 2, _ => 1 }, posts);
    }

    [Fact]
    public async Task TrustedInventoryRejectsDuplicatesAcrossPagesAndRechecksMissingEntries()
    {
        string unique = new('a', 64), duplicate = new('b', 64), missing = new('c', 64);
        string Name(string id) => "chunks/" + id + ".chunk";
        int inventoryPages = 0, exactQueries = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            string query = Uri.UnescapeDataString(request.RequestUri!.Query);
            if (query.Contains("name contains"))
            {
                inventoryPages++;
                return Task.FromResult(query.Contains("pageToken=second")
                    ? Json(new { files = new[] { FileObject("second-duplicate", Name(duplicate), 64) } })
                    : Json(new { files = new[] { FileObject("unique", Name(unique), 64), FileObject("first-duplicate", Name(duplicate), 64) }, nextPageToken = "second" }));
            }
            exactQueries++;
            Assert.Contains("name='" + GoogleDriveBackupStore.EncodeName(Name(missing)) + "'", query);
            return Task.FromResult(Json(new { files = Array.Empty<object>() }));
        }));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo");
        var inventory = await store.ReadTrustedChunkSizesAsync(new HashSet<string> { unique, duplicate, missing }, default);
        Assert.Equal(64, inventory[unique]);
        Assert.Equal(0, inventory[duplicate]);
        Assert.DoesNotContain(missing, inventory.Keys);
        await Assert.ThrowsAsync<IOException>(() => store.RecheckTrustedChunksAsync(new Dictionary<string, long> { [unique] = 64, [missing] = 64 }, default));
        Assert.Equal(4, inventoryPages);
        Assert.Equal(1, exactQueries);
    }
}
