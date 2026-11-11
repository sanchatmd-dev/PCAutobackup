using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCAutobackup.Core;
using PCAutobackup.Core.Google;

namespace PCAutobackup.Tests;

public sealed partial class GoogleTests
{
    private static byte[] EncryptDuplicateFixture(byte[] key, byte[] plain, string id)
    {
        var encrypted = new byte[plain.Length + 32];
        "PAB1"u8.CopyTo(encrypted); RandomNumberGenerator.Fill(encrypted.AsSpan(4, 12));
        using var aes = new AesGcm(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("PCAutobackup encryption v1")), 16);
        aes.Encrypt(encrypted.AsSpan(4, 12), plain, encrypted.AsSpan(32), encrypted.AsSpan(16, 16), Encoding.UTF8.GetBytes("chunk:" + id));
        return encrypted;
    }

    [Theory]
    [InlineData("equivalent")]
    [InlineData("corrupt")]
    [InlineData("wrong-content")]
    [InlineData("wrong-key")]
    [InlineData("no-verifier")]
    [InlineData("native")]
    [InlineData("parent")]
    [InlineData("marker")]
    [InlineData("name")]
    [InlineData("oversized")]
    [InlineData("short-metadata")]
    [InlineData("cancel")]
    public async Task DuplicateChunksRequireAuthenticationAndNeverMutate(string mode)
    {
        byte[] key = RandomNumberGenerator.GetBytes(32), plain = Encoding.UTF8.GetBytes("synthetic duplicate payload");
        string id = Convert.ToHexStringLower(HMACSHA256.HashData(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("PCAutobackup chunk names v1")), plain));
        string name = "chunks/" + id + ".chunk";
        byte[] a = EncryptDuplicateFixture(key, plain, id), b = EncryptDuplicateFixture(key, mode == "wrong-content" ? "other content"u8.ToArray() : plain, id);
        Assert.False(a.SequenceEqual(b));
        if (mode == "corrupt") b[^1] ^= 1;
        int mutations = 0, downloads = 0;
        using var cancel = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method != HttpMethod.Get) { mutations++; throw new Exception("Unexpected mutation"); }
            if (request.RequestUri!.Query.Contains("alt=media"))
            {
                downloads++;
                if (mode == "cancel") cancel.Cancel();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri.AbsolutePath.EndsWith("/a") ? a : b) });
            }
            var first = JsonSerializer.SerializeToNode(FileObject("a", name, a.Length))!;
            var second = JsonSerializer.SerializeToNode(FileObject("b", name, b.Length))!;
            second["mimeType"] = mode == "native" ? "application/vnd.google-apps.shortcut" : "image/jp2";
            if (mode == "parent") second["parents"] = new System.Text.Json.Nodes.JsonArray("other-repo");
            if (mode == "marker") second["appProperties"]!["pcabRole"] = "other";
            if (mode == "name") second["name"] = "other";
            if (mode == "oversized") second["size"] = "4194337";
            if (mode == "short-metadata") second["size"] = "32";
            return Task.FromResult(Json(new { files = new[] { second, first } }));
        }));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo");
        if (mode != "no-verifier") _ = new BackupEngine(store, mode == "wrong-key" ? RandomNumberGenerator.GetBytes(32) : key);
        if (mode == "equivalent")
        {
            Assert.True(await store.ExistsAsync(name));
            Assert.Equal(2, downloads);
            await using var read = await store.OpenReadAsync(name);
            using var memory = new MemoryStream(); await read.CopyToAsync(memory);
            Assert.Equal(a, memory.ToArray());
            Assert.Equal(3, downloads);
            var listed = new List<StoredObject>(); await foreach (var item in store.ListAsync("chunks/")) listed.Add(item);
            Assert.Equal(a.Length + b.Length, Assert.Single(listed).Size);
            // A new engine with a different recovery key must not inherit verified cache entries.
            _ = new BackupEngine(store, RandomNumberGenerator.GetBytes(32));
            await Assert.ThrowsAsync<IOException>(() => store.ExistsAsync(name));
        }
        else if (mode == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ExistsAsync(name, cancel.Token));
        else await Assert.ThrowsAsync<IOException>(() => store.ExistsAsync(name));
        await Assert.ThrowsAsync<IOException>(() => store.DeleteAsync(name));
        await Assert.ThrowsAsync<IOException>(() => store.PutAsync(name, new MemoryStream(a), overwrite: true));
        await Assert.ThrowsAsync<IOException>(() => store.PutAsync(name, new MemoryStream(a)));
        Assert.Equal(0, mutations);
    }

    [Theory]
    [InlineData("repository/owner")]
    [InlineData("snapshots/20261110120000000-0123456789abcdef0123456789abcdef.manifest")]
    public async Task DuplicateNonChunkObjectsFailClosed(string name)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(new { files = new[] { FileObject("a", name, 32), FileObject("b", name, 32) } }))));
        await using var store = new GoogleDriveBackupStore(new GoogleSession(Config(), () => Task.CompletedTask, http), "synthetic-repo");
        _ = new BackupEngine(store, RandomNumberGenerator.GetBytes(32));
        await Assert.ThrowsAsync<IOException>(() => store.ExistsAsync(name));
        await Assert.ThrowsAsync<IOException>(async () => { await foreach (var item in store.ListAsync("")) { } });
    }
}
