using alpha_island.Services;

internal static partial class Program
{
    private static void BootTimingPolicyChecks()
    {
        var boot = DateTimeOffset.Parse("2026-10-21T13:30:07.500Z");
        const string user = "S-1-5-21-123-456-789-1001";
        string Marker(DateTimeOffset finished, string owner = user, string task = "AllLogonTasks", string provider = "Microsoft-Windows-Shell-Core", int id = 62171) =>
            $"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='{provider}'/><EventID>{id}</EventID><TimeCreated SystemTime='{finished:O}'/><Security UserID='{owner}'/></System><EventData><Data Name='TaskName'>{task}</Data></EventData></Event>";
        var reference = BootTimingService.FromDesktopLogonXml(Marker(boot.AddSeconds(30.5)), boot, user);
        Check(reference?.DesktopDuration == TimeSpan.FromSeconds(30.5) && reference.IsEstimate
            && reference.DisplayText.Contains("参考") && reference.DisplayText.Contains("30.5 秒")
            && !reference.DisplayText.Contains("进入桌面用时"), "缺失性能记录时显示桌面初始化参考耗时，不冒充精确测量");
        Check(reference!.BootStartedUtc == boot && reference.Source.Contains("不含 BIOS"), "参考记录保留真实开机时间及测量口径");
        Check(BootTimingService.FromDesktopLogonXml(Marker(boot.AddDays(-1)), boot, user) == null,
            "上一轮启动的桌面完成事件不冒充本次耗时");
        Check(BootTimingService.FromDesktopLogonXml(Marker(boot), boot, user) == null
            && BootTimingService.FromDesktopLogonXml(Marker(boot.AddSeconds(-1)), boot, user) == null,
            "零值或倒序桌面事件不会生成耗时");
        Check(BootTimingService.FromDesktopLogonXml(Marker(boot.AddMinutes(11)), boot, user) == null,
            "启动很久后的重新登录或 shell 重启不用于开机参考");
        Check(BootTimingService.FromDesktopLogonXml(Marker(boot.AddSeconds(30), owner: "S-1-5-18"), boot, user) == null
            && BootTimingService.FromDesktopLogonXml(Marker(boot.AddSeconds(30)), boot, "") == null,
            "其他用户或身份缺失的桌面事件不用于当前用户开机耗时");
        Check(BootTimingService.FromDesktopLogonXml(Marker(boot.AddSeconds(30), task: "PreRoamingTasks"), boot, user) == null
            && BootTimingService.FromDesktopLogonXml(Marker(boot.AddSeconds(30), id: 62170), boot, user) == null
            && BootTimingService.FromDesktopLogonXml(Marker(boot.AddSeconds(30), provider: "OtherProvider"), boot, user) == null,
            "普通登录子任务、任务开始和其他来源不会被误认为桌面初始化完成");
        string performance = $"<Event><EventData><Data Name='BootStartTime'>{boot:O}</Data><Data Name='MainPathBootTime'>25100</Data></EventData></Event>";
        var exact = BootTimingService.FromPerformanceXml(performance, boot)!;
        Check(!exact.IsEstimate && exact.DisplayText == "进入桌面用时 25.1 秒", "本次性能记录仍使用精确主启动路径耗时");
        Check(BootTimingService.FromPerformanceXml(performance, boot.AddDays(1)) == null, "精确结果同样严格拒绝上一轮启动");
        Check(BootTimingService.NeedsExactRecord(reference, boot.AddMinutes(2))
            && !BootTimingService.NeedsExactRecord(exact, boot.AddMinutes(2)), "参考结果可继续等待精确记录，精确结果停止轮询");
        Check(!BootTimingService.NeedsExactRecord(reference, boot.AddMinutes(10))
            && !BootTimingService.NeedsExactRecord(reference, boot.AddSeconds(-1)), "启动窗口已过或系统时钟异常时停止有界重试");
        var unknown = new BootTimingReading(boot, null, "没有本次性能记录", "Windows 启动事件");
        Check(BootTimingService.PreferReading(reference, unknown) == reference
            && BootTimingService.PreferReading(reference, unknown with { BootStartedUtc = null }) == reference,
            "等待精确记录时偶发日志读取失败不会清空已核验的参考结果");
        Check(BootTimingService.PreferReading(reference, exact) == exact
            && BootTimingService.PreferReading(exact, reference) == exact,
            "参考结果可以升级为精确结果，精确结果不会被后续参考结果覆盖");
        Check(BootTimingService.PreferReading(reference, exact with { BootStartedUtc = boot.AddDays(-1) }) == reference,
            "等待期间出现其他启动的记录不会覆盖本次结果");
        Check(unknown.DisplayText.Contains(boot.ToLocalTime().ToString("HH:mm:ss")) && unknown.DisplayText.Contains("暂无耗时")
            && !unknown.DisplayText.Contains("无读取权限"), "缺少参考时仍显示已知开机时刻，不错误归因于权限");
        Check(new BootTimingReading(null, null, "没有启动记录", "Windows").DisplayText.Contains("暂时无法读取"),
            "所有启动来源缺失时明确说明不可读取，不伪造秒数");
    }

    private static void BootTimingLiveReadChecks()
    {
        var reading = BootTimingService.Read();
        Console.WriteLine("Boot timing: " + reading.DisplayText + "; reason=" + reading.Reason + "; source=" + reading.Source);
        Check(reading.DesktopDuration is { TotalMilliseconds: > 0 },
            "本机本次开机能读取可核验的启动耗时，而不是一直显示尚未提供记录");
        Check(reading.BootStartedUtc <= DateTimeOffset.UtcNow && (!reading.IsEstimate || reading.DisplayText.Contains("参考")),
            "本机结果具有有效启动时间，并明确区分精确与参考结果");
        Console.WriteLine($"Boot proof: started={reading.BootStartedUtc:O}; seconds={reading.DesktopDuration?.TotalSeconds:0.000}; estimate={reading.IsEstimate}");
    }
}
