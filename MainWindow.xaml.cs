using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

namespace alpha_island
{
    /// <summary>
    /// 主窗口。模块化之后这里只负责"装配 + 事件转发"：
    ///   * 形态与尺寸        → Core/IslandMetrics + Animation/IslandAnimator
    ///   * 设置              → Core/IslandOptions + Settings/SettingsWindow
    ///   * 托盘 / 全局钩子 / Win32 → Interop/*
    ///   * 数据              → Services/*（SMTC、WASAPI 频谱、网易云歌词）
    ///   * 绘制              → Controls/SpectrumVisualizer、Controls/WaveVisualizer
    /// </summary>
    public partial class MainWindow : Window
    {
        // ================= 生命周期与模块 =================
        private static Mutex? _mutex;
        private bool _exiting;

        private IslandOptions _options = new();
        private IslandAnimator? _animator;
        private SettingsWindow? _settingsWindow;
        private TrayIcon? _tray;
        private MouseHook? _mouseHook;
        private BackdropWindow? _backdrop;

        private SmtcService? _smtc;
        private AudioSpectrumService? _audio;
        private LyricsService? _lyrics;

        // ================= 状态机 =================
        private IslandState _state = IslandState.Bar;
        private readonly DispatcherTimer _hoverTimer;
        private readonly DispatcherTimer _progressTimer;
        private readonly DispatcherTimer _mediaLostTimer;

        /// <summary>光标连续不在热区里的次数。3 次 ≈ 360ms 降一级，6 次 ≈ 720ms 收成细条。</summary>
        private int _hoverOutTicks;
        private const int HoverDowngradeTicks = 3;
        private const int HoverCollapseTicks = 6;

        // ================= 曲目与歌词 =================
        private string _lastTitle = "";
        private string _lastArtist = "";
        private LyricsDocument? _lyricsDoc;
        private readonly RectangleGeometry _lyricWipe = new();
        private bool _karaokeHooked;
        private bool _lyricsAreaVisible;
        private int _lyricIndex = -2;
        private string _lyricsKey = "";
        private CancellationTokenSource? _lyricsCts;

        /// <summary>上一张封面。同一个封面反复进来时不重复取色（调色板计算要遍历像素）。</summary>
        private BitmapSource? _lastArt;

        /// <summary>
        /// 换曲提示（一级胶囊右侧的"接下来"）。SMTC 只给当前曲目、拿不到真正的"下一首"，
        /// 所以这里显示的就是刚切进来的这一首，也就是用户口中的"下一首"。
        /// </summary>
        private DateTime _upNextUntil = DateTime.MinValue;
        private string _upNextTitle = "";
        private bool _upNextShowing;

        private bool UpNextActive => DateTime.UtcNow < _upNextUntil;

        /// <summary>
        /// 已经看到过至少一首曲目了。启动时 SMTC 第一次报上来的那首歌**不算换曲**，
        /// 否则每次开机都会平白弹一次提示。
        /// </summary>
        private bool _sawMedia;

        /// <summary>PillLyric 不显示换曲提示时应该有的文本，由歌词/媒体逻辑维护。</summary>
        private string _pillLyricNormal = "Alpha Island";

        public MainWindow()
        {
            // 单实例：第二次启动直接提示并退出，避免两个岛互相抢钩子
            _mutex = new Mutex(true, "DynamicIslandDemo_SingleInstance_Mutex", out bool isNewInstance);
            if (!isNewInstance)
            {
                System.Windows.MessageBox.Show("Alpha Island 已经在运行了（请查看右下角托盘图标）。",
                    "Alpha Island", MessageBoxButton.OK, MessageBoxImage.Information);
                System.Windows.Application.Current.Shutdown();
                return;
            }

            InitializeComponent();

            _options = IslandOptions.Load();

            // 卡拉OK 擦除：裁剪几何只挂一次，之后逐帧只改 Rect（见 OnKaraokeTick）
            LyricCurrentBright.Clip = _lyricWipe;

            _animator = new IslandAnimator(IslandBody, IslandTranslate, IslandSquash, SheenTranslate,
                IslandShadow, SpecularBar, SpecularTranslate);
            _animator.SetImmediate(BarGeometry);

            _hoverTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            _hoverTimer.Tick += HoverTimer_Tick;
            _hoverTimer.Start();

            _progressTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _progressTimer.Tick += ProgressTimer_Tick;

            _mediaLostTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(1200)
            };
            _mediaLostTimer.Tick += (_, __) =>
            {
                _mediaLostTimer.Stop();
                ApplyNoMedia();
            };

            _tray = new TrayIcon();
            _tray.SettingsRequested += (_, __) => ShowSettings();
            _tray.ExitRequested += (_, __) => ExitApplication();
            _tray.Show();

            ApplyOptions();
        }

        /// <summary>
        /// 调试用：设 <c>ALPHA_ISLAND_STATE=pill|card</c> 可让岛体开机就停在指定形态，
        /// 且悬停轮询不再自动收回。仅为自动化截图/测量服务，正常启动时该值为 null。
        /// </summary>
        private readonly string? _forcedState = Environment.GetEnvironmentVariable("ALPHA_ISLAND_STATE");

        // ================= 启动 =================

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            PositionWindow();
            ApplyOptions();

            // 全局鼠标钩子：只用于"点击岛外立即收起"，不做悬停判定
            _mouseHook = new MouseHook();
            _mouseHook.ButtonDown += OnGlobalButtonDown;
            _mouseHook.Hook();

            // 音频：WASAPI 环回采集 → 频谱
            _audio = new AudioSpectrumService { Sensitivity = (float)_options.Sensitivity };
            _audio.SpectrumUpdated += OnSpectrumUpdated;
            _audio.Start();

            // 媒体：SMTC
            _smtc = new SmtcService(Dispatcher);
            _smtc.NowPlayingChanged += OnNowPlayingChanged;
            _smtc.Failed += (_, msg) => System.Diagnostics.Debug.WriteLine("[SMTC] " + msg);

            // 歌词：网易云
            _lyrics = new LyricsService { Enabled = _options.LyricsEnabled };

            _progressTimer.Start();

            ApplyOptions();   // 建/刷新真实玻璃层，并按"有没有真实模糊"重算本色浓度

            await _smtc.InitializeAsync();

            if (_forcedState != null)
            {
                if (_forcedState.Equals("orb", StringComparison.OrdinalIgnoreCase))
                {
                    // 调试定态：冻结在"收回中间态那颗球"，用于逐像素核对球心图案是否为专辑封面
                    _state = IslandState.Bar;
                    FadeLayer(PillLayer, 0, 0);
                    FadeLayer(CardLayer, 0, 0);
                    FadeLayer(CardWave, 0, 0);
                    PillLayer.IsHitTestVisible = false;
                    CardLayer.IsHitTestVisible = false;
                    SetActive(PillSpectrum, false);
                    SetActive(CardWave, false);
                    double c = IslandMetrics.CircleSize;
                    _animator?.SetImmediate(new IslandGeometry(c, c, 18, new CornerRadius(c / 2)));
                    SiriOrbTranslate.X = 0;
                    SiriOrb.BeginAnimation(OpacityProperty, null);
                    SiriOrb.Opacity = 1;
                }
                else
                {
                    SetState(_forcedState.Equals("card", StringComparison.OrdinalIgnoreCase)
                        ? IslandState.Card : IslandState.Pill, force: true);

                    if (_forcedState.Equals("upnext", StringComparison.OrdinalIgnoreCase))
                    {
                        // 调试定态：一级胶囊 + 换曲提示内容（"接下来"），用于截图核对排版；
                        // 时长仍走真实设置，所以这个状态也会自己收回，可以顺带验证提示生命周期。
                        _upNextTitle = _smtc?.Current?.DisplayTitle ?? "下一首歌名";
                        _upNextUntil = DateTime.UtcNow.AddSeconds(_options.UpNextSeconds);
                        _upNextShowing = true;
                        SyncUpNextContent();
                        SyncVisualizers();
                    }
                }
            }
        }

        /// <summary>窗口永远水平居中、贴屏幕顶部。</summary>
        private void PositionWindow()
        {
            Left = Math.Round((SystemParameters.PrimaryScreenWidth - Width) / 2.0);
            Top = 0;
        }

        private void Window_Closed(object sender, EventArgs e) => ExitApplication();

        // ================= 形态与几何 =================

        private static IslandGeometry BarGeometry =>
            new(IslandMetrics.BarWidth, IslandMetrics.BarHeight, IslandMetrics.BarTop, IslandMetrics.BarCorner);

        private static IslandGeometry PillGeometry =>
            new(IslandMetrics.PillWidth, IslandMetrics.PillHeight, IslandMetrics.PillTop, IslandMetrics.PillCorner);

        private IslandGeometry CardGeometry
        {
            get
            {
                double h = LyricsAreaVisible ? IslandMetrics.CardHeightLyrics : IslandMetrics.CardHeight;
                return new IslandGeometry(IslandMetrics.CardWidth, h, IslandMetrics.CardTop,
                    new CornerRadius(IslandMetrics.CardCornerRadius));
            }
        }

        /// <summary>歌词区是否真的占位（决定卡片高度）。</summary>
        private bool LyricsAreaVisible => _options.LyricsEnabled && _lyricsDoc != null && _lyricsDoc.HasLyrics;

        private IslandGeometry GeometryFor(IslandState state) => state switch
        {
            IslandState.Pill => PillGeometry,
            IslandState.Card => CardGeometry,
            _ => BarGeometry
        };

        /// <summary>
        /// 切换到目标形态。这是唯一的入口 —— 尺寸、圆角、内容层透明度、可视化开关、
        /// 卡拉OK 钩子全部在这里同步，避免各条路径各改一半导致状态不一致。
        /// </summary>
        private void SetState(IslandState next, bool force = false)
        {
            if (!force && next == _state) return;
            if (_exiting) return;

            IslandState prev = _state;
            _state = next;

            // 离开细条就立刻掐掉收回动画中间态那颗球。
            // 否则会出现"收回还没跑完、用户又点开"——球（带封面）和胶囊里的封面同时可见 = 两个专辑图标。
            if (next != IslandState.Bar) CancelSiriOrb();

            bool growing = next > prev || (force && next == prev);
            var duration = growing ? IslandMetrics.ExpandDuration : IslandMetrics.GrowDuration;

            _animator?.MorphTo(GeometryFor(next), _options.GelAnimation, duration,
                new CubicEase { EasingMode = EasingMode.EaseInOut });

            // 内容层不做传统交叉淡入淡出：IslandBody 开着 ClipToBounds，材质展开/收拢的过程中
            // 内容本来就是被轮廓"裁"出来的，过渡由形变本身完成。这里只留一个极短的淡入/淡出，
            // 纯粹为了避开硬切那一下的跳变。
            int fadeMs = 70;
            int beginMs = growing ? 70 : 0;
            FadeLayer(PillLayer, next == IslandState.Pill ? 1 : 0, fadeMs, beginMs);
            FadeLayer(CardLayer, next == IslandState.Card ? 1 : 0, fadeMs, beginMs);
            // 波浪层已上移到岛体根容器，不再跟着 CardLayer 一起淡入淡出，得单独驱动。
            FadeLayer(CardWave, next == IslandState.Card ? 1 : 0, fadeMs, beginMs);
            PillLayer.IsHitTestVisible = next == IslandState.Pill;
            CardLayer.IsHitTestVisible = next == IslandState.Card;

            SyncVisualizers();
            SyncUpNextContent();
            UpdateKaraokeHook();
            SyncBackdrop();
        }

        /// <summary>把"收回动画中间态那颗球"复位：停掉两条动画并把不可见状态写实。</summary>
        private void CancelSiriOrb()
        {
            SiriOrb.BeginAnimation(OpacityProperty, null);
            SiriOrb.Opacity = 0;
            SiriOrbTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            SiriOrbTranslate.X = -40;
        }

        /// <summary>PillLyric 的唯一写入口 —— 顺手记住"正常文本"，换曲提示结束后好还原。</summary>
        private void SetPillLyric(string text)
        {
            _pillLyricNormal = text;
            if (!UpNextActive) PillLyric.Text = text;
        }

        /// <summary>
        /// 换曲提示期间：一级胶囊右侧的频谱整块换成"接下来"指示，中间显示新曲名。
        /// 提示结束后原样还原（歌词由 UpdateKaraokeHook / ApplyLyricTexts 重新驱动）。
        /// </summary>
        private void SyncUpNextContent()
        {
            bool up = UpNextActive;
            PillSpectrum.Visibility = up ? Visibility.Collapsed : Visibility.Visible;
            PillUpNext.Visibility = up ? Visibility.Visible : Visibility.Collapsed;
            PillLyric.Text = up ? _upNextTitle : _pillLyricNormal;
        }

        /// <summary>
        /// 换曲提示：弹出一级胶囊，中间是新曲名、右侧是"接下来"，持续 UpNextSeconds 秒后自动收回。
        /// 只在细条状态触发 —— 已经展开说明用户正看着，不用打断。
        /// </summary>
        private void BeginUpNext(NowPlayingInfo info)
        {
            if (!_options.UpNextEnabled) return;

            _upNextTitle = info.DisplayTitle;
            _upNextUntil = DateTime.UtcNow.AddSeconds(_options.UpNextSeconds);
            _upNextShowing = true;
            // 已经展开说明用户正看着，只换内容、不动形态
            if (_state == IslandState.Bar) SetState(IslandState.Pill);
            else SyncUpNextContent();
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
            CompositionTarget.Rendering += OnBackdropTick;
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
            backdrop.SetVisible(_options.LiquidGlassBackdrop && _state != IslandState.Bar);
            _lastIslandRect = Rect.Empty;   // 强制下一帧重算裁剪
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

        private static void FadeLayer(UIElement element, double to, int ms, int beginTime = 0)
        {
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                BeginTime = TimeSpan.FromMilliseconds(beginTime)
            });
        }

        /// <summary>按当前形态决定哪个可视化控件在渲染；不活跃的一律停渲染并清空。</summary>
        private void SyncVisualizers()
        {
            bool on = _options.VisualizerEnabled;
            SetActive(PillSpectrum, on && _state == IslandState.Pill && !UpNextActive);
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
        /// 收回细条。保留用户定稿的三段式编排：
        ///   阶段1 卡片/胶囊 → 46px 正圆（Siri 球滑到球心并淡入）
        ///   阶段2 只做位移，正圆吸到屏幕顶部（保持正圆，绝不变形）
        ///   阶段3 正圆摊平成 130x10 细条
        /// 每一段回调开头都检查目标状态，被用户中途打断就整体放弃。
        /// </summary>
        private void CollapseToBar()
        {
            if (_exiting) return;

            _state = IslandState.Bar;
            _hoverOutTicks = 0;

            // 收回意味着换曲提示结束：把胶囊内容还原，下次展开不会还挂着"接下来"
            _upNextUntil = DateTime.MinValue;
            _upNextShowing = false;
            SyncUpNextContent();

            FadeLayer(PillLayer, 0, 70);
            FadeLayer(CardLayer, 0, 70);
            FadeLayer(CardWave, 0, 70);
            PillLayer.IsHitTestVisible = false;
            CardLayer.IsHitTestVisible = false;
            SetActive(PillSpectrum, false);
            SetActive(CardWave, false);
            StopKaraoke();
            _backdrop?.SetVisible(false);

            var animator = _animator;
            if (animator == null) return;

            // Siri 球：从左侧滑到球心并淡入
            SiriOrbTranslate.X = -40;
            SiriOrb.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
            {
                BeginTime = TimeSpan.FromMilliseconds(110)
            });
            SiriOrbTranslate.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-40, 0, TimeSpan.FromMilliseconds(160))
                {
                    BeginTime = TimeSpan.FromMilliseconds(110),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });

            const double circle = IslandMetrics.CircleSize;
            var stage1 = new IslandGeometry(circle, circle, 18, new CornerRadius(circle / 2));
            var stage2 = new IslandGeometry(circle, circle, 0, new CornerRadius(circle / 2));

            animator.MorphTo(stage1, _options.GelAnimation, IslandMetrics.CollapseStage1,
                new CubicEase { EasingMode = EasingMode.EaseInOut }, () =>
                {
                    if (_state != IslandState.Bar) return;
                    animator.MorphTo(stage2, false, IslandMetrics.CollapseStage2,
                        new CubicEase { EasingMode = EasingMode.EaseIn }, () =>
                        {
                            if (_state != IslandState.Bar) return;
                            SiriOrb.BeginAnimation(OpacityProperty,
                                new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
                            animator.MorphTo(BarGeometry, false, IslandMetrics.CollapseStage3,
                                new CubicEase { EasingMode = EasingMode.EaseOut });
                        });
                });
        }

        // ================= 悬停 / 点击 =================

        private void IslandBody_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_exiting) return;
            _hoverOutTicks = 0;
            if (_state == IslandState.Bar) SetState(IslandState.Pill);
            else if (_state == IslandState.Pill && !_options.TwoStepExpand) SetState(IslandState.Card);
        }

        /// <summary>一级 → 二级：用户要求"鼠标经过时第一次展开"是一级，再点一下才进完整卡片。</summary>
        private void IslandBody_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_exiting) return;
            _hoverOutTicks = 0;
            if (_state == IslandState.Pill) SetState(IslandState.Card);
        }

        private void HoverTimer_Tick(object? sender, EventArgs e)
        {
            if (_exiting) return;
            // 调试定态：不参与悬停收回（upnext 例外 —— 它要走真实的提示超时收回）
            if (_forcedState != null && !_forcedState.Equals("upnext", StringComparison.OrdinalIgnoreCase)) return;

            // 换曲提示期间不收回，等计时走完再回到正常判定
            if (UpNextActive && _state == IslandState.Pill)
            {
                _hoverOutTicks = 0;
                _upNextShowing = true;
                return;
            }
            if (_upNextShowing)
            {
                _upNextShowing = false;
                SyncUpNextContent();
                SyncVisualizers();
            }

            if (IsCursorInHotZone())
            {
                _hoverOutTicks = 0;
                if (_state == IslandState.Bar) SetState(IslandState.Pill);
                return;
            }

            _hoverOutTicks++;

            if (_state == IslandState.Card && _hoverOutTicks >= HoverDowngradeTicks)
            {
                SetState(IslandState.Pill);
                return;
            }

            if (_state == IslandState.Pill && _hoverOutTicks >= HoverCollapseTicks)
            {
                // "保持播放时展开"：用户勾了这个就别收
                if (_options.KeepCompactWhilePlaying && _smtc?.Current?.IsPlaying == true) return;
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
            if (!NativeMethods.TryGetCursorPos(out var pt)) return false;
            var cursor = new Point(pt.X, pt.Y);

            if (IslandBody.ActualWidth > 0 && IslandBody.ActualHeight > 0)
            {
                var tl = IslandBody.PointToScreen(new Point(0, 0));
                var br = IslandBody.PointToScreen(new Point(IslandBody.ActualWidth, IslandBody.ActualHeight));
                var bodyRect = new Rect(tl, br);
                bodyRect.Inflate(4, 4);
                if (bodyRect.Contains(cursor)) return true;
            }

            double windowWidth = ActualWidth > 0 ? ActualWidth : Width;
            var barTl = PointToScreen(new Point((windowWidth - IslandMetrics.BarWidth) / 2.0, 0));
            var barRect = new Rect(barTl.X, barTl.Y, IslandMetrics.BarWidth, IslandMetrics.BarHeight);
            barRect.Inflate(6, 0);
            return barRect.Contains(cursor);
        }

        /// <summary>钩子回调在钩子线程上 —— 绝不能同步等 UI 线程，否则整机鼠标都卡。</summary>
        private void OnGlobalButtonDown(object? sender, MouseButtonDownEventArgs e)
        {
            if (_exiting) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_exiting || _state == IslandState.Bar) return;
                if (!IsPointInsideIsland(e.X, e.Y)) CollapseToBar();
            }), DispatcherPriority.Input);
        }

        private bool IsPointInsideIsland(int screenX, int screenY)
        {
            if (IslandBody.ActualWidth <= 0 || IslandBody.ActualHeight <= 0) return false;
            var tl = IslandBody.PointToScreen(new Point(0, 0));
            var br = IslandBody.PointToScreen(new Point(IslandBody.ActualWidth, IslandBody.ActualHeight));
            return new Rect(tl, br).Contains(new Point(screenX, screenY));
        }

        // ================= 媒体 =================

        private void OnNowPlayingChanged(object? sender, NowPlayingInfo? info)
        {
            if (info == null || !info.HasMedia)
            {
                // SMTC 在换曲瞬间会短暂报"无会话"，立刻清空会闪；延迟 1.2s 再确认。
                _mediaLostTimer.Stop();
                _mediaLostTimer.Start();
                return;
            }

            _mediaLostTimer.Stop();
            ApplyMedia(info);
        }

        private void ApplyMedia(NowPlayingInfo info)
        {
            TitleText.Text = info.DisplayTitle;
            ArtistText.Text = info.DisplayArtist;
            AlbumText.Text = string.IsNullOrWhiteSpace(info.Album) ? "" : info.Album;

            var art = info.Artwork;
            CardCoverBrush.ImageSource = art;
            PillCoverBrush.ImageSource = art;
            ApplyArtwork(art);

            PlayPauseGlyph.Text = info.IsPlaying ? "\uE769" : "\uE768";
            PrevButton.IsEnabled = info.CanPrevious;
            NextButton.IsEnabled = info.CanNext;
            ProgressTrack.Cursor = info.CanSeek ? Cursors.Hand : Cursors.Arrow;
            ProgressTrack.Opacity = info.CanSeek ? 1.0 : 0.6;

            if (_lyricsDoc == null) SetPillLyric(info.DisplayTitle);

            string title = info.Title ?? "";
            string artist = info.Artist ?? "";
            bool trackChanged = title.Length > 0
                                && (title != _lastTitle || artist != _lastArtist);

            if (trackChanged)
            {
                _lastTitle = title;
                _lastArtist = artist;
                StartLyricsFetch(info);

                // 换曲提示：弹出细条→一级胶囊，中间显示新曲名、右侧"接下来"，过一会儿自己收回
                // _sawMedia 保证只有"真的换了一首"才弹，开机第一首不弹。
                if (info.IsPlaying && _sawMedia) BeginUpNext(info);
            }

            _sawMedia = true;
        }

        private void ApplyNoMedia()
        {
            _lastTitle = "";
            _lastArtist = "";
            _lyricsKey = "";
            ApplyLyrics(null);

            TitleText.Text = "未在播放";
            ArtistText.Text = "";
            AlbumText.Text = "";
            CardCoverBrush.ImageSource = null;
            PillCoverBrush.ImageSource = null;
            ApplyArtwork(null);
            _upNextUntil = DateTime.MinValue;
            _upNextShowing = false;
            SetPillLyric("Alpha Island");
            SyncUpNextContent();
            PlayPauseGlyph.Text = "\uE768";
            PrevButton.IsEnabled = false;
            NextButton.IsEnabled = false;
            PositionText.Text = "0:00";
            DurationText.Text = "0:00";
            ProgressFill.Width = 0;

            if (_state != IslandState.Bar) CollapseToBar();
        }

        /// <summary>
        /// 封面统一处理：① 回缩成球时球里显示专辑封面（用户要求换掉固定图标）；
        /// ② 从封面提取主色调，给一级胶囊的频谱柱和完全展开的流体波浪上色。
        /// 用引用比较去重 —— SMTC 元数据抖动很频繁，不要每次都重算调色板（要遍历 1024 个像素）。
        /// </summary>
        private void ApplyArtwork(BitmapSource? art)
        {
            if (ReferenceEquals(art, _lastArt)) return;
            _lastArt = art;

            OrbCoverBrush.ImageSource = art;
            double coverOn = art != null ? 1.0 : 0.0;
            OrbCover.Opacity = coverOn;
            OrbCoverRing.Opacity = coverOn;
            OrbGradient.Opacity = 1.0 - coverOn;

            var palette = CoverPalette.From(art);

            // Copilot 原型的四色阶（暖橙红 / 洋红 / 紫罗兰 / 蓝）按封面主色相旋转生成；
            // 每根柱子/每一层波浪自己的 alpha 由控件内部按原型常量叠加，这里给满不透明原色。
            var ramp = palette.Ramp.Length >= 4 ? palette.Ramp : CoverPalette.DefaultRamp;

            PillSpectrum.ColorLow = ramp[0];
            PillSpectrum.ColorMid = ramp[1];
            PillSpectrum.ColorHigh = ramp[2];
            PillSpectrum.ColorExtra = ramp[3];

            CardWave.ColorLow = ramp[0];
            CardWave.ColorMid = ramp[1];
            CardWave.ColorHigh = ramp[2];
            CardWave.ColorExtra = ramp[3];
        }

        // ================= 歌词 =================

        private async void StartLyricsFetch(NowPlayingInfo info)
        {
            var service = _lyrics;
            if (service == null || !_options.LyricsEnabled) return;

            string title = info.Title ?? "";
            string artist = info.Artist ?? "";
            if (title.Length == 0) return;

            string key = title + "\u0001" + artist;
            if (key == _lyricsKey) return;
            _lyricsKey = key;

            _lyricsCts?.Cancel();
            _lyricsCts?.Dispose();
            var cts = new CancellationTokenSource();
            _lyricsCts = cts;

            ApplyLyrics(null);

            if (service.TryGetCached(title, artist, out var cached))
            {
                ApplyLyrics(cached);
                return;
            }

            try
            {
                var doc = await service.GetLyricsAsync(title, artist, info.Duration, cts.Token);
                if (cts.IsCancellationRequested) return;

                // 回来时可能已经换歌了，核对一下再应用
                var current = _smtc?.Current;
                if (current == null || current.Title != title) return;

                ApplyLyrics(doc);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Lyrics] " + ex.Message);
            }
        }

        /// <summary>
        /// 应用歌词文档。
        ///
        /// 防抖要点：只有"有无歌词"真的翻转时才去重定向动画。
        /// 曾经因为 SMTC 元数据抖动导致反复 force 重定向，卡片一直停在淡入中途，
        /// 看起来就是个半透明空壳。
        /// </summary>
        private void ApplyLyrics(LyricsDocument? doc)
        {
            _lyricsDoc = doc;
            _lyricIndex = -2;
            _lyricWipe.Rect = Rect.Empty;

            bool show = LyricsAreaVisible;
            LyricsArea.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            if (show && doc != null)
            {
                OnKaraokeTick(this, EventArgs.Empty);
            }
            else
            {
                LyricPrev.Text = "";
                LyricCurrentDim.Text = "";
                LyricCurrentBright.Text = "";
                LyricTranslation.Text = "";
                LyricNext.Text = "";
                SetPillLyric(_smtc?.Current?.DisplayTitle ?? "Alpha Island");
            }

            if (_lyricsAreaVisible != show)
            {
                _lyricsAreaVisible = show;
                // 只在已经处于卡片态时调整高度；不要因为歌词到了就把用户从胶囊顶进卡片
                if (_state == IslandState.Card) SetState(IslandState.Card, force: true);
            }

            UpdateKaraokeHook();
        }

        private void UpdateKaraokeHook()
        {
            bool should = _state == IslandState.Card
                          && _options.LyricsEnabled
                          && _lyricsDoc != null
                          && _lyricsDoc.HasLyrics;

            if (should && !_karaokeHooked)
            {
                CompositionTarget.Rendering += OnKaraokeTick;
                _karaokeHooked = true;
            }
            else if (!should && _karaokeHooked)
            {
                StopKaraoke();
            }
        }

        private void StopKaraoke()
        {
            if (!_karaokeHooked) return;
            CompositionTarget.Rendering -= OnKaraokeTick;
            _karaokeHooked = false;
        }

        private void OnKaraokeTick(object? sender, EventArgs e)
        {
            var doc = _lyricsDoc;
            var media = _smtc;
            if (doc == null || !doc.HasLyrics || media == null) return;

            var lines = doc.Lines;
            if (lines.Count == 0) return;

            TimeSpan position = media.EstimatedPosition;
            int index = Math.Clamp(doc.IndexAt(position), 0, lines.Count - 1);

            if (index != _lyricIndex)
            {
                _lyricIndex = index;
                ApplyLyricTexts(lines, index);
            }

            var line = lines[index];
            double progress = WordProgress(line.Time.TotalSeconds, line.WordEnds, line.WordChars,
                position.TotalSeconds);

            if (progress < 0)
            {
                double start = line.Time.TotalSeconds;
                double end = index + 1 < lines.Count ? lines[index + 1].Time.TotalSeconds : start + 4.0;
                double span = end - start;
                progress = span > 0.08 ? Math.Clamp((position.TotalSeconds - start) / span, 0, 1) : 0;
            }

            // 擦除宽度取 Bright 层的实际文本宽度；两者文本必须完全一致，否则会错位
            double w = LyricCurrentBright.ActualWidth;
            if (w <= 0) w = LyricCurrentDim.ActualWidth;
            double h = LyricCurrentBright.ActualHeight;
            if (h <= 0) h = IslandMetrics.LyricCurrentHeight;

            _lyricWipe.Rect = new Rect(0, 0, w * progress, h);
        }

        private void ApplyLyricTexts(System.Collections.Generic.IReadOnlyList<LyricLine> lines, int index)
        {
            var line = lines[index];
            string main = string.IsNullOrWhiteSpace(line.Text) ? "♪" : line.Text;

            // 两层文本必须逐字相同
            LyricCurrentDim.Text = main;
            LyricCurrentBright.Text = main;

            LyricPrev.Text = index > 0 ? Tidy(lines[index - 1].Text) : "";
            LyricNext.Text = index + 1 < lines.Count ? Tidy(lines[index + 1].Text) : "";
            LyricTranslation.Text = _options.ShowTranslation ? (line.Translation ?? "") : "";

            // 一级胶囊里只显示当前这一句（用户明确说不要上下句预览）
            SetPillLyric(main);

            // 两层宽度可能因为字体度量差一点，重新裁一次避免首帧错位
            _lyricWipe.Rect = new Rect(0, 0, 0, IslandMetrics.LyricCurrentHeight);
        }

        private static string Tidy(string? text) => string.IsNullOrWhiteSpace(text) ? "♪" : text;

        /// <summary>
        /// 逐字歌词（网易云 yrc）的擦除进度：按每个字的时长/字数加权，
        /// 字与字之间做线性插值，所以看起来是匀速刷过去的而不是跳。
        /// 返回 -1 表示这首歌没有逐字时间，调用方退回整行线性插值。
        /// </summary>
        private static double WordProgress(double lineStart, double[]? ends, int[]? chars, double position)
        {
            if (ends == null || chars == null || ends.Length == 0 || chars.Length == 0) return -1;

            double total = 0;
            for (int i = 0; i < chars.Length; i++) total += chars[i];
            if (total <= 0) return -1;

            double done = 0;
            int count = Math.Min(ends.Length, chars.Length);
            for (int i = 0; i < count; i++)
            {
                if (position >= ends[i])
                {
                    done += chars[i];
                    continue;
                }

                double segmentStart = i == 0 ? lineStart : ends[i - 1];
                double segmentEnd = ends[i];
                double span = segmentEnd - segmentStart;
                double fraction = span > 0.001 ? Math.Clamp((position - segmentStart) / span, 0, 1) : 0;
                done += chars[i] * fraction;
                break;
            }

            return Math.Clamp(done / total, 0, 1);
        }

        // ================= 频谱 =================

        /// <summary>采集线程回调：PushFrame 内部加锁拷贝，可以安全跨线程。</summary>
        private void OnSpectrumUpdated(object? sender, float[] bands)
        {
            if (bands == null || bands.Length == 0 || _exiting) return;

            switch (_state)
            {
                case IslandState.Pill:
                    PillSpectrum.PushFrame(bands);
                    break;
                case IslandState.Card:
                    CardWave.PushFrame(bands);
                    break;
            }
        }

        // ================= 进度 =================

        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            var media = _smtc;
            var info = media?.Current;
            if (media == null || info == null || !info.HasMedia) return;

            TimeSpan position = media.EstimatedPosition;
            TimeSpan duration = info.Duration;

            PositionText.Text = FormatTime(position);
            DurationText.Text = FormatTime(duration);

            double ratio = duration.TotalSeconds > 0.5
                ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1)
                : 0;
            double width = ProgressTrack.ActualWidth;
            ProgressFill.Width = Math.Max(0, width * ratio);

            // 胶囊态没有卡拉OK 钩子，这里补上当前歌词
            if (_state == IslandState.Pill)
            {
                var doc = _lyricsDoc;
                if (doc != null && doc.HasLyrics)
                {
                    var lines = doc.Lines;
                    int index = Math.Clamp(doc.IndexAt(position), 0, lines.Count - 1);
                    string text = lines[index].Text;
                    SetPillLyric(string.IsNullOrWhiteSpace(text) ? "♪" : text);
                }
            }
        }

        private static string FormatTime(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        private async void ProgressTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var media = _smtc;
            var info = media?.Current;
            if (media == null || info == null || !info.CanSeek || ProgressTrack.ActualWidth <= 0) return;

            double ratio = Math.Clamp(e.GetPosition(ProgressTrack).X / ProgressTrack.ActualWidth, 0, 1);
            await media.SeekAsync(info.Duration * ratio);
        }

        // ================= 传输控制 =================

        private async void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            if (_smtc != null) await _smtc.PreviousAsync();
        }

        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_smtc != null) await _smtc.TogglePlayPauseAsync();
        }

        private async void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_smtc != null) await _smtc.NextAsync();
        }

        // ================= 设置 =================

        private void ShowSettings()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ShowSettings));
                return;
            }

            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow();
                _settingsWindow.OptionsChanged += (_, options) =>
                {
                    _options.CopyFrom(options);
                    ApplyOptions();
                };
            }

            _settingsWindow.LoadFrom(_options);

            var audio = _audio;
            _settingsWindow.UpdateDeviceText(audio?.DeviceName ?? "未检测到输出设备");
            _settingsWindow.UpdateLyricsSource(_lyrics == null ? "网易云（未启用）" : "网易云 · 非官方接口");

            _settingsWindow.Show();
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
        }

        /// <summary>把 options 应用到所有活着的部件。设置改动后统一走这里，避免漏项。</summary>
        private void ApplyOptions()
        {
            // 先让玻璃窗的存在状态跟开关一致，再按它算本色浓度。
            EnsureBackdrop();

            double opacity = Math.Clamp(_options.GlassOpacity, 0.2, 0.98);

            // 背后有真实模糊（BackdropWindow 建起来了）时，渐变本色只能留一小部分：
            // 否则它会盖住模糊层，又变回一块不透明的黑板。
            // 建不起来（旧系统/驱动拒绝）就退回原来的浓度，看到的还是纯渐变玻璃。
            double tintFactor = _backdrop?.IsAvailable == true ? 0.46 : 1.0;
            GlassTopStop.Color = Color.FromArgb((byte)Math.Round(opacity * 230 * tintFactor), 0x16, 0x16, 0x1D);
            GlassBottomStop.Color = Color.FromArgb((byte)Math.Round(opacity * 255 * tintFactor), 0x09, 0x09, 0x0D);

            if (_audio != null) _audio.Sensitivity = (float)_options.Sensitivity;
            if (_lyrics != null) _lyrics.Enabled = _options.LyricsEnabled;

            PillCoverHost.Visibility = _options.ShowCover ? Visibility.Visible : Visibility.Collapsed;
            CardWave.Visibility = _options.ShowWaveLayer ? Visibility.Visible : Visibility.Collapsed;

            SyncVisualizers();

            // 歌词开关关掉时立刻清干净
            if (!_options.LyricsEnabled)
            {
                ApplyLyrics(null);
            }
            else
            {
                var current = _smtc?.Current;
                if (_lyricsDoc == null && current != null && current.HasMedia) StartLyricsFetch(current);
            }

            if (_state == IslandState.Card) SetState(IslandState.Card, force: true);
        }

        // ================= 退出 =================

        private void ExitApplication()
        {
            if (_exiting) return;
            _exiting = true;

            try
            {
                _hoverTimer.Stop();
                _progressTimer.Stop();
                _mediaLostTimer.Stop();

                StopKaraoke();
                _animator?.Stop();
                CompositionTarget.Rendering -= OnBackdropTick;
                _backdrop?.Dispose();
                _backdrop = null;

                _lyricsCts?.Cancel();
                _lyricsCts?.Dispose();
                _lyricsCts = null;

                if (_settingsWindow != null)
                {
                    _settingsWindow.ForceClose = true;
                    _settingsWindow.Close();
                    _settingsWindow = null;
                }

                _mouseHook?.Dispose();
                _mouseHook = null;

                _tray?.Dispose();
                _tray = null;

                _lyrics?.Dispose();
                _lyrics = null;

                _audio?.Dispose();
                _audio = null;

                _smtc?.Dispose();
                _smtc = null;

                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
                _mutex = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Exit] " + ex.Message);
            }

            System.Windows.Application.Current.Shutdown();
        }
    }
}
