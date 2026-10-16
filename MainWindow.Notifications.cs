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

// Unified notification layer manager and music orb.
// Moved verbatim out of the former single-file MainWindow.xaml.cs; no logic changes.
namespace alpha_island
{
    public partial class MainWindow
    {
        // ================= 统一通知管理与左侧音乐收缩圆球 =================

        private bool HasActiveNotification => _layers.AnyActive;

        // ================= 叠层取证（C 组⑤ ①） =================
        //
        // S3 之前，一级胶囊内容层 PillLayer 不属于任何注册图层，历史上至少有六处代码各自写它的
        // Opacity/Visibility（形变完成回调、ApplyReveal 的手写互斥、这里的通知同步、音乐球的收缩/绽放、
        // 蓝牙塌回细条…）。任何一处把它点亮，歌词/封面就会压在通知层上。
        // S3 已把它收进图层宿主（构造函数里的 _layers.RegisterPrimary）：写它的只剩 IslandLayerHost 一处，
        // 判据仍是 WantPillLayerVisible()。下面两件取证工具保留 —— 它们是"改造没有回退"的回归依据：
        //   1) _pillLayerWriter：记录最后一次写 PillLayer 显隐的是谁；
        //   2) CheckPillLayerOverlapInvariant()：胶囊与通知层同时可见并持续 ≥250ms 就记一条 WARN。
        // 改造完成后，这条 WARN 必须不再出现。

        /// <summary>最后一次写 PillLayer 显隐的代码位置（回归取证用）。</summary>
        private string _pillLayerWriter = "init";
        private DateTime _pillOverlapSinceUtc = DateTime.MinValue;
        private string _pillOverlapOrigin = "-";
        private bool _pillOverlapWarned;

        /// <summary>
        /// 给 PillLayer 的每个写入点打标记 —— 叠层时日志能直接指出是谁把它点亮的。
        /// <paramref name="log"/> = false 时只更新归属、不写日志：ApplyReveal 每帧都跑，
        /// 它的两个标记（reveal:hide / reveal:reconcile）会把日志刷爆，反过来淹没真正有用的转换。
        /// </summary>
        private void NotePillLayerWriter(string writer, bool log = true)
        {
            if (string.Equals(_pillLayerWriter, writer, StringComparison.Ordinal)) return;
            _pillLayerWriter = writer;
            if (!log) return;
            IslandLog.Debug("Layer:Pill",
                $"一级胶囊层写入者 → {writer}（形态={_state}，opacity={(PillLayer == null ? "-" : PillLayer.Opacity.ToString("0.00"))}，可见={(PillLayer == null ? "-" : PillLayer.Visibility.ToString())}）");
        }

        /// <summary>
        /// 叠层不变量：PillLayer（歌词/封面）与任意通知层同时可见 = 字叠字。
        /// 只记日志、不阻止 —— 与「双封面」哨兵同一个套路。
        /// ≥60ms 的叠层在结束时报一条 DEBUG（含持续时长），≥250ms 就升级成 WARN：
        /// 250ms 以下基本是收缩/绽放的正常过渡，250ms 以上用户就能看见字压字了。
        /// </summary>
        private void CheckPillLayerOverlapInvariant(string origin)
        {
            var pill = PillLayer;
            double pillOpacity = pill?.Opacity ?? 0;
            bool pillOn = pill != null
                          && pill.Visibility == Visibility.Visible
                          && pillOpacity > 0.05;
            if (!pillOn || _layers.LastPlan?.AnyVisible != true)
            {
                if (_pillOverlapSinceUtc != DateTime.MinValue)
                {
                    double endedMs = (DateTime.UtcNow - _pillOverlapSinceUtc).TotalMilliseconds;
                    if (endedMs >= 60)
                    {
                        IslandLog.Debug("Layer:Overlap",
                            $"叠层结束：持续 {endedMs:0}ms（起始触发点={_pillOverlapOrigin}，本次触发点={origin}，"
                            + $"形态={_state}，胶囊写入者={_pillLayerWriter}）");
                    }
                }
                _pillOverlapSinceUtc = DateTime.MinValue;
                _pillOverlapWarned = false;
                return;
            }

            if (_pillOverlapSinceUtc == DateTime.MinValue)
            {
                _pillOverlapSinceUtc = DateTime.UtcNow;
                _pillOverlapOrigin = origin;
                return;
            }

            double heldMs = (DateTime.UtcNow - _pillOverlapSinceUtc).TotalMilliseconds;
            if (heldMs < 250 || _pillOverlapWarned) return;
            _pillOverlapWarned = true;
            IslandLog.Warn("Layer:Overlap",
                $"叠层：一级胶囊层与通知层同时可见（已持续 {heldMs:0}ms，起始触发点={_pillOverlapOrigin}，本次触发点={origin}，形态={_state}，"
                + $"独占图层={_layers.LastWinnerKey ?? "无"}，胶囊写入者={_pillLayerWriter}，"
                + $"胶囊 opacity={pillOpacity:0.00}，音乐球 显示={_isMusicOrbShowing}/回收={_isMusicOrbReturning}，"
                + $"置顶={_isPinned}，有媒体={_smtc?.Current?.HasMedia == true}）");
        }

        /// <summary>
        /// 一级胶囊层（PillLayer）该不该亮 —— **唯一判据**。
        ///
        /// 任何写 PillLayer 显隐的代码都必须问这一句。为什么必须收口：PillLayer 不是注册图层，
        /// 历史上至少有六处各自写它的 Opacity/Visibility（形变完成回调、ApplyReveal 的手写互斥、
        /// 通知同步、音乐球的收缩/绽放、蓝牙塌回细条…），其中几处完全不看 _layers ——
        /// 通知层正独占那 328x32 时被它们点亮，就是"歌词压在通知上"。
        ///
        /// 判据：一级胶囊形态、没有任何通知层要显示、音乐球不在收缩/绽放动画中。
        /// 左侧卫星可以保持活跃；是否占据中央胶囊由当前仲裁结果决定。
        /// </summary>
        private bool WantPillLayerVisible()
        {
            if (_state != IslandState.Pill) return false;
            // 欢迎语在场时，胶囊正中间独占展示欢迎语，音乐图层绝不重叠
            if (_layers.IsShowing(IslandLayerHost.GreetingKey) && _layers.IsActive(IslandLayerHost.GreetingKey)) return false;
            if (_greetingShowing && GreetingActive) return false;
            // 只要当前主岛不是音乐，音乐胶囊就不显示（由对应通知图层占据主岛）
            if (!string.Equals(_activeMainIslandKey, "music", StringComparison.Ordinal)) return false;
            if (_musicIslandDismissed) return false;
            if (!MusicIslandCanShow) return false;
            if (_isMusicOrbShowing || _isMusicOrbReturning) return false;
            return true;
        }

        /// <summary>
        /// 提交一条「占胶囊格」的通知请求，并**在这一处**决定岛体形态（S2：模块不再自己写 SetState）。
        ///
        /// 为什么必须收口：PillOnly 的图层在细条态 Show 会被 <see cref="IslandLayerHost"/> 的 ScopeAllows
        /// 静默挡掉 —— Show 成功、时间窗照走、屏幕上什么都没有（B 组那批"丢提示"）。规则：
        ///   * <c>OpensIsland = false</c>（后台续期）→ 不改形态，保留用户当前选择；
        ///   * 细条态（Bar）→ 展开成胶囊；
        ///   * 卡片态 / 中转站态（Card / Tray）→ **默认不动**（卡片态 deferred：回到胶囊后补显未过期的；
        ///     S4 会在岛体一旁弹小提示），只有请求显式声明 <c>DeferredInCard = false</c>
        ///     （审批这种阻塞动作）才强制切胶囊；
        ///   * 已经是胶囊态 → 什么也不做。
        /// 形态只会被"往上开"，任何通知都不会把岛压回细条。
        /// </summary>
        private bool ShowPillNotification(IslandNotification notification)
        {
            if (!_layers.Hub.CanRequest(notification)) return false;
            if (!GameQuietAllowsNotification(notification.Key))
            {
                // Preserve ongoing tasks/approvals and pinned views without opening or promoting the island.
                if (IsAgentIslandKey(notification.Key) || notification.Key == "approval" || _layers.IsPinned(notification.Key))
                    _layers.Request(notification with { OpensIsland = false }, requestSync: false);
                return false;
            }
            // 续期只更新内容与有效期；新事件才自动接管主岛。
            bool promoteToMain = _layers.Hub.ShouldPromoteToMain(notification);
            // 先登记生命周期，再更新形态和主岛；中途同步必须已经能看见新通知。
            if (!_layers.Request(notification, requestSync: false)) return false;
            // Keep an automatic desktop/boot popup visible for its promised display time.
            // Background activity remains available as a satellite; higher priority alerts can still interrupt.
            if (_desktopCurrent != null && _notificationAutoPill && _activeMainIslandKey == DesktopToastKey
                && DateTimeOffset.Now < _desktopExpires && notification.Key != DesktopToastKey
                && notification.Key != ApprovalLayerKey
                && _layers.Hub.ResolvedPriority(notification.Key) <= _desktopPolicy.Priority(_desktopCurrent))
                promoteToMain = false;
            // 新事件接管主岛，持续刷新的通知保留当前主岛；其它活跃内容进入卫星栏。
            if (promoteToMain
                && (_approvalCurrent == null || notification.Key == ApprovalLayerKey || GameDoNotDisturbActive && GameQuietAllowsNotification(notification.Key)))
            {
                if (!string.Equals(_activeMainIslandKey, notification.Key, StringComparison.Ordinal))
                {
                    string oldKey = _activeMainIslandKey;
                    _activeMainIslandKey = notification.Key;
                    _layers.PreferredWinnerKey = notification.Key;

                    // 若旧主岛仍活跃或是音乐，将其排入卫星小岛最前排，保证能随时点回
                    if (CanRetainIsland(oldKey))
                    {
                        _activeSatelliteOrder.Remove(oldKey);
                        _activeSatelliteOrder.Insert(0, oldKey);
                    }
                    _activeSatelliteOrder.Remove(notification.Key);
                    EnsureLayerContent(notification.Key);
                    AnimateIslandTransition(oldKey, notification.Key);
                }
            }

            // Choose the new content before calculating its pill width. Otherwise a
            // compact battery island opens at its old width, then grows a second time.
            if (notification.OpensIsland && _state != IslandState.Pill
                && (_state == IslandState.Bar || !notification.DeferredInCard)) SetState(IslandState.Pill);
            SyncNotificationLayers();
            return true;
        }

        private void SyncNotificationLayers()
        {
            CompletePowerIslandTransition();
            RestoreAfterChargingPreview();
            foreach (var view in _cardPinViews.Values.ToArray())
                if (view.PageId.StartsWith("plugin:", StringComparison.Ordinal)) UpdateCardPinSummary(view.PageId);
            // 审批的主岛与渲染仲裁保持一致，普通通知只进入卫星栏。
            if (_approvalCurrent != null && !GameDoNotDisturbActive && _activeMainIslandKey != ApprovalLayerKey)
            {
                string oldKey = _activeMainIslandKey;
                if (IsIslandActive(oldKey) && !_activeSatelliteOrder.Contains(oldKey))
                    _activeSatelliteOrder.Insert(0, oldKey);
                _activeMainIslandKey = ApprovalLayerKey;
                _activeSatelliteOrder.Remove(ApprovalLayerKey);
                _layers.PreferredWinnerKey = ApprovalLayerKey;
            }
            // 1. 各通知图层的显隐/命中测试：由注册表统一算完并写回 XAML
            //    （老实现是五个图层各写三行 Opacity/Visibility/IsHitTestVisible）
            // 图层宿主只关心"剪切板页是不是当前页"；页序号会随可用页变化，这里按 Id 比。
            // 先结算独立收起/过期后的主岛，再按新主岛仲裁，避免保留旧一拍的背景与内容。
            SyncSatelliteOrbs();
            var layers = _layers.Sync(_state, _pages.ActiveId == IslandPageIds.Clipboard);
            SyncResourceSampling();

            // 细条上的常驻指示（A 组①网速）：跟通知图层一起结算，只有 Bar 态才画。
            SyncBarIndicators();

            bool showGreeting = layers.GreetingVisible;

            // 2. 音乐图层（PillLayer）：该不该亮由 WantPillLayerVisible() 这**唯一判据**决定。
            //    写它的 Opacity/Visibility/IsHitTestVisible 已经全部交给图层宿主
            //    （构造函数里的 _layers.RegisterPrimary），执行点就是上面那句 _layers.Sync()。
            //    这里只剩"跟着显隐走的非图层动作"：歌词跑马灯的启停。
            //    改造前这里有第四套写手：它不看 ApplyReveal 的老式白名单，也不看形变完成回调，谁后跑谁赢。
            bool showPill = WantPillLayerVisible();
            SetPillLyricScrollRunning(showPill);

            // 3. 卡片态旁路小提示（S4）：卡片态收到"只在胶囊态显示"的通知时不打断卡片，
            //    改在岛右侧挂一个小气泡（几何走 _satellites 车道，和置顶气泡互不重叠）。
            UpdateSideBubbleHint();

            // 4. 若正处于剪切板历史卡片态，同步刷新列表
            if (_state == IslandState.Card && _pages.ActiveId == IslandPageIds.Clipboard)
            {
                SyncClipboardHistoryList();
            }

            SyncVisualizers();

            // C 组④ ①：把「收回球与音乐球不会同时带封面」这条不变量变成可观测的日志断言。
            CheckDoubleCoverInvariant();

            // C 组⑤ ①：PillLayer 与通知层的叠层不变量（谁点亮的会一起写进日志）。
            CheckPillLayerOverlapInvariant("SyncNotificationLayers");
        }

        // ---------- 卡片态旁路小提示（S4） ----------

        /// <summary>小气泡的"收起"位置：和左侧音乐球一样停在岛体左侧之外（等着滑出去）。</summary>
        private const double SideBubbleRestX = -160.0;

        private bool _sideBubbleShowing;

        /// <summary>正在播收起动画：这期间不要改它的 X，否则会把动画掐掉、气泡卡在屏幕上（与 <c>_pinBubbleHiding</c> 同理）。</summary>
        private bool _sideBubbleHiding;
        private string? _sideBubbleComponentKey;
        private string? _sideBubbleMotionEventId;
        private string? _sideBubbleMotionPhase;
        private ImageSource? _sideBubbleMotionArtwork;
        private int _sideBubbleMotionState;
        private Controls.NetworkSpeedPill? _sideNetworkView;
        private Controls.SystemStatusPill? _sideStatusView;
        private Controls.BatteryChargePill? _sideChargingView;
        private string? PageForCompactIsland(string key) => IsAgentIslandKey(key) ? AgentPage(key) : key switch
        {
            "music" => IslandPageIds.Music, "netspeed" => IslandPageIds.NetSpeed, "status" or "charging" or "battery" or ChargingPreviewIslandKey => IslandPageIds.System, "system" => _systemNotifyTargetPage,
            "clipboard" => IslandPageIds.Clipboard, "volume" => IslandPageIds.Volume,
            "download" => IslandPageIds.Download,
            "desktoptoast" or GameQuietNoticeKey => IslandPageIds.Notifications, "approval" => IslandPageIds.Ai,"agentstatus" => AgentActivityPage, _ => CardPageFromPinKey(key)
        };

        private string SideBubbleGlyph(string key) => IsAgentIslandKey(key) ? AgentGlyph(key) : key switch
        {
            "music" => "\uE8D6",
            "download" => "\uE896",   // Download
            "volume" => "\uE767",     // Volume
            "battery" => "\uE83F",    // Battery
            "bluetooth" => "\uE702",  // Bluetooth
            "netspeed" => AlphaIsland.PluginSdk.GlyphArtwork.NetworkTransferGlyph,   // NetSpeed
            "system" => "\uE7F4",     // System hardware
            "clipboard" => "\uE8C8",  // Clipboard
            "approval" => "\uE73E",   // Approval Check
            "agentstatus" or "ai" => "\uE99A", // AI Assistant
            _ => GetCurrentLayerGlyph(key),
        };

        private string SideBubbleLabel(string key) => IsAgentIslandKey(key) ? AgentLabel(key) : key switch
        {
            "music" => "媒体播放器",
            "clipboard" => "剪切板",
            "download" => "下载任务",
            "volume" => "音量",
            "battery" => "电量提醒",
            "bluetooth" => "蓝牙设备",
            "netspeed" => "实时网速",
            "status" => "系统状态",
            "charging" => "电池电量",
            ChargingPreviewIslandKey => "充电小岛测试",
            "system" => "系统状态",
            DesktopToastKey => _notificationSummary.Title.Text,
            "agentstatus" => "助手忙碌",
            _ => CardPinDisplayName(key) ?? "有新提示",
        };

        /// <summary>
        /// 卡片态旁路提示的唯一判据：仲裁算出的"够格但被当前形态挡住"的最高优先级请求（<see cref="StagePlan.DeferredKey"/>）。
        /// 只在卡片态挂；细条态虽也会产出 DeferredKey（如 agentstatus），但那里有常驻指示，再挂气泡是噪声。
        /// </summary>
        private void UpdateSideBubbleHint()
        {
            if (SideBubble == null) return;

            string? deferred = null;
            if (_state == IslandState.Card)
            {
                var candidates = _layers.ActiveKeys.Where(k => k != IslandLayerHost.GreetingKey).ToList();
                if (IsIslandActive("music")) candidates.Add("music");
                deferred = candidates.Distinct().Where(k => PageForCompactIsland(k) != _pages.ActiveId)
                    .OrderByDescending(k => _layers.IsPinned(k) || k == "music" && _isPinned)
                    .ThenByDescending(k => k switch { "approval" => 90, "volume" => 80, "bluetooth" => 76,
                        "battery" => 72, "system" => 68, "download" => 66, "netspeed" => 64, "clipboard" => 56, _ => 40 }).FirstOrDefault();
            }
            if (MultipleAgentCardIslands && IsAgentIslandKey(deferred)) { HideSideBubbleHint(immediate: true); return; }
            if (string.IsNullOrEmpty(deferred))
            {
                HideSideBubbleHint(immediate: _state != IslandState.Card);
                return;
            }

            var request = _layers.Peek(deferred);
            string? requestTitle = request.HasValue ? request.Value.Title : null;
            string title = !string.IsNullOrWhiteSpace(requestTitle) ? requestTitle!
                : deferred == "download" ? DownloadNotifyTitle.Text : SideBubbleLabel(deferred);
            UpdateCompactIslandContent(deferred);
            ShowSideBubbleHint(SideBubbleGlyph(deferred), title, deferred);
        }

        private void ShowSideBubbleHint(string glyph, string title, string key)
        {
            string iconGlyph = key == "music" ? (_lastIsPlaying == true ? "\uE769" : "\uE768")
                : key == "download" ? DownloadNotifyGlyph.Text : glyph;
            ImageSource? artwork = key switch
            {
                DesktopToastKey => _notificationSummary.IconImage.Source,
                "bluetooth" => BtDeviceArt.Source,
                _ => IsAgentIslandKey(key) ? AgentSession(key)?.Icon.Source : SideBubbleArtwork.Source,
            };
            string? eventId = key == DesktopToastKey ? _desktopCurrent?.Key : null;
            int eventState = key == "bluetooth" ? _btMotionRevision
                : IsAgentIslandKey(key) ? (int)AgentStateFor(key) : 0;
            // Focus time and temporary notice text change frequently; only its actual phase is an action.
            string? phase = key == "card/focus" && _cardPinViews.TryGetValue(key, out var focusView)
                ? focusView.MotionDetail : null;
            int? weatherCode = key switch
            {
                DesktopToastKey => _notificationSummary.Icon.WeatherCode,
                "weather" or "card/weather" => _weatherSnapshot?.Code,
                _ => null,
            };
            bool continuingVolume = _sideBubbleShowing && !_sideBubbleHiding
                && _sideBubbleComponentKey == "volume" && key == "volume";
            bool volumeChanged = continuingVolume && (SideBubbleIcon.Active != VolumeIcon.Active
                || Math.Abs(SideBubbleIcon.Level - VolumeIcon.Level) > .001);
            bool newAction = !_sideBubbleShowing || _sideBubbleHiding
                || _sideBubbleComponentKey != key || SideBubbleText.Text != title || SideBubbleIcon.Text != iconGlyph
                || _sideBubbleMotionEventId != eventId || _sideBubbleMotionState != eventState
                || _sideBubbleMotionPhase != phase || !ReferenceEquals(_sideBubbleMotionArtwork, artwork)
                || SideBubbleIcon.WeatherCode != weatherCode;
            // Continuous volume input retargets the current strokes instead of committing
            // the previous target and replaying from a reset state on every pointer move.
            if (newAction && !continuingVolume) ActionIconMotion.Stop(SideBubbleIconSlot);
            _sideBubbleComponentKey = key;
            _sideBubbleMotionEventId = eventId;
            _sideBubbleMotionState = eventState;
            _sideBubbleMotionPhase = phase;
            _sideBubbleMotionArtwork = artwork;
            SideBubbleArtwork.Source = artwork;
            SideBubbleArtwork.Visibility = Visibility.Collapsed;
            SideBubbleArtwork.Opacity = key == "bluetooth" ? BtDeviceArt.Opacity : 1;
            SideBubbleIcon.Visibility = Visibility.Visible;
            SideBubbleIcon.Opacity = key == "bluetooth" ? BtDeviceIcon.Opacity : 1;
            SideBubbleIcon.Text = iconGlyph;
            if (IsAgentIslandKey(key)) ApplyAgentArtwork(key, SideBubbleIcon);
            else
            {
                AgentAvatarMotion.Release(SideBubbleIcon); SideBubbleIcon.Source = artwork;
                SideBubbleIconSlot.Width = SideBubbleIconSlot.Height = 24;
                SideBubbleIcon.Width = SideBubbleIcon.Height = 24;
            }
            SideBubbleIcon.WeatherCode = weatherCode;
            SideBubbleIcon.Shape = key switch
            {
                "bluetooth" => BtDeviceIcon.Shape,
                "music" => Controls.IconShape.PlayPause,
                "volume" => Controls.IconShape.Volume,
                "battery" => Controls.IconShape.Battery,
                "download" => Controls.IconShape.Download,
                "clipboard" => Controls.IconShape.Clipboard,
                "approval" => Controls.IconShape.Approval,
                "card/focus" => Controls.IconShape.Timer,
                "weather" or "card/weather" => Controls.IconShape.Weather,
                _ when IsAgentIslandKey(key) => Controls.IconShape.Agent,
                DesktopToastKey => _notificationSummary.Icon.Shape,
                "system" => SysNotifyGlyph.Shape,
                _ => Controls.IconShape.Auto
            };
            SideBubbleIcon.Active = key switch
            {
                "bluetooth" => _btDisplayedConnected,
                "music" => _lastIsPlaying == true,
                "volume" => VolumeIcon.Active,
                "battery" => BatteryIcon.Active,
                "system" => SysNotifyGlyph.Active,
                DesktopToastKey => _notificationSummary.Icon.Active,
                "card/focus" => _pomodoro.Running,
                _ when IsAgentIslandKey(key) => AgentStateFor(key) != AgentRunState.Idle,
                _ => true
            };
            SideBubbleIcon.Level = key switch
            {
                "volume" => VolumeIcon.Level,
                "battery" => BatteryIcon.Level,
                "system" => SysNotifyGlyph.Level,
                DesktopToastKey => _notificationSummary.Icon.Level,
                _ => 1
            };
            SideBubbleText.Text = title;
            ActionIconMotion.SetKind(SideBubbleIconSlot, key switch
            {
                "bluetooth" => ActionIconMotion.GetKind(BtIconContainer),
                _ when IsAgentIslandKey(key) => AgentMotion(key),
                "card/focus" or "clipboard" or "download" or "approval" or "music" => ActionIconAnimation.Tick,
                _ => ActionIconAnimation.Pulse,
            });

            // 已经挂着：只跟着岛宽挪位置，不重播入场动画
            //（下载提示每 2 秒续期一次，重播会变成一直闪的动画；入场动画没播完时也不能重来，
            //  否则气泡会跳回起点再滑一次 —— 实测启动第一拍会连调两次本方法）。
            if (_sideBubbleShowing && !_sideBubbleHiding && SideBubble.Visibility == Visibility.Visible)
            {
                SyncSideBubbleOffset();
                if (newAction || volumeChanged) ActionIconMotion.Play(SideBubbleIconSlot);
                return;
            }

            IslandLog.Debug("SideBubble", $"旁路提示：{key} → 「{title}」（卡片态不夺形态，X={SideBubbleTargetX():0.0}；车道 {_satellites.Dump()}）");

            _sideBubbleShowing = true;
            _sideBubbleHiding = false;

            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(SideBubble,UIElement.OpacityProperty, null);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(SideBubbleScale,ScaleTransform.ScaleXProperty, null);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(SideBubbleScale,ScaleTransform.ScaleYProperty, null);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(SideBubbleTranslate,TranslateTransform.XProperty, null);
            SideBubble.Visibility = Visibility.Visible;

            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            // 在外侧原位显现，避免从主岛内部滑出时覆盖歌词与卡片内容。
            double targetX = SideBubbleTargetX();
            var move = new DoubleAnimation(targetX - 10, targetX, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = easeOut,
            };
            var scale = new DoubleAnimation(0.6, 1.0, TimeSpan.FromMilliseconds(220)) { EasingFunction = easeOut };
            var fade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180));

            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(SideBubbleTranslate,TranslateTransform.XProperty, move);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(SideBubbleScale,ScaleTransform.ScaleXProperty, scale);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(SideBubbleScale,ScaleTransform.ScaleYProperty, scale);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(SideBubble,UIElement.OpacityProperty, fade);
            if (newAction) ActionIconMotion.Play(SideBubbleIconSlot);
        }

        private void HideSideBubbleHint(bool immediate = false)
        {
            if (SideBubble == null) return;
            ActionIconMotion.Stop(SideBubbleIconSlot);
            if (!_sideBubbleShowing && SideBubble.Visibility != Visibility.Visible) return;

            _sideBubbleShowing = false;
            _sideBubbleComponentKey = null;
            _sideBubbleMotionEventId = null;
            _sideBubbleMotionPhase = null;
            _sideBubbleMotionArtwork = null;
            _sideBubbleMotionState = 0;

            if (immediate)
            {
                _sideBubbleHiding = false;
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(SideBubbleTranslate,TranslateTransform.XProperty, null);
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(SideBubbleScale,ScaleTransform.ScaleXProperty, null);
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(SideBubbleScale,ScaleTransform.ScaleYProperty, null);
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(SideBubble,UIElement.OpacityProperty, null);
                SideBubble.Visibility = Visibility.Collapsed;
                SideBubble.Opacity = 0;
                SideBubbleTranslate.X = SideBubbleRestX;
                return;
            }

            _sideBubbleHiding = true;

            var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
            var move = new DoubleAnimation(SideBubbleTranslate.X, SideBubbleTranslate.X - 10, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = easeIn,
            };
            var scale = new DoubleAnimation(SideBubbleScale.ScaleX, 0.4, TimeSpan.FromMilliseconds(180)) { EasingFunction = easeIn };
            var fade = new DoubleAnimation(SideBubble.Opacity, 0.0, TimeSpan.FromMilliseconds(180)) { EasingFunction = easeIn };

            fade.Completed += (_, _) =>
            {
                SideBubble.Visibility = Visibility.Collapsed;
                SideBubble.Opacity = 0;
                _sideBubbleHiding = false;
            };

            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(SideBubbleTranslate,TranslateTransform.XProperty, move);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(SideBubbleScale,ScaleTransform.ScaleXProperty, scale);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(SideBubbleScale,ScaleTransform.ScaleYProperty, scale);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(SideBubble,UIElement.OpacityProperty, fade);
        }

        /// <summary>
        /// 旁路气泡的目标 X：由卫星车道算（左车道，卡片态那里是空的），再夹一道窗口防裁切。
        /// 注意夹取方向是**左**边缘（气泡在左边，越往外越负）。
        /// </summary>
        private double SideBubbleTargetX()
        {
            double target = _satellites.Offset("side-hint");
            double windowW = ActualWidth > 0 ? ActualWidth : Width;
            double minAllowed = -((windowW / 2.0) - (SideBubble.Width / 2.0) - 8.0);
            return Math.Max(target, minAllowed);
        }

        /// <summary>岛体宽度每帧都在变（长歌词/形变），气泡要贴着岛边一起走 —— 与 <c>SyncPinBubbleOffset()</c> 同拍调用。</summary>
        private void SyncSideBubbleOffset()
        {
            if (SideBubble == null) return;
            if (SideBubble.Visibility != Visibility.Visible || _sideBubbleHiding) return;

            double target = SideBubbleTargetX();
            if (Math.Abs(SideBubbleTranslate.X - target) < 0.5) return;

            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(SideBubbleTranslate,TranslateTransform.XProperty, null);
            SideBubbleTranslate.X = target;
        }

        /// <summary>
        /// 「双封面」回归哨兵（C 组④ ①）。
        /// 带封面的圆形有三处：收回球 <c>SiriOrb</c>、左侧音乐球 <c>MusicOrbBubble</c>、
        /// 以及胶囊左封面 / 卡片封面（这两个是不同形态下的同一位置，不会同时可见）。
        /// 收回球与音乐球的互斥靠两条机制各自保证：图层互斥（收回期间音乐层不占位）
        /// 与 <c>CollapseMusicOrbWithMainIsland()</c>（岛体展开时把音乐球收回去）。
        /// 两者都只在特定时序下生效，一旦以后有人改了其一就会叠加成"双封面"——
        /// 所以这里不阻止，只记一条 WARN，让它在真机日志里留痕。
        /// </summary>
        private void CheckDoubleCoverInvariant()
        {
            bool orbCoverOn = SiriOrb != null && SiriOrb.Opacity > 0.05 && OrbCover.Opacity > 0.5;
            bool musicCoverOn = MusicOrbBubble != null
                                && MusicOrbBubble.Visibility == Visibility.Visible
                                && MusicOrbCover.Opacity > 0.5;
            if (orbCoverOn && musicCoverOn)
            {
                IslandLog.Warn("Cover", $"双封面：收回球与音乐球同时可见（形态={_state}，音乐球状态=显示={_isMusicOrbShowing}/回收={_isMusicOrbReturning}）");
            }
        }

        private void SyncMusicPillContent()
        {
            var current = _smtc?.Current;
            if (current != null && current.HasMedia)
            {
                var art = current.Artwork ?? _lastArt;
                if (art != null)
                {
                    PillCoverBrush.ImageSource = art;
                    if (PillCoverPlaceholder != null) PillCoverPlaceholder.Visibility = Visibility.Collapsed;
                }
                else
                {
                    if (PillCoverBrush.ImageSource == null && PillCoverPlaceholder != null)
                    {
                        PillCoverPlaceholder.Visibility = Visibility.Visible;
                    }
                }

                if (string.IsNullOrWhiteSpace(_pillLyricForce) && !UpNextActive)
                {
                    if (_lyricsDoc != null && _lyricsDoc.HasLyrics && _lyricsDoc.Lines.Count > 0)
                    {
                        var position = _smtc?.EstimatedPosition ?? TimeSpan.Zero;
                        if (_options.LyricsOffsetMs != 0)
                        {
                            position += TimeSpan.FromMilliseconds(_options.LyricsOffsetMs);
                            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
                        }
                        int idx = Math.Clamp(_lyricsDoc.IndexAt(position), 0, _lyricsDoc.Lines.Count - 1);
                        string lineText = _lyricsDoc.Lines[idx].Text;
                        SetPillLyric(string.IsNullOrWhiteSpace(lineText) ? "♪" : lineText);
                    }
                    else
                    {
                        SetPillLyric(current.DisplayTitle);
                    }
                }
            }
            else
            {
                if (PillCoverBrush.ImageSource == null && PillCoverPlaceholder != null)
                {
                    PillCoverPlaceholder.Visibility = Visibility.Visible;
                }
                if (!UpNextActive && string.IsNullOrWhiteSpace(_pillLyricForce))
                {
                    SetPillLyric("Alpha Island");
                }
            }
        }

        private void AnimatePillLayerContract()
        {
            if (PillLayerTranslate == null) return;
            var moveAnim = new DoubleAnimation(0, -22, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(PillLayerTranslate,TranslateTransform.XProperty, moveAnim);

            // 透明度交给宿主：它知道唯一判据（WantPillLayerVisible）与形变窗口，
            // 自己写完还会在动画结束时重问一次判据 —— 旧实现在这里无条件写 0，
            // 通知层在场时也会照写（"歌词压在通知上"的另一条来源）。
            _layers.ContractPrimary(200);
        }

        private void AnimatePillLayerBloom()
        {
            if (PillLayerTranslate == null) return;
            if (!WantPillLayerVisible()) return;

            SyncMusicPillContent();
            if (PillCoverHost != null)
            {
                PillCoverHost.Opacity = 1;
            }

            var moveAnim = new DoubleAnimation(-18, 0, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            moveAnim.Completed += (_, _) =>
            {
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(PillLayerTranslate,TranslateTransform.XProperty, null);
                PillLayerTranslate.X = 0;
            };
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(PillLayerTranslate,TranslateTransform.XProperty, moveAnim);

            // 淡入同样交给宿主：动画结束时它会重问判据，通知层若已独占就保持隐藏。
            _layers.BloomPrimary(240);
        }

        /// <summary>
        /// 一级胶囊内容层（PillLayer）的可见性由图层宿主裁定后回报（S3）：
        /// 变的这一刻只剩"跟着显隐走的附属动作" —— 复位位移、补内容、清歌词缓存。
        /// </summary>
        private void OnPillLayerVisibilityChanged(bool visible)
        {
            NotePillLayerWriter(visible ? "primary:show" : "primary:hide");
            if (!visible) return;

            if (PillCoverHost != null)
            {
                PillCoverHost.Opacity = 1;
            }
            if (PillLayerTranslate != null)
            {
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(PillLayerTranslate,TranslateTransform.XProperty, null);
                PillLayerTranslate.X = 0;
            }
            _lyricIndex = -2;
            SyncMusicPillContent();
        }

        private void ShowMusicOrb()
        {
            if (MusicOrbBubble == null || _greetingShowing || GreetingActive || _isMusicOrbShowing) return;
            ResetMusicOrbVisual();
            _isMusicOrbShowing = true; _isMusicOrbReturning = false;
            var art = _lastArt ?? _smtc?.Current?.Artwork;
            MusicOrbCoverBrush.ImageSource = art;
            MusicOrbCover.Opacity = art != null ? 1 : 0; MusicOrbGradient.Opacity = art != null ? 0 : 1;
            SatelliteOrbAnimator.Show(MusicOrbBubble);
            if (PillCoverHost != null) PillCoverHost.Opacity = 0;
            SatelliteOrbAnimator.SlideOut(MusicOrbBubble, -CurrentIslandVisualWidth / 2 + MusicOrbBubble.Width * .60, _satellites.Offset("music-orb"));
            AnimatePillLayerContract();
        }

        private void HideMusicOrb(bool immediate = false)
        {
            if (MusicOrbBubble == null || (!_isMusicOrbShowing && !_isMusicOrbReturning && MusicOrbBubble.Visibility == Visibility.Collapsed)) return;
            _isMusicOrbShowing = false;
            if (_isMusicOrbReturning && !immediate) return;
            _isMusicOrbReturning = true;
            SatelliteOrbAnimator.Return(MusicOrbBubble, -CurrentIslandVisualWidth / 2 + MusicOrbBubble.Width * .36, immediate, () =>
            {
                _isMusicOrbReturning = false;
                if (_isMusicOrbShowing) return;
                ResetMusicOrbVisual();
                if (PillCoverHost != null) PillCoverHost.Opacity = 1;
                if (!immediate && _state == IslandState.Pill) AnimatePillLayerBloom();
            });
        }
        /// <summary>
        /// 音乐球复位到"藏在主岛封面里"的静止态：摘掉 5 条属性上的动画时钟，再把终值写回本地值。
        ///
        /// 这段复位原来在三条路径上各抄了一遍（隐藏时立即复位 / 滑回原位后 / 随岛收缩后），
        /// 漏一条就会留下一个还挂着的时钟：属性被时钟接管后写本地值**不生效**，
        /// 下一次展开动画会从残值起步（表现为"偶尔从半路冒出来"）。
        /// </summary>
        private void ResetMusicOrbVisual()
        {
            SatelliteOrbAnimator.Hide(MusicOrbBubble, immediate: true);
            AnimationReset.Translate(MusicOrbTranslate);
            AnimationReset.Scale(MusicOrbScale);
            AnimationReset.Opacity(MusicOrbBubble);
            MusicOrbBubble.Visibility = Visibility.Collapsed;
            MusicOrbBubble.Opacity = 0;
            MusicOrbTranslate.X = -148;
            MusicOrbTranslate.Y = 0;
            MusicOrbScale.ScaleX = 1.0;
            MusicOrbScale.ScaleY = 1.0;
        }

        /// <summary>
        /// 当整个灵动岛直接收缩回细条（Bar）时，左侧圆球同步平滑向上、向中线收缩入细条并消失。
        /// </summary>
        private void CollapseMusicOrbWithMainIsland()
        {
            if (MusicOrbBubble == null) return;
            if (!_isMusicOrbShowing && !_isMusicOrbReturning && MusicOrbBubble.Visibility == Visibility.Collapsed) return;
            _isMusicOrbShowing = false;
            _isMusicOrbReturning = false;

            double currentX = MusicOrbTranslate.X;
            var duration = TimeSpan.FromMilliseconds(260);
            var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

            var moveXAnim = new DoubleAnimation(currentX, -65, duration) { EasingFunction = easeIn };
            var moveYAnim = new DoubleAnimation(0, -12, duration) { EasingFunction = easeIn };
            var scaleXAnim = new DoubleAnimation(MusicOrbScale.ScaleX, 0.1, duration) { EasingFunction = easeIn };
            var scaleYAnim = new DoubleAnimation(MusicOrbScale.ScaleY, 0.1, duration) { EasingFunction = easeIn };
            var opacityAnim = new DoubleAnimation(MusicOrbBubble.Opacity, 0.0, duration) { EasingFunction = easeIn };

            opacityAnim.Completed += (_, __) =>
            {
                _isMusicOrbReturning = false;
                if (!_isMusicOrbShowing)
                {
                    ResetMusicOrbVisual();

                    if (PillCoverHost != null)
                    {
                        PillCoverHost.Opacity = 1;
                    }
                }
            };

            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(MusicOrbTranslate,TranslateTransform.XProperty, moveXAnim);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(MusicOrbTranslate,TranslateTransform.YProperty, moveYAnim);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(MusicOrbScale,ScaleTransform.ScaleXProperty, scaleXAnim);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(MusicOrbScale,ScaleTransform.ScaleYProperty, scaleYAnim);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(MusicOrbBubble,UIElement.OpacityProperty, opacityAnim);
        }

        private void MusicOrbBubble_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            SwitchMainIsland("music");
        }

        private void MusicOrbBubble_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ToggleSatellitePin("music");
        }

        private void DismissActiveNotifications()
        {
            _greetingTimer?.Stop();
            _layers.HideAll();
            _clipboardLocked = false;
            _clipboard?.DismissCopiedPending();
            if (_state == IslandState.Tray)
            {
                SetState(IslandState.Pill);
            }
            SyncNotificationLayers();
        }

        private void SyncGreetingContent()
        {
            if (!GreetingActive)
            {
                _layers.Hide(IslandLayerHost.GreetingKey);
            }
            SyncNotificationLayers();
        }

    }
}
