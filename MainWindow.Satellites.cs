using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Controls;
using alpha_island.Services;
using Point = System.Windows.Point;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using FontFamily = System.Windows.Media.FontFamily;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Orientation = System.Windows.Controls.Orientation;

namespace alpha_island
{
    /// <summary>
    /// 多岛架构与卫星小岛管理系统。
    /// 解决：
    /// 1. 切换岛时不显示中间的转换/过渡图标，而是清晰展示对应小岛及其专属图标。
    /// 2. 独立置顶：主岛及每一个卫星小岛均可独立右键置顶/取消置顶，在胶囊态稳定置顶，绝不误弹出大卡片，且置顶卡片能随时顺畅收回。
    /// 3. 点击切换：左键点击缩小的小岛，小岛变大成为主岛，原主岛内容缩小为小岛，无损互换。
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>当前在主岛（中央胶囊）展示的内容标识（"music" 或通知图层 key）。</summary>
        private string _activeMainIslandKey = "music";

        /// <summary>动态通知卫星小岛视图字典。</summary>
        private readonly Dictionary<string, Border> _satelliteOrbViews = new(StringComparer.Ordinal);

        /// <summary>左侧当前显示的卫星小岛排序（靠近主岛到远离主岛）。</summary>
        private readonly List<string> _activeSatelliteOrder = new();

        private double FocusSatelliteWidth => _pomodoro.Options.ShowSeconds ? 112 : 138;
        private double SatelliteWidth(string key) => key switch
        {
            "netspeed" => StatusSatelliteWidth,
            "status" => ResourceSatelliteWidth,
            "charging" or ChargingPreviewIslandKey => 100,
            "card/focus" => FocusSatelliteWidth,
            _ when AgentHasDefaultAvatar(key) => 52,
            _ => 36
        };

        /// <summary>小岛切换动画互斥锁，避免快速连击产生状态混乱。</summary>
        private bool _isSwitchingMainIsland;
        private bool _musicIslandDismissed;

        private bool MusicIslandHasMedia => _smtc?.Current is { HasMedia: true,
            State: PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Changing };

        private bool ShouldHidePausedMedia => SettingsRegistry.GetBool(_options, "MediaHideWhenPaused", false)
            && !_isPinned && _smtc?.Current?.State == PlaybackState.Paused
            && !(_upNextKind == "pause" && UpNextActive);
        private bool MusicIslandCanShow => MusicIslandHasMedia && !ShouldHidePausedMedia && !ShouldHideForegroundMedia && !GameDoNotDisturbActive;
        private bool HasVisibleMusicPin => _isPinned && MusicIslandHasMedia && !ShouldHideForegroundMedia && !GameDoNotDisturbActive;

        // Playback alone does not pin the island. Only a live prompt, a manual pin
        // or an in-flight window handoff may keep the compact player open.
        private bool KeepPlayingMusicIslandVisible => !_musicIslandDismissed && MusicIslandCanShow
            && (HasVisibleMusicPin || UpNextActive || _mediaHandoffInFlight);
        private bool CanRetainIsland(string key) => IsIslandActive(key);

        /// <summary>
        /// 判断某个岛当前是否处于活跃状态。
        /// 活跃判定：置顶中，或者有媒体在播放，或者通知处于有效倒计时内。
        /// </summary>
        private bool IsIslandActive(string key)
        {
            // The expanded animation and compact view belong to one battery island.
            if ((key is ChargingIslandKey or ChargingPreviewIslandKey) && (_powerTransitionPreparing || BatteryNotifyActive)) return false;
            if (key == ChargingIslandKey && _layers.IsActive(ChargingPreviewIslandKey)) return false;
            if (_layers.IsPresentationSuppressed(key)) return false;
            if (string.Equals(key, "music", StringComparison.Ordinal))
            {
                // 音乐在有媒体播放、或被独立置顶时才作为卫星或主岛活跃，杜绝无音乐时强行常驻卫星栏
                return !_musicIslandDismissed && MusicIslandCanShow;
            }

            return _layers.IsPinned(key) || _layers.IsActive(key);
        }

        /// <summary>
        /// 主岛与卫星小岛切换（点击小岛变大，原主岛内容缩小为小岛）。
        /// 切换时坚决屏蔽任何中间转换图标，清爽展示小岛及其对应图标。
        /// </summary>
        private void SwitchMainIsland(string targetKey)
        {
            if (_approvalCurrent != null && targetKey != ApprovalLayerKey) return;
            if (targetKey == "music") _musicIslandDismissed = false;
            if (_isSwitchingMainIsland) return;
            if (string.Equals(_activeMainIslandKey, targetKey, StringComparison.Ordinal)) return;

            string prevKey = _activeMainIslandKey;
            _isSwitchingMainIsland = true;
            try
            {
                // 彻底压制中间任何过渡/转换图标，确保切换岛时绝不露出转换图标
                CancelSiriOrb();
                if (SiriOrb != null)
                {
                    AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(SiriOrb,UIElement.OpacityProperty, null);
                    SiriOrb.Opacity = 0;
                }
                if (OrbIconContainer != null)
                {
                    AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(OrbIconContainer,UIElement.OpacityProperty, null);
                    OrbIconContainer.Opacity = 0;
                }

                IslandLog.Debug("MultiIsland", $"主岛切换：[{prevKey}] -> [{targetKey}]（点击小岛展开）");

                // 0. 若切回音乐主岛，立即复位音乐球动画与状态，使 WantPillLayerVisible 立即放行
                if (string.Equals(targetKey, "music", StringComparison.Ordinal))
                {
                    _isMusicOrbShowing = false;
                    _isMusicOrbReturning = false;
                    ResetMusicOrbVisual();
                    if (PillCoverHost != null) PillCoverHost.Opacity = 1;
                }
                else
                {
                    // 若切到通知主岛，续期保证其 Active，防止一进仲裁就被判定过期而被丢弃（已置顶的无需覆盖）
                    EnsureLayerContent(targetKey);
                    if (!_layers.IsPinned(targetKey))
                    {
                        _layers.ShowSeconds(targetKey, 30.0);
                    }
                }

                // 1. 更新当前主岛键与宿主优先 Winner
                _activeMainIslandKey = targetKey;
                _layers.PreferredWinnerKey = targetKey;

                // 2. 调整卫星顺序：新主岛从左侧卫星中移除
                _activeSatelliteOrder.Remove(targetKey);

                // 若旧主岛依然活跃或为音乐，将其保留在卫星小岛列表（最靠近主岛位置），保证能随时切回
                if (CanRetainIsland(prevKey))
                {
                    if (!string.Equals(prevKey, "music", StringComparison.Ordinal) && !_layers.IsPinned(prevKey))
                    {
                        _layers.ShowSeconds(prevKey, 30.0);
                    }
                    if (!_activeSatelliteOrder.Contains(prevKey))
                    {
                        _activeSatelliteOrder.Insert(0, prevKey);
                    }
                }

                // 3. 播放切换动效
                AnimateIslandTransition(prevKey, targetKey);

                // 4. 同步各图层显隐与所有卫星小岛
                SyncNotificationLayers();
                SyncSatelliteOrbs();

                // 5. 胶囊宽度跟随新主岛平滑形变
                TryMorphPillWidth();
            }
            finally
            {
                _isSwitchingMainIsland = false;
            }
        }

        /// <summary>
        /// 确保指定通知图层的 UI 元素内容已填装（如实时音量、网速、下载状态等），防止置顶或切换后界面为空白。
        /// </summary>
        private void EnsureLayerContent(string key)
        {
            if (AgentSession(key) is { } agent) { WriteAgentSession(agent); return; }
            if (CardPageFromPinKey(key) is { } page) { UpdateCardPinSummary(page); return; }
            switch (key)
            {
                case "volume":
                    SyncVolumeCardContent();
                    int vol = _volumeService?.CurrentPercent ?? 50;
                    bool muted = _volumeService?.IsMuted ?? false;
                    if (VolumeText != null) VolumeText.Text = muted ? "静音" : $"{vol}%";
                    if (VolumeIcon != null)
                    {
                        if (muted || vol == 0) VolumeIcon.Text = "\uE74F";
                        else if (vol < 33) VolumeIcon.Text = "\uE993";
                        else if (vol < 66) VolumeIcon.Text = "\uE994";
                        else VolumeIcon.Text = "\uE995";
                    }
                    double trackWidth = VolumeTrackContainer != null && VolumeTrackContainer.ActualWidth > 0 ? VolumeTrackContainer.ActualWidth : 180.0;
                    if (VolumeProgressFill != null)
                    {
                        VolumeProgressFill.Width = Math.Clamp((trackWidth * vol) / 100.0, 0, trackWidth);
                    }
                    break;

                case "status":
                    UpdateResourceStatus(ResourceStatusRates);
                    break;
                case ChargingPreviewIslandKey:
                    SetChargingIslandContent(ChargingPreviewPrimary, ChargingPreviewIslandKey);
                    break;

                case "charging":
                    ChargingPrimary.SetBattery(_chargingStatus);
                    break;
                case "netspeed":
                    var netSample = CurrentNetworkSample;
                    UpdateNetSpeedPrimary(netSample);
                    break;

                case "download":
                    if (DownloadNotifyTitle != null && string.IsNullOrEmpty(DownloadNotifyTitle.Text))
                    {
                        DownloadNotifyTitle.Text = "下载任务";
                    }
                    break;

                case "system":
                    // 系统事件文字由事件源写入，切回卫星时保留原事件说明。
                    break;

                case "battery":
                    SyncBatteryNotifyContent();
                    break;

                case "bluetooth":
                    SyncBtNotifyContent();
                    break;

                case "clipboard":
                    SyncClipboardContent();
                    break;
            }
        }

        /// <summary>
        /// 播放主岛与卫星小岛互换的平滑过渡动画。
        /// </summary>
        private void AnimateIslandTransition(string outgoingKey, string incomingKey)
        {
            // 旧主岛收缩动画
            if (string.Equals(outgoingKey, "music", StringComparison.Ordinal))
            {
                AnimatePillLayerContract();
            }
            else
            {
                FadeOutNotifyLayer(outgoingKey);
            }

            // 新主岛绽放动画
            if (string.Equals(incomingKey, "music", StringComparison.Ordinal))
            {
                AnimatePillLayerBloom();
            }
            else
            {
                AnimateIncomingNotifyLayer(incomingKey);
            }
        }

        private UIElement? GetNotifyLayerElement(string layerKey) => layerKey switch
        {
            "volume" => VolumeNotifyLayer,
            "download" => DownloadNotifyLayer,
            "battery" => BatteryNotifyLayer,
            "bluetooth" => BtNotifyLayer,
            "netspeed" => NetSpeedNotifyLayer,
            "status" => ResourceStatusNotifyLayer,
            "charging" => ChargingNotifyLayer,
            ChargingPreviewIslandKey => ChargingPreviewNotifyLayer,
            "system" => SystemNotifyLayer,
            "clipboard" => ClipboardNotifyLayer,
            "approval" => ApprovalLayer,
            "agentstatus" or "ai" => AgentNotifyLayer,
            _ => AgentSession(layerKey)?.Root ?? (_cardPinViews.TryGetValue(layerKey, out var view) ? view.Root : null)
        };

        private void FadeOutNotifyLayer(string layerKey)
        {
            UIElement? layer = GetNotifyLayerElement(layerKey);
            if (layer == null) return;

            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(layer,UIElement.OpacityProperty, null);
            if (alpha_island.Animation.MotionPolicy.InstantMorph)
            { layer.Opacity = 0; layer.Visibility = Visibility.Collapsed; return; }
            var fadeAnim = new DoubleAnimation(layer.Opacity, 0.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fadeAnim.Completed += (_, _) =>
            {
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(layer,UIElement.OpacityProperty, null);
                layer.Opacity = 0.0;
                layer.Visibility = Visibility.Collapsed;
            };
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(layer,UIElement.OpacityProperty, fadeAnim);
        }

        private void AnimateIncomingNotifyLayer(string layerKey)
        {
            UIElement? layer = GetNotifyLayerElement(layerKey);
            if (layer == null) return;

            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(layer,UIElement.OpacityProperty, null);
            layer.Visibility = Visibility.Visible;
            if (alpha_island.Animation.MotionPolicy.InstantMorph) { layer.Opacity = 1; return; }
            var fadeAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            fadeAnim.Completed += (_, _) =>
            {
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(layer,UIElement.OpacityProperty, null);
                layer.Opacity = 1.0;
            };
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(layer,UIElement.OpacityProperty, fadeAnim);
        }

        /// <summary>
        /// 同步所有左侧卫星小岛的显隐、置顶状态指示与轨道位置。
        /// </summary>
        private void SyncSatelliteOrbs()
        {
            if (GameDoNotDisturbActive) { HideAllSatellites(immediate: true); return; }
            // 欢迎界面期间，绝不显示任何卫星小岛
            bool greetingAny = _layers.IsShowing(IslandLayerHost.GreetingKey) || _layers.IsActive(IslandLayerHost.GreetingKey);
            if (greetingAny)
            {
                HideAllSatellites(immediate: true);
                return;
            }

            // 1. 整理当前所有活跃的候选岛
            var candidateKeys = new HashSet<string>(StringComparer.Ordinal);
            if (IsIslandActive("music"))
            {
                candidateKeys.Add("music");
            }

            foreach (var activeKey in _layers.ActiveKeys)
            {
                if (string.Equals(activeKey, IslandLayerHost.GreetingKey, StringComparison.Ordinal)) continue;
                if (IsIslandActive(activeKey)) candidateKeys.Add(activeKey);
            }
            foreach (var pinnedKey in _layers.PinnedKeys)
            {
                if (IsIslandActive(pinnedKey)) candidateKeys.Add(pinnedKey);
            }

            // 保留当前正在展示的主岛与流转中的卫星小岛
            if (IsIslandActive(_activeMainIslandKey))
            {
                candidateKeys.Add(_activeMainIslandKey);
            }
            foreach (var satKey in _activeSatelliteOrder)
            {
                if (CanRetainIsland(satKey))
                {
                    candidateKeys.Add(satKey);
                }
            }

            // 检查主岛流转：仅当主岛为非置顶且倒计时已结束的临时通知时自动流转；音乐与已置顶的小岛绝不被自动踢出，保障自由互相切换
            bool cardAgents = MultipleAgentCardIslands;
            if (cardAgents) candidateKeys.RemoveWhere(k => !IsAgentIslandKey(k));
            bool mainInactive = !cardAgents && !IsIslandActive(_activeMainIslandKey);

            if (mainInactive)
            {
                string? nextMain = _layers.PinnedKeys.FirstOrDefault(k => candidateKeys.Contains(k));
                if (nextMain == null && _isPinned && IsIslandActive("music")) nextMain = "music";
                if (nextMain == null) nextMain = _activeSatelliteOrder.FirstOrDefault(k => candidateKeys.Contains(k));
                if (nextMain == null && candidateKeys.Contains("music")) nextMain = "music";
                if (nextMain == null && candidateKeys.Count > 0) nextMain = candidateKeys.First();

                if (nextMain != null && !string.Equals(_activeMainIslandKey, nextMain, StringComparison.Ordinal))
                {
                    string oldKey = _activeMainIslandKey;
                    _activeMainIslandKey = nextMain;
                    _layers.PreferredWinnerKey = nextMain;
                    _activeSatelliteOrder.Remove(nextMain);
                    if (CanRetainIsland(oldKey))
                    {
                        if (!_activeSatelliteOrder.Contains(oldKey))
                        {
                            _activeSatelliteOrder.Insert(0, oldKey);
                        }
                    }
                    IslandLog.Debug("MultiIsland", $"主岛超时/置顶流转：下一个接管主岛的是 [{nextMain}]");
                    if (string.Equals(nextMain, "music", StringComparison.Ordinal))
                    {
                        HideMusicOrb(immediate: true);
                        if (_state == IslandState.Pill) AnimatePillLayerBloom();
                    }
                    else
                    {
                        AnimateIslandTransition(oldKey, nextMain);
                    }
                }
            }

            // 2. 当前应该在左侧显示的卫星小岛 = 所有活跃候选岛中，排除掉当前主岛
            var desiredSatellites = candidateKeys.Where(k => cardAgents || !string.Equals(k, _activeMainIslandKey, StringComparison.Ordinal)).ToList();

            // 维持现有排序并同步新增/移除
            _activeSatelliteOrder.RemoveAll(k => !desiredSatellites.Contains(k));
            foreach (var key in desiredSatellites)
            {
                if (!_activeSatelliteOrder.Contains(key))
                {
                    _activeSatelliteOrder.Add(key);
                }
            }

            // 3. 卫星小岛只在胶囊态（Pill）或中转站（Tray）形态展示
            bool showSatellites = (_state == IslandState.Pill || _state == IslandState.Tray || cardAgents);
            if (!showSatellites)
            {
                // 卡片展开时不能把仍可见的小岛留在旧坐标，或让音乐球穿过卡片内容退场。
                HideAllSatellites(immediate: _state == IslandState.Card);
                return;
            }

            RegisterSatelliteRailEntries();

            // 4. 同步音乐卫星小岛（MusicOrbBubble）
            bool musicInSatellite = _activeSatelliteOrder.Contains("music");
            if (musicInSatellite)
            {
                ShowMusicOrb();
                UpdateMusicOrbPinIndicator();
            }
            else
            {
                HideMusicOrb();
            }

            // 5. 确保所有在卫星列表中的通知小岛视图存在并更新图标与置顶状态
            foreach (var key in _activeSatelliteOrder)
            {
                if (string.Equals(key, "music", StringComparison.Ordinal)) continue;

                var orb = EnsureSatelliteOrb(key);
                if (SatelliteOrbAnimator.Show(orb))
                {
                    AnimateOrbSlideOut(key, orb);
                }
                UpdateSatelliteOrbVisual(key, orb);
            }

            // 6. 隐藏不再处于卫星列表的通知小岛并从轨道注销
            foreach (var kvp in _satelliteOrbViews)
            {
                string key = kvp.Key;
                Border orb = kvp.Value;
                if (!_activeSatelliteOrder.Contains(key))
                {
                    AnimateHideSatelliteOrb(orb);
                    _satellites.Unregister("orb-" + key);
                }
            }
            if (!_activeSatelliteOrder.Contains("music"))
            {
                _satellites.Unregister("music-orb");
            }

            // 7. 将当前显示的卫星小岛按顺序在 SatelliteRail 注册并平滑排列
            LayoutSatelliteRail();
        }

        /// <summary>
        /// 利用 SatelliteRail 统管计算所有左侧小岛的 X 坐标，并执行平滑动画排列。
        /// </summary>
        private void RegisterSatelliteRailEntries()
        {
            // 这里只重排本模块的小岛；旁路提示与置顶气泡共享车道，不能一起清除。
            _satellites.Unregister("music-orb");
            foreach (string key in _satelliteOrbViews.Keys)
                _satellites.Unregister("orb-" + key);

            // 1. 先统一定义所有当前活跃小岛的轨道参数
            for (int i = 0; i < _activeSatelliteOrder.Count; i++)
            {
                string key = _activeSatelliteOrder[i];
                string railKey = string.Equals(key, "music", StringComparison.Ordinal) ? "music-orb" : ("orb-" + key);
                double orbWidth = SatelliteWidth(key);

                _satellites.Register(railKey, SatelliteSide.Left,
                    inner: () => CurrentIslandVisualWidth / 2.0 + 8.0,
                    width: () => orbWidth,
                    visible: () => _state == IslandState.Pill || _state == IslandState.Tray || MultipleAgentCardIslands,
                    gap: 8.0);
            }

        }

        private void LayoutSatelliteRail()
        {
            RegisterSatelliteRailEntries();
            // 2. 依次计算目标偏移并驱动平滑动画排列
            for (int i = 0; i < _activeSatelliteOrder.Count; i++)
            {
                string key = _activeSatelliteOrder[i];
                string railKey = string.Equals(key, "music", StringComparison.Ordinal) ? "music-orb" : ("orb-" + key);
                double targetX = _satellites.Offset(railKey);

                if (string.Equals(key, "music", StringComparison.Ordinal))
                {
                    AnimateOrbTranslate(MusicOrbTranslate, targetX);
                }
                else if (_satelliteOrbViews.TryGetValue(key, out var orb) && orb.RenderTransform is TransformGroup tg)
                {
                    if (tg.Children.OfType<TranslateTransform>().FirstOrDefault() is { } tt)
                    {
                        AnimateOrbTranslate(tt, targetX);
                    }
                }
            }
        }

        // Keep the rail attached while the main island changes width. Registration and
        // content stay unchanged; only the visible orb offsets follow this morph frame.
        private void SyncSatelliteOrbOffsets()
        {
            if (_state is not (IslandState.Pill or IslandState.Tray) && !MultipleAgentCardIslands) return;
            foreach (string key in _activeSatelliteOrder)
            {
                string railKey = key == "music" ? "music-orb" : "orb-" + key;
                if (!_satellites.IsVisible(railKey)) continue;
                TranslateTransform? translate = key == "music" ? MusicOrbTranslate
                    : _satelliteOrbViews.TryGetValue(key, out var orb) && orb.Visibility == Visibility.Visible
                        && orb.RenderTransform is TransformGroup group ? group.Children.OfType<TranslateTransform>().FirstOrDefault() : null;
                if (translate == null) continue;
                double target = _satellites.Offset(railKey);
                if (Math.Abs(translate.X - target) < .5) continue;
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(translate,TranslateTransform.XProperty, null);
                translate.X = target;
            }
        }

        /// <summary>
        /// 卫星小岛从主岛向左滑出的流体动画与轻微弹性形变（同音乐圆球效果）。
        /// </summary>
        private void AnimateOrbSlideOut(string key, Border orb)
        {
            string railKey = key == "music" ? "music-orb" : "orb-" + key;
            bool batteryHandoff = key == _powerCompactEmergingKey;
            if (batteryHandoff) _powerCompactEmergingKey = null;
            double startX = batteryHandoff ? 0 : -CurrentIslandVisualWidth / 2.0 + orb.Width * .60;
            SatelliteOrbAnimator.SlideOut(orb, startX, _satellites.Offset(railKey));
        }

        private void AnimateOrbTranslate(TranslateTransform tt, double targetX)
        {
            foreach (var border in _glassBorders)
                if (border.RenderTransform is TransformGroup group && group.Children.Contains(tt))
                { SatelliteOrbAnimator.TranslateTo(border, targetX); return; }
            AnimationReset.Translate(tt); tt.X = targetX;
        }
        /// <summary>
        /// 确保指定图层的卫星小岛 Border 已被创建并挂载到 SatellitesHost。
        /// </summary>
        private Border EnsureSatelliteOrb(string key)
        {
            if (!_satelliteOrbViews.TryGetValue(key, out var orb))
            {
                orb = CreateSatelliteOrb(key);
                _satelliteOrbViews[key] = orb;
            }

            if (SatellitesHost != null && !SatellitesHost.Children.Contains(orb))
            {
                SatellitesHost.Children.Add(orb);
            }

            return orb;
        }

        /// <summary>
        /// 构造液态玻璃卫星；网速和番茄钟使用能放下实时数值的胶囊。
        /// </summary>
        private Border CreateSatelliteOrb(string key)
        {
            bool isNetwork = key is "netspeed" or "status" or "charging" or ChargingPreviewIslandKey;
            double orbWidth = SatelliteWidth(key);
            double orbHeight = isNetwork ? IslandSizing.NetworkSatelliteHeight : AgentHasDefaultAvatar(key) ? 44 : 36.0;

            var orb = new Border
            {
                Width = orbWidth,
                Height = orbHeight,
                CornerRadius = new CornerRadius(orbHeight / 2),
                Margin = isNetwork ? new Thickness(0, IslandMetrics.PillTop + (IslandMetrics.PillHeight - orbHeight) / 2 - 18, 0, 0) : new Thickness(0, (36 - orbHeight) / 2, 0, 0),
                Background = IslandBody.Background,
                BorderBrush = (Brush)FindResource("GlassEdgeBrush"),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Cursor = Cursors.Hand,
                IsHitTestVisible = true,
                RenderTransformOrigin = new Point(0.5, 0.5),
                ClipToBounds = true,
                Opacity = 1.0,
                ToolTip = key is ChargingIslandKey or ChargingPreviewIslandKey
                    ? "电池电量（左键查看系统状态，右键置顶或取消置顶）"
                    : $"{GetSatelliteDisplayName(key)}（左键展开为主岛，右键切换置顶）"
            };

            // Rounded material layers provide depth without pixels outside the island outline.

            var tg = new TransformGroup();
            var st = new ScaleTransform(1.0, 1.0);
            var tt = new TranslateTransform(-148, 0);
            tg.Children.Add(st);
            tg.Children.Add(tt);
            orb.RenderTransform = tg;
            RegisterLiquidChrome(orb);

            var grid = new RoundedClipGrid();
            grid.SetBinding(RoundedClipGrid.CornerRadiusProperty,
                new System.Windows.Data.Binding(nameof(Border.CornerRadius)) { Source = orb });
            grid.SetBinding(RoundedClipGrid.BorderInsetProperty,
                new System.Windows.Data.Binding(nameof(Border.BorderThickness)) { Source = orb });

            if (key == "status")
            {
                var status = new SystemStatusPill(compact: true); UpdateResourceStatus(status); grid.Children.Add(status);
            }
            else if (key is ChargingIslandKey or ChargingPreviewIslandKey)
            {
                var charging = new BatteryChargePill(compact: true); SetChargingIslandContent(charging, key); grid.Children.Add(charging);
            }
            else if (string.Equals(key, "netspeed", StringComparison.Ordinal))
            {
                var rates = new alpha_island.Controls.NetworkSpeedPill(compact: true);
                var sample = CurrentNetworkSample;
                UpdateCombinedStatus(rates, sample);
                grid.Children.Add(rates);
            }
            else
            {
                // 中心专属模块图标
                var iconBlock = new SemanticIcon
                {
                    Name = "OrbGlyph",
                    Width = 26, Height = 26,
                    Text = GetCurrentLayerGlyph(key),
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 14,
                    Foreground = GetLayerBrush(key),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                UpdateSatelliteIconMetadata(key, iconBlock);
                ActionIconMotion.SetKind(iconBlock, SatelliteIconAnimation(key));
                if (key == "card/focus")
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    grid.ColumnDefinitions.Add(new ColumnDefinition());
                    iconBlock.FontSize = 16;
                    iconBlock.Margin = new Thickness(10, 0, 6, 0);
                    var time = new TextBlock
                    {
                        Name = "OrbTime", Text = _pomodoro.DisplayRemaining(), FontSize = 18,
                        FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.White,
                        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 0, 12, 0)
                    };
                    Grid.SetColumn(time, 1); grid.Children.Add(time);
                }
                grid.Children.Add(iconBlock);
                if (AgentSession(key) is { } agent)
                {
                    var badge = new TextBlock { Name = "AgentSessionBadge", FontSize = 8, FontWeight = FontWeights.SemiBold,
                        Text = (agent.Source == "claude-code" ? "Cl" : AgentSourceName(agent.Source)[..1]) + agent.Number, Foreground = System.Windows.Media.Brushes.White,
                        Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(170, 0, 0, 0)),
                        Padding = new Thickness(1, 0, 1, 0), HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 2, 3), IsHitTestVisible = false };
                    grid.Children.Add(badge);
                }
                if (key == DesktopToastKey)
                {
                    var counter = DesktopNotificationCard.ActionButton("", OpenDesktopNotificationStack);
                    counter.Name = "NotificationStackCount"; counter.FontSize = 8; counter.Padding = new Thickness(3, 0, 3, 0);
                    counter.MinHeight = 17; counter.MinWidth = 19; counter.Margin = new Thickness(0, 0, -2, -1);
                    counter.HorizontalAlignment = HorizontalAlignment.Right; counter.VerticalAlignment = VerticalAlignment.Bottom;
                    counter.Visibility = Visibility.Collapsed; System.Windows.Controls.Panel.SetZIndex(counter, 3); grid.Children.Add(counter);
                }
                if (key == DesktopToastKey || key == "bluetooth")
                {
                    var iconImage = new System.Windows.Controls.Image
                    {
                        Name = key == DesktopToastKey ? "OrbAppLogo" : "OrbDeviceArt", Width = 26, Height = 26, Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        Visibility = Visibility.Collapsed, IsHitTestVisible = false
                    };
                    grid.Children.Add(iconImage);
                }
            }

            // 右上角独立置顶指示小图标
            var pinBlock = new AlphaIsland.PluginSdk.GlyphIcon
            {
                Name = "OrbPinIcon",
                Text = "\uE718",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 8,
                Foreground = (Brush)FindResource("AccentCyan"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Margin = new Thickness(0, 3, 3, 0),
                Visibility = Visibility.Collapsed
            };
            Grid.SetColumnSpan(pinBlock, Math.Max(1, grid.ColumnDefinitions.Count));
            grid.Children.Add(pinBlock);

            orb.Child = grid;

            // 鼠标左键点击：变大成为主岛（Preview + MouseLeftButtonDown 双保）
            orb.PreviewMouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                if (!TryOpenMessengerNotification(key) && !TryOpenAgentIsland(key) && !TryOpenBatteryIsland(key)) SwitchMainIsland(key);
            };
            orb.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                if (!TryOpenMessengerNotification(key) && !TryOpenAgentIsland(key) && !TryOpenBatteryIsland(key)) SwitchMainIsland(key);
            };

            // 鼠标右键点击：独立置顶/取消置顶
            orb.PreviewMouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;
                ToggleSatellitePin(key);
            };

            return orb;
        }

        /// <summary>
        /// 更新通知卫星小岛的置顶指示、边框高亮与专属图标。
        /// </summary>
        private void UpdateSatelliteOrbVisual(string key, Border orb)
        {
            bool isPinned = _layers.IsPinned(key);

            if (IsAgentIslandKey(key))
            {
                bool enlarged = AgentHasDefaultAvatar(key);
                orb.Width = SatelliteWidth(key); orb.Height = enlarged ? 44 : 36;
                orb.CornerRadius = new CornerRadius(orb.Height / 2);
                orb.Margin = new Thickness(0, (36 - orb.Height) / 2, 0, 0);
                orb.ToolTip = AgentLabel(key) + " · " + AgentStateTitle(AgentStateFor(key)) + "（左键打开助手，右键置顶）";
                if (orb.Child is Grid agentGrid && agentGrid.Children.OfType<TextBlock>().FirstOrDefault(t => t.Name == "AgentSessionBadge") is { } badge)
                    badge.Visibility = AgentSession(key)?.Icon.Source != null ? Visibility.Collapsed : Visibility.Visible;
            }
            // 边框发光
            orb.BorderBrush = isPinned
                ? (Brush)FindResource("AccentCyan")
                : (Brush)FindResource("GlassEdgeBrush");

            // 置顶图钉图标与中心内容
            if (orb.Child is Grid grid)
            {
                var pinIcon = grid.Children.OfType<AlphaIsland.PluginSdk.GlyphIcon>().FirstOrDefault(t => t.Name == "OrbPinIcon");
                if (pinIcon != null)
                {
                    pinIcon.Visibility = isPinned ? Visibility.Visible : Visibility.Collapsed;
                }

                if (key == "status")
                {
                    orb.Width = ResourceSatelliteWidth;
                    if (grid.Children.OfType<SystemStatusPill>().FirstOrDefault() is { } status) UpdateResourceStatus(status);
                    return;
                }
                if (key is ChargingIslandKey or ChargingPreviewIslandKey)
                {
                    if (grid.Children.OfType<BatteryChargePill>().FirstOrDefault() is { } charging) SetChargingIslandContent(charging, key);
                    if (key == ChargingPreviewIslandKey) orb.ToolTip = isPinned ? "充电提示测试已置顶（右键取消）" : "充电提示测试（右键置顶，默认 8 秒后收回）";
                    return;
                }
                if (string.Equals(key, "netspeed", StringComparison.Ordinal))
                {
                    var sample = CurrentNetworkSample;
                    var rates = grid.Children.OfType<alpha_island.Controls.NetworkSpeedPill>().FirstOrDefault();
                    if (rates != null) UpdateCombinedStatus(rates, sample);
                }
                else
                {
                    if (key == "card/focus")
                    {
                        orb.Width = FocusSatelliteWidth;
                        var time = grid.Children.OfType<TextBlock>().FirstOrDefault(t => t.Name == "OrbTime");
                        if (time != null) time.Text = _pomodoro.DisplayRemaining();
                    }
                    var glyph = grid.Children.OfType<SemanticIcon>().FirstOrDefault(t => t.Name == "OrbGlyph");
                    if (glyph != null)
                    {
                        string nextGlyph = GetCurrentLayerGlyph(key);
                        int? weatherCode = key switch
                        {
                            DesktopToastKey => _notificationSummary.Icon.WeatherCode,
                            "weather" or "card/weather" => _weatherSnapshot?.Code,
                            _ => null,
                        };
                        bool changed = glyph.Text != nextGlyph || glyph.WeatherCode != weatherCode;
                        glyph.Text = nextGlyph;
                        glyph.Foreground = GetLayerBrush(key);
                        UpdateSatelliteIconMetadata(key, glyph);
                        ActionIconMotion.SetKind(glyph, SatelliteIconAnimation(key));
                        if (changed) ActionIconMotion.Play(glyph);
                    }
                    if (key == DesktopToastKey)
                    {
                        if (grid.Children.OfType<System.Windows.Controls.Button>().FirstOrDefault(button => button.Name == "NotificationStackCount") is { } counter)
                        {
                            counter.Content = NotificationStackCount >= 50 ? "50+" : NotificationStackCount.ToString();
                            counter.Visibility = NotificationStackCount > 1 ? Visibility.Visible : Visibility.Collapsed;
                            counter.ToolTip = $"展开 {NotificationStackCount} 条堆叠通知";
                        }
                        var appLogo = grid.Children.OfType<System.Windows.Controls.Image>().FirstOrDefault(i => i.Name == "OrbAppLogo");
                        if (appLogo != null)
                        {
                            bool newNotice = !Equals(appLogo.Tag, _desktopCurrent?.Key);
                            if (newNotice) appLogo.Tag = _desktopCurrent?.Key;
                            appLogo.Source = _notificationSummary.IconImage.Source;
                            appLogo.Visibility = Visibility.Collapsed;
                            if (glyph != null)
                            {
                                glyph.Source = appLogo.Source;
                                glyph.Visibility = Visibility.Visible;
                                if (newNotice) { ActionIconMotion.Stop(glyph); ActionIconMotion.Play(glyph); }
                            }
                        }
                    }
                    else if (key == "bluetooth")
                    {
                        var deviceArt = grid.Children.OfType<System.Windows.Controls.Image>().FirstOrDefault(i => i.Name == "OrbDeviceArt");
                        if (deviceArt != null)
                        {
                            bool changed = deviceArt.Tag is not int revision || revision != _btMotionRevision;
                            if (changed) deviceArt.Tag = _btMotionRevision;
                            deviceArt.Source = BtDeviceArt.Source;
                            deviceArt.Opacity = BtDeviceArt.Opacity;
                            deviceArt.Visibility = Visibility.Collapsed;
                            if (glyph != null)
                            {
                                glyph.Source = deviceArt.Source;
                                glyph.Visibility = Visibility.Visible;
                                ActionIconMotion.SetKind(glyph, ActionIconMotion.GetKind(BtIconContainer));
                                glyph.Opacity = 1;
                                if (changed) { ActionIconMotion.Stop(glyph); ActionIconMotion.Play(glyph); }
                            }
                        }
                    }
                }
            }

            orb.ToolTip = key == DesktopToastKey && _desktopCurrent is { CanOpenApp: true } notice
                && MessengerIdentity.FromNotification(notice) is { } messenger
                ? $"{MessengerIdentity.Name(messenger)}（左键打开应用，右键切换置顶）"
                : $"{GetSatelliteDisplayName(key)}（{(isPinned ? "已独立置顶 · " : "")}左键展开，右键切换置顶）";
        }

        private ActionIconAnimation SatelliteIconAnimation(string key) => IsAgentIslandKey(key) ? AgentMotion(key) : key switch
        {
            "bluetooth" => _btDisplayedConnected ? ActionIconAnimation.Connect : ActionIconAnimation.Disconnect,
            "card/focus" or "download" or "clipboard" or "approval" => ActionIconAnimation.Tick,
            _ => ActionIconAnimation.Pulse,
        };

        private void UpdateSatelliteIconMetadata(string key, SemanticIcon icon)
        {
            if (IsAgentIslandKey(key))
            {
                icon.Shape = IconShape.Agent; icon.Active = AgentStateFor(key) != AgentRunState.Idle;
                ApplyAgentArtwork(key, icon);
                return;
            }
            icon.Shape = key switch
            {
                "bluetooth" => BtDeviceIcon.Shape,
                "volume" => IconShape.Volume,
                "battery" => IconShape.Battery,
                "agentstatus" or "ai" => IconShape.Agent,
                "approval" => IconShape.Approval,
                "clipboard" => IconShape.Clipboard,
                "download" => IconShape.Download,
                "card/focus" => IconShape.Timer,
                "weather" or "card/weather" => IconShape.Weather,
                DesktopToastKey => _notificationSummary.Icon.Shape,
                _ => IconShape.Auto,
            };
            icon.Active = key switch
            {
                "bluetooth" => _btDisplayedConnected,
                "volume" => !(_volumeService?.IsMuted ?? false),
                "battery" => _battery?.Current is { } battery && (battery.IsCharging || battery.IsOnAcPower == true),
                "agentstatus" or "ai" => AgentStatus.IsBusy,
                "card/focus" => _pomodoro.Running,
                DesktopToastKey => _notificationSummary.Icon.Active,
                _ => true,
            };
            icon.Level = key switch
            {
                "volume" => Math.Clamp(_volumeDisplayPercent / 100.0, 0, 1),
                "battery" => _battery?.Current is { Percent: >= 0 } battery ? Math.Clamp(battery.Percent / 100.0, 0, 1) : 1,
                DesktopToastKey => _notificationSummary.Icon.Level,
                _ => 1,
            };
            icon.WeatherCode = key switch
            {
                DesktopToastKey => _notificationSummary.Icon.WeatherCode,
                "weather" or "card/weather" => _weatherSnapshot?.Code,
                _ => null,
            };
        }

        /// <summary>
        /// 实时更新网速卫星小岛的上下行数据。
        /// </summary>
        internal void UpdateNetSpeedSatelliteVisual(NetworkSpeedSample sample)
        {
            if (_satelliteOrbViews.TryGetValue("netspeed", out var orb) && orb.Visibility == Visibility.Visible)
            {
                if (orb.Child is Grid grid)
                {
                    var rates = grid.Children.OfType<alpha_island.Controls.NetworkSpeedPill>().FirstOrDefault();
                    if (rates != null) UpdateCombinedStatus(rates, sample);
                }
            }
        }

        /// <summary>
        /// 更新音乐球 MusicOrbBubble 的置顶指示。
        /// </summary>
        private void UpdateMusicOrbPinIndicator()
        {
            if (MusicOrbBubble == null) return;

            MusicOrbBubble.BorderBrush = _isPinned
                ? (Brush)FindResource("AccentCyan")
                : (Brush)FindResource("GlassEdgeBrush");

            if (MusicOrbPinIcon != null)
            {
                MusicOrbPinIcon.Visibility = _isPinned ? Visibility.Visible : Visibility.Collapsed;
            }

            MusicOrbBubble.ToolTip = $"媒体播放器（{(_isPinned ? "已独立置顶 · " : "")}左键展开，右键切换置顶）";
        }

        /// <summary>
        /// 卫星小岛独立置顶逻辑。
        /// </summary>
        private void ToggleSatellitePin(string key)
        {
            if (key == "music") SetMusicPinned(!_isPinned);
            else SetLayerPinned(key, !_layers.IsPinned(key), promoteToMain: false);
        }

        private void SetMusicPinned(bool pinned, bool promoteToMain = false, bool preserveCard = false)
        {
            if (pinned && !MusicIslandHasMedia) return;
            _isPinned = pinned;
            _musicIslandDismissed = !pinned;
            IslandLog.Debug("Pin", $"音乐独立置顶：{pinned} [pin={(pinned ? "global-on" : "global-off")}]");
            if (pinned && promoteToMain) SwitchMainIsland("music");
            if (!pinned) _activeSatelliteOrder.Remove("music");
            UpdateMusicOrbPinIndicator();
            SyncPausedMediaVisibility();
            if (pinned && HasVisibleMusicPin && _state == IslandState.Bar) SetState(IslandState.Pill);
            TryMorphPillWidth();
            if (!pinned && !(preserveCard && _state == IslandState.Card) && !_layers.AnyPinned && !HasActiveNotification)
            {
                CollapseToBar(force: true);
            }
            ShowPinBubble(pinned, "媒体播放器");
        }

        private void AnimateHideSatelliteOrb(Border orb)
        {
            SatelliteOrbAnimator.Return(orb, -CurrentIslandVisualWidth / 2.0 + orb.Width * .36, MotionPolicy.InstantMorph || MotionPolicy.PowerSave);
        }

        /// <summary>
        /// 隐藏所有卫星小岛。
        /// </summary>
        private void HideAllSatellites(bool immediate = false)
        {
            HideMusicOrb(immediate);

            foreach (var kvp in _satelliteOrbViews)
            {
                var orb = kvp.Value;
                if (immediate)
                {
                    SatelliteOrbAnimator.Hide(orb, immediate: true);
                }
                else
                {
                    AnimateHideSatelliteOrb(orb);
                }
            }
        }

        /// <summary>
        /// 随主岛收回细条（Bar）时，所有左侧卫星小岛平滑向上、向中线收缩入细条并消失。
        /// </summary>
        private void CollapseAllSatellitesWithMainIsland()
        {
            if (alpha_island.Animation.MotionPolicy.InstantMorph || alpha_island.Animation.MotionPolicy.PowerSave)
            { _activeSatelliteOrder.Clear(); HideAllSatellites(immediate: true); return; }
            _activeSatelliteOrder.Clear();
            CollapseMusicOrbWithMainIsland();

            foreach (var kvp in _satelliteOrbViews)
            {
                SatelliteOrbAnimator.Collapse(kvp.Value);
            }
        }

        /// <summary>
        /// 获取对应小岛的实时专属图标（优先使用当前图层的实时图标，如实时音量/电池/设备图标）。
        /// </summary>
        private string GetCurrentLayerGlyph(string key)
        {
            if (IsAgentIslandKey(key)) return AgentGlyph(key);
            return key switch
            {
                "music" => "\uE8D6", // 音乐音符
                "volume" => (VolumeIcon != null && !string.IsNullOrEmpty(VolumeIcon.Text)) ? VolumeIcon.Text : "\uE995",
                "battery" => (BatteryIcon != null && !string.IsNullOrEmpty(BatteryIcon.Text)) ? BatteryIcon.Text : "\uE83F",
                "bluetooth" => (BtDeviceIcon != null && BtDeviceIcon.Visibility == Visibility.Visible && !string.IsNullOrEmpty(BtDeviceIcon.Text))
                    ? BtDeviceIcon.Text
                    : "\uE7F6",
                "download" => "\uE896",    // 下载箭头
                "netspeed" => AlphaIsland.PluginSdk.GlyphArtwork.NetworkTransferGlyph,    // 双向网速
                "status" => "\uE950",
                "charging" or ChargingPreviewIslandKey => "\uE83F",
                "system" => SysNotifyGlyph?.Text ?? "\uE7F4",
                DesktopToastKey => _notificationSummary.Icon.Text,
                "clipboard" => (ClipboardIcon != null && !string.IsNullOrEmpty(ClipboardIcon.Text)) ? ClipboardIcon.Text : "\uE8C8",   // 剪贴板
                "approval" => "\uE73E",    // 审批勾
                "agentstatus" => AgentNotifyGlyph?.Text ?? "\uE99A",
                "ai" => "\uE99A", // AI 助手
                _ => CardPageFromPinKey(key) is { } page ? CardPinGlyph(page) : "\uE700"
            };
        }

        private Brush GetLayerBrush(string key)
        {
            if (IsAgentIslandKey(key)) return AgentBrush(key);
            return key switch
            {
                "download" => new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)),
                "battery" => BatteryIcon.Foreground,
                "charging" or ChargingPreviewIslandKey => System.Windows.Media.Brushes.LimeGreen,
                "volume" => VolumeIcon.Foreground,
                "system" => SysNotifyGlyph.Foreground,
                DesktopToastKey => _notificationSummary.Icon.Foreground,
                "bluetooth" => new SolidColorBrush(Colors.White),
                _ => (Brush)FindResource("AccentCyan")
            };
        }

        private string GetSatelliteDisplayName(string key) => IsAgentIslandKey(key) ? AgentLabel(key) : key switch
        {
            "music" => "媒体播放器",
            "volume" => "系统音量",
            "download" => "下载任务",
            "battery" => "电池状态",
            "bluetooth" => "蓝牙设备",
            "netspeed" => "实时网速",
            "status" => "系统状态",
            "charging" => "充电电量",
            ChargingPreviewIslandKey => "充电提示测试",
            "system" => !string.IsNullOrWhiteSpace(SysNotifyTitle.Text) ? SysNotifyTitle.Text : "系统事件",
            DesktopToastKey => _notificationSummary.Title.Text,
            "clipboard" => "剪贴板",
            "approval" => "权限审批",
            "agentstatus" => "AI 助手",
            _ => CardPinDisplayName(key) ?? key
        };
    }
}
