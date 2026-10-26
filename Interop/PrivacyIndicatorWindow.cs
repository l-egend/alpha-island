using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using alpha_island.Animation;
using alpha_island.Core;
// 项目同时引了 WinForms（ImplicitUsings 会带进 System.Drawing / System.Windows.Forms），
// 这些名字都两边的类型撞名，逐个显式指定成 WPF 的。
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Visibility = System.Windows.Visibility;

namespace alpha_island.Interop
{
    /// <summary>
    /// 屏幕右上角的「麦克风 / 摄像头占用」指示灯（独立窗口）：一枚橙色点 = 麦克风在用，
    /// 一枚绿色点 = 摄像头在用，和 iOS 灵动岛上的那两枚点同色同义。
    ///
    /// 为什么必须是**独立窗口**：岛体窗口只有 800×340、钉在屏幕顶部中间（`Left=(2560-800)/2, Top=0`），
    /// 屏幕右上角根本不在它的矩形里；而用户要的恰恰是「岛收起来之后，在屏幕右上角还能看见灯」。
    ///
    /// 三条不能省的窗口样式（都在 <see cref="SourceInitialized"/> 里设置，事后改会静默失败）：
    ///   • <c>WS_EX_TRANSPARENT</c>：整窗点击穿透 —— 这是纯提示，压在游戏/浏览器右上角时不能挡住关闭按钮；
    ///   • <c>WS_EX_NOACTIVATE</c>：永不抢焦点 —— 灯亮的那一瞬用户可能正在全屏游戏里打字；
    ///   • <c>WS_EX_TOOLWINDOW</c>：不进 Alt+Tab、不显示在任务栏。
    /// 配合 <c>ShowActivated=false</c> + <c>ShowInTaskbar=false</c>，它就是一个「只会被看见」的窗。
    /// </summary>
    internal sealed class PrivacyIndicatorWindow : IDisposable
    {
        // ── 观感常量（对齐 iOS 17 的占用点：橙 #FF9F0A / 绿 #30D158）──
        private const double DotSize = 8;
        private const double DotGap = 5;
        private const double PillPaddingX = 8;
        private const double PillPaddingY = 6;
        private const double CornerMarginX = 14;
        private const double CornerMarginY = 10;
        private const int FadeInMs = 140;
        private const int FadeOutMs = 200;
        // iOS 灵动岛那种「从中心点长出来」的出场：缩放在窗口内部做（窗口本身不移动、不变形），
        // 所以不会连带把桌面闪一下。退场就是它的反向（EaseIn 收进中心点）。
        // 这里**故意不做回弹**：胶囊正好填满窗口，任何 >1 的过冲都会被窗口边缘裁掉
        // （实测 BackEase Amplitude=0.5 峰值到 1.13，两端圆角会被切平）。
        private const int PopInMs = 220;
        private const int PopOutMs = 170;

        private static readonly Brush MicBrush = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));
        private static readonly Brush CamBrush = Frozen(Color.FromRgb(0x30, 0xD1, 0x58));
        private static readonly Brush PillBrush = Frozen(Color.FromArgb(0xCC, 0x00, 0x00, 0x00));

        private readonly Window _window;
        private readonly Ellipse _micDot;
        private readonly Ellipse _camDot;
        // 出/退场只缩放这一层（RenderTransformOrigin = 中心点）：胶囊填满整个窗口，
        // 所以「绕中心缩放」= 从窗口正中间的一点长出来，窗口位置与大小全程不动。
        private readonly ScaleTransform _pop = new(1, 1);
        private bool _mic;
        private bool _cam;
        private bool _shown;
        private bool _disposed;

        // ── 只读探针（默认关闭）：`ALPHA_ISLAND_ANIMDBG=1` 时把每帧的实际 ScaleX 打进日志，
        //    好让脚本能断言「确实是 0→1 均匀放大」而不是「肉眼看起来像」。
        private readonly bool _animDebug = Environment.GetEnvironmentVariable("ALPHA_ISLAND_ANIMDBG") == "1";
        /// <summary>出退场动画的慢放倍数（<c>ALPHA_ISLAND_ANIMDBG_SLOW</c>，默认 1 = 原速）。</summary>
        private readonly double _popScale = ReadPopScale();
        private readonly System.Diagnostics.Stopwatch _popWatch = new();
        private readonly List<double> _popSamples = new();
        private string _popPhase = "in";
        private double _popTarget = 1;
        private int _popLimitMs = 600;
        private bool _popProbing;
        /// <summary>正在播退场动画（这期间点的可见性 / 窗口宽度都保持不动，见 <see cref="SetActive"/>）。</summary>
        private bool _hiding;

        public PrivacyIndicatorWindow()
        {
            _micDot = new Ellipse { Width = DotSize, Height = DotSize, Fill = MicBrush, Visibility = Visibility.Collapsed };
            _camDot = new Ellipse { Width = DotSize, Height = DotSize, Fill = CamBrush, Visibility = Visibility.Collapsed };

            // 半透明黑底小胶囊：浅色壁纸上白点看不见、深色壁纸上绿点会糊，垫一层黑底两种情况都稳。
            var pill = new Border
            {
                Background = PillBrush,
                CornerRadius = new CornerRadius((DotSize + PillPaddingY * 2) / 2),
                Padding = new Thickness(PillPaddingX, PillPaddingY, PillPaddingX, PillPaddingY),
                SnapsToDevicePixels = true,
                RenderTransform = _pop,
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { _micDot, _camDot },
                },
            };

            _window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                WindowStartupLocation = WindowStartupLocation.Manual,
                // 尺寸自己算（不用 SizeToContent）：SizeToContent + ShowActivated=false 实测给出一个
                // 136×39 的默认尺寸（内容被拉到整窗、圆点跑到偏右下），显式给宽高才可控。
                SizeToContent = SizeToContent.Manual,
                Width = DotSize + PillPaddingX * 2,
                Height = DotSize + PillPaddingY * 2,
                ShowActivated = false,
                ShowInTaskbar = false,
                Topmost = true,
                Focusable = false,
                IsHitTestVisible = false,
                Content = pill,
            };

            _window.SourceInitialized += (_, __) => ApplyClickThrough(_window);
            _window.SizeChanged += (_, __) => Reposition();
            _window.DpiChanged += (_, _) => _window.Dispatcher.BeginInvoke(new Action(Reposition));
        }

        /// <summary>当前是否挂在屏幕上（供日志/验收断言使用）。</summary>
        public bool IsVisible => _shown;

        /// <summary>当前窗口矩形（DIP，供日志/验收断言使用）。</summary>
        public Rect Bounds => new(_window.Left, _window.Top,
            _window.Width > 0 ? _window.Width : _window.ActualWidth,
            _window.Height > 0 ? _window.Height : _window.ActualHeight);

        /// <summary>
        /// 由采样节拍（每 3s）调用：只传「现在谁在用」，窗口自己决定亮哪枚点、要不要出现。
        /// 连续两拍状态相同时什么都不做（不闪、不重复打日志）。
        /// </summary>
        public void SetActive(bool mic, bool cam, string? micApp = null, string? camApp = null)
        {
            if (_disposed) return;

            bool want = mic || cam;
            bool changed = mic != _mic || cam != _cam || want != _shown;

            _mic = mic;
            _cam = cam;
            // 退场动画播放期间保持最后一帧的样子：不然点是「瞬间消失」、胶囊再缩小，看起来是两段；
            // 窗口宽度也一样，先缩宽度再缩缩放等于缩两次。
            // 注意这一拍**即将**开始退场时也要保持（下面才调 HideCore，那之前 _hiding 还是 false）。
            bool willHide = !want && _shown;
            if (!_hiding && !willHide)
            {
                _micDot.Visibility = mic ? Visibility.Visible : Visibility.Collapsed;
                _camDot.Visibility = cam ? Visibility.Visible : Visibility.Collapsed;
                // 只有麦克风时摄像头点是收起的，那 5px 间距要一起收掉，否则圆点在胶囊里偏右。
                _camDot.Margin = new Thickness(mic ? DotGap : 0, 0, 0, 0);
                UpdateSize();
            }

            if (want != _shown)
            {
                if (want) ShowCore();
                else HideCore();
            }

            if (!changed) return;

            Rect b = Bounds;
            string app = mic ? micApp ?? "?" : cam ? camApp ?? "?" : "-";
            IslandLog.Debug("Privacy", $"角落指示灯：麦克风={(mic ? "亮" : "灭")} 摄像头={(cam ? "亮" : "灭")} " +
                                       $"可见={(want ? "True" : "False")}{(_hiding ? "（退场中）" : string.Empty)} 位置={b.X:0},{b.Y:0} 大小={b.Width:0}x{b.Height:0} 应用={app} {Diag()} " +
                                       $"[corner visible={(want ? "True" : "False")} mic={(mic ? "on" : "off")} " +
                                       $"cam={(cam ? "on" : "off")} hiding={_hiding} at={b.X:0},{b.Y:0} w={b.Width:0} h={b.Height:0} {DiagTags()}]");
        }

        /// <summary>窗口宽度按亮的点数算：一枚点 = 点宽 + 两边留白，两枚再加点间距。</summary>
        private void UpdateSize()
        {
            double dots = DotSize + (_mic && _cam ? DotGap + DotSize : _cam ? DotSize : 0d);
            _window.Width = dots + PillPaddingX * 2;
            _window.Height = DotSize + PillPaddingY * 2;
            if (_shown) Reposition();   // 宽度变了，右边缘要重新贴角
        }

        /// <summary>给日志用的实测指标：真 HWND、扩展样式（点击穿透是否真的设上了）、物理矩形、DPI 缩放。</summary>
        private string Diag()
        {
            IntPtr hwnd = new WindowInteropHelper(_window).Handle;
            int ex = hwnd == IntPtr.Zero ? 0 : NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            string phys = "n/a";
            if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT r))
            {
                phys = $"{r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}";
            }
            DpiScale dpi = VisualTreeHelper.GetDpi(_window);
            return $"hwnd=0x{hwnd.ToInt64():X} ex=0x{ex:X8} 物理={phys} dpi={dpi.DpiScaleX:0.##}";
        }

        private string DiagTags()
        {
            IntPtr hwnd = new WindowInteropHelper(_window).Handle;
            int ex = hwnd == IntPtr.Zero ? 0 : NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            string phys = "n/a";
            if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT r))
            {
                phys = $"{r.Left},{r.Top},{r.Right - r.Left}x{r.Bottom - r.Top}";
            }
            return $"hwnd=0x{hwnd.ToInt64():X} ex=0x{ex:X8} phys={phys} " +
                   $"passthrough={((ex & NativeMethods.WS_EX_TRANSPARENT) != 0 ? "1" : "0")}";
        }

        private void ShowCore()
        {
            _shown = true;
            _hiding = false;
            try
            {
                bool animate = !MotionPolicy.InstantMorph;
                ResetVisual();                      // 清掉上一轮残留的动画/局部值（退场没跑完就又亮）
                if (animate)
                {
                    _window.Opacity = 0;
                    _pop.ScaleX = 0;                // 从中心点的一个点开始长出来
                    _pop.ScaleY = 0;
                }

                _window.Show();
                // 别的置顶窗（或全屏游戏切分辨率）可能把我们压下去，每次出现都重新断言一次置顶。
                _window.Topmost = true;
                Reposition();
                if (!animate) return;

                int popInMs = PopMs(PopInMs);
                int fadeInMs = PopMs(FadeInMs);
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(_window,UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(fadeInMs)));
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(_pop,ScaleTransform.ScaleXProperty, PopAnim(0, 1, popInMs, easeIn: false));
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(_pop,ScaleTransform.ScaleYProperty, PopAnim(0, 1, popInMs, easeIn: false));
                IslandLog.Debug("Privacy", $"角落指示灯动画：出现（中心放大 0→1，{popInMs}ms；淡入 {fadeInMs}ms）" +
                    $" [corner-anim in pop={popInMs} fade={fadeInMs}]");
                StartPopProbe("in", 1, popInMs);
            }
            catch (Exception ex)
            {
                _shown = false;
                IslandLog.Warn("Privacy", "角落指示灯显示失败: " + ex.Message);
            }
        }

        private void HideCore()
        {
            _shown = false;
            _hiding = true;
            try
            {
                if (MotionPolicy.InstantMorph || !_window.IsVisible)
                {
                    _hiding = false;
                    ResetVisual();
                    _window.Hide();
                    return;
                }

                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(PopMs(FadeOutMs)));
                fade.Completed += (_, __) =>
                {
                    if (_disposed || _shown) return;   // 淡出期间又被点亮了：别把窗口收掉
                    _hiding = false;
                    ResetVisual();
                    _window.Hide();
                };
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(_window,UIElement.OpacityProperty, fade);
                // 出场动画的反向：缩回中心点。比淡出短一点（最后 30ms 只剩一层正在淡掉的黑底）。
                int popOutMs = PopMs(PopOutMs);
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(_pop,ScaleTransform.ScaleXProperty, PopAnim(1, 0, popOutMs, easeIn: true));
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(_pop,ScaleTransform.ScaleYProperty, PopAnim(1, 0, popOutMs, easeIn: true));
                IslandLog.Debug("Privacy", $"角落指示灯动画：消失（中心收缩 1→0，{popOutMs}ms；淡出 {PopMs(FadeOutMs)}ms）" +
                    $" [corner-anim out pop={popOutMs} fade={PopMs(FadeOutMs)}]");
                StartPopProbe("out", 0, popOutMs);
            }
            catch (Exception ex)
            {
                IslandLog.Warn("Privacy", "角落指示灯隐藏失败: " + ex.Message);
            }
        }

        /// <summary>贴屏幕工作区右上角（主屏；用 WorkArea 是为了避开任务栏，顶部又不吃遮挡）。</summary>
        public void Reposition()
        {
            if (_disposed) return;
            try
            {
                if (new WindowInteropHelper(_window).Handle != IntPtr.Zero)
                    WindowWorkArea.PlacePrimaryCorner(_window, CornerMarginX, CornerMarginY);
            }
            catch (Exception ex)
            {
                IslandLog.Debug("Privacy", "角落指示灯定位失败: " + ex.Message);
            }
        }

        /// <summary>把窗口的透明度与缩放复位到「静止态」：停掉动画并清掉动画留下的当前值。</summary>
        private void ResetVisual()
        {
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(_window,UIElement.OpacityProperty, null);
            _window.Opacity = 1;
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(_pop,ScaleTransform.ScaleXProperty, null);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(_pop,ScaleTransform.ScaleYProperty, null);
            _pop.ScaleX = 1;
            _pop.ScaleY = 1;
        }

        /// <summary>
        /// 出/退场用的缩放动画：出场 <see cref="CubicEase"/> EaseOut（由中心点快速长出、末尾减速到位），
        /// 退场 EaseIn（加速收进中心点）。不做回弹的原因见 PopInMs 那条注释。
        /// </summary>
        private static DoubleAnimation PopAnim(double from, double to, int ms, bool easeIn)
            => new(from, to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new CubicEase { EasingMode = easeIn ? EasingMode.EaseIn : EasingMode.EaseOut },
            };

        /// <summary>
        /// 慢放倍数（调试用）：<c>ALPHA_ISLAND_ANIMDBG_SLOW=6</c> 把出退场动画连同淡入淡出一起拉长 6 倍，
        /// 好让抓屏能拍到动画中间那一帧（正常 220ms 里截图根本来不及，DWM 合成还要一点时间）。
        /// </summary>
        private static double ReadPopScale()
        {
            string? raw = Environment.GetEnvironmentVariable("ALPHA_ISLAND_ANIMDBG_SLOW");
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0) return v;
            return 1d;
        }

        /// <summary>把基准毫秒按慢放倍数换算成实际时长（默认原速）。</summary>
        private int PopMs(int baseMs) => (int)Math.Round(baseMs * _popScale, MidpointRounding.AwayFromZero);

        // ── 缩放曲线探针（默认关闭，`ALPHA_ISLAND_ANIMDBG=1` 打开）──
        private void StartPopProbe(string phase, double target, int popMs)
        {
            if (!_animDebug) return;
            StopPopProbe();
            _popSamples.Clear();
            _popPhase = phase;
            _popTarget = target;
            _popLimitMs = 400 + popMs;          // 动画结束后再多采 400ms，确保曲线尾巴落到 1.00/0.00
            _popWatch.Restart();
            _popProbing = true;
            CompositionTarget.Rendering += OnPopFrame;
        }

        private void OnPopFrame(object? sender, EventArgs e)
        {
            if (!_popProbing) return;
            double s = _pop.ScaleX;
            _popSamples.Add(s);
            bool arrived = Math.Abs(s - _popTarget) < 0.01;
            if (!arrived && _popWatch.ElapsedMilliseconds < _popLimitMs) return;
            string curve = string.Join("->", _popSamples.Select(v => v.ToString("0.00")));
            IslandLog.Debug("Privacy", $"角落指示灯动画采样（{_popPhase}）：{_popSamples.Count} 帧 / {_popWatch.ElapsedMilliseconds}ms / 曲线 {curve}"
                + $" [corner-anim {_popPhase} frames={_popSamples.Count} ms={_popWatch.ElapsedMilliseconds} curve={curve}]");
            StopPopProbe();
        }

        private void StopPopProbe()
        {
            if (_popProbing) CompositionTarget.Rendering -= OnPopFrame;
            _popProbing = false;
            _popWatch.Stop();
        }

        private static void ApplyClickThrough(Window window)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
                ex |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
                NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);
            }
            catch (Exception ex2)
            {
                IslandLog.Warn("Privacy", "角落指示灯点击穿透设置失败: " + ex2.Message);
            }
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _shown = false;
            StopPopProbe();
            try
            {
                ResetVisual();
                _window.Hide();
                _window.Close();
            }
            catch (Exception ex)
            {
                IslandLog.Debug("Privacy", "角落指示灯关闭失败: " + ex.Message);
            }
        }
    }
}
