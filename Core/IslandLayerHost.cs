using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Animation;

namespace alpha_island.Core
{
    /// <summary>通知图层的可见范围。</summary>
    public enum IslandLayerScope
    {
        /// <summary>只在一级胶囊（Pill）形态显示。</summary>
        PillOnly = 0,

        /// <summary>一级胶囊，或卡片态的第 1 页（顶替卡片内容）。</summary>
        /// <summary>剪切板页在卡片态下是否也算可见。页序号由调用方按 Id 查出来传进来（-1 = 该页当前不可用）。</summary>
        PillOrClipboardPage = 1,
    }

    /// <summary>注册一个通知图层所需的一切（视觉绑定 + 仲裁参数）。</summary>
    /// <param name="Priority">
    /// 同屏仲裁优先级：数值大的赢。所有通知图层都是同一个 328x32 居中 Grid，
    /// 同时可见就是字叠字，所以 <see cref="NotificationHub.ComputePlan"/> 每拍只放行优先级最高的那一层。
    /// 约定（见 MainWindow 构造函数里的注册表）：
    /// 欢迎 100 &gt; 审批 90 &gt; 音量 80 &gt; 蓝牙 76 &gt; 电池 72 &gt; 系统告警 68 &gt; 下载 66 &gt; 网速 64 &gt; 剪切板 56 &gt; agent 状态 48。
    /// 排序依据："必须回应的用户动作/提醒"压过"持续性的背景状态"。
    /// </param>
    public sealed record IslandLayerSpec(
        string Key,
        UIElement Root,
        IslandLayerScope Scope = IslandLayerScope.PillOnly,
        Func<bool>? ForceActive = null,
        Action<bool>? OnVisibilityChanged = null,
        int Priority = 0,
        /// <summary>
        /// 动态优先级：非 null 时取它的返回值替代 <see cref="Priority"/>。
        /// 给"平时是背景状态、某一刻升级成必须看到的提醒"的图层用 ——
        /// 网速层平时 64（让位于下载提示 66），触发阈值告警的那 4 秒升到 70（压过下载），
        /// 否则告警会被每 2 秒续期一次的下载提示永久吞掉。
        /// </summary>
        Func<int>? PriorityOverride = null,
        /// <summary>显示槽位：细条角标 / 胶囊格 / 卡片 / 旁路小提示 / 卫星。</summary>
        IslandSlot Slot = IslandSlot.Pill,
        bool RequiresAttention = false);

    /// <summary>一次 Sync 的结果，供调用方接着决定音乐图层 / 音乐圆球的去留。</summary>
    public readonly struct LayerSyncResult
    {
        /// <summary>当前实际可见的图层里是否有任意一个。</summary>
        public bool AnyVisible { get; init; }

        /// <summary>除欢迎层以外是否有图层可见（音乐圆球只在非欢迎通知期间出现）。</summary>
        public bool AnyVisibleExceptGreeting { get; init; }

        /// <summary>欢迎层当前可见。</summary>
        public bool GreetingVisible { get; init; }
    }

    /// <summary>
    /// 通知图层宿主 —— **只管视觉落地**：把 <see cref="NotificationHub"/> 算出的
    /// <see cref="StagePlan"/> 刷到 XAML 上（Opacity / Visibility / IsHitTestVisible），
    /// 并把可见性变化回报给注册时挂的附属回调。
    ///
    /// 加一个新的弹出式通知（网速告警、agent 审批…）从此只需要：
    ///   1) 在 XAML 里放一个 <c>Grid</c> 并起名；
    ///   2) 构造函数里 <c>Register(new IslandLayerSpec("xxx", XxxLayer))</c>；
    ///   3) 弹的时候 <c>_layers.Show("xxx", TimeSpan.FromSeconds(n))</c>
    ///      （S2 起改成提交一条 <see cref="IslandNotification"/>）。
    /// 显隐、互斥、命中测试、音乐图层让位都由 <see cref="NotificationHub"/> 统一算，不必再改六处。
    /// </summary>
    public sealed class IslandLayerHost
    {
        public const string GreetingKey = "greeting";

        /// <summary>某一层的视图绑定（视觉面）与上一拍的可见性。</summary>
        private sealed class View
        {
            public required UIElement Root;
            public Action<bool>? OnVisibilityChanged;
            public bool LastVisible;
            public IslandSlot Slot;
        }

        private readonly NotificationHub _hub = new();
        private readonly Dictionary<string, View> _views = new(StringComparer.Ordinal);
        private readonly List<string> _order = new();

        // ---------- 形变（reveal）窗口 ----------
        //
        // 通知图层的透明度不是"开/关"，而是跟着主弹簧的形变进度走的：胶囊展开时淡入、塌回收缩时淡出。
        // 这段状态以前散在 MainWindow.ApplyReveal 的一份白名单里（`showVol = _volumeNotifying && ...`），
        // 它认不出 vivo / download / agentstatus / approval 这几层，于是同一拍里"宿主写 0、白名单写 1"，
        // 谁后跑谁赢 —— 这就是叠层与随机闪烁的根。现在窗口状态由宿主独占：
        //   BeginMorph(prev, next) —— 换态前调用：把"换态前真的画在屏幕上的层"记成淡出源层快照；
        //   SetReveal(from, to)    —— 形变每帧回传进度（0..1），宿主按同一个 plan 逐层重算透明度；
        //   EndMorph()             —— 形变结束，透明度归一化回 1。
        private bool _revealActive;
        private IslandState _revealFrom = IslandState.Bar;
        private IslandState _revealTo = IslandState.Bar;
        private double _fromFade;
        private double _toFade;

        /// <summary>本次形变里"正在淡出"的层（换态前可见）：plan 已经不要它们了，但还要把余下的透明度画完。</summary>
        private HashSet<string>? _fadingKeys;

        /// <summary>上一拍 plan 里可见的层，供下一次 <see cref="BeginMorph"/> 当淡出源层快照。</summary>
        private HashSet<string> _appliedVisible = new(StringComparer.Ordinal);

        // ---------- 主元素（一级胶囊的内容层 PillLayer）----------
        //
        // 它是"没有任何通知占位时"的那个默认内容（歌词/音乐胶囊），历史上不属于任何注册图层，
        // 于是 Reveal.cs 的白名单、Notifications.cs 的 show/hide 分支、Geometry.cs 的换态裸写
        // 三处各写各的 —— 也一并收进宿主。
        private UIElement? _primary;
        private Func<bool>? _primaryCanShow;
        private Action<bool>? _primaryVisibilityChanged;
        private bool _primaryLastVisible;
        private bool _primaryVisibleAtMorphStart;

        /// <summary>音乐圆球的收缩/绽放时间轴在跑：这期间主元素的透明度由动画掌管，宿主别插手。</summary>
        private bool _primaryAnimating;

        /// <summary>仲裁中枢（纯逻辑）。S3 的渲染器与 S4 的断言脚本都读它算出的 plan。</summary>
        public NotificationHub Hub => _hub;

        /// <summary>
        /// 外部优先指定的主岛 Winner（多岛流转时最新弹窗或用户点击选中的小岛）。
        /// 设为 "music" 表示主岛属于音乐，通知层全部不抢占 winner。
        /// </summary>
        public string? PreferredWinnerKey
        {
            get => _hub.PreferredWinnerKey;
            set => _hub.PreferredWinnerKey = value;
        }

        /// <summary>最近一次仲裁结果（诊断用）。</summary>
        public StagePlan? LastPlan => _hub.LastPlan;

        /// <summary>
        /// 写入口（Show/Hide）之后要立刻重算显隐的回调，由 MainWindow 在构造函数里指向
        /// SyncNotificationLayers。没有它，一次弹出要等到下一拍别的同步才生效 ——
        /// A 组系统提示正是踩了这个坑：弹出时音乐胶囊没让位，两层 328x32 就叠在一起。
        /// </summary>
        public Action? SyncRequested { get; set; }

        /// <summary>回调重入保护：SyncNotificationLayers 自身不会再调 Show/Hide，但别赌这个。</summary>
        private bool _requestingSync;

        private void RequestSync()
        {
            if (_requestingSync || SyncRequested is not { } sync) return;
            _requestingSync = true;
            try
            {
                sync();
            }
            catch (Exception ex)
            {
                IslandLog.Error("Layer:SyncRequested", ex);
            }
            finally
            {
                _requestingSync = false;
            }
        }

        public void Register(IslandLayerSpec spec)
        {
            if (spec is null) throw new ArgumentNullException(nameof(spec));
            if (string.IsNullOrEmpty(spec.Key)) throw new ArgumentException("Key 不能为空", nameof(spec));

            if (_views.ContainsKey(spec.Key)) _order.Remove(spec.Key);
            _views[spec.Key] = new View
            {
                Root = spec.Root,
                OnVisibilityChanged = spec.OnVisibilityChanged,
                Slot = spec.Slot,
            };
            _order.Add(spec.Key);

            _hub.Register(new NotificationRegistration(
                spec.Key, spec.Priority, spec.Scope, spec.ForceActive, spec.PriorityOverride, spec.Slot, spec.RequiresAttention));

            // 初始一律按隐藏落地，避免 XAML 里写了 Opacity=1 却没被 Sync 覆盖
            Apply(spec.Key, false);
        }

        public bool IsRegistered(string key) => _hub.IsRegistered(key);
        public bool Unregister(string key)
        {
            if (!_views.ContainsKey(key)) return false;
            Apply(key, false);
            _views.Remove(key); _order.Remove(key);
            _appliedVisible.Remove(key); _fadingKeys?.Remove(key);
            _hub.Unregister(key);
            RequestSync();
            return true;
        }

        // ---------- 写入口 ----------

        /// <summary>提交一条通知请求（S2 起模块统一走这条）。返回 false = 该 Key 没注册过。</summary>
        public bool Request(IslandNotification notification, bool requestSync = true)
        {
            bool accepted = _hub.Request(notification);
            if (accepted && requestSync) RequestSync();
            return accepted;
        }

        /// <summary>弹出一个通知，<paramref name="duration"/> 后自然过期。重复调用 = 重新计时。</summary>
        public void Show(string key, TimeSpan duration)
            => ShowUntil(key, DateTime.UtcNow + duration);

        public void ShowSeconds(string key, double seconds)
            => ShowUntil(key, DateTime.UtcNow + TimeSpan.FromSeconds(seconds));

        public void ShowUntil(string key, DateTime untilUtc)
        {
            if (!_hub.IsRegistered(key)) return;
            _hub.ShowUntil(key, untilUtc);
            RequestSync();
        }

        /// <summary>立刻撤下。</summary>
        public void Hide(string key)
        {
            if (!_hub.IsRegistered(key)) return;
            _hub.Hide(key);
            RequestSync();
        }

        public void HideAll()
        {
            _hub.HideAll();
            RequestSync();
        }

        /// <summary>
        /// 自然超时回收：Showing 还立着但已经 Active 不成立时，把 Showing 落下并返回一次 true。
        /// 复刻老代码「超时 → 清标志 → 若没置顶且无媒体就收回细条」的那一拍。
        /// </summary>
        public bool ExpireIfStale(string key) => _hub.ExpireIfStale(key);
        public bool ExpireAllStale() => _hub.ExpireAllStale();

        // ---------- 读入口 ----------

        public bool IsShowing(string key) => _hub.IsShowing(key);

        public bool IsActive(string key) => _hub.IsActive(key);

        internal bool IsPresentationSuppressed(string key) => _hub.IsPresentationSuppressed(key);

        internal void SetPresentationSuppressed(string key, bool suppressed)
        {
            if (!_hub.SetPresentationSuppressed(key, suppressed)) return;
            if (suppressed)
            {
                _fadingKeys?.Remove(key);
                _appliedVisible.Remove(key);
                Apply(key, false);
            }
            RequestSync();
        }

        /// <summary>
        /// 上一次 Sync() 算出的"这一层此刻真的画在屏幕上"，供需要跟着图层显隐走的附属内容查。
        /// 与 IsShowing 的区别：IsShowing 不看当前岛体形态/页签，本属性看（ScopeAllows）。
        /// </summary>
        public bool IsVisible(string key) => _hub.IsVisible(key);

        /// <summary>「刚才真正给用户看过哪一层」的记忆（由仲裁器维护，见 <see cref="NotificationHub.MemorableKey"/>）。</summary>
        public string? MemorableKey => _hub.MemorableKey;

        /// <summary>取该图层当前登记的请求（展示素材），供卡片态旁路提示复用；没登记过返回 null。</summary>
        public IslandNotification? Peek(string key) => _hub.Peek(key);

        /// <summary>诊断：上一次 Sync() 里独占屏幕的图层 Key（没有任何层显示时为 null）。</summary>
        public string? LastWinnerKey => _hub.LastWinnerKey;

        /// <summary>诊断：当前「Showing 且 Active」的图层 Key 列表（按注册顺序），叠层取证用。</summary>
        public IReadOnlyList<string> ActiveKeys => _hub.ActiveKeys;

        /// <summary>老代码里的 HasActiveNotification：任意图层 Showing 且 Active。</summary>
        public bool AnyActive => _hub.AnyActive;

        // ---------- 图层级置顶（③A：每个弹窗各自独立的置顶）----------

        /// <summary>
        /// 钉住/取消钉住某一层，返回**是否真的变了**（没登记过或没变化 = false）——
        /// 注意它**不是**"现在钉没钉"，要状态请回问 <see cref="IsPinned"/>（曾经把返回值当状态用，
        /// 结果"取消置顶"弹出了"已置顶"气泡）。变了就立刻 RequestSync：置顶会让"谁独占"当场换人，不能等下一拍。
        /// 翻转请调用方自己写 <c>!IsPinned(key)</c>：只留一个返回值约定，免得两套语义打架。
        /// </summary>
        public bool SetPinned(string key, bool pinned)
        {
            if (!_hub.SetPinned(key, pinned)) return false;
            RequestSync();
            return true;
        }

        public bool IsPinned(string key) => _hub.IsPinned(key);

        /// <summary>此刻钉住且还立着的图层 Key（按注册顺序）。</summary>
        public IReadOnlyList<string> PinnedKeys => _hub.PinnedKeys;

        /// <summary>有没有被钉住的图层。</summary>
        public bool AnyPinned => _hub.AnyPinned;

        // ---------- 主元素（PillLayer）与形变窗口 ----------

        /// <summary>
        /// 登记一级胶囊的内容层（PillLayer）并提供它的显示判据。
        /// 判据由 MainWindow 给（<c>WantPillLayerVisible</c>：胶囊态 + 没有通知占位 + 音乐圆球不在场），
        /// 但**写 Opacity/Visibility 的只有宿主这一处**。
        /// </summary>
        public void RegisterPrimary(UIElement root, Func<bool> canShow, Action<bool>? onVisibilityChanged = null)
        {
            _primary = root ?? throw new ArgumentNullException(nameof(root));
            _primaryCanShow = canShow ?? throw new ArgumentNullException(nameof(canShow));
            _primaryVisibilityChanged = onVisibilityChanged;
            _primaryAnimating = false;
            _primaryVisibleAtMorphStart = false;
            ApplyPrimary();
        }

        /// <summary>诊断：主元素此刻是否真的画在屏幕上。</summary>
        public bool PrimaryVisible => _primaryLastVisible;

        /// <summary>
        /// 开一段形变窗口（在 <c>SetState</c> 换态前调用）。快照"换态前的可见层"作为淡出源层 ——
        /// 这一句替代了旧白名单里 <c>_revealTo == Bar &amp;&amp; _currentAction == Xxx</c> 那 7 行特判：
        /// 旧写法只认得 7 个模块动作，新写法对任意图层（含 vivo/download/agentstatus/approval）一视同仁。
        /// </summary>
        public void BeginMorph(IslandState from, IslandState to)
        {
            _revealFrom = from;
            _revealTo = to;
            _fromFade = 0.0;   // 第一帧：展开时"要淡入的还没出现"，收缩时"要淡出的还全亮"
            _toFade = 0.0;
            _revealActive = from != to;
            _fadingKeys = new HashSet<string>(_appliedVisible, StringComparer.Ordinal);
            _primaryVisibleAtMorphStart = _primaryLastVisible;
        }

        /// <summary>形变每帧回传进度（0..1）：只按新的透明度重刷缓存下来的 plan，不重算仲裁。</summary>
        public void SetReveal(double fromFade, double toFade)
        {
            _fromFade = Math.Clamp(fromFade, 0.0, 1.0);
            _toFade = Math.Clamp(toFade, 0.0, 1.0);
            if (!_revealActive) return;
            ReapplyCurrentPlan();
        }

        /// <summary>形变结束：透明度归一化回 1，之后显隐完全由 plan 决定。</summary>
        public void EndMorph()
        {
            if (!_revealActive && _fadingKeys is null) return;
            _revealActive = false;
            _fadingKeys = null;
            ReapplyCurrentPlan();
        }

        /// <summary>
        /// 主元素"收缩"：音乐圆球滑出接管胶囊时把内容层淡掉（保留时间轴的中间态，宿主不插手）。
        /// </summary>
        public void ContractPrimary(double milliseconds)
        {
            if (_primary is null) return;
            _primaryAnimating = true;
            var anim = new DoubleAnimation(_primary.Opacity, 0.0, TimeSpan.FromMilliseconds(milliseconds));
            anim.Completed += (_, _) =>
            {
                _primaryAnimating = false;
                _primary.BeginAnimation(UIElement.OpacityProperty, null);
                ApplyPrimary();
            };
            _primary.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        /// <summary>主元素"绽放"：音乐圆球滑回后内容层淡入。结束时重新问一次判据（旧实现无条件写 1，通知在场时会漏出来）。</summary>
        public void BloomPrimary(double milliseconds)
        {
            if (_primary is null) return;
            _primaryAnimating = true;
            _primary.Visibility = Visibility.Visible;
            var anim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(milliseconds));
            anim.Completed += (_, _) =>
            {
                _primaryAnimating = false;
                _primary.BeginAnimation(UIElement.OpacityProperty, null);
                ApplyPrimary();
            };
            _primary.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        /// <summary>
        /// 清掉主元素上残留的时间轴（换态打断了收缩/绽放时用）。
        /// 必须走这里而不是自己写 <c>BeginAnimation(prop, null)</c>：时钟被外部清掉时
        /// <c>Completed</c> 不会触发，宿主的"动画在跑"标记必须一起复位，否则它再也不接管显隐。
        /// </summary>
        public void CancelPrimaryAnimation()
        {
            if (_primary is null) return;
            _primary.BeginAnimation(UIElement.OpacityProperty, null);
            _primaryAnimating = false;
            ApplyPrimary();
        }

        // ---------- 渲染 ----------
        /// <summary>
        /// 把当前状态刷到 XAML 上（Opacity / Visibility / IsHitTestVisible），并回报可见性给调用方。
        /// 唯一的视觉写手：判断全在 <see cref="NotificationHub.ComputePlan"/>，这里只落地。
        /// </summary>
        public LayerSyncResult Sync(IslandState state, bool clipboardPageActive)
        {
            StagePlan plan = _hub.ComputePlan(state, clipboardPageActive);

            bool anyVisible = false;
            bool anyVisibleExceptGreeting = false;
            bool greetingVisible = false;
            var visibleNow = new HashSet<string>(StringComparer.Ordinal);

            foreach (var key in _order)
            {
                var view = _views[key];
                bool visible = plan.IsVisible(key);

                if (visible != view.LastVisible)
                {
                    view.LastVisible = visible;
                    try
                    {
                        view.OnVisibilityChanged?.Invoke(visible);
                    }
                    catch (Exception ex)
                    {
                        IslandLog.Error("Layer:" + key, ex);
                    }
                }

                Apply(key, visible);

                if (visible)
                {
                    visibleNow.Add(key);
                    anyVisible = true;
                    if (string.Equals(key, GreetingKey, StringComparison.Ordinal))
                        greetingVisible = true;
                    else
                        anyVisibleExceptGreeting = true;
                }
            }

            _appliedVisible = visibleNow;
            ApplyPrimary();

            return new LayerSyncResult
            {
                AnyVisible = anyVisible,
                AnyVisibleExceptGreeting = anyVisibleExceptGreeting,
                GreetingVisible = greetingVisible,
            };
        }

        /// <summary>按缓存的 plan 重刷一遍（形变每帧、形变结束时用），不重算仲裁。</summary>
        private void ReapplyCurrentPlan()
        {
            if (_hub.LastPlan is { } plan)
            {
                foreach (var key in _order)
                    Apply(key, plan.IsVisible(key));
            }

            ApplyPrimary();
        }

        /// <summary>把某一层落地。透明度 = plan 要不要它 × 形变窗口给它的淡入淡出系数。</summary>
        private void Apply(string key, bool planVisible)
        {
            var view = _views[key];

            // 换态前画着、现在 plan 不要了 —— 但形变还没走完，得把余下的亮度画完再灭（旧的"收缩时淡出"）。
            bool fading = _revealActive && _fadingKeys is { } fadingKeys && fadingKeys.Contains(key);
            bool shown = planVisible || fading;
            double factor = shown ? RevealFactor(view.Slot) : 0.0;

            SetVisual(view.Root, shown && factor > 0.005, factor, planVisible);
        }

        private void ApplyPrimary()
        {
            if (_primary is null || _primaryAnimating) return;

            bool want = _primaryCanShow?.Invoke() ?? false;
            bool fading = _revealActive && _primaryVisibleAtMorphStart;
            bool shown = want || fading;
            double factor = shown ? RevealFactor(IslandSlot.Pill) : 0.0;
            bool visible = shown && factor > 0.005;

            SetVisual(_primary, visible, factor, want);

            if (visible == _primaryLastVisible) return;
            _primaryLastVisible = visible;
            try
            {
                _primaryVisibilityChanged?.Invoke(visible);
            }
            catch (Exception ex)
            {
                IslandLog.Error("Layer:Primary", ex);
            }
        }

        /// <summary>
        /// 形变窗口给某一槽位图层的淡入淡出系数。规则与老 <c>RevealLayer</c> 逐字对应：
        ///   to == Pill（展开到胶囊）：from == Pill ? 1 : toFade（淡入）
        ///   to != Pill（离开胶囊）：from == Pill ? 1 - fromFade（淡出）: 0（本来就不该在场）
        /// 非胶囊槽位（卡片/旁路/卫星）不走这套曲线。
        /// </summary>
        private double RevealFactor(IslandSlot slot)
        {
            if (!_revealActive || _revealFrom == _revealTo) return 1.0;
            if (slot != IslandSlot.Pill) return 1.0;

            bool fromPill = _revealFrom == IslandState.Pill;
            bool toPill = _revealTo == IslandState.Pill;
            if (toPill) return fromPill ? 1.0 : _toFade;
            return fromPill ? 1.0 - _fromFade : 0.0;
        }

        private static void SetVisual(UIElement root, bool visible, double opacity, bool hitTest)
        {
            root.Opacity = visible ? opacity : 0.0;
            root.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            root.IsHitTestVisible = visible && hitTest;
        }
    }
}
