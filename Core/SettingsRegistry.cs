using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace alpha_island.Core
{
    /// <summary>
    /// 设置项的单一真源（阶段 3.5）。见 <see cref="SettingDescriptor"/> 的说明。
    /// <para>
    /// 两条路：
    /// ① 已有 30 个强类型属性保持原样，只把"现在就该有 UI 的"那几项登记进来
    ///    （<c>StoredInExtras = false</c>，Get/Set 直接读写属性）；
    /// ② 新功能的设置项一律走 <see cref="IslandOptions.Extras"/>（<c>StoredInExtras = true</c>），
    ///    不新增强类型属性，避免每加一个功能就动 IslandOptions 的序列化与 CopyFrom。
    /// </para>
    /// </summary>
    public static class SettingsRegistry
    {
        private static readonly List<SettingDescriptor> Items = Build();

        /// <summary>全部设置项描述符（顺序 = 声明顺序 = 设置页里出现的顺序）。</summary>
        public static IReadOnlyList<SettingDescriptor> All => Items;

        public static int Count => Items.Count;

        public static IEnumerable<SettingDescriptor> ByPage(string? pageId)
            => string.IsNullOrEmpty(pageId)
                ? Enumerable.Empty<SettingDescriptor>()
                : Items.Where(d => string.Equals(d.PageId, pageId, StringComparison.OrdinalIgnoreCase));

        public static SettingDescriptor? Find(string? key)
            => string.IsNullOrEmpty(key)
                ? null
                : Items.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

        public static SettingDescriptor? FindByControl(string? controlName)
            => string.IsNullOrEmpty(controlName)
                ? null
                : Items.FirstOrDefault(d => string.Equals(d.ControlName, controlName, StringComparison.Ordinal));

        /// <summary>把某一页上所有设置项的标题/说明/键拼成搜索索引（设置页搜索框用它扩大命中面）。</summary>
        public static string SearchWordsForPage(string? pageId)
        {
            if (string.IsNullOrEmpty(pageId)) return "";
            var words = new List<string>();
            foreach (var d in ByPage(pageId))
            {
                if (!string.IsNullOrWhiteSpace(d.Label)) words.Add(d.Label);
                if (!string.IsNullOrWhiteSpace(d.Hint)) words.Add(d.Hint);
                if (!string.IsNullOrWhiteSpace(d.Key)) words.Add(d.Key);
            }
            return string.Join(" ", words);
        }

        /// <summary>读出设置项的当前值（已按类型收敛 + 夹取；键不存在时返回描述符的默认值）。</summary>
        public static object? Read(IslandOptions options, string key)
        {
            var d = Find(key);
            if (d == null || options == null) return null;
            try
            {
                return d.Coerce(d.Get(options));
            }
            catch (Exception ex)
            {
                IslandLog.Warn("Settings", $"读取设置 {key} 失败，回退默认值: {ex.Message}");
                return d.Coerce(null);
            }
        }

        /// <summary>写入设置项（按类型收敛 + 夹取；键不存在时忽略）。</summary>
        public static void Write(IslandOptions options, string key, object? value)
        {
            var d = Find(key);
            if (d == null || options == null) return;
            try
            {
                d.Set(options, d.Coerce(value));
            }
            catch (Exception ex)
            {
                IslandLog.Warn("Settings", $"写入设置 {key} 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 遍历注册表统一夹取，并把 Extras 里缺的键补成默认值。
        /// 由 <see cref="IslandOptions.Normalized"/> 调用；顺手让 options.json 自带全部可调项。
        /// </summary>
        public static void Normalize(IslandOptions options)
        {
            if (options == null) return;
            options.Extras ??= new Dictionary<string, JsonElement>();
            // The temporary easing comparison ended; window handoff now uses quadratic easing.
            options.Extras.Remove("MediaWindowHandoffEasing");

            foreach (var d in Items)
            {
                object? raw;
                try
                {
                    raw = d.Get(options);
                }
                catch
                {
                    raw = null;
                }

                object? fixedValue = d.Coerce(raw);

                // Extras 里的项一律写回：缺键时正好把默认值落盘；强类型项只在夹取真的改变了值时才写。
                if (d.StoredInExtras || !ValuesEqual(d, raw, fixedValue))
                {
                    try
                    {
                        d.Set(options, fixedValue);
                    }
                    catch (Exception ex)
                    {
                        IslandLog.Warn("Settings", $"规范化设置 {d.Key} 失败: {ex.Message}");
                    }
                }
            }
        }

        // ---- Extras 直读直写：功能代码不必自己处理 JsonElement ----

        public static bool GetBool(IslandOptions options, string key, bool fallback)
            => SettingDescriptor.ToBool(ReadExtra(options, key), fallback);

        public static int GetInt(IslandOptions options, string key, int fallback)
            => (int)Math.Round(SettingDescriptor.ToDouble(ReadExtra(options, key), fallback));

        public static double GetDouble(IslandOptions options, string key, double fallback)
            => SettingDescriptor.ToDouble(ReadExtra(options, key), fallback);

        public static string GetText(IslandOptions options, string key, string fallback)
            => SettingDescriptor.ToText(ReadExtra(options, key)) ?? fallback;

        public static void SetExtra(IslandOptions options, string key, object? value)
        {
            if (options == null) return;
            try
            {
                options.Extras ??= new Dictionary<string, JsonElement>();
                options.Extras[key] = value switch
                {
                    null => JsonSerializer.SerializeToElement<object?>(null, IslandOptions.JsonOptions),
                    bool b => JsonSerializer.SerializeToElement(b, IslandOptions.JsonOptions),
                    int i => JsonSerializer.SerializeToElement(i, IslandOptions.JsonOptions),
                    long l => JsonSerializer.SerializeToElement(l, IslandOptions.JsonOptions),
                    double d => JsonSerializer.SerializeToElement(d, IslandOptions.JsonOptions),
                    _ => JsonSerializer.SerializeToElement(
                        Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", IslandOptions.JsonOptions)
                };
            }
            catch (Exception ex)
            {
                IslandLog.Warn("Settings", $"写入 Extras {key} 失败: {ex.Message}");
            }
        }

        private static JsonElement? ReadExtra(IslandOptions options, string key)
        {
            if (options?.Extras != null && options.Extras.TryGetValue(key, out var element)) return element;
            return null;
        }

        private static bool ValuesEqual(SettingDescriptor d, object? a, object? b)
        {
            object? ca = d.Coerce(a);
            object? cb = d.Coerce(b);
            return ca switch
            {
                null => cb is null,
                bool x => cb is bool y && x == y,
                int x => cb is int y && x == y,
                double x => cb is double y && Math.Abs(x - y) < 1e-9,
                string x => cb is string y && string.Equals(x, y, StringComparison.Ordinal),
                _ => Equals(ca, cb)
            };
        }

        // ---- 描述符声明 ----

        /// <summary>
        /// 标记"这条描述符读写的是 IslandOptions 的强类型属性"（<see cref="SettingDescriptor.StoredInExtras"/>
        /// 缺省 false），与 <c>Extra*</c> 系列（读写 Extras）区分开，一眼能看出值落在哪里。
        /// </summary>
        private static SettingDescriptor Typed(SettingDescriptor descriptor) => descriptor;

        private static SettingDescriptor ExtraBool(string key, string label, string hint, bool def,
            string page = SettingsPageIds.System, string enabledBy = "", string controlName = "")
            => new SettingDescriptor
            {
                Key = key,
                Kind = SettingKind.Bool,
                Label = label,
                Hint = hint,
                PageId = page,
                EnabledByKey = enabledBy,
                ControlName = controlName,
                DefaultValue = def,
                StoredInExtras = true,
                Get = o => GetBool(o, key, def),
                Set = (o, v) => SetExtra(o, key, SettingDescriptor.ToBool(v, def))
            };

        private static SettingDescriptor ExtraInt(string key, string label, string hint, int def, double min, double max,
            string unit = "", string page = SettingsPageIds.System, string enabledBy = "",
            string controlName = "", string valueTextName = "")
            => new SettingDescriptor
            {
                Key = key,
                Kind = SettingKind.Int,
                Label = label,
                Hint = hint,
                PageId = page,
                Min = min,
                Max = max,
                Unit = unit,
                Format = "0",
                EnabledByKey = enabledBy,
                ControlName = controlName,
                ValueTextName = valueTextName,
                DefaultValue = def,
                StoredInExtras = true,
                Get = o => GetInt(o, key, def),
                Set = (o, v) => SetExtra(o, key, (int)Math.Round(SettingDescriptor.ToDouble(v, def)))
            };

        private static SettingDescriptor ExtraDouble(string key, string label, string hint, double def, double min, double max,
            string unit = "", string format = "0.##", string page = SettingsPageIds.System, string enabledBy = "",
            string controlName = "", string valueTextName = "")
            => new SettingDescriptor
            {
                Key = key,
                Kind = SettingKind.Double,
                Label = label,
                Hint = hint,
                PageId = page,
                Min = min,
                Max = max,
                Unit = unit,
                Format = format,
                EnabledByKey = enabledBy,
                ControlName = controlName,
                ValueTextName = valueTextName,
                DefaultValue = def,
                StoredInExtras = true,
                Get = o => GetDouble(o, key, def),
                Set = (o, v) => SetExtra(o, key, SettingDescriptor.ToDouble(v, def))
            };

        private static SettingDescriptor ExtraText(string key, string label, string hint, string def,
            string page = SettingsPageIds.Ai, bool secret = false, string controlName = "")
            => new SettingDescriptor
            {
                Key = key,
                Kind = secret ? SettingKind.Secret : SettingKind.Text,
                Label = label,
                Hint = hint,
                PageId = page,
                DefaultValue = def,
                StoredInExtras = true,
                ControlName = controlName,
                Get = o => GetText(o, key, def),
                Set = (o, v) => SetExtra(o, key, SettingDescriptor.ToText(v) ?? def)
            };

        private static List<SettingDescriptor> Build()
        {
            var list = new List<SettingDescriptor>();
            list.Add(ExtraBool("LauncherEnabled", "快速启动器快捷键", "全局呼出应用、岛内组件和 Agent 搜索", true,
                SettingsPageIds.Behavior, controlName: "LauncherEnabledToggle"));
            list.Add(ExtraText("LauncherShortcut", "快速启动组合键", "默认 Ctrl+Alt+Space；与其他程序冲突时显示提示", "Ctrl+Alt+Space",
                SettingsPageIds.Behavior, controlName: "LauncherShortcutBox"));
            list.Add(ExtraBool("ProductivityEnabled", "记录本机活动时长", "统计音乐、游戏、专注和助手运行；仅保存最近 90 天聚合时长，可在统计卡片导出和清除", true,
                SettingsPageIds.Behavior, controlName: "ProductivityEnabledToggle"));
            list.Add(ExtraBool("NotificationRememberHistory", "保留本机通知历史", "跨重启回溯最近 100 条；内容经当前 Windows 用户加密，关闭后清除已保存记录", true,
                SettingsPageIds.Notifications, controlName: "NotificationRememberHistoryToggle"));

            list.Add(ExtraInt("PomodoroFocusMinutes", "专注时长", "分钟；未开始的阶段立即生效，已开始的阶段保留进度",
                25, 1, 240, "分钟", SettingsPageIds.Focus, controlName: "PomodoroFocusMinutesBox"));
            list.Add(ExtraInt("PomodoroShortBreakMinutes", "短休息时长", "每轮专注后的短休息", 5, 1, 120,
                "分钟", SettingsPageIds.Focus, controlName: "PomodoroShortBreakMinutesBox"));
            list.Add(ExtraBool("PomodoroUseLongBreak", "使用长休息", "每完成指定轮数的专注，改用长休息", false,
                SettingsPageIds.Focus, controlName: "PomodoroUseLongBreakToggle"));
            list.Add(ExtraInt("PomodoroLongBreakMinutes", "长休息时长", "长休息持续时间", 15, 1, 240,
                "分钟", SettingsPageIds.Focus, "PomodoroUseLongBreak", controlName: "PomodoroLongBreakMinutesBox"));
            list.Add(ExtraInt("PomodoroLongBreakEvery", "长休息间隔", "每完成多少轮专注使用一次长休息", 4, 1, 20,
                "轮", SettingsPageIds.Focus, "PomodoroUseLongBreak", controlName: "PomodoroLongBreakEveryBox"));
            list.Add(ExtraBool("PomodoroAutoStart", "启动时自动开始", "下次启动岛时开始一轮专注", false,
                SettingsPageIds.Focus, controlName: "PomodoroAutoStartToggle"));
            list.Add(ExtraBool("PomodoroShowSeconds", "剩余时间显示秒", "关闭后显示向上取整的剩余分钟", true,
                SettingsPageIds.Focus, controlName: "PomodoroShowSecondsToggle"));
            list.Add(ExtraBool("PomodoroAutoCycle", "自动循环", "自动开始下一阶段，完成指定轮数及最后的休息后停止", false,
                SettingsPageIds.Focus, controlName: "PomodoroAutoCycleToggle"));
            list.Add(ExtraInt("PomodoroCycleCount", "自动循环次数", "一次计划包含的专注轮数", 4, 1, 100,
                "轮", SettingsPageIds.Focus, "PomodoroAutoCycle", controlName: "PomodoroCycleCountBox"));
            list.Add(ExtraBool("PomodoroDoubleClickSettings", "双击打开番茄钟设置", "开启：双击打开设置；关闭：双击开始或暂停计时", false,
                SettingsPageIds.Focus, controlName: "PomodoroDoubleClickSettingsToggle"));
            list.Add(ExtraBool("PomodoroPlaySound", "播放提示音", "专注或休息阶段结束时播放所选系统提示音", false,
                SettingsPageIds.Focus, controlName: "PomodoroPlaySoundToggle"));
            list.Add(ExtraText("PomodoroSound", "提示音", "可选择并试听系统提示音", "提示",
                SettingsPageIds.Focus, controlName: "PomodoroSoundBox"));

            list.Add(ExtraBool("WeatherAutoRefresh", "自动刷新天气", "后台定时更新；关闭后仍可在天气卡片手动刷新", true,
                SettingsPageIds.Behavior, controlName: "WeatherAutoRefreshToggle"));
            list.Add(ExtraInt("WeatherRefreshMinutes", "天气刷新间隔", "断网后每 5 分钟重试，恢复后不会补弹旧天气", 15, 5, 120,
                "分钟", SettingsPageIds.Behavior, "WeatherAutoRefresh", controlName: "WeatherRefreshMinutesBox"));
            list.Add(ExtraBool("WeatherConditionNotify", "恶劣天气提醒", "当前或未来 3 小时有雨、雪、雷雨、大风、雾时提醒；遵守静默设置", true,
                SettingsPageIds.Behavior, "WeatherAutoRefresh", controlName: "WeatherConditionNotifyToggle"));
            list.Add(ExtraBool("WeatherTemperatureNotify", "明显温差提醒", "近 3 小时或未来 3 小时的明显升温、降温", true,
                SettingsPageIds.Behavior, "WeatherAutoRefresh", controlName: "WeatherTemperatureNotifyToggle"));
            list.Add(ExtraInt("WeatherTemperatureThreshold", "温差提醒阈值", "达到此温差时提醒；相同方向 3 小时内不重复", 5, 2, 15,
                "°C", SettingsPageIds.Behavior, "WeatherTemperatureNotify", controlName: "WeatherTemperatureThresholdBox"));
            list.Add(ExtraBool("WeatherEveningNotify", "晚间明日天气", "每天晚间获取明日预报，提醒带伞或添衣；须保持程序运行", true,
                SettingsPageIds.Behavior, "WeatherAutoRefresh", controlName: "WeatherEveningNotifyToggle"));
            list.Add(ExtraInt("WeatherEveningHour", "晚间提醒时间", "按电脑当地时间，每晚一次；当晚稍后启动或解锁可补提醒", 20, 18, 23,
                "点", SettingsPageIds.Behavior, "WeatherEveningNotify", controlName: "WeatherEveningHourBox"));

            list.Add(ExtraBool("MediaHideWhenPaused", "媒体暂停时隐藏",
                "暂停提醒结束后自动收起；手动置顶保持显示，可随时打开继续播放", false, SettingsPageIds.Media,
                controlName: "MediaHideWhenPausedToggle"));
            list.Add(ExtraBool("MediaWindowHandoffEnabled", "媒体与助手窗口联动（试验）",
                "音乐软件与 Codex、Antigravity、DSH 在前台时隐藏对应小岛，后台播放或思考时保留小岛，最小化进入岛，点击小岛还原；浏览器随前台切换显隐", true, SettingsPageIds.Media,
                controlName: "MediaWindowHandoffToggle"));

            // ===== 已有强类型属性：原先没有 UI 的三项（3.5 顺手补齐滑杆） =====
            // 三个依赖用的总开关也登记进来，让「置灰级联」由注册表统一处理。
            list.Add(Typed(new SettingDescriptor
            {
                Key = "VolumeNotifyEnabled",
                Kind = SettingKind.Bool,
                Label = "音量调节提示",
                Hint = "调节系统主音量时由岛接管系统音量浮层，展示动态音量条与数字；关闭后恢复系统提示",
                PageId = SettingsPageIds.Notifications,
                ControlName = "VolumeNotifyToggle",
                DefaultValue = true,
                Get = o => o.VolumeNotifyEnabled,
                Set = (o, v) => o.VolumeNotifyEnabled = SettingDescriptor.ToBool(v, true)
            }));

            list.Add(Typed(new SettingDescriptor
            {
                Key = "VolumeNotifySeconds",
                Kind = SettingKind.Int,
                Label = "音量提示停留时长",
                Hint = "音量胶囊展示指定时长后自动收回细条",
                PageId = SettingsPageIds.Notifications,
                ControlName = "VolumeNotifySecondsSlider",
                ValueTextName = "VolumeNotifySecondsValue",
                Min = 1,
                Max = 5,
                Unit = "秒",
                Format = "0",
                EnabledByKey = "VolumeNotifyEnabled",
                DefaultValue = 2,
                Get = o => o.VolumeNotifySeconds,
                Set = (o, v) => o.VolumeNotifySeconds = (int)Math.Round(SettingDescriptor.ToDouble(v, 2))
            }));

            list.Add(Typed(new SettingDescriptor
            {
                Key = "BluetoothNotifyEnabled",
                Kind = SettingKind.Bool,
                Label = "蓝牙连接提示",
                Hint = "蓝牙设备连接时显示对应类型的二维图片、短动画与电量",
                PageId = SettingsPageIds.Notifications,
                ControlName = "BluetoothNotifyToggle",
                DefaultValue = true,
                Get = o => o.BluetoothNotifyEnabled,
                Set = (o, v) => o.BluetoothNotifyEnabled = SettingDescriptor.ToBool(v, true)
            }));

            list.Add(ExtraBool("BluetoothLowBatteryEnabled", "蓝牙设备低电量提醒", "连接设备电量低于 20% / 10% 时提醒；充电后恢复，不重复打扰", true,
                SettingsPageIds.Notifications, controlName: "BluetoothLowBatteryToggle"));

            list.Add(Typed(new SettingDescriptor
            {
                Key = "BatteryNotifyEnabled",
                Kind = SettingKind.Bool,
                Label = "电池状态提示",
                Hint = "电池接入/拔出/充满时弹出胶囊",
                PageId = SettingsPageIds.System,
                ControlName = "BatteryNotifyToggle",
                DefaultValue = true,
                Get = o => o.BatteryNotifyEnabled,
                Set = (o, v) => o.BatteryNotifyEnabled = SettingDescriptor.ToBool(v, true)
            }));

            list.Add(Typed(new SettingDescriptor
            {
                Key = "BatteryNotifySeconds",
                Kind = SettingKind.Int,
                Label = "电池提示停留时长",
                Hint = "电池胶囊展示指定时长后自动收回细条",
                PageId = SettingsPageIds.System,
                ControlName = "BatteryNotifySecondsSlider",
                ValueTextName = "BatteryNotifySecondsValue",
                Min = 2,
                Max = 10,
                Unit = "秒",
                Format = "0",
                EnabledByKey = "BatteryNotifyEnabled",
                DefaultValue = 4,
                Get = o => o.BatteryNotifySeconds,
                Set = (o, v) => o.BatteryNotifySeconds = (int)Math.Round(SettingDescriptor.ToDouble(v, 4))
            }));

            list.Add(Typed(new SettingDescriptor
            {
                Key = "ClipboardNotifyEnabled",
                Kind = SettingKind.Bool,
                Label = "剪切板提示",
                Hint = "监听剪切板新增文本，保持提示至粘贴；可启用右键快捷粘贴",
                PageId = SettingsPageIds.Behavior,
                ControlName = "ClipboardNotifyToggle",
                DefaultValue = true,
                Get = o => o.ClipboardNotifyEnabled,
                Set = (o, v) => o.ClipboardNotifyEnabled = SettingDescriptor.ToBool(v, true)
            }));

            list.Add(Typed(new SettingDescriptor
            {
                Key = "ClipboardAutoCollapseSeconds",
                Kind = SettingKind.Int,
                Label = "粘贴完成提示时长",
                Hint = "复制提示持续驻留，粘贴完成提示按指定时长自动收回",
                PageId = SettingsPageIds.Behavior,
                ControlName = "ClipboardAutoCollapseSecondsSlider",
                ValueTextName = "ClipboardAutoCollapseSecondsValue",
                Min = 1,
                Max = 5,
                Unit = "秒",
                Format = "0",
                EnabledByKey = "ClipboardNotifyEnabled",
                DefaultValue = 2,
                Get = o => o.ClipboardAutoCollapseSeconds,
                Set = (o, v) => o.ClipboardAutoCollapseSeconds = (int)Math.Round(SettingDescriptor.ToDouble(v, 2))
            }));

            list.Add(ExtraInt("ClipboardHistoryLimit", "剪切板历史保存条数",
                "0 表示不保存；超出后移除最旧记录，修改后立即生效", 10, 0, 100, "条", SettingsPageIds.Behavior,
                controlName: "ClipboardHistoryLimitBox"));
            list.Add(ExtraBool("StartupEnabled", "开机自启动", "登录 Windows 时自动启动 Alpha Island", false,
                SettingsPageIds.Behavior, controlName: "StartupEnabledToggle"));
            list.Add(ExtraBool("StartupBootTimeNotify", "开机耗时提醒", "自启动时显示本次进入桌面的精确耗时", true,
                SettingsPageIds.Behavior, controlName: "StartupBootTimeToggle", enabledBy: "StartupEnabled"));
            list.Add(ExtraBool("ClipboardLinkDetectionEnabled", "识别剪贴板链接", "复制网页链接后点击岛使用默认浏览器打开", true,
                SettingsPageIds.Behavior, controlName: "ClipboardLinkToggle", enabledBy: "ClipboardNotifyEnabled"));

            // ===== A 组：系统状态（设置页 system，随各功能落地再补 UI） =====
            list.Add(ExtraBool("CpuMonitorEnabled", "CPU 占用检测", "每秒更新，自动适配当前 Windows 的任务管理器统计方式", true, SettingsPageIds.System, controlName: "CpuMonitorToggle"));
            foreach (string item in StatusDisplayOptions.All)
                list.Add(ExtraBool(StatusDisplayOptions.SettingKey(item), StatusDisplayOptions.Label(item), "在状态栏中显示", true,
                    SettingsPageIds.System, controlName: StatusDisplayOptions.SettingKey(item) + "Toggle"));
            list.Add(ExtraText("StatusDisplayOrder", "状态栏显示顺序", "按列排列：先上后下，再从左到右", StatusDisplayOptions.DefaultOrder, SettingsPageIds.System));
            list.Add(ExtraBool("NotificationWifiRadio", "Wi-Fi 开关提醒", "开启或关闭 Wi-Fi 时提醒；遵守静默设置", true, SettingsPageIds.Notifications, controlName: "NotificationWifiRadioToggle"));
            list.Add(ExtraBool("NotificationBluetoothRadio", "蓝牙开关提醒", "开启或关闭系统蓝牙时提醒；与设备连接提醒独立", true, SettingsPageIds.Notifications, controlName: "NotificationBluetoothRadioToggle"));
            list.Add(ExtraBool("SystemToastEnabled", "接收 Windows 通知", "使用 Windows 通知读取权限；可在通知中心授权", true, SettingsPageIds.Notifications, controlName: "SystemToastToggle"));
            list.Add(ExtraBool("NotificationQQEnabled", "QQ 新消息提醒", "需 QQ 已登录；读取新消息弹窗、未读数或任务栏/托盘提醒，无法读取正文时提示有新消息", true, SettingsPageIds.Notifications, controlName: "NotificationQQToggle"));
            list.Add(ExtraBool("NotificationWeChatEnabled", "微信新消息提醒", "需微信已登录；读取新消息弹窗、未读数或任务栏/托盘提醒，无法读取正文时提示有新消息", true, SettingsPageIds.Notifications, controlName: "NotificationWeChatToggle"));
            list.Add(ExtraBool("NotificationReplaceSystemPopup", "由小岛接管系统通知", "接管 Windows 横幅，由小岛提示；静默与筛选同步生效，关闭后恢复系统弹窗", true, SettingsPageIds.Notifications, controlName: "NotificationReplaceSystemPopupToggle"));
            list.Add(ExtraBool("SessionEventNotifyEnabled", "锁屏与解锁事件", "", true, SettingsPageIds.Notifications, controlName: "SessionEventsToggle"));
            list.Add(ExtraBool("NotificationGameQuietEnabled", "游戏时自动免打扰", "游戏进入前台时提示一次；切到后台仍保持，全部游戏退出后恢复原来的静默设置", true,
                SettingsPageIds.Notifications, controlName: "NotificationGameQuietToggle"));
            list.Add(ExtraText("NotificationGameProcesses", "补充游戏进程", "未识别的游戏可填程序名（如 game.exe）或完整路径；多项用分号分隔", "",
                SettingsPageIds.Notifications, controlName: "NotificationGameProcessesBox"));
            list.Add(ExtraBool("NotificationQuietMode", "静默模式", "普通消息仅保留在通知中心", false, SettingsPageIds.Notifications, controlName: "NotificationQuietToggle"));
            list.Add(ExtraBool("NotificationQuietAllowPriority", "静默时允许高优先级", "手动静默时工具审批始终提示；游戏免打扰期间暂停自动弹出", true, SettingsPageIds.Notifications, controlName: "NotificationQuietPriorityToggle"));
            list.Add(ExtraBool("NotificationPriorityOnly", "只弹出高优先级通知", "", false, SettingsPageIds.Notifications, controlName: "NotificationPriorityOnlyToggle"));
            list.Add(ExtraInt("NotificationDisplaySeconds", "消息展示时长", "", 8, 3, 30, "秒", SettingsPageIds.Notifications, controlName: "NotificationDurationSlider", valueTextName: "NotificationDurationValue"));
            list.Add(ExtraText("NotificationBlockedApps", "屏蔽应用", "应用名称或 AppID；多项用分号分隔", "", SettingsPageIds.Notifications, controlName: "NotificationBlockedAppsBox"));
            list.Add(ExtraText("NotificationAllowedApps", "仅接收这些应用", "留空接收全部；多项用分号分隔", "", SettingsPageIds.Notifications, controlName: "NotificationAllowedAppsBox"));
            list.Add(ExtraText("NotificationPriorityApps", "高优先级应用", "多项用分号分隔", "", SettingsPageIds.Notifications, controlName: "NotificationPriorityAppsBox"));
            list.Add(ExtraText("NotificationKeywords", "仅接收包含这些关键词的消息", "留空不过滤；多项用分号分隔", "", SettingsPageIds.Notifications, controlName: "NotificationKeywordsBox"));
            list.Add(ExtraBool("NetSpeedEnabled", "网速检测",
                "细条上常驻显示实时上下行速率（自动排除回环与虚拟网卡）", true, SettingsPageIds.System,
                controlName: "NetSpeedEnabledToggle"));
            list.Add(ExtraBool("NetSpeedNotifyEnabled", "网速异常提示",
                "上传或下载速率持续高于阈值时，仅提示异常方向与速率（60 秒内只提示一次）", true, SettingsPageIds.System, "NetSpeedEnabled",
                controlName: "NetSpeedNotifyToggle"));
            list.Add(ExtraDouble("NetSpeedThresholdMbps", "网速提示阈值 (MB/s)",
                "上传或下载速率高于该值时触发提示", 10.0, 1.0, 200.0, "MB/s", "0", SettingsPageIds.System,
                enabledBy: "NetSpeedEnabled",
                controlName: "NetSpeedThresholdSlider", valueTextName: "NetSpeedThresholdValue"));

            list.Add(ExtraBool("MemoryMonitorEnabled", "内存占用检测",
                "在细条上常驻显示物理内存占用百分比", true, SettingsPageIds.System,
                controlName: "MemoryMonitorToggle"));
            list.Add(ExtraInt("MemoryNotifyPercent", "内存告警阈值",
                "占用率持续高于该值时提示一次（5 分钟内只提示一次）", 90, 50, 99, "%", SettingsPageIds.System, "MemoryMonitorEnabled",
                controlName: "MemoryNotifySlider", valueTextName: "MemoryNotifyValue"));
            list.Add(ExtraBool("PowerNotifyEnabled", "功耗提示",
                "电池供电时，放电功率持续高于阈值就提示一次（10 分钟内只提示一次；插着电源不提示）", false, SettingsPageIds.System,
                controlName: "PowerNotifyToggle"));
            list.Add(ExtraDouble("PowerNotifyWatts", "功耗提示阈值",
                "笔记本整机放电功率通常在 8~45 W 之间，按自己的机型调", 25, 1, 200, "W", "0.#", SettingsPageIds.System, "PowerNotifyEnabled",
                controlName: "PowerNotifySlider", valueTextName: "PowerNotifyValue"));
            list.Add(ExtraBool("AirplaneBarEnabled", "细条飞行模式图标",
                "飞行模式开启时，在细条右侧显示一个橙色飞机图标（只读，本程序不会去切换飞行模式）", true, SettingsPageIds.System,
                controlName: "AirplaneBarToggle"));
            list.Add(ExtraBool("AirplaneNotifyEnabled", "飞行模式提示",
                "飞行模式开启或关闭时弹一条提示", false, SettingsPageIds.System,
                controlName: "AirplaneNotifyToggle"));
            list.Add(ExtraBool("PrivacyIndicatorEnabled", "麦克风 / 摄像头占用灯",
                "有应用在用麦克风或摄像头时，细条上亮小圆点并弹一条提示（只读系统记录，免提权免授权）", false, SettingsPageIds.System,
                controlName: "PrivacyIndicatorToggle"));

            list.Add(ExtraBool("TrafficMonitorLinkEnabled", "每日流量（TrafficMonitor）",
                "只读 TrafficMonitor 的 history_traffic.dat：在网速卡片上显示它统计的今日 / 累计流量。"
                + "启用网络数据插件后优先使用桥接的实时速度，未连接时使用系统网卡采样", true, SettingsPageIds.System,
                controlName: "TrafficMonitorLinkToggle"));

            // ===== 下载状态：Steam 客户端日志 + 清单、浏览器下载记录（全部只读） =====
            list.Add(ExtraBool("DownloadMonitorEnabled", "下载状态（浏览器 / Steam）",
                "只读 Steam 清单与客户端日志，区分下载、暂停、排队、校验和文件更新。"
                + "浏览器通过临时文件与下载记录提供进行中和完成提示。"
                + "卡片页「下载状态」列出任务；没有可信的实时进度时不显示进度条、百分比、速度与剩余时间。"
                + "Firefox 与 Epic / 战网等启动器此来源尚未验证支持", true, SettingsPageIds.System,
                controlName: "DownloadMonitorToggle"));

            // ===== B 组：AI 接入与 agent（设置页 ai） =====
            list.Add(ExtraBool("ExternalTranslationEnabled", "快捷键调用外部翻译软件",
                "从屏幕翻译工具或岛卡片发送软件的全局快捷键；译文在外部软件中查看", true, SettingsPageIds.Ai,
                controlName: "ExternalTranslationToggle"));
            list.Add(ExtraText("ExternalTranslationName", "翻译软件名称",
                "网易有道翻译会自动检测并在调用时启动；其他软件请先运行，再填写名称与快捷键", ExternalTranslationOptions.DefaultName,
                SettingsPageIds.Ai, controlName: "ExternalTranslationNameBox"));
            list.Add(ExtraText("ExternalTranslationShortcut", "外部翻译快捷键",
                "须与翻译软件设置的全局快捷键一致；有道默认 Ctrl+Alt+D，可手动填写或录制", ExternalTranslationOptions.DefaultShortcut,
                SettingsPageIds.Ai, controlName: "ExternalTranslationShortcutBox"));
            list.Add(ExtraBool("AiEnabled", "AI 对话",
                "密钥保存且接口连接成功时自动开启；没有密钥或连接失败时关闭，关闭时隐藏岛内入口，历史仍可在插件设置管理", false, SettingsPageIds.Ai));
            list.Add(ExtraText("AiBaseUrl", "接口地址",
                "OpenAI 兼容的 Base URL，例如 https://api.deepseek.com/v1（本地 Ollama 可填 http://127.0.0.1:11434/v1）", "https://api.deepseek.com/v1",
                SettingsPageIds.Ai));
            list.Add(ExtraText("AiApiKey", "API Key",
                "输入完整密钥后点击保存；Windows DPAPI 加密存于 config\\ai\\secrets.dat，设置 JSON 不保存明文（本地模型可留空）", "",
                SettingsPageIds.Ai, secret: true));
            list.Add(ExtraText("AiModel", "模型",
                "连接接口获取可用模型后，从下拉列表选择", "deepseek-chat", SettingsPageIds.Ai));
            list.Add(ExtraText("AiSystemPrompt", "系统提示词",
                "每次对话都会带上的人设/约束", "你是 Alpha Island 的桌面助手，回答尽量简短。", SettingsPageIds.Ai));

            list.Add(ExtraBool("AgentStatusEnabled", "agent 状态",
                "细条上显示 agent 运行状态色点（思考/输出/等待授权/出错）", true, SettingsPageIds.Ai,
                controlName: "AgentStatusToggle"));
            list.Add(ExtraBool("AgentBridgeEnabled", "agent 桥接",
                "开启本机回环端口的 agent 桥接（仅 127.0.0.1，用于接收工具权限审批请求）", false, SettingsPageIds.Ai,
                controlName: "AgentBridgeToggle"));
            list.Add(ExtraInt("AgentBridgePort", "桥接端口",
                "本地 agent 连进来的回环端口", 8765, 1024, 65535, "", SettingsPageIds.Ai, "AgentBridgeEnabled",
                controlName: "AgentBridgePortBox"));
            list.Add(ExtraBool("AgentApprovalNotifyEnabled", "工具权限审批通知",
                "agent 请求工具权限时弹出审批提示（Y 允许 / N 拒绝）", true, SettingsPageIds.Ai, "AgentBridgeEnabled",
                controlName: "AgentApprovalToggle"));

            // ===== C 组②：动效与能耗（设置页 appearance） =====
            // 这两项是「外观」页的第一批注册表项：外观页原来 6 个开关是手写绑定
            // （LoadFrom/ApplyChanges 各一串 if），新项走注册表就不用再往那两处塞分支。
            list.Add(ExtraBool("ReduceMotionEnabled", "减弱动效",
                "形变与卡片翻页直接到位，不做弹簧/位移动画（前庭敏感或远程桌面下更舒服）", false, SettingsPageIds.Appearance,
                controlName: "ReduceMotionToggle"));
            list.Add(ExtraBool("PowerSaveEnabled", "节能模式",
                "收起成细条时把媒体轮询从每秒 10 次降到 1 次，并停掉细条态下的逐帧渲染", false, SettingsPageIds.Appearance,
                controlName: "PowerSaveToggle"));

            // ===== C 组④：3D 封面（设置页 island，和「显示专辑封面」同一张卡） =====
            // 默认关闭。开启后还要过渲染层级门槛（Tier ≥ 2）与一次渲染探针，
            // 不满足就只拒本次会话、不改写这个选项 —— 换台机器/插上独显就能直接用。
            list.Add(ExtraBool("Cover3DEnabled", "3D 封面",
                "卡片封面用 3D 立体卡片渲染，换曲时翻转入场（需要硬件加速 Tier 2 及以上；分层窗口下可能自动退回 2D）",
                false, SettingsPageIds.Island, controlName: "Cover3DToggle"));

            return list;
        }
    }
}
