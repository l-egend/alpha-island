using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;
using alpha_island.Settings;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;

// Gestures (swipe up to collapse) and hover/click hit testing.
namespace alpha_island
{
    public partial class MainWindow
    {
        // ================= 手势（上滑收起） =================

        private void IslandBody_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsCompactIslandDoubleClick(e)) { e.Handled = true; return; }
            if (GameDoNotDisturbActive) { e.Handled = true; return; }
            if (VoiceIslandVisible) { _isSwiping = false; _dragStartedInsideIsland = true; return; }
            _mediaOpenPointer = IsMediaOpenSurface(e.OriginalSource as DependencyObject) ? e.GetPosition(this) : null;
            _clipboardOpenPointer = IsClipboardLinkOpenSurface(e.OriginalSource as DependencyObject) && _clipboard?.CurrentWebLink is Uri link
                ? (e.GetPosition(this), link) : null;
            // 文件条目需要 OLE 拖出，不能同时启动收起手势或剪切板鼠标捕获。
            if (IsDescendantOf(e.OriginalSource as DependencyObject, ComponentHandle)
                || IsDescendantOf(e.OriginalSource as DependencyObject, ApprovalAllowButton)
                || IsDescendantOf(e.OriginalSource as DependencyObject, ApprovalDenyButton)
                || IsDescendantOf(e.OriginalSource as DependencyObject, CardVolumeSliderTrack)
                || IsDescendantOf(e.OriginalSource as DependencyObject, VolumeTrackContainer)
                || IsInsideTransferItem(e.OriginalSource)
                || IsCardControlSource(e.OriginalSource as DependencyObject))
            {
                _isSwiping = false;
                _dragStartedInsideIsland = true;
                return;
            }
            // 卡片停在 AI 页时，按住左键通常是在拖选气泡里的文字：那不是"上滑收起"手势，
            // 不掐掉的话往上拖 12px 就会把整张卡片收成细条，选中的字也一并没了。
            if (_state == IslandState.Card &&
                ((_pages.ActivePage is Plugins.PluginIslandPage pluginPage && IsDescendantOf(e.OriginalSource as DependencyObject, pluginPage.View))
                 || (_pages.ActiveId == IslandPageIds.Notifications && IsDescendantOf(e.OriginalSource as DependencyObject, _notificationsView))))
            {
                _swipeStartPoint = e.GetPosition(this);
                _swipeStartScreenPoint = IslandBody.PointToScreen(e.GetPosition(IslandBody));
                _isSwiping = false;
                _dragStartedInsideIsland = true;
                return;
            }

            _swipeStartPoint = e.GetPosition(this);
            _swipeStartScreenPoint = IslandBody.PointToScreen(e.GetPosition(IslandBody));
            _isSwiping = true;
            _dragStartedInsideIsland = true;
            if (_clipboardLocked)
            {
                IslandBody.CaptureMouse();
            }
        }

        /// <summary>node 是不是落在 ancestor 的视觉子树里（含自身）。用于判断"这一下点在卡片内哪个区域"。</summary>
        private static bool IsDescendantOf(DependencyObject? node, DependencyObject? ancestor)
        {
            if (node == null || ancestor == null) return false;

            DependencyObject? current = node;
            while (current != null)
            {
                if (ReferenceEquals(current, ancestor)) return true;

                // 只有 Visual / Visual3D 才有视觉父级；文本里的 Run 这类 ContentElement 会抛异常，遇到就停。
                if (current is not Visual) return false;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private static bool IsCardControlSource(DependencyObject? node)
        {
            while (node != null)
            {
                if (node is System.Windows.Controls.Primitives.RangeBase
                    or System.Windows.Controls.Primitives.Thumb or System.Windows.Controls.Primitives.ButtonBase
                    or System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.Primitives.Selector
                    or System.Windows.Controls.ScrollViewer or System.Windows.Controls.PasswordBox
                    or System.Windows.Documents.Hyperlink) return true;
                node = node switch
                {
                    Visual => VisualTreeHelper.GetParent(node),
                    ContentElement content => ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent,
                    _ => null
                };
            }
            return false;
        }

        private void IslandBody_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isSwiping && IsCardControlSource(Mouse.Captured as DependencyObject))
            { _isSwiping = false; return; }
            if (_isSwiping && e.LeftButton == MouseButtonState.Pressed)
            {
                Point current = e.GetPosition(this);
                double deltaY = _swipeStartPoint.Y - current.Y;
                if (deltaY > 10)
                {
                    _isSwiping = false;
                    _dragStartedInsideIsland = false;
                    _isPinned = false;
                    _pinnedCardPage = null;
                    HidePinBubble(immediate: true);
                    if (IslandBody.IsMouseCaptured) IslandBody.ReleaseMouseCapture();
                    if (_clipboardLocked)
                    {
                        _clipboard?.DismissCopiedPending();
                    }
                    // 上滑是用户明确要强制收起：解除所有钉子并强制收回细条
                    foreach (var k in _layers.PinnedKeys.ToList())
                    {
                        _layers.SetPinned(k, false);
                    }
                    CollapseToBar(force: true);
                }
            }
        }

        private async void IslandBody_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var openPointer = _mediaOpenPointer;
            var clipboardPointer = _clipboardOpenPointer;
            _clipboardOpenPointer = null;
            _mediaOpenPointer = null;
            _isSwiping = false;
            _dragStartedInsideIsland = false;
            if (IslandBody.IsMouseCaptured)
            {
                IslandBody.ReleaseMouseCapture();
            }
            if (clipboardPointer is { } copied && (e.GetPosition(this) - copied.Start).Length <= 5
                && IsClipboardLinkOpenSurface(e.OriginalSource as DependencyObject) && TryOpenClipboardWebLink(copied.Link))
            {
                e.Handled = true;
                return;
            }
            if (openPointer is Point start && (e.GetPosition(this) - start).Length <= 5
                && IsMediaOpenSurface(e.OriginalSource as DependencyObject))
            {
                e.Handled = true;
                await OpenMediaWindowAsync();
            }
        }

        private void IslandBody_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (GameDoNotDisturbActive) { e.Handled = true; return; }
            if (VoiceIslandVisible) { e.Handled = true; return; }
            // Handle the battery before the parent card's pin gesture in this tunnelling event.
            if (_state == IslandState.Card && _pages.ActiveId == IslandPageIds.System
                && IsDescendantOf(e.OriginalSource as DependencyObject, SystemBatterySummary))
            {
                e.Handled = true;
                if (ChargingIslandAvailable) SetLayerPinned(ChargingIslandKey, !_layers.IsPinned(ChargingIslandKey), promoteToMain: false);
                return;
            }
            // 诊断：右键到底有没有落到岛体上（脚本化验收时用它区分「事件没到」与「到了但没得钉」）。
            // 卡片态多报两个值：当前页与卡片置顶字段（④ 的验收断言就读这两个 ASCII 标记）。
            Point rp = e.GetPosition(this);
            IslandLog.Debug("Pin", $"右键落到岛体（形态 {_state}，窗口内 {rp.X:0.#},{rp.Y:0.#}，可钉图层={PinTargetLayer() ?? "无"}，当前卡片页={_pages.ActiveId ?? "-"}，卡片置顶={_pinnedCardPage ?? "无"}）"
                + $"[pin=click layer={PinTargetLayer() ?? "-"} state={_state} page={_pages.ActiveId ?? "-"} cardPin={_pinnedCardPage ?? "-"}]");

            if (_state == IslandState.Pill)
            {
                e.Handled = true;
                TogglePin();
                return;
            }

            // 卡片态支持右键置顶。
            // 例外：文件传输列表项的右键是"从列表里移除这一项"，那个手势要让给它。
            if (_state == IslandState.Card && !IsInsideTransferItem(e.OriginalSource))
            {
                e.Handled = true;
                TogglePin();
            }
        }

        /// <summary>命中元素的祖先链里有没有文件传输列表项（那些项的右键被用来移除自己）。</summary>
        private static bool IsInsideTransferItem(object? source)
        {
            var node = source as DependencyObject;
            while (node != null)
            {
                if (node is FrameworkElement fe && fe.DataContext is TransferItem) return true;
                node = node is System.Windows.Media.Visual || node is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : null;
            }

            return false;
        }

        /// <summary>置顶提示与主岛右边缘之间的水平间隙（旧固定值 226 = 344/2 + 12 + 84/2，基准宽度下位置不变）。</summary>
        private const double PinBubbleGap = 12.0;

        /// <summary>置顶提示正在播收起动画：这期间不要改它的 X，否则会把收起动画掐掉、气泡卡在屏幕上。</summary>
        private bool _pinBubbleHiding;

        /// <summary>岛体当前的可见宽度：形变进行中取动画器正在写的 Width，而不是滞后的 ActualWidth。</summary>
        private double CurrentIslandVisualWidth =>
            IslandBody != null && IslandBody.Width > 1.0 ? IslandBody.Width : CurrentPillWidth;

        /// <summary>
        /// 置顶提示的目标 X：始终贴在主岛右边缘外侧，带窗口物理边缘防裁切保护。
        /// S4 起这个数字由 <c>SatelliteRail</c> 的"pin"车道算（同车道还有卡片态旁路气泡，
        /// 谁在前面谁靠内），本方法只剩窗口防裁切的夹取 —— 车道里已经包含
        /// 「岛宽/2 + 间隙 + 气泡宽/2」这条老公式。
        /// </summary>
        private double PinBubbleTargetX()
        {
            double target = _satellites.Offset("pin");
            double windowW = ActualWidth > 0 ? ActualWidth : Width;
            double maxAllowed = (windowW / 2.0) - (PinBubble.Width / 2.0) - 8.0;
            return Math.Min(target, maxAllowed);
        }

        /// <summary>
        /// 让置顶提示跟着主岛宽度走。
        /// 按当前岛宽定位置顶气泡，避免长歌词撑宽后发生重叠。
        /// </summary>
        private void SyncPinBubbleOffset()
        {
            if (PinBubble == null || _pinBubbleHiding) return;
            if (PinBubble.Visibility != Visibility.Visible || PinBubble.Opacity <= 0.01) return;

            double target = PinBubbleTargetX();
            if (Math.Abs(PinBubbleTranslate.X - target) < 0.5) return;

            // 宽度形变每帧都会调到这里：直接把动画换成当前值，观感是气泡贴着岛边一起走
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(PinBubbleTranslate,TranslateTransform.XProperty, null);
            PinBubbleTranslate.X = target;
        }

        /// <summary>卡片页级置顶（本轮 ④）钉住的是哪一页；null = 没钉。释放点见 SetState / CollapseToBar / SwitchCardPage。</summary>
        private string? _pinnedCardPage;


        /// <summary>
        /// 右键置顶（三种对象）：
        ///   * **卡片态**（本轮 ④）→ 钉住**这一页**：鼠标移开也不降回胶囊、点岛外也不收，
        ///     再右键一次取消；翻页或离开卡片态自动取消。用户要的就是这个 —— 以前卡片态永远退到整岛锁，
        ///     形态降成胶囊后只剩音乐，观感就是"只会顶置音乐岛"。这张卡对应的通知层若正立着，一并钉住（③A）。
        ///   * 胶囊态且此刻正立着**某一条通知**时 → 钉住**那一层**（每个弹窗各自独立，取消前不超时回收、
        ///     也不被后来的通知抢走）。用户的诉求就是这个：以前只有整岛一个锁，换一条通知观感就变了。
        ///   * 其它（中转站 / 没有通知层的胶囊）→ 老的整岛锁「岛不自动收回」。
        /// </summary>
        private void TogglePin()
        {
            if (_state == IslandState.Pill && _activeMainIslandKey == "battery" && _powerTransitionPending)
            {
                string compact = PowerTransitionCompactKey;
                SetLayerPinned(compact, !_layers.IsPinned(compact), promoteToMain: false);
                return;
            }
            if (_state == IslandState.Card)
            {
                ToggleCardPin();
                return;
            }

            // 优先置顶当前主岛（若当前主岛为通知图层，则独立置顶该通知图层）
            if (!string.IsNullOrEmpty(_activeMainIslandKey) && !string.Equals(_activeMainIslandKey, "music", StringComparison.Ordinal))
            {
                if (!string.Equals(_activeMainIslandKey, IslandLayerHost.GreetingKey, StringComparison.Ordinal))
                {
                    SetLayerPinned(_activeMainIslandKey, !_layers.IsPinned(_activeMainIslandKey));
                    return;
                }
            }

            string? layer = PinTargetLayer();
            if (!string.IsNullOrEmpty(layer))
            {
                SetLayerPinned(layer!, !_layers.IsPinned(layer!));
                return;
            }

            SetMusicPinned(!_isPinned);
        }

        /// <summary>
        /// 卡片页级置顶：右键置顶当前卡片对应的模块小岛（Pill 态），
        /// 随后当光标离开或点击外部时，卡片平滑缩回为常驻置顶的小岛，绝不将 214px 巨卡死锁在屏幕上。
        /// </summary>
        private void ToggleCardPin()
        {
            string page = _pages.ActiveId ?? "";
            if (page.Length == 0) return;

            IslandLog.Debug("Pin", $"卡片右键置顶触发：当前页 {page}");

            _pinnedCardPage = null;

            if (string.Equals(page, IslandPageIds.Music, StringComparison.Ordinal))
            {
                SetMusicPinned(!_isPinned, promoteToMain: true);
                if (_state != IslandState.Bar) SetState(IslandState.Pill);
                SyncNotificationLayers();
                TryMorphPillWidth();
                return;
            }

            string? layer = CardLayerForPage(page);
            if (!string.IsNullOrEmpty(layer))
            {
                bool nowPinned = !_layers.IsPinned(layer!);
                SetLayerPinned(layer!, nowPinned);
                // 先登记要展示的载荷，再展开；暂停隐藏时空胶囊会被状态机直接收回。
                if (nowPinned || _state != IslandState.Bar) SetState(IslandState.Pill);
                return;
            }

            // An unavailable page must never toggle an unrelated module's pin.
            IslandLog.Warn("Pin", $"卡片 {page} 已不可用，保留其他模块的置顶");
        }

        /// <summary>当前是否正钉着眼前的这张卡片（始终放行平滑收缩小岛）。</summary>
        private bool IsCardPagePinned() => false;

        /// <summary>
        /// 页 Id → 该页对应的通知图层 Key。
        /// </summary>
        private string? CardLayerForPage(string pageId) => pageId switch
        {
            IslandPageIds.Volume => "volume",
            IslandPageIds.Download => "download",
            IslandPageIds.NetSpeed => "netspeed",
            IslandPageIds.System => "status",
            IslandPageIds.Notifications => DesktopToastKey,
            IslandPageIds.Clipboard => "clipboard",
            IslandPageIds.Ai => _approvalCurrent != null ? "approval" : "agentstatus",
            _ => EnsureCardPinLayer(pageId)
        };

        /// <summary>卡片页在提示气泡里的短名（直接用页标题，没有就退回页 Id）。</summary>
        private string CardDisplayName() => _pages.ActivePage?.Title ?? _pages.ActiveId ?? "卡片";

        /// <summary>
        /// 钉住 / 取消钉住**某一层**（③A）。右键路由与调试钩子共用这一条路径，
        /// 免得验收脚本走的是另一套语义（改语义时只改这里）。返回钉住后的状态。
        /// </summary>
        private bool SetLayerPinned(string layer, bool pinned, bool promoteToMain = true, bool preserveCard = false)
        {
            if (layer == GameQuietNoticeKey) return false; // The game-entry notice is transient.
            if (pinned)
            {
                EnsureLayerContent(layer);

                // 确保主岛切换到新置顶的小岛，旧主岛若仍活跃则移入左侧卫星栏
                if (promoteToMain && !string.Equals(_activeMainIslandKey, layer, StringComparison.Ordinal))
                {
                    string oldKey = _activeMainIslandKey;
                    _activeMainIslandKey = layer;
                    _layers.PreferredWinnerKey = layer;
                    _activeSatelliteOrder.Remove(layer);
                    if (CanRetainIsland(oldKey))
                    {
                        if (!_activeSatelliteOrder.Contains(oldKey))
                        {
                            _activeSatelliteOrder.Insert(0, oldKey);
                        }
                    }
                    AnimateIslandTransition(oldKey, layer);
                }
                else if (promoteToMain)
                {
                    _layers.PreferredWinnerKey = layer;
                }
            }

            _layers.SetPinned(layer, pinned);
            if (layer == ChargingIslandKey && _chargingStatus is { } battery)
                UpdateChargingIsland(battery, expandWhenIdle: false);
            bool now = _layers.IsPinned(layer);
            if (layer == ChargingPreviewIslandKey) SetChargingIslandContent(ChargingPreviewPrimary, layer);
            IslandLog.Debug("Pin", now
                ? $"右键置顶：图层 {layer} → 已置顶（形态 {_state}）[pin=layer-on]"
                : $"右键置顶：图层 {layer} → 已取消置顶（形态 {_state}）[pin=layer-off]");

            if (!now)
            {
                // 若当前主岛就是此图层，且有其他置顶岛，自动流转到下一个置顶岛
                if (string.Equals(_activeMainIslandKey, layer, StringComparison.Ordinal))
                {
                    string? nextPinned = _layers.PinnedKeys.FirstOrDefault(k => !string.Equals(k, layer, StringComparison.Ordinal));
                    if (nextPinned == null && HasVisibleMusicPin) nextPinned = "music";
                    string oldKey = _activeMainIslandKey;
                    string newTarget = nextPinned ?? "music";
                    _activeMainIslandKey = newTarget;
                    _layers.PreferredWinnerKey = newTarget;
                    if (nextPinned != null) _activeSatelliteOrder.Remove(nextPinned);
                    AnimateIslandTransition(oldKey, newTarget);
                }
            }

            if (_satelliteOrbViews.TryGetValue(layer, out var orb))
            {
                UpdateSatelliteOrbVisual(layer, orb);
            }
            SyncNotificationLayers();
            SyncSatelliteOrbs();
            TryMorphPillWidth();

            if (!now && !(preserveCard && _state == IslandState.Card) && !HasVisibleMusicPin && !_layers.AnyPinned && !HasActiveNotification)
            {
                CollapseToBar(force: !ShouldHidePausedMedia);
            }
            ShowPinBubble(now, LayerDisplayName(layer));
            return now;
        }

        /// <summary>
        /// 右键该钉哪一层：只认**此刻真的画在胶囊格里、且不是欢迎语**的那一层。
        /// 欢迎语排除掉（它不是"弹窗"，钉住它等于按住开场白）；非胶囊态返回 null 走老路径。
        /// 先看 LastWinnerKey 再看记忆，但无论哪条都必须 IsVisible 为真 —— 记忆可能指向 15 秒前那一层。
        /// </summary>
        private string? PinTargetLayer()
        {
            if (_state != IslandState.Pill) return null;

            // 优先检查当前处于主岛的非音乐通知层
            if (!string.IsNullOrEmpty(_activeMainIslandKey) && !string.Equals(_activeMainIslandKey, "music", StringComparison.Ordinal))
            {
                if (!string.Equals(_activeMainIslandKey, IslandLayerHost.GreetingKey, StringComparison.Ordinal)
                    && _layers.IsVisible(_activeMainIslandKey))
                {
                    return _activeMainIslandKey;
                }
            }

            string? key = _layers.LastWinnerKey ?? _layers.MemorableKey;
            if (string.IsNullOrEmpty(key)) return null;
            if (string.Equals(key, IslandLayerHost.GreetingKey, StringComparison.Ordinal)) return null;
            return _layers.IsVisible(key) ? key : null;
        }

        /// <summary>图层 Key → 气泡里的短名（气泡只有 11px 字号，名字越短越好）。</summary>
        private string LayerDisplayName(string key) => IsAgentIslandKey(key) ? AgentLabel(key) : key switch
        {
            "volume" => "音量",
            "battery" => "电量",
            "bluetooth" => "蓝牙",
            "clipboard" => "剪贴板",
            "netspeed" => "网速",
            "status" => "系统状态",
            "charging" => "充电电量",
            "system" => "系统",
            "download" => "下载",
            "agentstatus" => "助手",
            "approval" => "审批",
            _ => CardPinDisplayName(key) ?? key,
        };

        /// <summary>
        /// 置顶气泡。<paramref name="target"/> 非空时带上图层短名（③A），让用户知道钉的是哪一条。
        /// </summary>
        private void ShowPinBubble(bool pinned, string? target = null)
        {
            if (PinBubble == null) return;
            _pinBubbleHiding = false;

            string text = (pinned ? "已置顶" : "已取消") + (string.IsNullOrEmpty(target) ? string.Empty : "·" + target);

            PinBubbleIcon.Text = pinned ? "\uE718" : "\uE77A";
            PinBubbleText.Text = text;
            // XAML 里宽写死 84 且 ClipToBounds=true：带上图层名会被裁掉，所以按字数现算宽度。
            // 安全：SatelliteRail 的"pin"车道宽是 () => PinBubble.Width 现问现答的，改这里不会和车道脱钩。
            PinBubble.Width = Math.Max(84.0, 34.0 + (text.Length * 11.0));
            PinBubbleIcon.Foreground = pinned
                ? (System.Windows.Media.Brush)FindResource("AccentCyan")
                : new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));

            PinBubble.Visibility = Visibility.Visible;

            var easeOut = new QuinticEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(320);

            var moveAnim = new DoubleAnimation(160, PinBubbleTargetX(), duration) { EasingFunction = easeOut };
            var scaleXAnim = new DoubleAnimation(0.4, 1.0, duration) { EasingFunction = easeOut };
            var scaleYAnim = new DoubleAnimation(0.4, 1.0, duration) { EasingFunction = easeOut };
            var opacityAnim = new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = easeOut };

            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(PinBubbleTranslate,TranslateTransform.XProperty, moveAnim);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(PinBubbleScale,ScaleTransform.ScaleXProperty, scaleXAnim);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(PinBubbleScale,ScaleTransform.ScaleYProperty, scaleYAnim);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(PinBubble,UIElement.OpacityProperty, opacityAnim);

            if (_pinBubbleTimer == null)
            {
                _pinBubbleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
                _pinBubbleTimer.Tick += (_, __) =>
                {
                    _pinBubbleTimer.Stop();
                    HidePinBubble();
                };
            }
            _pinBubbleTimer.Stop();
            _pinBubbleTimer.Start();
        }

        private void HidePinBubble(bool immediate = false)
        {
            if (PinBubble == null) return;
            _pinBubbleTimer?.Stop();
            _pinBubbleHiding = true;

            if (immediate)
            {
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(PinBubbleTranslate,TranslateTransform.XProperty, null);
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(PinBubbleScale,ScaleTransform.ScaleXProperty, null);
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(PinBubbleScale,ScaleTransform.ScaleYProperty, null);
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(PinBubble,UIElement.OpacityProperty, null);
                PinBubble.Visibility = Visibility.Collapsed;
                PinBubble.Opacity = 0;
                _pinBubbleHiding = false;
                return;
            }

            var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
            var duration = TimeSpan.FromMilliseconds(260);

            var moveAnim = new DoubleAnimation(PinBubbleTranslate.X, 160, duration) { EasingFunction = easeIn };
            var scaleXAnim = new DoubleAnimation(PinBubbleScale.ScaleX, 0.4, duration) { EasingFunction = easeIn };
            var scaleYAnim = new DoubleAnimation(PinBubbleScale.ScaleY, 0.4, duration) { EasingFunction = easeIn };
            var opacityAnim = new DoubleAnimation(PinBubble.Opacity, 0.0, duration) { EasingFunction = easeIn };

            opacityAnim.Completed += (_, __) =>
            {
                _pinBubbleHiding = false;
                if (PinBubble.Opacity < 0.05)
                {
                    PinBubble.Visibility = Visibility.Collapsed;
                }
            };

            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(PinBubbleTranslate,TranslateTransform.XProperty, moveAnim);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(PinBubbleScale,ScaleTransform.ScaleXProperty, scaleXAnim);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(PinBubbleScale,ScaleTransform.ScaleYProperty, scaleYAnim);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(PinBubble,UIElement.OpacityProperty, opacityAnim);
        }

        // ================= 悬停 / 点击 =================

        private void ExpandIslandFromBar()
        {
            if (GameDoNotDisturbActive) return;
            bool showingNotification = HasActiveNotification || _approvalCurrent != null || _layers.AnyPinned;
            string page = LastOpenedCardPage();
            if (ShouldHideForegroundMedia && !showingNotification && page == IslandPageIds.Music) return;
            _musicIslandDismissed = false;
            // 非媒体页没有常驻胶囊摘要，直接恢复完整页面，避免空白中间态。
            // 媒体保留两级展开；暂停隐藏时直接提供播放控制。
            if (!showingNotification && (page != IslandPageIds.Music || ShouldHidePausedMedia))
                SetState(IslandState.Card, preferredPage: page);
            else
                SetState(IslandState.Pill);
        }

        private void IslandBody_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_exiting || GameDoNotDisturbActive) return;
            if (_approvalCurrent != null) { PresentCurrentApproval(); return; }
            if (_clipboardLocked) return;
            _hoverOutTicks = 0;
            if (_state == IslandState.Bar)
            {
                ExpandIslandFromBar();
            }

        }

        /// <summary>悬停展开一级胶囊，点击进入完整卡片。</summary>
        private void IslandBody_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsCompactIslandDoubleClick(e)) { e.Handled = true; return; }
            if (_exiting || GameDoNotDisturbActive) return;
            if (VoiceIslandVisible) { e.Handled = true; return; }
            if (_state == IslandState.Pill && IsAgentIslandKey(PinTargetLayer()) && TryOpenAgentIsland(PinTargetLayer()))
            {
                e.Handled = true;
                return;
            }
            if (_approvalCurrent != null)
            {
                PresentCurrentApproval();
                e.Handled = true;
                return;
            }
            if (_state == IslandState.Pill && TryOpenMessengerNotification(_activeMainIslandKey))
            {
                e.Handled = true;
                return;
            }
            if (_clipboardLocked) return; // 剪切板常驻期间禁用点击展开卡片手势，仅保留上滑收回手势
            _hoverOutTicks = 0;
            if (_greetingShowing)
            {
                DismissGreeting();
            }
            if (_state == IslandState.Bar) ExpandIslandFromBar();
            else if (_state == IslandState.Pill)
            {
                OpenCardFromPill();
            }
        }

        private void HoverTimer_Tick(object? sender, EventArgs e)
        {
            if (_exiting) return;
            SyncHoverPacing();

            // 实时维护外部前台窗口句柄与屏幕矩形缓存
            try
            {
                IntPtr fg = NativeMethods.GetForegroundWindow();
                var myHwnd = new WindowInteropHelper(this).Handle;
                if (fg != IntPtr.Zero && fg != myHwnd)
                {
                    _lastExternalForegroundHwnd = fg;
                }

                if (IslandBody.ActualWidth > 0 && IslandBody.ActualHeight > 0)
                {
                    var tl = IslandBody.PointToScreen(new Point(0, 0));
                    var br = IslandBody.PointToScreen(new Point(IslandBody.ActualWidth, IslandBody.ActualHeight));
                    var r = new Rect(tl, br);
                    r.Inflate(1, 1);
                    _cachedIslandScreenRect = r;
                    PublishIslandRect(r);
                }
            }
            catch { }

            // Games can consume the low-level hook. Reuse this timer only while a
            // card is open to detect an outside press without another global loop.
            if (_state == IslandState.Card && IsCardControlSource(Mouse.Captured as DependencyObject))
            { _hoverOutTicks = 0; return; }
            if (_compactIslandPress != null) { _hoverOutTicks = 0; return; }
            if (_state == IslandState.Card) PollOutsideCardClick();
            else _outsideCardButtonDown = false;

            if (VoiceIslandVisible || ComponentSwitchGestureActive || _fileDragActive) { _hoverOutTicks = 0; return; }
            if (_isDraggingVolumeSlider
                || IsDescendantOf(System.Windows.Input.Mouse.Captured as DependencyObject, VolumeCardLayer)
                || HoldSelectedComponentCard(IsCursorInHotZone())) { _hoverOutTicks = 0; return; }

            // 调试定态：不参与悬停收回（upnext / bluetooth 例外 —— 它要走真实的提示超时收回）
            if (_forcedState != null
                && !_forcedState.Equals("upnext", StringComparison.OrdinalIgnoreCase)
                && !_forcedState.Equals("bluetooth", StringComparison.OrdinalIgnoreCase)) return;

            // 1. 换曲 / 暂停提示超时检查与自动还原频谱
            if (_upNextShowing && !UpNextActive)
            {
                DismissUpNext();
            }

            // 2. 检查各项通知是否自然超时，超时则清理标志
            if (_greetingShowing && !GreetingActive)
            {
                DismissGreeting();
            }
            if (_layers.ExpireAllStale())
            {
                if (_state == IslandState.Pill && !HasVisibleMusicPin && !_layers.AnyPinned && !HasActiveNotification
                    && !KeepPlayingMusicIslandVisible)
                {
                    CollapseToBar();
                    return;
                }
            }

            // 2.5 胶囊宽度对账。通知层出现/消失、歌词是否允许撑宽，都会改变胶囊的目标宽度
            //     （见 CurrentPillWidth / PillLyricWantsWide）。只要有任何一条通知路径漏了触发
            //     宽度形变，岛体就会带着"长歌词"的宽度把通知层套在里面 —— 用户看到的就是
            //     "弹窗出现后没变回原来的长度，而是在它上面又叠了一个岛"。
            //     这里只做一次廉价对账：目标与当前几何不符才起形变（相符时 TryMorphPillWidth 直接返回）。
            if (_state == IslandState.Pill && _animator != null && !_animator.IsRunning && !_widthOnlyMorph)
            {
                if (Math.Abs(_animator.CurrentGeometry().Width - CurrentPillWidth) > 0.5)
                {
                    TryMorphPillWidth();
                }
            }

            // 3. 统一同步图层互斥显隐与左侧音乐圆球（通知结束时音乐圆球平滑回缩入主岛，PillLayer恢复居中）
            SyncNotificationLayers();

            // 若有任何提示/通知处于活跃期，保持展开不收回
            if (_state != IslandState.Card && ((_upNextShowing && UpNextActive) || HasActiveNotification))
            {
                _hoverOutTicks = 0;
                return;
            }

            // 若用户右键已置顶任意岛（音乐或通知），保持在一级展开形态，不自动收回至细条
            if ((HasVisibleMusicPin || _layers.AnyPinned) && _state == IslandState.Pill)
            {
                _hoverOutTicks = 0;
                return;
            }

            // 文件中转站：若有暂存文件常驻展开，绝不自动收回；无文件时离开热区达到超时，则平滑收回两岛并取消置顶
            if (_state == IslandState.Tray)
            {
                if (_fileTransfer != null && _fileTransfer.Count > 0)
                {
                    _hoverOutTicks = 0;
                    return;
                }

                if (IsCursorInHotZone())
                {
                    _hoverOutTicks = 0;
                    return;
                }

                _hoverOutTicks++;
                if (_hoverOutTicks >= HoverCollapseTicks)
                {
                    if (HasVisibleMusicPin)
                    {
                        SetState(IslandState.Pill);
                    }
                    else
                    {
                        CollapseToBar();
                    }
                    return;
                }
                return;
            }

            if (IsCursorInHotZone())
            {
                _hoverOutTicks = 0;
                if (_state == IslandState.Bar && !_musicIslandDismissed) ExpandIslandFromBar();
                return;
            }

            _hoverOutTicks++;

            if (_state == IslandState.Card && _hoverOutTicks >= HoverDowngradeTicks)
            {
                if (!MusicIslandCanShow && !HasActiveNotification && !_layers.AnyPinned)
                    CollapseToBar();
                else SetState(IslandState.Pill);
                return;
            }

            if (_state == IslandState.Pill && _hoverOutTicks >= HoverCollapseTicks)
            {
                if (HasVisibleMusicPin || _layers.AnyPinned) return;
                // 有待裁决的工具权限审批时不许收：收成细条用户就看不见「允许/拒绝」，agent 只能干等到超时。
                if (_approvalCurrent != null) return;
                // 播放保持与窗口联动共用后台活动的保持判据。
                if (KeepPlayingMusicIslandVisible) return;
                CollapseToBar();
            }
        }

        /// <summary>
        /// 热区 = 当前岛体屏幕矩形 ∪ 顶部细条的固定区域。
        ///
        /// 必须每次现取 GetCursorPos：曾经试过用全局钩子缓存 WM_MOUSEMOVE 的坐标，
        /// 结果程序化移动鼠标根本走不到低级钩子，缓存值永远是启动那一刻的位置。
        ///
        /// 顶部固定区域是必需的：细条只占 y∈[0,10]，而一级胶囊从 y=12 起，
        /// 中间有道缝。鼠标停在缝里时 WPF 会认为"从未进入岛体"，连 MouseLeave 都不发。
        /// </summary>
        private bool IsCursorInHotZone()
        {
            if (MediaSourcesButton.ContextMenu?.IsOpen == true) return true;
            if (!NativeMethods.TryGetCursorPos(out var pt)) return false;
            var cursor = new Point(pt.X, pt.Y);

            if (IsPointOnComponentHandle(pt.X, pt.Y)) return true;
            if (IsPointOnCompactIsland(pt.X, pt.Y)) return true;

            if (IslandBody.ActualWidth > 0 && IslandBody.ActualHeight > 0)
            {
                var tl = IslandBody.PointToScreen(new Point(0, 0));
                var br = IslandBody.PointToScreen(new Point(IslandBody.ActualWidth, IslandBody.ActualHeight));
                var bodyRect = new Rect(tl, br);
                bodyRect.Inflate(4, 4);
                if (bodyRect.Contains(cursor)) return true;
            }

            if (MusicOrbBubble != null && MusicOrbBubble.Visibility == Visibility.Visible && MusicOrbBubble.Opacity > 0.1 && MusicOrbBubble.ActualWidth > 0)
            {
                try
                {
                    var tl = MusicOrbBubble.PointToScreen(new Point(0, 0));
                    var br = MusicOrbBubble.PointToScreen(new Point(MusicOrbBubble.ActualWidth, MusicOrbBubble.ActualHeight));
                    var orbRect = new Rect(tl, br);
                    orbRect.Inflate(4, 4);
                    if (orbRect.Contains(cursor)) return true;
                }
                catch { }
            }

            if (_satelliteOrbViews.Count > 0)
            {
                foreach (var orb in _satelliteOrbViews.Values)
                {
                    if (orb != null && orb.Visibility == Visibility.Visible && orb.Opacity > 0.1 && orb.ActualWidth > 0)
                    {
                        try
                        {
                            var tl = orb.PointToScreen(new Point(0, 0));
                            var br = orb.PointToScreen(new Point(orb.ActualWidth, orb.ActualHeight));
                            var orbRect = new Rect(tl, br);
                            orbRect.Inflate(4, 4);
                            if (orbRect.Contains(cursor)) return true;
                        }
                        catch { }
                    }
                }
            }

            double windowWidth = ActualWidth > 0 ? ActualWidth : Width;
            // 使用承载岛体的实际呈现根；独立呈现源中的控件也能正确计算顶部热区。
            var visualRoot = PresentationSource.FromVisual(IslandBody)?.RootVisual;
            if (visualRoot == null) return false;
            var barTl = visualRoot.PointToScreen(new Point((windowWidth - IslandMetrics.BarWidth) / 2.0, 0));
            var barRect = new Rect(barTl.X, barTl.Y, IslandMetrics.BarWidth, IslandMetrics.BarHeight);
            barRect.Inflate(6, 0);
            return barRect.Contains(cursor);
        }

        /// <summary>钩子回调在钩子线程上 —— 绝不能同步等 UI 线程，否则整机鼠标都卡。</summary>
        private void OnGlobalButtonDown(object? sender, MouseButtonDownEventArgs e)
        {
            if (_exiting) return;

            IntPtr fg = NativeMethods.GetForegroundWindow();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_exiting || _compactIslandPress != null) return;
                // Ignore the opening click and its possible double-click tail while the
                // launcher/menu hands input over to the selected card's new geometry.
                if (_state == IslandState.Card && e.Timestamp <= _componentSelectedTick
                    + System.Windows.Forms.SystemInformation.DoubleClickTime) return;

                if (HandleComponentMenuGlobalClick(e.X, e.Y)) return;

                var myHwnd = new WindowInteropHelper(this).Handle;
                if (fg != IntPtr.Zero && fg != myHwnd)
                {
                    _lastExternalForegroundHwnd = fg;
                }

                bool inside = IsPointInsideIsland(e.X, e.Y);
                if (inside)
                {
                    _dragStartedInsideIsland = true;
                }
                else
                {
                    _dragStartedInsideIsland = false;
                }

                if (_state == IslandState.Bar) return;
                if (_approvalCurrent != null) return; // 阻塞审批只允许显式收起，岛外点击不能隐藏按钮。
                bool insideInteractive = IsPointInsideInteractiveZone(e.X, e.Y);
                if (!insideInteractive)
                {
                    HandleOutsideIslandClick();
                }
            }), DispatcherPriority.Input);
        }

        private bool _outsideCardButtonDown;
        private void PollOutsideCardClick()
        {
            short left = NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON);
            short right = NativeMethods.GetAsyncKeyState(NativeMethods.VK_RBUTTON);
            bool down = ((left | right) & 0x8000) != 0;
            bool pressed = (!_outsideCardButtonDown && down) || ((left | right) & 1) != 0;
            _outsideCardButtonDown = down;
            if (!pressed || ComponentSwitchGestureActive || _fileDragActive || _isDraggingVolumeSlider
                || Environment.TickCount64 - _componentSelectedTick < System.Windows.Forms.SystemInformation.DoubleClickTime
                || _approvalCurrent != null) return;
            if (NativeMethods.TryGetCursorPos(out var point) && !IsPointInsideInteractiveZone(point.X, point.Y))
                HandleOutsideIslandClick();
        }

        private void HandleOutsideIslandClick()
        {
            _componentCardAwaitingPointer = false;
            _hoverOutTicks = 0;
            // 若用户已置顶任意岛（音乐或通知）：点击外部绝不收回至细条。
            // 卡片态平滑降回"置顶小岛"（Pill 态常驻置顶）而不是一大张卡片顶置或塌成细条
            if ((HasVisibleMusicPin || _layers.AnyPinned) && (_state == IslandState.Pill || _state == IslandState.Card))
            {
                if (_state == IslandState.Card) SetState(IslandState.Pill);
                return;
            }

            // 若正在欢迎动画期间，点击外部立即提前结束欢迎并收缩回细条
            if (_greetingShowing || GreetingActive)
            {
                DismissGreeting();
                if (!HasActiveNotification && !KeepPlayingMusicIslandVisible) CollapseToBar();
                return;
            }

            // 若剪切板处于复制锁定常驻状态，点击外部绝不收回至细条
            if (_clipboardLocked)
            {
                // 若处于展开的卡片态，则平滑降回胶囊态继续常驻
                if (_state == IslandState.Card)
                {
                    SetState(IslandState.Pill);
                }
                return;
            }

            // 文件中转站若处于展示状态，点击外部：若有暂存文件保持常驻，绝不被点击外部关闭！
            if (_state == IslandState.Tray)
            {
                if (_fileTransfer != null && _fileTransfer.Count > 0)
                {
                    return;
                }
                if (HasVisibleMusicPin)
                {
                    SetState(IslandState.Pill);
                }
                else
                {
                    CollapseToBar();
                }
                return;
            }

            // Background playback stays compact while the user operates another app.
            // An open detail card still closes back to the compact music island.
            if (KeepPlayingMusicIslandVisible)
            {
                if (_state == IslandState.Card) SetState(IslandState.Pill);
                return;
            }

            // 正在播放通知提示期间，点击外部不打断
            if (HasActiveNotification || UpNextActive)
            {
                if (_state == IslandState.Card) SetState(IslandState.Pill);
                return;
            }

            CollapseToBar();
        }

        /// <summary>
        /// 钩子线程要读"岛的屏幕矩形"（低级钩子的同步回调不能等 UI 线程），而 <c>Rect</c> 是
        /// 32 字节结构体、跨线程直接读会被撕裂（读到一半新一半旧）。所以这里发布**装箱后的
        /// 不可变副本**：引用赋值是原子的，钩子线程读到的永远是某一拍完整的矩形。
        /// </summary>
        private volatile object? _islandRectSnapshot;

        private void PublishIslandRect(Rect rect) => _islandRectSnapshot = rect;

        private bool TryReadIslandRect(out Rect rect)
        {
            if (_islandRectSnapshot is Rect r)
            {
                rect = r;
                return true;
            }

            rect = Rect.Empty;
            return false;
        }

        private bool IsPointInsideIsland(int screenX, int screenY)
        {
            if (!Dispatcher.CheckAccess())
            {
                // 钩子线程：只认 UI 线程发布过的缓存矩形（现取 WPF 几何是 UI 线程的事）。
                return TryReadIslandRect(out Rect cached) && cached.Contains(new Point(screenX, screenY));
            }

            if (IslandBody.ActualWidth <= 0 || IslandBody.ActualHeight <= 0) return false;
            try
            {
                var tl = IslandBody.PointToScreen(new Point(0, 0));
                var br = IslandBody.PointToScreen(new Point(IslandBody.ActualWidth, IslandBody.ActualHeight));
                var rect = new Rect(tl, br);
                rect.Inflate(1, 1);
                _cachedIslandScreenRect = rect;
                PublishIslandRect(rect);
                return rect.Contains(new Point(screenX, screenY));
            }
            catch
            {
                return false;
            }
        }

    }
}
