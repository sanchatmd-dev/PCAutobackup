using System.Runtime.CompilerServices;

namespace PCAutobackup.Core;

public sealed class LocalBackupStore : IBackupStore
{
    private readonly string root;
    internal string RootDirectory => root;
    public LocalBackupStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("Destination is required.");
        root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(root);
        CheckNoReparse(root);
    }

    private static void CheckNoReparse(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Repository path contains a reparse point.");
    }

    private string Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.StartsWith('/') ||
            name.Split('/').Any(x => x is "" or "." or ".." || x.Contains(':')))
            throw new ArgumentException("Invalid repository object name.", nameof(name));
        string path = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid repository object name.", nameof(name));
        CheckNoReparse(path);
        return path;
    }
    public IDisposable AcquireLease()
    {
        string directory = Resolve("repository");
        Directory.CreateDirectory(directory);
        CheckNoReparse(directory);
        string path = Resolve("repository/lease.lock");
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new IOException("Repository is already in use by another backup or restore.", e); }
    }
    public Task<bool> ExistsAsync(string name, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.FromResult(File.Exists(Resolve(name))); }
    public Task<Stream> OpenReadAsync(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(new FileStream(Resolve(name), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }
    public async Task PutAsync(string name, Stream content, bool overwrite = false, CancellationToken ct = default)
    {
        string path = Resolve(name); string parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent); CheckNoReparse(parent);
        string temp = Path.Combine(parent, ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await content.CopyToAsync(output, ct); await output.FlushAsync(ct); output.Flush(true); }
            ct.ThrowIfCancellationRequested();
            try { File.Move(temp, path, overwrite); }
            catch (IOException) when (!overwrite && File.Exists(path)) { }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public Task DeleteAsync(string name, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); File.Delete(Resolve(name)); return Task.CompletedTask; }
    public async IAsyncEnumerable<StoredObject> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Prefixes are directory names in this engine. Enumerate only the immediate directory.
        string directory = Resolve(prefix.TrimEnd('/'));
        if (!Directory.Exists(directory)) yield break;
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            ct.ThrowIfCancellationRequested(); CheckNoReparse(path);
            var info = new FileInfo(path);
            yield return new StoredObject(prefix.TrimEnd('/') + "/" + info.Name, info.Length, info.LastWriteTimeUtc);
            await Task.Yield();
        }
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
