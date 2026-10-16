using System;
using System.Collections.Generic;

namespace alpha_island.Core
{
    /// <summary>
    /// 注册一个通知（**逻辑面**：优先级 / 可见范围 / 粘性判据）。
    /// 视觉（UIElement、附属动画回调）留在 <see cref="IslandLayerHost"/> 的视图表里 ——
    /// 这个分工就是为了让仲裁可以在没有 WPF 的地方算（S3 的渲染器、S4 的断言脚本）。
    /// </summary>
    public sealed record NotificationRegistration(
        string Key,
        int Priority = 0,
        IslandLayerScope Scope = IslandLayerScope.PillOnly,
        Func<bool>? ForceActive = null,
        Func<int>? PriorityOverride = null,
        IslandSlot Slot = IslandSlot.Pill,
        bool RequiresAttention = false);

    /// <summary>
    /// 通知仲裁中枢（纯逻辑，不碰任何 WPF 元素）。
    ///
    /// 拥有三件事的全部判断：
    ///   1) 「谁还占着位」—— Showing + 生命周期（<see cref="ExpireIfStale"/> 显式回收）；
    ///   2) 「谁此刻够格」—— 时间戳 / 粘性判据 / ForceActive / ScopeAllows(形态, 页签)；
    ///   3) 「谁独占」—— 同槽位按优先级取最高，平手保注册顺序在前的；
    ///   4) 「谁被钉住」—— 图层级置顶（③A）:钉住期间不超时回收、模块也撤不下、同级平手优先（更高的优先级仍可临时打断）。
    /// 算完输出一份不可变的 <see cref="StagePlan"/>，由渲染器落地。
    ///
    /// 状态模型刻意分两层（沿用旧 <c>_xxxNotifying &amp;&amp; XxxNotifyActive</c> 的语义）：
    ///   * Showing —— 写入口把它置真，**过期瞬间不自动变假**；
    ///   * Active  —— Showing 且时间戳没过期、额外条件也满足。
    /// 老代码靠「Showing 真 / Active 假」来判定自然超时并触发收回，所以必须分开。
    /// </summary>
    public sealed class NotificationHub
    {
        private sealed class Entry
        {
            public required NotificationRegistration Reg;
            public bool Showing;
            public DateTime UntilUtc = DateTime.MinValue;
            public IslandNotification? Request;
            public bool Dismissed;
            public bool PresentationSuppressed;

            /// <summary>
            /// 图层级「置顶」（③A）：用户右键钉住了**这一条通知**。语义两件 ——
            /// ① 钉住期间不参与超时回收，**模块自己也撤不下它**（<see cref="Hide"/> 会被拦掉）：
            ///    这是"置顶"必须有的强度，否则一条 2 秒/10 秒生命周期的提示到点就没了，等于没钉；
            /// ② 同级打平手时钉住的赢（优先级更高的层仍可临时打断，让开后钉住的那条自动回来）——
            ///    刻意**不让置顶压过更高优先级**：审批(90)是"必须回应的用户动作"，被一条钉住的下载顶掉
            ///    会让桥接请求一直挂到超时（12:28 的可行性结论里就点了这个风险）。
            /// 真正解除只有两条路：用户再右键取消，或整岛清空（<see cref="HideAll"/>，点音乐球）。
            /// </summary>
            public bool Pinned;
        }

        private readonly Dictionary<string, Entry> _byKey = new(StringComparer.Ordinal);
        private readonly List<Entry> _order = new();

        /// <summary>上一次算出的独占者 Key（只在变化时打日志，用来在日志里证明仲裁真的生效）。</summary>
        private string? _lastWinnerKey;

        /// <summary>上一次独占者的解析后优先级（-1 = 全部让位）；用它把 PriorityOverride 的升降也打进日志。</summary>
        private int _lastWinnerPrio = -1;

        private StagePlan? _lastPlan;

        /// <summary>最近一次 <see cref="ComputePlan"/> 的结果（诊断/断言用）。</summary>
        public StagePlan? LastPlan => _lastPlan;

        /// <summary>诊断：上一次仲裁里独占屏幕的图层 Key（全部让位时为 null）。</summary>
        public string? LastWinnerKey => _lastWinnerKey;

        /// <summary>
        /// 「最近真正给用户看过什么」的记忆 —— 只在有层**真的赢下舞台**时更新，全部让位不动它。
        /// 用途：收缩回细条时圆心（SiriOrb）里装配哪一类专属图标。设计目标见
        /// <c>MainWindow.Geometry.cs</c> 的 <c>PrepareSiriOrbContent</c>：「避免一律用 Siri 渐变或专辑封面」，
        /// 所以需要知道**刚才展示的是音量/电量/外设还是剪切板**。
        /// 这套记忆以前由 19 处 `_currentAction = ...` 手工维护，问题是：新加一个通知模块时
        /// 只要忘了在自己的弹窗路径里补一处赋值，过渡图标就静默退化成封面（而不是报错）。
        /// 现在归仲裁器独占维护 ⇒ 任何走 `NotificationHub.Request` 的新模块**自动**获得这段记忆。
        /// </summary>
        public string? MemorableKey =>
            _memorableAtUtc != default && DateTime.UtcNow - _memorableAtUtc <= MemorableWindow ? _memorableKey : null;

        private string? _memorableKey;
        private DateTime _memorableAtUtc;

        /// <summary>
        /// 记忆的有效期。收回细条时用，既要在"通知刚结束就自动塌回细条"（约 2 秒后）时保住连续性，
        /// 又不能让一次十分钟前的通知在用户手动收起胶囊时冒充"刚才显示的东西"。
        /// （旧实现没有这个概念：它靠"每次形变完成把 _currentAction 归零"来清，因此第二次手动收起必是封面。）
        /// </summary>
        private static readonly TimeSpan MemorableWindow = TimeSpan.FromSeconds(15);

        public void Register(NotificationRegistration reg)
        {
            if (reg is null) throw new ArgumentNullException(nameof(reg));
            if (string.IsNullOrEmpty(reg.Key)) throw new ArgumentException("Key 不能为空", nameof(reg));

            var entry = new Entry { Reg = reg };
            if (_byKey.TryGetValue(reg.Key, out var old)) _order.Remove(old);
            _byKey[reg.Key] = entry;
            _order.Add(entry);
        }

        public bool IsRegistered(string key) => _byKey.ContainsKey(key);
        internal bool SetPresentationSuppressed(string key, bool suppressed)
        {
            if (!_byKey.TryGetValue(key, out var entry) || entry.PresentationSuppressed == suppressed) return false;
            entry.PresentationSuppressed = suppressed;
            return true;
        }
        internal bool IsPresentationSuppressed(string key)
            => _byKey.TryGetValue(key, out var entry) && entry.PresentationSuppressed;
        internal int ResolvedPriority(string key) => _byKey.TryGetValue(key, out var entry) ? PriorityOf(entry) : int.MinValue;
        public bool Unregister(string key)
        {
            if (!_byKey.Remove(key, out var entry)) return false;
            _order.Remove(entry);
            if (PreferredWinnerKey == key) PreferredWinnerKey = null;
            if (_lastWinnerKey == key) _lastWinnerKey = null;
            if (_memorableKey == key) { _memorableKey = null; _memorableAtUtc = default; }
            return true;
        }

        /// <summary>首次续期请求可接管主岛，已有通知的后台刷新保留用户选择。</summary>
        internal bool ShouldPromoteToMain(IslandNotification notification)
            => CanRequest(notification) && notification.OpensIsland
                && notification.Key != IslandLayerHost.GreetingKey
                && (notification.Lifetime != NotificationLifetime.Refreshing || !IsActive(notification.Key));

        internal bool CanRequest(IslandNotification notification)
            => _byKey.TryGetValue(notification.Key, out var entry)
                && !(entry.Dismissed && notification.Lifetime == NotificationLifetime.Refreshing);

        // ---------- 写入口 ----------

        public void ShowUntil(string key, DateTime untilUtc)
        {
            if (!_byKey.TryGetValue(key, out var entry)) return;
            entry.Dismissed = false;
            entry.Showing = true;
            entry.UntilUtc = untilUtc;
        }

        /// <summary>提交一条通知请求（S2 起模块走这条；生命周期由请求自己描述）。</summary>
        public bool Request(IslandNotification notification)
        {
            if (!CanRequest(notification) || !_byKey.TryGetValue(notification.Key, out var entry)) return false;
            entry.Dismissed = false;
            entry.Request = notification;
            entry.Showing = true;
            entry.UntilUtc = notification.Lifetime == NotificationLifetime.Sticky
                ? DateTime.MaxValue
                : notification.UntilUtc;
            return true;
        }

        public void Hide(string key)
        {
            if (!_byKey.TryGetValue(key, out var entry)) return;
            // 置顶的层不接受"撤下"：模块自己到点收工时也不能把用户钉住的东西拿走。
            // 取消置顶由 SetPinned 立即收起；模块状态结束时的 Hide 允许下一次持续通知重新出现。
            if (entry.Pinned)
            {
                IslandLog.Debug("Pin", $"图层 {key} 的撤下请求被置顶拦住（先右键取消置顶，或点音乐球清空所有通知）[pin=block-hide]");
                return;
            }
            entry.Showing = false;
            entry.UntilUtc = DateTime.MinValue;
            entry.Request = null;
            entry.Dismissed = false; // 后台状态结束后，下一次新的持续通知可重新出现。
        }

        public void HideAll()
        {
            foreach (var entry in _order)
            {
                ClearPinOnHide(entry);
                entry.Showing = false;
                entry.UntilUtc = DateTime.MinValue;
                entry.Request = null;
                entry.Dismissed = false;
            }
        }

        // ---------- 图层级置顶（③A：每个弹窗各自独立的置顶）----------

        /// <summary>
        /// 钉住 / 取消钉住一个图层。返回**是否真的发生了变化**（没登记过或状态没变都返回 false，
        /// 免得调用方为一次空操作去打日志、弹气泡）。调用方随后应让视觉重算一次（Host 会 RequestSync）。
        /// </summary>
        public bool SetPinned(string key, bool pinned)
        {
            if (!_byKey.TryGetValue(key, out var entry)) return false;
            if (entry.Pinned == pinned) return false;

            entry.Pinned = pinned;
            if (pinned)
            {
                entry.Dismissed = false;
                entry.Showing = true;
                entry.UntilUtc = DateTime.MaxValue;
            }
            else
            {
                // 取消置顶是用户明确收起这一岛；审批仍在等待时必须继续可见。
                if (!entry.Reg.RequiresAttention || entry.Reg.ForceActive?.Invoke() != true)
                {
                    entry.Dismissed = true;
                    entry.Showing = false;
                    entry.UntilUtc = DateTime.MinValue;
                    entry.Request = null;
                }
            }
            IslandLog.Debug("Pin", pinned
                ? $"图层置顶：{key} → 已钉住（不再超时回收、模块撤不下、同级平手优先；优先级更高的通知仍可临时打断）[pin=on]"
                : $"图层置顶：{key} → 已取消并独立收起（待审批请求继续显示）[pin=off]");
            return true;
        }

        public bool IsPinned(string key)
            => _byKey.TryGetValue(key, out var entry) && entry.Pinned;

        /// <summary>此刻钉住且还立着的图层 Key（按注册顺序）。</summary>
        public IReadOnlyList<string> PinnedKeys
        {
            get
            {
                var keys = new List<string>();
                foreach (var entry in _order)
                {
                    if (entry.Pinned) keys.Add(entry.Reg.Key);
                }
                return keys;
            }
        }

        /// <summary>有没有被钉住的图层（整岛"不自动收回"的判据之一）。</summary>
        public bool AnyPinned
        {
            get
            {
                foreach (var entry in _order)
                {
                    if (entry.Pinned && !entry.PresentationSuppressed) return true;
                }
                return false;
            }
        }

        /// <summary>整岛清空（点音乐球）时连钉子一起拔掉 —— 那是用户明确说"全清"，也是解除置顶的兜底出口。</summary>
        private static void ClearPinOnHide(Entry entry)
        {
            if (!entry.Pinned) return;
            entry.Pinned = false;
            IslandLog.Debug("Pin", $"图层 {entry.Reg.Key} 随整岛清空解除置顶[pin=clear]");
        }

        /// <summary>
        /// 自然超时回收：Showing 还立着但已经 Active 不成立时，把 Showing 落下并返回一次 true。
        /// 复刻老代码「超时 → 清标志 → 若没置顶且无媒体就收回细条」的那一拍。
        /// </summary>
        public bool ExpireIfStale(string key)
        {
            if (!_byKey.TryGetValue(key, out var entry)) return false;
            if (!entry.Showing) return false;
            if (entry.PresentationSuppressed || PresentationAllowed?.Invoke(entry.Reg.Key) == false) return false;
            // 置顶的层不自然回收：否则"钉一条 2 秒的下载提示"在下一拍就会被收回，等于没钉。
            if (entry.Pinned) return false;
            if (IsActiveInternal(entry)) return false;

            entry.Showing = false;
            entry.UntilUtc = DateTime.MinValue;
            entry.Request = null;
            return true;
        }

        /// <summary>
        /// 批量自然超时回收：检查所有登记图层，凡是 Showing 且非置顶、且已不再 Active 的，统统回收。
        /// 若有任何图层被回收，返回 true。
        /// </summary>
        public bool ExpireAllStale()
        {
            bool anyExpired = false;
            foreach (var entry in _order)
            {
                if (!entry.Showing) continue;
                if (entry.PresentationSuppressed || PresentationAllowed?.Invoke(entry.Reg.Key) == false) continue;
                if (entry.Pinned) continue;
                if (IsActiveInternal(entry)) continue;

                entry.Showing = false;
                entry.UntilUtc = DateTime.MinValue;
                entry.Request = null;
                anyExpired = true;
            }
            return anyExpired;
        }

        // ---------- 读入口 ----------

        public bool IsShowing(string key)
            => _byKey.TryGetValue(key, out var entry) && (entry.Showing || entry.Pinned);

        public bool IsActive(string key)
            => _byKey.TryGetValue(key, out var entry) && IsActiveInternal(entry);

        /// <summary>最近一次仲裁里这一层真的画在屏幕上（看了形态/页签，<see cref="IsShowing"/> 不看）。</summary>
        public bool IsVisible(string key) => _lastPlan?.IsVisible(key) ?? false;

        /// <summary>
        /// 取该图层当前登记的请求（含 Glyph/Title/Detail 等展示素材），没登记过就返回 null。
        /// 卡片态旁路提示（<c>IslandSlot.SideBubble</c>）靠它把"被当前形态挡住的最高优先级请求"画成一个小气泡。
        /// </summary>
        public IslandNotification? Peek(string key)
            => _byKey.TryGetValue(key, out var entry) ? entry.Request : null;

        /// <summary>当前「Showing 且 Active」的图层 Key 列表（按注册顺序），叠层取证用。</summary>
        public IReadOnlyList<string> ActiveKeys
        {
            get
            {
                var keys = new List<string>();
                foreach (var entry in _order)
                {
                    if (IsActiveInternal(entry)) keys.Add(entry.Reg.Key);
                }
                return keys;
            }
        }

        /// <summary>老代码里的 HasActiveNotification：任意图层 Showing 且 Active。</summary>
        public bool AnyActive
        {
            get
            {
                foreach (var entry in _order)
                {
                    if (IsActiveInternal(entry)) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 外部优先指定的主岛 Winner（多岛流转时最新弹窗或用户点击选中的小岛）。
        /// 设为 "music" 表示主岛属于音乐，通知层全部不抢占 winner。
        /// </summary>
        public string? PreferredWinnerKey { get; set; }

        // ---------- 仲裁 ----------

        /// <summary>
        /// 算出这一拍该谁显示：槽位内按优先级取最高，平手保注册顺序在前的。
        /// 够格但被当前形态挡住的最高优先级请求记进 <see cref="StagePlan.DeferredKey"/>（卡片态旁路提示用）。
        /// </summary>
        public StagePlan ComputePlan(IslandState state, bool clipboardPageActive)
        {
            Entry? winner = null;
            Entry? deferred = null;
            var activeKeys = new List<string>();

            foreach (var entry in _order)
            {
                if (!IsActiveInternal(entry)) continue;
                activeKeys.Add(entry.Reg.Key);

                if (!ScopeAllows(entry, state, clipboardPageActive))
                {
                    // 够格但画不出来：卡片态排队等回到胶囊后补显（S4 用它弹旁路小提示）。
                    if (DeferredInCard(entry) && (deferred == null || Beats(entry, deferred)))
                    {
                        deferred = entry;
                    }
                    continue;
                }

                if (winner == null || Beats(entry, winner)) winner = entry;
            }

            // 优先主岛控制（多岛流转）：
            bool requiresAttention = winner?.Reg.RequiresAttention == true;
            if (!requiresAttention && string.Equals(PreferredWinnerKey, "music", StringComparison.Ordinal))
            {
                // 欢迎语必须在主岛展示（不作为卫星小岛）
                if (_byKey.TryGetValue(IslandLayerHost.GreetingKey, out var grt)
                    && IsActiveInternal(grt)
                    && ScopeAllows(grt, state, clipboardPageActive))
                {
                    winner = grt;
                }
                else
                {
                    winner = null; // 主岛让给音乐
                }
            }
            else if (!requiresAttention && !string.IsNullOrEmpty(PreferredWinnerKey) && _byKey.TryGetValue(PreferredWinnerKey, out var pref))
            {
                if (IsActiveInternal(pref) && ScopeAllows(pref, state, clipboardPageActive))
                {
                    winner = pref;
                }
            }

            // 只在「谁独占」发生变化时记一行 —— 这行日志是「两层不再叠字」的唯一可断言证据。
            string? winnerKey = winner?.Reg.Key;
            if (winnerKey is { Length: > 0 } && !string.Equals(winnerKey, _lastWinnerKey, StringComparison.Ordinal))
            {
                // 只在"新的一层真的赢下舞台"时刷新记忆（让位不动它，否则收起瞬间记忆就没了）
                _memorableKey = winnerKey;
                _memorableAtUtc = DateTime.UtcNow;
            }
            // 独占者没变、但它的优先级变了（PriorityOverride：网速平时 64、告警那 4 秒升到 70）
            // 同样要记一行，否则「升级有没有生效」在日志里完全看不出来。
            int winnerPrio = winner is null ? -1 : PriorityOf(winner);
            bool winnerChanged = !string.Equals(winnerKey, _lastWinnerKey, StringComparison.Ordinal);
            bool prioChanged = winner is not null && winnerPrio != _lastWinnerPrio;
            if (winnerChanged)
            {
                _lastWinnerKey = winnerKey;
            }
            if (winnerChanged || prioChanged)
            {
                _lastWinnerPrio = winnerPrio;
                IslandLog.Debug("Layer", winner is null
                    ? "通知图层仲裁：全部让位（没有层要显示）[layer=none]"
                    : $"通知图层仲裁：{winnerKey} 独占（优先级 {winnerPrio}，其余层全部隐藏"
                      + (winner.Pinned ? "；本条已置顶 [pin=win]" : string.Empty) + "）"
                      // 行尾的 ASCII 标记供验收脚本断言（中文正则写不进纯 ASCII 的 .ps1）
                      + $"[winner={winnerKey} prio={winnerPrio}]"
                      + (prioChanged && !winnerChanged ? "[prio-change]" : string.Empty));
            }

            var visibleKeys = winner is null
                ? (IReadOnlyList<string>)Array.Empty<string>()
                : new[] { winner.Reg.Key };

            var plan = new StagePlan(state, winnerKey, visibleKeys, activeKeys, deferred?.Reg.Key);
            _lastPlan = plan;
            return plan;
        }

        // ---------- 内部 ----------

        internal Func<string, bool>? PresentationAllowed { get; set; }
        private bool IsActiveInternal(Entry entry)
        {
            // Foreground suppression preserves the task claim, request and user's pin.
            if (entry.PresentationSuppressed || PresentationAllowed?.Invoke(entry.Reg.Key) == false) return false;
            // 置顶：始终有效（即便是在卡片态置顶未被触发过弹窗）
            if (entry.Pinned) return true;
            if (!entry.Showing) return false;
            if (entry.Reg.ForceActive is { } force && force()) return true;

            if (entry.Request is { } request && request.Lifetime == NotificationLifetime.Sticky)
            {
                if (request.Sticky is null) return true;
                try
                {
                    return request.Sticky();
                }
                catch (Exception ex)
                {
                    IslandLog.Error("Layer:" + entry.Reg.Key, ex);
                    return false;
                }
            }

            return DateTime.UtcNow < entry.UntilUtc;
        }

        /// <summary>
        /// 同级比较（③A 图层级置顶）：**先比优先级，打平手时钉住的赢**。
        /// 刻意不让置顶压过更高优先级的层：审批(90)这类"必须回应的用户动作"被一条钉住的背景层
        /// 顶掉会让桥接请求挂到超时。置顶的层不会因此消失 —— 它仍然 Active，高优先级的让开后自动回来。
        /// </summary>
        private static bool Beats(Entry candidate, Entry current)
        {
            if (candidate.Reg.RequiresAttention != current.Reg.RequiresAttention)
                return candidate.Reg.RequiresAttention;
            int candidatePriority = PriorityOf(candidate);
            int currentPriority = PriorityOf(current);
            if (candidatePriority != currentPriority) return candidatePriority > currentPriority;
            return candidate.Pinned && !current.Pinned;
        }

        /// <summary>这一层此刻的仲裁优先级：请求自带 &gt; 注册表的动态优先级 &gt; 请求静态值 &gt; 注册表静态值。</summary>
        private static int PriorityOf(Entry entry)
        {
            Func<int>? over = null;
            if (entry.Request is { } request && request.PriorityOverride is { } requestOverride)
            {
                over = requestOverride;
            }
            else if (entry.Reg.PriorityOverride is { } specOverride)
            {
                over = specOverride;
            }

            if (over is not null)
            {
                try
                {
                    return over();
                }
                catch (Exception ex)
                {
                    IslandLog.Error("Layer:" + entry.Reg.Key, ex);
                }
            }

            return entry.Request is { } req && req.Priority != IslandNotification.UseRegistrationPriority
                ? req.Priority
                : entry.Reg.Priority;
        }

        private static bool DeferredInCard(Entry entry)
            => entry.Request is { } request ? request.DeferredInCard : true;

        private static bool ScopeAllows(Entry entry, IslandState state, bool clipboardPageActive)
        {
            // 细条角标不占 328x32 胶囊格，任何形态都能显示。
            if (entry.Reg.Slot == IslandSlot.BarIndicator) return true;

            // 目前只有 Pill 槽位有真实视觉：PillOnly 只在胶囊态放行，
            // PillOrClipboardPage 额外放行卡片态的第 1 页（剪切板页）。
            return entry.Reg.Scope switch
            {
                IslandLayerScope.PillOrClipboardPage =>
                    state == IslandState.Pill || (state == IslandState.Card && clipboardPageActive),
                _ => state == IslandState.Pill,
            };
        }
    }
}
