using System;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;
using Point = System.Windows.Point;

internal static partial class Program
{
    private static void MediaMinimizeEntryChecks(alpha_island.MainWindow window, System.Windows.Controls.Grid root,
        SmtcService media, IntPtr fixtureHandle, string source)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        bool oldReduce = MotionPolicy.ReduceMotion, oldPower = MotionPolicy.PowerSave;
        int oldTier = MotionPolicy.RenderTier;
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        using var watcher = new MediaWindowService(window.Dispatcher);
        SetWatcherField(watcher, "_window", fixtureHandle);
        SetWatcherField(watcher, "_kind", MediaWindowKind.DesktopPlayer);
        SetWatcherField(watcher, "_nextResolve", long.MaxValue);
        WatcherCall(watcher, "SuppressTransitions");
        ((IslandTimer)typeof(MediaWindowService).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(watcher)!).Stop();
        var origin = root.PointToScreen(new Point(200, 120));
        var state = new MediaWindowState(fixtureHandle, MediaWindowKind.DesktopPlayer, new Rect(origin.X, origin.Y, 800, 600), false, true);
        typeof(MediaWindowService).GetProperty("Current", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(watcher, state);
        void FinishHandoff()
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            // Include compositor commits around the animation while keeping a bounded timeout.
            while (AgentField<bool>(window, "_mediaHandoffInFlight")
                && elapsed.ElapsedMilliseconds < MediaHandoffOverlay.DurationMilliseconds + 600)
                PumpAnimations(10);
            var pending = AgentField<MediaHandoffOverlay?>(window, "_mediaHandoff");
            Check(!AgentField<bool>(window, "_mediaHandoffInFlight"), $"媒体动画在有界等待内完成交接 (progress={pending?.Progress}, source={((pending == null) ? null : AgentField<Task?>(pending, "_sourceCommit"))?.Status}, destination={((pending == null) ? null : AgentField<Task?>(pending, "_destinationCommit"))?.Status})");
        }
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            SettingsRegistry.Write(options, "MediaWindowHandoffEnabled", true);
            SettingsRegistry.Write(options, "MediaHideWhenPaused", true);
            SetAgentField(window, "_mediaWindows", watcher); SetAgentField(window, "_isPinned", false);
            layers.HideAll();
            media.SetDebugNowPlaying(new NowPlayingInfo { SourceApp = source, Title = "暂停最小化回归", State = PlaybackState.Paused });
            PrivateCall(window, "SetState", IslandState.Bar, true);
            SetAgentField(window, "_mediaSourceForeground", true);
            PrivateCall(window, "OnMediaWindowMinimizing", state);
            Check(AgentField<MediaHandoffOverlay?>(window, "_mediaHandoff") != null && AgentField<bool>(window, "_mediaHandoffInFlight"),
                "开启暂停隐藏后最小化仍进入真实吸入动画入口");
            Check(watcher.SnapshotCaptureSuspended, "动画交接期间暂停屏幕快照，不能把自己的过渡窗口录入下次动画");
            FinishHandoff();
            Check(AgentField<MediaHandoffOverlay?>(window, "_mediaHandoff") == null && !AgentField<bool>(window, "_mediaHandoffInFlight")
                && AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "暂停吸入完成后保留暂停隐藏并释放动画窗口");
            Check(!watcher.SnapshotCaptureSuspended, "交接完成后恢复媒体窗口快照采集");
            var capturedFrame = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            capturedFrame.Freeze();
            long capturedEpoch = (long)typeof(MediaWindowService).GetField("_captureEpoch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(watcher)!;
            long sourceEpoch = (long)typeof(MediaWindowService).GetField("_epoch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(watcher)!;
            watcher.SnapshotCaptureSuspended = true; watcher.SnapshotCaptureSuspended = false;
            WatcherCall(watcher, "PublishSnapshot", sourceEpoch, capturedEpoch, fixtureHandle, capturedFrame);
            Check(watcher.Snapshot == null, "暂停又恢复采集后，迟到的旧快照仍不能回填缓存");
            capturedEpoch = (long)typeof(MediaWindowService).GetField("_captureEpoch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(watcher)!;
            WatcherCall(watcher, "PublishSnapshot", sourceEpoch, capturedEpoch, fixtureHandle, capturedFrame);
            Check(watcher.Snapshot == capturedFrame, "交接结束后的新快照可以正常更新缓存");

            SettingsRegistry.Write(options, "MediaHideWhenPaused", false);
            media.SetDebugNowPlaying(new NowPlayingInfo { SourceApp = source, Title = "末帧交接回归", State = PlaybackState.Playing });
            PrivateCall(window, "SetState", IslandState.Bar, true);
            SetAgentField(window, "_mediaSourceForeground", true);
            PrivateCall(window, "OnMediaWindowMinimizing", state);
            bool readyBehindProxy = false;
            var terminalSizes = new List<(double Width, double Height)>();
            EventHandler landingSample = (_, _) =>
            {
                if (!AgentField<bool>(window, "_mediaHandoffLanding")) return;
                readyBehindProxy |= AgentField<bool>(window, "_mediaHandoffInFlight")
                    && AgentField<IslandState>(window, "_state") == IslandState.Pill
                    && window.PillLayer.Visibility == Visibility.Visible && window.PillLayer.Opacity > .95;
                terminalSizes.Add((window.IslandBody.Width, window.IslandBody.Height));
            };
            System.Windows.Media.CompositionTarget.Rendering += landingSample;
            try { FinishHandoff(); }
            finally { System.Windows.Media.CompositionTarget.Rendering -= landingSample; }
            Check(readyBehindProxy && terminalSizes.Count > 0 && terminalSizes.All(size => Math.Abs(size.Height - IslandMetrics.PillHeight) < .01)
                && !AgentField<bool>(window, "_mediaHandoffLanding"),
                "快照退场前真实媒体岛已完整显示，交接时不会退回细条");
            double settledWidth = window.IslandBody.Width;
            PumpAnimations(100);
            Check(Math.Abs(window.IslandBody.Width - settledWidth) < .01 && Math.Abs(window.IslandBody.Height - IslandMetrics.PillHeight) < .01,
                "吸入结束后不再启动第二次展开弹簧");

            media.SetDebugNowPlaying(new NowPlayingInfo { SourceApp = source, Title = "通知共存回归", State = PlaybackState.Playing });
            layers.ShowSeconds("netspeed", 30); PrivateCall(window, "SetLayerPinned", "netspeed", true);
            PrivateCall(window, "SetState", IslandState.Pill, true);
            SetAgentField(window, "_mediaSourceForeground", true);
            PrivateCall(window, "OnMediaWindowMinimizing", state);
            Check(AgentField<MediaHandoffOverlay?>(window, "_mediaHandoff") != null,
                "普通通知和置顶小岛并存时仍播放最小化吸入");
            FinishHandoff();
            Check(layers.IsPinned("netspeed") && AgentField<MediaHandoffOverlay?>(window, "_mediaHandoff") == null,
                "吸入完成保留其他小岛的置顶与通知");
            PrivateCall(window, "SelectComponent", "focus"); root.UpdateLayout();
            PrivateCall(window, "OnMediaWindowMinimizing", state);
            Check(AgentField<MediaHandoffOverlay?>(window, "_mediaHandoff") != null,
                "另一张卡片展开时最小化同样有吸入动画");
            FinishHandoff();
            Check(AgentField<IslandPageHost>(window, "_pages").ActiveId == "focus"
                && AgentField<IslandState>(window, "_state") == IslandState.Card,
                "吸入动画不会关闭或替换正在查看的其他卡片");
        }
        finally
        {
            PrivateCall(window, "CancelMediaHandoff");
            SetAgentField(window, "_mediaWindows", null); SetAgentField(window, "_mediaSourceForeground", false);
            PrivateCall(window, "SetLayerPinned", "netspeed", false); layers.HideAll();
            SettingsRegistry.Write(options, "MediaHideWhenPaused", false);
            MotionPolicy.OverrideRenderTierForDebug(oldTier); MotionPolicy.Apply(oldReduce, oldPower);
            SynchronizationContext.SetSynchronizationContext(oldContext);
        }
    }

    private static void MediaWindowMinimizeEventChecks(Dispatcher dispatcher, IntPtr fixtureHandle)
    {
        using var watcher = new MediaWindowService(dispatcher);
        SetWatcherField(watcher, "_window", fixtureHandle);
        SetWatcherField(watcher, "_kind", MediaWindowKind.DesktopPlayer);
        SetWatcherField(watcher, "_nextResolve", long.MaxValue);
        WatcherCall(watcher, "SuppressTransitions");
        WatcherCall(watcher, "Publish");
        int count = 0;
        var order = new List<string>();
        watcher.Minimizing += state => { count++; order.Add("minimize"); };
        watcher.Changed += state => { if (state.Minimized) order.Add("background"); };
        try
        {
            WatcherCall(watcher, "OnWinEvent", IntPtr.Zero, NativeMethods.EVENT_SYSTEM_MINIMIZESTART, fixtureHandle, 0, 0, (uint)0, (uint)0);
            PumpAnimations(60);
            Check(count == 0 && !watcher.Current.Minimized,
                "提前到达的最小化开始事件不会在窗口仍展开时创建再取消动画");
            NativeMethods.ShowWindowAsync(fixtureHandle, 6); // Only this regression fixture's own window.
            PumpAnimations(260);
            Check(count == 1 && watcher.Current.Minimized && !watcher.Current.Foreground,
                "真实最小化状态触发一次吸入且不保留前台标记");
            Check(!watcher.IsRestoredVisible(fixtureHandle), "窗口仍最小化时不能撤掉还原快照");
            Check(order.Count >= 2 && order[0] == "minimize" && order[1] == "background",
                "先启动吸入再通知退到后台，避免岛提前展开");
            WatcherCall(watcher, "Publish");
            Check(count == 1, "重复窗口状态采样不重播最小化动画");
            NativeMethods.ShowWindowAsync(fixtureHandle, NativeMethods.SW_RESTORE);
            PumpAnimations(260);
            Check(watcher.IsRestoredVisible(fixtureHandle), "窗口实际显示后才允许快照交接");
            NativeMethods.ShowWindowAsync(fixtureHandle, 6);
            PumpAnimations(260);
            Check(count == 2, "还原后再次最小化可重新触发吸入");
        }
        finally
        {
            NativeMethods.ShowWindowAsync(fixtureHandle, NativeMethods.SW_RESTORE);
            PumpAnimations(80);
        }
    }

    private static void SetWatcherField(MediaWindowService watcher, string name, object value) =>
        typeof(MediaWindowService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(watcher, value);
    private static void WatcherCall(MediaWindowService watcher, string name, params object[] args) =>
        typeof(MediaWindowService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(watcher, args);
}
