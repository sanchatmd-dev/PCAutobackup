using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCAutobackup.Core;

public sealed class BackupEngine
{
    private const int ChunkSize = 4 * 1024 * 1024;
    private readonly IBackupStore store;
    private readonly byte[] encryptionKey, namingKey;
    private readonly TimeProvider timeProvider;
    private readonly int maxConcurrentChunks;
    private readonly int maxConcurrentRestoreChunks;
    private static readonly Regex ChunkId = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex SnapshotId = new("^[0-9]{17}-[a-f0-9]{32}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = false };

    public BackupEngine(IBackupStore store, byte[] recoveryKey, TimeProvider? timeProvider = null, int maxConcurrentChunks = 1, int maxConcurrentRestoreChunks = 1)
    {
        if (maxConcurrentChunks is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maxConcurrentChunks));
        if (maxConcurrentRestoreChunks is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maxConcurrentRestoreChunks));
        this.maxConcurrentChunks = maxConcurrentChunks;
        this.maxConcurrentRestoreChunks = maxConcurrentRestoreChunks;
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        if (recoveryKey is null || recoveryKey.Length != 32) throw new ArgumentException("Recovery key must be 32 bytes.", nameof(recoveryKey));
        encryptionKey = HMACSHA256.HashData(recoveryKey, Encoding.UTF8.GetBytes("PCAutobackup encryption v1"));
        namingKey = HMACSHA256.HashData(recoveryKey, Encoding.UTF8.GetBytes("PCAutobackup chunk names v1"));
    }
    private sealed class Manifest
    {
        public int Version { get; set; } = 1;
        public string Id { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; }
        public string DeviceName { get; set; } = "";
        public List<Entry> Entries { get; set; } = [];
    }
    private sealed record ManifestSummary(string Id, DateTimeOffset CreatedUtc, string DeviceName, long FileCount, long TotalBytes, HashSet<string> Chunks);
    private static ManifestSummary Summarize(Manifest m) => new(m.Id, m.CreatedUtc, m.DeviceName, m.Entries.LongCount(e => !e.Directory), m.Entries.Where(e => !e.Directory).Sum(e => e.Length), m.Entries.SelectMany(e => e.Chunks).ToHashSet(StringComparer.Ordinal));
    private sealed class Entry
    {
        public string Path { get; set; } = "";
        public bool Directory { get; set; }
        public long Length { get; set; }
        public DateTimeOffset ModifiedUtc { get; set; }
        public List<string> Chunks { get; set; } = [];
    }
    private static string ChunkName(string id) => "chunks/" + id + ".chunk";
    private static string ManifestName(string id) => "snapshots/" + id + ".manifest";
    private string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(HMACSHA256.HashData(namingKey, bytes));
    private byte[] Encrypt(ReadOnlySpan<byte> plain, string purpose)
    {
        byte[] output = new byte[4 + 12 + 16 + plain.Length];
        "PAB1"u8.CopyTo(output); RandomNumberGenerator.Fill(output.AsSpan(4, 12));
        using var aes = new AesGcm(encryptionKey, 16);
        aes.Encrypt(output.AsSpan(4, 12), plain, output.AsSpan(32), output.AsSpan(16, 16), Encoding.UTF8.GetBytes(purpose));
        return output;
    }
    private byte[] Decrypt(byte[] data, string purpose)
    {
        if (data.Length < 32 || !data.AsSpan(0, 4).SequenceEqual("PAB1"u8)) throw new InvalidDataException("Invalid encrypted object.");
        byte[] plain = new byte[data.Length - 32];
        using var aes = new AesGcm(encryptionKey, 16);
        aes.Decrypt(data.AsSpan(4, 12), data.AsSpan(32), data.AsSpan(16, 16), plain, Encoding.UTF8.GetBytes(purpose));
        return plain;
    }
    private async Task<byte[]> ReadObject(string name, int maxLength, CancellationToken ct)
    {
        await using var input = await store.OpenReadAsync(name, ct);
        using var memory = new MemoryStream();
        byte[] buffer = new byte[65536]; int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (memory.Length + read > maxLength) throw new InvalidDataException("Encrypted object exceeds size limit.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }
    private async Task Put(string name, byte[] bytes, CancellationToken ct)
    { using var data = new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true); await store.PutAsync(name, data, false, ct); }
    private async Task<byte[]> ReadChunk(string id, CancellationToken ct)
    {
        if (!ChunkId.IsMatch(id)) throw new InvalidDataException("Invalid chunk ID.");
        byte[] data = Decrypt(await ReadObject(ChunkName(id), ChunkSize + 32, ct), "chunk:" + id);
        if (Hash(data) != id) throw new InvalidDataException("Chunk hash mismatch.");
        return data;
    }
    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.StartsWith('/') || path.EndsWith('/')) throw new InvalidDataException("Invalid restore path.");
        foreach (string part in path.Split('/'))
        {
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Contains(':'))
                throw new InvalidDataException("Invalid restore path.");
            var stem = part.Split('.')[0];
            if (Regex.IsMatch(stem, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("Reserved restore path.");
        }
    }
    private async Task<Manifest> ReadManifest(string id, CancellationToken ct)
    {
        if (!SnapshotId.IsMatch(id)) throw new ArgumentException("Invalid snapshot ID.", nameof(id));
        byte[] plain = Decrypt(await ReadObject(ManifestName(id), 256 * 1024 * 1024, ct), "manifest:" + id);
        var manifest = JsonSerializer.Deserialize<Manifest>(plain, JsonOptions) ?? throw new InvalidDataException("Invalid manifest.");
        if (manifest.Version != 1 || manifest.Id != id || manifest.Entries is null) throw new InvalidDataException("Invalid manifest.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Entries)
        {
            if (entry is null || entry.Chunks is null) throw new InvalidDataException("Invalid manifest entry.");
            ValidateRelativePath(entry.Path);
            if (!paths.Add(entry.Path) || entry.Length < 0 || (entry.Directory && (entry.Length != 0 || entry.Chunks.Count != 0)) || entry.Chunks.Any(x => x is null || !ChunkId.IsMatch(x))) throw new InvalidDataException("Invalid manifest entry.");
        }
        return manifest;
    }
    private async Task<List<ManifestSummary>> ReadAllManifests(CancellationToken ct)
    {
        var result = new List<ManifestSummary>();
        await foreach (var obj in store.ListAsync("snapshots/", ct))
        {
            string file = obj.Name.StartsWith("snapshots/", StringComparison.Ordinal) ? obj.Name[10..] : "";
            if (!file.EndsWith(".manifest", StringComparison.Ordinal)) continue;
            string id = file[..^9];
            if (SnapshotId.IsMatch(id)) result.Add(Summarize(await ReadManifest(id, ct)));
        }
        return result.OrderBy(x => x.CreatedUtc).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
    }
    private static void CheckReparse(string path)
    {
        for (string? p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse points are not allowed: " + p);
    }
    private async Task EnsureOwner(string deviceId, bool hasSnapshots, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) throw new InvalidOperationException("Device ID is required.");
        const string name = "repository/owner";
        if (!await store.ExistsAsync(name, ct))
        {
            if (hasSnapshots) throw new InvalidDataException("Repository ownership metadata is missing.");
            await Put(name, Encrypt(Encoding.UTF8.GetBytes(deviceId), "repository-owner"), ct);
        }
        string owner = Encoding.UTF8.GetString(Decrypt(await ReadObject(name, 4096, ct), "repository-owner"));
        if (!string.Equals(owner, deviceId, StringComparison.Ordinal)) throw new InvalidOperationException("This repository belongs to a different device; select a separate destination for backups.");
    }
    private static bool PathsOverlap(string first, string second)
    {
        first = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        second = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase) ||
            first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static string SourceFolderName(BackupSource source, string fullPath)
    {
        string label = string.IsNullOrWhiteSpace(source.Label) ? Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : source.Label;
        if (string.IsNullOrWhiteSpace(label)) label = "Source";
        var invalid = Path.GetInvalidFileNameChars();
        label = new string(label.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (label.Length > 80) label = label[..80].TrimEnd(' ', '.');
        if (string.IsNullOrEmpty(label)) label = "Source";
        string suffix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.Id)))[..12];
        string result = label + "-" + suffix;
        ValidateRelativePath(result);
        return result;
    }
    public async Task<BackupResult> BackupAsync(AppConfiguration config, IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.RetentionDays < 0 || config.QuotaBytes < 0) throw new ArgumentOutOfRangeException(nameof(config));
        if (config.Sources.Count == 0) throw new InvalidOperationException("At least one source is required.");
        ct.ThrowIfCancellationRequested();
        // Fail before creating ownership metadata if any selected source is unavailable.
        var preflightIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in config.Sources)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item.Id) || !preflightIds.Add(item.Id)) throw new InvalidOperationException("Source IDs must be nonempty and unique.");
            string path = Path.GetFullPath(item.Path);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Source unavailable: " + path);
            CheckReparse(path);
            if (store is LocalBackupStore local && PathsOverlap(path, local.RootDirectory)) throw new InvalidOperationException("Source and repository paths must not overlap.");
        }
        // This estimate does not cache the file list; the actual backup still validates every source.
        long? estimatedTotal = null;
        if (progress is not null)
        {
            progress.Report(new BackupProgress("scanning"));
            long total = 0;
            foreach (var item in config.Sources)
            {
                var directories = new Stack<string>();
                directories.Push(Path.GetFullPath(item.Path));
                while (directories.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    string directory = directories.Pop();
                    CheckReparse(directory);
                    foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        ct.ThrowIfCancellationRequested();
                        CheckReparse(path);
                        if (Directory.Exists(path)) directories.Push(path);
                        else total = checked(total + new FileInfo(path).Length);
                    }
                }
            }
            estimatedTotal = total;
            progress.Report(new BackupProgress("backup", TotalBytes: estimatedTotal));
        }
        // Authenticate all existing manifests before writing or pruning; wrong keys must never remove data.
        var previous = await ReadAllManifests(ct);
        await EnsureOwner(config.DeviceId, previous.Count != 0, ct);
        var trustedChunks = previous.SelectMany(m => m.Chunks).ToHashSet(StringComparer.Ordinal);
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        // Durable content-addressed chunks are the resume checkpoint. Re-read source bytes
        // on every attempt, and authenticate any chunk not referenced by a prior snapshot.
        var now = timeProvider.GetUtcNow();
        var manifest = new Manifest { Id = now.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"), CreatedUtc = now, DeviceName = config.DeviceName };
        long count = 0, bytes = 0, uploaded = 0;
        using var chunkCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pendingChunks = new Dictionary<string, (Task Task, byte[] Buffer, long LogicalBytes)>(StringComparer.Ordinal);
        long progressBytes = 0;
        string? progressPath = null;
        var availableBuffers = new Queue<byte[]>();
        ExceptionDispatchInfo? chunkFailure = null;
        var completedChunks = new HashSet<string>(StringComparer.Ordinal);
        async Task ProcessChunk(string id, byte[] buffer, int filled)
        {
            var token = chunkCancellation.Token;
            try
            {
                string name = ChunkName(id);
                if (await store.ExistsAsync(name, token))
                {
                    if (!trustedChunks.Contains(id))
                    {
                        try
                        {
                            byte[] existing = await ReadChunk(id, token);
                            if (!existing.AsSpan().SequenceEqual(buffer.AsSpan(0, filled))) throw new InvalidDataException("Existing chunk does not match its name.");
                        }
                        catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
                        {
                            // This orphan is not referenced by a committed snapshot. Replace a
                            // corrupted interrupted upload, preserving all committed chunks.
                            await store.DeleteAsync(name, token);
                            byte[] encrypted = Encrypt(buffer.AsSpan(0, filled), "chunk:" + id);
                            await Put(name, encrypted, token); Interlocked.Add(ref uploaded, encrypted.Length);
                        }
                    }
                }
                else
                {
                    byte[] encrypted = Encrypt(buffer.AsSpan(0, filled), "chunk:" + id);
                    await Put(name, encrypted, token); Interlocked.Add(ref uploaded, encrypted.Length);
                }
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                    Interlocked.CompareExchange(ref chunkFailure, ExceptionDispatchInfo.Capture(ex), null);
                chunkCancellation.Cancel();
                throw;
            }
        }
        async Task DrainOne()
        {
            Task finished = await Task.WhenAny(pendingChunks.Values.Select(value => value.Task));
            var pair = pendingChunks.First(item => ReferenceEquals(item.Value.Task, finished));
            await pair.Value.Task;
            pendingChunks.Remove(pair.Key);
            availableBuffers.Enqueue(pair.Value.Buffer);
            completedChunks.Add(pair.Key);
            progressBytes = checked(progressBytes + pair.Value.LogicalBytes);
            progress?.Report(new BackupProgress("backup", progressPath, count, progressBytes, estimatedTotal));
        }
        try
        {
            for (int sourceIndex = 0; sourceIndex < config.Sources.Count; sourceIndex++)
            {
                ct.ThrowIfCancellationRequested();
                string source = Path.GetFullPath(config.Sources[sourceIndex].Path);
                if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Source unavailable: " + source);
                if (store is LocalBackupStore local && PathsOverlap(source, local.RootDirectory)) throw new InvalidOperationException("Source and repository paths must not overlap.");
                CheckReparse(source);
                if (string.IsNullOrWhiteSpace(config.Sources[sourceIndex].Id) || !sourceIds.Add(config.Sources[sourceIndex].Id)) throw new InvalidOperationException("Source IDs must be nonempty and unique.");
                string prefix = SourceFolderName(config.Sources[sourceIndex], source);
                var pending = new Stack<(string Directory, string Relative)>(); pending.Push((source, prefix));
                while (pending.Count > 0)
                {
                    ct.ThrowIfCancellationRequested(); var current = pending.Pop(); CheckReparse(current.Directory);
                    manifest.Entries.Add(new Entry { Path = current.Relative, Directory = true });
                    foreach (string path in Directory.EnumerateFileSystemEntries(current.Directory))
                    {
                        ct.ThrowIfCancellationRequested(); CheckReparse(path);
                        string relative = current.Relative + "/" + Path.GetFileName(path); ValidateRelativePath(relative);
                        if (Directory.Exists(path)) { pending.Push((path, relative)); continue; }
                        progressPath = path;
                        var info = new FileInfo(path); long initialLength = info.Length; DateTime modified = info.LastWriteTimeUtc;
                        var entry = new Entry { Path = relative, Length = initialLength, ModifiedUtc = modified };
                        long fileBytes = 0;
                        await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
                        {
                            while (true)
                            {
                                // Reap completed uploads even when repeated chunks share one pending task.
                                while (pendingChunks.Values.Any(value => value.Task.IsCompleted)) await DrainOne();
                                while (pendingChunks.Count >= maxConcurrentChunks) await DrainOne();
                                byte[] buffer = availableBuffers.Count > 0 ? availableBuffers.Dequeue() : new byte[ChunkSize];
                                int filled = 0, n;
                                while (filled < buffer.Length && (n = await input.ReadAsync(buffer.AsMemory(filled), chunkCancellation.Token)) > 0) filled += n;
                                if (filled == 0) { availableBuffers.Enqueue(buffer); break; }
                                string id = Hash(buffer.AsSpan(0, filled));
                                if (pendingChunks.TryGetValue(id, out var pendingChunk))
                                {
                                    pendingChunks[id] = (pendingChunk.Task, pendingChunk.Buffer, checked(pendingChunk.LogicalBytes + filled));
                                    availableBuffers.Enqueue(buffer);
                                }
                                else if (completedChunks.Contains(id))
                                {
                                    availableBuffers.Enqueue(buffer);
                                    progressBytes = checked(progressBytes + filled);
                                    progress?.Report(new BackupProgress("backup", progressPath, count, progressBytes, estimatedTotal));
                                }
                                else pendingChunks.Add(id, (ProcessChunk(id, buffer, filled), buffer, filled));
                                entry.Chunks.Add(id); fileBytes += filled;
                            }
                        }
                        while (pendingChunks.Count > 0) await DrainOne();
                        info.Refresh();
                        if (!info.Exists || fileBytes != initialLength || info.Length != initialLength || info.LastWriteTimeUtc != modified) throw new IOException("Source file changed during backup: " + path);
                        manifest.Entries.Add(entry); count++; bytes += fileBytes;
                        progress?.Report(new BackupProgress("backup", path, count, progressBytes, estimatedTotal));
                    }
                }
            }
        }
        catch
        {
            chunkCancellation.Cancel();
            try { await Task.WhenAll(pendingChunks.Values.Select(value => value.Task)); } catch { }
            if (!ct.IsCancellationRequested) chunkFailure?.Throw();
            throw;
        }
        ct.ThrowIfCancellationRequested();
        progress?.Report(new BackupProgress("finalizing", null, count, progressBytes, estimatedTotal));
        byte[] manifestPlain = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (manifestPlain.Length > 256 * 1024 * 1024 - 32) throw new InvalidOperationException("Snapshot manifest exceeds the supported size.");
        byte[] manifestBytes = Encrypt(manifestPlain, "manifest:" + manifest.Id);
        await CheckNewSnapshotQuota(manifest, manifestBytes.Length, config.QuotaBytes, ct);
        ct.ThrowIfCancellationRequested();
        await Put(ManifestName(manifest.Id), manifestBytes, ct); uploaded += manifestBytes.Length;
        var warnings = new List<string>();
        try { previous.Add(Summarize(manifest)); await Prune(previous, manifest.Id, config.RetentionDays, config.QuotaBytes, warnings, ct); }
        catch (OperationCanceledException) { warnings.Add("Snapshot committed; cleanup was cancelled."); }
        catch (Exception ex) { warnings.Add("Snapshot committed; cleanup failed: " + ex.Message); }
        return new BackupResult(manifest.Id, count, bytes, uploaded, warnings);
    }
    private async Task CheckNewSnapshotQuota(Manifest manifest, long manifestBytes, long? quota, CancellationToken ct)
    {
        if (!quota.HasValue) return;
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        await foreach (var obj in store.ListAsync("chunks/", ct))
            if (obj.Name.StartsWith("chunks/", StringComparison.Ordinal) && obj.Name.EndsWith(".chunk", StringComparison.Ordinal))
            { string id = obj.Name[7..^6]; if (ChunkId.IsMatch(id)) sizes[id] = obj.Size; }
        long total = manifestBytes;
        foreach (string id in manifest.Entries.SelectMany(e => e.Chunks).Distinct(StringComparer.Ordinal))
        {
            if (!sizes.TryGetValue(id, out long size)) throw new InvalidDataException("A staged chunk is missing.");
            total = checked(total + size);
        }
        if (total > quota.Value) throw new InvalidOperationException("The new snapshot exceeds the configured quota; the previous snapshot was retained.");
    }
    public async Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(CancellationToken ct = default) =>
        (await ReadAllManifests(ct)).OrderByDescending(x => x.CreatedUtc).Select(x => new SnapshotInfo(x.Id, x.CreatedUtc, x.DeviceName, x.FileCount, x.TotalBytes)).ToList();
    private async Task Prune(List<ManifestSummary> manifests, string committedId, int days, long? quota, List<string> warnings, CancellationToken ct)
    {
        manifests = manifests.OrderBy(x => x.CreatedUtc).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
        var newest = manifests.Single(m => m.Id == committedId);
        var keep = manifests.Where(m => m.Id != committedId).Append(newest).ToList();
        foreach (var m in manifests)
            if (m != newest && (days == 0 || m.CreatedUtc < newest.CreatedUtc.AddDays(-days))) keep.Remove(m);
        var objects = new List<StoredObject>();
        await foreach (var obj in store.ListAsync("chunks/", ct)) objects.Add(obj);
        var sizes = objects.Where(o => o.Name.StartsWith("chunks/", StringComparison.Ordinal) && o.Name.EndsWith(".chunk", StringComparison.Ordinal) && ChunkId.IsMatch(o.Name[7..^6])).ToDictionary(o => o.Name[7..^6], o => o.Size, StringComparer.Ordinal);
        var manifestSizes = new Dictionary<string, long>();
        await foreach (var obj in store.ListAsync("snapshots/", ct)) manifestSizes[obj.Name] = obj.Size;
        long SizeOf(List<ManifestSummary> list)
        {
            var ids = list.SelectMany(m => m.Chunks).Distinct(StringComparer.Ordinal);
            long total = list.Sum(m => manifestSizes.GetValueOrDefault(ManifestName(m.Id)));
            foreach (var id in ids) { if (!sizes.TryGetValue(id, out long size)) throw new InvalidDataException("Snapshot references a missing chunk; cleanup stopped."); total = checked(total + size); }
            return total;
        }
        // Validate references for every manifest before deleting anything.
        _ = SizeOf(manifests);
        while (quota.HasValue && keep.Count > 1 && SizeOf(keep) > quota.Value) keep.RemoveAt(0);
        if (quota.HasValue && SizeOf(keep) > quota.Value) warnings.Add("The latest snapshot exceeds the quota and was retained.");
        foreach (var m in manifests.Except(keep)) await store.DeleteAsync(ManifestName(m.Id), ct);
        var referenced = keep.SelectMany(m => m.Chunks).ToHashSet(StringComparer.Ordinal);
        foreach (var obj in objects)
            if (obj.Name.StartsWith("chunks/", StringComparison.Ordinal) && obj.Name.EndsWith(".chunk", StringComparison.Ordinal))
            { string id = obj.Name[7..^6]; if (ChunkId.IsMatch(id) && !referenced.Contains(id)) await store.DeleteAsync(obj.Name, ct); }
    }
    public async Task<RestoreResult> RestoreAsync(string snapshotId, string destinationDirectory, IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
    {
        var manifest = await ReadManifest(snapshotId, ct);
        long totalBytes = manifest.Entries.Where(entry => !entry.Directory).Sum(entry => entry.Length);
        progress?.Report(new BackupProgress("restore", TotalBytes: totalBytes));
        string root = Path.GetFullPath(destinationDirectory); CheckReparse(root);
        if (File.Exists(root)) throw new IOException("Restore destination is a file.");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("Restore destination must be empty.");
        Directory.CreateDirectory(root); CheckReparse(root);
        long files = 0, bytes = 0;
        foreach (var entry in manifest.Entries.OrderBy(x => x.Path.Count(c => c == '/')).ThenBy(x => x.Path, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Restore path escaped destination.");
            CheckReparse(path);
            if (entry.Directory) { Directory.CreateDirectory(path); CheckReparse(path); continue; }
            string parent = Path.GetDirectoryName(path)!; Directory.CreateDirectory(parent); CheckReparse(parent);
            long written = 0;
            bool created = false;
            try
            {
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                created = true;
                using var chunkCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var pending = new Queue<Task<byte[]>>();
                ExceptionDispatchInfo? chunkFailure = null;
                async Task<byte[]> FetchChunk(string id)
                {
                    try { return await ReadChunk(id, chunkCancellation.Token); }
                    catch (Exception ex)
                    {
                        if (ex is not OperationCanceledException)
                            Interlocked.CompareExchange(ref chunkFailure, ExceptionDispatchInfo.Capture(ex), null);
                        chunkCancellation.Cancel();
                        throw;
                    }
                }
                try
                {
                    int next = 0;
                    while (next < entry.Chunks.Count || pending.Count > 0)
                    {
                        while (next < entry.Chunks.Count && pending.Count < maxConcurrentRestoreChunks)
                            pending.Enqueue(FetchChunk(entry.Chunks[next++]));
                        byte[] chunk = await pending.Peek();
                        _ = pending.Dequeue();
                        written = checked(written + chunk.Length);
                        if (written > entry.Length) throw new InvalidDataException("File exceeds manifest length.");
                        await output.WriteAsync(chunk, chunkCancellation.Token);
                        progress?.Report(new BackupProgress("restore", path, files, checked(bytes + written), totalBytes));
                    }
                }
                catch
                {
                    chunkCancellation.Cancel();
                    try { await Task.WhenAll(pending); } catch { }
                    if (!ct.IsCancellationRequested) chunkFailure?.Throw();
                    throw;
                }
                if (written != entry.Length) throw new InvalidDataException("File length does not match manifest.");
                await output.FlushAsync(ct);
            }
            catch { if (created && File.Exists(path)) File.Delete(path); throw; }
            File.SetLastWriteTimeUtc(path, entry.ModifiedUtc.UtcDateTime);
            files++; bytes += written; progress?.Report(new BackupProgress("restore", path, files, bytes, totalBytes));
        }
        return new RestoreResult(files, bytes, []);
    }
}
