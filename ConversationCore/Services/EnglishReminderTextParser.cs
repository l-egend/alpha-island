using System.Globalization;
using System.Text.RegularExpressions;
using L = AlphaIsland.PluginSdk.Localization.UiText;

namespace alpha_island.Services;

public static partial class ReminderTextParser
{
    private const RegexOptions EnglishMatch = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static ReminderParseResult? ParseEnglish(string input, DateTimeOffset now, TimeZoneInfo zone)
    {
        var request = Regex.Match(input, @"^(?:please\s+)?remind\s+me\s+(?<rest>.+?)[.!]?$", EnglishMatch);
        if (!request.Success) return null;
        if (input.Length > 512) return new(true, null, L.T("提醒指令过长，请精简到512个字符以内。"));
        string rest = request.Groups["rest"].Value.Trim();
        Match parts;
        if (rest.StartsWith("to ", StringComparison.OrdinalIgnoreCase))
            parts = Regex.Match(rest[3..], @"^(?<title>.+?)\s+(?<when>(?:in|every|at|today|tomorrow|on)\s+.+)$", EnglishMatch);
        else parts = Regex.Match(rest, @"^(?<when>.+?)\s+to\s+(?<title>.+)$", EnglishMatch);
        if (!parts.Success) return EnglishTimeHelp();
        string title = parts.Groups["title"].Value.Trim(), when = parts.Groups["when"].Value.Trim();
        if (title.Length is 0 or > 160 || title.Any(char.IsControl)) return new(true, null, L.T("提醒事项请控制在1至160个字。"));
        string? schedule = EnglishSchedule(when);
        if (schedule == null) return EnglishTimeHelp();
        // Reuse the existing bounded scheduler, including DST gaps and repeated local times.
        var result = Parse(schedule + "提醒我" + title, now, zone);
        return result with { Error = L.T(result.Error) };
    }

    private static ReminderParseResult EnglishTimeHelp() => new(true, null,
        L.T("请写明时间，例如：Remind me to drink water in 30 minutes，或 Remind me tomorrow at 9 am to call Alex。"));

    private static string? EnglishSchedule(string input)
    {
        string text = Regex.Replace(input.Trim(), @"\s+", " ");
        var duration = Regex.Match(text, @"^(?<kind>in|every)\s+(?<value>(?:\d|an?\b|one\b|half\b).+)$", EnglishMatch);
        if (duration.Success)
        {
            string value = duration.Groups["value"].Value;
            value = Regex.Replace(value, @"\b(?:an?|one)\b", "1", EnglishMatch);
            value = Regex.Replace(value, @"\bhalf (?:an? )?hour\b", "30 minutes", EnglishMatch);
            var units = Regex.Matches(value, @"(?<number>\d{1,9})\s*(?<unit>weeks?|days?|hours?|hrs?|minutes?|mins?|seconds?|secs?)\b", EnglishMatch);
            if (units.Count == 0) return null;
            string remainder = Regex.Replace(Regex.Replace(value, @"\d{1,9}\s*(?:weeks?|days?|hours?|hrs?|minutes?|mins?|seconds?|secs?)\b", "", EnglishMatch), @"\band\b|[\s,]", "", EnglishMatch);
            if (remainder.Length != 0) return null;
            long total = 0;
            foreach (Match unit in units)
            {
                long amount = long.Parse(unit.Groups["number"].Value, CultureInfo.InvariantCulture);
                string name = unit.Groups["unit"].Value.ToLowerInvariant();
                long scale = name.StartsWith("week") ? 604800 : name.StartsWith("day") ? 86400 : name.StartsWith("h") ? 3600 : name.StartsWith("m") ? 60 : 1;
                total += amount * scale;
                if (total > 31_536_000) return null;
            }
            return (duration.Groups["kind"].Value.Equals("every", StringComparison.OrdinalIgnoreCase) ? "每隔" : "") + total + "秒后";
        }
        var clock = Regex.Match(text, @"^(?<day>today|tomorrow|every day|every weekday|every weekend|every (?:monday|tuesday|wednesday|thursday|friday|saturday|sunday)|on \d{4}-\d{1,2}-\d{1,2})?\s*(?:at\s+)?(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<period>am|pm)?$", EnglishMatch);
        if (!clock.Success) return null;
        int hour = int.Parse(clock.Groups["hour"].Value, CultureInfo.InvariantCulture);
        int minute = clock.Groups["minute"].Success ? int.Parse(clock.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
        string period = clock.Groups["period"].Value.ToLowerInvariant();
        if (minute > 59 || period.Length > 0 && hour is < 1 or > 12 || period.Length == 0 && hour > 23) return null;
        if (period.Length > 0) hour = hour % 12 + (period == "pm" ? 12 : 0);
        string day = clock.Groups["day"].Value.ToLowerInvariant();
        string prefix = day switch
        {
            "tomorrow" => "明天", "every day" => "每天", "every weekday" => "每个工作日", "every weekend" => "每个周末",
            "every monday" => "每周一", "every tuesday" => "每周二", "every wednesday" => "每周三", "every thursday" => "每周四",
            "every friday" => "每周五", "every saturday" => "每周六", "every sunday" => "每周日",
            _ => day.StartsWith("on ", StringComparison.Ordinal) ? day[3..] + "T" : "今天"
        };
        return prefix + hour + ":" + minute.ToString("00", CultureInfo.InvariantCulture);
    }
}
