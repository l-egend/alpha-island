using AlphaIsland.PluginSdk;

namespace alpha_island.Services;

/// <summary>Shared relevance and field boundary for automatic context and on-demand island reads.</summary>
public static class DesktopContextPolicy
{
    public static readonly IReadOnlyList<string> InformationSections = Array.AsReadOnly(new[]
    {
        "devices", "power", "media", "audio", "resources", "network", "weather", "focus", "productivity",
        "notifications", "agents", "clipboard", "reminders", "launcher", "plugins", "island", "privacy", "foreground"
    });
    public static string? SectionForField(string field) => field switch
    {
        "deviceBatteries" => "devices", "power" => "power", "media" => "media", "audio" => "audio",
        "cpu" or "memory" or "resources" => "resources", "downloads" or "network" or "dailyTraffic" => "network",
        "weather" => "weather", "focus" => "focus", "productivity" => "productivity",
        "notifications" => "notifications", "agents" => "agents", "clipboard" => "clipboard",
        "reminders" => "reminders", "launcher" => "launcher", "plugins" => "plugins", "island" or "game" => "island",
        "microphone" or "camera" or "microphoneMuted" or "privacyStatus" => "privacy", "foregroundApp" => "foreground",
        _ => null
    };
    private static bool Has(string prompt, params string[] words) => words.Any(w => prompt.Contains(w, StringComparison.OrdinalIgnoreCase));
    public static bool ExcludesContext(string prompt) => prompt.Length > 12000
        || Has(prompt, "写首诗", "写诗", "翻译以下", "翻译这段", "写小说", "write a poem", "translate this");
    public static string[] RequestedSections(string prompt)
    {
        if (ExcludesContext(prompt)) return [];
        if (Has(prompt, "系统状态", "电脑状态", "当前状态", "所有信息", "所有状态", "岛内信息", "岛内状态", "desktop status", "system status")) return ["all"];
        var sections = new List<string>();
        void Add(string section, params string[] words) { if (Has(prompt, words)) sections.Add(section); }
        Add("devices", "电量", "电池", "充电", "鼠标", "键盘", "耳机", "手柄", "接收器", "蓝牙", "battery", "mouse", "keyboard", "headphone", "earbud", "controller", "bluetooth");
        Add("power", "电脑电", "笔记本", "电量", "电池", "充电", "供电", "battery", "charging", "power supply");
        Add("privacy", "会议", "开会", "麦克风", "摄像头", "开麦", "meeting", "microphone", "camera");
        Add("media", "音乐", "歌曲", "播放", "切歌", "暂停", "music", "song", "playing", "track", "media");
        Add("audio", "静音", "音量", "声音", "开麦", "volume", "mute", "audio");
        Add("island", "游戏", "战绩", "小岛", "置顶", "免打扰", "game", "gaming", "island");
        Add("network", "下载", "网速", "网络", "流量", "download", "network", "traffic", "bandwidth");
        Add("resources", "卡顿", "电脑慢", "内存", "风扇", "性能", "功耗", "cpu", "gpu", "memory", "slow", "电脑卡");
        Add("foreground", "前台", "当前窗口", "哪个应用", "foreground", "active app");
        Add("weather", "天气", "气温", "温度", "下雨", "降雨", "weather", "temperature", "rain");
        Add("focus", "番茄", "专注", "计时", "pomodoro", "focus", "timer");
        Add("productivity", "统计", "生产力", "时间轴", "用了多久", "用时", "玩了多久", "听了多久", "productivity", "timeline");
        Add("notifications", "通知", "未读", "提醒中心", "notification", "unread");
        Add("agents", "助手", "智能体", "agent", "codex", "claude", "反重力");
        Add("clipboard", "剪贴板", "剪切板", "复制历史", "clipboard");
        Add("reminders", "提醒", "待办", "reminder");
        Add("launcher", "启动器", "快速启动", "快捷启动", "launcher");
        Add("plugins", "插件", "plugin");
        return sections.ToArray();
    }
    public static DesktopContextSnapshot Select(DesktopContextSnapshot snapshot, IReadOnlyList<string> sections)
    {
        if (snapshot.Fields == null) return snapshot with { Fields = new() };
        bool all = sections.Contains("all", StringComparer.Ordinal);
        var fields = new Dictionary<string, string>();
        int remaining = 12000;
        foreach (var (key, value) in snapshot.Fields)
        {
            var section = SectionForField(key);
            if (section == null || !all && !sections.Contains(section, StringComparer.Ordinal) || string.IsNullOrWhiteSpace(value)) continue;
            int limit = key == "deviceBatteries" ? 3000 : key is "weather" or "plugins" or "island" ? 1200 : 600;
            string bounded = value[..Math.Min(value.Length, Math.Min(limit, remaining))];
            if (bounded.Length == 0) break;
            fields[key] = bounded; remaining -= bounded.Length;
        }
        return snapshot with { Fields = fields };
    }
    public static DesktopContextSnapshot Filter(DesktopContextSnapshot snapshot, string prompt)
        => Select(snapshot, RequestedSections(prompt));
}
