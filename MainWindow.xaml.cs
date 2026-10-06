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

        /// <summary>
        /// 通知图层宿主。所有弹出式通知（欢迎/蓝牙/音量/电池/剪切板）的显隐、互斥与命中测试
        /// 都由它统一算，新加通知只需 Register 一次 + Show/Hide，不再改六处。
        /// </summary>
        private readonly IslandLayerHost _layers = new();

        /// <summary>
        /// 多岛卫星（置顶气泡 / 左侧音乐球 / 卡片态旁路小提示）的横向偏移统管（S4）。
        /// 卫星不参与布局，偏移只能由这一处算，否则每加一个卫星就要回头改所有硬编码的 X。
        /// </summary>
        private readonly SatelliteRail _satellites = new();

        private SmtcService? _smtc;
        private AudioSpectrumService? _audio;
        // 切换输出设备后，环回采集要重建，这段时间采不到声音属于正常现象，
        // 不能拿来当"已暂停"的证据（否则换设备瞬间歌词会自己停一下）。
        private DateTime _audioDeviceSuppressUntil = DateTime.MinValue;

        /// <summary>卡片翻页动画进行中。期间是"拖动+鼠标捕获+整层布局"的敏感窗口，见 ProgressTimer_Tick。</summary>
        private bool _cardAnimating;

        /// <summary>
        /// "胶囊→卡片"这一次换态是否已经完成内容交接（一次性、不可逆）。
        /// 交接前胶囊内容保持满不透明，交接后整层立刻交给卡片，避免两边同时半透明导致内容发灰。
        /// 每次换态在 SetState 里复位。
        /// </summary>
        private bool _cardHandoff;

        /// <summary>内容交接发生的时间戳（TickCount64），用于把交接后的模糊按时间化开。</summary>
        private long _cardHandoffMs;

        /// <summary>播放器自身音频会话的"无声"阈值（0~1 峰值）：低于它认为这个播放器没在输出声音。</summary>
        private const float AudioSessionSilencePeak = 0.0015f;

        /// <summary>播放器会话从"有声"变成"无声"的时刻（TickCount64）；0 表示当前无声不成立。</summary>
        private long _appSilentSinceMs;

        /// <summary>播放器会话从"无声"变成"有声"的时刻（TickCount64）；0 表示当前没有持续有声。</summary>
        private long _appActiveSinceMs;
        private string _audioEvidenceSession = "";
        private long _audioEvidenceEpoch;

        /// <summary>媒体/音频仲裁的诊断开关：只有显式设置环境变量 ALPHA_ISLAND_SMTCDBG=1 时，
        /// 才每秒往日志写一行 SMTC_DBG（默认零噪声，复现"暂停后还在判为播放"时用来取现场）。</summary>
        private readonly bool _smtcDebugEnabled =
            Environment.GetEnvironmentVariable("ALPHA_ISLAND_SMTCDBG") == "1";
        private long _dbgLastMs;

        private LyricsService? _lyrics;
        private BluetoothService? _bluetooth;
        private VolumeService? _volumeService;
        private BatteryService? _battery;
        private HotkeyService? _hotkey;
        private ClipboardService? _clipboard;
        private FileTransferService? _fileTransfer;

        // ================= 电池状态通知 =================
        // 状态本体搬到了 _layers（Core/IslandLayerHost.cs）；这两个转发属性保留旧名字，
        // 让散落各处的读取点（Geometry / Reveal / PillLyrics / GetCurrentActionType…）一行都不用改。
        private bool _batteryNotifying => _layers.IsShowing("battery");
        private bool BatteryNotifyActive => _layers.IsActive("battery");

        // ================= 剪切板通知与卡片态 =================
        /// <summary>当前卡片页下标。唯一真源是 IslandPageHost（见 MainWindow.Pages.cs），
        /// 这里只做只读转发 —— 漏改的读取点也永远不会读到过期的页码。</summary>
        private int _currentCardPage => _pages.ActiveIndex;
        /// <summary>剪切板层有两个额外的活跃条件：锁定的内容一直算活跃，刚粘贴完的一小段时间也算。</summary>
        private bool _clipboardLocked;
        private DateTime _clipboardPastedUntil = DateTime.MinValue;
        private bool _clipboardNotifying => _layers.IsShowing("clipboard");
        private bool ClipboardNotifyActive => _layers.IsActive("clipboard");

        // ================= 手势与拖拽 =================
        private Point _swipeStartPoint;
        private Point _swipeStartScreenPoint;
        private bool _isSwiping;
        private bool _dragStartedInsideIsland;
        private Point _dragStartPoint;
        private TransferItem? _dragSourceItem;

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
        private bool _volumeNotifying => _layers.IsShowing("volume");
        private bool VolumeNotifyActive => _layers.IsActive("volume");

        // ================= 蓝牙通知 =================
        /// <summary>蓝牙通知正在显示。Pill 形态时 PillLayer 与 BtNotifyLayer 互斥。</summary>
        private bool _btNotifying => _layers.IsShowing("bluetooth");
        private bool BtNotifyActive => _layers.IsActive("bluetooth");

        // ================= 网速提示（A 组①） =================
        /// <summary>下行速率持续超过阈值时的一级胶囊提示。</summary>
        private bool _netSpeedNotifying => _layers.IsShowing("netspeed");
        private bool NetSpeedNotifyActive => _layers.IsActive("netspeed");

        // ================= 系统状态通用提示（A 组②③④⑤共用一层） =================
        /// <summary>内存整理/内存偏高/功率/飞行模式/隐私灯共用 "system" 图层，弹出前写内容。</summary>
        private bool _sysNotifying => _layers.IsShowing("system");
        private bool SysNotifyActive => _layers.IsActive("system");

        // ================= 当前展示动作类型（用于收缩回圆时的过渡图标） =================
        private enum IslandActionType
        {
            Music,
            Greeting,
            Volume,
            Battery,
            Bluetooth,
            Clipboard,
            FileTransfer,
            NetSpeed,
            SysNotify
        }
        /// <summary>
        /// 收缩回圆时圆心里装配哪一类专属图标（"避免一律用 Siri 渐变或专辑封面"，见 PrepareSiriOrbContent）。
        /// 以前这里是「先查 7 个通知层的活状态，再退回由 19 处手工赋值的 <c>_currentAction</c> 记忆」；
        /// 现在退路换成仲裁器自动维护的 <see cref="IslandLayerHost.MemorableKey"/>。
        /// 差别不只是少一个字段：**新加通知模块不必再记得**"在自己的弹窗路径里补一处 `_currentAction = ...`"，
        /// 忘了就是静默退化成封面（不报错、测不出），而现在只要走 <c>_layers.Request</c> 就自动进记忆。
        /// </summary>
        private IslandActionType GetCurrentActionType()
        {
            if (_greetingShowing && GreetingActive) return IslandActionType.Greeting;
            if (_volumeNotifying && VolumeNotifyActive) return IslandActionType.Volume;
            if (_batteryNotifying && BatteryNotifyActive) return IslandActionType.Battery;
            if (_btNotifying && BtNotifyActive) return IslandActionType.Bluetooth;
            if (_clipboardNotifying && ClipboardNotifyActive) return IslandActionType.Clipboard;
            if (_netSpeedNotifying && NetSpeedNotifyActive) return IslandActionType.NetSpeed;
            if (_sysNotifying && SysNotifyActive) return IslandActionType.SysNotify;
            if (_state == IslandState.Tray) return IslandActionType.FileTransfer;
            return ActionForLayerKey(_layers.MemorableKey);
        }

        /// <summary>
        /// 通知层 Key → 过渡图标类型。表里没有的层（vivo / download / agentstatus / approval）
        /// 退回音乐态（有封面放封面，没有就走 Siri 渐变）。
        /// </summary>
        private static IslandActionType ActionForLayerKey(string? key) => key switch
        {
            "greeting" => IslandActionType.Greeting,
            "volume" => IslandActionType.Volume,
            "battery" => IslandActionType.Battery,
            "bluetooth" => IslandActionType.Bluetooth,
            "clipboard" => IslandActionType.Clipboard,
            "netspeed" => IslandActionType.NetSpeed,
            "system" => IslandActionType.SysNotify,
            _ => IslandActionType.Music
        };

        // ================= 启动问候 =================
        private bool _greetingShowing => _layers.IsShowing("greeting");
        private bool GreetingActive => _layers.IsActive("greeting");
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
        private string _lyricsFetchingKey = "";
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

        /// <summary>歌词首次拿不到时的最大重试次数（网络抖动/换曲瞬间时长不可靠都会导致假失败）。</summary>
        private const int LyricsFetchMaxRetry = 2;

        /// <summary>歌词重试间隔毫秒。</summary>
        private const int LyricsFetchRetryDelayMs = 5000;

        private readonly bool _runtimeEnabled;

        public MainWindow() : this(null) { }

        // 隔离的界面验证入口：不占单实例锁，不注册托盘、钩子或后台服务。
        internal MainWindow(IslandOptions? isolatedOptions)
        {
            _runtimeEnabled = isolatedOptions == null;
            // 单实例：第二次启动直接提示并退出，避免两个岛互相抢钩子
            if (_runtimeEnabled)
            {
                _mutex = new Mutex(true, "DynamicIslandDemo_SingleInstance_Mutex", out bool isNewInstance);
                if (!isNewInstance)
                {
                    System.Windows.MessageBox.Show("Alpha Island 已经在运行了（请查看右下角托盘图标）。",
                        "Alpha Island", MessageBoxButton.OK, MessageBoxImage.Information);
                    System.Windows.Application.Current.Shutdown();
                    return;
                }
            }

            InitializeComponent();

            // 通知图层注册。此处就定义了"哪些弹出通知存在"，显隐/互斥/命中测试全部由
            // _layers 统一算（见 SyncNotificationLayers），不再逐个图层手写三行。
            // Priority = 同屏仲裁优先级（数值大者赢，只有一个层能画在那块 328x32 上）：
            // 必须回应的用户动作/提醒 > 持续性背景状态。
            _layers.Register(new IslandLayerSpec(IslandLayerHost.GreetingKey, GreetingLayer, Priority: 100));
            _layers.Register(new IslandLayerSpec("bluetooth", BtNotifyLayer,
                OnVisibilityChanged: visible => BtDeviceModel.IsRotating = visible,
                Priority: 76));
            _layers.Register(new IslandLayerSpec("volume", VolumeNotifyLayer, Priority: 80));
            _layers.Register(new IslandLayerSpec("battery", BatteryNotifyLayer, Priority: 72,
                PriorityOverride: () => _battery?.Current?.EventType is BatteryPowerEvent.LowBattery or BatteryPowerEvent.CriticalBattery ? 84 : 72));
            _layers.Register(new IslandLayerSpec("clipboard", ClipboardNotifyLayer,
                Scope: IslandLayerScope.PillOrClipboardPage,
                // 老语义：ClipboardNotifyActive = _clipboardNotifying && (locked || 刚粘贴完)
                ForceActive: () => _clipboardLocked || DateTime.UtcNow < _clipboardPastedUntil,
                Priority: 56));
            // A 组①：下行持续超过阈值时的提示（弹出时机由 MainWindow.MaybeAlertHighSpeed 决定）。
            // 同一个层平时是"下载中"的网速胶囊（优先级 64，让位于下载卡片 66）；
            // 阈值告警的那 4 秒用 PriorityOverride 升到 70 —— 否则下载提示每 2 秒续期一次，
            // 告警的 Show 永远输在仲裁上：屏幕上什么都不出现，60 秒冷却却已经开始计时。
            _layers.Register(new IslandLayerSpec("netspeed", NetSpeedNotifyLayer,
                Priority: 64,
                PriorityOverride: () => DateTime.UtcNow < _netSpeedAlertUntilUtc ? 70 : 64));
            // A 组②③④⑤：内存/功率/飞行模式/隐私灯共用的"系统状态"提示层
            // （弹出前由 ShowSystemNotify 写字形/标题/说明/数值，所以加新提示不用再加 XAML 层）
            _layers.Register(new IslandLayerSpec("system", SystemNotifyLayer, Priority: 68));
            // B 组③：agent 运行状态（思考/输出/等待授权/出错）。状态只要不是 Idle 就靠 ForceActive
            // 一直占住胶囊，音乐图层自动让位；归零后由 SyncNotificationLayers 收回。
            _layers.Register(new IslandLayerSpec("agentstatus", AgentNotifyLayer,
                // 有审批请求在等的时候让位：两层都是 328x32 居中，同时显示会叠在一起（审批本身已经说明"等待授权"）。
                ForceActive: () => _approvalCurrent == null
                                   && (AgentStatus.IsBusy || AgentStatus.State == AgentRunState.Error),
                Priority: 48));
            // B 组④：agent 工具权限审批（外部队列 → 用户点允许/拒绝或按 Y/N → 结果回传）。
            // 有待裁决请求时 ForceActive 一直占住，不靠超时续期；裁决后立刻 Hide。
            _layers.Register(new IslandLayerSpec("approval", ApprovalLayer,
                ForceActive: () => _approvalCurrent != null,
                Priority: 90, RequiresAttention: true));
            // 第二批②：手机联动（vivo 办公套件记录文件里"刚连上 / 刚断开"时弹一次）。
            // 优先级 74 = 在音量(80)/蓝牙(76)之下、电池(72)之上：它是信息提示，不该压过用户正在操作的音量条。
            // 第二批③：下载进度（浏览器 / Steam）。下载中每 2 秒续期一次 = 常显，
            // 优先级 66 = 压过网速(64)、让位于电池(72)/系统状态(68)/用户正在操作的音量(80)。
            _layers.Register(new IslandLayerSpec("download", DownloadNotifyLayer, Priority: 66));

            // 一级胶囊的内容层（PillLayer）也交给宿主（S3）。它不是"通知图层"（不参与仲裁），
            // 而是"没有任何通知占位时"的默认内容，判据仍是 WantPillLayerVisible()，
            // 但写它 Opacity/Visibility 的从此只剩 IslandLayerHost 一处 ——
            // 以前这里有四套写手（宿主 / ApplyReveal 白名单 / 换态完成回调 / 音乐圆球时间轴），
            // 互相不知道对方，谁后跑谁赢，叠层与随机闪烁都从这来。
            _layers.RegisterPrimary(PillLayer, WantPillLayerVisible, OnPillLayerVisibilityChanged);

            // 多岛卫星车道（S4）：卫星不在 WPF 布局里（都在同一格居中 + TranslateTransform 手挪），
            // 所以"互不重叠"只能由 SatelliteRail 统一算。原来每个卫星各自硬编码 X
            //（置顶气泡 226 / 音乐球 -204、-222），加第三个必然撞车，且长歌词把岛撑宽后数字就错了。
            // 这里只声明"我想贴在哪"+"我多宽"，谁可见由 lambda 现问现答（每帧算，跟随形变）。
            _satellites.Register("pin", SatelliteSide.Right,
                inner: () => CurrentIslandVisualWidth / 2.0 + PinBubbleGap,
                width: () => PinBubble.Width);
            _satellites.Register("music-orb", SatelliteSide.Left,
                // 复刻原来的固定值：胶囊态 204、中转站 222（= inner + 36/2），改成走车道的数字逐位相同。
                inner: () => _state == IslandState.Tray ? 204.0 : 186.0,
                width: () => MusicOrbBubble.Width,
                visible: () => MusicOrbBubble.Visibility == Visibility.Visible && MusicOrbBubble.Opacity > 0.01);
            // 卡片态旁路小提示：走**左**车道。实测卡片态岛宽 430（半宽 215）+ 右侧置顶气泡已经
            // 吃掉 96px，右侧窗口只剩不到 90px，再塞一个 112 宽的提示必然和置顶气泡叠上
            //（第一次实测就是这个结果：pin X=269、side-hint 被窗口夹到 336，重叠 31px）。
            // 左车道在卡片态是空的（音乐球只在胶囊/中转站显示），所以这里天然不会撞。
            _satellites.Register("side-hint", SatelliteSide.Left,
                inner: () => CurrentIslandVisualWidth / 2.0 + PinBubbleGap,
                width: () => SideBubble.Width,
                visible: () => SideBubble.Visibility == Visibility.Visible && SideBubble.Opacity > 0.01);

            // 写入口立刻重算显隐：没有这一句，一次弹出要等下一拍别的同步才画出来，
            // 而这段时间里音乐胶囊还占着同一块位置（A 组系统提示的叠层就是这个成因）。
            _layers.SyncRequested = SyncNotificationLayers;

            _options = isolatedOptions ?? IslandOptions.Load();

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
            if (_runtimeEnabled) _hoverTimer.Start();

            // 必须是 Background：Tick 里有一次同步的 WinRT 跨进程调用（GetPlaybackInfo），
            // 用 Normal 会抢在鼠标输入（Input=5）与渲染（Render=7）之前执行，
            // 翻页拖动时表现为"手一动就卡"，快速连翻直接卡死。
            _progressTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _progressTimer.Tick += ProgressTimer_Tick;

            _mediaLostTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(1200)
            };
            _mediaLostTimer.Tick += (_, __) =>
            {
                _mediaLostTimer.Stop();
                if (!MusicIslandHasMedia) ApplyNoMedia();
            };

            if (_runtimeEnabled)
            {
                _tray = new TrayIcon();
                _tray.SettingsRequested += (_, __) => Dispatcher.BeginInvoke(DispatcherPriority.Normal, ShowSettings);
                _tray.ExitRequested += (_, __) => Dispatcher.BeginInvoke(DispatcherPriority.Normal, ExitApplication);
                _tray.MemoryTrimRequested += (_, __) => Dispatcher.BeginInvoke(DispatcherPriority.Normal, TrimMemoryNow);
                _tray.Show();
                _tray.SetMemoryTrimEnabled(SettingsRegistry.GetBool(_options, "MemoryTrimEnabled", false));
            }

            // 分页宿主必须先建：SetState(Card) 会按 Id 选页，而页集合是它的唯一真源。
            BuildPageHost();

            // 服务宿主必须先建：ApplyOptions() 里的服务部分已经收敛成 _services.Sync(_options)。
            BuildServiceHost();
            BuildPluginHost();

            if (_runtimeEnabled) ApplyOptions();
            else
            {
                ApplyGlassAppearance();
                _pages.Sync(_options);
                SyncCardPageVisuals();
            }
        }

        /// <summary>
        /// 调试用：设 <c>ALPHA_ISLAND_STATE=pill|card|tray|orb</c> 可让岛体开机就停在指定形态，
        /// 且悬停轮询不再自动收回。仅为自动化截图/测量服务，正常启动时该值为 null。
        /// </summary>
        private readonly string? _forcedState = Environment.GetEnvironmentVariable("ALPHA_ISLAND_STATE");
        private string? _pillLyricForce;

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

                _settingsWindow ??= CreateSettingsWindow();
                _settingsWindow.LoadFrom(_options);
                ApplySettingsRuntimeText(_settingsWindow);

                _settingsWindow.ShowInForeground();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Settings] 打开异常，重新创建: " + ex);
                try { _settingsWindow?.Close(); } catch { }
                _settingsWindow = null;

                try
                {
                    _settingsWindow = CreateSettingsWindow();
                    _settingsWindow.LoadFrom(_options);
                    ApplySettingsRuntimeText(_settingsWindow);
                    _settingsWindow.ShowInForeground();
                }
                catch (Exception ex2)
                {
                    System.Diagnostics.Debug.WriteLine("[Settings] 重新创建后仍打开失败: " + ex2);
                }
            }
        }

        /// <summary>
        /// 建设置窗口的唯一入口：正常打开与「异常后重建」两条路径共用，
        /// 避免重建路径漏订阅事件（历史上就漏过设备名/歌词源的回填）。
        /// </summary>
        private SettingsWindow CreateSettingsWindow()
        {
            var window = new SettingsWindow();
            window.BindHardwarePowerActions(EnableHardwarePowerFromSettingsAsync);
            if (_plugins != null) window.BindPluginHost(_plugins);
            window.Closed += (_, __) => _settingsWindow = null;
            window.OptionsChanged += (_, options) =>
            {
                _options.CopyFrom(options);
                ApplyOptions();
            };
            window.ResetRequested += Settings_ResetRequested;
            window.NotificationCenterRequested += (_, _) =>
            {
                _notificationAutoCard = false;
                SetState(IslandState.Card); SwitchCardPage(_pages.IndexOf(IslandPageIds.Notifications), animated: false);
            };
            return window;
        }

        /// <summary>回填那些「不进 options 的运行时文字」：当前输出设备名、当前歌词源。</summary>
        private void ApplySettingsRuntimeText(SettingsWindow window)
        {
            var audio = _audio;
            window.UpdateDeviceText(audio?.DeviceName ?? "未检测到输出设备");
            // 歌词按「网易云 → LRCLIB → 酷狗」的顺序回退，命中哪个源就显示哪个
            window.UpdateLyricsSource(_lyrics == null
                ? "网易云 → LRCLIB → 酷狗（未启用）"
                : $"网易云 → LRCLIB → 酷狗 · 当前：{_lyricsDoc?.Source ?? "尚未命中"}");
        }

        /// <summary>
        /// 设置里点「恢复默认」后的运行时清理。默认值此时已由设置窗口写回 options 并经 OptionsChanged
        /// 生效，这里只收干净那些「按旧设置留在屏幕上的东西」：通知层、音乐球、卡片抽屉、固定态、分页。
        /// </summary>
        private void Settings_ResetRequested(object? sender, EventArgs e)
        {
            try
            {
                DismissActiveNotifications();
                HideMusicOrb(true);
                ResetCardDrawers();
                _isPinned = false;
                ShowPinBubble(false);
                _pages.SetActiveIndex(_pages.IndexOfOrDefault(IslandPageIds.Music));
                SyncCardPageVisuals();

                if (_state == IslandState.Card)
                {
                    SetState(IslandState.Bar, force: true);
                }

                IslandLog.Debug("Settings", "恢复默认：通知层/音乐球/抽屉/固定态已清理，卡片回到音乐页");
            }
            catch (Exception ex)
            {
                IslandLog.Warn("Settings", "恢复默认后清理运行时状态失败: " + ex.Message);
            }
        }

        /// <summary>把 options 应用到所有活着的部件。设置改动后统一走这里，避免漏项。</summary>
        private void ApplyGlassAppearance()
        {
            double opacity = Math.Clamp(_options.GlassOpacity, 0.05, 0.98);

            // 当真实毛玻璃可用时，同步调整其精灵层的不透明度，实现真正的全动态背景通透调节
            _backdrop?.SetOpacity(Math.Clamp(opacity * 1.1, 0.05, 1.0));

            // 玻璃背景渐变底色：随 opacity 全动态线性缩放，通透时宛如水晶玻璃，拉满时深邃厚重
            byte topAlpha = (byte)Math.Clamp(Math.Round(12 + opacity * 220), 10, 245);
            byte bottomAlpha = (byte)Math.Clamp(Math.Round(22 + opacity * 230), 18, 255);
            GlassTopStop.Color = Color.FromArgb(topAlpha, 0x12, 0x12, 0x18);
            GlassBottomStop.Color = Color.FromArgb(bottomAlpha, 0x07, 0x07, 0x0B);

            // 置顶提示用和主岛同一块玻璃底色（共享同一个画刷实例，所以上面的透明度改值会自动跟过来）。
            // 之前它是写死的纯色 #E5101016，比半透明的主岛实得多，浮在旁边很突兀。
            if (PinBubble != null && IslandBody?.Background is LinearGradientBrush glassFill)
            {
                PinBubble.Background = glassFill;
                // 卡片态旁路小提示（S4）同属右侧卫星车道，跟置顶气泡共用同一块玻璃底色。
                if (SideBubble != null) SideBubble.Background = glassFill;
                if (MusicOrbBubble != null) MusicOrbBubble.Background = glassFill;
                foreach (var orb in _satelliteOrbViews.Values) orb.Background = glassFill;
            }

            // 高通透度下相应柔化 SheenLayer，避免一层过厚的白雾盖在玻璃上
            if (SheenLayer != null)
            {
                SheenLayer.Opacity = Math.Clamp(opacity * 0.40, 0.02, 0.45);
            }
        }

        private void ApplyOptions()
        {
            _pomodoro.Configure(PomodoroOptions.From(_options));
            SyncFocus();
            EnsureBackdrop();
            SyncBackdrop();
            ApplyGlassAppearance();

            if (_audio != null) _audio.Sensitivity = (float)_options.Sensitivity;
            if (_lyrics != null) _lyrics.Enabled = _options.LyricsEnabled;

            PillCoverHost.Visibility = _options.ShowCover ? Visibility.Visible : Visibility.Collapsed;
            CardWave.Visibility = _options.ShowWaveLayer ? Visibility.Visible : Visibility.Collapsed;

            // 封面会占掉歌词的可用宽度（32px），开关一变就要重算胶囊该多宽。
            UpdatePillLyricScroll();

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
                // 暂停或当前句未变化时，翻译开关也即时更新，避免等到下一句。
                SyncCurrentLyricTranslation();
            }

            // 服务启停全部交给宿主（阶段 3.3）：音量/电池/蓝牙的「开关 ∧ 实例空 → 建 + 订阅 + 后台 Start」
            // 与「关掉 → 退订 + Dispose」三组手写分支已收敛到 _services.Sync 里，每个服务独立 try/catch。
            _services.Sync(_options);
            _plugins?.Sync(_options);
            OnStatusDisplayOptionsChanged();
            OnDesktopNotificationOptionsChanged();

            // 分页宿主也吃设置：某页在当前设置下不可用时它就从翻页序列里下线（页与翻页点同源）。
            _pages.Sync(_options);
            SyncCardPageVisuals();
            SyncPausedMediaVisibility();

            // AI 卡片页头部文字（模型名 / 密钥状态）跟着设置走。
            OnAiOptionsChanged();

            // agent 状态色点 / 胶囊（B 组③）也吃设置里的开关。
            OnAgentOptionsChanged();

            // 「减弱动效」「节能模式」（C 组②）：策略灌进 MotionPolicy，并按当前形态重算节能节奏。
            ApplyMotionPolicy();
            SyncMediaWindowOptions();

            // 「3D 封面」（C 组④）：默认关闭；开启时先过渲染层级门槛，再探一次 3D 到底画不画得出来。
            ApplyCover3DMode();

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
            StopMediaWindowHandoff();
            EndVolumeDrag();
            CloseComponentSwitcher();
            if (!_runtimeEnabled)
            {
                _plugins?.Dispose();
                StopUtilities();
                _hoverTimer?.Stop();
                _progressTimer?.Stop();
                _mediaLostTimer?.Stop();
                _ticker?.Dispose();
                _animator?.Stop();
                return;
            }
            if (_exiting) return;
            _exiting = true;
            _plugins?.Dispose();
            StopUtilities();
            IslandLog.Info("Island", "ExitApplication —— 开始优雅退出");

            try
            {
                // 退出前落盘一次：把新增的 Extras 默认值一并写入 options.json（设置窗口没开过也能落），
                // 同时验证一轮「读 → 规范化 → 序列化」往返。
                try
                {
                    _options.Normalized().Save();
                }
                catch (Exception ex)
                {
                    IslandLog.Warn("Options", "退出前保存设置失败: " + ex.Message);
                }

                _hoverTimer?.Stop();
                _progressTimer?.Stop();
                _mediaLostTimer?.Stop();
                _greetingTimer?.Stop();

                // 低频采样节拍先停（阶段 3.6）：服务释放期间不该再有任何 tick 回调进来
                _ticker?.Dispose();
                _ticker = null!;

                StopKaraoke();
                _animator?.Stop();
                CompositionTarget.Rendering -= OnBackdropTick;
                _backdrop?.Dispose();
                _backdrop = null;

                // 服务释放统一走宿主（阶段 3.3）：逐个 try/catch，一个服务释放失败不连累其余。
                // 顺序仍是「电池 → 剪切板/热键 → 蓝牙 → 音量 → 歌词 → 频谱 → SMTC → 文件中转站」。
                _services?.StopAll();

                // 屏幕右上角的占用灯是独立窗口（不进岛体窗口的矩形），必须单独收掉，
                // 否则 Application.ShutdownMode=OnLastWindowClose 会因为它还开着而卡住退出。
                _privacyIndicator?.Dispose();
                _privacyIndicator = null;

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
