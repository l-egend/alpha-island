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
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;

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
        private BluetoothService? _bluetooth;
        private VolumeService? _volumeService;
        private BatteryService? _battery;
        private HotkeyService? _hotkey;
        private ClipboardService? _clipboard;
        private FileTransferService? _fileTransfer;

        // ================= 电池状态通知 =================
        private bool _batteryNotifying;
        private DateTime _batteryNotifyUntil = DateTime.MinValue;
        private bool BatteryNotifyActive => DateTime.UtcNow < _batteryNotifyUntil;

        // ================= 剪切板通知与卡片态 =================
        private int _currentCardPage = 0; // 0: 音乐播放器, 1: 剪切板历史
        private bool _clipboardNotifying;
        private bool _clipboardLocked;
        private DateTime _clipboardPastedUntil = DateTime.MinValue;
        private bool ClipboardNotifyActive => _clipboardNotifying && (_clipboardLocked || DateTime.UtcNow < _clipboardPastedUntil);

        // ================= 手势与拖拽 =================
        private Point _swipeStartPoint;
        private Point _swipeStartScreenPoint;
        private bool _isSwiping;
        private bool _dragStartedInsideIsland;
        private Point _dragStartPoint;
        private TransferItem? _dragSourceItem;
        private bool _isDragTriggering;

        // 拖拽高亮画刷：DragOver/DragLeave 在拖拽期间会以极高频率触发，
        // 原实现每次事件都 new SolidColorBrush 或者 FindResource，既产生大量垃圾对象，
        // 又让 WPF 反复做属性失效与重绘。这里预建并冻结后复用，
        // 并且只在目标画刷真正变化时才写 BorderBrush。
        private static readonly System.Windows.Media.Brush DropCopyIdleBrush = FrozenDropBrush(0x33, 0x00, 0xF0, 0xFF);
        private static readonly System.Windows.Media.Brush DropCutIdleBrush = FrozenDropBrush(0x33, 0xFF, 0x70, 0x43);
        private static readonly System.Windows.Media.Brush DropCutActiveBrush = FrozenDropBrush(0xFF, 0xFF, 0x88, 0x55);
        private System.Windows.Media.Brush? _dropCopyActiveBrush;

        private static System.Windows.Media.Brush FrozenDropBrush(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        private System.Windows.Media.Brush DropCopyActiveBrush => _dropCopyActiveBrush ??= (System.Windows.Media.Brush)FindResource("AccentCyan");

        /// <summary>拖拽中按指针所在半区高亮：isCutSide=true 时剪切侧高亮，否则复制侧高亮。</summary>
        private void ApplyDropZoneHighlight(bool isCutSide)
        {
            var copy = isCutSide ? DropCopyIdleBrush : DropCopyActiveBrush;
            var cut = isCutSide ? DropCutActiveBrush : DropCutIdleBrush;
            if (!ReferenceEquals(DropZoneCopy.BorderBrush, copy)) DropZoneCopy.BorderBrush = copy;
            if (!ReferenceEquals(DropZoneCut.BorderBrush, cut)) DropZoneCut.BorderBrush = cut;
        }

        /// <summary>恢复两侧的常态描边。</summary>
        private void ResetDropZoneHighlight()
        {
            if (!ReferenceEquals(DropZoneCopy.BorderBrush, DropCopyIdleBrush)) DropZoneCopy.BorderBrush = DropCopyIdleBrush;
            if (!ReferenceEquals(DropZoneCut.BorderBrush, DropCutIdleBrush)) DropZoneCut.BorderBrush = DropCutIdleBrush;
        }

        // ================= 音量调节 =================
        private bool _volumeNotifying;
        private DateTime _volumeNotifyUntil = DateTime.MinValue;
        private bool VolumeNotifyActive => DateTime.UtcNow < _volumeNotifyUntil;

        // ================= 蓝牙通知 =================
        /// <summary>蓝牙通知正在显示。Pill 形态时 PillLayer 与 BtNotifyLayer 互斥。</summary>
        private bool _btNotifying;
        private DateTime _btNotifyUntil = DateTime.MinValue;
        private bool BtNotifyActive => DateTime.UtcNow < _btNotifyUntil;

        // ================= 当前展示动作类型（用于收缩回圆时的过渡图标） =================
        private enum IslandActionType
        {
            Music,
            Greeting,
            Volume,
            Battery,
            Bluetooth,
            Clipboard,
            FileTransfer
        }
        private IslandActionType _currentAction = IslandActionType.Music;

        private IslandActionType GetCurrentActionType()
        {
            if (_greetingShowing && GreetingActive) return IslandActionType.Greeting;
            if (_volumeNotifying && VolumeNotifyActive) return IslandActionType.Volume;
            if (_batteryNotifying && BatteryNotifyActive) return IslandActionType.Battery;
            if (_btNotifying && BtNotifyActive) return IslandActionType.Bluetooth;
            if (_clipboardNotifying && ClipboardNotifyActive) return IslandActionType.Clipboard;
            if (_state == IslandState.Tray) return IslandActionType.FileTransfer;
            return _currentAction;
        }

        // ================= 启动问候 =================
        private bool _greetingShowing;
        private DateTime _greetingUntil = DateTime.MinValue;
        private bool GreetingActive => DateTime.UtcNow < _greetingUntil;
        private DispatcherTimer? _greetingTimer;
        private TaskCompletionSource<bool>? _greetingFinishedTcs;

        // ================= 一级展开置顶与左侧音乐圆球 =================
        private bool _isPinned;
        private DispatcherTimer? _pinBubbleTimer;
        private bool _isMusicOrbShowing;
        private bool _isMusicOrbReturning;

        // ================= 状态机 =================
        private volatile IslandState _state = IslandState.Bar;
        private readonly DispatcherTimer? _hoverTimer;
        private readonly DispatcherTimer? _progressTimer;
        private readonly DispatcherTimer? _mediaLostTimer;

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
        /// 换曲 / 暂停提示（一级胶囊右侧指示）。
        /// </summary>
        private DateTime _upNextUntil = DateTime.MinValue;
        private string _upNextTitle = "";
        private string _upNextKind = "upnext"; // "upnext" | "pause"
        private bool _upNextShowing;
        private DispatcherTimer? _upNextTimer;
        private bool? _lastIsPlaying;

        private bool UpNextActive => _upNextShowing && DateTime.UtcNow < _upNextUntil;

        /// <summary>
        /// 已经看到过至少一首曲目了。启动时 SMTC 第一次报上来的那首歌**不算换曲**，
        /// 否则每次开机都会平白弹一次提示。
        /// </summary>
        private bool _sawMedia;

        /// <summary>PillLyric 不显示换曲提示时应该有的文本，由歌词/媒体逻辑维护。</summary>
        private string _pillLyricNormal = "Alpha Island";

        private IntPtr _lastExternalForegroundHwnd = IntPtr.Zero;
        private Rect _cachedIslandScreenRect = Rect.Empty;
        private TimeSpan _lastFetchDuration = TimeSpan.Zero;

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
            // 内容层显露与封面球都挂在主弹簧的进度上：共用同一条时间轴，没有各自独立的淡入淡出。
            _animator.ProgressHook = OnMorphProgress;
            _animator.SetImmediate(BarGeometry);

            _hoverTimer = new DispatcherTimer(DispatcherPriority.Background)
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
            _tray.SettingsRequested += (_, __) => Dispatcher.BeginInvoke(DispatcherPriority.Normal, ShowSettings);
            _tray.ExitRequested += (_, __) => Dispatcher.BeginInvoke(DispatcherPriority.Normal, ExitApplication);
            _tray.Show();

            ApplyOptions();
        }

        /// <summary>
        /// 调试用：设 <c>ALPHA_ISLAND_STATE=pill|card</c> 可让岛体开机就停在指定形态，
        /// 且悬停轮询不再自动收回。仅为自动化截图/测量服务，正常启动时该值为 null。
        /// </summary>
        private readonly string? _forcedState = Environment.GetEnvironmentVariable("ALPHA_ISLAND_STATE");
        private string? _pillLyricForce;

        // ================= Win32 消息过滤：无条件穿透非交互区 =================
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_POWERBROADCAST = 0x0218;
        private const int HTTRANSPARENT = -1;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            hwndSource?.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_POWERBROADCAST)
            {
                _battery?.NotifyPowerBroadcast();
            }
            else if (msg == WM_NCHITTEST)
            {
                int x = unchecked((short)(lParam.ToInt32() & 0xFFFF));
                int y = unchecked((short)((lParam.ToInt32() >> 16) & 0xFFFF));

                if (!IsPointInsideInteractiveZone(x, y))
                {
                    handled = true;
                    return new IntPtr(HTTRANSPARENT);
                }
            }
            return IntPtr.Zero;
        }

        private bool IsPointInsideInteractiveZone(int screenX, int screenY)
        {
            if (IsPointInsideIsland(screenX, screenY)) return true;
            if (PinBubble != null && PinBubble.Visibility == Visibility.Visible && PinBubble.Opacity > 0.1)
            {
                if (PinBubble.ActualWidth > 0 && PinBubble.ActualHeight > 0)
                {
                    try
                    {
                        var tl = PinBubble.PointToScreen(new Point(0, 0));
                        var br = PinBubble.PointToScreen(new Point(PinBubble.ActualWidth, PinBubble.ActualHeight));
                        if (new Rect(tl, br).Contains(new Point(screenX, screenY))) return true;
                    }
                    catch { }
                }
            }
            if (MusicOrbBubble != null && MusicOrbBubble.Visibility == Visibility.Visible && MusicOrbBubble.Opacity > 0.1)
            {
                if (MusicOrbBubble.ActualWidth > 0 && MusicOrbBubble.ActualHeight > 0)
                {
                    try
                    {
                        var tl = MusicOrbBubble.PointToScreen(new Point(0, 0));
                        var br = MusicOrbBubble.PointToScreen(new Point(MusicOrbBubble.ActualWidth, MusicOrbBubble.ActualHeight));
                        if (new Rect(tl, br).Contains(new Point(screenX, screenY))) return true;
                    }
                    catch { }
                }
            }
            return false;
        }

        // ================= 启动 =================

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            PositionWindow();
            ApplyOptions();

            try
            {
                IntPtr fg = NativeMethods.GetForegroundWindow();
                var myHwnd = new WindowInteropHelper(this).Handle;
                if (fg != IntPtr.Zero && fg != myHwnd) _lastExternalForegroundHwnd = fg;
            }
            catch { }

            // 全局鼠标钩子：只用于"点击岛外立即收起"，不做悬停判定
            _mouseHook = new MouseHook();
            _mouseHook.ButtonDown += OnGlobalButtonDown;
            _mouseHook.Hook();

            // 频谱采集尽早启动：它全程跑在后台线程（Start() 内部自建采集线程），不占 UI 线程、
            // 不影响问候动画。排到 Phase 2 的第 7 位要十几秒后才就绪，那段时间频谱只能是一条平线。
            StartAudioSpectrum();

            // ── Phase 1（立即）：先播问候动画，UI 线程完全空闲 ──────────────────
            // 所有重型服务在此期间一概不启动，UI 线程 100% 满帧顺滑展示欢迎动画。
            if (_forcedState == null && _options.GreetingEnabled)
            {
                _greetingFinishedTcs = new TaskCompletionSource<bool>();
                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                {
                    if (_state == IslandState.Bar && _forcedState == null)
                        ShowStartupGreeting();
                }));
            }

            // ── Phase 2（等问候完全结束并收拢后，再逐模块从容有序加载）───────────
            _ = Task.Run(async () =>
            {
                if (_forcedState == null && _options.GreetingEnabled)
                {
                    // 等待问候自然结束（按设置的时长 + 500ms 缓冲让动画平滑收回至细条），或者等待用户点击提前结束
                    int greetingTimeout = (int)(Math.Max(1.0, _options.GreetingSeconds) * 1000) + 500;
                    var tcs = _greetingFinishedTcs;
                    if (tcs != null)
                    {
                        await Task.WhenAny(tcs.Task, Task.Delay(greetingTimeout));
                        // 确保收缩动画播完平稳归位
                        await Task.Delay(400);
                    }
                    else
                    {
                        await Task.Delay(greetingTimeout);
                    }
                }
                else
                {
                    // 若未开启问候，也稍微留 600ms 缓冲，等主窗口完全呈现与桌面就绪
                    await Task.Delay(600);
                }

                if (_exiting) return;
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(InitializeServicesDeferred));
            });

            if (string.Equals(Environment.GetEnvironmentVariable("ALPHA_ISLAND_OPEN_SETTINGS"), "1", StringComparison.OrdinalIgnoreCase))
            {
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ShowSettings));
            }
        }

        /// <summary>尽早拉起 WASAPI 频谱采集（全程后台线程，失败不影响启动）。已启动则直接返回。</summary>
        private void StartAudioSpectrum()
        {
            if (_audio != null || _exiting) return;
            try
            {
                var audio = new AudioSpectrumService { Sensitivity = (float)_options.Sensitivity };
                audio.SpectrumUpdated += OnSpectrumUpdated;
                _audio = audio;
                StartInBackground(audio.Start, "Audio");
                System.Diagnostics.Debug.WriteLine("[Startup] 频谱采集已启动");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Audio] 启动异常: " + ex.Message);
            }
        }

        /// <summary>
        /// Phase 2 逐模块异步平滑拉起：所有重型服务严格按序逐一启动，
        /// 每一个模块启动成功后再加载下一个，模块间留出充裕调度间隔（600~700ms），
        /// 且硬件与 COM 驱动在后台线程执行，彻底杜绝大量模块并发争抢造成 UI 线程卡死。
        /// 必须在 UI 线程上调用（已通过 Dispatcher.BeginInvoke 保证）。
        /// </summary>
        private async void InitializeServicesDeferred()
        {
            if (_exiting) return;

            // 1. 歌词服务（纯内存/网络客户端，极度轻量无阻塞）
            try
            {
                _lyrics = new LyricsService { Enabled = _options.LyricsEnabled };
                System.Diagnostics.Debug.WriteLine("[Startup] 模块 1/8 [歌词服务] 就绪");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Lyrics] 初始化异常: " + ex.Message);
            }

            // 充裕时间片间隔：让出渲染与调度，等上一个就绪后平滑进入下一个
            await Task.Delay(600);
            if (_exiting) return;

            // 2. 全局热键与剪切板服务
            try
            {
                EnsureClipboardInitialized();
                System.Diagnostics.Debug.WriteLine("[Startup] 模块 2/8 [剪切板与热键] 就绪");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Clipboard] 初始化异常: " + ex.Message);
            }

            await Task.Delay(600);
            if (_exiting) return;

            // 3. 文件中转站
            try
            {
                _fileTransfer = new FileTransferService(Dispatcher);
                _fileTransfer.ItemsChanged += OnFileTransferItemsChanged;
                _fileTransfer.AllFilesEmptied += OnFileTransferAllFilesEmptied;
                TransferItemsList.ItemsSource = _fileTransfer.Items;
                System.Diagnostics.Debug.WriteLine("[Startup] 模块 3/8 [文件中转站] 就绪");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[FileTransfer] 初始化失败: " + ex.Message);
            }

            await Task.Delay(600);
            if (_exiting) return;

            // 4. 系统主音量监听（后台执行 COM 终结点绑定，带 3s 超时保护）
            //    注意 `_volumeService == null` 这个判据：构造函数里的 ApplyOptions() 已经可能
            //    创建并启动过同一个服务，这里若无条件 new，会留下一个永不注销
            //    RegisterEndpointNotificationCallback 的僵尸实例（事件重复触发、COM 回调泄漏）。
            if (_options.VolumeNotifyEnabled && _volumeService == null)
            {
                try
                {
                    var vol = new VolumeService(Dispatcher);
                    var volTask = Task.Run(() => vol.Start());
                    await Task.WhenAny(volTask, Task.Delay(3000));
                    vol.VolumeChanged += OnVolumeChanged;
                    _volumeService = vol;
                    System.Diagnostics.Debug.WriteLine("[Startup] 模块 4/8 [系统音量] 就绪");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Volume] 初始化失败: " + ex.Message);
                }
            }

            await Task.Delay(700);
            if (_exiting) return;

            // 5. 电池状态监听（后台线程执行首次硬件电源采样，带 3s 超时保护）
            //    同上：复用 ApplyOptions() 已经建好的实例，避免重复订阅与泄漏。
            if (_options.BatteryNotifyEnabled && _battery == null)
            {
                try
                {
                    var bat = new BatteryService(Dispatcher);
                    var batTask = Task.Run(() => bat.Start());
                    await Task.WhenAny(batTask, Task.Delay(3000));
                    bat.BatteryStatusChanged += OnBatteryStatusChanged;
                    _battery = bat;
                    System.Diagnostics.Debug.WriteLine("[Startup] 模块 5/8 [电池服务] 就绪");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Battery] 初始化失败: " + ex.Message);
                }
            }

            await Task.Delay(700);
            if (_exiting) return;

            // 6. 媒体：SMTC 系统媒体传输控制（后台请求 WinRT Session，带 4s 超时保护）
            try
            {
                var smtc = new SmtcService(Dispatcher);
                var smtcTask = Task.Run(async () => await smtc.InitializeAsync());
                await Task.WhenAny(smtcTask, Task.Delay(4000));
                smtc.NowPlayingChanged += OnNowPlayingChanged;
                smtc.Failed += (_, msg) => System.Diagnostics.Debug.WriteLine("[SMTC] " + msg);
                _smtc = smtc;
                System.Diagnostics.Debug.WriteLine("[Startup] 模块 6/8 [SMTC媒体] 就绪");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SMTC] 初始化异常: " + ex.Message);
            }

            await Task.Delay(700);
            if (_exiting) return;

            // 7. 音频：WASAPI 环回采集 → 频谱（已在 Window_Loaded 里提前启动，这里只做兜底）
            try
            {
                StartAudioSpectrum();
                System.Diagnostics.Debug.WriteLine("[Startup] 模块 7/8 [WASAPI频谱] 就绪");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Audio] 初始化异常: " + ex.Message);
            }

            await Task.Delay(700);
            if (_exiting) return;

            // 8. 蓝牙：设备连接通知（后台线程启动 WinRT DeviceWatchers，带 3s 超时保护）
            if (_options.BluetoothNotifyEnabled)
            {
                if (_bluetooth == null)
                {
                    try
                    {
                        var bt = new BluetoothService(Dispatcher);
                        bt.DeviceConnected += OnBluetoothDeviceConnected;
                        bt.DeviceDisconnected += OnBluetoothDeviceDisconnected;
                        bt.BatteryUpdated += OnBluetoothBatteryUpdated;
                        var btTask = Task.Run(() => bt.Start());
                        await Task.WhenAny(btTask, Task.Delay(3000));
                        _bluetooth = bt;
                        System.Diagnostics.Debug.WriteLine("[Startup] 模块 8/8 [蓝牙服务] 就绪");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[Bluetooth] 初始化失败: " + ex.Message);
                    }
                }
                else
                {
                    // ApplyOptions() 的早期路径已经建好并启动了同一实例（且只订阅了已连接/电量），
                    // 这里复用实例、补上断开通知，绝不再 new 第二个（否则旧实例的 WinRT watcher 永不释放）。
                    _bluetooth.DeviceDisconnected += OnBluetoothDeviceDisconnected;
                    System.Diagnostics.Debug.WriteLine("[Startup] 模块 8/8 [蓝牙服务] 复用既有实例");
                }
            }

            await Task.Delay(300);
            if (_exiting) return;

            _progressTimer?.Start();

            ApplyOptions();   // 建/刷新真实玻璃层，并按"有没有真实模糊"重算本色浓度

            if (_forcedState != null)
            {
                if (_forcedState.Equals("orb", StringComparison.OrdinalIgnoreCase))
                {
                    // 调试定态：冻结在"收回中间态那颗球"，用于逐像素核对球心图案是否为专辑封面
                    _state = IslandState.Bar;
                    FadeLayer(PillLayer, 0, 0);
                    FadeLayer(CardCarouselHost, 0, 0);
                    FadeLayer(CardWave, 0, 0);
                    PillLayer.IsHitTestVisible = false;
                    CardCarouselHost.IsHitTestVisible = false;
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
                    else if (_forcedState.Equals("bluetooth", StringComparison.OrdinalIgnoreCase))
                    {
                        // 调试定态：一级胶囊 + 蓝牙连接提示（3D模型旋转 + 设备名称 + 电量）
                        ShowBluetoothNotify(new BluetoothDeviceInfo("test-id", "AirPods Pro", BluetoothDeviceCategory.Audio, 85, true));
                    }
                }
            }

            // 调试：ALPHA_ISLAND_PILL_LYRIC 可以灌一段超长文本，专门用来验证胶囊歌词的跑马灯。
            string? forcedLyric = Environment.GetEnvironmentVariable("ALPHA_ISLAND_PILL_LYRIC");
            if (!string.IsNullOrEmpty(forcedLyric))
            {
                _pillLyricForce = forcedLyric;
                _pillLyricNormal = forcedLyric;
                ApplyPillLyricText(forcedLyric);
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

        private static IslandGeometry TrayGeometry =>
            new(IslandMetrics.TrayWidth, IslandMetrics.TrayHeight, IslandMetrics.TrayTop, IslandMetrics.TrayCorner);

        private IslandGeometry CardGeometry
        {
            get
            {
                double h = (_currentCardPage == 1 || LyricsAreaVisible) ? IslandMetrics.CardHeightLyrics : IslandMetrics.CardHeight;
                return new IslandGeometry(IslandMetrics.CardWidth, h, IslandMetrics.CardTop,
                    new CornerRadius(IslandMetrics.CardCornerRadius));
            }
        }

        /// <summary>歌词区是否真的占位（决定卡片高度）。</summary>
        private bool LyricsAreaVisible => _options.LyricsEnabled && _lyricsDoc != null && _lyricsDoc.HasLyrics;

        private IslandGeometry GeometryFor(IslandState state) => state switch
        {
            IslandState.Pill => PillGeometry,
            IslandState.Tray => TrayGeometry,
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

            // 离开细条就立刻掐掉收回动画中间态那颗球；若收缩回细条，则装配当前左侧状态的圆形过渡内容
            if (next == IslandState.Bar && prev != IslandState.Bar)
            {
                PrepareSiriOrbContent();
            }
            if (next != IslandState.Bar) CancelSiriOrb();

            bool growing = next > prev || (force && next == prev);
            var duration = growing ? IslandMetrics.ExpandDuration : IslandMetrics.GrowDuration;

            // 内容层不做独立时间轴的淡入淡出：显露度由主弹簧进度驱动（见 ApplyReveal），
            // 材质展开到哪儿内容就露到哪儿，源层在同一个进度上退场。
            _revealFrom = prev;
            _revealTo = next;
            _reveal = 0.0;
            ApplyReveal();

            if (_animator != null)
            {
                _animator.MorphTo(GeometryFor(next), _options.GelAnimation, duration,
                    new CubicEase { EasingMode = EasingMode.EaseInOut });
            }
            else
            {
                _reveal = 1.0;
                ApplyReveal();
            }

            // 歌词跑马灯只在胶囊态跑，其它形态停掉，别白耗动画帧。
            bool isPillNormal = next == IslandState.Pill && !_btNotifying && !_greetingShowing && !_volumeNotifying && !_batteryNotifying && !_clipboardNotifying;
            SetPillLyricScrollRunning(isPillNormal);
            PillLayer.IsHitTestVisible = isPillNormal;
            BtNotifyLayer.IsHitTestVisible = next == IslandState.Pill && _btNotifying;
            GreetingLayer.IsHitTestVisible = next == IslandState.Pill && _greetingShowing;
            VolumeNotifyLayer.IsHitTestVisible = next == IslandState.Pill && _volumeNotifying;
            BatteryNotifyLayer.IsHitTestVisible = next == IslandState.Pill && _batteryNotifying;
            ClipboardNotifyLayer.IsHitTestVisible = next == IslandState.Pill && _clipboardNotifying;
            FileTransferLayer.IsHitTestVisible = next == IslandState.Tray;
            CardCarouselHost.IsHitTestVisible = next == IslandState.Card;
            if (next == IslandState.Pill)
            {
                _lyricIndex = -2;
                PillLayer.BeginAnimation(UIElement.OpacityProperty, null);
                if (PillLayerTranslate != null)
                {
                    PillLayerTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                    PillLayerTranslate.X = 0;
                }
                SyncMusicPillContent();
            }
            else if (next == IslandState.Card)
            {
                PillLayer.BeginAnimation(UIElement.OpacityProperty, null);
                PillLayer.Opacity = 0;
                PillLayer.Visibility = Visibility.Collapsed;
            }
            else if (next == IslandState.Bar)
            {
                PillLayer.BeginAnimation(UIElement.OpacityProperty, null);
                PillLayer.Opacity = 0;
                PillLayer.Visibility = Visibility.Collapsed;
                CardCarouselHost.BeginAnimation(UIElement.OpacityProperty, null);
                CardCarouselHost.Opacity = 0;
                CardCarouselHost.Visibility = Visibility.Collapsed;
            }

            if (next == IslandState.Card)
            {
                int targetPage = 0;
                if (_clipboardLocked || _clipboardNotifying)
                {
                    targetPage = 1;
                }
                else if (_smtc?.Current?.HasMedia == true && _smtc.Current.IsPlaying)
                {
                    targetPage = 0;
                }
                else if (_clipboard?.History.Count > 0)
                {
                    targetPage = 1;
                }
                else
                {
                    targetPage = 0;
                }
                SwitchCardPage(targetPage, animated: false);
            }
            else
            {
                CardCarouselHost.BeginAnimation(UIElement.OpacityProperty, null);
                CardCarouselHost.Opacity = 0;
                CardCarouselHost.Visibility = Visibility.Collapsed;
                Keyboard.ClearFocus();
            }

            SyncVisualizers();
            SyncUpNextContent();
            SyncBtNotifyContent();
            SyncGreetingContent();
            SyncVolumeContent();
            SyncBatteryNotifyContent();
            SyncClipboardContent();
            SyncFileTransferContent();
            UpdateKaraokeHook();
            SyncBackdrop();
        }

        /// <summary>
        /// 缩回成圆过渡态：根据当前灵动岛左侧实时状态（音量/外设/问候/剪切板/中转站/专辑），在圆内装配对应的专属图标与背景色。
        /// 使形变收缩过渡极其自然连续，避免一律用 Siri 渐变或专辑封面。
        /// </summary>
        private void PrepareSiriOrbContent(IslandActionType? forcedAction = null)
        {
            var action = forcedAction ?? GetCurrentActionType();
            switch (action)
            {
                case IslandActionType.Volume:
                    OrbCover.Opacity = 0;
                    OrbCoverRing.Opacity = 0;
                    OrbGradient.Opacity = 0;
                    OrbIconContainer.Opacity = 1;
                    OrbIconContainer.Background = VolumeIconContainer.Background ?? new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0xE5, 0xFF));
                    OrbIconContainer.BorderBrush = VolumeIconContainer.BorderBrush ?? new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xE5, 0xFF));
                    OrbIconContainer.BorderThickness = new Thickness(1);
                    OrbIcon.Text = VolumeIcon.Text;
                    OrbIcon.Foreground = VolumeIcon.Foreground ?? new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));
                    break;

                case IslandActionType.Battery:
                    OrbCover.Opacity = 0;
                    OrbCoverRing.Opacity = 0;
                    OrbGradient.Opacity = 0;
                    OrbIconContainer.Opacity = 1;
                    OrbIconContainer.Background = BatteryIconContainer.Background ?? new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0xFF, 0x88));
                    OrbIconContainer.BorderBrush = BatteryIconContainer.BorderBrush ?? new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xFF, 0x88));
                    OrbIconContainer.BorderThickness = new Thickness(1);
                    OrbIcon.Text = BatteryIcon.Text;
                    OrbIcon.Foreground = BatteryIcon.Foreground ?? new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x88));
                    break;

                case IslandActionType.Clipboard:
                    OrbCover.Opacity = 0;
                    OrbCoverRing.Opacity = 0;
                    OrbGradient.Opacity = 0;
                    OrbIconContainer.Opacity = 1;
                    OrbIconContainer.Background = ClipboardIconContainer.Background ?? new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0xF0, 0xFF));
                    OrbIconContainer.BorderBrush = ClipboardIconContainer.BorderBrush ?? new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xF0, 0xFF));
                    OrbIconContainer.BorderThickness = new Thickness(1);
                    OrbIcon.Text = ClipboardIcon.Text;
                    OrbIcon.Foreground = ClipboardIcon.Foreground ?? new SolidColorBrush(Color.FromRgb(0x00, 0xF0, 0xFF));
                    break;

                case IslandActionType.Bluetooth:
                    OrbCover.Opacity = 0;
                    OrbCoverRing.Opacity = 0;
                    OrbGradient.Opacity = 0;
                    OrbIconContainer.Opacity = 1;
                    OrbIconContainer.Background = BtIconContainer.Background ?? new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
                    OrbIconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
                    OrbIconContainer.BorderThickness = new Thickness(1);
                    if (BtDeviceIcon.Visibility == Visibility.Visible && !string.IsNullOrEmpty(BtDeviceIcon.Text))
                    {
                        OrbIcon.Text = BtDeviceIcon.Text;
                        OrbIcon.Foreground = BtDeviceIcon.Foreground ?? new SolidColorBrush(Colors.White);
                    }
                    else
                    {
                        // 耳机 3D 旋转模型在圆球中以经典微光耳机图标呈现
                        OrbIcon.Text = "\uE7F6";
                        OrbIcon.Foreground = new SolidColorBrush(Colors.White);
                    }
                    break;

                case IslandActionType.Greeting:
                    OrbCover.Opacity = 0;
                    OrbCoverRing.Opacity = 0;
                    OrbGradient.Opacity = 0;
                    OrbIconContainer.Opacity = 1;
                    OrbIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0xE5, 0xFF));
                    OrbIconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xE5, 0xFF));
                    OrbIconContainer.BorderThickness = new Thickness(1);
                    OrbIcon.Text = "\uE735";
                    OrbIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));
                    break;

                case IslandActionType.FileTransfer:
                    OrbCover.Opacity = 0;
                    OrbCoverRing.Opacity = 0;
                    OrbGradient.Opacity = 0;
                    OrbIconContainer.Opacity = 1;
                    OrbIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0xE5, 0xFF));
                    OrbIconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xE5, 0xFF));
                    OrbIconContainer.BorderThickness = new Thickness(1);
                    OrbIcon.Text = "\uE8B7";
                    OrbIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));
                    break;

                case IslandActionType.Music:
                default:
                    // 音乐媒体状态：优先更新封面笔刷，有封面显示封面，无封面显示优雅音符图标
                    var art = _smtc?.Current?.Artwork ?? _lastArt;
                    if (art != null)
                    {
                        OrbCoverBrush.ImageSource = art;
                    }
                    bool hasCover = OrbCoverBrush.ImageSource != null;
                    if (hasCover)
                    {
                        OrbCover.Opacity = 1;
                        OrbCoverRing.Opacity = 1;
                        OrbGradient.Opacity = 0;
                        OrbIconContainer.Opacity = 0;
                    }
                    else
                    {
                        OrbCover.Opacity = 0;
                        OrbCoverRing.Opacity = 0;
                        OrbGradient.Opacity = 0;
                        OrbIconContainer.Opacity = 1;
                        OrbIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0xE5, 0xFF));
                        OrbIconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xE5, 0xFF));
                        OrbIconContainer.BorderThickness = new Thickness(1);
                        OrbIcon.Text = "\uE767";
                        OrbIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));
                    }
                    break;
            }
        }

        /// <summary>把"收回动画中间态那颗球"复位：停掉两条动画并把不可见状态写实。</summary>
        private void CancelSiriOrb()
        {
            SiriOrb.BeginAnimation(OpacityProperty, null);
            SiriOrb.Opacity = 0;
            OrbIconContainer.Opacity = 0;
            OrbCover.Opacity = 0;
            OrbCoverRing.Opacity = 0;
            OrbGradient.Opacity = 0;
            SiriOrbTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            SiriOrbTranslate.X = -40;
        }

        /// <summary>PillLyric 的唯一写入口 —— 顺手记住"正常文本"，换曲提示结束后好还原。</summary>
        private void SetPillLyric(string text)
        {
            // 调试灌词期间屏蔽真实歌词，否则歌词服务每 ~100ms 覆盖一次会把跑马灯动画一直打回起点。
            if (_pillLyricForce != null) return;

            _pillLyricNormal = text;
            if (!UpNextActive) ApplyPillLyricText(text);
        }

        // ===================== 一级胶囊歌词的跑马灯 =====================

        private const double PillLyricGap = 56.0;      // 两份文本之间的空档，和 XAML 里 Echo 的 Margin 对齐
        private const double PillLyricSpeed = 36.0;    // 滚动速度 px/s
        private const double PillLyricLeadIn = 1.4;    // 第一遍之前的停顿（秒）
        private const double PillLyricTailPause = 0.9; // 一轮结束后的停顿（秒）

        /// <summary>
        /// "宿主宽度还没量出来"时的补测次数预算。见 <see cref="UpdatePillLyricScroll"/>：
        /// 只允许有限次重投递，避免把自己无限排进 Loaded 队列。
        /// </summary>
        private const int PillLyricRetryLimit = 5;
        private int _pillLyricRetry;

        /// <summary>写入胶囊歌词：文本变化时重新量宽度，决定居中静止还是跑马灯滚动。</summary>
        private void ApplyPillLyricText(string text)
        {
            text = text ?? string.Empty;

            // 新的文本 = 新的量宽需求，重新给足补测预算。
            _pillLyricRetry = 0;

            if (PillLyric != null && PillLyric.Text == text)
            {
                UpdatePillLyricScroll();
                return;
            }

            if (PillLyric != null) PillLyric.Text = text;
            if (PillLyricEcho != null) PillLyricEcho.Text = text;

            UpdatePillLyricScroll();
        }

        private void PillLyricHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 布局真的变了：重置补测预算，让 UpdatePillLyricScroll 有机会重新量宽度。
            _pillLyricRetry = 0;
            UpdatePillLyricScroll();
        }

        /// <summary>
        /// 放得下 → 收起副本、整条居中；放不下 → 亮出副本，挂一个"停顿 · 滚动 · 停顿"的循环动画。
        /// 宽度还没算出来（ActualWidth ≈ 0）时异步排入 Loaded 队列补测，**但只补测有限次**。
        /// </summary>
        private void UpdatePillLyricScroll()
        {
            if (PillLyric == null || PillLyricEcho == null || PillLyricHost == null || PillLyricScroll == null) return;

            PillLyricScroll.BeginAnimation(TranslateTransform.XProperty, null);
            PillLyricScroll.X = 0;

            double available = PillLyricHost.ActualWidth;
            if (available <= 1.0)
            {
                // 布局还没完成（宿主还是 0 宽，例如所在图层收起 / 尚未 Measure）。
                //
                // ⚠ 这里**绝对不能无上限地重投递自己**：Loaded(6) 的优先级高于
                // Background(4)，一旦被无条件续命，Dispatcher 队列里就永远存在一个 6 级项，
                // Background 队列中的 InitializeServicesDeferred（启动服务加载）与
                // Input(5) 的输入分发会被**永久饿死** —— 实测表现为"启动即卡死"：
                // 岛体画得出来但点不动，歌词/剪贴板/媒体/频谱/蓝牙等模块一个都起不来。
                //
                // 因此只给有限次补测预算；真正的布局变化由 PillLyricHost_SizeChanged 重置预算后再补测。
                if (++_pillLyricRetry <= PillLyricRetryLimit)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdatePillLyricScroll));
                }
                return;
            }

            _pillLyricRetry = 0;

            // 必须重新量一次：刚写完 Text 时 DesiredSize 还是上一段文本的宽度（实测会拿到 73px 这种陈旧值），
            // 直接用它判断会把长歌词误判成"放得下"。用无限宽度量，得到的就是整句话的自然宽度。
            PillLyric.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double textWidth = PillLyric.DesiredSize.Width;
            if (textWidth <= 1.0) return;

            if (textWidth <= available - 1.0)
            {
                if (PillLyricEcho.Visibility != Visibility.Collapsed) PillLyricEcho.Visibility = Visibility.Collapsed;
                PillLyricScroll.X = Math.Max(0.0, (available - textWidth) / 2.0);
                return;
            }

            if (PillLyricEcho.Visibility != Visibility.Visible) PillLyricEcho.Visibility = Visibility.Visible;

            double dist = textWidth + PillLyricGap;
            var travel = TimeSpan.FromSeconds(dist / PillLyricSpeed);
            var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(PillLyricLeadIn))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(-dist, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(PillLyricLeadIn) + travel)));
            anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(-dist,
                KeyTime.FromTimeSpan(TimeSpan.FromSeconds(PillLyricLeadIn) + travel + TimeSpan.FromSeconds(PillLyricTailPause))));
            PillLyricScroll.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        /// <summary>进入/离开胶囊态时起停跑马灯，细条状态下不白耗动画帧。</summary>
        private void SetPillLyricScrollRunning(bool running)
        {
            if (PillLyricScroll == null) return;

            if (running)
            {
                // 重新进入胶囊态：宿主会被重新布局，补测预算重置后再量一次。
                _pillLyricRetry = 0;
                UpdatePillLyricScroll();
            }
            else
            {
                PillLyricScroll.BeginAnimation(TranslateTransform.XProperty, null);
            }
        }

        /// <summary>
        /// 换曲 / 暂停提示期间：一级胶囊右侧的频谱整块换成状态指示，中间显示曲名。
        /// 提示结束后原样还原（歌词由 UpdateKaraokeHook / ApplyLyricTexts 重新驱动）。
        /// </summary>
        private void SyncUpNextContent()
        {
            bool up = _upNextShowing && UpNextActive;
            PillSpectrum.Visibility = up ? Visibility.Collapsed : Visibility.Visible;
            PillUpNext.Visibility = up ? Visibility.Visible : Visibility.Collapsed;

            if (up)
            {
                if (_upNextKind == "pause")
                {
                    PillUpNextIcon.Text = "\uE769"; // 暂停图标
                    PillUpNextIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xAA, 0x00)); // 柔和橙黄
                    PillUpNextText.Text = "已暂停";
                }
                else if (_upNextKind == "play")
                {
                    PillUpNextIcon.Text = "\uE768"; // 播放图标
                    PillUpNextIcon.Foreground = (System.Windows.Media.Brush)FindResource("AccentCyan");
                    PillUpNextText.Text = "正在播放";
                }
                else
                {
                    PillUpNextIcon.Text = "\uE893"; // 下一曲图标
                    PillUpNextIcon.Foreground = (System.Windows.Media.Brush)FindResource("AccentCyan");
                    PillUpNextText.Text = "接下来";
                }
                ApplyPillLyricText(_upNextTitle);
            }
            else
            {
                PillSpectrum.Visibility = Visibility.Visible;
                PillUpNext.Visibility = Visibility.Collapsed;
                if (!string.IsNullOrWhiteSpace(_pillLyricNormal))
                {
                    ApplyPillLyricText(_pillLyricNormal);
                }
            }
            SyncVisualizers();
        }

        private void ScheduleUpNextDismiss()
        {
            _upNextTimer?.Stop();
            double seconds = Math.Max(1.0, _options.UpNextSeconds);
            _upNextTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _upNextTimer.Tick += (_, __) =>
            {
                _upNextTimer.Stop();
                DismissUpNext();
            };
            _upNextTimer.Start();
        }

        private void DismissUpNext()
        {
            _upNextTimer?.Stop();
            _upNextShowing = false;
            _upNextUntil = DateTime.MinValue;
            _lyricIndex = -2;
            SyncUpNextContent();
            SyncVisualizers();
            SyncMusicPillContent();

            // 若未置顶，且光标不在热区，且未保持播放展开，则收回
            if (!_isPinned && _state == IslandState.Pill && !IsCursorInHotZone())
            {
                if (!_options.KeepCompactWhilePlaying || _smtc?.Current?.IsPlaying != true)
                {
                    CollapseToBar();
                }
            }
        }

        /// <summary>
        /// 换曲提示：弹出一级胶囊，中间是新曲名、右侧是"接下来"，持续 UpNextSeconds 秒后自动还原频谱与歌词。
        /// </summary>
        private void BeginUpNext(NowPlayingInfo info)
        {
            if (!_options.UpNextEnabled) return;
            if (_greetingShowing && GreetingActive) return;

            _upNextTitle = info.DisplayTitle;
            _upNextKind = "upnext";
            _upNextUntil = DateTime.UtcNow.AddSeconds(_options.UpNextSeconds);
            _upNextShowing = true;
            ScheduleUpNextDismiss();
            // 已经展开说明用户正看着，只换内容、不动形态
            if (_state == IslandState.Bar) SetState(IslandState.Pill);
            else SyncUpNextContent();
        }

        /// <summary>
        /// 暂停提示：音乐暂停时弹出一级胶囊，中间是曲名、右侧是"已暂停"，持续 UpNextSeconds 秒后自动还原频谱与歌词。
        /// </summary>
        private void BeginPausePrompt(NowPlayingInfo info)
        {
            if (!_options.PauseNotifyEnabled) return;
            if (_greetingShowing && GreetingActive) return;

            _upNextTitle = info.DisplayTitle;
            _upNextKind = "pause";
            _upNextUntil = DateTime.UtcNow.AddSeconds(_options.UpNextSeconds);
            _upNextShowing = true;
            ScheduleUpNextDismiss();
            if (_state == IslandState.Bar) SetState(IslandState.Pill);
            else SyncUpNextContent();
        }

        /// <summary>
        /// 恢复播放提示：音乐继续播放时弹出一级胶囊，中间是曲名、右侧是"正在播放"，持续 UpNextSeconds 秒后自动还原频谱与歌词。
        /// </summary>
        private void BeginPlayPrompt(NowPlayingInfo info)
        {
            if (!_options.PlayNotifyEnabled) return;
            if (_greetingShowing && GreetingActive) return;

            _upNextTitle = info.DisplayTitle;
            _upNextKind = "play";
            _upNextUntil = DateTime.UtcNow.AddSeconds(_options.UpNextSeconds);
            _upNextShowing = true;
            ScheduleUpNextDismiss();
            if (_state == IslandState.Bar) SetState(IslandState.Pill);
            else SyncUpNextContent();
        }

        private void ShowStartupGreeting()
        {
            if (_exiting || !_options.GreetingEnabled) return;

            _currentAction = IslandActionType.Greeting;
            _greetingShowing = true;
            _greetingUntil = DateTime.UtcNow.AddSeconds(_options.GreetingSeconds);
            GreetingText.Text = string.IsNullOrWhiteSpace(_options.GreetingText) ? "欢迎回来" : _options.GreetingText;

            ScheduleGreetingDismiss();

            if (_state == IslandState.Bar)
            {
                SetState(IslandState.Pill);
            }
            else
            {
                SyncGreetingContent();
            }
        }

        private void ScheduleGreetingDismiss()
        {
            _greetingTimer?.Stop();
            double seconds = Math.Max(1.0, _options.GreetingSeconds);
            _greetingTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromSeconds(seconds)
            };
            _greetingTimer.Tick += (_, __) =>
            {
                _greetingTimer.Stop();
                DismissGreeting();
            };
            _greetingTimer.Start();
        }

        private void DismissGreeting()
        {
            _greetingTimer?.Stop();
            _greetingFinishedTcs?.TrySetResult(true);
            if (!_greetingShowing && !GreetingActive) return;

            _greetingShowing = false;
            _greetingUntil = DateTime.MinValue;

            // 若未置顶，且光标不在热区，且没有正在播放的媒体，自动平滑收回至细条
            if (!_isPinned && _state == IslandState.Pill && !IsCursorInHotZone())
            {
                if (!HasActiveNotification && (!_options.KeepCompactWhilePlaying || _smtc?.Current?.IsPlaying != true))
                {
                    _currentAction = IslandActionType.Greeting;
                    CollapseToBar();
                    return;
                }
            }

            SyncGreetingContent();
            SyncNotificationLayers();
        }

        // ================= 统一通知管理与左侧音乐收缩圆球 =================

        private bool HasActiveNotification =>
            (_greetingShowing && GreetingActive) ||
            (_btNotifying && BtNotifyActive) ||
            (_volumeNotifying && VolumeNotifyActive) ||
            (_batteryNotifying && BatteryNotifyActive) ||
            (ClipboardNotifyActive);

        private void SyncNotificationLayers()
        {
            bool showGreeting = _greetingShowing && GreetingActive && _state == IslandState.Pill;
            bool showBt = _btNotifying && BtNotifyActive && _state == IslandState.Pill;
            bool showVol = _volumeNotifying && VolumeNotifyActive && _state == IslandState.Pill;
            bool showBattery = _batteryNotifying && BatteryNotifyActive && _state == IslandState.Pill;
            bool showClipboard = ClipboardNotifyActive && (_state == IslandState.Pill || (_state == IslandState.Card && _currentCardPage == 1));

            bool anyNotification = showGreeting || showBt || showVol || showBattery || (showClipboard && _state == IslandState.Pill);
            bool isMusicActive = _isPinned || (_smtc?.Current?.HasMedia == true);

            // 1. 各通知图层互斥显隐（确保 Opacity 与 Visibility 同步，杜绝出现空白岛）
            GreetingLayer.Opacity = showGreeting ? 1 : 0;
            GreetingLayer.Visibility = showGreeting ? Visibility.Visible : Visibility.Collapsed;
            GreetingLayer.IsHitTestVisible = showGreeting;

            BtNotifyLayer.Opacity = showBt ? 1 : 0;
            BtNotifyLayer.Visibility = showBt ? Visibility.Visible : Visibility.Collapsed;
            BtNotifyLayer.IsHitTestVisible = showBt;
            BtDeviceModel.IsRotating = showBt;

            VolumeNotifyLayer.Opacity = showVol ? 1 : 0;
            VolumeNotifyLayer.Visibility = showVol ? Visibility.Visible : Visibility.Collapsed;
            VolumeNotifyLayer.IsHitTestVisible = showVol;

            BatteryNotifyLayer.Opacity = showBattery ? 1 : 0;
            BatteryNotifyLayer.Visibility = showBattery ? Visibility.Visible : Visibility.Collapsed;
            BatteryNotifyLayer.IsHitTestVisible = showBattery;

            if (_state == IslandState.Pill)
            {
                ClipboardNotifyLayer.Opacity = showClipboard ? 1 : 0;
                ClipboardNotifyLayer.Visibility = showClipboard ? Visibility.Visible : Visibility.Collapsed;
                ClipboardNotifyLayer.IsHitTestVisible = showClipboard;
            }
            else
            {
                ClipboardNotifyLayer.Opacity = 0;
                ClipboardNotifyLayer.Visibility = Visibility.Collapsed;
                ClipboardNotifyLayer.IsHitTestVisible = false;
            }

            // 2. 音乐图层（PillLayer）：如果有其他通知在展示，完全隐藏，避免叠层！
            bool showPill = _state == IslandState.Pill && !anyNotification;
            PillLayer.IsHitTestVisible = showPill;
            SetPillLyricScrollRunning(showPill);

            // 3. 左侧音乐收缩圆球：
            // 关键：在欢迎界面期间（showGreeting），绝对不显示音乐圆球，仅显示欢迎界面！
            bool allowMusicOrb = isMusicActive && !showGreeting && !_greetingShowing && !GreetingActive && (_state == IslandState.Pill || _state == IslandState.Tray) && (showBt || showVol || showBattery || (showClipboard && _state == IslandState.Pill) || _state == IslandState.Tray);

            if (showGreeting || _greetingShowing || GreetingActive)
            {
                HideMusicOrb(immediate: true);
            }
            else if (allowMusicOrb)
            {
                ShowMusicOrb();
            }
            else
            {
                HideMusicOrb();
            }

            bool musicOrbBusy = _isMusicOrbShowing || _isMusicOrbReturning;
            if (showPill && !musicOrbBusy)
            {
                PillLayer.BeginAnimation(UIElement.OpacityProperty, null);
                PillLayer.Opacity = 1;
                PillLayer.Visibility = Visibility.Visible;
                if (PillCoverHost != null)
                {
                    PillCoverHost.Opacity = 1;
                }
                if (PillLayerTranslate != null)
                {
                    PillLayerTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                    PillLayerTranslate.X = 0;
                }
                _lyricIndex = -2;
                SyncMusicPillContent();
            }
            else if (!musicOrbBusy)
            {
                // 非一级胶囊展示期，彻底清除 PillLayer，严禁叠层或浮在细条上
                PillLayer.BeginAnimation(UIElement.OpacityProperty, null);
                PillLayer.Opacity = 0;
                PillLayer.Visibility = Visibility.Collapsed;
            }

            // 4. 若正处于剪切板历史卡片态，同步刷新列表
            if (_state == IslandState.Card && _currentCardPage == 1)
            {
                SyncClipboardHistoryList();
            }

            SyncVisualizers();
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
            PillLayerTranslate.BeginAnimation(TranslateTransform.XProperty, moveAnim);

            var fadeAnim = new DoubleAnimation(PillLayer.Opacity, 0.0, TimeSpan.FromMilliseconds(200));
            fadeAnim.Completed += (_, _) =>
            {
                PillLayer.BeginAnimation(UIElement.OpacityProperty, null);
                PillLayer.Opacity = 0;
                PillLayer.Visibility = Visibility.Collapsed;
            };
            PillLayer.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
        }

        private void AnimatePillLayerBloom()
        {
            if (PillLayerTranslate == null) return;
            SyncMusicPillContent();
            if (PillCoverHost != null)
            {
                PillCoverHost.Opacity = 1;
            }
            PillLayer.Visibility = Visibility.Visible;

            var moveAnim = new DoubleAnimation(-18, 0, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            moveAnim.Completed += (_, _) =>
            {
                PillLayerTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                PillLayerTranslate.X = 0;
            };
            PillLayerTranslate.BeginAnimation(TranslateTransform.XProperty, moveAnim);

            var fadeAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(240));
            fadeAnim.Completed += (_, _) =>
            {
                PillLayer.BeginAnimation(UIElement.OpacityProperty, null);
                PillLayer.Opacity = 1.0;
            };
            PillLayer.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
        }

        private void ShowMusicOrb()
        {
            if (MusicOrbBubble == null) return;
            if (_greetingShowing || GreetingActive) return;
            if (_isMusicOrbShowing) return;
            _isMusicOrbShowing = true;
            _isMusicOrbReturning = false;

            var art = _lastArt ?? _smtc?.Current?.Artwork;
            bool hasCover = art != null;
            MusicOrbCoverBrush.ImageSource = art;
            MusicOrbCover.Opacity = hasCover ? 1 : 0;
            MusicOrbGradient.Opacity = hasCover ? 0 : 1;

            MusicOrbBubble.Visibility = Visibility.Visible;
            MusicOrbBubble.Opacity = 1.0;
            MusicOrbTranslate.X = -148;
            MusicOrbTranslate.Y = 0;
            MusicOrbScale.ScaleX = 1.0;
            MusicOrbScale.ScaleY = 1.0;

            // 关键：立即隐藏主岛 PillCoverHost，从这一刻起封面由 MusicOrbBubble 接管向左滑出，杜绝画面出现两个封面
            if (PillCoverHost != null)
            {
                PillCoverHost.Opacity = 0;
            }

            double targetX = _state == IslandState.Tray ? -222 : -204;
            var splineGlide = new KeySpline(0.16, 0.90, 0.35, 1.0);

            // 1. 水平位移动画（从主岛封面位置平滑向左滑出，不突兀闪烁）
            var moveFrames = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
            moveFrames.KeyFrames.Add(new DiscreteDoubleKeyFrame(-148, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            moveFrames.KeyFrames.Add(new SplineDoubleKeyFrame(targetX - 4.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), splineGlide));
            moveFrames.KeyFrames.Add(new SplineDoubleKeyFrame(targetX, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420))));

            // 2. 水平轻微弹性形变
            var scaleXFrames = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
            scaleXFrames.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.00, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            scaleXFrames.KeyFrames.Add(new SplineDoubleKeyFrame(1.15, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160)), splineGlide));
            scaleXFrames.KeyFrames.Add(new SplineDoubleKeyFrame(0.96, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320))));
            scaleXFrames.KeyFrames.Add(new SplineDoubleKeyFrame(1.00, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420))));

            // 3. 垂直轻微弹性形变
            var scaleYFrames = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
            scaleYFrames.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.00, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            scaleYFrames.KeyFrames.Add(new SplineDoubleKeyFrame(0.88, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160)), splineGlide));
            scaleYFrames.KeyFrames.Add(new SplineDoubleKeyFrame(1.04, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320))));
            scaleYFrames.KeyFrames.Add(new SplineDoubleKeyFrame(1.00, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420))));

            MusicOrbTranslate.BeginAnimation(TranslateTransform.XProperty, moveFrames);
            MusicOrbScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXFrames);
            MusicOrbScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYFrames);

            AnimatePillLayerContract();
        }

        private void HideMusicOrb(bool immediate = false)
        {
            if (MusicOrbBubble == null) return;
            if (!_isMusicOrbShowing && !_isMusicOrbReturning && MusicOrbBubble.Visibility == Visibility.Collapsed) return;
            _isMusicOrbShowing = false;

            if (immediate)
            {
                _isMusicOrbReturning = false;
                MusicOrbTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                MusicOrbTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                MusicOrbScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                MusicOrbScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                MusicOrbBubble.BeginAnimation(UIElement.OpacityProperty, null);
                MusicOrbBubble.Visibility = Visibility.Collapsed;
                MusicOrbBubble.Opacity = 0;
                MusicOrbTranslate.X = -148;
                MusicOrbTranslate.Y = 0;
                MusicOrbScale.ScaleX = 1.0;
                MusicOrbScale.ScaleY = 1.0;
                if (PillCoverHost != null)
                {
                    PillCoverHost.Opacity = 1;
                }
                return;
            }

            if (_isMusicOrbReturning) return;
            _isMusicOrbReturning = true;

            double currentX = MusicOrbTranslate.X;
            var splineReturn = new KeySpline(0.2, 0.8, 0.4, 1.0);

            // 1. 水平位移（从脱离位置平滑冲回主岛封面 X = -148）
            var moveFrames = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
            moveFrames.KeyFrames.Add(new DiscreteDoubleKeyFrame(currentX, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            moveFrames.KeyFrames.Add(new SplineDoubleKeyFrame(-148, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), splineReturn));

            var scaleXFrames = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
            scaleXFrames.KeyFrames.Add(new DiscreteDoubleKeyFrame(MusicOrbScale.ScaleX, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            scaleXFrames.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), splineReturn));

            var scaleYFrames = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
            scaleYFrames.KeyFrames.Add(new DiscreteDoubleKeyFrame(MusicOrbScale.ScaleY, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            scaleYFrames.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), splineReturn));

            moveFrames.Completed += (_, __) =>
            {
                _isMusicOrbReturning = false;
                if (!_isMusicOrbShowing)
                {
                    MusicOrbBubble.Visibility = Visibility.Collapsed;
                    MusicOrbBubble.Opacity = 0;
                    MusicOrbTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                    MusicOrbTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                    MusicOrbScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    MusicOrbScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    MusicOrbBubble.BeginAnimation(UIElement.OpacityProperty, null);
                    MusicOrbTranslate.X = -148;
                    MusicOrbTranslate.Y = 0;
                    MusicOrbScale.ScaleX = 1.0;
                    MusicOrbScale.ScaleY = 1.0;

                    // 关键：圆球完全滑回主岛原位交接后，才显现 PillCoverHost 并向右舒展展开歌词！
                    if (PillCoverHost != null)
                    {
                        PillCoverHost.Opacity = 1;
                    }

                    if (_state == IslandState.Pill)
                    {
                        AnimatePillLayerBloom();
                    }
                }
            };

            MusicOrbTranslate.BeginAnimation(TranslateTransform.XProperty, moveFrames);
            MusicOrbScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXFrames);
            MusicOrbScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYFrames);
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
                    MusicOrbBubble.Visibility = Visibility.Collapsed;
                    MusicOrbTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                    MusicOrbTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                    MusicOrbScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    MusicOrbScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    MusicOrbBubble.BeginAnimation(UIElement.OpacityProperty, null);
                    MusicOrbBubble.Opacity = 0;
                    MusicOrbTranslate.X = -148;
                    MusicOrbTranslate.Y = 0;
                    MusicOrbScale.ScaleX = 1.0;
                    MusicOrbScale.ScaleY = 1.0;

                    if (PillCoverHost != null)
                    {
                        PillCoverHost.Opacity = 1;
                    }
                }
            };

            MusicOrbTranslate.BeginAnimation(TranslateTransform.XProperty, moveXAnim);
            MusicOrbTranslate.BeginAnimation(TranslateTransform.YProperty, moveYAnim);
            MusicOrbScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
            MusicOrbScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
            MusicOrbBubble.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        }

        private void MusicOrbBubble_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DismissActiveNotifications();
            e.Handled = true;
        }

        private void DismissActiveNotifications()
        {
            _greetingTimer?.Stop();
            _greetingShowing = false;
            _btNotifying = false;
            _volumeNotifying = false;
            _batteryNotifying = false;
            _clipboardNotifying = false;
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
            if (!_greetingShowing || !GreetingActive)
            {
                _greetingShowing = false;
            }
            SyncNotificationLayers();
        }

        // ================= 音量调节 =================

        private void OnVolumeChanged(object? sender, VolumeInfo info)
        {
            if (_exiting || !_options.VolumeNotifyEnabled) return;
            ShowVolumeNotify(info.Percent, info.IsMuted);
        }

        private void ShowVolumeNotify(int percent, bool isMuted)
        {
            if (_greetingShowing && GreetingActive) return;
            _currentAction = IslandActionType.Volume;
            _volumeNotifying = true;
            _volumeNotifyUntil = DateTime.UtcNow.AddSeconds(_options.VolumeNotifySeconds);

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
                VolumeProgressFill.Background = cyan;
            }

            VolumeText.Text = isMuted ? "静音" : $"{percent}%";

            // 动态计算进度条填充宽度 (根据实际槽体宽度，缺省取 180px)
            double trackWidth = VolumeTrackContainer.ActualWidth > 0 ? VolumeTrackContainer.ActualWidth : 180.0;
            VolumeProgressFill.Width = Math.Clamp((trackWidth * percent) / 100.0, 0, trackWidth);

            if (_state == IslandState.Bar)
            {
                SetState(IslandState.Pill);
            }
            else
            {
                SyncVolumeContent();
            }
        }

        private void SyncVolumeContent()
        {
            if (!_volumeNotifying || !VolumeNotifyActive)
            {
                _volumeNotifying = false;
            }
            SyncNotificationLayers();
        }

        // ================= 电池状态通知 =================

        private void OnBatteryStatusChanged(object? sender, BatteryStatusInfo info)
        {
            if (_exiting || !_options.BatteryNotifyEnabled) return;
            ShowBatteryNotify(info);
        }

        private void ShowBatteryNotify(BatteryStatusInfo info)
        {
            if (_greetingShowing || GreetingActive)
            {
                _greetingTimer?.Stop();
                _greetingShowing = false;
                _greetingUntil = DateTime.MinValue;
                GreetingLayer.Opacity = 0;
                GreetingLayer.Visibility = Visibility.Collapsed;
            }

            _currentAction = IslandActionType.Battery;
            _batteryNotifying = true;
            _batteryNotifyUntil = DateTime.UtcNow.AddSeconds(_options.BatteryNotifySeconds);

            // 更新波纹特效
            if (_options.BatteryRippleEnabled)
            {
                if (info.EventType == BatteryPowerEvent.PluggedIn || (info.EventType == BatteryPowerEvent.None && info.IsCharging))
                {
                    BatteryRipple.Trigger(isCharging: true);
                }
                else if (info.EventType == BatteryPowerEvent.Unplugged || info.EventType == BatteryPowerEvent.LowBattery || info.EventType == BatteryPowerEvent.CriticalBattery)
                {
                    BatteryRipple.Trigger(isCharging: false);
                }
            }

            // 更新电池图标与提示文字
            string statusDesc;
            Color accentColor;

            if (info.EventType == BatteryPowerEvent.PluggedIn)
            {
                if (info.ChargeType == BatteryChargeType.ConservationMode)
                {
                    statusDesc = $"已连接电源 · 电池保养中 ({info.Percent}%)";
                    accentColor = Color.FromRgb(0x00, 0xE5, 0xFF);
                    BatteryBoltIcon.Visibility = Visibility.Visible;
                }
                else
                {
                    statusDesc = info.ChargeType switch
                    {
                        BatteryChargeType.PD => "已连接电源 · PD 快充",
                        BatteryChargeType.StandardAC => "已连接电源 · 电源适配器",
                        _ => "已连接电源"
                    };
                    accentColor = Color.FromRgb(0x00, 0xFF, 0x88);
                    BatteryBoltIcon.Visibility = Visibility.Visible;
                }
            }
            else if (info.EventType == BatteryPowerEvent.Unplugged)
            {
                statusDesc = "已拔出电源 · 电池供电中";
                accentColor = Color.FromRgb(0xFF, 0x91, 0x00);
                BatteryBoltIcon.Visibility = Visibility.Collapsed;
            }
            else if (info.EventType == BatteryPowerEvent.FullyCharged)
            {
                statusDesc = "电池已充满";
                accentColor = Color.FromRgb(0x00, 0xFF, 0x88);
                BatteryBoltIcon.Visibility = Visibility.Collapsed;
            }
            else if (info.EventType == BatteryPowerEvent.CriticalBattery)
            {
                statusDesc = "⚠ 电池电量严重不足";
                accentColor = Color.FromRgb(0xFF, 0x33, 0x33);
                BatteryBoltIcon.Visibility = Visibility.Collapsed;
            }
            else if (info.EventType == BatteryPowerEvent.LowBattery)
            {
                statusDesc = "电池电量不足";
                accentColor = Color.FromRgb(0xFF, 0x91, 0x00);
                BatteryBoltIcon.Visibility = Visibility.Collapsed;
            }
            else
            {
                if (info.ChargeType == BatteryChargeType.ConservationMode)
                {
                    statusDesc = $"已连接电源 · 电池保养中 ({info.Percent}%)";
                    accentColor = Color.FromRgb(0x00, 0xE5, 0xFF);
                    BatteryBoltIcon.Visibility = Visibility.Visible;
                }
                else
                {
                    statusDesc = info.IsCharging ? "正在充电" : "电池供电中";
                    accentColor = info.IsCharging ? Color.FromRgb(0x00, 0xFF, 0x88) : Color.FromRgb(0xE0, 0xE0, 0xE0);
                    BatteryBoltIcon.Visibility = info.IsCharging ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            BatteryStatusText.Text = statusDesc;
            BatteryPercentText.Text = $"{info.Percent}%";

            if (info.IsCharging)
            {
                BatteryIcon.Text = "\uEA93";
            }
            else
            {
                if (info.Percent >= 90) BatteryIcon.Text = "\uE83F";
                else if (info.Percent >= 60) BatteryIcon.Text = "\uE855";
                else if (info.Percent >= 40) BatteryIcon.Text = "\uE853";
                else if (info.Percent >= 20) BatteryIcon.Text = "\uE852";
                else BatteryIcon.Text = "\uE850";
            }

            var brush = new SolidColorBrush(accentColor);
            BatteryIcon.Foreground = brush;
            BatteryBoltIcon.Foreground = brush;
            BatteryIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x28, accentColor.R, accentColor.G, accentColor.B));
            BatteryIconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, accentColor.R, accentColor.G, accentColor.B));

            if (_state != IslandState.Pill)
            {
                SetState(IslandState.Pill);
            }
            else
            {
                SyncBatteryNotifyContent();
            }
        }

        private void SyncBatteryNotifyContent()
        {
            if (!_batteryNotifying || !BatteryNotifyActive)
            {
                _batteryNotifying = false;
            }
            SyncNotificationLayers();
        }

        // ================= 剪切板通知与右键粘贴 =================

        private void OnClipboardContentCopied(object? sender, (string Summary, bool IsCut) e)
        {
            if (_exiting || !_options.ClipboardNotifyEnabled) return;
            if (_greetingShowing && GreetingActive) return;

            _currentAction = IslandActionType.Clipboard;
            _clipboardNotifying = true;
            _clipboardLocked = true;
            _clipboardPastedUntil = DateTime.MinValue;

            ClipboardStatusTitle.Text = e.IsCut ? "已剪切" : "已复制";
            ClipboardModeBadgeText.Text = e.IsCut ? "剪切" : "文本";
            ClipboardPreviewText.Text = string.IsNullOrWhiteSpace(e.Summary) ? "剪切板内容" : e.Summary;

            var cyan = (System.Windows.Media.Brush)FindResource("AccentCyan");
            ClipboardIcon.Foreground = cyan;
            ClipboardIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0xF0, 0xFF));
            ClipboardIconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xF0, 0xFF));

            if (_state != IslandState.Pill)
            {
                SetState(IslandState.Pill);
            }
            else
            {
                SyncClipboardContent();
            }
        }

        private void OnClipboardContentPasted(object? sender, EventArgs e)
        {
            if (_exiting || !_options.ClipboardNotifyEnabled) return;

            _currentAction = IslandActionType.Clipboard;
            _clipboardNotifying = true;
            _clipboardLocked = false;
            _clipboardPastedUntil = DateTime.UtcNow.AddSeconds(_options.ClipboardAutoCollapseSeconds);

            ClipboardStatusTitle.Text = "已粘贴";
            ClipboardModeBadgeText.Text = "完成";
            if (string.IsNullOrWhiteSpace(ClipboardPreviewText.Text))
            {
                ClipboardPreviewText.Text = "已成功粘贴";
            }

            var green = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x88));
            ClipboardIcon.Foreground = green;
            ClipboardIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x28, 0x00, 0xFF, 0x88));
            ClipboardIconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0x00, 0xFF, 0x88));

            if (_state != IslandState.Pill)
            {
                SetState(IslandState.Pill);
            }
            else
            {
                SyncClipboardContent();
            }
        }

        private void OnClipboardCopiedDismissed(object? sender, EventArgs e)
        {
            _clipboardLocked = false;
            _clipboardNotifying = false;
            _clipboardPastedUntil = DateTime.MinValue;
            if (_state == IslandState.Pill && !_isPinned && (_smtc?.Current?.HasMedia != true))
            {
                _currentAction = IslandActionType.Clipboard;
                CollapseToBar();
            }
            else
            {
                SyncClipboardContent();
            }
        }

        private bool OnShouldInterceptRightClick(int x, int y)
        {
            if (!_options.ClipboardRightClickPaste) return false;
            if (!_clipboardLocked || _clipboard?.IsCopiedPending != true) return false;

            if (IsPointInsideIsland(x, y)) return false;

            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                _clipboard.ExecuteRightClickPaste(x, y);
            });

            return true;
        }

        private void SyncClipboardContent()
        {
            if (!_clipboardNotifying || !ClipboardNotifyActive)
            {
                _clipboardNotifying = false;
                _clipboardLocked = false;
            }
            SyncNotificationLayers();
        }

        private void EnsureClipboardInitialized()
        {
            if (_clipboard != null) return;
            try
            {
                if (_hotkey == null)
                {
                    _hotkey = new HotkeyService(Dispatcher);
                    _hotkey.Start();
                }

                _clipboard = new ClipboardService(Dispatcher, _hotkey);
                _clipboard.ContentCopied += OnClipboardContentCopied;
                _clipboard.ContentPasted += OnClipboardContentPasted;
                _clipboard.CopiedDismissed += OnClipboardCopiedDismissed;
                _clipboard.Start();

                if (_mouseHook != null)
                {
                    _mouseHook.ShouldInterceptRightClick = OnShouldInterceptRightClick;
                    _mouseHook.IsInsideIsland = (x, y) => IsPointInsideIsland(x, y);
                    _mouseHook.DragMove += OnMouseHookDragMove;
                    _mouseHook.DragEnd += OnMouseHookDragEnd;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Clipboard/Hotkey] 初始化失败: " + ex.Message);
            }
        }

        private void SyncClipboardHistoryList()
        {
            EnsureClipboardInitialized();

            var history = _clipboard?.History;
            int count = history?.Count ?? 0;
            ClipboardCountBadge.Text = $"{count}条";

            if (count > 0 && history != null)
            {
                var items = new System.Collections.Generic.List<ClipboardItemViewModel>();
                foreach (var entry in history)
                {
                    items.Add(new ClipboardItemViewModel(entry));
                }
                ClipboardHistoryList.ItemsSource = items;
                ClipboardHistoryScroll.Visibility = Visibility.Visible;
                ClipboardEmptyState.Visibility = Visibility.Collapsed;
            }
            else
            {
                ClipboardHistoryList.ItemsSource = null;
                ClipboardHistoryScroll.Visibility = Visibility.Collapsed;
                ClipboardEmptyState.Visibility = Visibility.Visible;
            }
        }

        private void ClipboardHistoryItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 如果用户点击的是项内部的具体按钮（例如“粘贴”按钮或“删除”按钮），忽略外层整行点击
            if (e.OriginalSource is DependencyObject dep)
            {
                var parent = dep;
                while (parent != null && parent != sender)
                {
                    if (parent is System.Windows.Controls.Button)
                    {
                        return; // 留给对应按钮的 Click 事件处理
                    }
                    parent = VisualTreeHelper.GetParent(parent);
                }
            }

            if (sender is FrameworkElement elem && elem.DataContext is ClipboardItemViewModel vm)
            {
                e.Handled = true;
                ExecuteClipboardItemPaste(vm.Entry.Text);
            }
        }

        private void PasteClipboardItem_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement elem && elem.DataContext is ClipboardItemViewModel vm)
            {
                ExecuteClipboardItemPaste(vm.Entry.Text);
            }
        }

        private void ExecuteClipboardItemPaste(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                System.Windows.Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Clipboard] SetText 异常: " + ex.Message);
            }

            _clipboard?.NotifyPasted();
            SetState(IslandState.Pill);

            // 关键：把输入焦点交还给原本正在打字/工作的宿主软件窗口，90~120ms 后模拟 Ctrl+V
            IntPtr targetHwnd = _lastExternalForegroundHwnd;
            if (targetHwnd != IntPtr.Zero)
            {
                NativeMethods.SetForegroundWindow(targetHwnd);
            }

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            timer.Tick += (s, args) =>
            {
                timer.Stop();
                if (targetHwnd != IntPtr.Zero)
                {
                    NativeMethods.SetForegroundWindow(targetHwnd);
                }
                HotkeyService.SimulatePaste();
            };
            timer.Start();
        }

        private void DeleteClipboardItem_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement elem && elem.DataContext is ClipboardItemViewModel vm)
            {
                _clipboard?.RemoveHistory(vm.Entry);
                SyncClipboardHistoryList();
            }
        }

        private void ClearClipboardButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            _clipboard?.ClearHistory();
            SyncClipboardHistoryList();
        }

        // ================= 卡片页轮播 (Win11 风格) =================

        private void SwitchCardPage(int targetPage, bool animated = true)
        {
            _currentCardPage = targetPage;

            CardLayer.Visibility = Visibility.Visible;
            ClipboardCardLayer.Visibility = Visibility.Visible;

            if (targetPage == 1)
            {
                SyncClipboardHistoryList();
            }

            TimeSpan duration = animated ? TimeSpan.FromMilliseconds(260) : TimeSpan.Zero;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            if (_state == IslandState.Card && _animator != null)
            {
                _animator.MorphTo(CardGeometry, _options.GelAnimation, duration, ease);
            }

            if (targetPage == 0)
            {
                // Page 0 (Music) Active
                CardLayer.IsHitTestVisible = true;
                ClipboardCardLayer.IsHitTestVisible = false;

                if (animated)
                {
                    var anim0Y = new DoubleAnimation(0, duration) { EasingFunction = ease };
                    var anim0Op = new DoubleAnimation(1.0, duration) { EasingFunction = ease };
                    CardPage0Translate.BeginAnimation(TranslateTransform.YProperty, anim0Y);
                    CardLayer.BeginAnimation(UIElement.OpacityProperty, anim0Op);

                    var anim1Y = new DoubleAnimation(50, duration) { EasingFunction = ease };
                    var anim1Op = new DoubleAnimation(0.0, duration) { EasingFunction = ease };
                    CardPage1Translate.BeginAnimation(TranslateTransform.YProperty, anim1Y);
                    ClipboardCardLayer.BeginAnimation(UIElement.OpacityProperty, anim1Op);

                    var dot0H = new DoubleAnimation(12, duration) { EasingFunction = ease };
                    var dot1H = new DoubleAnimation(5, duration) { EasingFunction = ease };
                    PageDot0.BeginAnimation(FrameworkElement.HeightProperty, dot0H);
                    PageDot1.BeginAnimation(FrameworkElement.HeightProperty, dot1H);
                }
                else
                {
                    CardPage0Translate.BeginAnimation(TranslateTransform.YProperty, null);
                    CardLayer.BeginAnimation(UIElement.OpacityProperty, null);
                    CardPage1Translate.BeginAnimation(TranslateTransform.YProperty, null);
                    ClipboardCardLayer.BeginAnimation(UIElement.OpacityProperty, null);
                    PageDot0.BeginAnimation(FrameworkElement.HeightProperty, null);
                    PageDot1.BeginAnimation(FrameworkElement.HeightProperty, null);

                    CardPage0Translate.Y = 0;
                    CardLayer.Opacity = 1.0;
                    CardPage1Translate.Y = 50;
                    ClipboardCardLayer.Opacity = 0.0;
                    PageDot0.Height = 12;
                    PageDot1.Height = 5;
                }

                PageDot0.Background = new SolidColorBrush(Color.FromArgb(0xEF, 0xFF, 0xFF, 0xFF));
                PageDot1.Background = new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));

                if (_state == IslandState.Card && _options.ShowWaveLayer)
                {
                    CardWave.Opacity = 1.0;
                }
            }
            else
            {
                // Page 1 (Clipboard) Active
                CardLayer.IsHitTestVisible = false;
                ClipboardCardLayer.IsHitTestVisible = true;

                if (animated)
                {
                    var anim0Y = new DoubleAnimation(-50, duration) { EasingFunction = ease };
                    var anim0Op = new DoubleAnimation(0.0, duration) { EasingFunction = ease };
                    CardPage0Translate.BeginAnimation(TranslateTransform.YProperty, anim0Y);
                    CardLayer.BeginAnimation(UIElement.OpacityProperty, anim0Op);

                    var anim1Y = new DoubleAnimation(0, duration) { EasingFunction = ease };
                    var anim1Op = new DoubleAnimation(1.0, duration) { EasingFunction = ease };
                    CardPage1Translate.BeginAnimation(TranslateTransform.YProperty, anim1Y);
                    ClipboardCardLayer.BeginAnimation(UIElement.OpacityProperty, anim1Op);

                    var dot0H = new DoubleAnimation(5, duration) { EasingFunction = ease };
                    var dot1H = new DoubleAnimation(12, duration) { EasingFunction = ease };
                    PageDot0.BeginAnimation(FrameworkElement.HeightProperty, dot0H);
                    PageDot1.BeginAnimation(FrameworkElement.HeightProperty, dot1H);
                }
                else
                {
                    CardPage0Translate.BeginAnimation(TranslateTransform.YProperty, null);
                    CardLayer.BeginAnimation(UIElement.OpacityProperty, null);
                    CardPage1Translate.BeginAnimation(TranslateTransform.YProperty, null);
                    ClipboardCardLayer.BeginAnimation(UIElement.OpacityProperty, null);
                    PageDot0.BeginAnimation(FrameworkElement.HeightProperty, null);
                    PageDot1.BeginAnimation(FrameworkElement.HeightProperty, null);

                    CardPage0Translate.Y = -50;
                    CardLayer.Opacity = 0.0;
                    CardPage1Translate.Y = 0;
                    ClipboardCardLayer.Opacity = 1.0;
                    PageDot0.Height = 5;
                    PageDot1.Height = 12;
                }

                PageDot0.Background = new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
                PageDot1.Background = (System.Windows.Media.Brush)FindResource("AccentCyan");

                CardWave.Opacity = 0.0;
            }
        }

        private void CardCarousel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_state != IslandState.Card) return;

            if (e.Delta < 0)
            {
                // 向下滚：切到下一页（剪切板）
                if (_currentCardPage == 0)
                {
                    SwitchCardPage(1);
                    e.Handled = true;
                }
            }
            else if (e.Delta > 0)
            {
                // 向上滚：如果在剪切板页，且列表已经滚到最顶端（或空），切回音乐页
                if (_currentCardPage == 1)
                {
                    if (ClipboardHistoryScroll.VerticalOffset <= 1.0)
                    {
                        SwitchCardPage(0);
                        e.Handled = true;
                    }
                }
            }
        }

        private void PageDot0_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_currentCardPage != 0)
            {
                SwitchCardPage(0);
            }
            e.Handled = true;
        }

        private void PageDot1_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_currentCardPage != 1)
            {
                SwitchCardPage(1);
            }
            e.Handled = true;
        }

        // ================= 手势（上滑收起） =================

        private void IslandBody_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _swipeStartPoint = e.GetPosition(this);
            _swipeStartScreenPoint = PointToScreen(_swipeStartPoint);
            _isSwiping = true;
            _dragStartedInsideIsland = true;
            if (_clipboardLocked)
            {
                IslandBody.CaptureMouse();
            }
        }

        private void IslandBody_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isSwiping && e.LeftButton == MouseButtonState.Pressed)
            {
                Point current = e.GetPosition(this);
                double deltaY = _swipeStartPoint.Y - current.Y;
                if (deltaY > 12)
                {
                    _isSwiping = false;
                    _dragStartedInsideIsland = false;
                    _isPinned = false;
                    HidePinBubble(immediate: true);
                    if (IslandBody.IsMouseCaptured) IslandBody.ReleaseMouseCapture();
                    if (_clipboardLocked)
                    {
                        _clipboard?.DismissCopiedPending();
                    }
                    CollapseToBar();
                }
            }
        }

        private void IslandBody_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isSwiping = false;
            _dragStartedInsideIsland = false;
            if (IslandBody.IsMouseCaptured)
            {
                IslandBody.ReleaseMouseCapture();
            }
        }

        private void IslandBody_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_state == IslandState.Pill)
            {
                e.Handled = true;
                TogglePin();
            }
        }

        private void TogglePin()
        {
            _isPinned = !_isPinned;
            ShowPinBubble(_isPinned);
        }

        private void ShowPinBubble(bool pinned)
        {
            if (PinBubble == null) return;

            PinBubbleIcon.Text = pinned ? "\uE718" : "\uE77A";
            PinBubbleText.Text = pinned ? "已置顶" : "已取消";
            PinBubbleIcon.Foreground = pinned
                ? (System.Windows.Media.Brush)FindResource("AccentCyan")
                : new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));

            PinBubble.Visibility = Visibility.Visible;

            var easeOut = new QuinticEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(320);

            var moveAnim = new DoubleAnimation(160, 226, duration) { EasingFunction = easeOut };
            var scaleXAnim = new DoubleAnimation(0.4, 1.0, duration) { EasingFunction = easeOut };
            var scaleYAnim = new DoubleAnimation(0.4, 1.0, duration) { EasingFunction = easeOut };
            var opacityAnim = new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = easeOut };

            PinBubbleTranslate.BeginAnimation(TranslateTransform.XProperty, moveAnim);
            PinBubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
            PinBubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
            PinBubble.BeginAnimation(UIElement.OpacityProperty, opacityAnim);

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

            if (immediate)
            {
                PinBubbleTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                PinBubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                PinBubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                PinBubble.BeginAnimation(UIElement.OpacityProperty, null);
                PinBubble.Visibility = Visibility.Collapsed;
                PinBubble.Opacity = 0;
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
                if (PinBubble.Opacity < 0.05)
                {
                    PinBubble.Visibility = Visibility.Collapsed;
                }
            };

            PinBubbleTranslate.BeginAnimation(TranslateTransform.XProperty, moveAnim);
            PinBubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
            PinBubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
            PinBubble.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        }

        // ================= 文件中转站 =================

        private void OnFileTransferItemsChanged(object? sender, EventArgs e)
        {
            SyncFileTransferContent();
        }

        private void OnFileTransferAllFilesEmptied(object? sender, EventArgs e)
        {
            if (_state == IslandState.Tray)
            {
                if (_isPinned)
                {
                    SetState(IslandState.Pill);
                }
                else
                {
                    CollapseToBar();
                }
            }
        }

        private void SyncFileTransferContent()
        {
            if (_fileTransfer == null) return;
            bool hasFiles = _fileTransfer.Count > 0;
            DropZoneGrid.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
            FileListGrid.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            if (!_options.FileTransferEnabled) return;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy | DragDropEffects.Move;
                e.Handled = true;
                if (_state != IslandState.Tray)
                {
                    SetState(IslandState.Tray);
                }
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (!_options.FileTransferEnabled) return;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Handled = true;
                Point pos = e.GetPosition(this);
                double midX = ActualWidth / 2.0;
                bool isRight = pos.X > midX;

                if (isRight)
                {
                    e.Effects = DragDropEffects.Move;
                }
                else
                {
                    e.Effects = DragDropEffects.Copy;
                }

                ApplyDropZoneHighlight(isRight);
            }
        }

        private void Window_DragLeave(object sender, DragEventArgs e)
        {
            ResetDropZoneHighlight();
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!_options.FileTransferEnabled) return;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Handled = true;
                Point pos = e.GetPosition(this);
                bool isCut = pos.X > ActualWidth / 2.0;

                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    _fileTransfer?.AddFiles(files, isCut);
                }

                ResetDropZoneHighlight();
            }
        }

        private void TransferItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(this);
            if (sender is FrameworkElement elem && elem.DataContext is TransferItem item)
            {
                _dragSourceItem = item;
            }
        }

        private void TransferItem_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _dragSourceItem != null)
            {
                Point current = e.GetPosition(this);
                Vector diff = _dragStartPoint - current;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    var item = _dragSourceItem;
                    _dragSourceItem = null;

                    var dataObject = new DataObject(DataFormats.FileDrop, new string[] { item.FullPath });
                    var effects = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;

                    var applied = DragDrop.DoDragDrop((DependencyObject)sender, dataObject, effects);

                    // 只有真正投放到目标（效果不为 None）才算“已转出”。
                    // 原实现无条件 RemoveItem：用户中途按 Esc 取消、或把条目丢在空白处时，
                    // 文件虽然没被复制/移动，条目却已经从中转站消失（数据丢失）。
                    if (applied != DragDropEffects.None)
                    {
                        _fileTransfer?.RemoveItem(item);
                    }
                }
            }
        }

        // 摇晃唤出中转站手势检测状态
        private int _lastShakeX;
        private int _shakeDirection;
        private int _shakeCount;
        private long _lastShakeTimestamp;

        // ---- 拖拽载荷靠近 / 意图识别响应 ----
        private void OnMouseHookDragMove(object? sender, (int X, int Y, int StartX, int StartY) pt)
        {
            if (_exiting || !_options.FileTransferEnabled) return;

            // 若正在岛体上手势上滑，通过全局坐标快速响应收起，绝不误触文件中转站
            if (_isSwiping)
            {
                double deltaY = _swipeStartScreenPoint.Y - pt.Y;
                if (deltaY > 12)
                {
                    _isSwiping = false;
                    _dragStartedInsideIsland = false;
                    _isPinned = false;
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                    {
                        HidePinBubble(immediate: true);
                        if (IslandBody.IsMouseCaptured) IslandBody.ReleaseMouseCapture();
                        if (_clipboardLocked)
                        {
                            _clipboard?.DismissCopiedPending();
                        }
                        CollapseToBar();
                    });
                    return;
                }
            }

            if (_dragStartedInsideIsland || _isSwiping || _dragSourceItem != null) return;
            if (_state == IslandState.Tray) return;

            // 1. 距离位移门槛：必须累计拖拽位移超过 35 像素
            int dx = pt.X - pt.StartX;
            int dy = pt.Y - pt.StartY;
            if (dx * dx + dy * dy < 35 * 35) return;

            // 2. 摇晃手势检测（Shake to summon：0.55s 内左右来回摇晃 3 次，任意全屏位置唤出中转站）
            bool isShaking = false;
            long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            double secondsSinceLastShake = (double)(nowTicks - _lastShakeTimestamp) / System.Diagnostics.Stopwatch.Frequency;
            if (secondsSinceLastShake > 0.55)
            {
                _shakeCount = 0;
                _shakeDirection = 0;
                _lastShakeX = pt.X;
                _lastShakeTimestamp = nowTicks;
            }
            else
            {
                int xDiff = pt.X - _lastShakeX;
                if (Math.Abs(xDiff) >= 20)
                {
                    int currentDir = xDiff > 0 ? 1 : -1;
                    if (_shakeDirection != 0 && currentDir != _shakeDirection)
                    {
                        _shakeCount++;
                        if (_shakeCount >= 3)
                        {
                            isShaking = true;
                            _shakeCount = 0;
                        }
                    }
                    _shakeDirection = currentDir;
                    _lastShakeX = pt.X;
                    _lastShakeTimestamp = nowTicks;
                }
            }

            // 只有摇晃手势才由鼠标移动直接触发；拖拽真实文件靠近顶部一律由 TopDropZone 的 OLE DragEnter 验证！
            // 彻底消除"长按鼠标、原地拖动或框选文字导致误触发文件中转站"的严重问题！
            if (!isShaking)
            {
                return;
            }

            // 触发灵动岛展开为文件中转站
            if (_isDragTriggering) return;
            _isDragTriggering = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                _isDragTriggering = false;
                if (_exiting || !_options.FileTransferEnabled) return;
                if (_dragStartedInsideIsland || _isSwiping || _dragSourceItem != null) return;
                if (_state != IslandState.Tray)
                {
                    SetState(IslandState.Tray);
                }
            });
        }

        private void OnMouseHookDragEnd(object? sender, EventArgs e)
        {
            _dragStartedInsideIsland = false;
            _isSwiping = false;
            _shakeCount = 0;
            _shakeDirection = 0;

            // 鼠标松开，立即将顶部 OLE 拖放感应带设为不可命中，保证正常桌面点击 100% 穿透
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (TopDropZone != null) TopDropZone.IsHitTestVisible = false;
                if (_exiting || _state != IslandState.Tray) return;
                // 拖拽松手后，若中转站无文件且鼠标未悬停在热区内，自动平滑收回
                if ((_fileTransfer == null || _fileTransfer.Count == 0) && !IsCursorInHotZone())
                {
                    if (_isPinned)
                    {
                        SetState(IslandState.Pill);
                    }
                    else
                    {
                        CollapseToBar();
                    }
                }
            });
        }

        // ---- 分区拖放事件 ----
        private void DropZoneCopy_DragEnter(object sender, DragEventArgs e)
        {
            if (!_options.FileTransferEnabled) return;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                ApplyDropZoneHighlight(false);
            }
        }

        private void DropZoneCopy_DragOver(object sender, DragEventArgs e)
        {
            if (!_options.FileTransferEnabled) return;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void DropZoneCopy_DragLeave(object sender, DragEventArgs e)
        {
            ResetDropZoneHighlight();
        }

        private void DropZoneCopy_Drop(object sender, DragEventArgs e)
        {
            if (!_options.FileTransferEnabled) return;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Handled = true;
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    _fileTransfer?.AddFiles(files, isCut: false);
                }
                ResetDropZoneHighlight();
            }
        }

        private void DropZoneCut_DragEnter(object sender, DragEventArgs e)
        {
            if (!_options.FileTransferEnabled) return;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                ApplyDropZoneHighlight(true);
            }
        }

        private void DropZoneCut_DragOver(object sender, DragEventArgs e)
        {
            if (!_options.FileTransferEnabled) return;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
            }
        }

        private void DropZoneCut_DragLeave(object sender, DragEventArgs e)
        {
            ResetDropZoneHighlight();
        }

        private void DropZoneCut_Drop(object sender, DragEventArgs e)
        {
            if (!_options.FileTransferEnabled) return;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Handled = true;
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    _fileTransfer?.AddFiles(files, isCut: true);
                }
                ResetDropZoneHighlight();
            }
        }

        private void TransferItem_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.DataContext is TransferItem item)
            {
                _fileTransfer?.RemoveItem(item);
                e.Handled = true;
            }
        }

        private void CloseFileTransferButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isPinned)
            {
                SetState(IslandState.Pill);
            }
            else
            {
                _currentAction = IslandActionType.FileTransfer;
                CollapseToBar();
            }
        }

        private void ClearFileTransferButton_Click(object sender, RoutedEventArgs e)
        {
            _fileTransfer?.Clear();
            if (_isPinned)
            {
                SetState(IslandState.Pill);
            }
            else
            {
                _currentAction = IslandActionType.FileTransfer;
                CollapseToBar();
            }
        }

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

        private void OnBluetoothBatteryUpdated(object? sender, BluetoothDeviceInfo info)
        {
            if (_btNotifying && BtNotifyActive)
            {
                if (info.BatteryPercent.HasValue)
                {
                    BtBatteryText.Text = $"{info.BatteryPercent.Value}%";
                }
            }
        }

        /// <summary>
        /// 蓝牙设备连接/断开时弹出一级胶囊，显示设备名称、分类专属图标/3D模型和连接/断开状态及电量，一段时间后自动收回。
        /// </summary>
        private void ShowBluetoothNotify(BluetoothDeviceInfo info, bool isConnected = true)
        {
            _currentAction = IslandActionType.Bluetooth;
            _btNotifying = true;
            _btNotifyUntil = DateTime.UtcNow.AddSeconds(_options.BluetoothNotifySeconds);

            BtDeviceName.Text = info.Name;

            if (isConnected)
            {
                BtStatusText.Text = "已连接";
                BtStatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");
                BtBatteryIcon.Visibility = Visibility.Visible;
                BtBatteryText.Visibility = Visibility.Visible;
                BtBatteryText.Text = info.BatteryPercent.HasValue ? $"{info.BatteryPercent.Value}%" : "--";
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
            if (!_btNotifying || !BtNotifyActive)
            {
                _btNotifying = false;
            }
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
            bool shouldShow = _options.LiquidGlassBackdrop && _state != IslandState.Bar && _options.GlassOpacity >= 0.45;
            backdrop.SetVisible(shouldShow);
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
            bool on = _options.VisualizerEnabled;
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
        /// 编排和之前用户定稿的三段式一样，但不再拆成三次 MorphTo ——
        /// 每次 MorphTo 都会 Stop() 上一条弹簧，速度在接缝处硬归零，观感是"三段机械动画串起来"。
        /// 现在只有一条弹簧，路径点在两端用 smoothstep 收速度，所以"顿在球上"是速度真的到 0，
        /// 而不是两个动画拼接出来的人为停顿。
        /// 内容层与封面球都不走自己的时间轴，全部挂在主弹簧进度上（见 ApplyReveal）。
        /// </summary>
        private void CollapseToBar()
        {
            if (_exiting) return;

            var actionType = GetCurrentActionType();
            _currentAction = actionType;
            PrepareSiriOrbContent(actionType);

            IslandState prev = _state;
            _state = IslandState.Bar;
            _hoverOutTicks = 0;
            _isPinned = false;
            HidePinBubble(immediate: true);
            CollapseMusicOrbWithMainIsland();
            _upNextTimer?.Stop();
            _greetingTimer?.Stop();

            // 收回意味着换曲提示结束：把胶囊内容还原，下次展开不会还挂着"接下来"
            _upNextUntil = DateTime.MinValue;
            _upNextShowing = false;
            SyncUpNextContent();

            // 启动问候重置
            _greetingUntil = DateTime.MinValue;
            _greetingShowing = false;
            GreetingLayer.Opacity = 0;
            GreetingLayer.Visibility = Visibility.Collapsed;
            GreetingLayer.IsHitTestVisible = false;

            // 蓝牙通知也随收回重置
            _btNotifyUntil = DateTime.MinValue;
            _btNotifying = false;
            BtDeviceModel.IsRotating = false;
            BtNotifyLayer.IsHitTestVisible = false;

            // 电池与剪切板通知随收回重置
            _batteryNotifyUntil = DateTime.MinValue;
            _batteryNotifying = false;
            BatteryNotifyLayer.IsHitTestVisible = false;

            _clipboardPastedUntil = DateTime.MinValue;
            _clipboardNotifying = false;
            _clipboardLocked = false;
            ClipboardNotifyLayer.IsHitTestVisible = false;

            // 音量通知重置
            _volumeNotifyUntil = DateTime.MinValue;
            _volumeNotifying = false;
            VolumeNotifyLayer.IsHitTestVisible = false;

            FileTransferLayer.IsHitTestVisible = false;

            _revealFrom = prev;
            _revealTo = IslandState.Bar;
            _reveal = 0.0;
            ApplyReveal();

            // PillLayer 的淡出由 ApplyReveal 随弹簧进度驱动，
            // 此处仅停止任何残余的独立动画，不立即隐藏内容层。
            PillLayer.BeginAnimation(UIElement.OpacityProperty, null);
            PillLayerTranslate?.BeginAnimation(TranslateTransform.XProperty, null);
            if (PillCoverHost != null)
            {
                PillCoverHost.Opacity = 1;
            }

            CardCarouselHost.BeginAnimation(UIElement.OpacityProperty, null);
            CardCarouselHost.Opacity = 0;
            CardCarouselHost.Visibility = Visibility.Collapsed;
            CardCarouselHost.IsHitTestVisible = false;
            CardLayer.IsHitTestVisible = false;
            ClipboardCardLayer.IsHitTestVisible = false;
            ClipboardCardLayer.Visibility = Visibility.Collapsed;
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

            animator.MorphTo(BarGeometry, _options.GelAnimation, IslandMetrics.CollapseDuration,
                new CubicEase { EasingMode = EasingMode.EaseInOut },
                completed: () =>
                {
                    PillLayer.BeginAnimation(UIElement.OpacityProperty, null);
                    PillLayer.Opacity = 0;
                    PillLayer.Visibility = Visibility.Collapsed;
                    CardCarouselHost.BeginAnimation(UIElement.OpacityProperty, null);
                    CardCarouselHost.Opacity = 0;
                    CardCarouselHost.Visibility = Visibility.Collapsed;
                    CancelSiriOrb();
                    _currentAction = IslandActionType.Music;
                },
                via: via,
                viaAt: IslandMetrics.CollapseViaAt,
                followLag: IslandMetrics.CollapseFollowLag);
        }

        // ================= 内容层的显露（挂在主弹簧进度上，没有自己的时间轴） =================

        /// <summary>形变进度 0..1（含过冲），由 IslandAnimator.ProgressHook 每帧回传。</summary>
        private double _reveal = 1.0;
        private IslandState _revealFrom = IslandState.Bar;
        private IslandState _revealTo = IslandState.Bar;

        private void OnMorphProgress(double t)
        {
            _reveal = t;
            ApplyReveal();
        }

        /// <summary>
        /// 把内容层透明度和封面球推到"当前形变进度该有的样子"。
        /// 刻意不生成任何 Opacity 动画：一旦有独立时间轴，它就会和形变错相位，
        /// 观感上变成"盖在形变上的淡入淡出"，而不是材质本身在开合。
        /// </summary>
        private void ApplyReveal()
        {
            double t = _reveal;
            double span = IslandMetrics.ContentRevealEnd - IslandMetrics.ContentRevealStart;
            double content = span <= 0 ? 1.0 : Math.Clamp((t - IslandMetrics.ContentRevealStart) / span, 0.0, 1.0);

            bool showVol = (_volumeNotifying && VolumeNotifyActive) || (_revealTo == IslandState.Bar && _currentAction == IslandActionType.Volume);
            bool showBattery = (_batteryNotifying && BatteryNotifyActive) || (_revealTo == IslandState.Bar && _currentAction == IslandActionType.Battery);
            bool showClipboard = (_clipboardNotifying && ClipboardNotifyActive) || (_revealTo == IslandState.Bar && _currentAction == IslandActionType.Clipboard);
            bool showGreeting = (_greetingShowing && GreetingActive) || (_revealTo == IslandState.Bar && _currentAction == IslandActionType.Greeting);
            bool showBt = (_btNotifying && BtNotifyActive) || (_revealTo == IslandState.Bar && _currentAction == IslandActionType.Bluetooth);

            if (showVol)
            {
                RevealLayer(VolumeNotifyLayer, _revealFrom == IslandState.Pill, _revealTo == IslandState.Pill, content);
                PillLayer.Opacity = 0;
                BatteryNotifyLayer.Opacity = 0;
                ClipboardNotifyLayer.Opacity = 0;
                BtNotifyLayer.Opacity = 0;
                GreetingLayer.Opacity = 0;
            }
            else if (showBattery)
            {
                RevealLayer(BatteryNotifyLayer, _revealFrom == IslandState.Pill, _revealTo == IslandState.Pill, content);
                PillLayer.Opacity = 0;
                VolumeNotifyLayer.Opacity = 0;
                ClipboardNotifyLayer.Opacity = 0;
                BtNotifyLayer.Opacity = 0;
                GreetingLayer.Opacity = 0;
            }
            else if (showClipboard)
            {
                RevealLayer(ClipboardNotifyLayer, _revealFrom == IslandState.Pill, _revealTo == IslandState.Pill, content);
                PillLayer.Opacity = 0;
                VolumeNotifyLayer.Opacity = 0;
                BatteryNotifyLayer.Opacity = 0;
                BtNotifyLayer.Opacity = 0;
                GreetingLayer.Opacity = 0;
            }
            else if (showGreeting)
            {
                RevealLayer(GreetingLayer, _revealFrom == IslandState.Pill, _revealTo == IslandState.Pill, content);
                PillLayer.Opacity = 0;
                BatteryNotifyLayer.Opacity = 0;
                ClipboardNotifyLayer.Opacity = 0;
                BtNotifyLayer.Opacity = 0;
                VolumeNotifyLayer.Opacity = 0;
            }
            else if (showBt)
            {
                RevealLayer(BtNotifyLayer, _revealFrom == IslandState.Pill, _revealTo == IslandState.Pill, content);
                PillLayer.Opacity = 0;
                BatteryNotifyLayer.Opacity = 0;
                ClipboardNotifyLayer.Opacity = 0;
                GreetingLayer.Opacity = 0;
                VolumeNotifyLayer.Opacity = 0;
            }
            else
            {
                RevealLayer(PillLayer, _revealFrom == IslandState.Pill, _revealTo == IslandState.Pill, content);
                BatteryNotifyLayer.Opacity = 0;
                ClipboardNotifyLayer.Opacity = 0;
                BtNotifyLayer.Opacity = 0;
                GreetingLayer.Opacity = 0;
                VolumeNotifyLayer.Opacity = 0;
            }

            // 一级胶囊层（音乐播放器）与通知图层严格显隐互斥
            bool pillVisible = (_revealFrom == IslandState.Pill || _revealTo == IslandState.Pill) && PillLayer.Opacity > 0.005;
            PillLayer.Visibility = pillVisible ? Visibility.Visible : Visibility.Collapsed;

            GreetingLayer.Visibility = GreetingLayer.Opacity > 0.005 ? Visibility.Visible : Visibility.Collapsed;
            VolumeNotifyLayer.Visibility = VolumeNotifyLayer.Opacity > 0.005 ? Visibility.Visible : Visibility.Collapsed;
            BatteryNotifyLayer.Visibility = BatteryNotifyLayer.Opacity > 0.005 ? Visibility.Visible : Visibility.Collapsed;
            ClipboardNotifyLayer.Visibility = ClipboardNotifyLayer.Opacity > 0.005 ? Visibility.Visible : Visibility.Collapsed;
            BtNotifyLayer.Visibility = BtNotifyLayer.Opacity > 0.005 ? Visibility.Visible : Visibility.Collapsed;

            // 中间态托盘层（文件中转站）
            RevealLayer(FileTransferLayer, _revealFrom == IslandState.Tray, _revealTo == IslandState.Tray, content);
            bool trayVisible = (_revealFrom == IslandState.Tray || _revealTo == IslandState.Tray) && FileTransferLayer.Opacity > 0.001;
            FileTransferLayer.Visibility = trayVisible ? Visibility.Visible : Visibility.Collapsed;

            RevealLayer(CardCarouselHost, _revealFrom == IslandState.Card, _revealTo == IslandState.Card, content);
            bool cardVisible = (_revealFrom == IslandState.Card || _revealTo == IslandState.Card) && CardCarouselHost.Opacity > 0.001;
            CardCarouselHost.Visibility = cardVisible ? Visibility.Visible : Visibility.Collapsed;

            if (_currentCardPage == 0)
            {
                RevealLayer(CardWave, _revealFrom == IslandState.Card, _revealTo == IslandState.Card, content);
            }
            else
            {
                CardWave.Opacity = 0;
            }
            if (_revealTo != IslandState.Card)
            {
                Keyboard.ClearFocus();
            }

            // 收回途中那颗"封面球"。显形窗口写死在主弹簧进度上，且起点晚于内容层退场结束点，
            // 所以不会再出现"胶囊里的封面 + 球上的封面"两个专辑图标同时可见。
            if (_revealTo == IslandState.Bar && _revealFrom != IslandState.Bar)
            {
                double riseSpan = IslandMetrics.OrbRevealEnd - IslandMetrics.OrbRevealStart;
                double rise = riseSpan <= 0 ? 1.0
                    : Math.Clamp((t - IslandMetrics.OrbRevealStart) / riseSpan, 0.0, 1.0);
                double fall = Math.Clamp((IslandMetrics.OrbFadeStart - t) / IslandMetrics.OrbFadeSpan, 0.0, 1.0);

                SiriOrb.Opacity = rise * fall;
                SiriOrbTranslate.X = 0;
            }
        }

        private static void RevealLayer(UIElement element, bool isFrom, bool isTo, double content)
        {
            element.Opacity = isTo ? (isFrom ? 1.0 : content) : (isFrom ? 1.0 - content : 0.0);
        }

        // ================= 悬停 / 点击 =================

        private void IslandBody_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_exiting) return;
            if (_clipboardLocked) return;
            _hoverOutTicks = 0;
            if (_state == IslandState.Bar) SetState(IslandState.Pill);
            else if (_state == IslandState.Pill && !_options.TwoStepExpand) SetState(IslandState.Card);
        }

        /// <summary>一级 → 二级：用户要求"鼠标经过时第一次展开"是一级，再点一下才进完整卡片。</summary>
        private void IslandBody_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_exiting) return;
            if (_clipboardLocked) return; // 剪切板常驻期间禁用点击展开卡片手势，仅保留上滑收回手势
            _hoverOutTicks = 0;
            if (_greetingShowing)
            {
                DismissGreeting();
            }
            if (_state == IslandState.Pill)
            {
                SetState(IslandState.Card);
            }
        }

        private void HoverTimer_Tick(object? sender, EventArgs e)
        {
            if (_exiting) return;

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
                }
            }
            catch { }

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
            if (_btNotifying && !BtNotifyActive)
            {
                _btNotifying = false;
                if (_state == IslandState.Pill && !_isPinned && (_smtc?.Current?.HasMedia != true))
                {
                    _currentAction = IslandActionType.Bluetooth;
                    CollapseToBar();
                    return;
                }
            }
            if (_volumeNotifying && !VolumeNotifyActive)
            {
                _volumeNotifying = false;
                if (_state == IslandState.Pill && !_isPinned && (_smtc?.Current?.HasMedia != true))
                {
                    _currentAction = IslandActionType.Volume;
                    CollapseToBar();
                    return;
                }
            }
            if (_batteryNotifying && !BatteryNotifyActive)
            {
                _batteryNotifying = false;
                if (_state == IslandState.Pill && !_isPinned && (_smtc?.Current?.HasMedia != true))
                {
                    _currentAction = IslandActionType.Battery;
                    CollapseToBar();
                    return;
                }
            }
            if (_clipboardNotifying && !_clipboardLocked && !ClipboardNotifyActive)
            {
                _clipboardNotifying = false;
                if (_state == IslandState.Pill && !_isPinned && (_smtc?.Current?.HasMedia != true))
                {
                    _currentAction = IslandActionType.Clipboard;
                    CollapseToBar();
                    return;
                }
            }

            // 3. 统一同步图层互斥显隐与左侧音乐圆球（通知结束时音乐圆球平滑回缩入主岛，PillLayer恢复居中）
            SyncNotificationLayers();

            // 若有任何提示/通知处于活跃期，保持展开不收回
            if ((_upNextShowing && UpNextActive) || HasActiveNotification)
            {
                _hoverOutTicks = 0;
                return;
            }

            // 若用户右键已置顶，保持在一级展开形态，不自动收回至细条
            if (_isPinned && _state == IslandState.Pill)
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
                    if (_isPinned)
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

            IntPtr fg = NativeMethods.GetForegroundWindow();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_exiting) return;

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
                bool insideInteractive = IsPointInsideInteractiveZone(e.X, e.Y);
                if (!insideInteractive)
                {
                    // 若用户已置顶一级胶囊，点击外部不收回至细条
                    if (_isPinned && _state == IslandState.Pill) return;

                    // 若正在欢迎动画期间，点击外部立即提前结束欢迎并收缩回细条
                    if (_greetingShowing || GreetingActive)
                    {
                        DismissGreeting();
                        CollapseToBar();
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
                        if (_isPinned)
                        {
                            SetState(IslandState.Pill);
                        }
                        else
                        {
                            CollapseToBar();
                        }
                        return;
                    }

                    // 正在播放通知提示期间，点击外部不打断
                    if (VolumeNotifyActive || BatteryNotifyActive || BtNotifyActive || UpNextActive)
                        return;

                    CollapseToBar();
                }
            }), DispatcherPriority.Input);
        }

        private bool IsPointInsideIsland(int screenX, int screenY)
        {
            if (!Dispatcher.CheckAccess())
            {
                var cached = _cachedIslandScreenRect;
                if (!cached.IsEmpty)
                {
                    return cached.Contains(new Point(screenX, screenY));
                }
                return false;
            }

            if (IslandBody.ActualWidth <= 0 || IslandBody.ActualHeight <= 0) return false;
            try
            {
                var tl = IslandBody.PointToScreen(new Point(0, 0));
                var br = IslandBody.PointToScreen(new Point(IslandBody.ActualWidth, IslandBody.ActualHeight));
                var rect = new Rect(tl, br);
                rect.Inflate(1, 1);
                _cachedIslandScreenRect = rect;
                return rect.Contains(new Point(screenX, screenY));
            }
            catch
            {
                return false;
            }
        }

        // ================= 媒体 =================

        private void OnNowPlayingChanged(object? sender, NowPlayingInfo? info)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnNowPlayingChanged(sender, info)));
                return;
            }

            if (info == null || !info.HasMedia)
            {
                // SMTC 在换曲瞬间会短暂报"无会话"，立刻清空会闪；延迟 1.2s 再确认。
                _mediaLostTimer?.Stop();
                _mediaLostTimer?.Start();
                return;
            }

            _mediaLostTimer?.Stop();
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

            if (!info.IsPlaying)
            {
                PillSpectrum.Clear();
                CardWave.Clear();
            }

            if (_lyricsDoc == null) SetPillLyric(info.DisplayTitle);

            string title = info.Title ?? "";
            string artist = info.Artist ?? "";
            bool trackChanged = title.Length > 0
                                && (title != _lastTitle || artist != _lastArtist);

            if (trackChanged)
            {
                _lastTitle = title;
                _lastArtist = artist;
                _lastFetchDuration = info.Duration;
                StartLyricsFetch(info);

                // 换曲提示：弹出细条→一级胶囊，中间显示新曲名、右侧"接下来"，过一会儿自己收回
                // _sawMedia 保证只有"真的换了一首"才弹，开机第一首不弹。
                if (info.IsPlaying && _sawMedia) BeginUpNext(info);
            }
            else if (!trackChanged && _lyricsDoc == null && info.IsPlaying && info.Duration > TimeSpan.Zero && _lastFetchDuration == TimeSpan.Zero)
            {
                // 当刚切歌时 SMTC 报过来的 Duration 为 0，可能搜索/匹配因此受影响或失败；
                // 一旦后续真实的歌曲时长更新（Duration > 0 且此前为 0），补抓一次歌词！
                _lastFetchDuration = info.Duration;
                StartLyricsFetch(info);
            }
            else if (_sawMedia && _lastIsPlaying == true && !info.IsPlaying && _options.PauseNotifyEnabled)
            {
                // 播放中暂停提示：与换曲提示形态一致，中间为当前曲名、右侧为“已暂停”
                _lyricIndex = -2;
                BeginPausePrompt(info);
            }
            else if (_sawMedia && _lastIsPlaying != true && info.IsPlaying && _options.PlayNotifyEnabled)
            {
                // 暂停后恢复播放提示：中间为当前曲名、右侧为“正在播放”
                _lyricIndex = -2;
                BeginPlayPrompt(info);
            }

            _lastIsPlaying = info.IsPlaying;
            _sawMedia = true;
            SyncVisualizers();
        }

        private void ApplyNoMedia()
        {
            _lastTitle = "";
            _lastArtist = "";
            _lyricsKey = "";
            _lastFetchDuration = TimeSpan.Zero;
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
            HideMusicOrb(immediate: true);
            if (MusicOrbCoverBrush != null) MusicOrbCoverBrush.ImageSource = null;

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

            if (MusicOrbCoverBrush != null)
            {
                MusicOrbCoverBrush.ImageSource = art;
                MusicOrbCover.Opacity = coverOn;
                MusicOrbGradient.Opacity = 1.0 - coverOn;
            }

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
            if (key == _lyricsKey && _lyricsDoc != null) return;
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
            bool should = (_state == IslandState.Card || _state == IslandState.Pill)
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
            if (_options.LyricsOffsetMs != 0)
            {
                position += TimeSpan.FromMilliseconds(_options.LyricsOffsetMs);
                if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            }
            int index = Math.Clamp(doc.IndexAt(position), 0, lines.Count - 1);

            if (index != _lyricIndex)
            {
                _lyricIndex = index;
                ApplyLyricTexts(lines, index);
            }

            if (_state != IslandState.Card) return;

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

            // 无论当前是 Pill 还是 Card，都直接推入最新频谱帧
            // SpectrumVisualizer 内部由 UI 线程的 IsActive 控制渲染循环，未激活时不消耗任何 CPU
            PillSpectrum.PushFrame(bands);
            CardWave.PushFrame(bands);
        }

        // ================= 进度 =================

        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            var media = _smtc;
            if (media != null)
            {
                media.CheckFastPlaybackState();
            }

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

            // 兜底：若未挂上高频钩子，在此更新胶囊态当前歌词
            if (!_karaokeHooked && _state == IslandState.Pill)
            {
                var doc = _lyricsDoc;
                if (doc != null && doc.HasLyrics)
                {
                    var lyricPos = position;
                    if (_options.LyricsOffsetMs != 0)
                    {
                        lyricPos += TimeSpan.FromMilliseconds(_options.LyricsOffsetMs);
                        if (lyricPos < TimeSpan.Zero) lyricPos = TimeSpan.Zero;
                    }
                    var lines = doc.Lines;
                    int index = Math.Clamp(doc.IndexAt(lyricPos), 0, lines.Count - 1);
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

            try
            {
                if (_settingsWindow != null && (!_settingsWindow.IsLoaded && !_settingsWindow.IsVisible))
                {
                    try { _settingsWindow.Close(); } catch { }
                    _settingsWindow = null;
                }

                if (_settingsWindow == null)
                {
                    _settingsWindow = new SettingsWindow();
                    _settingsWindow.Closed += (_, __) => _settingsWindow = null;
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Settings] 打开异常，重新创建: " + ex);
                try { _settingsWindow?.Close(); } catch { }
                _settingsWindow = null;

                try
                {
                    _settingsWindow = new SettingsWindow();
                    _settingsWindow.Closed += (_, __) => _settingsWindow = null;
                    _settingsWindow.OptionsChanged += (_, options) =>
                    {
                        _options.CopyFrom(options);
                        ApplyOptions();
                    };
                    _settingsWindow.LoadFrom(_options);
                    _settingsWindow.Show();
                    _settingsWindow.Activate();
                }
                catch (Exception ex2)
                {
                    System.Diagnostics.Debug.WriteLine("[Settings] 重新创建后仍打开失败: " + ex2);
                }
            }
        }

        /// <summary>把 options 应用到所有活着的部件。设置改动后统一走这里，避免漏项。</summary>
        private void ApplyOptions()
        {
            // 先让玻璃窗的存在状态跟开关一致，再按它算本色浓度。
            EnsureBackdrop();
            SyncBackdrop();

            double opacity = Math.Clamp(_options.GlassOpacity, 0.05, 0.98);

            // 当真实毛玻璃可用时，同步调整其精灵层的不透明度，实现真正的全动态背景通透调节
            _backdrop?.SetOpacity(Math.Clamp(opacity * 1.1, 0.05, 1.0));

            // 玻璃背景渐变底色：随 opacity 全动态线性缩放，通透时宛如水晶玻璃，拉满时深邃厚重
            byte topAlpha = (byte)Math.Clamp(Math.Round(12 + opacity * 220), 10, 245);
            byte bottomAlpha = (byte)Math.Clamp(Math.Round(22 + opacity * 230), 18, 255);
            GlassTopStop.Color = Color.FromArgb(topAlpha, 0x12, 0x12, 0x18);
            GlassBottomStop.Color = Color.FromArgb(bottomAlpha, 0x07, 0x07, 0x0B);

            // 高通透度下相应柔化 SheenLayer，避免一层过厚的白雾盖在玻璃上
            if (SheenLayer != null)
            {
                SheenLayer.Opacity = Math.Clamp(opacity * 0.40, 0.02, 0.45);
            }

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

            if (_options.BluetoothNotifyEnabled && _bluetooth == null)
            {
                try
                {
                    var bt = new BluetoothService(Dispatcher);
                    bt.DeviceConnected += OnBluetoothDeviceConnected;
                    bt.BatteryUpdated += OnBluetoothBatteryUpdated;
                    _bluetooth = bt;
                    // Start() 里有 WinRT DeviceWatcher 与 COM 端点绑定，会阻塞调用线程。
                    // 早先在这里同步 Start()，直接让主窗口晚了约 2 秒才出现。
                    StartInBackground(bt.Start, "Bluetooth");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Bluetooth] 启动失败: " + ex.Message);
                }
            }
            else if (!_options.BluetoothNotifyEnabled && _bluetooth != null)
            {
                _bluetooth.Dispose();
                _bluetooth = null;
            }

            if (_options.VolumeNotifyEnabled && _volumeService == null)
            {
                try
                {
                    var vol = new VolumeService(Dispatcher);
                    vol.VolumeChanged += OnVolumeChanged;
                    _volumeService = vol;
                    StartInBackground(vol.Start, "Volume");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Volume] 启动失败: " + ex.Message);
                }
            }
            else if (!_options.VolumeNotifyEnabled && _volumeService != null)
            {
                _volumeService.Dispose();
                _volumeService = null;
            }

            if (_options.BatteryNotifyEnabled && _battery == null)
            {
                try
                {
                    var bat = new BatteryService(Dispatcher);
                    bat.BatteryStatusChanged += OnBatteryStatusChanged;
                    _battery = bat;
                    StartInBackground(bat.Start, "Battery");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Battery] 启动失败: " + ex.Message);
                }
            }
            else if (!_options.BatteryNotifyEnabled && _battery != null)
            {
                _battery.Dispose();
                _battery = null;
            }

            if (_state == IslandState.Card) SetState(IslandState.Card, force: true);
        }

        /// <summary>把服务的 Start() 放到后台线程：COM/WinRT 初始化不能占用 UI 线程（否则窗口迟迟不出现）。</summary>
        private static void StartInBackground(Action start, string tag)
        {
            _ = Task.Run(() =>
            {
                try { start(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[{tag}] 后台启动异常: " + ex.Message); }
            });
        }

        // ================= 退出 =================

        private void ExitApplication()
        {
            App.Log(new Exception("ExitApplication called, stack:\n" + Environment.StackTrace), "ExitApplication");
            if (_exiting) return;
            _exiting = true;

            try
            {
                _hoverTimer?.Stop();
                _progressTimer?.Stop();
                _mediaLostTimer?.Stop();
                _greetingTimer?.Stop();

                StopKaraoke();
                _animator?.Stop();
                CompositionTarget.Rendering -= OnBackdropTick;
                _backdrop?.Dispose();
                _backdrop = null;

                _battery?.Dispose();
                _battery = null;

                _clipboard?.Dispose();
                _clipboard = null;

                _hotkey?.Dispose();
                _hotkey = null;

                _bluetooth?.Dispose();
                _bluetooth = null;

                _volumeService?.Dispose();
                _volumeService = null;

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

    /// <summary>剪切板历史卡片列表项视图模型。</summary>
    public sealed class ClipboardItemViewModel
    {
        public ClipboardEntry Entry { get; }
        public string DisplayText { get; }
        public string TimeString { get; }

        public ClipboardItemViewModel(ClipboardEntry entry)
        {
            Entry = entry;
            string clean = entry.Text.Replace("\r", " ").Replace("\n", " ").Trim();
            DisplayText = clean.Length > 48 ? clean.Substring(0, 48) + "..." : clean;

            var span = DateTime.Now - entry.Timestamp;
            if (span.TotalSeconds < 60) TimeString = "刚刚";
            else if (span.TotalHours < 24) TimeString = entry.Timestamp.ToString("HH:mm");
            else TimeString = entry.Timestamp.ToString("MM/dd HH:mm");
        }
    }
}
