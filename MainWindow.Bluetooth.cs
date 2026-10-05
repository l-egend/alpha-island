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
// Moved verbatim out of the former single-file MainWindow.xaml.cs; no logic changes.
namespace alpha_island
{
    public partial class MainWindow
    {
        // ================= 蓝牙通知 =================

        private void OnBluetoothDeviceConnected(object? sender, BluetoothDeviceInfo info)
        {
            if (_exiting || !_options.BluetoothNotifyEnabled) return;
            if (_greetingShowing && GreetingActive) return;
            ShowBluetoothNotify(info, isConnected: true);
        }

        private void OnBluetoothDeviceDisconnected(object? sender, BluetoothDeviceInfo info)
        {
            if (_exiting || !_options.BluetoothNotifyEnabled) return;
            ShowBluetoothNotify(info, isConnected: false);
        }

        private string? _btDisplayedDeviceId;
        private bool _btDisplayedConnected;
        private void OnBluetoothBatteryUpdated(object? sender, BluetoothDeviceInfo info)
        {
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
        /// 蓝牙设备连接/断开时弹出一级胶囊，显示设备名称、分类专属图标/3D模型和连接/断开状态及电量，一段时间后自动收回。
        /// </summary>
        private void ShowBluetoothNotify(BluetoothDeviceInfo info, bool isConnected = true)
        {
            _btDisplayedDeviceId = info.Id;
            _btDisplayedConnected = isConnected;
            // 换设备及断联时立即清除上一个设备的显示，不延续旧电量动画。
            UpdateBluetoothBattery(null);
            ShowPillNotification(IslandNotification.Seconds("bluetooth", _options.BluetoothNotifySeconds));

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

            // 根据设备分类切换 3D 旋转耳机模型或专属硬件矢量图标
            switch (info.Category)
            {
                case BluetoothDeviceCategory.Audio:
                    BtDeviceModel.Visibility = Visibility.Visible;
                    BtDeviceModel.IsRotating = isConnected;
                    BtDeviceIcon.Visibility = Visibility.Collapsed;
                    BtIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
                    break;
                case BluetoothDeviceCategory.Keyboard:
                    BtDeviceModel.Visibility = Visibility.Collapsed;
                    BtDeviceModel.IsRotating = false;
                    BtDeviceIcon.Visibility = Visibility.Visible;
                    BtDeviceIcon.Text = "\uE765"; // 键盘
                    BtDeviceIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2));
                    BtIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x28, 0x4A, 0x90, 0xE2));
                    break;
                case BluetoothDeviceCategory.Mouse:
                    BtDeviceModel.Visibility = Visibility.Collapsed;
                    BtDeviceModel.IsRotating = false;
                    BtDeviceIcon.Visibility = Visibility.Visible;
                    BtDeviceIcon.Text = "\uE962"; // 鼠标
                    BtDeviceIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x50, 0xE3, 0xC2));
                    BtIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x28, 0x50, 0xE3, 0xC2));
                    break;
                case BluetoothDeviceCategory.Gamepad:
                    BtDeviceModel.Visibility = Visibility.Collapsed;
                    BtDeviceModel.IsRotating = false;
                    BtDeviceIcon.Visibility = Visibility.Visible;
                    BtDeviceIcon.Text = "\uE7FC"; // 游戏手柄
                    BtDeviceIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0x66, 0xFF));
                    BtIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xD0, 0x66, 0xFF));
                    break;
                case BluetoothDeviceCategory.Input:
                    BtDeviceModel.Visibility = Visibility.Collapsed;
                    BtDeviceModel.IsRotating = false;
                    BtDeviceIcon.Visibility = Visibility.Visible;
                    BtDeviceIcon.Text = "\uE92C"; // 输入设备
                    BtDeviceIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x50, 0xE3, 0xC2));
                    BtIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x28, 0x50, 0xE3, 0xC2));
                    break;
                default:
                    BtDeviceModel.Visibility = Visibility.Collapsed;
                    BtDeviceModel.IsRotating = false;
                    BtDeviceIcon.Visibility = Visibility.Visible;
                    BtDeviceIcon.Text = "\uE702"; // 蓝牙通用图标
                    BtDeviceIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD7));
                    BtIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x28, 0x00, 0x78, 0xD7));
                    break;
            }

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

            if (!_options.LiquidGlassBackdrop)
            {
                _backdrop = null;
                return;
            }

            try { _backdrop = BackdropWindow.TryCreate(); }
            catch { _backdrop = null; }

            var backdrop = _backdrop;
            if (backdrop == null) return;

            backdrop.SetVisible(false);

            // 玻璃窗**固定**覆盖"岛体可能出现的最大范围"（最大卡片 + 过冲余量），
            // 之后连续形变只改 Composition 侧的圆角裁剪几何、不再动窗口尺寸，
            // 省掉每帧 4~6.7ms 的窗口重排（纯属性写入便宜一个数量级）。
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
        /// 玻璃窗外扩的余量。**必须很小**：DWM 的窗口级 backdrop 只认窗口矩形，
        /// 无论 Composition 侧的圆角裁剪还是 <c>SetWindowRgn</c> 都管不住它
        /// （实测：模糊范围永远等于窗口矩形）。所以只能让窗口本身贴着岛体走，
        /// 余量留 2px 防止边缘出现 1px 缝隙，圆角处那一点点方形"耳朵"由投影盖住。
        /// </summary>
        private const double BackdropMargin = 2;

        private double _backdropX, _backdropY;
        private Rect _lastIslandRect = Rect.Empty;

        private void OnBackdropTick(object? sender, EventArgs e)
        {
            var backdrop = _backdrop;
            if (backdrop == null || !backdrop.IsAvailable) return;
            if (_state == IslandState.Bar || !IsVisible) return;

            Point p0 = IslandBody.PointToScreen(new Point(0, 0));
            Point p1 = IslandBody.PointToScreen(new Point(IslandBody.ActualWidth, IslandBody.ActualHeight));
            var rect = new Rect(p0.X, p0.Y, Math.Max(0, p1.X - p0.X), Math.Max(0, p1.Y - p0.Y));
            if (rect.Width < 2 || rect.Height < 2) return;
            if (rect.Equals(_lastIslandRect)) return;
            _lastIslandRect = rect;

            // 玻璃窗紧贴岛体（含动画位移与过冲），只留 BackdropMargin 的余量。
            int bx = (int)Math.Round(rect.X - BackdropMargin);
            int by = (int)Math.Round(rect.Y - BackdropMargin);
            int bw = (int)Math.Round(rect.Width + BackdropMargin * 2);
            int bh = (int)Math.Round(rect.Height + BackdropMargin * 2);
            _backdropX = bx;
            _backdropY = by;
            backdrop.SetBounds(bx, by, bw, bh);

            // PointToScreen 已经把 RenderTransform（挤压 + 位移）算进去了，
            // CornerRadius 读的也是动画中的实时值。
            backdrop.SetIsland(rect.X - bx, rect.Y - by, rect.Width, rect.Height,
                IslandBody.CornerRadius.TopLeft);
        }

        /// <summary>形态切换时同步玻璃窗的显隐；细条态没必要让它露着。</summary>
        private void SyncBackdrop()
        {
            var backdrop = _backdrop;
            if (backdrop == null || !backdrop.IsAvailable) return;
            bool shouldShow = _options.LiquidGlassBackdrop && _state != IslandState.Bar && _options.GlassOpacity >= 0.45;
            backdrop.SetVisible(shouldShow);
            _lastIslandRect = Rect.Empty;   // 强制下一帧重算裁剪

            // 逐帧回调只在玻璃窗真露着的时候挂：挂着 CompositionTarget.Rendering 会让 WPF 一直
            // 以刷新率请求帧、永远进不了空闲（实测细条态空转 6.5% of one core，而 OnBackdropTick
            // 在 Bar 态下第一句就 return，纯属白烧）。形态切换必经 SetState → 这里，挂/摘都幂等。
            CompositionTarget.Rendering -= OnBackdropTick;
            if (shouldShow) CompositionTarget.Rendering += OnBackdropTick;
        }

        /// <summary>创建尝试只做一次：驱动/系统拒绝时不必每次 ApplyOptions 都重试。</summary>
        private bool _backdropTried;

        /// <summary>
        /// 液态玻璃开关的落点：打开就（必要时）把真实 HostBackdrop 玻璃窗建起来，
        /// 关掉就把它藏起来、本色退回纯渐变。设置里那个 <c>LiquidGlassBackdrop</c>
        /// 开关就是走这里生效的。
        /// </summary>
        private void EnsureBackdrop()
        {
            if (!_options.LiquidGlassBackdrop)
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
                element.BeginAnimation(OpacityProperty, null);
                element.Opacity = to;
                return;
            }

            element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
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
            bool on = _options.VisualizerEnabled && AllowsFrameLoop();
            bool pillActive = on && _state == IslandState.Pill && !UpNextActive && !(_btNotifying && BtNotifyActive);
            SetActive(PillSpectrum, pillActive);
            SetActive(CardWave, on && _state == IslandState.Card && _options.ShowWaveLayer);
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
            _state = IslandState.Bar;

            // 开一段形变窗口（S3）：这条路径不走 SetState，得自己告诉宿主"要塌回收细条了"。
            // 必须紧跟换态：下面那些 _layers.Hide(...) 每个都会立刻触发一次 Sync，
            // 那时若窗口还没开，宿主会按"Bar 态没有任何图层要显示"把内容层**瞬间**掐灭 ——
            // 实测就是"淡出被跳过后 3ms 内 show→hide"的一帧闪。
            _layers.BeginMorph(prev, IslandState.Bar);
            _hoverOutTicks = 0;
            _isPinned = false;
            HidePinBubble(immediate: true);
            CollapseAllSatellitesWithMainIsland();
            _upNextTimer?.Stop();
            _greetingTimer?.Stop();

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
            BtDeviceModel.IsRotating = false;
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
            PillLayerTranslate?.BeginAnimation(TranslateTransform.XProperty, null);
            if (PillCoverHost != null)
            {
                PillCoverHost.Opacity = 1;
            }

            // 卡片是本次收回的源层时不能提前折叠：它要留在屏幕上被 ApplyReveal 压到 0，
            // 否则收回的第一帧卡片内容直接消失（用户看到的"小岛返回时闪一下"）。
            CardCarouselHost.BeginAnimation(UIElement.OpacityProperty, null);
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
                    CardCarouselHost.BeginAnimation(UIElement.OpacityProperty, null);
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
