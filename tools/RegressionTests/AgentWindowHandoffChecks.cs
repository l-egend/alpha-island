using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Services;
using alpha_island.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Image = System.Windows.Controls.Image;

internal static partial class Program
{
    private static void AgentForegroundVisibilityChecks(alpha_island.MainWindow window, Grid root)
    {
        using var destination = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Agent foreground fixture")
        { Width = 640, Height = 420, PositionX = -31000, PositionY = -31800,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        var target = new AgentAppWindow(destination.Handle, "Codex", "Fixture conversation", false);
        using var other = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Other agent fixture")
        { Width = 640, Height = 420, PositionX = -30000, PositionY = -30800,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        var antigravity = new AgentAppWindow(other.Handle, "Antigravity", "Other fixture conversation", false);
        var fixture = new AgentWindowFixture { Windows = [target, antigravity] };
        var oldNavigation = AgentField<AgentAppNavigationService>(window, "_agentNavigation");
        var options = AgentField<IslandOptions>(window, "_options");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        const string owner = "external:codex/window-visibility-fixture";
        const string otherOwner = "external:antigravity/window-visibility-fixture";
        try
        {
            SettingsRegistry.Write(options, "AgentStatusEnabled", true);
            SettingsRegistry.Write(options, "MediaWindowHandoffEnabled", true);
            SetAgentField(window, "_agentNavigation", new AgentAppNavigationService(fixture));
            AgentStatus.Set(AgentRunState.Thinking, "Codex · 思考中", owner);
            PrivateCall(window, "UpdateAgentStatusUi");
            PrivateCall(window, "SwitchMainIsland", "agentstatus");
            PrivateCall(window, "SetState", IslandState.Pill, true);
            PumpAnimations(350); root.UpdateLayout();
            Check(layers.IsActive("agentstatus"), "助手在后台时思考状态岛显示");
            fixture.Foreground = destination.Handle;
            PrivateCall(window, "UpdateAgentStatusUi");
            PumpAnimations(350); root.UpdateLayout();
            Check(!layers.IsActive("agentstatus") && window.AgentNotifyLayer.Visibility == Visibility.Collapsed
                && AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "对应助手已在前台时隐藏其思考主岛并收回空白胶囊");
            Check(AgentStatus.Owner == owner && AgentStatus.State == AgentRunState.Thinking,
                "前台隐藏只改变显示而保留正在思考状态");
            fixture.Activated = null;
            var task = (Task)PrivateCall(window, "OpenAgentWindowAsync", target)!;
            Check(task.IsCompletedSuccessfully && AgentField<alpha_island.Animation.MediaHandoffOverlay?>(window, "_agentHandoff") == null,
                "点击已前台的助手不再创建出岛动画");
            fixture.Foreground = IntPtr.Zero;
            PrivateCall(window, "UpdateAgentStatusUi");
            PumpAnimations(350); root.UpdateLayout();
            Check(layers.IsActive("agentstatus") && AgentField<IslandState>(window, "_state") == IslandState.Pill,
                "助手退到后台时恢复相应思考岛");
            layers.SetPinned("agentstatus", true);
            fixture.Foreground = destination.Handle;
            PrivateCall(window, "UpdateAgentStatusUi"); PumpAnimations(350);
            Check(layers.IsPinned("agentstatus") && !layers.IsActive("agentstatus") && !layers.AnyPinned,
                "前台临时隐藏保留助手置顶且不让隐藏的钉子撑开空岛");
            fixture.Foreground = IntPtr.Zero;
            PrivateCall(window, "UpdateAgentStatusUi"); PumpAnimations(350);
            Check(layers.IsPinned("agentstatus") && layers.IsActive("agentstatus"), "后台恢复助手原置顶");
            fixture.Foreground = destination.Handle;
            AgentStatus.Reset(owner: owner); PrivateCall(window, "UpdateAgentStatusUi"); PumpAnimations(200);
            var idleTracker = AgentField<MediaWindowService>(window, "_agentWindows");
            Check(!AgentStatus.IsBusy && !layers.IsActive("agentstatus") && !idleTracker.AutomaticSnapshotCaptureEnabled,
                "已置顶助手任务空闲后保留前台隐藏关联并停止自动截图");
            fixture.Activated = null;
            Check((bool)PrivateCall(window, "TryOpenAgentIsland", "agentstatus")! && fixture.Activated == null,
                "空闲置顶入口仍识别对应已前台的助手而不跳到其他页面");
            fixture.Foreground = IntPtr.Zero; PrivateCall(window, "UpdateAgentStatusUi"); PumpAnimations(350);
            Check(layers.IsPinned("agentstatus") && layers.IsActive("agentstatus")
                && AgentField<IslandState>(window, "_state") == IslandState.Pill,
                "已置顶空闲助手退到后台后恢复可用状态岛");
            AgentStatus.Set(AgentRunState.Thinking, "Fixture task resumed", owner);
            fixture.Foreground = destination.Handle; PrivateCall(window, "UpdateAgentStatusUi");
            SettingsRegistry.Write(options, "AgentStatusEnabled", false); PrivateCall(window, "OnAgentOptionsChanged");
            fixture.Activated = null;
            Check(!layers.IsPinned("agentstatus") && !layers.IsActive("agentstatus")
                && layers.IsPresentationSuppressed("agentstatus")
                && AgentField<MediaWindowService?>(window, "_agentWindows") == null,
                "关闭助手状态功能时取消旧置顶并保持隐藏，不被停止追踪重新露出");
            Check((bool)PrivateCall(window, "TryOpenAgentIsland", "agentstatus")! && fixture.Activated == null,
                "关闭功能后助手入口不会激活外部应用或错误打开内部 AI");
            SettingsRegistry.Write(options, "AgentStatusEnabled", true); fixture.Foreground = IntPtr.Zero;
            PrivateCall(window, "OnAgentOptionsChanged"); layers.SetPinned("agentstatus", true); PumpAnimations(350);
            Check(layers.IsActive("agentstatus") && !layers.IsPresentationSuppressed("agentstatus"),
                "再次开启助手功能撤销硬抑制并恢复后台正在运行的状态");
            PrivateCall(window, "SetState", IslandState.Card, true, "focus"); PumpAnimations(350); root.UpdateLayout();
            Check(window.SideBubble.Visibility == Visibility.Visible
                && AgentField<string?>(window, "_sideBubbleComponentKey") == "agentstatus", "助手后台通过卡片旁岛显示");
            fixture.Foreground = destination.Handle;
            PrivateCall(window, "UpdateAgentStatusUi"); PumpAnimations(350); root.UpdateLayout();
            Check(window.SideBubble.Visibility != Visibility.Visible
                || AgentField<string?>(window, "_sideBubbleComponentKey") != "agentstatus", "助手前台同时隐藏卡片旁岛");
            fixture.Foreground = IntPtr.Zero;
            PrivateCall(window, "UpdateAgentStatusUi");
            layers.ShowSeconds("system", 30);
            PrivateCall(window, "SwitchMainIsland", "system"); PrivateCall(window, "SetState", IslandState.Pill, true);
            PumpAnimations(350); root.UpdateLayout();
            var satellites = AgentField<Dictionary<string, Border>>(window, "_satelliteOrbViews");
            Check(satellites.TryGetValue("agentstatus", out var satellite) && satellite.Visibility == Visibility.Visible,
                "另一通知作为主岛时助手后台显示卫星岛");
            fixture.Foreground = destination.Handle;
            PrivateCall(window, "UpdateAgentStatusUi"); PumpAnimations(350); root.UpdateLayout();
            Check(satellite!.Visibility != Visibility.Visible && layers.IsActive("system"),
                "前台隐藏助手卫星并保留其他通知岛");
            AgentStatus.Set(AgentRunState.Streaming, "Other agent working", otherOwner);
            PrivateCall(window, "UpdateAgentStatusUi"); PumpAnimations(350);
            string otherKey = (string)PrivateCall(window, "AgentSessionKey", otherOwner)!;
            Check(AgentStatus.Owner == otherOwner && layers.IsActive(otherKey) && !layers.IsActive("agentstatus"),
                "Codex 前台不隐藏后台的 Antigravity 状态");
            fixture.Foreground = other.Handle;
            PrivateCall(window, "UpdateAgentStatusUi"); PumpAnimations(350);
            Check(!layers.IsActive(otherKey) && layers.IsActive("agentstatus") && AgentStatus.Owner == otherOwner,
                "只隐藏当前对应前台的 Antigravity 状态");
            layers.SetPinned("agentstatus", false);
            AgentStatus.Reset(owner: otherOwner); AgentStatus.Reset(owner: owner);
            PrivateCall(window, "UpdateAgentStatusUi"); PumpAnimations(200);
            Check(!layers.IsPresentationSuppressed("agentstatus") && AgentField<MediaWindowService?>(window, "_agentWindows") == null,
                "助手关闭或任务终止后撤销显示抑制并销毁窗口追踪");
            Check(NativeMethods.GetProp(destination.Handle, DwmTransitionLease.OriginalProperty) == IntPtr.Zero
                && NativeMethods.GetProp(other.Handle, DwmTransitionLease.OriginalProperty) == IntPtr.Zero,
                "助手停止追踪释放所有目标窗口的原生动画抑制");
        }
        finally
        {
            PrivateCall(window, "CancelAgentHandoff");
            AgentStatus.Reset(owner: owner);
            AgentStatus.Reset(owner: otherOwner);
            PrivateCall(window, "StopAgentWindowTracking");
            SetAgentField(window, "_agentNavigation", oldNavigation);
            layers.HideAll();
            PrivateCall(window, "SetState", IslandState.Bar, true);
        }
        AgentWindowMotionChecks(window, root);
        AgentOnDemandCaptureChecks(window);
        AgentBoundedCaptureChecks(window);
    }

    private sealed class AgentNativeWindowFixture(AgentAppWindow target) : IAgentWindowAccess
    {
        internal IntPtr CurrentForeground;
        internal int Activations;
        public IntPtr Foreground => CurrentForeground;
        public IReadOnlyList<AgentAppWindow> ReadWindows() => [target];
        public bool Activate(AgentAppWindow window)
        {
            Activations++;
            new AgentAppNavigationService.NativeAgentWindowAccess().Activate(window);
            CurrentForeground = window.Handle;
            return true;
        }
    }

    private static void AgentWindowMotionChecks(alpha_island.MainWindow window, Grid root)
    {
        using var destination = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Agent native motion fixture")
        { Width = 640, Height = 420, PositionX = -31000, PositionY = -31800,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        var target = new AgentAppWindow(destination.Handle, "Codex", "Fixture conversation", false);
        var fixture = new AgentNativeWindowFixture(target);
        var oldNavigation = AgentField<AgentAppNavigationService>(window, "_agentNavigation");
        var oldContext = SynchronizationContext.Current;
        bool reduced = alpha_island.Animation.MotionPolicy.ReduceMotion, powerSave = alpha_island.Animation.MotionPolicy.PowerSave;
        const string owner = "external:codex/native-window-motion-fixture";
        IntPtr previousForeground = NativeMethods.GetForegroundWindow();
        MediaWindowService? tracker = null;
        void WaitUntil(Func<bool> ready, int timeout = 1000)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!ready() && watch.ElapsedMilliseconds < timeout) PumpAnimations(10);
        }
        try
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
            alpha_island.Animation.MotionPolicy.Apply(false, false);
            SetAgentField(window, "_agentNavigation", new AgentAppNavigationService(fixture));
            AgentStatus.Set(AgentRunState.Thinking, "Fixture task", owner);
            PrivateCall(window, "UpdateAgentStatusUi");
            tracker = AgentField<MediaWindowService>(window, "_agentWindows");
            Check(!tracker.TransitionsSuppressed && NativeMethods.GetProp(destination.Handle, DwmTransitionLease.OriginalProperty) == IntPtr.Zero,
                "首次真实截图尚未就绪时保留目标窗口的系统原生动画");
            Check(!new AgentAppNavigationService.NativeAgentWindowAccess().Activate(target with { ProcessId = uint.MaxValue }),
                "导航拒绝 HWND 相同但保存进程身份不符的目标");
            uint process = (uint)typeof(MediaWindowService).GetField("_explicitProcess", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(tracker)!;
            typeof(MediaWindowService).GetField("_explicitProcess", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(tracker, uint.MaxValue);
            Check(!tracker.IsTrackedWindowValid(target.Handle) && !tracker.IsRestoredVisible(target.Handle),
                "联动与还原确认均拒绝复用到其他进程的窗口句柄");
            typeof(MediaWindowService).GetField("_explicitProcess", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(tracker, process);
            var image = AgentFixtureWindowSnapshot();
            typeof(MediaWindowService).GetProperty("Snapshot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(tracker, image);
            WatcherCall(tracker, "SuppressTransitions");
            Check(tracker.TransitionsSuppressed && NativeMethods.GetProp(destination.Handle, DwmTransitionLease.OriginalProperty) != IntPtr.Zero,
                "助手最小化之前已抑制该窗口的 Windows 原生过渡");
            Check((int)typeof(MediaWindowService).GetField("_captureIntervalMilliseconds", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(tracker)! == 2000,
                "助手使用两秒截图缓存而不增加媒体的常驻截图频率");
            fixture.CurrentForeground = destination.Handle;
            PrivateCall(window, "UpdateAgentStatusUi");
            NativeMethods.ShowWindowAsync(destination.Handle, 6);
            WaitUntil(() => AgentField<alpha_island.Animation.MediaHandoffOverlay?>(window, "_agentHandoff") != null, 450);
            var overlay = AgentField<alpha_island.Animation.MediaHandoffOverlay?>(window, "_agentHandoff");
            Check(NativeMethods.IsIconic(destination.Handle) && overlay != null,
                "目标真实最小化的 WinEvent 触发助手吸入动画");
            var proxy = (Image)typeof(alpha_island.Animation.MediaHandoffOverlay).GetField("_windowImage",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(overlay)!;
            Check(ReferenceEquals(proxy.Source, image) && tracker.SnapshotCaptureSuspended,
                "吸入使用缓存的窗口截图且交接期间停止抓屏避免拍入代理自身");
            WaitUntil(() => AgentField<alpha_island.Animation.MediaHandoffOverlay?>(window, "_agentHandoff") == null);
            Check(AgentField<alpha_island.Animation.MediaHandoffOverlay?>(window, "_agentHandoff") == null
                && !tracker.SnapshotCaptureSuspended && AgentField<IslandState>(window, "_state") == IslandState.Pill,
                "助手吸入完成后交接真实状态岛并释放逐帧动画");
            fixture.CurrentForeground = IntPtr.Zero;
            var task = (Task)PrivateCall(window, "OpenAgentWindowAsync", target)!;
            Check(!task.IsCompleted && AgentField<alpha_island.Animation.MediaHandoffOverlay?>(window, "_agentHandoff") != null,
                "点击助手岛用同一窗口缓存创建真实出岛还原动画");
            WaitUntil(() => task.IsCompleted);
            Check(task.IsCompletedSuccessfully && fixture.Activations > 0 && !NativeMethods.IsIconic(destination.Handle),
                "已缓存 Minimized=false 的窗口也按当前真实状态还原");
            Check(AgentField<alpha_island.Animation.MediaHandoffOverlay?>(window, "_agentHandoff") == null,
                "助手还原完成后移除短时代理窗口");
            AgentStatus.Reset(owner: owner); PrivateCall(window, "UpdateAgentStatusUi");
            Check(tracker.Snapshot == null && !tracker.TransitionsSuppressed
                && NativeMethods.GetProp(destination.Handle, DwmTransitionLease.OriginalProperty) == IntPtr.Zero,
                "助手任务终止及时释放截图和该目标窗口的动画抑制");
        }
        finally
        {
            AgentStatus.Reset(owner: owner); PrivateCall(window, "StopAgentWindowTracking");
            SetAgentField(window, "_agentNavigation", oldNavigation);
            SynchronizationContext.SetSynchronizationContext(oldContext);
            alpha_island.Animation.MotionPolicy.Apply(reduced, powerSave);
            NativeMethods.ShowWindowAsync(destination.Handle, NativeMethods.SW_RESTORE);
            if (previousForeground != IntPtr.Zero) NativeMethods.SetForegroundWindow(previousForeground);
            AgentField<IslandLayerHost>(window, "_layers").HideAll();
            PrivateCall(window, "SetState", IslandState.Bar, true);
        }
    }

    private static BitmapSource AgentFixtureWindowSnapshot()
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(System.Windows.Media.Brushes.CornflowerBlue, null, new Rect(0, 0, 320, 180));
            drawing.DrawRoundedRectangle(System.Windows.Media.Brushes.White, null, new Rect(40, 60, 240, 18), 5, 5);
            drawing.DrawRoundedRectangle(System.Windows.Media.Brushes.LightGray, null, new Rect(40, 96, 170, 12), 4, 4);
        }
        var image = new RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); image.Freeze(); return image;
    }

    private static void AgentOnDemandCaptureChecks(alpha_island.MainWindow window)
    {
        using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Owned capture fixture")
        { Width = 320, Height = 180, PositionX = -30000, PositionY = -31000,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        source.RootVisual = new Grid { Background = System.Windows.Media.Brushes.CornflowerBlue };
        int prints = 0;
        source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (message == 0x0317)
            {
                using var drawing = System.Drawing.Graphics.FromHdc(wParam);
                drawing.Clear(System.Drawing.Color.CornflowerBlue);
                prints++; handled = true;
            }
            return IntPtr.Zero; // WM_PRINT may draw successfully while its message result remains zero.
        });
        using var tracker = new MediaWindowService(window.Dispatcher, 2000, 500) { RequireSnapshotForTransitions = true };
        tracker.SetTrackedWindow(new AgentAppWindow(source.Handle, "Codex", "Owned capture", false));
        Check(!tracker.TransitionsSuppressed && tracker.Snapshot == null,
            "首次后台调用未获取真实画面前不禁用系统原生动画");
        var context = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
            var capture = tracker.CaptureWindowSnapshotAsync();
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (!capture.IsCompleted && wait.ElapsedMilliseconds < 1000) PumpAnimations(10);
            Check(capture.IsCompletedSuccessfully && capture.Result != null && prints == 1,
                "首次后台调用通过窗口自身绘制取得真实画面，WM_PRINT 返回零也不误判失败");
            Check(tracker.Snapshot == capture.Result && tracker.TransitionsSuppressed,
                "按需首帧进入缓存后才获取该目标原生过渡抑制");
        }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
        tracker.Dispose();
        Check(NativeMethods.GetProp(source.Handle, DwmTransitionLease.OriginalProperty) == IntPtr.Zero,
            "按需抓屏追踪退出后释放截图与原生过渡抑制");
    }

    private static void AgentBoundedCaptureChecks(alpha_island.MainWindow window)
    {
        using var ready = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        System.Windows.Threading.Dispatcher? dispatcher = null;
        IntPtr handle = IntPtr.Zero;
        var worker = new Thread(() =>
        {
            dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Hung capture fixture")
            { Width = 320, Height = 180, PositionX = -30000, PositionY = -31000,
                WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
            source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message == 0x0317) { entered.Set(); Thread.Sleep(1200); handled = true; }
                return IntPtr.Zero;
            });
            handle = source.Handle; ready.Set(); System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        try
        {
            Check(ready.Wait(2000), "限时抓屏测试使用独立窗口线程");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var capture = WindowSnapshotCapture.CaptureAsync(handle, new Rect(-30000, -31000, 320, 180));
            Check(capture.Wait(900) && capture.Result == null && watch.ElapsedMilliseconds < 900 && entered.IsSet,
                "挂起目标的 WM_PRINT 在有限时长结束并安全降级，不遗留无限抓屏 worker");
            var pending = typeof(WindowSnapshotCapture).GetField("Pending", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
            Check(SpinWait.SpinUntil(() => (int)pending.GetType().GetProperty("Count")!.GetValue(pending)! == 0, 500),
                "限时抓屏完成后移除缓存任务并释放本次截图资源");
        }
        finally
        {
            dispatcher?.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            worker.Join(2200);
        }
    }
}
