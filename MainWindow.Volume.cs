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

// Volume adjustment and volume card drawer.
// Moved verbatim out of the former single-file MainWindow.xaml.cs; no logic changes.
namespace alpha_island
{
    public partial class MainWindow
    {
        // ================= 音量调节 =================

        private void OnVolumeChanged(object? sender, VolumeInfo info)
        {
            if (_exiting) return;
            if (!_options.VolumeNotifyEnabled) { SyncVolumeCardContent(); return; }
            RecordVolumeDesktopEvent(info);
            if (_sessionLocked || SettingsRegistry.GetBool(_options, "NotificationQuietMode", false)) { SyncVolumeCardContent(); return; }
            ShowVolumeNotify(info.Percent, info.IsMuted);
        }

        private void OnDeviceChanged(object? sender, EventArgs e)
        {
            // 换设备后环回采集要重建，接下来几秒采不到声音是正常的，先关掉静音仲裁的判据。
            _audioDeviceSuppressUntil = DateTime.UtcNow.AddSeconds(5.0);
            Dispatcher.InvokeAsync(() =>
            {
                SyncVolumeCardContent();
                if (_isAudioDeviceExpanded)
                {
                    RefreshAudioDeviceList();
                }
            });
        }

        /// <summary>
        /// 后台刷新线程算好新的设备 / 会话列表后回调。只有当前真的展开着对应抽屉才重建 UI，
        /// 卡片没打开、抽屉没收起时不白刷 ItemsSource。
        /// </summary>
        private void OnVolumeListsChanged(object? sender, EventArgs e)
        {
            if (_exiting || _state != IslandState.Card) return;
            if (_isAudioDeviceExpanded) RefreshAudioDeviceList();
            if (_isVolumeMixerExpanded) RefreshVolumeAppSessionsList();
        }

        private void ShowVolumeNotify(int percent, bool isMuted)
        {
            if (_greetingShowing && GreetingActive) return;
            ShowPillNotification(IslandNotification.Seconds("volume", _options.VolumeNotifySeconds));
            SyncVolumePillContent(percent, isMuted);
            if (_state != IslandState.Pill && _state != IslandState.Card) SetState(IslandState.Pill);
            SyncVolumeContent();
            SyncVolumeCardContent();
        }

        private void SyncVolumePillContent(int percent, bool isMuted)
        {
            if (_isDraggingVolumeSlider)
            {
                percent = _volumeDragPercent;
                if (percent > 0) isMuted = false;
            }

            // 更新自适应音量图标及微光颜色
            if (isMuted || percent == 0)
            {
                VolumeIcon.Text = "\uE74F"; // 静音图标
                VolumeIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55));
                VolumeIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0x55, 0x55));
                VolumeIconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0x55, 0x55));
                VolumeProgressFill.Background = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            }
            else
            {
                if (percent < 33) VolumeIcon.Text = "\uE993"; // 音量低
                else if (percent < 66) VolumeIcon.Text = "\uE994"; // 音量中
                else VolumeIcon.Text = "\uE995"; // 音量高

                var cyan = (System.Windows.Media.Brush)FindResource("AccentCyan");
                VolumeIcon.Foreground = cyan;
                VolumeIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0xE5, 0xFF));
                VolumeIconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xE5, 0xFF));
                VolumeProgressFill.Background = new SolidColorBrush(Color.FromArgb(0x9F, 255, 255, 255));
            }

            VolumeText.Text = isMuted ? "静音" : $"{percent}%";

            // 动态计算进度条填充宽度 (根据实际槽体宽度，缺省取 180px)
            double trackWidth = VolumeTrackContainer.ActualWidth > 0 ? VolumeTrackContainer.ActualWidth : 180.0;
            VolumeProgressFill.Width = Math.Clamp((trackWidth * percent) / 100.0, 0, trackWidth);

        }

        private void SyncVolumeContent()
        {
            // 已经过期就把 Showing 落下，等价于老代码的 _volumeNotifying = false
            if (!VolumeNotifyActive) _layers.Hide("volume");
            SyncNotificationLayers();
        }

        /// <summary>
        /// 刷新音量卡显示。拖拽滑块时用 <paramref name="displayPercent"/> 直接给出"手上算出来的值"，
        /// 不再回读服务 —— 写入被 40ms 合并后，服务里的值会落后于手感。
        /// </summary>
        private void SyncVolumeCardContent(int? displayPercent = null)
        {
            if (CardVolumeSliderFill == null || CardVolumePercentText == null) return;

            EnsureVolumeService();

            int percent = displayPercent ?? (_isDraggingVolumeSlider ? _volumeDragPercent : _volumeService?.CurrentPercent ?? 0);
            bool isMuted = _volumeService?.IsMuted ?? false;
            if ((displayPercent != null || _isDraggingVolumeSlider) && percent > 0) isMuted = false;
            _volumeDisplayPercent = percent;
            SyncVolumePillContent(percent, isMuted);
            string devName = _volumeService?.CurrentDeviceName ?? "系统扬声器";

            CardVolumeDeviceText.Text = devName;
            if (CurrentDeviceNameLabel != null) CurrentDeviceNameLabel.Text = devName;
            CardVolumePercentText.Text = isMuted ? "静音" : $"{percent}%";

            if (isMuted || percent == 0)
            {
                CardVolumeIcon.Text = "\uE74F"; // 静音图标
                CardVolumeIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55));
                CardVolumeIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0x55, 0x55));
                CardVolumeIconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0x55, 0x55));
                CardVolumeSliderFill.Background = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                CardVolumePercentText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55));
            }
            else
            {
                if (percent < 33) CardVolumeIcon.Text = "\uE993"; // 低
                else if (percent < 66) CardVolumeIcon.Text = "\uE994"; // 中
                else CardVolumeIcon.Text = "\uE995"; // 高

                var cyan = (System.Windows.Media.Brush)FindResource("AccentCyan");
                CardVolumeIcon.Foreground = cyan;
                CardVolumeIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0xE5, 0xFF));
                CardVolumeIconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xE5, 0xFF));
                CardVolumeSliderFill.Background = new SolidColorBrush(Color.FromArgb(0x9F, 255, 255, 255));
                CardVolumePercentText.Foreground = cyan;
            }

            double trackW = CardVolumeSliderTrack.ActualWidth > 0 ? CardVolumeSliderTrack.ActualWidth : 180.0;
            CardVolumeSliderFill.Width = Math.Clamp((trackW * percent) / 100.0, 0, trackW);
        }

        private void VolumeNotifyLayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            // 若点击在音量进度条区域，通过点击直接调节音量大小
            if (VolumeTrackContainer != null && VolumeTrackContainer.ActualWidth > 1)
            {
                Point pos = e.GetPosition(VolumeTrackContainer);
                if (pos.X >= 0 && pos.X <= VolumeTrackContainer.ActualWidth && pos.Y >= -8 && pos.Y <= VolumeTrackContainer.ActualHeight + 8)
                {
                    double ratio = Math.Clamp(pos.X / Math.Max(1.0, VolumeTrackContainer.ActualWidth), 0.0, 1.0);
                    int targetVolume = (int)Math.Round(ratio * 100);
                    _volumeService?.SetVolume(targetVolume);
                    return;
                }
            }

            // 双击才展开完整音量调节大卡片，防止误触展开大卡片
            if (e.ClickCount >= 2)
            {
                SetState(IslandState.Card);
                SwitchCardPage(_pages.IndexOfOrDefault(IslandPageIds.Volume));
            }
        }

        private readonly WheelAccumulator _volumeWheel = new();
        private void IslandBody_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_state == IslandState.Card)
            {
                if (_pages.ActiveId is IslandPageIds.Notifications or IslandPageIds.System) return;

                if (_pages.ActiveId == IslandPageIds.Volume)
                {
                    // 音量卡：滚轮调音量（本页既有语义，别改）。
                    int delta = _volumeWheel.Consume(e.Delta) * 2;
                    _volumeService?.AdjustVolume(delta);
                    SyncVolumeCardContent();
                    e.Handled = true;
                    return;
                }

                // 其它任何页：滚轮翻页（内层"剪切板历史 / AI 气泡还没滚到底"的守卫在 TryWheelSwitchPage 里）。
                // 没换页时不设 Handled，让内层滚动条照常工作。
                if (TryWheelSwitchPage(e.Delta, e.OriginalSource as DependencyObject)) e.Handled = true;
                return;
            }

            if (_state == IslandState.Bar || _state == IslandState.Pill)
            {
                int step = _volumeWheel.Consume(e.Delta) * 2;
                if (step == 0) { e.Handled = true; return; }
                _volumeService?.AdjustVolume(step);
                int currentVol = _volumeService?.CurrentPercent ?? 0;
                bool isMuted = _volumeService?.IsMuted ?? false;
                ShowVolumeNotify(currentVol, isMuted);
                e.Handled = true;
            }
        }

        private void CardVolumeMuteButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            _volumeService?.ToggleMute();
            SyncVolumeCardContent();
        }

        private void CardVolumeDown_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            _volumeService?.AdjustVolume(-5);
            SyncVolumeCardContent();
        }

        private void CardVolumeUp_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            _volumeService?.AdjustVolume(+5);
            SyncVolumeCardContent();
        }

        private bool _isDraggingVolumeSlider;
        private FrameworkElement? _volumeDragTrack;
        private int _volumeDragPercent, _volumeDisplayPercent;

        /// <summary>上一次真正写到系统音量的时刻（Stopwatch 刻度），用于把拖拽期间的密集写入合并到 40ms。</summary>
        private long _volumeWriteStamp;

        /// <summary>被合并窗口丢掉的最后一帧音量值；松手或丢失捕获时补写一次。</summary>
        private int _pendingVolumePercent = -1;
        private DispatcherTimer? _volumeWriteTimer;

        /// <summary>音量卡抽屉额外占用的高度（0 = 全部收起），见 SetCardDrawerExtra。</summary>
        private double _cardDrawerExtra;

        /// <summary>VolumeService 是按需创建的（Start 是后台异步），列表为空时重试的剩余次数。</summary>
        private int _volumeListRetriesLeft;

        private DispatcherTimer? _volumeListRetryTimer;

        private void CardVolumeSliderTrack_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            BeginVolumeDrag((FrameworkElement)sender, e.GetPosition((FrameworkElement)sender).X);
            e.Handled = true;
        }

        private void BeginVolumeDrag(FrameworkElement track, double x, bool capture = true)
        {
            EndVolumeDrag();
            _isSwiping = false;
            _dragStartedInsideIsland = true;
            _volumeDragTrack = track;
            // CaptureMouse 会同步鼠标位置并重发一次移动；先获得捕获，再开启拖动，
            // 避免那次同步移动使用旧按键状态提前结束新手势。
            if (capture && !track.CaptureMouse())
            {
                _volumeDragTrack = null;
                _dragStartedInsideIsland = false;
                return;
            }
            _isDraggingVolumeSlider = true;
            SetVolumeTrackHovered(track, true);
            UpdateVolumeFromSliderPosition(x);
        }

        private void CardVolumeSliderTrack_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingVolumeSlider && e.LeftButton == MouseButtonState.Pressed)
            {
                UpdateVolumeFromSliderPosition(e.GetPosition(_volumeDragTrack!).X);
                e.Handled = true;
            }
            else if (_isDraggingVolumeSlider) EndVolumeDrag();
        }

        private void CardVolumeSliderTrack_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingVolumeSlider)
            {
                UpdateVolumeFromSliderPosition(e.GetPosition(_volumeDragTrack!).X);
                EndVolumeDrag();
                e.Handled = true;
            }
        }

        /// <summary>
        /// 拖拽途中丢失鼠标捕获（例如卡片翻页把这一层置为 Collapsed 时 WPF 会自动释放）：
        /// 不收尾的话 _isDraggingVolumeSlider 会永远停在 true，之后鼠标在滑块上随便一动都在改音量。
        /// </summary>
        private void CardVolumeSliderTrack_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (!_isDraggingVolumeSlider) return;
            EndVolumeDrag();
        }

        private void EndVolumeDrag()
        {
            if (!_isDraggingVolumeSlider) return;
            var track = _volumeDragTrack;
            _isDraggingVolumeSlider = false;
            _volumeDragTrack = null;
            _dragStartedInsideIsland = false;
            if (track?.IsMouseCaptured == true) track.ReleaseMouseCapture();
            FlushPendingVolumeWrite();
            if (track != null) SetVolumeTrackHovered(track, track.IsMouseOver);
        }

        private void SetVolumeTrackHovered(FrameworkElement track, bool hovered) =>
            AnimateTrackThickness(ReferenceEquals(track, CardVolumeSliderTrack) ? CardVolumeCapsule : VolumeCapsule, hovered);

        private void VolumeTrack_MouseEnter(object sender, MouseEventArgs e) => SetVolumeTrackHovered((FrameworkElement)sender, true);
        private void VolumeTrack_MouseLeave(object sender, MouseEventArgs e) =>
            SetVolumeTrackHovered((FrameworkElement)sender, ReferenceEquals(sender, _volumeDragTrack));

        private void VolumeCapsule_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var capsule = (Grid)sender;
            capsule.Clip = new RectangleGeometry(new Rect(0, 0, capsule.ActualWidth, capsule.ActualHeight),
                capsule.ActualHeight / 2, capsule.ActualHeight / 2);
            var fill = ReferenceEquals(capsule, CardVolumeCapsule) ? CardVolumeSliderFill : VolumeProgressFill;
            fill.Width = capsule.ActualWidth * Math.Clamp(_volumeDisplayPercent / 100.0, 0, 1);
        }

        private void UpdateVolumeFromSliderPosition(double x)
        {
            double trackW = (_volumeDragTrack ?? CardVolumeSliderTrack).ActualWidth;
            if (trackW <= 0) return;
            double ratio = Math.Clamp(x / trackW, 0.0, 1.0);
            int percent = (int)Math.Round(ratio * 100.0);
            _volumeDragPercent = percent;

            // 服务把设备写入投给后台；这里每 40ms 合并密集请求，画面每次移动立即更新。
            // 暂停拖动但仍按住时，也由短定时器写入最新位置，不必等到下一次移动或松手。
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double sinceMs = _volumeWriteStamp == 0
                ? double.MaxValue
                : (now - _volumeWriteStamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (sinceMs >= 40.0)
            {
                _volumeWriteStamp = now;
                _pendingVolumePercent = -1;
                _volumeWriteTimer?.Stop();
                _volumeService?.SetVolume(percent);
            }
            else
            {
                _pendingVolumePercent = percent;
                if (_volumeWriteTimer == null)
                {
                    _volumeWriteTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
                    _volumeWriteTimer.Tick += (_, _) => FlushPendingVolumeWrite();
                }
                if (!_volumeWriteTimer.IsEnabled) _volumeWriteTimer.Start();
            }

            SyncVolumeCardContent(percent);
        }

        /// <summary>把拖拽期间被合并掉的那次写入补上。</summary>
        private void FlushPendingVolumeWrite()
        {
            _volumeWriteTimer?.Stop();
            int pending = _pendingVolumePercent;
            _pendingVolumePercent = -1;
            if (pending < 0) return;
            _volumeWriteStamp = System.Diagnostics.Stopwatch.GetTimestamp();
            _volumeService?.SetVolume(pending);
            SyncVolumeCardContent(pending);
        }

        // ================= 音量卡片抽屉折叠面板 (输出设备与音量合成器) =================

        private bool _isAudioDeviceExpanded;
        private bool _isVolumeMixerExpanded;

        private void AudioDeviceHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            _isAudioDeviceExpanded = !_isAudioDeviceExpanded;
            if (_isAudioDeviceExpanded && _isVolumeMixerExpanded)
            {
                AnimateDrawer(VolumeMixerPanel, VolumeMixerChevronRotate, false, 0);
                _isVolumeMixerExpanded = false;
            }

            if (_isAudioDeviceExpanded)
            {
                RefreshAudioDeviceList();
            }

            AnimateDrawer(AudioDevicePanel, AudioDeviceChevronRotate, _isAudioDeviceExpanded, 68);
        }

        private void VolumeMixerHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            _isVolumeMixerExpanded = !_isVolumeMixerExpanded;
            if (_isVolumeMixerExpanded && _isAudioDeviceExpanded)
            {
                AnimateDrawer(AudioDevicePanel, AudioDeviceChevronRotate, false, 0);
                _isAudioDeviceExpanded = false;
            }

            if (_isVolumeMixerExpanded)
            {
                RefreshVolumeAppSessionsList();
            }

            AnimateDrawer(VolumeMixerPanel, VolumeMixerChevronRotate, _isVolumeMixerExpanded, 78);
        }

        private void AnimateDrawer(Grid panel, RotateTransform chevronRotate, bool expand, double targetHeight)
        {
            var duration = TimeSpan.FromMilliseconds(220);
            var ease = new CubicEase { EasingMode = expand ? EasingMode.EaseOut : EasingMode.EaseIn };

            if (expand)
            {
                panel.Visibility = Visibility.Visible;
                var hAnim = new DoubleAnimation(0, targetHeight, duration) { EasingFunction = ease };
                var opAnim = new DoubleAnimation(0, 1.0, duration) { EasingFunction = ease };
                var rotAnim = new DoubleAnimation(0, 180, duration) { EasingFunction = ease };

                panel.BeginAnimation(FrameworkElement.HeightProperty, hAnim);
                panel.BeginAnimation(UIElement.OpacityProperty, opAnim);
                chevronRotate.BeginAnimation(RotateTransform.AngleProperty, rotAnim);
                SetCardDrawerExtra(targetHeight);
            }
            else
            {
                var hAnim = new DoubleAnimation(panel.ActualHeight, 0, duration) { EasingFunction = ease };
                var opAnim = new DoubleAnimation(panel.Opacity, 0, duration) { EasingFunction = ease };
                var rotAnim = new DoubleAnimation(chevronRotate.Angle, 0, duration) { EasingFunction = ease };

                opAnim.Completed += (_, __) =>
                {
                    if (!_isAudioDeviceExpanded && panel == AudioDevicePanel)
                    {
                        panel.Visibility = Visibility.Collapsed;
                        panel.Height = 0;
                    }
                    if (!_isVolumeMixerExpanded && panel == VolumeMixerPanel)
                    {
                        panel.Visibility = Visibility.Collapsed;
                        panel.Height = 0;
                    }
                };

                panel.BeginAnimation(FrameworkElement.HeightProperty, hAnim);
                panel.BeginAnimation(UIElement.OpacityProperty, opAnim);
                chevronRotate.BeginAnimation(RotateTransform.AngleProperty, rotAnim);
                SetCardDrawerExtra(0);
            }
        }

        /// <summary>
        /// 音量卡的两个折叠面板（输出设备 / 音量合成器）展开时必须同时把卡片视口和岛体撑高。
        /// 视口写死 194、岛体按页高（无歌词 138 / 有歌词或第 1、2 页 214），而抽屉一展开内容就要 ~280：
        /// 不撑高的话多出来的一整块会被 CardViewport 与 IslandBody 的 ClipToBounds 裁掉，
        /// 表现就是"展开后一片空白、点不动"（被裁掉的部分连命中测试都不存在）。
        /// </summary>
        private void SetCardDrawerExtra(double extra)
        {
            if (extra < 0) extra = 0;
            if (Math.Abs(_cardDrawerExtra - extra) < 0.5) return;
            _cardDrawerExtra = extra;

            if (CardViewport != null) CardViewport.Height = 194 + extra;

            // 光长视口不够：页自己还写死 194，行 1（输出设备 + 音量合成器两个折叠区）拿不到
            // 额外高度就会溢出行 2，底部提示叠在"音量合成器"那一行上。这里是广播而不是直接改
            // VolumeCardLayer：跟不跟由页自己的 OnViewportHeightChanged 决定（见 MainWindow.Pages.cs）。
            _pages.BroadcastViewportHeight(194 + extra);

            if (_state == IslandState.Card && _animator != null)
            {
                MorphIsland(CardGeometry, TimeSpan.FromMilliseconds(220),
                    new CubicEase { EasingMode = EasingMode.EaseOut });
            }
        }

        /// <summary>
        /// 强制收回音量卡的两个折叠面板（不动画）并把额外高度归零。
        /// 翻页、离开卡片、重新进卡片都要调：抽屉撑高的岛体不会自己缩回去，
        /// 否则用户看到的就是"展开过一次抽屉，之后切到哪张卡岛都还是大的"。
        /// </summary>
        private void ResetCardDrawers()
        {
            _isAudioDeviceExpanded = false;
            _isVolumeMixerExpanded = false;

            ResetDrawerPanel(AudioDevicePanel, AudioDeviceChevronRotate);
            ResetDrawerPanel(VolumeMixerPanel, VolumeMixerChevronRotate);

            SetCardDrawerExtra(0);
        }

        private static void ResetDrawerPanel(Grid? panel, RotateTransform? chevron)
        {
            if (panel != null)
            {
                AnimationReset.Height(panel);
                AnimationReset.Opacity(panel);
                panel.Height = 0;
                panel.Opacity = 0;
                panel.Visibility = Visibility.Collapsed;
            }

            if (chevron != null)
            {
                AnimationReset.Rotate(chevron);
                chevron.Angle = 0;
            }
        }

        /// <summary>
        /// 音量卡（输出设备列表 / 音量合成器 / 百分比）需要 VolumeService 才有数据。
        /// 即使"音量通知"开关是关的，用户也应该能在这张卡里切设备、静音应用，所以按需创建。
        /// 安全性：OnVolumeChanged 内部会按 _options.VolumeNotifyEnabled 早退，不会因此弹通知气泡。
        /// </summary>
        private VolumeService? EnsureVolumeService()
        {
            if (_volumeService != null)
            {
                if (_volumeListRetriesLeft > 0) ScheduleVolumeListRetry();
                return _volumeService;
            }
            if (_exiting || !_runtimeEnabled) return null;

            // force: 音量卡是核心功能，即使「音量通知」开关关着也得能切设备/静音应用；
            // 被 force 拉起后宿主会把它标成 Pinned，之后的 Sync 不会因为开关关着就收走它。
            _services.EnsureStarted(IslandServiceIds.Volume, force: true);

            if (_volumeService != null)
            {
                // Start() 里的 COM 端点绑定在后台线程上跑，刚创建时枚举必然是空的 —— 稍后补刷几次。
                _volumeListRetriesLeft = 4;
                ScheduleVolumeListRetry();
                IslandLog.Debug("Volume", "音量卡按需创建 VolumeService");
            }

            return _volumeService;
        }

        private void ScheduleVolumeListRetry()
        {
            if (_exiting || _volumeListRetriesLeft <= 0 || _volumeListRetryTimer != null) return;

            _volumeListRetryTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(700)
            };
            _volumeListRetryTimer.Tick += (_, __) =>
            {
                _volumeListRetryTimer?.Stop();
                _volumeListRetryTimer = null;
                _volumeListRetriesLeft--;
                RefreshAudioDeviceList();
                RefreshVolumeAppSessionsList();
                SyncVolumeCardContent();
            };
            _volumeListRetryTimer.Start();
        }

        private void RefreshAudioDeviceList()
        {
            if (AudioDeviceList == null) return;
            var service = EnsureVolumeService();
            List<AudioDeviceInfo> list = service?.GetActiveOutputDevices() ?? new List<AudioDeviceInfo>();
            AudioDeviceList.ItemsSource = list;
        }

        private void RefreshVolumeAppSessionsList()
        {
            if (VolumeAppSessionsList == null) return;
            var service = EnsureVolumeService();
            List<AudioAppSessionViewModel> list = service?.GetAudioAppSessions() ?? new List<AudioAppSessionViewModel>();
            VolumeAppSessionsList.ItemsSource = list;
        }

        private void AudioDeviceItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement elem && elem.DataContext is AudioDeviceInfo dev)
            {
                _volumeService?.SetDefaultDevice(dev.Id);
                RefreshAudioDeviceList();
                SyncVolumeCardContent();
            }
        }

        private void OpenSystemVolumeMixer_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            VolumeService.OpenWindowsVolumeMixer();
        }

        private void ToggleAppMute_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement elem && elem.DataContext is AudioAppSessionViewModel vm)
            {
                _volumeService?.ToggleAppMute(vm.ProcessId);
                RefreshVolumeAppSessionsList();
            }
        }

    }
}
