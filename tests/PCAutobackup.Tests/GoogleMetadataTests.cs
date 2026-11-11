using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCAutobackup.Core.Google;

namespace PCAutobackup.Tests;

public sealed partial class GoogleTests
{
    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("parents")]
    [InlineData("pcabRole")]
    [InlineData("mimeType")]
    [InlineData("binary")]
    public async Task MetadataReportsExactConflictAndAcceptsBinaryClassification(string field)
    {
        string? id = null, uploadId = null; bool uploaded = false;
        int creates = 0, puts = 0, searches = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                var item = JsonSerializer.SerializeToNode(FileObject(id ?? "reserved", "chunks/meta", 3, uploadId))!;
                if (request.RequestUri!.AbsolutePath == "/drive/v3/files")
                {
                    Assert.DoesNotContain("mimeType='application/octet-stream'", Uri.UnescapeDataString(request.RequestUri.Query));
                    if (!uploaded || ++searches == 1) return Json(new { files = Array.Empty<object>() });
                    item["mimeType"] = "image/jp2";
                    return Json(new { files = new[] { item } });
                }
                switch (field)
                {
                    case "id": item["id"] = "wrong-id"; break;
                    case "name": item["name"] = "secret-wrong-name"; break;
                    case "parents": item["parents"] = new JsonArray("secret-parent"); break;
                    case "pcabRole": item["appProperties"]!["pcabRole"] = "secret-role"; break;
                    case "mimeType": item["mimeType"] = "application/vnd.google-apps.document"; break;
                    case "binary": item["mimeType"] = "image/jp2"; break;
                }
                return Json(item);
            }
            if (request.Method == HttpMethod.Post)
            {
                creates++;
                using var metadata = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                id = metadata.RootElement.GetProperty("id").GetString();
                uploadId = metadata.RootElement.GetProperty("appProperties").GetProperty("uploadId").GetString();
                var response = Json(new { }); response.Headers.Location = new("https://www.googleapis.com/upload/meta"); return response;
            }
            puts++; uploaded = true; return Json(new { id });
        }));
        var session = new GoogleSession(Config(), () => Task.CompletedTask, http) { DelayAsync = (_, _) => Task.CompletedTask };
        await using var store = new GoogleDriveBackupStore(session, "synthetic-repo", true);
        using var content = new MemoryStream([1, 2, 3]);
        if (field == "binary") await store.PutAsync("chunks/meta", content);
        else
        {
            var error = await Assert.ThrowsAsync<IOException>(() => store.PutAsync("chunks/meta", content));
            Assert.EndsWith(": " + field, error.Message);
            using var log = JsonDocument.Parse((await File.ReadAllLinesAsync(GoogleDiagnostics.LogPath)).Last(line => line.Contains(uploadId!) && line.Contains("identity-conflict")));
            Assert.Equal(field == "mimeType" ? "unsupported" : "mismatch", log.RootElement.GetProperty("MetadataChecks").GetProperty(field).GetString());
        }
        Assert.Equal(1, creates); Assert.Equal(1, puts);
        var text = await File.ReadAllTextAsync(GoogleDiagnostics.LogPath);
        Assert.DoesNotContain("secret-wrong-name", text); Assert.DoesNotContain("secret-parent", text); Assert.DoesNotContain("secret-role", text);
    }

    [Theory]
    [InlineData("image/jp2", false)]
    [InlineData("application/pdf", false)]
    [InlineData("application/vnd.google-apps.folder", true)]
    [InlineData("application/vnd.google-apps.shortcut", true)]
    [InlineData("application/vnd.google-apps.document", true)]
    public async Task BinaryObjectsRemainVisibleForReadListDeleteButWorkspaceObjectsFailClosed(string mime, bool rejected)
    {
        int deletes = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Delete) { deletes++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
            var item = JsonSerializer.SerializeToNode(FileObject("object", "chunks/meta"))!;
            item["mimeType"] = mime;
            return Task.FromResult(Json(new { files = new[] { item } }));
        }));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo");
        async Task List() { int count = 0; await foreach (var item in store.ListAsync("chunks/")) count++; Assert.Equal(1, count); }
        if (rejected)
        {
            await Assert.ThrowsAsync<IOException>(() => store.ExistsAsync("chunks/meta"));
            await Assert.ThrowsAsync<IOException>(List);
            await Assert.ThrowsAsync<IOException>(() => store.DeleteAsync("chunks/meta"));
            Assert.Equal(0, deletes);
        }
        else { Assert.True(await store.ExistsAsync("chunks/meta")); await List(); await store.DeleteAsync("chunks/meta"); Assert.Equal(1, deletes); }
    }
}
