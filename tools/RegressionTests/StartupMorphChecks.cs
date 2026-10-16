using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Animation;
using alpha_island.Core;

internal static partial class Program
{
    private static void StartupMorphUiChecks(alpha_island.MainWindow window, Grid root)
    {
        int previousTier = MotionPolicy.RenderTier;
        bool previousReduce = MotionPolicy.ReduceMotion, previousPowerSave = MotionPolicy.PowerSave;
        var options = AgentField<IslandOptions>(window, "_options");
        bool greetingEnabled = options.GreetingEnabled;
        double greetingSeconds = options.GreetingSeconds;
        var animator = AgentField<IslandAnimator>(window, "_animator");
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            options.GreetingEnabled = true; options.GreetingSeconds = 20;
            PrivateCall(window, "ClearDesktopInbox");
            AgentField<IslandLayerHost>(window, "_layers").HideAll();
            PrivateCall(window, "SetState", IslandState.Bar, true);
            animator.SetImmediate(new IslandGeometry(IslandMetrics.BarWidth, IslandMetrics.BarHeight,
                IslandMetrics.BarTop, IslandMetrics.BarCorner));
            PrivateCall(window, "ShowStartupGreeting");
            Check(animator.IsRunning && window.IslandBody.Height < IslandMetrics.PillHeight,
                "正常动效设置的启动问候从细条开始形变，不直出完整岛体");
            // Replay four delayed compositor frames through the real animator callback.
            // This is the startup stall seen in the user's Motion warning, not a fake tier override.
            var lastTick = typeof(IslandAnimator).GetField("_lastTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var rendering = typeof(IslandAnimator).GetMethod("OnRendering", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (int i = 0; i < 4; i++)
            {
                lastTick.SetValue(animator, (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency - .1);
                rendering.Invoke(animator, new object?[] { null, EventArgs.Empty });
            }
            Check(!animator.IsRunning && Math.Abs(window.IslandBody.Height - IslandMetrics.PillHeight) < .01,
                "启动一时掉帧时完成当前形变并停止帧循环，不卡在半展开");
            Check(MotionPolicy.RenderTier == 2 && !MotionPolicy.InstantMorph,
                "启动的一时慢帧不把硬件渲染永久写成软件渲染，后续展开仍保留动画");
            PrivateCall(window, "DismissGreeting");
            animator.SetImmediate(new IslandGeometry(IslandMetrics.BarWidth, IslandMetrics.BarHeight,
                IslandMetrics.BarTop, IslandMetrics.BarCorner));
            PrivateCall(window, "SetState", IslandState.Bar, true);
            PrivateCall(window, "ShowStartupGreeting");
            Check(animator.IsRunning, "服务加载结束后再次展开问候仍启动形变动画");
            PrivateCall(window, "DismissGreeting"); animator.Stop();
            MotionPolicy.Apply(true, false);
            PrivateCall(window, "SetState", IslandState.Bar, true);
            PrivateCall(window, "ShowStartupGreeting");
            Check(!animator.IsRunning && window.IslandBody.Height == IslandMetrics.PillHeight,
                "用户主动开启减弱动效时仍尊重瞬变选择");
            PrivateCall(window, "DismissGreeting"); animator.Stop();
            MotionPolicy.Apply(false, false); MotionPolicy.OverrideRenderTierForDebug(0);
            PrivateCall(window, "SetState", IslandState.Bar, true);
            PrivateCall(window, "ShowStartupGreeting");
            Check(!animator.IsRunning && window.IslandBody.Height == IslandMetrics.PillHeight,
                "真实软件渲染层级仍立即展开，不强行运行逐帧动画");
        }
        finally
        {
            PrivateCall(window, "DismissGreeting"); animator.Stop();
            options.GreetingEnabled = greetingEnabled; options.GreetingSeconds = greetingSeconds;
            MotionPolicy.OverrideRenderTierForDebug(previousTier);
            MotionPolicy.Apply(previousReduce, previousPowerSave);
            PrivateCall(window, "SetState", IslandState.Bar, true);
        }
    }
}
