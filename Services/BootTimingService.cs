using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;

namespace alpha_island.Services;

internal sealed record BootTimingReading(DateTimeOffset? BootStartedUtc, TimeSpan? DesktopDuration, string Reason, string Source, bool Waiting = false)
{
    internal string DisplayText => DesktopDuration is TimeSpan duration
        ? $"进入桌面用时 {Format(duration)}" : Waiting ? "正在等待 Windows 精确开机记录…"
        : Reason.Contains("不可读取") ? "开机日志无读取权限，请在设置重新开启自启动" : "Windows 尚未提供本次开机性能记录";
    private static string Format(TimeSpan duration) => duration.TotalSeconds < 60 ? $"{duration.TotalSeconds:0.0} 秒"
        : $"{(int)duration.TotalMinutes} 分 {duration.Seconds} 秒";
}

/// <summary>One-shot read only. No WMI, admin helper, tracing session, or persistent listener.</summary>
internal static class BootTimingService
{
    internal static BootTimingReading Read()
    {
        DateTimeOffset? started = null;
        string reason = "系统没有提供本次启动记录";
        try
        {
            using var record = Latest("System", "*[System[Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12]]");
            if (record != null) started = Date(Data(record.ToXml()), "StartTime");
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or System.Security.SecurityException)
        { reason = "系统启动记录不可读取：" + ex.Message; }
        if (started == null || started > DateTimeOffset.UtcNow) return new(null, null, reason, "Windows 启动事件");

        try
        {
            using var record = Latest(BootLogAccess.Channel, "*[System[EventID=100]]");
            if (record != null && FromPerformanceXml(record.ToXml(), started.Value) is { } exact) return exact;
            reason = "没有与本次启动匹配的精确性能记录";
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or System.Security.SecurityException)
        { reason = "精确启动性能记录不可读取：" + ex.Message; }

        return new(started, null, reason, "Windows 启动性能记录");
    }

    internal static async Task<BootTimingReading> ReadWhenReadyAsync(Func<bool> stopping)
    {
        var reading = await Task.Run(Read);
        // Event 100 may be committed after post-boot work. Wait asynchronously, never block the island.
        for (int attempt = 0; attempt < 60 && !stopping() && reading.DesktopDuration == null && reading.BootStartedUtc is { } started
            && DateTimeOffset.UtcNow - started < TimeSpan.FromMinutes(10); attempt++)
        {
            if (!await Task.Run(BootLogAccess.CanRead)) break;
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (stopping()) break;
            reading = await Task.Run(Read);
        }
        return reading;
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

    private static EventRecord? Latest(string log, string query)
    {
        using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, query) { ReverseDirection = true });
        return reader.ReadEvent(TimeSpan.FromSeconds(2));
    }
    private static Dictionary<string, string> Data(string xml) => XDocument.Parse(xml).Descendants()
        .Where(x => x.Name.LocalName == "Data" && x.Attribute("Name") != null)
        .GroupBy(x => (string)x.Attribute("Name")!, StringComparer.Ordinal)
        .ToDictionary(x => x.Key, x => x.First().Value, StringComparer.Ordinal);
    private static DateTimeOffset? Date(Dictionary<string, string> data, string key) => data.TryGetValue(key, out string? value)
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
}
