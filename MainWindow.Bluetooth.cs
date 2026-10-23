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

// Bluetooth connection and battery notifications.
namespace alpha_island
{
    public partial class MainWindow
    {
        // ================= 蓝牙通知 =================

        private void OnBluetoothDeviceConnected(object? sender, BluetoothDeviceInfo info)
        {
            SyncDeviceBatteryCard();
            if (_exiting || _sessionLocked || GameDoNotDisturbActive || !_options.BluetoothNotifyEnabled) return;
            if (_greetingShowing && GreetingActive) return;
            ShowBluetoothNotify(info, isConnected: true);
        }

        private void OnBluetoothDeviceDisconnected(object? sender, BluetoothDeviceInfo info)
        {
            SyncDeviceBatteryCard();
            if (_exiting || _sessionLocked || GameDoNotDisturbActive || !_options.BluetoothNotifyEnabled) return;
            ShowBluetoothNotify(info, isConnected: false);
        }

        private string? _btDisplayedDeviceId;
        private bool _btDisplayedConnected;
        private BluetoothDeviceCategory _btDisplayedCategory;
        private int _btMotionRevision;
        private void OnBluetoothBatteryUpdated(object? sender, BluetoothDeviceInfo info)
        {
            SyncDeviceBatteryCard();
            if (_btNotifying && BtNotifyActive && _btDisplayedConnected && info.IsConnected && info.Id == _btDisplayedDeviceId)
            {
                UpdateBluetoothBattery(info.BatteryPercent);
            }
        }

        private void UpdateBluetoothBattery(int? percent)
        {
            BtBatteryIcon.Percent = percent;
            BtBatteryText.Text = BtBatteryIcon.Percent.HasValue ? $"{BtBatteryIcon.Percent}%" : "—";
        }

        /// <summary>
        /// 蓝牙设备连接/断开时显示设备名称、分类图片、短动画、状态和真实电量。
        /// </summary>
        private void ShowBluetoothNotify(BluetoothDeviceInfo info, bool isConnected = true)
        {
            bool newEvent = info.Id != _btDisplayedDeviceId || isConnected != _btDisplayedConnected
                || info.Name != BtDeviceName.Text || info.Category != _btDisplayedCategory || !BtNotifyActive;
            if (newEvent) { _btMotionRevision++; ActionIconMotion.Stop(BtIconContainer); }
            _btDisplayedDeviceId = info.Id;
            _btDisplayedConnected = isConnected;
            _btDisplayedCategory = info.Category;
            // 换设备及断联时立即清除上一个设备的显示，不延续旧电量动画。
            if (newEvent || !isConnected) UpdateBluetoothBattery(null);

            BtDeviceName.Text = info.Name;

            if (isConnected)
            {
                BtStatusText.Text = "已连接";
                BtStatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");
                BtBatteryIcon.Visibility = Visibility.Visible;
                BtBatteryText.Visibility = Visibility.Visible;
                UpdateBluetoothBattery(info.BatteryPercent);
            }
            else
            {
                BtStatusText.Text = "已断开";
                BtStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x55));
                BtBatteryIcon.Visibility = Visibility.Collapsed;
                BtBatteryText.Visibility = Visibility.Collapsed;
            }

            BtDeviceArt.Source = info.Category is BluetoothDeviceCategory.Audio or BluetoothDeviceCategory.Input
                ? alpha_island.Controls.BluetoothDeviceArtwork.For(info.Name) : null;
            BtDeviceArt.Opacity = BtDeviceIcon.Opacity = 1;
            // 二维音频设备图片与键鼠手柄等专属图标共享同一个动画槽。
            switch (info.Category)
            {
                case BluetoothDeviceCategory.Audio:
                    BtDeviceIcon.Text = "\uE702";
                    BtDeviceArt.Visibility = Visibility.Visible;
                    BtDeviceIcon.Visibility = Visibility.Collapsed;
                    BtIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
                    break;
                case BluetoothDeviceCategory.Keyboard:
                    BtDeviceArt.Visibility = Visibility.Collapsed;
                    BtDeviceIcon.Visibility = Visibility.Visible;
                    BtDeviceIcon.Text = "\uE765"; // 键盘
                    BtDeviceIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2));
                    BtIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x28, 0x4A, 0x90, 0xE2));
                    break;
                case BluetoothDeviceCategory.Mouse:
                    BtDeviceArt.Visibility = Visibility.Collapsed;
                    BtDeviceIcon.Visibility = Visibility.Visible;
                    BtDeviceIcon.Text = "\uE962"; // 鼠标
                    BtDeviceIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x50, 0xE3, 0xC2));
                    BtIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x28, 0x50, 0xE3, 0xC2));
                    break;
                case BluetoothDeviceCategory.Gamepad:
                    BtDeviceArt.Visibility = Visibility.Collapsed;
                    BtDeviceIcon.Visibility = Visibility.Visible;
                    BtDeviceIcon.Text = "\uE7FC"; // 游戏手柄
                    BtDeviceIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0x66, 0xFF));
                    BtIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xD0, 0x66, 0xFF));
                    break;
                case BluetoothDeviceCategory.Input:
                    BtDeviceArt.Visibility = Visibility.Collapsed;
                    BtDeviceIcon.Visibility = Visibility.Visible;
                    BtDeviceIcon.Text = "\uE92C"; // 输入设备
                    BtDeviceIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x50, 0xE3, 0xC2));
                    BtIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x28, 0x50, 0xE3, 0xC2));
                    break;
                default:
                    BtDeviceArt.Visibility = Visibility.Collapsed;
                    BtDeviceIcon.Visibility = Visibility.Visible;
                    BtDeviceIcon.Text = "\uE702"; // 蓝牙通用图标
                    BtDeviceIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD7));
                    BtIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x28, 0x00, 0x78, 0xD7));
                    break;
            }

            // Keep the image as a data mirror for satellites; the semantic icon draws the selected
            // device silhouette and progressively draws/erases its connection strokes.
            BtDeviceArt.Visibility = Visibility.Collapsed;
            BtDeviceIcon.Source = BtDeviceArt.Source;
            BtDeviceIcon.Shape = BtDeviceArt.Source != null ? IconShape.Bluetooth : IconShape.Auto;
            BtDeviceIcon.Active = isConnected;
            BtDeviceIcon.Visibility = Visibility.Visible;
            ActionIconMotion.SetKind(BtIconContainer, isConnected ? ActionIconAnimation.Connect : ActionIconAnimation.Disconnect);
            ShowPillNotification(IslandNotification.Seconds("bluetooth", _options.BluetoothNotifySeconds));
            if (newEvent) ActionIconMotion.Play(BtIconContainer);

            if (_state == IslandState.Bar)
            {
                SetState(IslandState.Pill);
            }
            else
            {
                SyncBtNotifyContent();
            }
        }

        private void SyncBtNotifyContent()
        {
            if (!BtNotifyActive) _layers.Hide("bluetooth");
            SyncNotificationLayers();
        }

        /// <summary>
        /// 真实背景玻璃：起一个独立的 <c>WS_EX_NOREDIRECTIONBITMAP</c> Win32 窗口，用
        /// <c>Windows.UI.Composition</c> 的 HostBackdropBrush 让 DWM 真的模糊它背后的桌面，
        /// 再把它压到 overlay（本窗口）的正下方。
        /// 失败就什么都不做 —— 玻璃本色会退回原来的不透明度，观感不受影响。
        /// </summary>
        private void InitBackdrop()
        {
            if (_backdrop != null) return;   // 幂等：构造期已经建过就不再建第二个

            if (MotionPolicy.InstantMorph || MotionPolicy.PowerSave)
            {
                _backdrop = null;
                return;
            }

            try { _backdrop = BackdropWindow.TryCreate(); }
            catch { _backdrop = null; }

            var backdrop = _backdrop;
            if (backdrop == null) return;

            backdrop.SetVisible(false);

            // 初始化隐藏的宿主范围；显示前和形变时同步到实际岛体的屏幕坐标。
            double half = IslandMetrics.CardWidth / 2.0 + BackdropMargin;
            _backdropX = Math.Round(Left + Width / 2.0 - half);
            _backdropY = 0;
            backdrop.SetBounds((int)_backdropX, (int)_backdropY,
                (int)Math.Round(half * 2),
                (int)Math.Round(IslandMetrics.CardTop + IslandMetrics.CardHeightLyrics + BackdropMargin));

            backdrop.PlaceBelow(new WindowInteropHelper(this).Handle);
            // 逐帧回调**不在这里挂**：见 SyncBackdrop()。细条态下 OnBackdropTick 第一句就 return，
            // 挂着它只会让 WPF 一直以刷新率请求帧、永远进不了空闲。
        }

        /// <summary>
        /// 玻璃宿主保留 2px 布局余量。背景材质仅绘制在圆角精灵内，
        /// 窗口矩形本身不再绘制亚克力底色，余量及四角保持透明。
        /// </summary>
        private const double BackdropMargin = 2;

        private double _backdropX, _backdropY;


        private void OnBackdropTick(object? sender, EventArgs e) => RenderLiquidBackdrop();

        /// <summary>形态切换时同步玻璃窗的显隐；细条态没必要让它露着。</summary>
        private void SyncBackdrop() => SyncLiquidBackdrop();

        /// <summary>创建尝试只做一次：驱动/系统拒绝时不必每次 ApplyOptions 都重试。</summary>
        private bool _backdropTried;

        /// <summary>
        /// 默认创建真实 HostBackdrop 玻璃窗；削减动效或硬件回退时隐藏并使用纯渐变。
        /// </summary>
        private void EnsureBackdrop()
        {
            if (MotionPolicy.InstantMorph || MotionPolicy.PowerSave)
            {
                _backdrop?.SetVisible(false);
                return;
            }

            if (_backdrop == null)
            {
                if (_backdropTried) return;
                _backdropTried = true;
                InitBackdrop();
                return;
            }

            SyncBackdrop();
        }

        /// <summary>
        /// 只在调试定态里用（把某一层瞬间按到 0 / 1）。ms &lt;= 0 表示直接落值。
        /// 正常形态切换一律走 ApplyReveal —— 那边的透明度由主弹簧进度驱动，不生成动画。
        /// </summary>
        private static void FadeLayer(UIElement element, double to, int ms, int beginTime = 0)
        {
            if (ms <= 0)
            {
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(element,OpacityProperty, null);
                element.Opacity = to;
                return;
            }

            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(element,OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                BeginTime = TimeSpan.FromMilliseconds(beginTime)
            });
        }

        /// <summary>按当前形态决定哪个可视化控件在渲染；不活跃的一律停渲染并清空。</summary>
        private void SyncVisualizers()
        {
            // AllowsFrameLoop()：细条态一律不挂逐帧循环（节能模式的显式契约）。
            // 这里的 _state == Pill / Card 本来就排除了细条态，写成一句是为了以后新增
            // 可视化控件时不用再想"收起态要不要跑"——照抄这个 && 就行。
            bool on = _options.VisualizerEnabled && MusicIslandHasMedia && AllowsFrameLoop();
            bool pillActive = on && WantPillLayerVisible() && !UpNextActive && !(_btNotifying && BtNotifyActive);
            SetActive(PillSpectrum, pillActive);
            SetActive(CardWave, on && _state == IslandState.Card && _pages.ActiveId == IslandPageIds.Music && _options.ShowWaveLayer);
            SyncSpectrumWork();
        }

        private void SyncSpectrumWork()
        {
            if (_audio != null) _audio.VisualizationEnabled =
                (PillSpectrum.IsActive && PillSpectrum.IsVisible) || (CardWave.IsActive && CardWave.IsVisible);
            SyncGlassLightPacing();
        }

        private static void SetActive(SpectrumVisualizer v, bool active)
        {
            if (v.IsActive == active) return;
            v.IsActive = active;
            if (!active) v.Clear();
        }

        private static void SetActive(WaveVisualizer v, bool active)
        {
            if (v.IsActive == active) return;
            v.IsActive = active;
            if (!active) v.Clear();
        }

        /// <summary>
        /// 收回细条。一条主弹簧跑完全程，途中强制经过"正圆"这个路径点：
        ///   卡片/胶囊 --(塌向球心)--> 46px 正圆 --(吸顶 + 摊平)--> 130x10 细条
        /// 编排为优雅的两段式形变，正圆中转期展示专属状态图标/封面球 (SiriOrb)。
        ///
        /// <paramref name="force"/> = false（默认）时，只要有图层被置顶就不收（③A）：
        /// 置顶的层只在胶囊态画得出来，收成细条等于把用户钉住的东西藏起来。
        /// 显式手势（下滑收起）传 force: true —— 用户自己的动作永远优先。
        /// </summary>
        private void CollapseToBar(bool force = false)
        {
            if (_exiting) return;

            if (!force && (_clipboardLocked || HasVisibleMusicPin || _layers.AnyPinned || IsCardPagePinned()))
            {
                IslandLog.Debug("Pin", $"有置顶（音乐={_isPinned}，图层={( _layers.AnyPinned ? string.Join("/", _layers.PinnedKeys) : "无")}），跳过本次自动收回[pin=hold]");
                return;
            }

            var actionType = GetCurrentActionType();
            PrepareSiriOrbContent(actionType);

            IslandState prev = _state;
            RememberOpenCardPage();
            _state = IslandState.Bar;

            // 开一段形变窗口（S3）：这条路径不走 SetState，得自己告诉宿主"要塌回收细条了"。
            // 必须紧跟换态：下面那些 _layers.Hide(...) 每个都会立刻触发一次 Sync，
            // 那时若窗口还没开，宿主会按"Bar 态没有任何图层要显示"把内容层**瞬间**掐灭 ——
            // 实测就是"淡出被跳过后 3ms 内 show→hide"的一帧闪。
            _layers.BeginMorph(prev, IslandState.Bar);
            _hoverOutTicks = 0;
            // Game mode temporarily hides the island; every collapse path must
            // retain the user's music pin until normal presentation resumes.
            if (!GameDoNotDisturbActive) _isPinned = false;
            HidePinBubble(immediate: true);
            CollapseAllSatellitesWithMainIsland();
            _upNextTimer?.Stop();
            CompleteGreetingLifetime();

            // 收回意味着换曲提示结束：把胶囊内容还原，下次展开不会还挂着"接下来"
            _upNextUntil = DateTime.MinValue;
            _upNextShowing = false;
            SyncUpNextContent();

            // 启动问候重置
            _layers.Hide(IslandLayerHost.GreetingKey);
            GreetingLayer.Opacity = 0;
            GreetingLayer.Visibility = Visibility.Collapsed;
            GreetingLayer.IsHitTestVisible = false;

            // 蓝牙通知也随收回重置
            _layers.Hide("bluetooth");
            ActionIconMotion.Stop(BtIconContainer);
            BtNotifyLayer.IsHitTestVisible = false;

            // 电池与剪切板通知随收回重置
            _layers.Hide("battery");
            BatteryNotifyLayer.IsHitTestVisible = false;

            _clipboardPastedUntil = DateTime.MinValue;
            _layers.Hide("clipboard");
            _clipboardLocked = false;
            ClipboardNotifyLayer.IsHitTestVisible = false;

            // 音量通知重置
            _layers.Hide("volume");
            VolumeNotifyLayer.IsHitTestVisible = false;

            FileTransferLayer.IsHitTestVisible = false;

            // 形变窗口已在换态那一刻开好（见本方法开头）——这里只交当前进度。
            _revealFrom = prev;
            _revealTo = IslandState.Bar;
            _reveal = 0.0;
            ApplyReveal();

            // 主元素的淡出由宿主的形变窗口随弹簧进度驱动，
            // 此处仅停止任何残余的独立动画，不立即隐藏内容层（时钟清理走宿主，避免标记不同步）。
            _layers.CancelPrimaryAnimation();
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(PillLayerTranslate,TranslateTransform.XProperty, null);
            if (PillCoverHost != null)
            {
                PillCoverHost.Opacity = 1;
            }

            // 卡片是本次收回的源层时不能提前折叠：它要留在屏幕上被 ApplyReveal 压到 0，
            // 否则收回的第一帧卡片内容直接消失（用户看到的"小岛返回时闪一下"）。
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(CardCarouselHost,UIElement.OpacityProperty, null);
            if (prev != IslandState.Card)
            {
                CardCarouselHost.Opacity = 0;
            }
            CardCarouselHost.IsHitTestVisible = false;
            CardLayer.IsHitTestVisible = false;
            ClipboardCardLayer.IsHitTestVisible = false;
            ClipboardCardLayer.Opacity = 0;
            SetActive(PillSpectrum, false);
            SetActive(CardWave, false);
            StopKaraoke();
            _backdrop?.SetVisible(false);

            var animator = _animator;
            if (animator == null)
            {
                _reveal = 1.0;
                ApplyReveal();
                return;
            }

            const double circle = IslandMetrics.CircleSize;
            var via = new IslandGeometry(circle, circle, IslandMetrics.CardTop,
                new CornerRadius(circle / 2));

            MorphIsland(BarGeometry, IslandMetrics.CollapseDuration,
                new CubicEase { EasingMode = EasingMode.EaseInOut },
                completed: () =>
                {
                    NotePillLayerWriter("bluetooth:collapse");
                    // 主元素的显隐不再在这里收尾：形变结束时宿主会 EndMorph() 并按 plan 归一化
                    // （MorphIsland 的完成回调里统一调用），这里只剩与图层无关的收尾。
                    if (PillLayerScale != null)
                    {
                        PillLayerScale.ScaleX = 1.0;
                        PillLayerScale.ScaleY = 1.0;
                    }
                    AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(CardCarouselHost,UIElement.OpacityProperty, null);
                    CardCarouselHost.Opacity = 0;
                    CardCarouselHost.IsHitTestVisible = false;
                    ClearCardTransitionBlur();
                    CancelSiriOrb();
                },
                via: via,
                viaAt: IslandMetrics.CollapseViaAt,
                followLag: IslandMetrics.CollapseFollowLag);
        }

    }
}
