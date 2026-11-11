using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCAutobackup.Core.Google;

/// <summary>A dedicated marked Drive folder. Only marked encrypted repository objects may be modified.</summary>
public sealed class GoogleDriveBackupStore : IBackupStore, IAuthenticatedChunkStore
{
    private const string ObjectRole = "object-v1";
    private readonly GoogleSession session;
    private readonly string folderId;
    private readonly SemaphoreSlim mutationLock = new(1, 1);
    private readonly SemaphoreSlim mutationSlots;
    private readonly SemaphoreSlim[] chunkNameLocks;
    private readonly bool optimizeChunkUploads;
    internal bool UseSmallChunkMultipart { get; }
    internal const int SmallChunkPlaintextLimit = 256 * 1024;
    internal const int TrustedInventoryThreshold = 1024;
    private readonly int maxConcurrentMutations;
    private const int OptimizedChunkLimit = 4 * 1024 * 1024 + 32;
    private readonly Dictionary<string, DriveFile> lookupCache = new(StringComparer.Ordinal);
    private readonly Queue<string> cacheOrder = new();
    private const int CacheLimit = 2048;
    private Func<string, byte[], bool>? chunkVerifier;

    void IAuthenticatedChunkStore.SetChunkVerifier(Func<string, byte[], bool> verifier)
    {
        // Verification is key-specific; never reuse a selection made by another engine.
        lock (lookupCache) { lookupCache.Clear(); cacheOrder.Clear(); chunkVerifier = verifier; }
    }
    private readonly SemaphoreSlim idLock = new(1, 1);
    private readonly Queue<string> reservedIds = new();

    private async Task<string> ReserveIdAsync(CancellationToken ct)
    {
        await idLock.WaitAsync(ct);
        try
        {
            if (reservedIds.Count == 0)
            {
                using var response = await session.SendAsync(() => new(HttpMethod.Get,
                    "https://www.googleapis.com/drive/v3/files/generateIds?count=100&space=drive&type=files"), ct);
                await GoogleSession.EnsureSuccessAsync(response, ct);
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                foreach (var id in json.RootElement.GetProperty("ids").EnumerateArray())
                {
                    var value = id.GetString();
                    if (string.IsNullOrWhiteSpace(value)) throw new IOException("Google คืนรหัสไฟล์ที่จองไว้เป็นค่าว่าง");
                    reservedIds.Enqueue(value);
                }
                if (reservedIds.Count == 0) throw new IOException("Google ไม่ได้คืนรหัสไฟล์ที่จองไว้");
            }
            return reservedIds.Dequeue();
        }
        finally { idLock.Release(); }
    }

    internal GoogleDriveBackupStore(GoogleSession session, string folderId, bool optimizeChunkUploads = false, int maxConcurrentMutations = 1, bool useSmallChunkMultipart = false)
    {
        if (maxConcurrentMutations is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maxConcurrentMutations));
        this.session = session;
        this.folderId = folderId;
        this.optimizeChunkUploads = optimizeChunkUploads;
        UseSmallChunkMultipart = useSmallChunkMultipart;
        this.maxConcurrentMutations = maxConcurrentMutations;
        mutationSlots = new(maxConcurrentMutations, maxConcurrentMutations);
        chunkNameLocks = maxConcurrentMutations > 1 ? Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray() : [];
    }

    private async Task<IDisposable> AcquireMutationAsync(string name, bool chunkPut, CancellationToken ct)
    {
        // Bounded name stripes serialize conflicting uploads without retaining every object name.
        var nameLock = chunkPut && chunkNameLocks.Length > 0 ? chunkNameLocks[(uint)StringComparer.Ordinal.GetHashCode(name) % (uint)chunkNameLocks.Length] : null;
        var nameHeld = false;
        var gateHeld = false;
        var acquired = 0;
        try
        {
            if (nameLock is not null) { await nameLock.WaitAsync(ct); nameHeld = true; }
            // Only one caller acquires slots at a time. Exclusive mutations cannot each
            // hold a partial set while waiting for one another's remaining slots.
            await mutationLock.WaitAsync(ct);
            gateHeld = true;
            var required = chunkPut ? 1 : maxConcurrentMutations;
            while (acquired < required) { await mutationSlots.WaitAsync(ct); acquired++; }
            if (chunkPut) { mutationLock.Release(); gateHeld = false; }
            return new MutationLease(this, nameLock, acquired, gateHeld);
        }
        catch
        {
            if (acquired > 0) mutationSlots.Release(acquired);
            if (gateHeld) mutationLock.Release();
            if (nameHeld) nameLock!.Release();
            throw;
        }
    }

    private sealed class MutationLease(GoogleDriveBackupStore store, SemaphoreSlim? nameLock, int slots, bool gateHeld) : IDisposable
    {
        public void Dispose()
        {
            store.mutationSlots.Release(slots);
            if (gateHeld) store.mutationLock.Release();
            nameLock?.Release();
        }
    }

    public static Task<GoogleDriveBackupStore> CreateAsync(AppConfiguration config, Func<Task> saveConfiguration, CancellationToken ct = default) =>
        CreateAsync(config, saveConfiguration, new GoogleSession(config, saveConfiguration), ct);

    internal static async Task<GoogleDriveBackupStore> CreateAsync(AppConfiguration config, Func<Task> saveConfiguration, GoogleSession session, CancellationToken ct = default)
    {
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
        return new(session, folder, optimizeChunkUploads: true, maxConcurrentMutations: 16, useSmallChunkMultipart: true);
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
        using var mutation = await AcquireMutationAsync(name, name.StartsWith("chunks/", StringComparison.Ordinal), ct);
        string? temporary = null;
        try
        {
            var existing = await FindAsync(name, ct, fresh: true, allowEquivalentChunks: false);
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
            await ConfirmUploadAsync(name, receipt, ct);
        }
        finally
        {
            if (temporary is not null) { try { File.Delete(temporary); } catch (IOException) { } }
        }
    }

    private sealed record UploadReceipt(string UploadId, long Length, string FileId);

    internal async Task<int?> TryCreateSmallChunkAsync(string name, Func<byte[]> encrypt, CancellationToken ct)
    {
        if (!IsChunkName(name)) throw new ArgumentException("Expected a canonical chunk name", nameof(name));
        using var mutation = await AcquireMutationAsync(name, chunkPut: true, ct);
        // Combine the existence check and create under the same lease. Existing chunks
        // still go through the engine's normal authentication and repair path.
        if (await FindAsync(name, ct, fresh: true) is not null) return null;

        var encrypted = encrypt();
        if (encrypted.Length is < 32 or > SmallChunkPlaintextLimit + 32)
            throw new ArgumentException("Encrypted chunk exceeds the multipart size limit", nameof(encrypt));
        var receipt = new UploadReceipt(Guid.NewGuid().ToString("N"), encrypted.Length, await ReserveIdAsync(ct));
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = receipt.FileId,
            name = EncodeName(name),
            parents = new[] { folderId },
            mimeType = "application/octet-stream",
            appProperties = new Dictionary<string, string> { ["pcabRole"] = ObjectRole, ["uploadId"] = receipt.UploadId }
        });
        var boundary = "pcab-" + Guid.NewGuid().ToString("N");
        using var response = await session.SendAsync(() =>
        {
            var body = new MultipartContent("related", boundary)
            {
                new ByteArrayContent(metadata) { Headers = { ContentType = new("application/json") { CharSet = "UTF-8" } } },
                new ByteArrayContent(encrypted) { Headers = { ContentType = new("application/octet-stream") } }
            };
            return new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id") { Content = body };
        }, ct);
        // A lost response can leave the reserved ID already created. Confirm the same
        // ID and upload version after 409, with no new ID or name-based overwrite.
        if (response.StatusCode != HttpStatusCode.Conflict)
            await GoogleSession.EnsureSuccessAsync(response, ct);
        await ConfirmUploadAsync(name, receipt, ct, verifyReceiptIdentity: true);
        if (response.StatusCode == HttpStatusCode.Conflict)
            GoogleDiagnostics.Write("upload-multipart", receipt.UploadId, 1, "confirmed-after-conflict", status: 409);
        return encrypted.Length;
    }

    internal const int ConfirmationAttempts = 8;

    private async Task ConfirmUploadAsync(string name, UploadReceipt receipt, CancellationToken ct, bool verifyReceiptIdentity = false)
    {
        bool delayed = false;
        for (int attempt = 1; attempt <= ConfirmationAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            // Keep the name query: Drive allows duplicate names, even when one ID is valid.
            var found = await FindAsync(name, ct, fresh: true, allowEquivalentChunks: false);
            if (found is not null && found.Size == receipt.Length &&
                found.Properties.GetValueOrDefault("uploadId") == receipt.UploadId)
            {
                if (verifyReceiptIdentity) RequireMatchingIdentity(CheckMetadata(found, name, receipt), receipt, attempt);
                if (delayed || found.MimeType != "application/octet-stream") GoogleDiagnostics.Write("upload-confirm", receipt.UploadId, attempt,
                    delayed ? "confirmed-after-delay" : "confirmed-binary-mime", metadataChecks: CheckMetadata(found, name, receipt));
                return;
            }
            delayed = true;
            string outcome = found is null ? "search-not-visible" : "search-version-pending";
            // Inspect the same reserved/existing ID. Never create another object while confirming.
            // This is diagnostic evidence, not permission to bypass the duplicate-name query.
            try
            {
                var direct = await DriveApi.GetFileAsync(session, receipt.FileId, ct);
                var checks = CheckMetadata(direct, name, receipt);
                RequireMatchingIdentity(checks, receipt, attempt);
                GoogleDiagnostics.Write("upload-confirm", receipt.UploadId, attempt, "metadata-observed", metadataChecks: checks);
                if (direct.Size == receipt.Length && direct.Properties.GetValueOrDefault("uploadId") == receipt.UploadId)
                    outcome += "-id-confirmed";
            }
            catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { outcome += "-id-not-visible"; }
            if (attempt == ConfirmationAttempts)
            {
                GoogleDiagnostics.Write("upload-confirm", receipt.UploadId, attempt, "confirmation-exhausted-" + outcome);
                throw new IOException("Google Drive ยังไม่ยืนยันวัตถุที่อัปโหลดหลังรอตรวจซ้ำครบจำนวนครั้งแล้ว");
            }
            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt - 1)) + Random.Shared.NextDouble());
            GoogleDiagnostics.Write("upload-confirm", receipt.UploadId, attempt, outcome, delaySeconds: delay.TotalSeconds);
            await session.DelayAsync(delay, ct);
        }
    }

    private static void RequireMatchingIdentity(Dictionary<string, string> checks, UploadReceipt receipt, int attempt)
    {
        var conflicts = checks.Where(c => c.Value is "mismatch" or "unsupported").Select(c => c.Key).ToArray();
        if (conflicts.Length == 0) return;
        GoogleDiagnostics.Write("upload-confirm", receipt.UploadId, attempt, "identity-conflict", metadataChecks: checks);
        throw new IOException("Google Drive คืนวัตถุยืนยันที่ไม่ตรงกับคลังสำรอง: " + string.Join(", ", conflicts));
    }

    private static bool IsBinaryMime(string mime) => !string.IsNullOrWhiteSpace(mime) &&
        !mime.StartsWith("application/vnd.google-apps.", StringComparison.OrdinalIgnoreCase);

    private Dictionary<string, string> CheckMetadata(DriveFile file, string name, UploadReceipt receipt) => new()
    {
        ["id"] = file.Id == receipt.FileId ? "match" : "mismatch",
        ["name"] = file.Name == EncodeName(name) ? "match" : "mismatch",
        ["parents"] = file.Parents.Contains(folderId) ? "match" : "mismatch",
        ["pcabRole"] = file.Properties.GetValueOrDefault("pcabRole") == ObjectRole ? "match" : "mismatch",
        ["mimeType"] = IsBinaryMime(file.MimeType) ? file.MimeType == "application/octet-stream" ? "match" : "accepted-binary" : "unsupported",
        ["mimeTypeClass"] = file.MimeType switch { "image/jp2" => "image/jp2", "application/octet-stream" => "application/octet-stream", "" => "missing", _ => IsBinaryMime(file.MimeType) ? "other-binary" : "google-workspace-or-missing" },
        ["size"] = file.Size == receipt.Length ? "match" : "version-pending",
        ["uploadId"] = file.Properties.GetValueOrDefault("uploadId") == receipt.UploadId ? "match" : "version-pending"
    };

    private static void RequireBinaryObject(DriveFile file)
    {
        if (!IsBinaryMime(file.MimeType)) throw new IOException("วัตถุสำรองมี mimeType ที่ไม่รองรับ");
    }

    private async Task<UploadReceipt> UploadAsync(string name, DriveFile? existing, Stream content, CancellationToken ct)
    {
        var start = content.Position;
        var length = content.Length - start;

        // A stable pre-generated ID makes an uncertain create retry idempotent on Drive.
        // Keep it for every restarted session of this object; never allocate on retry.
        var reservedId = existing is null ? await ReserveIdAsync(ct) : null;
        var receipt = new UploadReceipt(Guid.NewGuid().ToString("N"), length, existing?.Id ?? reservedId!);
        for (var restart = 0; ; restart++)
        {
            content.Position = start;
            try { return await UploadSessionAsync(name, existing, content, receipt, reservedId, ct); }
            catch (UploadSessionExpiredException)
            {
                // A lost final acknowledgement can leave a completed object. Reconcile before
                // creating another session, and never overwrite a different writer's object.
                var found = await FindAsync(name, ct, fresh: true, allowEquivalentChunks: false);
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

    private async Task<UploadReceipt> UploadSessionAsync(string name, DriveFile? existing, Stream content, UploadReceipt receipt, string? reservedId, CancellationToken ct)
    {
        var start = content.Position;
        var length = receipt.Length;
        var metadata = new Dictionary<string, object> { ["name"] = EncodeName(name), ["mimeType"] = "application/octet-stream", ["appProperties"] = new Dictionary<string, string> { ["pcabRole"] = ObjectRole, ["uploadId"] = receipt.UploadId } };
        if (existing is null) { metadata["parents"] = new[] { folderId }; metadata["id"] = reservedId!; }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata);
        var endpoint = "https://www.googleapis.com/upload/drive/v3/files" + (existing is null ? "" : "/" + Uri.EscapeDataString(existing.Id)) + "?uploadType=resumable&fields=id";
        using var initial = await session.SendAsync(() =>
        {
            var request = new HttpRequestMessage(existing is null ? HttpMethod.Post : HttpMethod.Patch, endpoint) { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new("application/json");
            request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "application/octet-stream");
            request.Headers.TryAddWithoutValidation("X-Upload-Content-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return request;
        }, ct, retrySafe: true);
        // A retry can observe an already committed ID after its final acknowledgement was lost.
        if (initial.StatusCode == HttpStatusCode.Conflict) throw new UploadSessionExpiredException();
        await GoogleSession.EnsureSuccessAsync(initial, ct);
        var location = initial.Headers.Location ?? throw new IOException("Google ไม่ได้คืน upload session");
        if (!location.IsAbsoluteUri || location.Scheme != "https" || !location.Host.Equals("www.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
            !location.IsDefaultPort || !string.IsNullOrEmpty(location.UserInfo) || !string.IsNullOrEmpty(location.Fragment))
            throw new IOException("Google คืน upload endpoint ที่ไม่ถูกต้อง");
        var singleRequest = optimizeChunkUploads && name.StartsWith("chunks/", StringComparison.Ordinal) && length <= OptimizedChunkLimit;
        ArraySegment<byte> source = default;
        var direct = singleRequest && content is MemoryStream memory && memory.TryGetBuffer(out source);
        // Small encrypted chunks fit one final request. Exposed MemoryStream slices need no
        // second payload buffer; other streams and larger objects keep bounded allocations.
        var buffer = direct ? source.Array! : new byte[singleRequest ? Math.Max(1, (int)length) : 1024 * 1024]; // Streaming blocks remain multiples of 256 KiB, bounded independently of file size.
        long offset = 0;
        var stalled = 0;
        var recoveredRequest = false;
        do
        {
            ct.ThrowIfCancellationRequested();
            content.Position = start + offset;
            var needed = (int)Math.Min(singleRequest ? length : buffer.Length, length - offset);
            var bufferOffset = direct ? checked(source.Offset + (int)(start + offset)) : 0;
            var read = 0;
            while (!direct && read < needed)
            {
                var count = await content.ReadAsync(buffer.AsMemory(read, needed - read), ct);
                if (count == 0) throw new EndOfStreamException("สตรีมอัปโหลดสั้นกว่าขนาดที่ระบุ");
                read += count;
            }
            if (direct) content.Position = start + offset + needed;
            HttpResponseMessage? response = null;
            try
            {
                response = await session.SendAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Put, location) { Content = new ByteArrayContent(buffer, bufferOffset, needed) };
                    request.Content.Headers.ContentType = new("application/octet-stream");
                    request.Content.Headers.ContentRange = needed == 0 ? new ContentRangeHeaderValue(length) : new ContentRangeHeaderValue(offset, offset + needed - 1, length);
                    return request;
                }, ct, retrySafe: false);
            }
            catch (HttpRequestException) { /* Resolve ambiguous acknowledgement through the existing session. */ }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* HTTP timeout; probe before retrying data. */ }
            if (response is null || (int)response.StatusCode is 408 or 429 || (int)response.StatusCode >= 500 ||
                response.StatusCode == HttpStatusCode.Forbidden && await GoogleSession.ReadReasonAsync(response, ct) is "rateLimitExceeded" or "userRateLimitExceeded")
            {
                recoveredRequest = true;
                var delay = response?.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, stalled));
                response?.Dispose();
                if (++stalled > 4) throw new IOException("Google Drive ไม่ยืนยันสถานะอัปโหลดหลังลองซ้ำแล้ว");
                await Task.Delay(delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay < TimeSpan.Zero ? TimeSpan.Zero : delay, ct);
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
                    if (recoveredRequest) GoogleDiagnostics.Write("upload-session", receipt.UploadId, 1, "recovered-after-probe", status: (int)response.StatusCode);
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
        ValidateName(name);
        using var mutation = await AcquireMutationAsync(name, false, ct);
        var file = await FindAsync(name, ct, fresh: true, allowEquivalentChunks: false);
        if (file is null) return;
        // FindAsync restricts both direct parent and app marker; arbitrary destination files are never touched.
        using var response = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(file.Id)), ct);
        if (response.StatusCode != HttpStatusCode.NotFound) await GoogleSession.EnsureSuccessAsync(response, ct);
        lock (lookupCache) lookupCache.Remove(name);
    }

    public async IAsyncEnumerable<StoredObject> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (prefix.Contains('\\') || prefix.Contains('\0')) throw new ArgumentException("Invalid repository prefix", nameof(prefix));
        var objects = new Dictionary<string, StoredObject>(StringComparer.Ordinal);
        await foreach (var file in DriveApi.EnumerateFilesAsync(session, BaseQuery, ct))
        {
            var name = DecodeName(file.Name);
            if (name is null || !name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            RequireBinaryObject(file);
            if (objects.TryGetValue(name, out var previous))
            {
                if (!IsChunkName(name)) throw DuplicateConflict("non-chunk", 2);
                // One logical item, all physical bytes count against quota. Reads authenticate
                // every copy. Deletion of duplicates is deliberately refused, even during prune.
                objects[name] = previous with { Size = checked(previous.Size + file.Size),
                    ModifiedUtc = previous.ModifiedUtc > file.ModifiedUtc ? previous.ModifiedUtc : file.ModifiedUtc };
            }
            else objects.Add(name, new(name, file.Size, file.ModifiedUtc));
        }
        foreach (var item in objects.Values) { ct.ThrowIfCancellationRequested(); yield return item; }
    }

    private string BaseQuery => $"'{DriveApi.Escape(folderId)}' in parents and trashed=false and appProperties has {{ key='pcabRole' and value='{ObjectRole}' }}";

    internal async Task<Dictionary<string, long>> ReadTrustedChunkSizesAsync(IReadOnlySet<string> ids, CancellationToken ct)
    {
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        // Six UTF-8 bytes align with base64 groups, so this prefix covers every
        // encoded chunk name. Validate the exact decoded name as well.
        var query = BaseQuery + $" and name contains '{EncodeName("chunks")}'";
        await foreach (var file in DriveApi.EnumerateFilesAsync(session, query, ct))
        {
            var name = DecodeName(file.Name);
            if (name is null || !IsChunkName(name)) continue;
            var id = name[7..^6];
            if (!ids.Contains(id)) continue;
            // Zero means ambiguous or invalid. A subsequent record cannot restore trust.
            var eligible = IsBinaryMime(file.MimeType) && file.Parents.Contains(folderId) &&
                file.Properties.GetValueOrDefault("pcabRole") == ObjectRole && file.Size is >= 32 and <= OptimizedChunkLimit;
            if (!sizes.TryAdd(id, eligible ? file.Size : 0)) sizes[id] = 0;
        }
        return sizes;
    }

    internal async Task RecheckTrustedChunksAsync(IReadOnlyDictionary<string, long> reused, CancellationToken ct)
    {
        var current = await ReadTrustedChunkSizesAsync(reused.Keys.ToHashSet(StringComparer.Ordinal), ct);
        foreach (var (id, size) in reused)
        {
            ct.ThrowIfCancellationRequested();
            if (current.TryGetValue(id, out var observed) && observed == size) continue;
            // A stale page or a duplicate must go through the same exact-name lookup
            // and authentication as any other existing chunk.
            var file = await FindAsync("chunks/" + id + ".chunk", ct, fresh: true);
            if (file?.Size != size) throw new IOException("ข้อมูลสำรองเดิมหายไปหรือขนาดเปลี่ยนก่อนบันทึกชุดสำรองใหม่");
        }
    }

    private async Task<DriveFile?> FindAsync(string name, CancellationToken ct, bool fresh = false, bool allowEquivalentChunks = true)
    {
        ValidateName(name);
        ct.ThrowIfCancellationRequested();
        lock (lookupCache)
        {
            if (!fresh && lookupCache.TryGetValue(name, out var cached)) return cached;
            if (fresh) lookupCache.Remove(name);
        }
        var files = await DriveApi.ListFilesAsync(session, BaseQuery + $" and name='{DriveApi.Escape(EncodeName(name))}'", ct);
        var file = files.Count > 1
            ? allowEquivalentChunks ? await ResolveDuplicateChunkAsync(name, files, ct) : throw DuplicateConflict("mutation-refused", files.Count)
            : files.SingleOrDefault();
        if (file is not null)
        {
            RequireBinaryObject(file);
            lock (lookupCache)
            {
                if (!lookupCache.ContainsKey(name)) cacheOrder.Enqueue(name);
                lookupCache[name] = file;
                while (cacheOrder.Count > CacheLimit) lookupCache.Remove(cacheOrder.Dequeue());
            }
        }
        return file;
    }

    private static bool IsChunkName(string name) => name.Length == 77 && name.StartsWith("chunks/", StringComparison.Ordinal) &&
        name.EndsWith(".chunk", StringComparison.Ordinal) && name.AsSpan(7, 64).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static IOException DuplicateConflict(string reason, int count, Exception? inner = null)
    {
        GoogleDiagnostics.Write("duplicate-object", Guid.NewGuid().ToString("N"), 1, reason, inner,
            metadataChecks: new Dictionary<string, string> { ["copies"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        return new IOException("พบชื่อวัตถุซ้ำใน Google Drive ที่ยังใช้ต่อไม่ได้: " + reason + " (" + count + " สำเนา) เก็บทุกสำเนาไว้เพื่อป้องกันข้อมูลสูญหาย", inner);
    }

    private async Task<DriveFile> ResolveDuplicateChunkAsync(string name, List<DriveFile> files, CancellationToken ct)
    {
        var verify = chunkVerifier;
        if (!IsChunkName(name) || verify is null) throw DuplicateConflict("authentication-required", files.Count);
        foreach (var file in files.OrderBy(f => f.Id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (file.Name != EncodeName(name) || !file.Parents.Contains(folderId) || file.Properties.GetValueOrDefault("pcabRole") != ObjectRole)
                throw DuplicateConflict("identity-conflict", files.Count);
            if (!IsBinaryMime(file.MimeType) || file.Size is < 32 or > OptimizedChunkLimit)
                throw DuplicateConflict("unsupported-metadata", files.Count);
            using var response = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
                "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(file.Id) + "?alt=media"), ct);
            await GoogleSession.EnsureSuccessAsync(response, ct);
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            using var memory = new MemoryStream();
            var buffer = new byte[65536];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) != 0)
            {
                if (memory.Length + read > OptimizedChunkLimit) throw DuplicateConflict("size-limit", files.Count);
                memory.Write(buffer, 0, read);
            }
            try
            {
                if (memory.Length != file.Size || !verify(name[7..^6], memory.ToArray()))
                    throw DuplicateConflict("chunk-identity-mismatch", files.Count);
            }
            catch (Exception e) when (e is CryptographicException or InvalidDataException)
            { throw DuplicateConflict("chunk-authentication-failed", files.Count, e); }
        }
        // Each payload authenticates to the same keyed content address. Random encryption
        // nonces may differ. Select deterministically; retain every physical copy unchanged.
        GoogleDiagnostics.Write("duplicate-object", Guid.NewGuid().ToString("N"), 1, "authenticated-equivalent-chunks",
            metadataChecks: new Dictionary<string, string> { ["copies"] = files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        return files.MinBy(f => f.Id, StringComparer.Ordinal)!;
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

    public ValueTask DisposeAsync()
    {
        idLock.Dispose();
        mutationLock.Dispose();
        mutationSlots.Dispose();
        foreach (var nameLock in chunkNameLocks) nameLock.Dispose();
        return ValueTask.CompletedTask;
    }

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
