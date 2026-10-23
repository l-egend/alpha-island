namespace alpha_island.Core;

internal sealed record DesktopSuggestion(string Kind, string Title, string Detail);
internal sealed class MeetingGuard
{
    private string _candidate = "";
    private DateTimeOffset _since, _absent;
    private bool _offered, _mutedOffered;
    private static readonly string[] MeetingApps = ["teams", "zoom", "wemeet", "腾讯会议", "meeting", "feishu", "飞书", "dingtalk", "钉钉"];
    internal static bool IsMeetingApp(string name) => MeetingApps.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));
    internal DesktopSuggestion? Observe(IEnumerable<string> applications, bool fresh, bool? microphoneMuted, DateTimeOffset now)
    {
        if (!fresh) { _since = now; return null; }
        string app = applications.FirstOrDefault(IsMeetingApp) ?? "";
        if (app.Length == 0)
        {
            if (_absent == default) _absent = now;
            if (now - _absent >= TimeSpan.FromSeconds(20)) { _candidate = ""; _offered = _mutedOffered = false; }
            return null;
        }
        _absent = default;
        if (_candidate != app) { _candidate = app; _since = now; _offered = _mutedOffered = false; }
        if (now - _since < TimeSpan.FromSeconds(6)) return null;
        if (microphoneMuted == true && !_mutedOffered)
        {
            _mutedOffered = true;
            return new("microphone", "会议麦克风提醒", "检测到 " + app + " 正在使用设备；默认通信麦克风已在系统中静音，请确认是否需要开麦。");
        }
        if (!_offered)
        {
            _offered = true;
            return new("meeting", "会议助手", "检测到 " + app + " 正在使用设备。要打开助手，在提供会议笔记后整理待办吗？");
        }
        return null;
    }
}
