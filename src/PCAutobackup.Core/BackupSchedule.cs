using System.Globalization;

namespace PCAutobackup.Core;

public static class BackupSchedule
{
    public const int MaximumDailyTimes = 24;

    public static bool ShouldRun(AppConfiguration config, DateTimeOffset now, TimeZoneInfo zone, RunStatus? lastSuccess, RunStatus? lastAttempt)
    {
        if (!config.ScheduleEnabled) return false;
        var pending = lastAttempt?.Status is "Running" or "Interrupted" or "Failed"
            && (lastSuccess is null || lastAttempt.StartedUtc > lastSuccess.StartedUtc);
        if (pending) return true;
        var due = MostRecentDue(config, now, zone);
        // A job started before a round does not cover that round merely by ending later.
        // A start time after now (clock moved back) proves nothing about the current round.
        bool Covers(RunStatus? run) => run is not null && run.StartedUtc >= due && run.StartedUtc <= now;
        // An explicit cancellation declines its own round only; the next round is still due.
        return !Covers(lastSuccess) && !(lastAttempt?.Status == "Cancelled" && Covers(lastAttempt));
    }

    public static IReadOnlyList<string> GetTimes(AppConfiguration config) =>
        ValidateTimes(config.DailyTimes ?? [config.DailyTime]);

    public static List<string> ParseTimes(string text) => ValidateTimes(text.Split(',', StringSplitOptions.TrimEntries)).ToList();

    private static IReadOnlyList<string> ValidateTimes(IEnumerable<string> values)
    {
        var times = values.ToList();
        if (times.Count is < 1 or > MaximumDailyTimes)
            throw new ArgumentException($"เลือกเวลาสำรอง 1–{MaximumDailyTimes} รอบต่อวัน");
        foreach (var time in times)
            if (time is null || time.Length != 5 || !TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new ArgumentException("ระบุเวลาแบบ 24 ชั่วโมง คั่นด้วย , เช่น 09:00, 13:00, 21:00");
        if (times.Distinct(StringComparer.Ordinal).Count() != times.Count)
            throw new ArgumentException("เวลาสำรองต้องไม่ซ้ำกัน");
        return times.Order(StringComparer.Ordinal).ToArray();
    }

    public static DateTimeOffset MostRecentDue(AppConfiguration config, DateTimeOffset now, TimeZoneInfo zone)
    {
        var times = GetTimes(config);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        DateTimeOffset? latest = null;
        // Coalesce missed rounds into one current backup rather than replaying old rounds.
        foreach (var date in new[] { today.AddDays(-1), today })
        foreach (var time in times)
        {
            var local = DateTime.SpecifyKind(date.Add(TimeOnly.ParseExact(time, "HH:mm", CultureInfo.InvariantCulture).ToTimeSpan()), DateTimeKind.Unspecified);
            // A skipped spring-forward time becomes due at the first valid local minute.
            while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
            // Run an ambiguous fall-back time once, at its first occurrence.
            var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
            var candidate = new DateTimeOffset(local, offset);
            if (candidate <= now && (latest is null || candidate > latest)) latest = candidate;
        }
        return latest ?? throw new InvalidOperationException("ไม่พบรอบเวลาที่ผ่านมา");
    }
}
