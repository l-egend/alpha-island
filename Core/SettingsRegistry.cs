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
            list.Add(ExtraBool("ReduceEffectsEnabled", "削减动效", "关闭液态玻璃、液态拉伸、3D 封面与逐帧装饰，切换直接到位并降低后台刷新频率", false, SettingsPageIds.Appearance, controlName: "ReduceEffectsToggle"));
            list.Add(ExtraBool("NotificationMessengerEnabled", "QQ / 微信新消息提醒", "读取已登录应用的新消息、未读数或任务栏提醒；有正文时直接展示", true, SettingsPageIds.Notifications, controlName: "NotificationMessengerToggle"));

            list.Add(ExtraBool("AgentDesktopToolsEnabled", "允许 AI 控制桌面功能", "开放扬声器、媒体、番茄钟与提醒指令；关闭后停止新的工具执行", true, SettingsPageIds.Assistant, controlName: "AgentDesktopToolsEnabledToggle"));
            list.Add(ExtraText("SpeechProvider", "语音识别引擎", "语音指令和声音字幕共用；云端仅在唤醒或开始字幕后上传音频", "mimo", SettingsPageIds.Assistant, controlName: "SpeechProviderBox"));
            list.Add(ExtraText("SpeechBaseUrl", "语音识别接口", "可填写基础地址或完整端点；与对话模型配置独立", SpeechSettings.DefaultUrl, SettingsPageIds.Assistant, controlName: "SpeechBaseUrlBox"));
            list.Add(ExtraText("SpeechModel", "语音识别模型", "从接口获取后选择，或手动输入；请选择支持语音识别的模型", SpeechSettings.DefaultModel, SettingsPageIds.Assistant, controlName: "SpeechModelBox"));
            list.Add(ExtraText("SpeechLanguage", "识别语种", "已知语种时可指定中文或英文，提高识别准确性", "auto", SettingsPageIds.Assistant, controlName: "SpeechLanguageBox"));
            list.Add(ExtraText("SubtitleTargetLanguage", "字幕翻译语言", "下方翻译岛只显示译文，原文保留在工具窗口；相同语种保持原文", "zh-CN", SettingsPageIds.Assistant, controlName: "SubtitleTargetLanguageBox"));
            list.Add(ExtraBool("VoiceWakeEnabled", "API 助手语音唤醒", "唤醒后才使用麦克风，停顿约 1.6 秒自动发送文字；右侧按钮或 Esc 取消，不保存录音", true, SettingsPageIds.Assistant, controlName: "VoiceWakeEnabledToggle"));
            list.Add(ExtraText("VoiceWakeShortcut", "语音唤醒快捷键", "默认 Ctrl+Alt+V；再次按下取消当前录音或本次指令", "Ctrl+Alt+V", SettingsPageIds.Assistant, controlName: "VoiceWakeShortcutBox"));
            list.Add(ExtraBool("VoiceWakeHoldEnabled", "长按右 Ctrl 唤醒", "按住右 Ctrl 0.6 秒切换语音；短按及 Ctrl 组合键保持原有用途", false, SettingsPageIds.Assistant, controlName: "VoiceWakeHoldEnabledToggle"));
            list.Add(ExtraBool("VoiceWakeSoundEnabled", "语音唤醒提示音", "识别器就绪后播放短提示音，听到提示音后即可说话", true, SettingsPageIds.Assistant, controlName: "VoiceWakeSoundEnabledToggle"));
            list.Add(ExtraBool("NaturalRemindersEnabled", "自然语言提醒", "支持一次性、每天、工作日、每周和间隔提醒；本机保存，程序恢复后补记错过事项", true, SettingsPageIds.Assistant, controlName: "NaturalRemindersEnabledToggle"));
            list.Add(ExtraBool("HabitLearningEnabled", "本地习惯学习", "仅记录类别时段与有效时长，保留28天；样本足够后提出建议，采纳后才更改设置，不调用 API", true, SettingsPageIds.Assistant, controlName: "HabitLearningEnabledToggle"));
            list.Add(ExtraBool("SystemHealthEnabled", "系统健康预警", "检测持续高负载、内存压力、进程内存增长与持续高 I/O；趋势不等于故障，不自动结束进程", true, SettingsPageIds.Assistant, controlName: "SystemHealthEnabledToggle"));
            list.Add(ExtraBool("ScreenToolsHotkeysEnabled", "识字与取色快捷键", "按需框选和截图；仅识字、取色无需 API，快捷键冲突可在下方更改", true, SettingsPageIds.Assistant, controlName: "ScreenToolsHotkeysEnabledToggle"));
            list.Add(ExtraText("ScreenOcrShortcut", "识字并复制快捷键", "默认 Ctrl+Alt+O；Esc 取消框选", "Ctrl+Alt+O", SettingsPageIds.Assistant, controlName: "ScreenOcrShortcutBox"));
            list.Add(ExtraText("ScreenColorShortcut", "颜色吸管快捷键", "默认 Ctrl+Alt+C；单击复制 HEX，工具窗口可复制 RGB / HSL", "Ctrl+Alt+C", SettingsPageIds.Assistant, controlName: "ScreenColorShortcutBox"));
            list.Add(ExtraBool("AgentPluginToolsEnabled", "允许其他插件贡献工具", "仅对你信任的插件开启；插件仍在独立工作进程运行，高风险工具暂不执行", false, SettingsPageIds.Assistant, controlName: "AgentPluginToolsEnabledToggle"));
            list.Add(ExtraBool("TimelineEnabled", "记录详细活动时间轴", "在本机记录开机、游戏、音乐、麦克风和摄像头占用；可在生产力卡片清空和导出", true, SettingsPageIds.Assistant, controlName: "TimelineEnabledToggle"));
            list.Add(ExtraInt("TimelineRetentionDays", "时间轴保留天数", "默认 7 天，最多 30 天和 1500 段；与累计时长统计独立", 7, 1, 30, "天", SettingsPageIds.Assistant, controlName: "TimelineRetentionDaysBox"));
            list.Add(ExtraBool("MeetingGuardEnabled", "会议麦克风静音提醒", "会议应用占用设备且默认通信麦克风在系统中静音时提醒；不录音、不自动开麦", true, SettingsPageIds.Assistant, controlName: "MeetingGuardEnabledToggle"));
            list.Add(ExtraBool("AgentProactiveEnabled", "本地助手建议", "规则在本机判断，提醒统一进入通知中心；待处理事项自动置顶，不自动调用 API", true, SettingsPageIds.Assistant, controlName: "AgentProactiveEnabledToggle"));
            foreach (var rule in new[] {
                ("AssistantGameWrapEnabled", "游戏收尾卡", "游戏退出后汇总本次时长、保留未读和任务结束数"),
                ("AssistantQuietDigestEnabled", "免打扰与专注结束摘要", "结束后合并未读、助手和下载，不逐条补弹旧消息"),
                ("AssistantAgentFollowupEnabled", "助手任务跟进", "等待授权过久、报告错误时提醒；明确结束的任务合并汇总"),
                ("AssistantUsageBreakEnabled", "连续使用休息提醒", "仅观察最后输入时间；闲置五分钟、锁屏或休眠后重新计时"),
                ("AssistantMeetingWrapEnabled", "会议结束收尾", "设备占用结束后提供笔记整理，以及可用的会前音量和音乐恢复操作"),
                ("AssistantDownloadStallEnabled", "下载进度停滞提醒", "只检查有可信进度且正在下载的任务；排除暂停、排队和校验"),
                ("AssistantBatteryContextEnabled", "情境电量提醒", "为现有低电量提示补充游戏或会议场景，待处理记录在通知中心置顶"),
                ("AssistantDailyReviewEnabled", "每日活动回顾", "在设定时间后且电脑正在使用时汇总本地时长；AI 整理需点击后发送") })
                list.Add(ExtraBool(rule.Item1, rule.Item2, rule.Item3, true, SettingsPageIds.Assistant, controlName: rule.Item1 + "Toggle"));
            list.Add(ExtraInt("AssistantReminderCooldownMinutes", "提醒弹窗冷却", "分钟；冷却期间仍写入通知中心，不丢失待处理事项", 3, 1, 60, "分钟", SettingsPageIds.Assistant, controlName: "AssistantReminderCooldownMinutesBox"));
            list.Add(ExtraInt("AssistantAgentWaitMinutes", "授权等待提醒阈值", "等待超过此时长再提醒，不自动批准", 3, 1, 60, "分钟", SettingsPageIds.Assistant, controlName: "AssistantAgentWaitMinutesBox"));
            list.Add(ExtraInt("AssistantUsageBreakMinutes", "连续使用提醒阈值", "分钟；长时间无输入不累计为连续操作", 75, 15, 240, "分钟", SettingsPageIds.Assistant, controlName: "AssistantUsageBreakMinutesBox"));
            list.Add(ExtraInt("AssistantDownloadStallSeconds", "下载停滞提醒阈值", "可信下载进度持续未变化的秒数", 90, 30, 900, "秒", SettingsPageIds.Assistant, controlName: "AssistantDownloadStallSecondsBox"));
            list.Add(ExtraInt("AssistantDailyReviewHour", "每日回顾时间：时", "本机时间，0～23；锁屏或免打扰期间延后", 21, 0, 23, "时", SettingsPageIds.Assistant, controlName: "AssistantDailyReviewHourBox"));
            list.Add(ExtraInt("AssistantDailyReviewMinute", "每日回顾时间：分", "本机时间，0～59；每天最多一条", 0, 0, 59, "分", SettingsPageIds.Assistant, controlName: "AssistantDailyReviewMinuteBox"));
            list.Add(ExtraBool("ClipboardSensitiveGuardEnabled", "剪贴板敏感内容保护", "本地检测身份证、银行卡和常见访问密钥；隐藏预览并跳过历史，保留系统剪贴板原文", true, SettingsPageIds.Behavior, controlName: "ClipboardSensitiveGuardEnabledToggle"));
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
            list.Add(ExtraBool("MediaWindowHandoffEnabled", "媒体窗口联动",
                "支持网易云音乐、QQ 音乐、Spotify、酷狗音乐、酷我音乐和 Apple Music；前台收起，最小化后恢复", true,
                SettingsPageIds.Media, controlName: "MediaWindowHandoffToggle"));

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

            list.Add(ExtraBool("BluetoothLowBatteryEnabled", "设备低电量提醒", "蓝牙、2.4G 和手柄电量达到 20% / 10% 时提醒；充电时不提醒，恢复后重新计次", true,
                SettingsPageIds.Notifications, controlName: "BluetoothLowBatteryToggle"));
            list.Add(ExtraBool("DeviceBatteryFullEnabled", "设备充满提醒", "仅在已观察到充电后达到 100% 时提醒；连接已满电设备不提醒", true,
                SettingsPageIds.Notifications, controlName: "DeviceBatteryFullToggle"));
            list.Add(ExtraBool("DeviceBatteryChargingEnabled", "设备充电提醒", "蓝牙和接收器设备开始充电时提醒；持续充电不重复提示", true,
                SettingsPageIds.Notifications, controlName: "DeviceBatteryChargingToggle"));

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
                Key = "MediaPlaybackNotifyEnabled", Kind = SettingKind.Bool,
                Label = "换曲、暂停与继续播放", Hint = "统一控制三种播放状态提示",
                PageId = SettingsPageIds.Media, ControlName = "MediaPlaybackNotifyToggle", DefaultValue = true,
                Get = o => o.UpNextEnabled || o.PauseNotifyEnabled || o.PlayNotifyEnabled,
                Set = (o, v) => o.UpNextEnabled = o.PauseNotifyEnabled = o.PlayNotifyEnabled = SettingDescriptor.ToBool(v, true)
            }));

            list.Add(ExtraInt("ClipboardHistoryLimit", "剪切板历史保存条数",
                "0 表示不保存；超出后移除最旧记录，修改后立即生效", 10, 0, 100, "条", SettingsPageIds.Behavior,
                controlName: "ClipboardHistoryLimitBox"));
            list.Add(ExtraBool("StartupEnabled", "开机自启动", "登录 Windows 时自动启动 Alpha Island", false,
                SettingsPageIds.General, controlName: "StartupEnabledToggle"));
            list.Add(ExtraBool("StartupBootTimeNotify", "开机耗时提醒", "自启动时显示开机耗时；缺少精确记录时提供参考值", true,
                SettingsPageIds.General, controlName: "StartupBootTimeToggle", enabledBy: "StartupEnabled"));
            list.Add(ExtraText("GreetingImagePath", "左侧欢迎图片", "在启动问候中导入图片；保留本机副本，清除后恢复默认兔子图标", "", SettingsPageIds.Notifications));
            list.Add(ExtraBool("GreetingSoundEnabled", "欢迎音效", "欢迎提示出现时播放一次，可自选音效和试听", true,
                SettingsPageIds.Notifications, controlName: "GreetingSoundToggle"));
            list.Add(ExtraText("GreetingSoundPath", "启动音效文件", "支持 WAV、MP3 等系统可解码音效；最长 10 秒，留空使用内置轻柔启动音", "", SettingsPageIds.Notifications));
            list.Add(ExtraInt("GreetingSoundVolume", "音效音量", "只调整欢迎音效，不改变系统音量", 35, 0, 100, "%",
                SettingsPageIds.Notifications, controlName: "GreetingSoundVolumeBox", enabledBy: "GreetingSoundEnabled"));
            list.Add(ExtraBool("ClipboardLinkDetectionEnabled", "识别剪贴板链接", "复制网页链接后点击岛使用默认浏览器打开", true,
                SettingsPageIds.Behavior, controlName: "ClipboardLinkToggle", enabledBy: "ClipboardNotifyEnabled"));

            // ===== 系统状态（设置页 system，随各功能落地再补 UI） =====
            list.Add(ExtraBool("CpuMonitorEnabled", "CPU 占用检测", "每秒更新，自动适配当前 Windows 的任务管理器统计方式", true, SettingsPageIds.System, controlName: "CpuMonitorToggle"));
            foreach (string item in StatusDisplayOptions.All)
                list.Add(ExtraBool(StatusDisplayOptions.SettingKey(item), StatusDisplayOptions.Label(item), "在状态栏中显示", true,
                    SettingsPageIds.System, controlName: StatusDisplayOptions.SettingKey(item) + "Toggle"));
            list.Add(ExtraText("StatusDisplayOrder", "状态栏显示顺序", "按列排列：先上后下，再从左到右", StatusDisplayOptions.DefaultOrder, SettingsPageIds.System));
            list.Add(ExtraBool("NotificationWifiRadio", "Wi-Fi 开关提醒", "开启或关闭 Wi-Fi 时提醒；遵守静默设置", true, SettingsPageIds.Notifications, controlName: "NotificationWifiRadioToggle"));
            list.Add(ExtraBool("NotificationBluetoothRadio", "蓝牙开关提醒", "开启或关闭系统蓝牙时提醒；与设备连接提醒独立", true, SettingsPageIds.Notifications, controlName: "NotificationBluetoothRadioToggle"));
            list.Add(ExtraBool("SystemToastEnabled", "接收 Windows 通知", "使用 Windows 通知读取权限；可在通知中心授权", true, SettingsPageIds.Notifications, controlName: "SystemToastToggle"));
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
            list.Add(ExtraDouble("NetSpeedThresholdMbps", "网速提示阈值",
                "上传或下载速率高于该值时触发提示", 10.0, 0.01, 10000.0, "MB/s", "0.###", SettingsPageIds.System,
                enabledBy: "NetSpeedEnabled",
                controlName: "NetSpeedThresholdBox"));

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
                "笔记本整机放电功率通常在 8~45 W 之间，按自己的机型调", 25, 1, 200, "W", "0.###", SettingsPageIds.System,
                controlName: "PowerNotifyWattsBox"));
            list.Add(ExtraBool("PrivacyIndicatorEnabled", "麦克风 / 摄像头占用灯",
                "有应用在用麦克风或摄像头时，细条上亮小圆点并弹一条提示（只读系统记录，免提权免授权）", false, SettingsPageIds.System,
                controlName: "PrivacyIndicatorToggle"));

            list.Add(ExtraBool("DailyTrafficEnabled", "每日流量", "在本机保存每日上传和下载总量，重启后继续统计", true, SettingsPageIds.System, controlName: "DailyTrafficToggle"));

            // ===== 下载状态：Steam 客户端日志 + 清单、浏览器下载记录（全部只读） =====
            list.Add(ExtraBool("DownloadMonitorEnabled", "下载状态（浏览器 / Steam）",
                "只读 Steam 清单与客户端日志，区分下载、暂停、排队、校验和文件更新。"
                + "浏览器通过临时文件与下载记录提供进行中和完成提示。"
                + "卡片页「下载状态」列出任务；没有可信的实时进度时不显示进度条、百分比、速度与剩余时间。"
                + "Firefox 与 Epic / 战网等启动器此来源尚未验证支持", true, SettingsPageIds.System,
                controlName: "DownloadMonitorToggle"));

            // ===== AI 接入与 agent（设置页 ai） =====
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
            // Keep the legacy key/default for existing profiles; the API plugin treats it as a personality fallback.
            list.Add(ExtraText("AiSystemPrompt", "人格提示词",
                "角色与语气在会话首次发送；支持导入 TXT，修改后新建或清空对话生效；系统规则内置", "你是 Alpha Island 的桌面助手，回答尽量简短。", SettingsPageIds.Ai));

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

            // ===== 动效与能耗（设置页 appearance） =====
            // 这两项是「外观」页的第一批注册表项：外观页原来 6 个开关是手写绑定
            // （LoadFrom/ApplyChanges 各一串 if），新项走注册表就不用再往那两处塞分支。

            // ===== 3D 封面（设置页 island，和「显示专辑封面」同一张卡） =====
            // 默认关闭。开启后还要过渲染层级门槛（Tier ≥ 2）与一次渲染探针，
            // 不满足就只拒本次会话、不改写这个选项 —— 换台机器/插上独显就能直接用。

            return list;
        }
    }
}
