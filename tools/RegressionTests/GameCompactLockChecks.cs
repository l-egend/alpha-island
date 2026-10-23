using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void GameCompactLockChecks(alpha_island.MainWindow window, Grid root, string testRoot)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var animator = AgentField<IslandAnimator>(window, "_animator");
        using var hotkeys = new HotkeyService(window.Dispatcher);
        using var clipboard = new ClipboardService(window.Dispatcher, hotkeys, () => 1u, () => (string?)null,
            System.IO.Path.Combine(testRoot, "game-clipboard.json"));
        clipboard.ContentCopied += (sender, args) => PrivateCall(window, "OnClipboardContentCopied", sender, args);
        clipboard.CopiedDismissed += (sender, args) => PrivateCall(window, "OnClipboardCopiedDismissed", sender, args);
        SetAgentField(window, "_clipboard", clipboard);
        bool reduce = MotionPolicy.ReduceMotion, power = MotionPolicy.PowerSave;
        long revision = 0;
        void Game(bool active) => PrivateCall(window, "ApplyGameActivity",
            new GameActivityState(++revision, active, active ? "游戏交互回归" : "", "用户指定", active ? 1 : 0));
        void Hover() => PrivateCall(window, "IslandBody_MouseEnter", window.IslandBody,
            new System.Windows.Input.MouseEventArgs(Mouse.PrimaryDevice, 0));
        void Click() => PrivateCall(window, "IslandBody_MouseLeftButtonDown", window.IslandBody,
            new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
        try
        {
            SettingsRegistry.Write(options, "NotificationGameQuietEnabled", true);
            SettingsRegistry.Write(options, "AssistantGameWrapEnabled", false);
            SettingsRegistry.Write(options, "LastOpenedCardPage", IslandPageIds.System);
            options.ClipboardNotifyEnabled = true;
            options.ClipboardRightClickPaste = true;
            clipboard.ProcessClipboardData(new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText, "进入游戏前复制"), false);
            Check(AgentField<bool>(window, "_clipboardLocked"), "真实剪贴板服务建立未粘贴的驻留状态");
            Game(true);
            Check(layers.IsActive("gamequiet") && AgentField<IslandState>(window, "_state") == IslandState.Pill,
                "检测到游戏时先显示免打扰提示，再进入最小态");
            Hover(); Click(); PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.System);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill && !layers.IsActive("clipboard"),
                "进入提示期间也不能展开其他内容");
            PumpAnimations(2200);
            Check(!layers.IsActive("gamequiet") && AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "游戏进入提示结束后自动锁定最小态");
            Check(!(bool)PrivateCall(window, "CanUseClipboardQuickPaste")! && !AgentField<bool>(window, "_clipboardLocked"),
                "进入游戏释放复制驻留和鼠标拦截，不等待游戏内可能缺失的粘贴消息");
            Game(false); layers.HideAll();
            PrivateCall(window, "SetState", IslandState.Bar, true);
            Hover(); Click();
            Check(!AgentField<bool>(window, "_clipboardLocked") && AgentField<IslandState>(window, "_state") == IslandState.Card,
                "游戏中粘贴不遗留剪贴板交互锁，退出游戏后悬停和点击均能恢复展开");
            MotionPolicy.Apply(false, false);
            layers.HideAll();
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.System);
            // No dispatcher/render frames between opening the card and entering a game:
            // exclusive fullscreen can suspend WPF rendering at exactly this point.
            Game(true);
            PrivateCall(window, "FinishGameQuietIntro");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar && animator.IsRunning,
                "游戏进入立即锁定最小态交互，同时保留正常收起动画");
            PrivateCall(window, "SettleGameQuietCollapse", null, EventArgs.Empty);
            Console.WriteLine($"Game entry without frames: state={AgentField<IslandState>(window, "_state")}; animator={animator.IsRunning}; cardAnimating={AgentField<bool>(window, "_cardAnimating")}");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar && !animator.IsRunning,
                "全屏停止渲染时，独立兜底可在无需下一帧的情况下稳定为最小态");
            Check(Math.Abs(animator.CurrentGeometry().Height - IslandMetrics.BarHeight) < .01,
                "游戏最小态的实际几何与状态一致，不残留卡片命中区域");
            Hover(); Click(); PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.Notifications);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "游戏期间悬停、点击和直接打开页面均不能展开");
            PrivateCall(window, "SetLayerPinned", "status", true, false);
            PrivateCall(window, "SetState", IslandState.Pill, true);
            Check(layers.IsPinned("status") && AgentField<IslandState>(window, "_state") == IslandState.Bar
                && !layers.IsActive("status"), "游戏期间保留置顶偏好但不显示置顶岛");
            SetAgentField(window, "_volumeUserFeedbackUntil", Environment.TickCount64 + 5000);
            PrivateCall(window, "OnVolumeChanged", null, new VolumeInfo(42, false));
            SetAgentField(window, "_volumeUserFeedbackUntil", 0L);
            Check(!layers.IsActive("volume") && AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "游戏期间主动音量调节也不能突破最小态锁定");
            PrivateCall(window, "SelectComponent", IslandPageIds.System);
            PrivateCall(window, "SetState", IslandState.Tray, true);
            Check(!AgentField<bool>(window, "_componentCardAwaitingPointer") && AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "组件切换和中转站入口不留下等待手势或展开托盘");
            Game(false); layers.HideAll(); Click(); PumpAnimations(650); root.UpdateLayout();
            Check(AgentField<IslandState>(window, "_state") == IslandState.Card && window.CardCarouselHost.IsHitTestVisible
                && !AgentField<bool>(window, "_cardAnimating"), "游戏退出后原窗口立即恢复展开和命中，无需重启");
            Game(true); Game(false); layers.HideAll(); Click(); PumpAnimations(2200);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Card && !layers.IsShowing("gamequiet"),
                "提示期间退出游戏会取消收起计时，不会事后缩回正在使用的岛");
            Game(true); SettingsRegistry.Write(options, "NotificationGameQuietEnabled", false);
            PrivateCall(window, "OnGameActivityOptionsChanged");
            Check(!AgentField<bool>(window, "_gameQuietActive") && AgentField<long>(window, "_gameQuietIntroUntil") == 0,
                "提示期间关闭游戏免打扰立即取消待执行的锁定");
            SettingsRegistry.Write(options, "NotificationGameQuietEnabled", true);
            for (int cycle = 0; cycle < 12; cycle++)
            {
                PrivateCall(window, "SetState", IslandState.Bar, true);
                Click(); Game(true); PrivateCall(window, "FinishGameQuietIntro"); Hover(); Click();
                Check(AgentField<IslandState>(window, "_state") == IslandState.Bar && !window.CardCarouselHost.IsHitTestVisible,
                    "快速进出游戏时收起动画不解除最小态交互锁 " + cycle);
                Game(false); layers.HideAll(); Click();
                Check(AgentField<IslandState>(window, "_state") == IslandState.Card,
                    "快速进出游戏后仍能再次展开 " + cycle);
            }
        }
        finally
        {
            SettingsRegistry.Write(options, "NotificationGameQuietEnabled", true);
            clipboard.DismissCopiedPending(); SetAgentField(window, "_clipboard", null);
            Game(false); layers.HideAll();
            MotionPolicy.Apply(reduce, power);
            PrivateCall(window, "SetState", IslandState.Bar, true);
        }
    }
}
