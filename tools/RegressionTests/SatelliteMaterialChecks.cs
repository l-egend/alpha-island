using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Services;
using alpha_island.Controls;
using Size = System.Windows.Size;

internal static partial class Program
{
    private static void SatelliteMaterialChecks(alpha_island.MainWindow window, Grid root)
    {
        using var destination = new HwndSource(new HwndSourceParameters("Satellite background destination")
        { Width = 640, Height = 420, PositionX = -31000, PositionY = -31000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        var navigation = AgentField<AgentAppNavigationService>(window, "_agentNavigation");
        bool reduce = MotionPolicy.ReduceMotion, save = MotionPolicy.PowerSave; int tier = MotionPolicy.RenderTier;
        var options = AgentField<IslandOptions>(window, "_options");
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            SettingsRegistry.Write(options, "AgentStatusEnabled", true);
            SetAgentField(window, "_agentNavigation", new AgentAppNavigationService(new AgentWindowFixture
            { Windows = [new(destination.Handle, "Codex", "Fixture", false)] }));
            AgentStatus.Set(AgentRunState.Thinking, "Fixture", "external:codex/material-a");
            AgentStatus.Set(AgentRunState.Thinking, "Fixture", "external:codex/material-b");
            PrivateCall(window, "UpdateAgentStatusUi"); PumpAnimations(400);
            var fresh = (Border)PrivateCall(window, "CreateSatelliteOrb", "agentstatus")!;
            Check(HasBodyContour(fresh), "助手小岛首次布局之前就有可绘制背景轮廓");
            fresh.Measure(new Size(fresh.Width, fresh.Height)); fresh.Arrange(new Rect(0, 0, fresh.Width, fresh.Height));
            LiquidMaterialOverlap.Apply(fresh, null);
            Check(HasBodyContour(fresh),
                $"新建助手小岛在异步背景刷新前已有完整轮廓，不能只显示出框头像（clip={fresh.Clip?.Bounds}）");
            fresh.HorizontalAlignment = System.Windows.HorizontalAlignment.Left; fresh.VerticalAlignment = VerticalAlignment.Top;
            root.Children.Add(fresh); root.UpdateLayout();
            fresh.Width = fresh.Height = 36; fresh.CornerRadius = new CornerRadius(18); root.UpdateLayout();
            Check(HasBodyContour(fresh) && Math.Abs(fresh.Clip!.Bounds.Width - 36) < .01 && Math.Abs(fresh.Clip.Bounds.Height - 36) < .01,
                "助手小岛尺寸变化在布局内同步更新背景裁剪，不等待合成帧");
            SatelliteOrbAnimator.Return(fresh, 0, immediate: true); SatelliteOrbAnimator.Show(fresh);
            Check(HasBodyContour(fresh) && MaterialAlpha(fresh) > 5, "中断并重新显示的小岛同步保留背景与完整轮廓");
            root.Children.Remove(fresh);
            var automatic = new Border { Child = new Grid { Width = 60, Height = 40 },
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            PrivateCall(window, "RegisterLiquidChrome", automatic);
            Check(automatic.Clip == null, "未知尺寸的小岛不使用空轮廓裁去背景");
            root.Children.Add(automatic); root.UpdateLayout();
            Check(HasBodyContour(automatic) && automatic.Clip!.Bounds.Width == 60 && automatic.Clip.Bounds.Height == 40,
                "自动尺寸的小岛首次布局完成即可显示完整背景");
            root.Children.Remove(automatic);
            var orbs = AgentField<Dictionary<string, Border>>(window, "_satelliteOrbViews");
            var random = new Random(161);
            foreach (string pattern in new[] { "rapid", "glass-refresh", "policy", "status-refresh", "session-count" })
            {
                for (int cycle = 0; cycle < 20; cycle++)
                {
                    PrivateCall(window, "SetState", cycle % 3 == 0 ? IslandState.Pill : IslandState.Card, false, IslandPageIds.Music);
                    if (pattern == "glass-refresh") PrivateCall(window, "ApplyGlassAppearance");
                    if (pattern == "policy") MotionPolicy.Apply(cycle % 4 == 0, false);
                    if (pattern == "status-refresh")
                    {
                        AgentStatus.Set(cycle % 2 == 0 ? AgentRunState.Working : AgentRunState.Thinking, "Fixture", "external:codex/material-b");
                        PrivateCall(window, "UpdateAgentStatusUi");
                    }
                    if (pattern == "session-count")
                    {
                        if (cycle % 3 == 1) AgentStatus.Reset(owner: "external:codex/material-b");
                        else AgentStatus.Set(AgentRunState.Thinking, "Fixture", "external:codex/material-b");
                        PrivateCall(window, "UpdateAgentStatusUi");
                    }
                    root.UpdateLayout(); PrivateCall(window, "RenderLiquidBackdrop");
                    PumpAnimations(random.Next(3, 24));
                }
                MotionPolicy.Apply(false, false);
                AgentStatus.Set(AgentRunState.Thinking, "Fixture", "external:codex/material-b");
                PrivateCall(window, "UpdateAgentStatusUi");
                PrivateCall(window, "SetState", IslandState.Card, false, IslandPageIds.Music);
                PumpAnimations(650); root.UpdateLayout();
                var visible = orbs.Values.Where(orb => orb.IsVisible && orb.Opacity > .95).ToArray();
                Check(visible.Length == 2, $"{pattern}：反复切换后确实显示两个助手小岛");
                foreach (var orb in visible)
                    Check(HasBodyContour(orb) && MaterialAlpha(orb) > 5,
                        $"{pattern}：反复切换后头像旁的玻璃背景和裁剪均有效，alpha={MaterialAlpha(orb)}，clip={orb.Clip?.Bounds}");
            }
            Directory.CreateDirectory("bin/SatelliteBackground161/previews");
            SaveThemePreview(root, "bin/SatelliteBackground161/previews/repeated-card-switch.png");
            foreach (var orb in orbs.Values.Where(orb => orb.IsVisible && orb.Opacity > .95).ToArray())
            {
                var glyph = ((Grid)orb.Child).Children.OfType<SemanticIcon>().Single(icon => icon.Name == "OrbGlyph");
                var adorners = System.Windows.Documents.AdornerLayer.GetAdornerLayer(glyph)?.GetAdorners(glyph);
                Check(adorners?.OfType<AgentAvatarAdorner>().Any(avatar => avatar.IsVisible) == true, "可见的助手小岛具有出框头像层");
                SatelliteOrbAnimator.Return(orb, 0, immediate: true);
                Check(!orb.IsVisible && adorners?.OfType<AgentAvatarAdorner>().Any(avatar => avatar.IsVisible) != true,
                    "隐藏助手小岛时头像浮层同步隐藏，没有透明小岛外的残留头像");
            }
        }
        finally
        {
            AgentStatus.Reset(owner: "external:codex/material-a"); AgentStatus.Reset(owner: "external:codex/material-b");
            PrivateCall(window, "UpdateAgentStatusUi");
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduce, save);
            SetAgentField(window, "_agentNavigation", navigation);
        }
    }

    private static bool HasBodyContour(Border orb)
    {
        double width = orb.ActualWidth > 0 ? orb.ActualWidth : orb.Width;
        double height = orb.ActualHeight > 0 ? orb.ActualHeight : orb.Height;
        return double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0
            && orb.Clip?.FillContains(new System.Windows.Point(width / 2, height / 2)) == true;
    }
}
