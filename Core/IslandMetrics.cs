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
        public const double PillSpectrumWidth = 28;
        public const double PillSpectrumHeight = 22;

        // ================= 中间托盘：文件中转站 =================
        public const double TrayWidth = 380;
        public const double TrayHeight = 88;
        public const double TrayTop = 14;
        public static readonly CornerRadius TrayCorner = new(22);

        // ================= 二级展开：完整卡片 =================
        public const double CardWidth = 430;
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

        /// <summary>
        /// 收回全程只跑一条主弹簧（旧的 260+220+220 三段式已废弃）。
        /// 标称 620ms 对应"两条通道都进 ±1.2%"的实际观感 ≈ 750ms，和三段式的 700ms 基本重合，
        /// 但速度不再在接缝处归零。
        /// </summary>
        public static readonly Duration CollapseDuration = new(TimeSpan.FromMilliseconds(620));

        /// <summary>正圆路径点在收回路径上的相对位置（0..1）。0.38 ≈ 620ms 里的 235ms，对齐旧的 CollapseStage1。</summary>
        public const double CollapseViaAt = 0.38;

        /// <summary>
        /// 跟随通道（高度）相对主通道的时间倍数。&gt;1 = 滞后，细条的"肚子"拖在轮廓后面才像胶体。
        /// 1.25 时高度比宽度晚约 40ms 到位。
        /// </summary>
        public const double CollapseFollowLag = 1.25;

        /// <summary>
        /// 内容层显露窗口：形变进度 t 从 Start 到 End 的区间里，目标层从 0 露到 1、源层从 1 退到 0。
        /// 这不是独立的淡入淡出时间轴 —— 它直接由主弹簧的进度驱动，
        /// 也就是"材质展开到哪儿、内容就露到哪儿"，没有自己的起止时间。
        /// </summary>
        public const double ContentRevealStart = 0.04;
        public const double ContentRevealEnd = 0.34;

        /// <summary>收回途中"封面球"的显形窗口（同样是主弹簧进度，不是独立时间轴）。</summary>
        public const double OrbRevealStart = 0.32;
        public const double OrbRevealEnd = 0.46;
        public const double OrbFadeStart = 0.95;
        public const double OrbFadeSpan = 0.42;
    }
}
