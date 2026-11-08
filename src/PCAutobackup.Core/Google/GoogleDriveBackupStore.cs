using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCAutobackup.Core.Google;

/// <summary>A dedicated marked Drive folder. Only marked encrypted repository objects may be modified.</summary>
public sealed class GoogleDriveBackupStore : IBackupStore
{
    private const string ObjectRole = "object-v1";
    private readonly GoogleSession session;
    private readonly string folderId;
    private readonly SemaphoreSlim mutationLock = new(1, 1);
    private readonly Dictionary<string, DriveFile> lookupCache = new(StringComparer.Ordinal);
    private readonly Queue<string> cacheOrder = new();
    private const int CacheLimit = 2048;

    internal GoogleDriveBackupStore(GoogleSession session, string folderId) { this.session = session; this.folderId = folderId; }

    public static async Task<GoogleDriveBackupStore> CreateAsync(AppConfiguration config, Func<Task> saveConfiguration, CancellationToken ct = default)
    {
        var session = new GoogleSession(config, saveConfiguration);
        string folder;
        if (!string.IsNullOrWhiteSpace(config.GoogleRootFolderId))
        {
            var existing = await DriveApi.GetFileAsync(session, config.GoogleRootFolderId, ct);
            if (existing.MimeType != "application/vnd.google-apps.folder" || existing.Properties.GetValueOrDefault("pcabRole") != "repository-v1")
                throw new InvalidOperationException("โฟลเดอร์ Google Drive นี้ไม่ใช่คลัง PC AutoBackup ที่รองรับ");
            folder = existing.Id;
        }
        else
        {
            var roots = await DriveApi.ListFilesAsync(session, "trashed=false and mimeType='application/vnd.google-apps.folder' and appProperties has { key='pcabRole' and value='root-v1' }", ct);
            var root = roots.OrderBy(x => x.Id, StringComparer.Ordinal).FirstOrDefault()
                ?? await DriveApi.CreateFolderAsync(session, "PC AutoBackup", null, new() { ["pcabRole"] = "root-v1" }, ct);
            var repos = await DriveApi.ListFilesAsync(session, $"'{DriveApi.Escape(root.Id)}' in parents and trashed=false and mimeType='application/vnd.google-apps.folder' and appProperties has {{ key='pcabRole' and value='repository-v1' }} and appProperties has {{ key='deviceId' and value='{DriveApi.Escape(config.DeviceId)}' }}", ct);
            if (repos.Count > 1) throw new IOException("พบคลัง Google Drive ซ้ำสำหรับเครื่องนี้ กรุณาเลือกคลังที่จะใช้");
            var repository = repos.SingleOrDefault() ?? await DriveApi.CreateFolderAsync(session, config.DeviceName + " (" + config.DeviceId + ")", root.Id,
                new() { ["pcabRole"] = "repository-v1", ["deviceId"] = config.DeviceId, ["deviceName"] = config.DeviceName }, ct);
            folder = repository.Id;
            config.GoogleRootFolderId = folder;
            await saveConfiguration();
        }
        return new(session, folder);
    }

    public async Task<bool> ExistsAsync(string name, CancellationToken ct = default) => await FindAsync(name, ct) is not null;

    public async Task<Stream> OpenReadAsync(string name, CancellationToken ct = default)
    {
        var file = await FindAsync(name, ct) ?? throw new FileNotFoundException("ไม่พบวัตถุในคลังสำรอง", name);
        var response = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(file.Id) + "?alt=media"), ct);
        try
        {
            await GoogleSession.EnsureSuccessAsync(response, ct);
            return new ResponseStream(await response.Content.ReadAsStreamAsync(ct), response);
        }
        catch { response.Dispose(); throw; }
    }

    public async Task PutAsync(string name, Stream content, bool overwrite = false, CancellationToken ct = default)
    {
        ValidateName(name);
        await mutationLock.WaitAsync(ct);
        string? temporary = null;
        try
        {
            var existing = await FindAsync(name, ct, fresh: true);
            if (existing is not null && !overwrite) throw new IOException("มีวัตถุชื่อเดียวกันในคลังสำรองแล้ว");
            Stream upload = content;
            if (!content.CanSeek)
            {
                temporary = Path.Combine(Path.GetTempPath(), "PCAutobackup-upload-" + Guid.NewGuid().ToString("N") + ".tmp");
                var staged = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                try { await content.CopyToAsync(staged, 64 * 1024, ct); staged.Position = 0; }
                catch { await staged.DisposeAsync(); throw; }
                upload = staged;
            }
            UploadReceipt receipt;
            try { receipt = await UploadAsync(name, existing, upload, ct); }
            finally { if (!ReferenceEquals(upload, content)) await upload.DisposeAsync(); }
            // Drive permits duplicate names. Fail closed if another writer races this process.
            var committed = await FindAsync(name, ct, fresh: true) ?? throw new IOException("Google Drive ยังไม่ยืนยันวัตถุที่อัปโหลด");
            if (committed.Size != receipt.Length || committed.Properties.GetValueOrDefault("uploadId") != receipt.UploadId)
                throw new IOException("Google Drive ไม่ได้ยืนยันวัตถุอัปโหลดเวอร์ชันที่ถูกต้อง");
        }
        finally
        {
            if (temporary is not null) { try { File.Delete(temporary); } catch (IOException) { } }
            mutationLock.Release();
        }
    }

    private sealed record UploadReceipt(string UploadId, long Length);

    private async Task<UploadReceipt> UploadAsync(string name, DriveFile? existing, Stream content, CancellationToken ct)
    {
        var start = content.Position;
        var length = content.Length - start;
        var receipt = new UploadReceipt(Guid.NewGuid().ToString("N"), length);
        for (var restart = 0; ; restart++)
        {
            content.Position = start;
            try { return await UploadSessionAsync(name, existing, content, receipt, ct); }
            catch (UploadSessionExpiredException)
            {
                // A lost final acknowledgement can leave a completed object. Reconcile before
                // creating another session, and never overwrite a different writer's object.
                var found = await FindAsync(name, ct, fresh: true);
                if (found is not null)
                {
                    var ours = found.Properties.GetValueOrDefault("uploadId") == receipt.UploadId;
                    var unchangedOriginal = existing is not null && found.Id == existing.Id &&
                        found.Properties.GetValueOrDefault("uploadId") == existing.Properties.GetValueOrDefault("uploadId") &&
                        found.Size == existing.Size && found.ModifiedUtc == existing.ModifiedUtc;
                    if (!ours && !unchangedOriginal)
                        throw new IOException("พบวัตถุจากงานอื่นระหว่างเริ่มอัปโหลดใหม่ หยุดเพื่อป้องกันข้อมูลซ้ำ");
                    if (ours && found.Size == length)
                    {
                        content.Position = start;
                        var expectedHash = await SHA256.HashDataAsync(content, ct);
                        await using var stored = await OpenReadAsync(name, ct);
                        var actualHash = await SHA256.HashDataAsync(stored, ct);
                        if (CryptographicOperations.FixedTimeEquals(expectedHash, actualHash)) return receipt;
                    }
                    // Reuse this upload's placeholder (or the explicitly overwritten ID).
                    existing = found;
                }
                if (restart >= 2) throw new IOException("Google Drive upload session ใช้งานไม่ได้หลังเริ่มใหม่ครบจำนวนครั้งแล้ว");
                await Task.Delay(TimeSpan.FromSeconds(1 << restart), ct);
            }
        }
    }

    private sealed class UploadSessionExpiredException : IOException { }

    private async Task<UploadReceipt> UploadSessionAsync(string name, DriveFile? existing, Stream content, UploadReceipt receipt, CancellationToken ct)
    {
        var start = content.Position;
        var length = receipt.Length;
        var metadata = new Dictionary<string, object> { ["name"] = EncodeName(name), ["mimeType"] = "application/octet-stream", ["appProperties"] = new Dictionary<string, string> { ["pcabRole"] = ObjectRole, ["uploadId"] = receipt.UploadId } };
        if (existing is null) metadata["parents"] = new[] { folderId };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata);
        var endpoint = "https://www.googleapis.com/upload/drive/v3/files" + (existing is null ? "" : "/" + Uri.EscapeDataString(existing.Id)) + "?uploadType=resumable&fields=id";
        using var initial = await session.SendAsync(() =>
        {
            var request = new HttpRequestMessage(existing is null ? HttpMethod.Post : HttpMethod.Patch, endpoint) { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new("application/json");
            request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "application/octet-stream");
            request.Headers.TryAddWithoutValidation("X-Upload-Content-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return request;
        }, ct, retrySafe: false);
        await GoogleSession.EnsureSuccessAsync(initial, ct);
        var location = initial.Headers.Location ?? throw new IOException("Google ไม่ได้คืน upload session");
        if (!location.IsAbsoluteUri || location.Scheme != "https" || !location.Host.Equals("www.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
            !location.IsDefaultPort || !string.IsNullOrEmpty(location.UserInfo) || !string.IsNullOrEmpty(location.Fragment))
            throw new IOException("Google คืน upload endpoint ที่ไม่ถูกต้อง");
        var buffer = new byte[1024 * 1024]; // Multiple of 256 KiB, bounded independently of file size.
        long offset = 0;
        var stalled = 0;
        do
        {
            ct.ThrowIfCancellationRequested();
            content.Position = start + offset;
            var needed = (int)Math.Min(buffer.Length, length - offset);
            var read = 0;
            while (read < needed)
            {
                var count = await content.ReadAsync(buffer.AsMemory(read, needed - read), ct);
                if (count == 0) throw new EndOfStreamException("สตรีมอัปโหลดสั้นกว่าขนาดที่ระบุ");
                read += count;
            }
            HttpResponseMessage? response = null;
            try
            {
                response = await session.SendAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Put, location) { Content = new ByteArrayContent(buffer, 0, needed) };
                    request.Content.Headers.ContentType = new("application/octet-stream");
                    request.Content.Headers.ContentRange = needed == 0 ? new ContentRangeHeaderValue(length) : new ContentRangeHeaderValue(offset, offset + needed - 1, length);
                    return request;
                }, ct, retrySafe: false);
            }
            catch (HttpRequestException) { /* Resolve ambiguous acknowledgement through the existing session. */ }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* HTTP timeout; probe before retrying data. */ }
            if (response is null || (int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
            {
                var delay = response?.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, stalled));
                var throttled = response is not null;
                response?.Dispose();
                if (++stalled > 4) throw new IOException("Google Drive ไม่ยืนยันสถานะอัปโหลดหลังลองซ้ำแล้ว");
                if (throttled) await Task.Delay(delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay < TimeSpan.Zero ? TimeSpan.Zero : delay, ct);
                response = await ProbeUploadAsync(location, length, ct);
            }
            using (response)
            {
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                    throw new UploadSessionExpiredException();
                if ((int)response.StatusCode == 308)
                {
                    var next = GetAcknowledgedOffset(response);
                    if (next < offset || next > offset + needed || next >= length) throw new IOException("Google คืนช่วงข้อมูลอัปโหลดที่ไม่ถูกต้อง");
                    stalled = next == offset ? stalled + 1 : 0;
                    if (stalled > 4) throw new IOException("Google Drive ไม่รับข้อมูลอัปโหลดเพิ่มเติม");
                    offset = next;
                }
                else
                {
                    await GoogleSession.EnsureSuccessAsync(response, ct);
                    if (offset + needed != length) throw new IOException("Google ยืนยันอัปโหลดก่อนข้อมูลครบ");
                    return receipt;
                }
            }
        } while (true);
    }

    private async Task<HttpResponseMessage> ProbeUploadAsync(Uri location, long length, CancellationToken ct) =>
        await session.SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Put, location) { Content = new ByteArrayContent([]) };
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(length);
            return request;
        }, ct);

    private static long GetAcknowledgedOffset(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Range", out var values)) return 0;
        var range = values.Single();
        if (!range.StartsWith("bytes=0-", StringComparison.Ordinal) || !long.TryParse(range[8..], out var last) || last < 0 || last == long.MaxValue)
            throw new IOException("Google คืนช่วงข้อมูลอัปโหลดที่ไม่ถูกต้อง");
        return last + 1;
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        await mutationLock.WaitAsync(ct);
        try
        {
            var file = await FindAsync(name, ct, fresh: true);
            if (file is null) return;
            // FindAsync restricts both direct parent and app marker; arbitrary destination files are never touched.
            using var response = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(file.Id)), ct);
            if (response.StatusCode != HttpStatusCode.NotFound) await GoogleSession.EnsureSuccessAsync(response, ct);
            lock (lookupCache) lookupCache.Remove(name);
        }
        finally { mutationLock.Release(); }
    }

    public async IAsyncEnumerable<StoredObject> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (prefix.Contains('\\') || prefix.Contains('\0')) throw new ArgumentException("Invalid repository prefix", nameof(prefix));
        await foreach (var file in DriveApi.EnumerateFilesAsync(session, BaseQuery, ct))
        {
            var name = DecodeName(file.Name);
            if (name is not null && name.StartsWith(prefix, StringComparison.Ordinal)) yield return new(name, file.Size, file.ModifiedUtc);
        }
    }

    private string BaseQuery => $"'{DriveApi.Escape(folderId)}' in parents and trashed=false and mimeType='application/octet-stream' and appProperties has {{ key='pcabRole' and value='{ObjectRole}' }}";

    private async Task<DriveFile?> FindAsync(string name, CancellationToken ct, bool fresh = false)
    {
        ValidateName(name);
        ct.ThrowIfCancellationRequested();
        lock (lookupCache)
        {
            if (!fresh && lookupCache.TryGetValue(name, out var cached)) return cached;
            if (fresh) lookupCache.Remove(name);
        }
        var files = await DriveApi.ListFilesAsync(session, BaseQuery + $" and name='{DriveApi.Escape(EncodeName(name))}'", ct);
        if (files.Count > 1) throw new IOException("พบชื่อวัตถุซ้ำใน Google Drive หยุดเพื่อป้องกันข้อมูลสำรองเสียหาย");
        var file = files.SingleOrDefault();
        if (file is not null)
        {
            lock (lookupCache)
            {
                if (!lookupCache.ContainsKey(name)) cacheOrder.Enqueue(name);
                lookupCache[name] = file;
                while (cacheOrder.Count > CacheLimit) lookupCache.Remove(cacheOrder.Dequeue());
            }
        }
        return file;
    }

    public static string EncodeName(string name)
    {
        ValidateName(name);
        return "pcab1_" + GoogleAccountService.Base64Url(Encoding.UTF8.GetBytes(name));
    }

    public static string? DecodeName(string encoded)
    {
        if (!encoded.StartsWith("pcab1_", StringComparison.Ordinal)) return null;
        try
        {
            var base64 = encoded[6..].Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
            var name = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(base64));
            ValidateName(name);
            return EncodeName(name) == encoded ? name : null;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { return null; }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 512 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl) ||
            name.Split('/').Any(x => x is "" or "." or "..")) throw new ArgumentException("Invalid repository-relative object name", nameof(name));
    }

    public ValueTask DisposeAsync() { mutationLock.Dispose(); return ValueTask.CompletedTask; }

    private sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); response.Dispose(); } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); response.Dispose(); GC.SuppressFinalize(this); }
    }
}
