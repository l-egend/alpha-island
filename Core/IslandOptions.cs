using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace alpha_island.Core
{
    /// <summary>
    /// 用户可调设置。以前是一堆散在 MainWindow 里的私有字段 + SettingsWindow 里逐个同步，
    /// 加一个开关要改四处；现在集中成一个可序列化对象。
    /// 所有字段都必须有合理的默认值 —— 配置文件缺失/损坏时直接吃默认值。
    /// </summary>
    public sealed class IslandOptions : alpha_island.Conversations.IChatSettings
    {
        public string GetText(string key, string fallback) => SettingsRegistry.GetText(this, key, fallback);
        public void SetValue(string key, object value) => SettingsRegistry.SetExtra(this, key, value);

        // ---------- 外观 ----------
        /// <summary>液态玻璃通透度 0.05~0.98。</summary>
        public double GlassOpacity { get; set; } = 0.08;

        // ---------- 布局与交互 ----------

        // ---------- 音频可视化 ----------
        public bool VisualizerEnabled { get; set; } = true;
        /// <summary>卡片底部那条连续波浪背景层。</summary>
        public bool ShowWaveLayer { get; set; } = true;
        /// <summary>频谱灵敏度 0.3~3.0。</summary>
        public double Sensitivity { get; set; } = 1.0;

        // ---------- 歌词 ----------
        public bool LyricsEnabled { get; set; } = true;
        public bool ShowTranslation { get; set; } = true;
        /// <summary>歌词时间微调偏移量（毫秒），用于补偿蓝牙耳机延迟或系统音频缓冲，-2000~+2000。</summary>
        public int LyricsOffsetMs { get; set; } = 0;

        // ---------- 行为 ----------

        /// <summary>换曲时是否弹出一级胶囊做"接下来"提示。</summary>
        public bool UpNextEnabled { get; set; } = true;
        /// <summary>换曲提示停留秒数，2~30。</summary>
        public int UpNextSeconds { get; set; } = 5;
        /// <summary>音乐暂停时是否弹出一级胶囊提示。</summary>
        public bool PauseNotifyEnabled { get; set; } = true;
        /// <summary>音乐恢复播放时是否弹出一级胶囊提示。</summary>
        public bool PlayNotifyEnabled { get; set; } = true;

        // ---------- 音量调节 ----------
        /// <summary>调节系统音量时是否弹出一级胶囊音量条。</summary>
        public bool VolumeNotifyEnabled { get; set; } = true;
        /// <summary>音量调节条停留秒数，1~5。</summary>
        public int VolumeNotifySeconds { get; set; } = 2;

        // ---------- 启动问候 ----------
        /// <summary>启动时是否弹出一级胶囊展示欢迎问候语。</summary>
        public bool GreetingEnabled { get; set; } = true;
        private bool _retiredGreetingText;
        [System.Text.Json.Serialization.JsonInclude]
        [System.Text.Json.Serialization.JsonPropertyName("GreetingText")]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        internal string? LegacyGreetingText { get => null; set => _retiredGreetingText = true; }
        /// <summary>问候语停留秒数，1.5~10（下限即程序启动到服务就绪的平均耗时）。</summary>
        public double GreetingSeconds { get; set; } = 4;

        // ---------- 蓝牙 ----------
        /// <summary>蓝牙设备连接时是否弹出通知。</summary>
        public bool BluetoothNotifyEnabled { get; set; } = true;
        /// <summary>蓝牙通知停留秒数，2~30。</summary>
        public int BluetoothNotifySeconds { get; set; } = 5;

        // ---------- 电池 ----------
        /// <summary>电池充电状态或低电量变化时是否弹出通知。</summary>
        public bool BatteryNotifyEnabled { get; set; } = true;
        /// <summary>电池通知停留秒数，2~10。</summary>
        public int BatteryNotifySeconds { get; set; } = 4;

        // ---------- 剪切板与热键 ----------
        /// <summary>复制内容时是否弹出剪切板通知。</summary>
        public bool ClipboardNotifyEnabled { get; set; } = true;
        /// <summary>已复制驻留弹窗下，鼠标右键是否替换为自动粘贴。</summary>
        public bool ClipboardRightClickPaste { get; set; } = true;

        // ---------- 文件中转站 ----------
        /// <summary>是否启用拖拽文件中转站功能。</summary>
        public bool FileTransferEnabled { get; set; } = true;

        /// <summary>
        /// 注册表声明但还没有强类型属性的设置项（新功能用：AI 接入、网速阈值、agent 桥接……）。
        /// 读写一律走 <see cref="SettingsRegistry"/>（带类型校验与范围夹取），
        /// 不新增属性也就不会牵动 <see cref="CopyFrom"/> 与序列化。缺失的键会在
        /// <see cref="Normalized"/> 里补成默认值，所以 options.json 始终自带全部可调项。
        /// </summary>
        public Dictionary<string, JsonElement> Extras { get; set; } = new();

        /// <summary>把其它实例的值全部拷贝进来（用于"恢复默认"后再回填）。</summary>
        public void CopyFrom(IslandOptions other)
        {
            IsFreshInstallation = false;
            GlassOpacity = other.GlassOpacity;
            VisualizerEnabled = other.VisualizerEnabled;
            ShowWaveLayer = other.ShowWaveLayer;
            Sensitivity = other.Sensitivity;
            LyricsEnabled = other.LyricsEnabled;
            ShowTranslation = other.ShowTranslation;
            LyricsOffsetMs = other.LyricsOffsetMs;
            UpNextEnabled = other.UpNextEnabled;
            UpNextSeconds = other.UpNextSeconds;
            PauseNotifyEnabled = other.PauseNotifyEnabled;
            PlayNotifyEnabled = other.PlayNotifyEnabled;
            VolumeNotifyEnabled = other.VolumeNotifyEnabled;
            VolumeNotifySeconds = other.VolumeNotifySeconds;
            GreetingEnabled = other.GreetingEnabled;
            GreetingSeconds = other.GreetingSeconds;
            BluetoothNotifyEnabled = other.BluetoothNotifyEnabled;
            BluetoothNotifySeconds = other.BluetoothNotifySeconds;
            BatteryNotifyEnabled = other.BatteryNotifyEnabled;
            BatteryNotifySeconds = other.BatteryNotifySeconds;
            ClipboardNotifyEnabled = other.ClipboardNotifyEnabled;
            ClipboardRightClickPaste = other.ClipboardRightClickPaste;
            FileTransferEnabled = other.FileTransferEnabled;

            // Extras 是注册表驱动态设置项的容器，必须跟着拷贝，
            // 否则设置窗口那一份副本回填时会丢掉这些键（AI/网速等整批消失）。
            Extras = other.Extras == null
                ? new Dictionary<string, JsonElement>()
                : new Dictionary<string, JsonElement>(other.Extras);
        }

        // ================= 持久化 =================

        internal static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        public static IslandOptions Load()
        {
            AppPaths.EnsureConfiguration();
            string legacy = Path.Combine(AppPaths.Root, "options.json");
            return Load(File.Exists(AppPaths.OptionsFile) || !File.Exists(legacy) ? AppPaths.OptionsFile : legacy);
        }

        private bool _loadFailed;
        internal bool IsFreshInstallation { get; private set; }

        internal static IslandOptions Load(string path)
        {
            foreach (string candidate in new[] { path, path + ".bak" })
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    var loaded = AtomicJsonFile.Read<IslandOptions>(candidate, JsonOptions);
                    bool retiredPreference = loaded != null && (SettingsMigration.Required(loaded) || loaded._retiredGreetingText)
                        || loaded?.Extras?.ContainsKey("HardwarePowerElevatedReader") == true
                        || loaded?.Extras?.ContainsKey("MemoryTrimEnabled") == true;
                    loaded = loaded?.Normalized();
                    if (loaded == null) continue;
                    if (candidate != path || retiredPreference)
                    {
                        if (candidate != path) IslandLog.Warn("Options", "已从设置备份恢复");
                        try { AtomicJsonFile.Write(path, JsonSerializer.Serialize(loaded, JsonOptions), keepBackup: candidate == path); }
                        catch (Exception ex) { IslandLog.Warn("Options", "设置已读取，配置迁移或恢复写入失败: " + ex.Message); }
                    }
                    return loaded;
                }
                catch (Exception ex)
                {
                    IslandLog.Warn("Options", "读取失败: " + ex.Message);
                }
            }
            // 无有效备份时，退出不能把临时默认值覆盖到用户的损坏文件上。
            bool hasSettings = File.Exists(path) || File.Exists(path + ".bak");
            var defaults = new IslandOptions { _loadFailed = hasSettings, IsFreshInstallation = !hasSettings };
            if (!hasSettings)
            {
                // Only a genuinely new profile receives automatic integration defaults.
                // Persist them even without external apps so later installation works after a restart.
                foreach (string key in new[] { "AgentBridgeEnabled" })
                    defaults.Extras[key] = JsonSerializer.SerializeToElement(true);
            }
            return defaults;
        }

        public void Save(bool allowRecovery = false) => TrySave(allowRecovery);
        internal void Save(string path, bool allowRecovery = false) => TrySave(path, allowRecovery);
        public bool TrySave(bool allowRecovery = false) => TrySave(AppPaths.OptionsFile, allowRecovery);
        internal bool TrySave(string path, bool allowRecovery = false)
        {
            try
            {
                if (_loadFailed && !allowRecovery)
                {
                    IslandLog.Warn("Options", "原设置读取失败，保留文件；修改设置或恢复默认后再保存");
                    return false;
                }
                SettingsRegistry.SetExtra(this, "AiApiKey", string.Empty);
                AtomicJsonFile.Write(path, JsonSerializer.Serialize(this, JsonOptions));
                _loadFailed = false; IsFreshInstallation = false;
                return true;
            }
            catch (Exception ex)
            {
                IslandLog.Warn("Options", "保存失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 把超范围的值夹回合法区间（options.json 可以被手改坏，也可以是老版本留下的值）。
        /// 每个数值字段都要夹 —— 设置页的滑杆范围必须和这里的区间一致，
        /// 区间同时写在字段的 XML 注释上，改动时三处一起改。
        /// </summary>
        public IslandOptions Normalized()
        {
            GlassOpacity = ClampDouble(GlassOpacity, 0.08, 0.05, 0.98);
            Sensitivity = ClampDouble(Sensitivity, 1.0, 0.3, 3.0);
            GreetingSeconds = ClampDouble(GreetingSeconds, 4.0, 1.5, 10.0);
            LyricsOffsetMs = Math.Clamp(LyricsOffsetMs, -2000, 2000);
            UpNextSeconds = Math.Clamp(UpNextSeconds, 2, 30);
            VolumeNotifySeconds = Math.Clamp(VolumeNotifySeconds, 1, 5);
            BluetoothNotifySeconds = Math.Clamp(BluetoothNotifySeconds, 2, 30);
            BatteryNotifySeconds = Math.Clamp(BatteryNotifySeconds, 2, 10);

            // 注册表里的设置项（含 Extras 里新功能的项）统一夹取 + 补齐缺的键。
            // 上面那些逐项夹取保持原样：它们是历史行为，改动风险大于收益。
            Extras ??= new Dictionary<string, JsonElement>();
            // Retired privileged reader preference must not survive old configuration imports.
            Extras.Remove("HardwarePowerElevatedReader");
            Extras.Remove("MemoryTrimEnabled");
            SettingsMigration.Apply(this);
            SettingsRegistry.Normalize(this);
            SettingsRegistry.SetExtra(this, "AiApiKey", string.Empty);

            return this;
        }

        /// <summary>NaN / ±∞ 一律退回默认值，其余夹进区间。</summary>
        private static double ClampDouble(double value, double fallback, double min, double max)
            => double.IsNaN(value) ? fallback : Math.Clamp(value, min, max);

    }
}
