using System;
using System.Collections.Generic;

namespace alpha_island.Core
{
    /// <summary>通知占用的显示槽位。同一槽位同一时刻只放行一个赢家。</summary>
    public enum IslandSlot
    {
        /// <summary>细条上的常驻小指示（角标/色点，不占 328x32 格）。</summary>
        BarIndicator = 0,

        /// <summary>一级胶囊格（328x32 居中 Grid）—— 目前所有通知层都在这里。</summary>
        Pill = 1,

        /// <summary>卡片页内容。</summary>
        Card = 2,

        /// <summary>岛体一旁的小提示（卡片态旁路提醒，S4 的「小岛提示」）。</summary>
        SideBubble = 3,

        /// <summary>岛体外挂容器（音乐球等卫星，S4 泛化后的容器的消费者）。</summary>
        Satellite = 4,
    }

    /// <summary>通知的生命周期。决定「谁还占着位」怎么算。</summary>
    public enum NotificationLifetime
    {
        /// <summary>一次性：到点即失效。<c>Show(key, duration)</c> 就是它。</summary>
        Timed = 0,

        /// <summary>可续期：调用方每拍重新计时（下载进度、网速常显）。</summary>
        Refreshing = 1,

        /// <summary>粘性：只要 <see cref="IslandNotification.Sticky"/> 返回真就一直有效（审批、agent 忙）。</summary>
        Sticky = 2,
    }

    /// <summary>
    /// 一条通知请求 —— 纯数据 + 纯委托，**不含任何 WPF 类型**。
    ///
    /// 它是「模块 → <see cref="NotificationHub"/>（纯逻辑仲裁）→ 渲染器（唯一视觉写手）」
    /// 三层架构里的中间层契约：模块只描述「要提醒什么、多重要、放哪个槽、活多久」，
    /// 不再自己写 Opacity / Visibility / SetState。
    ///
    /// S1 阶段它还是旁路 API（旧的 <c>_layers.Show(...)</c> 内部转成它）；S2 起
    /// 32 处调用点直接提交它。
    /// </summary>
    public readonly record struct IslandNotification(
        string Key,
        /// <summary>不指定时用注册表里那一层的优先级（绝大多数通知都不需要自己写）。</summary>
        int Priority = IslandNotification.UseRegistrationPriority,
        NotificationLifetime Lifetime = NotificationLifetime.Timed,
        DateTime UntilUtc = default,
        IslandSlot Slot = IslandSlot.Pill,
        string Glyph = "",
        string Title = "",
        string Detail = "",
        string Value = "",
        /// <summary>卡片态是否排队、等回到胶囊后补显（false = 强制切胶囊，审批用）。</summary>
        bool DeferredInCard = true,
        /// <summary>
        /// 这条请求允不允许改变岛体形态（细条 / 卡片 / 中转站 → 胶囊）。
        /// false = **只登记、不主动展开**：给「背景状态」用（agent 忙碌层）——
        /// 细条态只亮角标，用户自己把岛打开时才看得到这一层。
        /// </summary>
        bool OpensIsland = true,
        /// <summary><see cref="NotificationLifetime.Sticky"/> 的判据。</summary>
        Func<bool>? Sticky = null,
        /// <summary>动态优先级：非 null 时取它替代 <see cref="Priority"/>。</summary>
        Func<int>? PriorityOverride = null)
    {
        /// <summary><see cref="Priority"/> 的哨兵值：用注册表里的优先级。</summary>
        public const int UseRegistrationPriority = int.MinValue;

        /// <summary>弹 <paramref name="seconds"/> 秒后自然过期。</summary>
        public static IslandNotification Seconds(
            string key, double seconds, IslandSlot slot = IslandSlot.Pill,
            bool deferredInCard = true, bool opensIsland = true)
            => new(key, UseRegistrationPriority, NotificationLifetime.Timed,
                   DateTime.UtcNow + TimeSpan.FromSeconds(seconds), slot,
                   DeferredInCard: deferredInCard, OpensIsland: opensIsland);

        /// <summary>可续期（调用方每拍重新提交）。</summary>
        public static IslandNotification Refreshing(
            string key, double seconds, IslandSlot slot = IslandSlot.Pill,
            bool deferredInCard = true, bool opensIsland = true)
            => new(key, UseRegistrationPriority, NotificationLifetime.Refreshing,
                   DateTime.UtcNow + TimeSpan.FromSeconds(seconds), slot,
                   DeferredInCard: deferredInCard, OpensIsland: opensIsland);

        /// <summary>粘性：<paramref name="sticky"/> 为真期间一直有效。</summary>
        public static IslandNotification While(
            string key, Func<bool> sticky, IslandSlot slot = IslandSlot.Pill,
            bool deferredInCard = true, bool opensIsland = true)
            => new(key, UseRegistrationPriority, NotificationLifetime.Sticky, DateTime.MaxValue,
                   slot, DeferredInCard: deferredInCard, OpensIsland: opensIsland, Sticky: sticky);

        /// <summary>这一刻还活着吗。</summary>
        public bool IsAlive(DateTime now)
            => Lifetime == NotificationLifetime.Sticky
                ? Sticky is null || Sticky()
                : now < UntilUtc;
    }

    /// <summary>
    /// 一次仲裁的结果。渲染器**只认它**，不再自己判断「谁能显示」。
    /// 不可变 —— 便于断言与日志（S4 的断言脚本会直接 dump 它）。
    /// </summary>
    public sealed record StagePlan(
        IslandState State,
        string? WinnerKey,
        IReadOnlyList<string> VisibleKeys,
        IReadOnlyList<string> ActiveKeys,
        /// <summary>此刻够格、但被当前形态挡住的最高优先级请求（卡片态旁路小提示用它）。</summary>
        string? DeferredKey)
    {
        public bool AnyVisible => VisibleKeys.Count > 0;

        public bool IsVisible(string key)
        {
            for (int i = 0; i < VisibleKeys.Count; i++)
            {
                if (string.Equals(VisibleKeys[i], key, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
