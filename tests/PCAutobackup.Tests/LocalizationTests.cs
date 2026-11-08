using System.Globalization;
using System.Text;
using System.Text.Json;
using PCAutobackup.Core;

namespace PCAutobackup.Tests;

public sealed class LocalizationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PCAutobackup-localization-tests-" + Guid.NewGuid().ToString("N"));

    public LocalizationTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task ExistingConfigurationWithoutLanguageDefaultsToThai()
    {
        var path = Path.Combine(root, "legacy.json");
        var config = new AppConfiguration { DailyTime = "19:35", ScheduleEnabled = true, RetentionDays = 13 };
        var json = JsonSerializer.SerializeToNode(config, ConfigurationStore.JsonOptions)!.AsObject();
        json.Remove(nameof(AppConfiguration.Language));
        await File.WriteAllTextAsync(path, json.ToJsonString());

        var loaded = await ConfigurationStore.LoadAsync(path);

        Assert.Equal("th", loaded.Language);
        Assert.Equal("19:35", loaded.DailyTime);
        Assert.True(loaded.ScheduleEnabled);
        Assert.Equal(13, loaded.RetentionDays);
    }

    [Fact]
    public async Task EnglishChoicePersistsWithoutChangingScheduleSourcesOrDestination()
    {
        var path = Path.Combine(root, "configuration.json");
        var service = new ApplicationService(path);
        var config = await service.LoadConfigurationAsync();
        config.Destination = DestinationKind.LocalFolder;
        config.LocalDestinationPath = Path.Combine(root, "คลัง backup");
        config.Sources = [new BackupSource { Path = Path.Combine(root, "ข้อมูล source"), Label = "ข้อมูล" }];
        config.DailyTime = "09:05";
        config.DailyTimes = ["09:05", "21:55"];
        config.ScheduleEnabled = true;
        config.RetentionDays = 19;
        config.QuotaBytes = 42_000_000_000;
        config.ProtectedGoogleTokens = "synthetic-protected-token";
        config.ProtectedRecoveryKey = "synthetic-protected-key";
        await service.SaveConfigurationAsync();
        var before = JsonSerializer.SerializeToNode(config, ConfigurationStore.JsonOptions)!.AsObject();

        config.Language = "en";
        await service.SaveConfigurationAsync();
        var loaded = await new ApplicationService(path).LoadConfigurationAsync();

        Assert.Equal("en", loaded.Language);
        Assert.Equal(new[] { "09:05", "21:55" }, BackupSchedule.GetTimes(loaded));
        Assert.True(loaded.ScheduleEnabled);
        var after = JsonSerializer.SerializeToNode(loaded, ConfigurationStore.JsonOptions)!.AsObject();
        before.Remove(nameof(AppConfiguration.Language));
        after.Remove(nameof(AppConfiguration.Language));
        before.Remove(nameof(AppConfiguration.Revision));
        after.Remove(nameof(AppConfiguration.Revision));
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(before, after));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("de")]
    public async Task UnsupportedOrMissingLanguageFallsBackToThai(string? language)
    {
        var path = Path.Combine(root, "unknown-language.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new AppConfiguration { Language = language! }));
        Assert.Equal("th", (await ConfigurationStore.LoadAsync(path)).Language);
        Assert.Equal("th", AppLocalization.NormalizeLanguage(language));
    }

    [Fact]
    public void DiagnosticsTranslateBothDirectionsWithoutChangingNonAsciiPaths()
    {
        var path = Path.Combine(root, "สำเร็จ ไม่พบต้นทาง 工作", "report.txt");
        Assert.Equal("ไม่พบต้นทาง: " + path, AppLocalization.TranslateMessage("Source unavailable: " + path, "th"));
        Assert.Equal("Source unavailable: " + path, AppLocalization.TranslateMessage("ไม่พบต้นทาง: " + path, "en"));
        Assert.Equal("Snapshot committed; cleanup failed: Source unavailable: " + path,
            AppLocalization.TranslateMessage("ยืนยันชุดสำรองแล้ว แต่ล้างประวัติไม่สำเร็จ: ไม่พบต้นทาง: " + path, "en"));
        Assert.Equal(path, AppLocalization.TranslateMessage(path, "en"));
    }

    [Fact]
    public void DynamicDiagnosticsAndDatesUseSelectedLanguage()
    {
        Assert.Equal("Choose 1–24 backup times per day", AppLocalization.TranslateMessage("เลือกเวลาสำรอง 1–24 รอบต่อวัน", "en"));
        Assert.Equal("Google API request failed (HTTP 403)", AppLocalization.TranslateMessage("Google API ทำรายการไม่สำเร็จ (HTTP 403)", "en"));
        Assert.Equal("Google API ทำรายการไม่สำเร็จ (HTTP 403)", AppLocalization.TranslateMessage("Google API request failed (HTTP 403)", "th"));
        var date = new DateTime(2026, 11, 7);
        Assert.Equal("2026", date.ToString("yyyy", AppLocalization.CultureFor("en")));
        Assert.Equal("2569", date.ToString("yyyy", AppLocalization.CultureFor("th")));
    }

    [Fact]
    public void CatalogTranslationsPreserveCompositeFormatArguments()
    {
        object?[] arguments = Enumerable.Range(0, 10).Select(value => (object)value).ToArray();
        Assert.NotEmpty(AppLocalization.English);
        foreach (var (thai, english) in AppLocalization.English)
        {
            Assert.False(string.IsNullOrWhiteSpace(thai));
            Assert.False(string.IsNullOrWhiteSpace(english));
            var thaiFormat = CompositeFormat.Parse(thai);
            var englishFormat = CompositeFormat.Parse(english);
            Assert.Equal(thaiFormat.MinimumArgumentCount, englishFormat.MinimumArgumentCount);
            _ = string.Format(CultureInfo.InvariantCulture, english, arguments);
        }
    }

    [Fact]
    public void TransferMetricsTranslateAndUseProcessedDecimalMegabytes()
    {
        var elapsed = TimeSpan.FromSeconds(2);
        Assert.Equal("25% · Processed 1.00 MB/s · ETA 00:00:06",
            AppLocalization.FormatTransferMetrics(2_000_000, 8_000_000, elapsed, "en"));
        Assert.Equal("25% · ประมวลผล 1.00 MB/s · เหลือประมาณ 00:00:06",
            AppLocalization.FormatTransferMetrics(2_000_000, 8_000_000, elapsed, null));
    }

    [Fact]
    public void UnknownTotalsAndZeroElapsedNeverInventPercentSpeedOrEta()
    {
        Assert.Null(AppLocalization.ProgressPercentage(20, null));
        Assert.Equal("--% · Processed -- MB/s · ETA --",
            AppLocalization.FormatTransferMetrics(0, null, TimeSpan.Zero, "en"));
        Assert.Equal("25% · Processed -- MB/s · ETA --",
            AppLocalization.FormatTransferMetrics(2_000_000, 8_000_000, TimeSpan.Zero, "en"));
    }

    [Fact]
    public void TransferPercentStaysBelowCompleteWhenTotalChangesAndHandlesEmptyJobs()
    {
        Assert.Equal(99d, AppLocalization.ProgressPercentage(8_000_000, 8_000_000));
        Assert.Equal(99d, AppLocalization.ProgressPercentage(8_000_000, 4_000_000));
        Assert.Equal(0d, AppLocalization.ProgressPercentage(0, 0));
        Assert.Equal(100d, AppLocalization.ProgressPercentage(0, 0, completed: true));
        Assert.Equal("99% · Processed 1.00 MB/s · ETA --",
            AppLocalization.FormatTransferMetrics(8_000_000, 8_000_000, TimeSpan.FromSeconds(8), "en"));
        Assert.Equal("100% · Processed 1.00 MB/s · ETA 00:00:00",
            AppLocalization.FormatTransferMetrics(8_000_000, 8_000_000, TimeSpan.FromSeconds(8), "en", completed: true));
    }

    public void Dispose() => Directory.Delete(root, true);
}
