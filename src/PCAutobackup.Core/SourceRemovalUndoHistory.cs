namespace PCAutobackup.Core;

public sealed record SourceRemovalUndoEntry(BackupSource Source, int OriginalIndex, DateTimeOffset RemovedUtc);
public sealed record SourceRemovalRestoreResult(int RestoredCount, int ConflictCount);

/// <summary>Unconfirmed removals persist atomically with the active source list; entries are never evicted automatically.</summary>
public sealed class SourceRemovalUndoHistory(AppConfiguration configuration)
{
    public int Count => configuration.PendingSourceRemovals.Count;

    private static BackupSource Clone(BackupSource source) => new() { Id = source.Id, Path = source.Path, Label = source.Label };

    public SourceRemovalUndoEntry? Peek() => Count == 0 ? null
        : configuration.PendingSourceRemovals[^1] with { Source = Clone(configuration.PendingSourceRemovals[^1].Source) };

    public TimeSpan BannerRemaining(DateTimeOffset now)
    {
        var entry = Peek();
        if (entry is null) return TimeSpan.Zero;
        var remaining = TimeSpan.FromSeconds(60) - (now - entry.RemovedUtc);
        return remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : remaining;
    }

    public bool CanRestore(IReadOnlyList<BackupSource> sources) => Count > 0 && CanRestoreSource(configuration.PendingSourceRemovals[^1].Source, sources);

    private static bool CanRestoreSource(BackupSource source, IReadOnlyList<BackupSource> sources)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(source.Id) || !Path.IsPathFullyQualified(source.Path)) return false;
            var path = CanonicalPath(source.Path);
            return !sources.Any(item => string.Equals(item.Id, source.Id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(CanonicalPath(item.Path), path, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string CanonicalPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public async Task<bool> RemoveAsync(string id, Func<Task> save, DateTimeOffset now)
    {
        var index = configuration.Sources.FindIndex(source => string.Equals(source.Id, id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        var sources = configuration.Sources.ToList();
        var pending = configuration.PendingSourceRemovals.ToList();
        pending.Add(new(Clone(sources[index]), index, now.ToUniversalTime()));
        sources.RemoveAt(index);
        await CommitAsync(sources, pending, save);
        return true;
    }

    public async Task<bool> UndoLatestAsync(Func<Task> save)
    {
        if (!CanRestore(configuration.Sources)) return false;
        var entry = configuration.PendingSourceRemovals[^1];
        var sources = configuration.Sources.ToList();
        sources.Insert(Math.Min(entry.OriginalIndex, sources.Count), Clone(entry.Source));
        var pending = configuration.PendingSourceRemovals.ToList();
        pending.RemoveAt(pending.Count - 1);
        await CommitAsync(sources, pending, save);
        return true;
    }

    public async Task<bool> ConfirmLatestAsync(Func<Task> save)
    {
        if (Count == 0) return false;
        var pending = configuration.PendingSourceRemovals.ToList();
        pending.RemoveAt(pending.Count - 1);
        await CommitAsync(configuration.Sources, pending, save);
        return true;
    }

    public async Task<bool> ClearAsync(bool confirmed, Func<Task> save)
    {
        if (!confirmed || Count == 0) return false;
        await CommitAsync(configuration.Sources, [], save);
        return true;
    }

    public async Task<SourceRemovalRestoreResult> RestoreAllAsync(Func<Task> save)
    {
        var sources = configuration.Sources.ToList();
        var pending = configuration.PendingSourceRemovals.ToList();
        var restored = 0;
        for (var index = pending.Count - 1; index >= 0; index--)
        {
            var entry = pending[index];
            if (!CanRestoreSource(entry.Source, sources)) continue;
            sources.Insert(Math.Min(entry.OriginalIndex, sources.Count), Clone(entry.Source));
            pending.RemoveAt(index);
            restored++;
        }
        if (restored > 0) await CommitAsync(sources, pending, save);
        return new(restored, pending.Count);
    }

    private async Task CommitAsync(List<BackupSource> sources, List<SourceRemovalUndoEntry> pending, Func<Task> save)
    {
        var previousSources = configuration.Sources;
        var previousPending = configuration.PendingSourceRemovals;
        configuration.Sources = sources;
        configuration.PendingSourceRemovals = pending;
        try { await save(); }
        catch
        {
            configuration.Sources = previousSources;
            configuration.PendingSourceRemovals = previousPending;
            throw;
        }
    }
}
