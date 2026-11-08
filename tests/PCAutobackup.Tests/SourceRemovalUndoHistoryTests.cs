using System.Text.Json;
using System.Text.Json.Nodes;
using PCAutobackup.Core;

namespace PCAutobackup.Tests;

public sealed class SourceRemovalUndoHistoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PCAutobackup-persistent-source-undo-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset RemovedUtc = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);
    private static Task Saved() => Task.CompletedTask;

    public SourceRemovalUndoHistoryTests() => Directory.CreateDirectory(root);
    private BackupSource Source(string id) => new() { Id = id, Label = "โฟลเดอร์-" + id, Path = Path.Combine(root, "sources", id) };
    private AppConfiguration Configuration(params string[] ids) => new() { Sources = ids.Select(Source).ToList() };

    [Fact]
    public async Task RemovalAndUndoPersistAcrossReopenAndPreserveOtherSettings()
    {
        var path = Path.Combine(root, "configuration.json");
        var service = new ApplicationService(path);
        var config = await service.LoadConfigurationAsync();
        config.Sources = [Source("a"), Source("b"), Source("c"), Source("d")];
        var original = config.Sources.Select(source => (source.Id, source.Path, source.Label)).ToArray();
        config.ScheduleEnabled = true;
        config.DailyTimes = ["09:15", "21:00"];
        config.RetentionDays = 13;
        config.ProtectedGoogleTokens = "synthetic-protected-token";
        config.ProtectedRecoveryKey = "synthetic-protected-key";
        var settingsBefore = UnrelatedSettings(config);
        await service.SaveConfigurationAsync();
        var history = new SourceRemovalUndoHistory(config);
        await history.RemoveAsync("b", service.SaveConfigurationAsync, RemovedUtc);
        await history.RemoveAsync("d", service.SaveConfigurationAsync, RemovedUtc.AddSeconds(1));

        var reopened = new ApplicationService(path);
        var reloaded = await reopened.LoadConfigurationAsync();
        var persisted = new SourceRemovalUndoHistory(reloaded);
        Assert.Equal(new[] { "a", "c" }, reloaded.Sources.Select(source => source.Id));
        Assert.Equal(new[] { "b", "d" }, reloaded.PendingSourceRemovals.Select(entry => entry.Source.Id));
        Assert.Equal(RemovedUtc.AddSeconds(1), persisted.Peek()!.RemovedUtc);
        Assert.Equal(settingsBefore, UnrelatedSettings(reloaded));
        reloaded.Language = "en";
        await reopened.SaveConfigurationAsync();
        Assert.Equal(2, (await ConfigurationStore.LoadAsync(path)).PendingSourceRemovals.Count);
        Assert.True(await persisted.UndoLatestAsync(reopened.SaveConfigurationAsync));
        reloaded = await reopened.LoadConfigurationAsync();
        Assert.Equal(new[] { "a", "c", "d" }, reloaded.Sources.Select(source => source.Id));
        Assert.Equal("b", Assert.Single(reloaded.PendingSourceRemovals).Source.Id);
        var result = await new SourceRemovalUndoHistory(reloaded).RestoreAllAsync(reopened.SaveConfigurationAsync);
        Assert.Equal(new SourceRemovalRestoreResult(1, 0), result);
        Assert.Equal(original, reloaded.Sources.Select(source => (source.Id, source.Path, source.Label)));
        Assert.Empty((await ConfigurationStore.LoadAsync(path)).PendingSourceRemovals);
    }

    [Fact]
    public async Task MoreThanTwentyRemovalsSurviveReloadAndRestoreInOriginalOrder()
    {
        var ids = Enumerable.Range(0, 37).Select(value => value.ToString()).ToArray();
        var config = Configuration(ids);
        var history = new SourceRemovalUndoHistory(config);
        foreach (var id in ids) await history.RemoveAsync(id, Saved, RemovedUtc);
        Assert.Empty(config.Sources);
        Assert.Equal(37, history.Count);
        var copy = history.Peek()!;
        copy.Source.Label = "changed copy";
        Assert.Equal("โฟลเดอร์-36", history.Peek()!.Source.Label);
        var path = Path.Combine(root, "many.json");
        await ConfigurationStore.SaveAsync(path, config);
        var reloaded = await ConfigurationStore.LoadAsync(path);
        var result = await new SourceRemovalUndoHistory(reloaded).RestoreAllAsync(() => ConfigurationStore.SaveAsync(path, reloaded));
        Assert.Equal(new SourceRemovalRestoreResult(37, 0), result);
        Assert.Equal(ids, reloaded.Sources.Select(source => source.Id));
        Assert.Empty((await ConfigurationStore.LoadAsync(path)).PendingSourceRemovals);
    }

    [Fact]
    public async Task RestoreAllRetainsConflictsAndNeverOverwritesExistingEntries()
    {
        var config = Configuration("a", "b", "c", "d");
        var history = new SourceRemovalUndoHistory(config);
        foreach (var id in new[] { "b", "d", "c" }) await history.RemoveAsync(id, Saved, RemovedUtc);
        var sameId = Source("other"); sameId.Id = "D";
        var samePath = Source("other-path"); samePath.Path = Path.Combine(Source("b").Path, "child", "..") + Path.DirectorySeparatorChar;
        config.Sources.AddRange([sameId, samePath]);
        var result = await history.RestoreAllAsync(Saved);
        Assert.Equal(new SourceRemovalRestoreResult(1, 2), result);
        Assert.Contains(sameId, config.Sources);
        Assert.Contains(samePath, config.Sources);
        Assert.Equal(new[] { "b", "d" }, config.PendingSourceRemovals.Select(entry => entry.Source.Id));
        Assert.False(await history.UndoLatestAsync(() => throw new Exception("Conflicting undo must not save")));
        config.Sources.Remove(sameId); config.Sources.Remove(samePath);
        Assert.Equal(new SourceRemovalRestoreResult(2, 0), await history.RestoreAllAsync(Saved));
        Assert.Equal(new[] { "a", "b", "c", "d" }, config.Sources.Select(source => source.Id));
    }

    [Fact]
    public async Task ConfirmLatestDiscardsOnlyLatestPendingRemovalWithoutRestoringSource()
    {
        var config = Configuration("a", "b", "c");
        var history = new SourceRemovalUndoHistory(config);
        await history.RemoveAsync("a", Saved, RemovedUtc);
        await history.RemoveAsync("b", Saved, RemovedUtc);
        Assert.True(await history.ConfirmLatestAsync(Saved));
        Assert.Equal("a", Assert.Single(config.PendingSourceRemovals).Source.Id);
        Assert.Equal("c", Assert.Single(config.Sources).Id);
        await history.RestoreAllAsync(Saved);
        Assert.Equal(new[] { "a", "c" }, config.Sources.Select(source => source.Id));
    }

    [Fact]
    public async Task ClearRequiresConfirmationAndPreservesActiveSources()
    {
        var config = Configuration("a", "b");
        var history = new SourceRemovalUndoHistory(config);
        await history.RemoveAsync("a", Saved, RemovedUtc);
        Assert.False(await history.ClearAsync(false, () => throw new Exception("Declined clear must not save")));
        Assert.Equal(1, history.Count);
        var active = config.Sources;
        Assert.True(await history.ClearAsync(true, Saved));
        Assert.Same(active, config.Sources);
        Assert.Equal("b", Assert.Single(config.Sources).Id);
        Assert.Empty(config.PendingSourceRemovals);
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("undo")]
    [InlineData("confirm")]
    [InlineData("clear")]
    [InlineData("restore-all")]
    public async Task FailedSaveRollsBackSourcesAndPendingHistoryTogether(string action)
    {
        var config = Configuration("a", "b", "c");
        var history = new SourceRemovalUndoHistory(config);
        await history.RemoveAsync("b", Saved, RemovedUtc);
        var before = JsonSerializer.Serialize(config);
        var sourceList = config.Sources;
        var pendingList = config.PendingSourceRemovals;
        static Task Fail() => throw new IOException("Synthetic save failure");
        Func<Task> operation = action switch
        {
            "remove" => async () => { await history.RemoveAsync("a", Fail, RemovedUtc); },
            "undo" => async () => { await history.UndoLatestAsync(Fail); },
            "confirm" => async () => { await history.ConfirmLatestAsync(Fail); },
            "clear" => async () => { await history.ClearAsync(true, Fail); },
            _ => async () => { await history.RestoreAllAsync(Fail); }
        };
        await Assert.ThrowsAsync<IOException>(operation);
        Assert.Equal(before, JsonSerializer.Serialize(config));
        Assert.Same(sourceList, config.Sources);
        Assert.Same(pendingList, config.PendingSourceRemovals);
    }

    [Fact]
    public async Task ExpiredBannerStaysExpiredAfterReloadWithoutDiscardingHistory()
    {
        var config = Configuration("a");
        var history = new SourceRemovalUndoHistory(config);
        await history.RemoveAsync("a", Saved, RemovedUtc);
        var path = Path.Combine(root, "expiry.json");
        await ConfigurationStore.SaveAsync(path, config);
        var reloaded = await ConfigurationStore.LoadAsync(path);
        history = new SourceRemovalUndoHistory(reloaded);
        Assert.Equal(TimeSpan.FromSeconds(1), history.BannerRemaining(RemovedUtc.AddSeconds(59)));
        Assert.Equal(TimeSpan.Zero, history.BannerRemaining(RemovedUtc.AddSeconds(60)));
        Assert.Equal(TimeSpan.Zero, history.BannerRemaining(RemovedUtc.AddDays(1)));
        Assert.Equal(TimeSpan.FromSeconds(60), history.BannerRemaining(RemovedUtc.AddDays(-1)));
        Assert.Equal(1, history.Count);
        Assert.Empty(reloaded.Sources);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyOrNullPendingHistoryLoadsEmpty(bool explicitNull)
    {
        var config = Configuration("a");
        var json = JsonSerializer.SerializeToNode(config)!.AsObject();
        if (explicitNull) json[nameof(AppConfiguration.PendingSourceRemovals)] = null;
        else json.Remove(nameof(AppConfiguration.PendingSourceRemovals));
        var path = Path.Combine(root, "legacy.json");
        await File.WriteAllTextAsync(path, json.ToJsonString());
        var reloaded = await ConfigurationStore.LoadAsync(path);
        Assert.Empty(reloaded.PendingSourceRemovals);
        Assert.Equal("a", Assert.Single(reloaded.Sources).Id);
    }

    [Fact]
    public async Task StaleRevisionCannotOverwriteNewSourcesOrPendingRemovals()
    {
        var path = Path.Combine(root, "revision.json");
        var first = new ApplicationService(path);
        var firstConfig = await first.LoadConfigurationAsync();
        firstConfig.Sources = [Source("a"), Source("b")];
        await first.SaveConfigurationAsync();
        var second = new ApplicationService(path);
        var stale = await second.LoadConfigurationAsync();
        await new SourceRemovalUndoHistory(firstConfig).RemoveAsync("a", first.SaveConfigurationAsync, RemovedUtc);
        await Assert.ThrowsAsync<IOException>(() => new SourceRemovalUndoHistory(stale).RemoveAsync("b", second.SaveConfigurationAsync, RemovedUtc));
        Assert.Equal(new[] { "a", "b" }, stale.Sources.Select(source => source.Id));
        Assert.Empty(stale.PendingSourceRemovals);
        var disk = await ConfigurationStore.LoadAsync(path);
        Assert.Equal("b", Assert.Single(disk.Sources).Id);
        Assert.Equal("a", Assert.Single(disk.PendingSourceRemovals).Source.Id);
    }

    private static string UnrelatedSettings(AppConfiguration config)
    {
        var json = JsonSerializer.SerializeToNode(config)!.AsObject();
        foreach (var field in new[] { nameof(config.Revision), nameof(config.Sources), nameof(config.PendingSourceRemovals) }) json.Remove(field);
        return json.ToJsonString();
    }

    public void Dispose() => Directory.Delete(root, true);
}
