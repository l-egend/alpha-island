using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Security.Principal;
using System.Xml.Linq;

namespace alpha_island.Services;

internal sealed record BootTimingReading(DateTimeOffset? BootStartedUtc, TimeSpan? DesktopDuration, string Reason, string Source,
    bool Waiting = false, bool IsEstimate = false)
{
    internal string DisplayText => DesktopDuration is TimeSpan duration
        ? IsEstimate ? $"桌面启动参考 {Format(duration)}" : $"进入桌面用时 {Format(duration)}"
        : Waiting ? "正在读取本次开机时间…"
        : BootStartedUtc is { } started ? $"本次开机 {started.ToLocalTime():HH:mm:ss}（暂无耗时）"
        : "暂时无法读取本次开机记录";
    private static string Format(TimeSpan duration) => duration.TotalSeconds < 60 ? $"{duration.TotalSeconds:0.0} 秒"
        : $"{(int)duration.TotalMinutes} 分 {duration.Seconds} 秒";
}

/// <summary>Bounded read only. No WMI, admin helper, tracing session, or persistent listener.</summary>
internal static class BootTimingService
{
    private const string ShellChannel = "Microsoft-Windows-Shell-Core/Operational";
    internal static BootTimingReading Read()
    {
        DateTimeOffset? started = null;
        string reason = "系统没有提供本次启动记录";
        try
        {
            using var record = Latest("System", "*[System[Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12]]");
            if (record != null) started = Date(Data(record.ToXml()), "StartTime");
        }
        catch (Exception ex) when (LogUnavailable(ex))
        { reason = "系统启动记录不可读取：" + ex.Message; }
        if (started == null || started > DateTimeOffset.UtcNow) return new(null, null, reason, "Windows 启动事件");

        try
        {
            using var record = Latest(BootLogAccess.Channel, "*[System[EventID=100]]");
            if (record != null && FromPerformanceXml(record.ToXml(), started.Value) is { } exact) return exact;
            reason = "没有与本次启动匹配的精确性能记录";
        }
        catch (Exception ex) when (LogUnavailable(ex))
        { reason = "精确启动性能记录不可读取：" + ex.Message; }

        // Some Windows installations stop producing Event 100 even though the log is enabled.
        // AllLogonTasks is a desktop initialization marker, not the official MainPath measurement.
        // Match the current boot and current user, and take the first completion rather than a later logon.
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is { } user)
            {
                string from = started.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
                string until = started.Value.AddMinutes(10).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
                string query = $"*[System[Provider[@Name='Microsoft-Windows-Shell-Core'] and EventID=62171 and Security[@UserID='{user.Value}'] and TimeCreated[@SystemTime>='{from}' and @SystemTime<='{until}']] and EventData[Data[@Name='TaskName']='AllLogonTasks']]";
                using var record = Latest(ShellChannel, query, reverse: false);
                if (record != null && FromDesktopLogonXml(record.ToXml(), started.Value, user.Value) is { } reference)
                    return reference with { Reason = reason };
            }
        }
        catch (Exception ex) when (LogUnavailable(ex))
        { reason += "；桌面初始化记录不可读取：" + ex.Message; }
        return new(started, null, reason, "Windows 启动事件（仅启动时间，未估算耗时）");
    }

    internal static async Task<BootTimingReading> ReadWhenReadyAsync(Func<bool> stopping, Action<BootTimingReading>? onAvailable = null)
    {
        var reading = await Task.Run(Read);
        if (!stopping() && reading.DesktopDuration != null) onAvailable?.Invoke(reading);
        // Publish the reference immediately, but allow a delayed Event 100 to replace it with the exact result.
        for (int attempt = 0; attempt < 60 && !stopping() && NeedsExactRecord(reading, DateTimeOffset.UtcNow); attempt++)
        {
            if (!await Task.Run(BootLogAccess.CanRead)) break;
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (stopping()) break;
            var updated = PreferReading(reading, await Task.Run(Read));
            if (!stopping() && updated != reading && updated.DesktopDuration != null) onAvailable?.Invoke(updated);
            reading = updated;
        }
        return reading;
    }

    // A transient log read failure must not erase an already verified result while waiting for Event 100.
    internal static BootTimingReading PreferReading(BootTimingReading current, BootTimingReading updated)
    {
        if (current.BootStartedUtc != null && updated.BootStartedUtc != current.BootStartedUtc) return current;
        if (current.DesktopDuration != null && (updated.DesktopDuration == null || !current.IsEstimate || updated.IsEstimate)) return current;
        return updated;
    }

    internal static bool NeedsExactRecord(BootTimingReading reading, DateTimeOffset now) =>
        (reading.DesktopDuration == null || reading.IsEstimate) && reading.BootStartedUtc is { } started
        && now >= started && now - started < TimeSpan.FromMinutes(10);

    internal static BootTimingReading? FromDesktopLogonXml(string xml, DateTimeOffset currentBoot, string currentUserSid)
    {
        var document = XDocument.Parse(xml);
        var system = document.Root?.Elements().FirstOrDefault(x => x.Name.LocalName == "System");
        XElement? Element(string name) => system?.Elements().FirstOrDefault(x => x.Name.LocalName == name);
        if (string.IsNullOrEmpty(currentUserSid)
            || (string?)Element("Provider")?.Attribute("Name") != "Microsoft-Windows-Shell-Core"
            || Element("EventID")?.Value != "62171"
            || !string.Equals((string?)Element("Security")?.Attribute("UserID"), currentUserSid, StringComparison.OrdinalIgnoreCase)
            || !Data(xml).TryGetValue("TaskName", out var task) || task != "AllLogonTasks"
            || !DateTimeOffset.TryParse((string?)Element("TimeCreated")?.Attribute("SystemTime"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var finished)) return null;
        var duration = finished - currentBoot;
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(10)) return null;
        return new(currentBoot, duration, "", "Windows 桌面登录初始化完成记录（参考，不含 BIOS，不等同完整进入桌面耗时）", IsEstimate: true);
    }

    internal static BootTimingReading? FromPerformanceXml(string xml, DateTimeOffset currentBoot)
    {
        var data = Data(xml);
        DateTimeOffset? start = Date(data, "BootStartTime");
        if (start == null || Math.Abs((start.Value - currentBoot).TotalSeconds) > 30 ||
            !data.TryGetValue("MainPathBootTime", out string? value) || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long ms)
            || ms is <= 0 or > 600_000) return null;
        return new(currentBoot, TimeSpan.FromMilliseconds(ms), "", "Windows 主启动路径性能记录（不含 BIOS 与桌面后台收尾）");
    }

    private static bool LogUnavailable(Exception ex) => ex is EventLogException or UnauthorizedAccessException or System.Security.SecurityException;
    private static EventRecord? Latest(string log, string query, bool reverse = true)
    {
        using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, query) { ReverseDirection = reverse });
        return reader.ReadEvent(TimeSpan.FromSeconds(2));
    }
    private static Dictionary<string, string> Data(string xml) => XDocument.Parse(xml).Descendants()
        .Where(x => x.Name.LocalName == "Data" && x.Attribute("Name") != null)
        .GroupBy(x => (string)x.Attribute("Name")!, StringComparer.Ordinal)
        .ToDictionary(x => x.Key, x => x.First().Value, StringComparer.Ordinal);
    private static DateTimeOffset? Date(Dictionary<string, string> data, string key) => data.TryGetValue(key, out string? value)
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
}