using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;

namespace alpha_island.Services;

public sealed record ReminderDraft(string Title, DateTimeOffset Due, string ZoneId, int[] Weekdays, int IntervalMinutes = 0)
{
    public bool Repeats => Weekdays.Length > 0 || IntervalMinutes > 0;
}
public sealed record ReminderParseResult(bool IsRequest, ReminderDraft? Draft, string Error = "");

/// <summary>Deterministic local interpretation. Never sends text to a model or executes it.</summary>
public static partial class ReminderTextParser
{
    private const string Number = @"[0-9零〇一二两三四五六七八九十百千]+";
    private static readonly Regex Duration = new($@"^(?:(?<d>{Number})天)?(?:(?<h>{Number})(?:个)?小时)?(?:(?<m>{Number})(?:个)?分(?:钟)?)?(?:(?<s>{Number})秒(?:钟)?)?后$", RegexOptions.CultureInvariant);
    private static readonly Regex Clock = new($@"^(?<period>凌晨|早上|早晨|上午|中午|下午|傍晚|晚上|夜里)?\s*(?<h>{Number})(?:[:：](?<m>\d{{1,2}})|点(?:(?<m>{Number})分?|(?<part>半|一刻|三刻))?)$", RegexOptions.CultureInvariant);
    public static bool IsListRequest(string text) => Regex.IsMatch(text.Trim(), @"^(?:please )?(?:(?:show|list)(?: me)?(?: my)? reminders|my reminders)[.!?]?$", EnglishMatch) || Regex.IsMatch(text.Trim(), @"^(?:请)?(?:查看|列出|显示|看看)(?:我的)?提醒(?:列表)?[。！？?]?$|^(?:我的)?提醒列表[。！？?]?$|^我有哪些提醒[。！？?]?$", RegexOptions.CultureInvariant);
    public static string? CancellationTitle(string text)
    {
        var english = Regex.Match(text.Trim(), @"^(?:please )?(?:cancel|delete) (?:the )?reminder (?:named )?(?<name>.+?)[.!]?$", EnglishMatch);
        if (english.Success) return english.Groups["name"].Value.Trim();
        var match = Regex.Match(text.Trim(), @"^(?:请)?(?:取消|删除)(?:名为|叫)?(?:提醒[：:]?\s*(?<name>.+)|(?<name>.+?)(?:的)?提醒)[。！]?$", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["name"].Value.Trim().TrimEnd('。', '！') : null;
    }
    public static ReminderParseResult Parse(string input, DateTimeOffset now, TimeZoneInfo zone)
    {
        var result = ParseEnglish(input.Trim(), now, zone) ?? ParseChinese(input, now, zone);
        return result with { Error = AlphaIsland.PluginSdk.Localization.UiText.T(result.Error) };
    }
    private static ReminderParseResult ParseChinese(string input, DateTimeOffset now, TimeZoneInfo zone)
    {
        string normalized;
        try { normalized = input.Trim().Normalize(NormalizationForm.FormKC); }
        catch (ArgumentException) { return new(false, null); }
        if (Regex.IsMatch(normalized, @"(?:这句话|这段话|这段代码).*(?:解释|翻译|意思|怎么|如何)|(?:是什么意思|怎么翻译|如何实现)[。?？]?$", RegexOptions.CultureInvariant)) return new(false, null);
        string text = Regex.Replace(normalized, @"^(?:请|麻烦)?(?:帮我)?(?:(?:设置|创建)(?:一个|一条)?)?", "").Trim();
        if (text.Length == 0) return new(false, null);
        if (text.Length > 512)
        {
            bool explicitReminder = text.StartsWith("提醒我", StringComparison.Ordinal) || Regex.IsMatch(text[..80], @"^(?:今天|明天|后天|每天|每个工作日|每周|\d+(?:分钟|小时|秒|天|点|[:：])).*提醒", RegexOptions.CultureInvariant);
            return new(explicitReminder, null, explicitReminder ? "提醒请求过长，请将时间和事项控制在512字内。" : "");
        }
        string when, title;
        if (text.StartsWith("提醒我", StringComparison.Ordinal) || text.StartsWith("提醒：", StringComparison.Ordinal) || text.StartsWith("提醒:", StringComparison.Ordinal))
        {
            string rest = text[3..].Trim();
            var found = Regex.Match(rest, $@"^(?<when>(?:每隔)?(?:{Number}(?:天|周))?(?:{Number}个?小时)?(?:{Number}分钟)?(?:{Number}秒)?后|半小时后|{Number}(?:个)?半小时后|一刻钟后|半分钟后|(?:今天|明天|后天|今晚|明早|明晚|每天|每日|每个工作日|每个周末|工作日|每周[一二三四五六日天]|(?:下|本)?周[一二三四五六日天]|\d{{4}}[-/]\d{{1,2}}[-/]\d{{1,2}})?(?:凌晨|早上|上午|中午|下午|傍晚|晚上)?{Number}(?:[:：]\d{{1,2}}|点(?:半|一刻|三刻|{Number}分?)?))\s*[,，：:]?\s*(?<title>.+)$", RegexOptions.CultureInvariant);
            if (!found.Success) return new(true, null, "请同时写明时间和事项，例如：30分钟后提醒我喝水，或明天上午9点提醒我开会。");
            when = found.Groups["when"].Value; title = found.Groups["title"].Value;
        }
        else
        {
            var found = Regex.Match(text, @"^(?:在)?(?<when>.+?)\s*[,，]?\s*提醒(?:我)?\s*(?<title>.+)$", RegexOptions.CultureInvariant);
            if (!found.Success) return new(false, null);
            when = found.Groups["when"].Value.Trim(); title = found.Groups["title"].Value;
            if (!Regex.IsMatch(when, @"^(?:今天|明天|后天|今晚|明早|明晚|每天|每日|每个工作日|每个周末|工作日|每周|每隔|本周|下周|周|星期|凌晨|早上|上午|中午|下午|傍晚|晚上|夜里|半|[0-9零〇一二两三四五六七八九十百千])", RegexOptions.CultureInvariant)) return new(false, null);
        }
        title = Regex.Replace(title.Trim().TrimEnd('。', '！'), @"\s+", " ");
        if (title.Length is 0 or > 160 || title.Any(char.IsControl)) return new(true, null, "提醒事项请控制在1至160个字。");
        when = Regex.Replace(when, @"^(\d{4}[-/]\d{1,2}[-/]\d{1,2})\s+", "$1T");
        when = when.Replace(" ", "").Replace("今晚", "今天晚上").Replace("明早", "明天早上").Replace("明晚", "明天晚上").Replace("星期", "周");
        if (when == "半小时后") when = "30分钟后";
        if (when == "一刻钟后") when = "15分钟后";
        if (when == "半分钟后") when = "30秒后";
        when = Regex.Replace(when, $@"^(?<h>{Number})(?:个)?半小时后$", match => (Value(match.Groups["h"].Value) * 60 + 30) + "分钟后");
        when = Regex.Replace(when, $@"^(?<w>{Number})周后$", match => (Value(match.Groups["w"].Value) * 7) + "天后");
        bool interval = when.StartsWith("每隔", StringComparison.Ordinal);
        string durationText = interval ? when[2..] + (when.EndsWith("后", StringComparison.Ordinal) ? "" : "后") : when;
        var duration = Duration.Match(durationText);
        if (duration.Success && (duration.Groups["d"].Success || duration.Groups["h"].Success || duration.Groups["m"].Success || duration.Groups["s"].Success))
        {
            int daysLater = Value(duration.Groups["d"].Value);
            if (daysLater < 0 || Value(duration.Groups["h"].Value) < 0 || Value(duration.Groups["m"].Value) < 0 || Value(duration.Groups["s"].Value) < 0)
                return new(true, null, "时间数值过大或格式无效。");
            long seconds = daysLater * 86400L + (long)Value(duration.Groups["h"].Value) * 3600 + Value(duration.Groups["m"].Value) * 60L + Value(duration.Groups["s"].Value);
            if (seconds is < 1 or > 31_536_000 || interval && (seconds < 60 || seconds > 604800 || seconds % 60 != 0))
                return new(true, null, interval ? "重复提醒请使用1分钟至7天的整分钟间隔。" : "提醒时间请在1秒至365天之间。");
            var dueAfter = !interval && daysLater > 0 ? AtLocal(zone, DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(now, zone).DateTime.AddDays(daysLater).AddSeconds(seconds - daysLater * 86400L), DateTimeKind.Unspecified), now) : now.AddSeconds(seconds);
            return dueAfter == null ? new(true, null, "这个本地时刻在夏令时切换中不存在，请换一个时间。") : new(true, new(title, dueAfter.Value, zone.Id, [], interval ? (int)(seconds / 60) : 0));
        }
        var local = TimeZoneInfo.ConvertTime(now, zone);
        DateTime date = local.Date;
        int[] days = [];
        if (when.StartsWith("每个工作日", StringComparison.Ordinal)) { days = [1,2,3,4,5]; when = when[5..]; }
        else if (when.StartsWith("每个周末", StringComparison.Ordinal)) { days = [0,6]; when = when[4..]; }
        else if (when.StartsWith("工作日", StringComparison.Ordinal)) { days = [1,2,3,4,5]; when = when[3..]; }
        else if (when.StartsWith("每天", StringComparison.Ordinal) || when.StartsWith("每日", StringComparison.Ordinal)) { days = [0,1,2,3,4,5,6]; when = when[2..]; }
        else if (when.StartsWith("每周", StringComparison.Ordinal) && when.Length > 2 && Day(when[2]) is int weekly) { days = [weekly]; when = when[3..]; }
        else if (when.StartsWith("后天", StringComparison.Ordinal)) { date = date.AddDays(2); when = when[2..]; }
        else if (when.StartsWith("明天", StringComparison.Ordinal)) { date = date.AddDays(1); when = when[2..]; }
        else if (when.StartsWith("今天", StringComparison.Ordinal)) when = when[2..];
        else
        {
            var weekday = Regex.Match(when, @"^(?<next>下|本)?周(?<day>[一二三四五六日天])");
            var fullDate = Regex.Match(when, @"^\d{4}[-/]\d{1,2}[-/]\d{1,2}");
            if (weekday.Success)
            {
                int day = Day(weekday.Groups["day"].Value[0])!.Value;
                int offset = (day - (int)local.DayOfWeek + 7) % 7;
                if (weekday.Groups["next"].Value == "下") offset = (7 - ((int)local.DayOfWeek + 6) % 7) + (day + 6) % 7;
                if (weekday.Groups["next"].Value == "本") offset = (day + 6) % 7 - ((int)local.DayOfWeek + 6) % 7;
                date = date.AddDays(offset); when = when[weekday.Length..];
            }
            else if (fullDate.Success)
            {
                if (!DateTime.TryParseExact(fullDate.Value, ["yyyy-M-d","yyyy/M/d"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return new(true, null, "日期无效，请使用有效的年-月-日。");
                when = when[fullDate.Length..].TrimStart('T');
            }
        }
        var clock = Clock.Match(when);
        if (!clock.Success) return new(true, null, "未能确定具体时间。请写成“明天上午9点”“每天15:30”或“30分钟后”等明确时间。");
        int hour = Value(clock.Groups["h"].Value), minute = Value(clock.Groups["m"].Value);
        minute = clock.Groups["part"].Value switch { "半" => 30, "一刻" => 15, "三刻" => 45, _ => minute };
        string period = clock.Groups["period"].Value;
        if (hour is < 0 or > 23 || minute is < 0 or > 59) return new(true, null, "时分超出范围。");
        if (period is "下午" or "傍晚" or "晚上" or "夜里" && hour < 12) hour += 12;
        if (period is "晚上" or "夜里" && hour == 12)
        { hour = 0; date = date.AddDays(1); days = days.Select(day => (day + 1) % 7).ToArray(); }
        if (period is "凌晨" or "早上" or "早晨" or "上午" && hour == 12) hour = 0;
        if (period == "中午" && hour < 11) hour += 12;
        if (hour is < 0 or > 23) return new(true, null, "小时应在0至23之间。");
        var localDue = DateTime.SpecifyKind(date.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
        var due = days.Length > 0 ? NextLocal(zone, hour, minute, days, now) : AtLocal(zone, localDue, now);
        if (due == null || due <= now) return new(true, null, "这个时间已过去或在夏令时切换中不存在，请改用明确的未来时间。");
        if (due > now.AddDays(366)) return new(true, null, "首次提醒请设置在未来一年内。");
        return new(true, new(title, due.Value, zone.Id, days));
    }
    public static DateTimeOffset? NextLocal(TimeZoneInfo zone, int hour, int minute, int[] days, DateTimeOffset after, DateTime? excludedDate = null)
    {
        var date = TimeZoneInfo.ConvertTime(after, zone).Date;
        for (int i = 0; i < 9; i++)
        {
            var day = date.AddDays(i);
            if (day == excludedDate) continue;
            if (!days.Contains((int)day.DayOfWeek)) continue;
            var due = AtLocal(zone, DateTime.SpecifyKind(day.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified), after);
            if (due > after) return due;
        }
        return null;
    }
    private static DateTimeOffset? AtLocal(TimeZoneInfo zone, DateTime local, DateTimeOffset after)
    {
        if (zone.IsInvalidTime(local)) return null;
        if (zone.IsAmbiguousTime(local)) return zone.GetAmbiguousTimeOffsets(local).Select(offset => new DateTimeOffset(local, offset)).Where(value => value > after).OrderBy(value => value.UtcTicks).Cast<DateTimeOffset?>().FirstOrDefault();
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }
    private static int? Day(char day) => day switch { '一'=>1,'二'=>2,'三'=>3,'四'=>4,'五'=>5,'六'=>6,'日' or '天'=>0,_=>null };
    private static int Value(string text)
    {
        if (text.Length == 0) return 0;
        if (text.All(char.IsAsciiDigit)) return int.TryParse(text, out int number) && number is >= 0 and <= 31_536_000 ? number : -1;
        static int Digit(char value) => value switch { '零' or '〇'=>0,'一'=>1,'二' or '两'=>2,'三'=>3,'四'=>4,'五'=>5,'六'=>6,'七'=>7,'八'=>8,'九'=>9,_=>-1 };
        if (!text.Any(value => value is '十' or '百' or '千'))
        {
            long resultDigits = 0;
            foreach (char value in text) { int digitValue = Digit(value); if (digitValue < 0) return -1; resultDigits = resultDigits * 10 + digitValue; if (resultDigits > 31_536_000) return -1; }
            return (int)resultDigits;
        }
        int result = 0, digit = 0, previousUnit = 10000;
        foreach (char c in text)
        {
            int unit = c switch { '十'=>10,'百'=>100,'千'=>1000,_=>0 };
            if (unit > 0) { if (unit >= previousUnit) return -1; result += (digit == 0 ? 1 : digit) * unit; digit = 0; previousUnit = unit; }
            else { digit = Digit(c); if (digit < 0) return -1; }
        }
        return result + digit;
    }
}
