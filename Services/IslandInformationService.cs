using System.Globalization;
using AlphaIsland.PluginSdk;
using alpha_island.Core;
using alpha_island.ViewModels;

namespace alpha_island.Services;

internal sealed record IslandPluginState(string Name, string Version, bool Running, bool Failed);

/// <summary>Only existing observations are passed in. No clipboard text, images, paths, identifiers or configuration values.</summary>
internal sealed record IslandInformationState
{
    internal IReadOnlyList<DeviceBatterySample> DeviceBatteries { get; init; } = [];
    internal BatteryStatusInfo? Power { get; init; }
    internal SystemResourceSnapshot Resources { get; init; }
    internal NetworkSpeedSample? Network { get; init; }
    internal DailyTrafficDay? DailyTraffic { get; init; }
    internal string NetworkSource { get; init; } = "本机采样";
    internal WeatherSnapshot? Weather { get; init; }
    internal int? Volume { get; init; }
    internal bool? Muted { get; init; }
    internal ProductivityDay? Productivity { get; init; }
    internal int? ClipboardCount { get; init; }
    internal bool SensitiveGuard { get; init; }
    internal int? ActiveReminders { get; init; }
    internal int? LauncherCount { get; init; }
    internal IReadOnlyList<IslandPluginState> Plugins { get; init; } = [];
    internal bool GameMode { get; init; }
    internal bool QuietMode { get; init; }
    internal bool Pinned { get; init; }
}

/// <summary>Formats bounded read-only information from the same observations as cards. No polling or retained state.</summary>
internal static class IslandInformationService
{
    internal static DesktopContextSnapshot Capture(IslandWorkspaceViewModel workspace, IslandInformationState state,
        DesktopContextSnapshot baseline, IReadOnlyList<string> sections, DateTimeOffset now)
    {
        var fields = new Dictionary<string, string>(baseline.Fields);
        bool all = sections.Contains("all", StringComparer.Ordinal);
        void Put(string key, Func<string> value)
        {
            var section = DesktopContextPolicy.SectionForField(key);
            if (all || sections.Contains(section!, StringComparer.Ordinal))
                fields[key] = SensitiveTextDetector.Inspect(value()).Redacted;
        }
        Put("deviceBatteries", () => FormatDevices(state.DeviceBatteries, now));
        Put("power", () => FormatPower(state.Power, now));
        Put("media", () => workspace.Media.Current is not { } media ? "没有可用的媒体会话"
            : $"{(media.IsPlaying ? "正在播放" : "未在播放")}：{media.SourceName} · {media.Title} · {media.Artist}；进度 {media.Position:hh\\:mm\\:ss}/{media.Duration:hh\\:mm\\:ss}");
        Put("audio", () => state.Volume is int volume && state.Muted is bool muted
            ? $"系统扬声器 {volume}% · {(muted ? "已静音" : "未静音")}" : "系统扬声器状态暂不可用");
        Put("resources", () => $"CPU {Percent(state.Resources.CpuPercent)}；内存 {Percent(state.Resources.MemoryPercent)}；"
            + $"CPU 功耗 {Watts(state.Resources.CpuWatts)}，GPU 功耗 {Watts(state.Resources.GpuWatts)}，合计功耗 {Watts(state.Resources.TotalWatts)}。未启用或未采到的指标保持未知");
        Put("network", () => state.Network is { } network
            ? $"下载 {Bytes(network.DownBytesPerSecond)}/s；上传 {Bytes(network.UpBytesPerSecond)}/s；来源 {state.NetworkSource}"
            : "网速监测未启用或尚无可用采样");
        Put("dailyTraffic", () => state.DailyTraffic is { } traffic && traffic.Date == DateOnly.FromDateTime(now.LocalDateTime)
            ? $"{traffic.Date:yyyy-MM-dd} 下载 {Bytes(traffic.Down)}；上传 {Bytes(traffic.Up)}" : "今日流量尚无可用记录");
        Put("downloads", () => $"已观测 {workspace.Downloads.Current.Count} 个下载，{workspace.Downloads.Current.Count(d => d.IsRunning)} 个正在进行；"
            + (workspace.Downloads.SampledUtc == default ? "尚未采样" : $"最近采样 {workspace.Downloads.SampledUtc:O}") + "；不包含文件名和下载地址");
        Put("weather", () => FormatWeather(state.Weather, now));
        Put("focus", () => $"番茄钟{(workspace.Focus.Timer.Running ? "运行中" : workspace.Focus.Timer.HasStarted ? "已暂停" : "未启动")}；"
            + $"{(workspace.Focus.Timer.IsBreak ? "休息" : "专注")}阶段；剩余 {workspace.Focus.Timer.DisplayRemaining()}；已完成 {workspace.Focus.Timer.CompletedFocus} 轮");
        Put("productivity", () => state.Productivity is { } day
            ? $"{day.Date} 已记录：专注 {Duration(day.FocusSeconds)}，游戏 {Duration(day.GameSeconds)}，音乐 {Duration(day.MusicSeconds)}，助手 {Duration(day.AgentSeconds)}；类别可能重叠，不能相加作为电脑使用时长"
            : "生产力统计未开启或当前没有可用记录");
        Put("notifications", () => $"通知中心共 {workspace.Notifications.Inbox.History.Count} 条；未读 {workspace.Notifications.Inbox.History.Count(n => !n.IsRead)} 条；"
            + $"待处理 {workspace.Notifications.Inbox.History.Count(n => n.Reminder is { Pending: true })} 条；不包含通知正文");
        Put("agents", () =>
        {
            var agents = workspace.Agents.Sessions.Where(s => s.Activity?.State is not null and not AgentRunState.Idle).ToArray();
            return agents.Length == 0 ? "没有正在显示的助手任务" : $"当前 {agents.Length} 个助手任务：" + string.Join("；", agents.Take(12).Select(s => $"{s.Source} {s.Number} · {s.Activity!.State}")) + "；不包含对话内容或执行命令";
        });
        Put("clipboard", () => state.ClipboardCount is int count
            ? $"已保存 {count} 条历史；敏感内容保护{(state.SensitiveGuard ? "开启" : "关闭")}；不读取剪贴板正文" : "剪贴板历史服务暂不可用");
        Put("reminders", () => state.ActiveReminders is int count ? $"已启用 {count} 条本地提醒；查询具体事项请使用 list_reminders" : "本地提醒未启用或服务不可用");
        Put("launcher", () => state.LauncherCount is int count ? $"快速启动器已索引 {count} 个应用；不包含应用路径" : "快速启动器尚未建立索引");
        Put("plugins", () => state.Plugins.Count == 0 ? "没有已发现的插件" : string.Join("；", state.Plugins.Take(16)
            .Select(p => $"{p.Name} {p.Version} · {(p.Running ? "运行中" : p.Failed ? "加载失败" : "未运行")}")));
        Put("island", () => $"游戏模式{OnOff(state.GameMode)}；免打扰{OnOff(state.QuietMode)}；置顶{OnOff(state.Pinned)}；"
            + "可用卡片：" + string.Join("、", workspace.Pages.Host.Pages.Take(24).Select(p => p.Title))
            + "。可读取设备电量、电源、媒体、音量、资源、网络/流量、天气、番茄钟、今日统计、通知数量、助手状态、隐私占用、前台应用和功能概况；详细状态按对应分区读取");
        return DesktopContextPolicy.Select(new(now, fields), sections);
    }

    internal static string FormatDevices(IEnumerable<DeviceBatterySample> samples, DateTimeOffset now)
    {
        var groups = DeviceBatteryCatalog.Group(samples, now);
        if (groups.Count == 0) return "当前未观测到已连接的蓝牙或接收器设备；断开的设备不列出，不代表所有设备都支持电量上报";
        var rows = groups.Take(16).Select(group =>
        {
            var device = group.Display.Device;
            string kind = device.Kind switch { DeviceBatteryKind.Mouse => "鼠标", DeviceBatteryKind.Keyboard => "键盘", DeviceBatteryKind.Audio => "耳机/音频", DeviceBatteryKind.Gamepad => "手柄", _ => "设备" };
            string transport = device.Transport == DeviceBatteryTransport.Bluetooth ? "蓝牙" : device.Transport == DeviceBatteryTransport.WirelessReceiver ? "2.4G 接收器" : "USB/接收器";
            var levels = group.Components.Select(r =>
            {
                var d = r.Sample.Device;
                bool stale = r.Sample.Expires && now - r.Sample.UpdatedAt > TimeSpan.FromMinutes(3);
                string level = stale ? "读数已过期" : d.Percent is int percent ? $"{(d.Estimated ? "约 " : "")}{percent}%" : string.IsNullOrWhiteSpace(d.Level) ? "未上报电量" : d.Level + "（非精确百分比）";
                return $"{DeviceBatteryGroup.PartName(r.Part)} {level}{(d.Charging ? "，正在充电" : "")}，最近上报 {r.Sample.UpdatedAt:O}";
            });
            return $"{group.Name}（{kind}，{transport}，已连接）：{string.Join("；", levels)}";
        });
        return string.Join("\n", rows) + (groups.Count > 16 ? $"\n另有 {groups.Count - 16} 个设备，当前返回前 16 个" : "");
    }
    private static string FormatPower(BatteryStatusInfo? power, DateTimeOffset now)
    {
        if (power == null) return "电脑电源状态尚不可用";
        if (!power.HasBattery) return "此电脑未报告内置电池";
        return $"电脑电池 {(power.Percent is >= 0 and <= 100 ? power.Percent + "%" : "电量未知")} · {(power.IsCharging ? "正在充电" : power.IsOnAcPower == true ? "已连接电源" : "未确认正在充电")}；"
            + $"充电功率 {(power.ChargeRateMilliwatts is int mw ? (mw / 1000d).ToString("0.##", CultureInfo.InvariantCulture) + " W" : "未知")}；最近采样 {power.SampledUtc:O}"
            + (power.SampledUtc == default || now.UtcDateTime - power.SampledUtc > TimeSpan.FromMinutes(2) ? "（缓存读数，可能过期）" : "");
    }
    private static string FormatWeather(WeatherSnapshot? weather, DateTimeOffset now)
    {
        if (weather == null) return "天气卡片尚无可用数据，不使用 IP 或模型猜测所在城市";
        return $"{weather.City.Display}：{weather.Description}，{weather.Temperature:0.#}℃，体感 {weather.FeelsLike:0.#}℃，最低/最高 {weather.Low:0.#}/{weather.High:0.#}℃；"
            + $"更新时间 {weather.UpdatedUtc:O}" + (now.UtcDateTime - weather.UpdatedUtc > TimeSpan.FromHours(2) ? "（缓存可能过期）" : "")
            + "；预报：" + string.Join("；", weather.Days.Take(3).Select(d => $"{d.Date:MM-dd} {WeatherService.Describe(d.Code)}，{d.Low:0.#}～{d.High:0.#}℃"));
    }
    private static string OnOff(bool value) => value ? "开启" : "关闭";
    private static string Percent(double? value) => value is double v && double.IsFinite(v) ? v.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "未知";
    private static string Watts(double? value) => value is double v && double.IsFinite(v) ? v.ToString("0.#", CultureInfo.InvariantCulture) + " W" : "未知";
    private static string Duration(double seconds) => $"{Math.Max(0, seconds) / 60:0.#} 分钟";
    private static string Bytes(double value)
    {
        value = double.IsFinite(value) ? Math.Max(0, value) : 0;
        string[] units = ["B", "KB", "MB", "GB", "TB"]; int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
