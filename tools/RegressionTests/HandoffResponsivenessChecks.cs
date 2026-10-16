using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using alpha_island.Animation;
using alpha_island.Services;

internal static partial class Program
{
    private static void HandoffResponsivenessChecks(alpha_island.MainWindow window, Grid root)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        var image = AgentFixtureWindowSnapshot();
        var warmup = new MediaHandoffOverlay(new Rect(200, 120, 600, 400), new Rect(740, 12, 344, 48), image, image, "Warmup", false);
        var warmed = warmup.PlayAsync();
        while (!warmed.IsCompleted) PumpAnimations(10);
        warmup.Close();
        bool busy = true;
        void BackgroundWork() { if (busy) { Thread.Sleep(1); window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(BackgroundWork)); } }
        window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(BackgroundWork));
        var watch = Stopwatch.StartNew();
        double started = -1, arrived = -1;
        var overlay = new MediaHandoffOverlay(new Rect(200, 120, 600, 400), new Rect(740, 12, 344, 48),
            image, image, "Responsive handoff fixture", opening: false,
            sourceCovered: () => started = watch.Elapsed.TotalMilliseconds,
            reveal: () => arrived = watch.Elapsed.TotalMilliseconds);
        try
        {
            watch.Restart();
            var playing = overlay.PlayAsync();
            double prepared = watch.Elapsed.TotalMilliseconds;
            Console.WriteLine($"TIMING: prepare={prepared:F1}ms");
            while (!playing.IsCompleted && watch.ElapsedMilliseconds < 1200) PumpAnimations(10);
            double ended = watch.Elapsed.TotalMilliseconds;
            Console.WriteLine($"TIMING: first={started:F1}ms motion={arrived - started:F1}ms tail={ended - arrived:F1}ms total={ended:F1}ms");
            Check(playing.IsCompletedSuccessfully && playing.Result, "助手和播放器共用交接在繁忙 Dispatcher 下正常完成");
            Check(started >= 0 && started - prepared < 100, "动画首帧不等待低优先级空闲队列的百毫秒超时");
            Check(arrived > started && ended - arrived < 130, "动画到达后不再等待低优先级空闲队列再退出");
            var previous = AgentField<MediaWindowService?>(window, "_mediaWindows");
            using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Media cache miss fixture")
            { Width = 640, Height = 420, PositionX = -31000, PositionY = -31000,
                WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
            using var service = new MediaWindowService(window.Dispatcher);
            service.SetTrackedWindow(new AgentAppWindow(source.Handle, "Fixture", "Media", false));
            try
            {
                SetAgentField(window, "_mediaWindows", service);
                var restore = (Task)PrivateCall(window, "OpenMediaWindowAsync")!;
                Check(restore.IsCompletedSuccessfully && AgentField<MediaHandoffOverlay?>(window, "_mediaHandoff") == null
                    && !service.TransitionsSuppressed, "播放器缺少真实缓存时立即使用原生还原，不播放空白代理或等待抓图");
            }
            finally { PrivateCall(window, "CancelMediaHandoff"); SetAgentField(window, "_mediaWindows", previous); }
        }
        finally { busy = false; overlay.Close(); SynchronizationContext.SetSynchronizationContext(context); }
    }
}
