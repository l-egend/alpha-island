using System;
using System.Windows.Media;

namespace alpha_island.Animation
{
    /// <summary>
    /// 动效策略：把「减弱动效」「节能模式」两个开关和渲染层级探测收在一处，
    /// 让动画器与各图层用同一套判定，而不是各自去读设置。
    ///
    /// 三件事各自的理由：
    ///   * <b>减弱动效</b>：弹簧形变（<see cref="IslandAnimator"/>）和卡片翻页 260ms 位移
    ///     对前庭敏感的人是负担，开关打开后几何直接到位（<see cref="InstantMorph"/>），
    ///     内容层靠 <c>ProgressHook(t=1)</c> 拿到终态，不会卡在半透明；
    ///   * <b>节能模式</b>：细条态本该"几乎不干活"。逐帧回调（频谱/波形/卡拉OK/玻璃窗）
    ///     早已按形态惰性挂载，但那是**隐含**的 —— 一旦以后有人加了新的循环就漏了，
    ///     所以用 <see cref="AllowsFrameLoop"/> 把「收起态不挂帧循环」变成显式约束；
    ///   * <b>渲染层级</b>：<see cref="RenderCapability.Tier"/> 高位为 0 表示纯软件渲染
    ///     （远程桌面、虚拟机、显卡驱动被禁用）。此时弹簧逐帧积分只会把 CPU 烧掉、
    ///     帧率还是个位数，不如直接瞬变。
    ///
    /// 线程约定：只在 UI 线程读写（<see cref="Apply"/> 由设置变更驱动）。
    /// </summary>
    public static class MotionPolicy
    {
        /// <summary>「减弱动效」：形变与翻页直接到位。</summary>
        public static bool ReduceMotion { get; private set; }

        /// <summary>「节能模式」：收起态压掉逐帧循环与高频轮询。</summary>
        public static bool PowerSave { get; private set; }

        /// <summary>渲染层级：0 = 软件渲染，1 = 部分硬件加速，2 = 全部硬件加速。</summary>
        public static int RenderTier { get; private set; } = 2;

        /// <summary>层级只能探一次（见 <see cref="ProbeRenderTier"/>）。</summary>
        private static bool _probed;

        /// <summary>当前是否处于纯软件渲染（Tier 0）。</summary>
        public static bool SoftwareRendering => RenderTier <= 0;

        /// <summary>是否需要把形变降级成瞬变（开关打开，或软件渲染下积分没意义）。</summary>
        public static bool InstantMorph => ReduceMotion || SoftwareRendering;

        /// <summary>
        /// 是否允许挂逐帧循环。<paramref name="collapsed"/> 为真表示岛体处于收起的细条态 ——
        /// 节能模式下此时不挂（细条态没有任何逐帧动画）。
        /// </summary>
        public static bool AllowsFrameLoop(bool collapsed) => !(PowerSave && collapsed);

        /// <summary>策略变化（用于让窗口立刻重算挂载与轮询节奏）。</summary>
        public static event Action? Changed;

        /// <summary>写入两个开关。值没变时直接返回，不触发 <see cref="Changed"/>。</summary>
        public static void Apply(bool reduceMotion, bool powerSave)
        {
            if (ReduceMotion == reduceMotion && PowerSave == powerSave) return;
            ReduceMotion = reduceMotion;
            PowerSave = powerSave;
            NotifyChanged();
        }

        /// <summary>
        /// 探测渲染层级。只探一次（层级在进程生命周期内基本不变），
        /// 必须在有 Dispatcher 的线程上调用。
        ///
        /// "只探一次"是硬要求：帧预算看门狗会把层级改写成 0（见 <see cref="MarkSoftwareRendering"/>），
        /// 若每次应用设置都重新探测，那个运行时判定会被系统层级覆盖掉，形变又开始逐帧积分 —— 越跑越卡。
        /// </summary>
        public static void ProbeRenderTier()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                // 高位字才是层级；低位是「渲染能力」的其它标志位，直接比较会永远为真。
                RenderTier = RenderCapability.Tier >> 16;
            }
            catch (Exception)
            {
                // 极少数环境下取层级会抛（无显示器/会话未初始化）：按硬件渲染处理，
                // 大不了退回普通路径，不影响功能。
                RenderTier = 2;
            }
            SyncBatteryAnimations();
        }

        /// <summary>
        /// 运行时确认整机只能软件渲染（帧预算看门狗连续掉帧时调用）。
        /// 一旦标记，<see cref="InstantMorph"/> 之后恒为真，形变不再尝试逐帧积分。
        /// </summary>
        public static void MarkSoftwareRendering()
        {
            if (RenderTier <= 0) return;
            RenderTier = 0;
            NotifyChanged();
        }

        /// <summary>
        /// 调试钩子专用（<c>ALPHA_ISLAND_FAKE_RENDER_TIER=0|1|2</c>）：在首次探测之前把层级写死，
        /// 用来在硬件机器上验证「Tier 门槛」那条拒绝分支（3D 封面、形变降级）。
        /// 写死的同时把 <c>_probed</c> 置真，避免紧随其后的 <see cref="ProbeRenderTier"/> 用真实层级覆盖它。
        /// </summary>
        public static void OverrideRenderTierForDebug(int tier)
        {
            _probed = true;
            RenderTier = tier;
            NotifyChanged();
        }

        private static void SyncBatteryAnimations() => AlphaIsland.PluginSdk.DeviceBatteryIndicator.ConfigureAnimations(!InstantMorph && !PowerSave);
        private static void NotifyChanged()
        { SyncBatteryAnimations(); Changed?.Invoke(); }

        /// <summary>一行描述，供启动日志与设置变更日志复用。</summary>
        public static string Describe()
        {
            string tier = SoftwareRendering
                ? $"Tier {RenderTier}（软件渲染，形变自动降级）"
                : $"Tier {RenderTier}（硬件加速）";
            return $"减弱动效={(ReduceMotion ? "开" : "关")} 节能模式={(PowerSave ? "开" : "关")} 渲染层级={tier}";
        }
    }
}
