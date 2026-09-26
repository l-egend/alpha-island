using System;
using System.Diagnostics;
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
    public sealed class IslandOptions
    {
        // ---------- 外观 ----------
        /// <summary>液态玻璃通透度 0.05~0.98。</summary>
        public double GlassOpacity { get; set; } = 0.08;
        /// <summary>是否尝试真实背景模糊（做不到时会自动降级，见 Interop/WindowEffects）。</summary>
        public bool LiquidGlassBackdrop { get; set; } = true;
        /// <summary>是否启用凝胶形变动效（关闭则退回普通缓动）。</summary>
        public bool GelAnimation { get; set; } = true;

        // ---------- 布局与交互 ----------
        /// <summary>true = 悬停只到一级长椭圆、点击才进二级卡片；false = 悬停直接进二级。</summary>
        public bool TwoStepExpand { get; set; } = true;
        /// <summary>一级展开里是否显示左侧封面。</summary>
        public bool ShowCover { get; set; } = true;

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
        /// <summary>关闭时：仅在换曲瞬间弹一下胶囊，之后自动收起。</summary>
        public bool KeepCompactWhilePlaying { get; set; }

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
        /// <summary>欢迎问候语文本，默认“欢迎回来”。</summary>
        public string GreetingText { get; set; } = "欢迎回来";
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
        /// <summary>充放电时是否播放光波纹特效。</summary>
        public bool BatteryRippleEnabled { get; set; } = true;

        // ---------- 剪切板与热键 ----------
        /// <summary>复制内容时是否弹出剪切板通知。</summary>
        public bool ClipboardNotifyEnabled { get; set; } = true;
        /// <summary>已复制驻留弹窗下，鼠标右键是否替换为自动粘贴。</summary>
        public bool ClipboardRightClickPaste { get; set; } = true;
        /// <summary>粘贴后提示停留秒数，1~5。</summary>
        public int ClipboardAutoCollapseSeconds { get; set; } = 2;

        // ---------- 文件中转站 ----------
        /// <summary>是否启用拖拽文件中转站功能。</summary>
        public bool FileTransferEnabled { get; set; } = true;

        public IslandOptions Clone() => (IslandOptions)MemberwiseClone();

        /// <summary>把其它实例的值全部拷贝进来（用于"恢复默认"后再回填）。</summary>
        public void CopyFrom(IslandOptions other)
        {
            GlassOpacity = other.GlassOpacity;
            LiquidGlassBackdrop = other.LiquidGlassBackdrop;
            GelAnimation = other.GelAnimation;
            TwoStepExpand = other.TwoStepExpand;
            ShowCover = other.ShowCover;
            VisualizerEnabled = other.VisualizerEnabled;
            ShowWaveLayer = other.ShowWaveLayer;
            Sensitivity = other.Sensitivity;
            LyricsEnabled = other.LyricsEnabled;
            ShowTranslation = other.ShowTranslation;
            LyricsOffsetMs = other.LyricsOffsetMs;
            KeepCompactWhilePlaying = other.KeepCompactWhilePlaying;
            UpNextEnabled = other.UpNextEnabled;
            UpNextSeconds = other.UpNextSeconds;
            PauseNotifyEnabled = other.PauseNotifyEnabled;
            PlayNotifyEnabled = other.PlayNotifyEnabled;
            VolumeNotifyEnabled = other.VolumeNotifyEnabled;
            VolumeNotifySeconds = other.VolumeNotifySeconds;
            GreetingEnabled = other.GreetingEnabled;
            GreetingText = other.GreetingText;
            GreetingSeconds = other.GreetingSeconds;
            BluetoothNotifyEnabled = other.BluetoothNotifyEnabled;
            BluetoothNotifySeconds = other.BluetoothNotifySeconds;
            BatteryNotifyEnabled = other.BatteryNotifyEnabled;
            BatteryNotifySeconds = other.BatteryNotifySeconds;
            BatteryRippleEnabled = other.BatteryRippleEnabled;
            ClipboardNotifyEnabled = other.ClipboardNotifyEnabled;
            ClipboardRightClickPaste = other.ClipboardRightClickPaste;
            ClipboardAutoCollapseSeconds = other.ClipboardAutoCollapseSeconds;
            FileTransferEnabled = other.FileTransferEnabled;
        }

        // ================= 持久化 =================

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        public static IslandOptions Load()
        {
            try
            {
                if (File.Exists(AppPaths.OptionsFile))
                {
                    string json = File.ReadAllText(AppPaths.OptionsFile);
                    var loaded = JsonSerializer.Deserialize<IslandOptions>(json, JsonOptions);
                    if (loaded != null) return loaded.Normalized();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Options] 读取失败，使用默认值: " + ex.Message);
            }
            return new IslandOptions();
        }

        public void Save()
        {
            try
            {
                AppPaths.EnsureRoot();
                File.WriteAllText(AppPaths.OptionsFile, JsonSerializer.Serialize(this, JsonOptions), System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Options] 保存失败: " + ex.Message);
            }
        }

        /// <summary>把超范围的值夹回合法区间（配置文件可以被手改坏）。</summary>
        public IslandOptions Normalized()
        {
            GlassOpacity = Math.Clamp(GlassOpacity, 0.05, 0.98);
            Sensitivity = Math.Clamp(Sensitivity, 0.3, 3.0);
            UpNextSeconds = Math.Clamp(UpNextSeconds, 2, 30);
            BluetoothNotifySeconds = Math.Clamp(BluetoothNotifySeconds, 2, 30);
            GreetingSeconds = Math.Clamp(GreetingSeconds, 1.5, 10.0);
            if (string.IsNullOrWhiteSpace(GreetingText) || GreetingText.Contains("娆") || GreetingText.Contains("鍥") || GreetingText.Contains(""))
            {
                GreetingText = "欢迎回来";
            }
            return this;
        }
    }
}
