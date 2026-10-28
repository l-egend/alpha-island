using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendAgentMouseActivate(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    private static void AgentForegroundClickChecks(alpha_island.MainWindow window, Grid root)
    {
        using var targetWindow = new HwndSource(new HwndSourceParameters("Assistant foreground click destination")
        { Width = 640, Height = 420, PositionX = -31000, PositionY = -31800,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        using var islandWindow = new HwndSource(new HwndSourceParameters("Assistant click activation fixture")
        { Width = 600, Height = 300, PositionX = -30000, PositionY = -30800,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x80 });
        islandWindow.RootVisual = new Grid();
        var hook = (HwndSourceHook)Delegate.CreateDelegate(typeof(HwndSourceHook), window,
            typeof(alpha_island.MainWindow).GetMethod("WndProc", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!);
        islandWindow.AddHook(hook);
        var original = AgentField<AgentAppNavigationService>(window, "_agentNavigation");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var options = AgentField<IslandOptions>(window, "_options");
        try
        {
            SettingsRegistry.Write(options, "AgentStatusEnabled", true);
            SettingsRegistry.Write(options, "MediaWindowHandoffEnabled", true);
            foreach (var (provider, process, title) in new[] { ("codex", "Codex", "Current assistant"),
                ("antigravity", "Antigravity", "Current assistant"), ("dsh", "WindowsTerminal", "DSH current assistant") })
            {
                var target = new AgentAppWindow(targetWindow.Handle, process, title, false);
                var fixture = new AgentWindowFixture { Windows = [target] };
                SetAgentField(window, "_agentNavigation", new AgentAppNavigationService(fixture));
                string owner = "external:" + provider + "/foreground-click-fixture";
                layers.HideAll(); AgentStatus.Set(AgentRunState.Thinking, "Foreground click test", owner);
                PrivateCall(window, "UpdateAgentStatusUi");
                layers.ShowSeconds("system", 30);
                PrivateCall(window, "SwitchMainIsland", "system"); PrivateCall(window, "SetState", IslandState.Pill, true);
                PumpAnimations(50); root.UpdateLayout();
                var orbs = AgentField<Dictionary<string, Border>>(window, "_satelliteOrbViews");
                Check(orbs.TryGetValue("agentstatus", out var orb) && orb.Visibility == Visibility.Visible,
                    provider + " 前台切换前助手已经作为缩略小岛可见");
                fixture.Foreground = target.Handle; fixture.Activated = null;
                // Exercise the real native message before routing the WPF click.
                int activation = (int)SendAgentMouseActivate(islandWindow.Handle, 0x21, target.Handle, new IntPtr((0x201 << 16) | 1));
                if (activation is 1 or 2) fixture.Foreground = islandWindow.Handle;
                ClickCompactIsland(orb!);
                Check(activation == 3 && fixture.Activated == null && fixture.Foreground == target.Handle,
                    provider + " 在前台时点击残留縮略小岛不抢焦点，也不重复跳转");
                Check(AgentField<alpha_island.Animation.MediaHandoffOverlay?>(window, "_agentHandoff") == null,
                    "前台点击不创建出岛动画");

                fixture.Foreground = IntPtr.Zero; fixture.Activated = null;
                PrivateCall(window, "UpdateAgentStatusUi");
                PrivateCall(window, "SwitchMainIsland", "system"); PumpAnimations(50); root.UpdateLayout();
                ClickCompactIsland(orb!);
                Check(fixture.Activated?.Handle == target.Handle,
                    provider + " 在后台时缩略小岛仍直接打开对应助手");

                fixture.Foreground = IntPtr.Zero; fixture.Activated = null;
                layers.Hide("system"); PrivateCall(window, "UpdateAgentStatusUi");
                PrivateCall(window, "SetState", IslandState.Card, true, "focus"); PumpAnimations(50); root.UpdateLayout();
                Check(AgentField<string?>(window, "_sideBubbleComponentKey") == "agentstatus", "助手在卡片旁侧显示独立入口");
                var side = window.SideBubble.PointToScreen(new System.Windows.Point(window.SideBubble.ActualWidth / 2, window.SideBubble.ActualHeight / 2));
                var center = window.IslandBody.PointToScreen(new System.Windows.Point(window.IslandBody.ActualWidth / 2, window.IslandBody.ActualHeight / 2));
                Check((bool)PrivateCall(window, "CompactClickKeepsExternalFocus", (int)side.X, (int)side.Y)!,
                    "卡片旁侧小岛保持外部应用焦点");
                Check(!(bool)PrivateCall(window, "CompactClickKeepsExternalFocus", (int)center.X, (int)center.Y)!,
                    "完整卡片主体仍允许激活和文字输入");
                fixture.Foreground = target.Handle;
                ClickCompactIsland(window.SideBubble);
                Check(fixture.Activated == null, "助手已在前台时旁侧入口不重复打开");
                AgentStatus.Reset(owner: owner); PrivateCall(window, "StopAgentWindowTracking"); layers.HideAll();
            }
        }
        finally
        {
            foreach (string source in new[] { "codex", "antigravity", "dsh" }) AgentStatus.Reset(owner: "external:" + source + "/foreground-click-fixture"); PrivateCall(window, "StopAgentWindowTracking");
            SetAgentField(window, "_agentNavigation", original); layers.HideAll();
            islandWindow.RemoveHook(hook);
        }
    }
}