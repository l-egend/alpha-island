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
        /// <summary>液态玻璃通透度 0.2~0.98。</summary>
        public double GlassOpacity { get; set; } = 0.85;
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

        // ---------- 行为 ----------
        /// <summary>关闭时：仅在换曲瞬间弹一下胶囊，之后自动收起。</summary>
        public bool KeepCompactWhilePlaying { get; set; }

        /// <summary>换曲时是否弹出一级胶囊做"接下来"提示。</summary>
        public bool UpNextEnabled { get; set; } = true;
        /// <summary>换曲提示停留秒数，2~30。</summary>
        public int UpNextSeconds { get; set; } = 5;

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
            KeepCompactWhilePlaying = other.KeepCompactWhilePlaying;
            UpNextEnabled = other.UpNextEnabled;
            UpNextSeconds = other.UpNextSeconds;
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
                File.WriteAllText(AppPaths.OptionsFile, JsonSerializer.Serialize(this, JsonOptions));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Options] 保存失败: " + ex.Message);
            }
        }

        /// <summary>把超范围的值夹回合法区间（配置文件可以被手改坏）。</summary>
        public IslandOptions Normalized()
        {
            GlassOpacity = Math.Clamp(GlassOpacity, 0.2, 0.98);
            Sensitivity = Math.Clamp(Sensitivity, 0.3, 3.0);
            UpNextSeconds = Math.Clamp(UpNextSeconds, 2, 30);
            return this;
        }
    }
}
