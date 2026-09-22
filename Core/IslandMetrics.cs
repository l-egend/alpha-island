using System.Windows;

namespace alpha_island.Core
{
    /// <summary>
    /// 所有形态尺寸与动画时长集中在这里。
    /// 原先散落在 MainWindow 的 const 区，加一个形态就要满文件找数字，改一处漏一处。
    /// 单位一律是 WPF 设备无关像素（DIP）。
    /// </summary>
    public static class IslandMetrics
    {
        // ================= 待机细条 =================
        public const double BarWidth = 130;
        public const double BarHeight = 10;
        public const double BarTop = 0;
        public static readonly CornerRadius BarCorner = new(0, 0, 10, 10);

        // ================= 一级展开：长椭圆 =================
        // 用户描述："大体表现为一个长长的椭圆" —— 所以圆角取高度的一半，做成完整胶囊。
        public const double PillWidth = 344;
        public const double PillHeight = 48;
        public const double PillTop = 12;
        public static readonly CornerRadius PillCorner = new(PillHeight / 2);

        /// <summary>一级展开里左侧圆形封面的边长。</summary>
        public const double PillCoverSize = 32;
        /// <summary>一级展开里右侧可视化区的宽高。</summary>
        public const double PillSpectrumWidth = 74;
        public const double PillSpectrumHeight = 22;

        // ================= 二级展开：完整卡片 =================
        public const double CardWidth = 420;
        /// <summary>无歌词时的卡片高度。</summary>
        public const double CardHeight = 138;
        /// <summary>有歌词时的卡片高度（多出歌词区 68 + 上下间距 8）。</summary>
        public const double CardHeightLyrics = 214;
        public const double CardTop = 18;
        public const double CardCornerRadius = 28;

        /// <summary>歌词区各行的固定高度：上一行 / 当前行 / 翻译 / 下一行。</summary>
        public const double LyricPrevHeight = 15;
        public const double LyricCurrentHeight = 24;
        public const double LyricTranslationHeight = 14;
        public const double LyricNextHeight = 15;
        public const double LyricsBlockHeight =
            LyricPrevHeight + LyricCurrentHeight + LyricTranslationHeight + LyricNextHeight;

        /// <summary>卡片底部那条连续波浪层的高度（作为背景层压在歌词下面）。</summary>
        public const double WaveLayerHeight = 72;

        // ================= 收回动画中间态 =================
        /// <summary>缩回过程中间那个正圆的直径。</summary>
        public const double CircleSize = 46;

        // ================= 时长 =================
        // 齿轮弹簧的"标称时长"含义：两条通道都进 ±1.2% 所需的时间约为标称值的 1.09 倍
        // （实测 460ms → 498ms、280ms → 304ms）。下面的数字按"实际观感时长"倒推。
        /// <summary>展开：凝胶过冲回弹的总时长。</summary>
        public static readonly Duration ExpandDuration = new(TimeSpan.FromMilliseconds(420));
        /// <summary>一级 → 二级的过冲时长（比从细条展开稍短）。</summary>
        public static readonly Duration GrowDuration = new(TimeSpan.FromMilliseconds(340));

        public static readonly Duration CollapseStage1 = new(TimeSpan.FromMilliseconds(260)); // 卡片 → 圆球
        public static readonly Duration CollapseStage2 = new(TimeSpan.FromMilliseconds(220)); // 圆球 → 吸顶
        public static readonly Duration CollapseStage3 = new(TimeSpan.FromMilliseconds(220)); // 吸顶 → 细条

        /// <summary>
        /// 内容层退场时长。只用来避开"硬切"那一下的跳变 —— 真正的过渡由材质形变完成：
        /// IslandBody 开着 ClipToBounds，内容是被展开/收拢的轮廓裁出来的。
        /// </summary>
        public static readonly Duration ContentFadeOut = new(TimeSpan.FromMilliseconds(70));
    }
}
