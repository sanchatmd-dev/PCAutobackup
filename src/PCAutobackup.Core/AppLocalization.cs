using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCAutobackup.Core;

/// <summary>Shared Thai/English presentation text. Identifiers, file paths and stored backup data remain unchanged.</summary>
public static class AppLocalization
{
    public static IReadOnlyDictionary<string, string> English { get; } = LoadEnglish();

    public static string NormalizeLanguage(string? language) =>
        string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "th";

    public static CultureInfo CultureFor(string? language) =>
        CultureInfo.GetCultureInfo(NormalizeLanguage(language) == "en" ? "en-US" : "th-TH");

    public static string Text(string source, string? language) =>
        NormalizeLanguage(language) == "en" && English.TryGetValue(source, out var translated) ? translated : source;

    public static string Format(string source, string? language, params object?[] arguments) =>
        string.Format(CultureFor(language), Text(source, language), arguments);

    public static double? ProgressPercentage(long processedBytes, long? totalBytes, bool completed = false)
    {
        if (completed) return 100;
        if (totalBytes is null or < 0) return null;
        if (totalBytes == 0) return 0;
        return Math.Clamp(Math.Max(0, processedBytes) / (double)totalBytes.Value * 100, 0, 99);
    }

    /// <summary>Average logical bytes processed per second, including scan time; this is not network throughput.</summary>
    public static string FormatTransferMetrics(long processedBytes, long? totalBytes, TimeSpan elapsed, string? language, bool completed = false)
    {
        var culture = CultureFor(language);
        var percent = ProgressPercentage(processedBytes, totalBytes, completed);
        var speed = elapsed.TotalSeconds > 0 && processedBytes > 0 ? processedBytes / elapsed.TotalSeconds : 0;
        var percentageText = percent?.ToString("0", culture) + "%";
        if (percent is null) percentageText = "--%";
        var speedText = speed > 0 ? (speed / 1_000_000).ToString("0.00", culture) : "--";
        var eta = "--";
        if (completed) eta = "00:00:00";
        else if (totalBytes is >= 0 && totalBytes > processedBytes && speed > 0)
        {
            var seconds = Math.Min(Math.Ceiling((totalBytes.Value - (double)Math.Max(0, processedBytes)) / speed), TimeSpan.MaxValue.TotalSeconds - 1);
            var remaining = TimeSpan.FromSeconds(seconds);
            eta = remaining.ToString(remaining.TotalDays >= 1 ? @"d\.hh\:mm\:ss" : @"hh\:mm\:ss", culture);
        }
        var template = NormalizeLanguage(language) == "en"
            ? "{0} · Processed {1} MB/s · ETA {2}"
            : "{0} · ประมวลผล {1} MB/s · เหลือประมาณ {2}";
        return string.Format(culture, template, percentageText, speedText, eta);
    }

    /// <summary>Translates application diagnostics while preserving appended filenames, server details and unknown system messages.</summary>
    public static string TranslateMessage(string message, string? language)
    {
        var isEnglish = NormalizeLanguage(language) == "en";
        foreach (var (thai, english) in English)
        {
            var source = isEnglish ? thai : english;
            var target = isEnglish ? english : thai;
            if (string.Equals(message, source, StringComparison.Ordinal)) return target;
        }
        // A diagnostic prefix is explicitly marked by a trailing space. Never search or replace text inside a path.
        foreach (var (thai, english) in English.OrderByDescending(pair => (isEnglish ? pair.Key : pair.Value).Length))
        {
            var source = isEnglish ? thai : english;
            if (!source.EndsWith(' ') || !message.StartsWith(source, StringComparison.Ordinal)) continue;
            return (isEnglish ? english : thai) + TranslateMessage(message[source.Length..], language);
        }
        foreach (var (thai, english) in English)
        {
            var source = isEnglish ? thai : english;
            if (!source.Contains("{0}", StringComparison.Ordinal)) continue;
            // Only the explicit one-argument diagnostic templates need matching. Capture data verbatim.
            var pattern = "\\A" + Regex.Escape(source).Replace("\\{0}", "(?<value>.*?)", StringComparison.Ordinal) + "\\z";
            var match = Regex.Match(message, pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            if (match.Success) return (isEnglish ? english : thai).Replace("{0}", match.Groups["value"].Value, StringComparison.Ordinal);
        }
        if (message.Contains('\n')) return string.Join('\n', message.Split('\n').Select(line => TranslateMessage(line.TrimEnd('\r'), language)));
        return message;
    }

    private static IReadOnlyDictionary<string, string> LoadEnglish()
    {
        using var stream = typeof(AppLocalization).Assembly.GetManifestResourceStream("PCAutobackup.Core.Localization.English.json")
            ?? throw new InvalidOperationException("Missing embedded translation catalog.");
        var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException("Invalid embedded translation catalog.");
        translations["เลิกทำ"] = "Undo";
        translations["เลิกทำการนำโฟลเดอร์ออก"] = "Undo folder removal";
        translations["นำโฟลเดอร์ออกแล้ว: {0}"] = "Folder removed: {0}";
        translations["เลิกทำไม่ได้ เพราะมีโฟลเดอร์หรือรหัสเดียวกันอยู่แล้ว"] = "Cannot undo: a folder with the same path or ID already exists";
        translations["คืนโฟลเดอร์เข้าสู่รายการสำรองแล้ว"] = "Folder restored to the backup list";
        translations["กำลังคืนโฟลเดอร์…"] = "Restoring folder selection…";
        translations["ยืนยันการนำออก"] = "Confirm removal";
        translations["ยืนยันการนำโฟลเดอร์ออกแล้ว · ไฟล์ต้นฉบับยังอยู่ตามเดิม"] = "Folder removal confirmed · Original files remain unchanged";
        translations["คืนโฟลเดอร์"] = "Restore folders";
        translations["คืนโฟลเดอร์ที่นำออกแต่ยังไม่ยืนยันสู่รายการสำรอง"] = "Restore all unconfirmed removals to the backup list";
        translations["ล้างประวัติ"] = "Clear history";
        translations["มีโฟลเดอร์ที่นำออกและยังไม่ยืนยัน {0:N0} รายการ"] = "{0:N0} unconfirmed folder removals";
        translations["คืนโฟลเดอร์แล้ว {0:N0} รายการ · ยังมีรายการซ้ำที่ไม่ได้คืน {1:N0} รายการ"] = "Restored {0:N0} folders · {1:N0} conflicting entries remain pending";
        translations["ล้างประวัติการนำโฟลเดอร์ออกแล้ว"] = "Folder removal history cleared";
        translations["ล้างประวัติการนำโฟลเดอร์ออกทั้งหมดหรือไม่?\n\nรายการที่นำออกจะยังไม่ถูกสำรอง และจะเลิกทำจากประวัตินี้ไม่ได้ ไฟล์ต้นฉบับและรายการโฟลเดอร์ที่ยังใช้งานจะไม่ถูกลบ"] = "Clear all folder removal history?\n\nRemoved folders will remain excluded from backup and cannot be restored from this history. Original files and active source folders will not be deleted.";
        translations["รายการโฟลเดอร์ที่นำออกไม่ถูกต้อง"] = "Invalid removed folder history";
        return translations;
    }
}
