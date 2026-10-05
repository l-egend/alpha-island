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

// Startup pipeline, service wiring, Win32 message filter.
// Moved verbatim out of the former single-file MainWindow.xaml.cs; no logic changes.
namespace alpha_island
{
    public partial class MainWindow
    {
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

        /// <summary>诊断开关 ALPHA_ISLAND_HITTEST=1：把每次 WM_NCHITTEST 的判定与岛矩形写进日志。
        /// 脚本化点击验收用它回答"注入的点击为什么没进 WPF"（鼠标在窗口上移动时才会打，默认关闭）。</summary>
        private readonly bool _debugHitTest =
            Environment.GetEnvironmentVariable("ALPHA_ISLAND_HITTEST") == "1";

        /// <summary>诊断开关 ALPHA_ISLAND_INPUTDBG=1：把每条鼠标按钮窗口消息写进日志。
        /// 与 HITTEST 配合能一次分清「注入的消息根本没到窗口」和「到了但 WPF 没路由成事件」。</summary>
        private readonly bool _debugInput =
            Environment.GetEnvironmentVariable("ALPHA_ISLAND_INPUTDBG") == "1";

        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x007E || msg == 0x02E0)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (_exiting) return;
                    PositionWindow();
                    _privacyIndicator?.Reposition();
                }));
            if (_debugInput
                && (msg == WM_MOUSEMOVE || msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP || msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP))
            {
                int cx = unchecked((short)(lParam.ToInt32() & 0xFFFF));
                int cy = unchecked((short)((lParam.ToInt32() >> 16) & 0xFFFF));
                IslandLog.Debug("InputDebug", $"按钮消息 0x{msg:X4} client=({cx},{cy}) 形态 {_state} 激活={IsActive} 前台=0x{NativeMethods.GetForegroundWindow().ToInt64():X}");
            }

            if (msg == WM_POWERBROADCAST)
            {
                _battery?.NotifyPowerBroadcast();
            }
            else if (msg == WM_NCHITTEST)
            {
                int x = unchecked((short)(lParam.ToInt32() & 0xFFFF));
                int y = unchecked((short)((lParam.ToInt32() >> 16) & 0xFFFF));

                bool inside = IsPointInsideInteractiveZone(x, y);
                if (_debugHitTest)
                {
                    IslandLog.Debug("HitTest", $"NCHITTEST ({x},{y}) → {(inside ? "命中窗口" : "HTTRANSPARENT")}｜岛矩形 {_cachedIslandScreenRect}｜形态 {_state}");
                }

                if (!inside)
                {
                    handled = true;
                    return new IntPtr(HTTRANSPARENT);
                }
            }
            return IntPtr.Zero;
        }

        private bool IsPointInsideInteractiveZone(int screenX, int screenY)
        {
            if (IsPointOnMediaSourcesMenu(screenX, screenY)) return true;
            if (IsPointOnCompactIsland(screenX, screenY)) return true;
            if (IsPointOnFileDropZone(screenX, screenY)) return true;
            if (IsPointOnComponentHandle(screenX, screenY)) return true;
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
            // 卫星小岛（多岛并行显示与置顶）命中测试：确保所有缩小到左侧的小岛都能接收左键切换与右键置顶
            if (_satelliteOrbViews.Count > 0)
            {
                foreach (var orb in _satelliteOrbViews.Values)
                {
                    if (orb != null && orb.Visibility == Visibility.Visible && orb.Opacity > 0.1 && orb.ActualWidth > 0 && orb.ActualHeight > 0)
                    {
                        try
                        {
                            var tl = orb.PointToScreen(new Point(0, 0));
                            var br = orb.PointToScreen(new Point(orb.ActualWidth, orb.ActualHeight));
                            if (new Rect(tl, br).Contains(new Point(screenX, screenY))) return true;
                        }
                        catch { }
                    }
                }
            }
            return false;
        }

        // ================= 启动 =================

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_runtimeEnabled) return;
            PositionWindow();
            ApplyOptions();

            try
            {
                IntPtr fg = NativeMethods.GetForegroundWindow();
                var myHwnd = new WindowInteropHelper(this).Handle;
                if (fg != IntPtr.Zero && fg != myHwnd) _lastExternalForegroundHwnd = fg;
            }
            catch { }

            // 全局鼠标钩子：只用于"点击岛外立即收起"与岛外拖拽，不做悬停判定。
            // 钩子由 MouseHook 自己的宿主线程安装并抽消息（见 Interop/HookThread.cs）——
            // 装到 UI 线程上的话，UI 线程一卡就整机鼠标一起停（alpha_74 实测过 800ms/2s 的冻鼠）。
            // 回调里只做 O(1) 判断，事件一律 Post 回 UI 线程执行，保证钩子线程立刻返回。
            _mouseHook = new MouseHook();
            _mouseHook.ButtonDown += OnGlobalButtonDown;
            _mouseHook.IsInsideIsland = (x, y) => IsPointInsideIsland(x, y);
            _mouseHook.DragMove += OnMouseHookDragMove;
            _mouseHook.DragEnd += OnMouseHookDragEnd;
            _mouseHook.Post = action =>
            {
                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(action, DispatcherPriority.Input);
            };
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
                    // 若未开启问候，也稍微留一点缓冲，等主窗口完全呈现与桌面就绪
                    await Task.Delay(120);
                }

                if (_exiting) return;
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(InitializeServicesDeferred));
            });

            if (string.Equals(Environment.GetEnvironmentVariable("ALPHA_ISLAND_OPEN_SETTINGS"), "1", StringComparison.OrdinalIgnoreCase))
            {
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ShowSettings));
            }

            // 卡片子树不再需要启动期"冷排版预热"：CardCarouselHost 现在固定尺寸且常驻 Visible，
            // 第一次窗口排版时它就已经被测量/排列过，之后一直保持有效（见 MainWindow.xaml 的 CardCarouselHost）。
        }

        /// <summary>尽早拉起 WASAPI 频谱采集（实现里已把 Start() 丢后台线程，失败不影响启动）。已启动则直接返回。</summary>
        private void StartAudioSpectrum()
        {
            if (_exiting) return;
            EnsureService(IslandServiceIds.Spectrum, "频谱采集");
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
            EnsureService(IslandServiceIds.Lyrics, "模块 1/8 [歌词服务]");

            // 说明：原来每个模块之间都插 600/700ms 的固定 await Task.Delay 做"节流"。
            // InitializeServicesDeferred 是 async void 且带 DispatcherSynchronizationContext，
            // await 时 UI 线程是让出状态、并不会卡界面 —— 这些延时纯粹是把"服务全部就绪"
            // 从约 1 秒硬拖到近 5 秒（实测），对启动速度没有任何好处，故全部去掉。
            if (_exiting) return;

            // 2. 全局热键与剪切板服务
            EnsureService(IslandServiceIds.Clipboard, "模块 2/8 [剪切板与热键]");

            if (_exiting) return;

            // 3. 文件中转站
            EnsureService(IslandServiceIds.FileTransfer, "模块 3/8 [文件中转站]");

            if (_exiting) return;

            // 4. 系统主音量监听（后台执行 COM 终结点绑定，带超时保护）
            //    重复创建的护栏现在在宿主状态机里：ApplyOptions() 已经拉起来的话，EnsureStarted 直接返回。
            EnsureService(IslandServiceIds.Volume, "模块 4/8 [系统音量]");

            if (_exiting) return;

            // 5. 电池状态监听（后台线程执行首次硬件电源采样，带超时保护）
            EnsureService(IslandServiceIds.Battery, "模块 5/8 [电池服务]");

            if (_exiting) return;

            // 6. 媒体：SMTC 系统媒体传输控制（后台请求 WinRT Session，带超时保护）
            //    实例创建与事件订阅都在 StartSmtcService()（那里注释解释了为什么必须先订阅再初始化），
            //    这里只负责「等首帧 + 兜底补一帧」。
            try
            {
                _services.EnsureStarted(IslandServiceIds.Smtc);

                var smtc = _smtc;
                if (smtc != null)
                {
                    var smtcTask = Task.Run(async () => await smtc.InitializeAsync());
                    // 只等 1.5s：SMTC 首帧拿不到也没关系，下面的兜底与后续事件都会补上，
                    // 而这里是启动路径上唯一还会阻塞"服务全部就绪"的 await（原来等 4s）。
                    await Task.WhenAny(smtcTask, Task.Delay(1500));

                    // 兜底：初始化超时，或在订阅前就已发出首帧时，用 Current 补一次首帧，
                    // 保证媒体状态机（_lastIsPlaying / _sawMedia）一定被初始化。
                    if (!_sawMedia && smtc.Current?.HasMedia == true)
                    {
                        OnNowPlayingChanged(smtc, smtc.Current);
                    }
                }

                System.Diagnostics.Debug.WriteLine("[Startup] 模块 6/8 [SMTC媒体] 就绪");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SMTC] 初始化异常: " + ex.Message);
            }

            if (_exiting) return;

            // 7. 音频：WASAPI 环回采集 → 频谱（已在 Window_Loaded 里提前启动，这里只做兜底）
            EnsureService(IslandServiceIds.Spectrum, "模块 7/8 [WASAPI频谱]");

            if (_exiting) return;

            // 8. 蓝牙：设备连接通知（后台线程启动 WinRT DeviceWatchers，带超时保护）
            //    三个事件一次订阅齐、只可能有一个实例 —— 这两条纪律现在都在 StartBluetoothService() 里。
            EnsureService(IslandServiceIds.Bluetooth, "模块 8/8 [蓝牙服务]");

            if (_exiting) return;

            // 9. 能力探测：把「哪些可选项真能用」提前探出来（关于页 / 各功能降级分支都要读）。
            //    异步能力（无线电、出网）自己后台跑；同步能力丢到线程池，UI 线程不碰注册表/DPAPI。
            _ = CapabilityProbe.WarmUpAsync();
            _ = Task.Run(() => CapabilityProbe.CheckAll());

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
                    // 调试定态还支持 tray（文件中转站）—— 它是"非胶囊态里唯一能收阻塞型通知"的形态，
                    // 审批层在 Tray 态被 ScopeAllows 挡掉是协议记录过的回归点，留个可复现入口。
                    IslandState forced = _forcedState.Equals("card", StringComparison.OrdinalIgnoreCase)
                        ? IslandState.Card
                        : _forcedState.Equals("tray", StringComparison.OrdinalIgnoreCase)
                            ? IslandState.Tray
                            : IslandState.Pill;
                    SetState(forced, force: true);

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

            // 调试：ALPHA_ISLAND_STATE_SWITCH=<毫秒>:<形态> —— 开机 N 毫秒后自动切一次形态
            //（形态同 ALPHA_ISLAND_STATE：bar|pill|card|tray）。
            // 用途：验证 S4 的核心断言「卡片态被挡住的通知，回到胶囊态后补显」——
            // 不依赖鼠标手势也能走一遍 Card→Pill，脚本里只靠日志就能判定。
            string? stateSwitch = Environment.GetEnvironmentVariable("ALPHA_ISLAND_STATE_SWITCH");
            if (!string.IsNullOrEmpty(stateSwitch))
            {
                string[] switchParts = stateSwitch.Split(':', 2);
                if (switchParts.Length == 2 && double.TryParse(switchParts[0], out double switchAfterMs))
                {
                    IslandState switchTarget = switchParts[1].Trim().ToLowerInvariant() switch
                    {
                        "bar" => IslandState.Bar,
                        "pill" => IslandState.Pill,
                        "card" => IslandState.Card,
                        _ => IslandState.Tray,
                    };
                    var stateSwitchTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(Math.Max(0, switchAfterMs)),
                    };
                    stateSwitchTimer.Tick += (_, _) =>
                    {
                        stateSwitchTimer.Stop();
                        IslandLog.Debug("Debug", $"调试定态切换：{_state} → {switchTarget}（{switchAfterMs:0} ms 后）");
                        SetState(switchTarget, force: true);
                    };
                    stateSwitchTimer.Start();
                }
            }

            // 调试：ALPHA_ISLAND_CARD_PAGE 可让岛体开机就停在指定卡片页 ——
            // 写数字 = 页索引（0 起）；写页 id（music/clipboard/volume/ai/netspeed/system）也行，
            // 因为加了新页之后索引会随"某页不可用"而变，脚本里按 id 断言才稳。
            // （配合 ALPHA_ISLAND_STATE=card 核对翻页与页高）；再加
            // ALPHA_ISLAND_CARD_PAGE_ANIM=1 则走 260ms 翻页动画那条路径（验证动画收尾的命中恢复）。
            string? forcedCardPage = Environment.GetEnvironmentVariable("ALPHA_ISLAND_CARD_PAGE");
            if (!string.IsNullOrEmpty(forcedCardPage) && _state == IslandState.Card)
            {
                bool animatedCardPage = Environment.GetEnvironmentVariable("ALPHA_ISLAND_CARD_PAGE_ANIM") == "1";

                if (int.TryParse(forcedCardPage, out int forcedCardPageIndex))
                {
                    SwitchCardPage(forcedCardPageIndex, animated: animatedCardPage);
                }
                else
                {
                    int byId = _pages.IndexOf(forcedCardPage);
                    if (byId >= 0)
                    {
                        SwitchCardPage(byId, animated: animatedCardPage);
                    }
                    else
                    {
                        IslandLog.Warn("Debug", $"调试钩子：卡片页「{forcedCardPage}」不存在（既不是索引也不是页 id），已忽略");
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

            // 调试：ALPHA_ISLAND_MEMORY_TRIM=<秒> 启动 N 秒后自动触发一次「整理内存」，
            // 用来验证托盘菜单那条链路（免点鼠标）。
            string? autoTrim = Environment.GetEnvironmentVariable("ALPHA_ISLAND_MEMORY_TRIM");
            if (int.TryParse(autoTrim, out int trimAfterSeconds) && trimAfterSeconds > 0)
            {
                var trimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(trimAfterSeconds) };
                trimTimer.Tick += (_, __) =>
                {
                    trimTimer!.Stop();
                    IslandLog.Debug("Memory", "调试钩子：自动触发「整理内存」");
                    TrimMemoryNow();
                };
                trimTimer.Start();
            }

            // 调试：ALPHA_ISLAND_POWER_FAKE=<瓦数> —— 伪造放电功率（插着电源也能验证功耗提示链路）
            string? fakePower = Environment.GetEnvironmentVariable("ALPHA_ISLAND_POWER_FAKE");
            if (double.TryParse(fakePower, out double fakeWatts) && fakeWatts > 0)
            {
                _powerFakeWatts = fakeWatts;
                IslandLog.Debug("Power", $"调试注入：放电功耗 {fakeWatts:0.#} W");
                _ticker?.TriggerNow("power");
            }

            // 调试：ALPHA_ISLAND_AIRPLANE_FAKE=on|off —— 伪造飞行模式状态（不动真实无线电）；
            // 再加 ALPHA_ISLAND_AIRPLANE_FLIP=<秒> 则在 N 秒后翻转一次，用来验证「状态变化 → 弹提示」。
            string? fakeAirplane = Environment.GetEnvironmentVariable("ALPHA_ISLAND_AIRPLANE_FAKE");
            bool? airplaneValue = null;
            if (string.Equals(fakeAirplane, "on", StringComparison.OrdinalIgnoreCase) || fakeAirplane == "1") airplaneValue = true;
            else if (string.Equals(fakeAirplane, "off", StringComparison.OrdinalIgnoreCase) || fakeAirplane == "0") airplaneValue = false;

            if (airplaneValue.HasValue)
            {
                _airplaneFake = airplaneValue;
                IslandLog.Debug("Radio", $"调试注入：飞行模式 = {(airplaneValue.Value ? "开" : "关")}");
                _ticker?.TriggerNow("radio");

                string? flipAfter = Environment.GetEnvironmentVariable("ALPHA_ISLAND_AIRPLANE_FLIP");
                if (int.TryParse(flipAfter, out int flipSeconds) && flipSeconds > 0)
                {
                    var flipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(flipSeconds) };
                    flipTimer.Tick += (_, __) =>
                    {
                        flipTimer!.Stop();
                        _airplaneFake = !airplaneValue.Value;
                        IslandLog.Debug("Radio", $"调试注入：飞行模式翻转为 {(_airplaneFake.Value ? "开" : "关")}");
                        _ticker?.TriggerNow("radio");
                    };
                    flipTimer.Start();
                }
            }

            // 调试：ALPHA_ISLAND_AI_SELFTEST=1 —— 开机 3 秒后向配置里的接口发一次真实请求，
            // 用来验证 SSE 流式解析 / HTTP 错误翻译 / 超时链路（没填 Key 时应看到中文的 401 提示）。
            // =2 则改为做一次 DPAPI 密钥往返（加密→回读→还原，不动用户已存的密钥）。
            string? aiSelfTest = Environment.GetEnvironmentVariable("ALPHA_ISLAND_AI_SELFTEST");
            if (aiSelfTest == "2")
            {
                RunAiSecretSelfTest();
            }
            else if (aiSelfTest == "1")
            {
                var aiTestTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                aiTestTimer.Tick += async (_, __) =>
                {
                    aiTestTimer!.Stop();
                    await RunAiSelfTestAsync();
                };
                aiTestTimer.Start();
            }

            // 调试：ALPHA_ISLAND_AI_ASK=<文本> —— 开机 N 秒后（默认 4 秒，可用
            // ALPHA_ISLAND_AI_ASK_DELAY 改）往 AI 卡片页输入框里灌一句话并发送，
            // 用来在没有人手的情况下跑通「用户气泡 → 流式追加 → 错误/回复 → 状态复位」整条 UI 链路。
            // 配合 ALPHA_ISLAND_STATE=card + ALPHA_ISLAND_CARD_PAGE=3，且 options 里 AiEnabled=true。
            string? aiAsk = Environment.GetEnvironmentVariable("ALPHA_ISLAND_AI_ASK");
            if (!string.IsNullOrEmpty(aiAsk))
            {
                double askAfter = 4;
                if (double.TryParse(Environment.GetEnvironmentVariable("ALPHA_ISLAND_AI_ASK_DELAY"), out double customAskAfter) &&
                    customAskAfter > 0)
                {
                    askAfter = customAskAfter;
                }

                var askTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(askAfter) };
                askTimer.Tick += (_, __) =>
                {
                    askTimer!.Stop();
                    if (AiInputBox == null) return;
                    AiInputBox.Text = aiAsk;
                    IslandLog.Debug("Ai", $"调试钩子：向 AI 页发送「{aiAsk}」");
                    SendAiFromInput();
                };
                askTimer.Start();
            }

            // 调试：ALPHA_ISLAND_AGENT_FAKE=thinking|streaming|toolwait|error|idle —— 直接往
            // Core\AgentStatus.cs 写一个假状态（不调模型），用来核对细条色点与 agent 胶囊；
            // 再加 ALPHA_ISLAND_AGENT_CYCLE=<秒> 则每 N 秒按 思考→输出→等待授权→出错→空闲 循环一次
            // （用来验证"进入/退出占用"那一拍音乐图层是否让位并让回来）。
            string? fakeAgent = Environment.GetEnvironmentVariable("ALPHA_ISLAND_AGENT_FAKE");
            if (!string.IsNullOrEmpty(fakeAgent))
            {
                AgentStatus.Set(ParseFakeAgentState(fakeAgent), "调试注入");
                IslandLog.Debug("AgentStatus", $"调试注入：agent 状态 = {AgentStatus.Describe()}");

                string? cycleAfter = Environment.GetEnvironmentVariable("ALPHA_ISLAND_AGENT_CYCLE");
                if (double.TryParse(cycleAfter, out double cycleSeconds) && cycleSeconds > 0)
                {
                    var states = new[]
                    {
                        AgentRunState.Thinking, AgentRunState.Streaming, AgentRunState.ToolWait,
                        AgentRunState.Error, AgentRunState.Idle
                    };
                    int cursor = Array.IndexOf(states, AgentStatus.State);
                    if (cursor < 0) cursor = 0;

                    var cycleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(cycleSeconds) };
                    cycleTimer.Tick += (_, __) =>
                    {
                        cursor = (cursor + 1) % states.Length;
                        if (states[cursor] == AgentRunState.Idle) AgentStatus.Reset("调试注入");
                        else AgentStatus.Set(states[cursor], "调试注入");
                        IslandLog.Debug("AgentStatus", $"调试注入：agent 状态 = {AgentStatus.Describe()}");
                    };
                    cycleTimer.Start();
                }
            }

            // 调试：ALPHA_ISLAND_AGENT_APPROVAL_FAKE=allow|deny（+ _DELAY=<秒>，默认 5）——
            // 审批界面弹出后自动点一次，用来在没人值守的环境里验证"弹出→裁决→回传→收尾"整条链；
            // ALPHA_ISLAND_AGENT_APPROVAL_DEMO=1 —— 本地注入一条假审批请求（不需要真的起外部 agent）。
            string? fakeApproval = Environment.GetEnvironmentVariable("ALPHA_ISLAND_AGENT_APPROVAL_FAKE");
            if (!string.IsNullOrEmpty(fakeApproval))
            {
                double.TryParse(Environment.GetEnvironmentVariable("ALPHA_ISLAND_AGENT_APPROVAL_FAKE_DELAY"), out double approvalDelay);
                ConfigureApprovalFake(fakeApproval, approvalDelay);
                IslandLog.Debug("AgentBridge",
                    $"调试钩子：审批自动裁决 = {fakeApproval}（延迟 {(approvalDelay > 0 ? approvalDelay : 5):0.#}s）");
            }

            if (Environment.GetEnvironmentVariable("ALPHA_ISLAND_AGENT_APPROVAL_DEMO") == "1")
            {
                var demoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                demoTimer.Tick += (_, __) =>
                {
                    demoTimer.Stop();
                    InjectApprovalDemo();
                };
                demoTimer.Start();
            }

            // 调试：ALPHA_ISLAND_FAKE_RENDER_TIER=0|1|2 —— 在首次探测前把渲染层级写死，
            // 用来在硬件机器上验证「Tier < 2 就拒绝 3D 封面」那条分支（顺带也会让形变降级）。
            string? fakeTier = Environment.GetEnvironmentVariable("ALPHA_ISLAND_FAKE_RENDER_TIER");
            if (!string.IsNullOrEmpty(fakeTier) && int.TryParse(fakeTier, out int tierOverride))
            {
                MotionPolicy.OverrideRenderTierForDebug(tierOverride);
                IslandLog.Debug("Motion", $"调试钩子：渲染层级强制为 Tier {tierOverride}（真实硬件可能更高）");
            }

            // 调试：ALPHA_ISLAND_COVER3D_DEMO=1 —— 启动 3s / 6s 各注入一张合成封面，
            // 不用真的放音乐就能看到两次 3D 封面翻转（配合 ALPHA_ISLAND_CARD_PAGE=0 / STATE=card）。
            if (Environment.GetEnvironmentVariable("ALPHA_ISLAND_COVER3D_DEMO") == "1")
            {
                for (int i = 0; i < 2; i++)
                {
                    int index = i;
                    var coverTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3 + i * 3) };
                    coverTimer.Tick += (_, __) =>
                    {
                        coverTimer.Stop();
                        InjectCover3DDemo(index);
                    };
                    coverTimer.Start();
                }
            }

            // 调试：ALPHA_ISLAND_FAKE_MEDIA=1 —— 注入合成「正在播放」（不需要真的开播放器），
            // 用来复现「音乐胶囊 + 通知层叠层」、音乐圆球、封面交接这些只在有媒体时才走到的路径。
            if (Environment.GetEnvironmentVariable("ALPHA_ISLAND_FAKE_MEDIA") == "1")
            {
                EnableFakeMediaForDebug();
            }

            // 调试：ALPHA_ISLAND_FAKE_TM=1 —— TrafficMonitor 每日流量改用合成数据，
            // 不用装 TrafficMonitor 就能验证网速卡片提示行上的流量后缀。
            if (Environment.GetEnvironmentVariable("ALPHA_ISLAND_FAKE_TM") == "1")
            {
                EnableFakeTrafficForDebug();
            }

            // 调试：ALPHA_ISLAND_FAKE_DOWNLOAD=1 —— 下载进度改用合成任务（每拍 +8%，12 拍后完成），
            // 不用真的下东西就能验证细条提示层与「下载进度」卡片页。
            if (Environment.GetEnvironmentVariable("ALPHA_ISLAND_FAKE_DOWNLOAD") == "1")
            {
                EnableFakeDownloadForDebug();
            }

            // 调试：ALPHA_ISLAND_DL_PROFILE=<目录> —— 只把这一个目录当浏览器配置目录扫。
            // 验证脚本用临时的 Edge profile 真下一份文件，机器上真实的 Chrome/Edge 完全不受影响。
            string? downloadProfile = Environment.GetEnvironmentVariable("ALPHA_ISLAND_DL_PROFILE");
            if (!string.IsNullOrWhiteSpace(downloadProfile))
            {
                UseDownloadProfileForDebug(downloadProfile!);
            }

            // 调试：ALPHA_ISLAND_PIN=<层名>（+ _PIN_DELAY / _PIN_OFF / _PIN_SELFTEST）——
            // 到点在 UI 线程走 ③A 的置顶生产路径，详见 MainWindow.PinDebug.cs 的注释。
            StartPinDebugHook();

            // 调试：ALPHA_ISLAND_EXIT_AFTER=<秒> —— 启动 N 秒后走**正常退出路径**（ExitApplication），
            // 用来验收「有独立置顶小窗（角落占用灯）时进程还能不能干净退掉」：本机没法用托盘菜单退出
            // （前台常是全屏游戏，注入的鼠标到不了岛窗/托盘），所以给一条可脚本化的退出路径。
            double exitAfter = ParseSeconds("ALPHA_ISLAND_EXIT_AFTER", 0);
            if (exitAfter > 0)
            {
                var exitTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(exitAfter) };
                exitTimer.Tick += (_, _) =>
                {
                    exitTimer.Stop();
                    IslandLog.Debug("Debug", $"调试钩子：{exitAfter:0.#} 秒到，走正常退出路径 [exit-after]");
                    ExitApplication();
                };
                exitTimer.Start();
            }
        }

        /// <summary>把 ALPHA_ISLAND_AGENT_FAKE 的字符串翻成状态；认不出来的当空闲。</summary>
        private static AgentRunState ParseFakeAgentState(string text)
        {
            switch ((text ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "thinking":
                case "think": return AgentRunState.Thinking;
                case "streaming":
                case "stream": return AgentRunState.Streaming;
                case "toolwait":
                case "tool": return AgentRunState.ToolWait;
                case "error":
                case "err": return AgentRunState.Error;
                default: return AgentRunState.Idle;
            }
        }

        /// <summary>B 组①自检（=2）：DPAPI 密钥加密/回读往返，跑完把现场还原。</summary>
        private static void RunAiSecretSelfTest()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "alpha-island-secret-test-" + Guid.NewGuid().ToString("N") + ".dat");
            const string probe = "test-only-not-a-real-api-key";
            try
            {
                bool saved = AiSecretStore.TrySave(path, probe);
                string back = AiSecretStore.Load(path);
                byte[] raw = System.IO.File.Exists(path) ? System.IO.File.ReadAllBytes(path) : Array.Empty<byte>();
                bool leaked = System.Text.Encoding.UTF8.GetString(raw).Contains(probe, StringComparison.Ordinal);
                IslandLog.Debug("Ai", $"密钥自检：保存={saved} 回读一致={back == probe} 明文泄露={leaked}");
            }
            finally { AiSecretStore.Clear(path); }
        }

        /// <summary>B 组①自检：跑一次真实的流式对话，把首片段延迟与错误如实记进日志。</summary>
        private async Task RunAiSelfTestAsync()
        {
            ChatConfig config = ChatConfig.From(_options);
            IslandLog.Debug("Ai", $"自检：host={config.HostForLog} model={config.Model} key={(config.HasKey ? "有" : "无")}");

            var messages = new List<ChatMessage>
            {
                new(ChatMessage.RoleSystem, "你是连通性自检探针，回答尽量短。"),
                new(ChatMessage.RoleUser, "只回复两个字：收到")
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int chunks = 0, chars = 0;
            var sb = new System.Text.StringBuilder();
            try
            {
                using var chat = new ChatService();
                await foreach (string delta in chat.StreamAsync(messages, config, CancellationToken.None))
                {
                    if (chunks == 0)
                    {
                        IslandLog.Debug("Ai", $"自检：首个片段 {sw.ElapsedMilliseconds}ms");
                    }
                    chunks++;
                    chars += delta.Length;
                    sb.Append(delta);
                    if (chars > 200)
                    {
                        break;   // 自检不需要读完整个回答
                    }
                }
                IslandLog.Debug("Ai", $"自检成功：片段={chunks} 字符={chars} 用时={sw.ElapsedMilliseconds}ms 文本=\"{sb.ToString().Replace('\n', ' ')}\"");
            }
            catch (Exception ex)
            {
                IslandLog.Warn("Ai", $"自检失败（{sw.ElapsedMilliseconds}ms）：{ex.Message}");
            }
        }
        /// <summary>窗口永远水平居中、贴屏幕顶部。</summary>
        private void PositionWindow()
        {
            Rect area = SystemParameters.WorkArea;
            Left = Math.Round(area.Left + (area.Width - Width) / 2.0);
            Top = area.Top;
        }

        private void Window_Closed(object sender, EventArgs e) => ExitApplication();

    }
}
