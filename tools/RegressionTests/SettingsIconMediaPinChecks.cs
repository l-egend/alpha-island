using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AlphaIsland.PluginSdk;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void SettingsIconMediaPinChecks(alpha_island.MainWindow window, Grid root)
    {
        using var media = new SmtcService(window.Dispatcher);
        SetAgentField(window, "_smtc", media);
        var options = AgentField<IslandOptions>(window, "_options");
        var pages = AgentField<IslandPageHost>(window, "_pages");
        SettingsRegistry.Write(options, "MediaWindowHandoffEnabled", false);
        SetAgentField(window, "_mediaSourceForeground", false);
        SetAgentField(window, "_gameQuietActive", false);
        SetAgentField(window, "_sessionLocked", false);
        SetAgentField(window, "_musicIslandDismissed", false);
        PrivateCall(window, "DismissGreeting");
        PrivateCall(window, "ClearDesktopInbox");
        var track = new NowPlayingInfo { Title = "媒体侧岛置顶检查", Artist = "独立测试", SourceApp = "fixture-player.exe",
            SessionId = "settings-icon-pin", State = PlaybackState.Paused, Duration = TimeSpan.FromMinutes(3), CanToggle = true };
        media.SetDebugNowPlaying(track); PrivateCall(window, "ApplyMedia", track);
        PrivateCall(window, "SelectComponent", "screen-tools");
        PrivateCall(window, "SetState", IslandState.Card, true, "screen-tools");
        PrivateCall(window, "SyncSatelliteOrbs");
        PumpAnimations(500); root.UpdateLayout();
        Check(AgentField<IslandState>(window, "_state") == IslandState.Card && pages.ActiveId == "screen-tools"
            && AgentField<string?>(window, "_sideBubbleComponentKey") == "music"
            && window.SideBubble.IsVisible && window.SideBubble.Opacity > .9,
            "复现屏幕工具卡展开且左侧媒体侧岛显示暂停状态");
        var right = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
            { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent };
        window.SideBubble.RaiseEvent(right);
        PumpAnimations(100); root.UpdateLayout();
        Check(right.Handled && AgentField<bool>(window, "_isPinned"),
            "其他卡片展开时，真实媒体侧岛的右键事件可以独立置顶播放器");
        Check(AgentField<IslandState>(window, "_state") == IslandState.Card && pages.ActiveId == "screen-tools",
            "右键置顶媒体侧岛保留正在查看的屏幕工具卡");
        Check(window.SideBubble.ToolTip?.ToString()?.Contains("已独立置顶") == true
            && ReferenceEquals(window.SideBubble.BorderBrush, window.FindResource("AccentCyan")),
            "媒体侧岛的悬停提示与高亮边框同步反馈置顶状态");
        PrivateCall(window, "ApplyMedia", track);
        Check(AgentField<bool>(window, "_isPinned"), "暂停媒体的重复状态刷新不会撤销侧岛置顶");
        System.IO.Directory.CreateDirectory("bin/settings-icon-pin-previews");
        SaveThemePreview(window.SideBubble, "bin/settings-icon-pin-previews/media-side-pinned.png");
        void RightClick()
        {
            var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent };
            window.SideBubble.RaiseEvent(click); root.UpdateLayout();
            Check(click.Handled, "侧岛右键事件由自身消费，不继续触发其他卡片操作");
        }
        RightClick();
        Check(!AgentField<bool>(window, "_isPinned") && AgentField<IslandState>(window, "_state") == IslandState.Card
            && pages.ActiveId == "screen-tools", "再次右键取消媒体置顶，当前卡片保持展开");
        var playing = track with { State = PlaybackState.Playing };
        media.SetDebugNowPlaying(playing); SetAgentField(window, "_musicIslandDismissed", false);
        PrivateCall(window, "ApplyMedia", playing);
        PrivateCall(window, "SetState", IslandState.Card, true, "screen-tools");
        PrivateCall(window, "SyncNotificationLayers"); PumpAnimations(350); root.UpdateLayout();
        Check(window.SideBubble.IsVisible && AgentField<string?>(window, "_sideBubbleComponentKey") == "music",
            "正在播放时也通过同一个卡片旁媒体侧岛提供操作");
        RightClick();
        Check(AgentField<bool>(window, "_isPinned") && pages.ActiveId == "screen-tools", "播放中媒体侧岛也能置顶，保留当前卡片");
        RightClick();
        Check(!AgentField<bool>(window, "_isPinned") && AgentField<IslandState>(window, "_state") == IslandState.Card,
            "播放中取消置顶同样不会关闭当前卡片");
        PrivateCall(window, "ShowPillNotification", IslandNotification.Seconds("netspeed", 5));
        PrivateCall(window, "SetState", IslandState.Card, true, "screen-tools");
        PumpAnimations(350); root.UpdateLayout();
        Check(AgentField<string?>(window, "_sideBubbleComponentKey") == "netspeed", "同一侧岛显示网速提示时正确切换操作目标");
        RightClick();
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        Check(layers.IsPinned("netspeed") && !AgentField<bool>(window, "_isPinned"), "其他侧岛置顶只作用于当前显示的模块");
        RightClick();
        Check(!layers.IsPinned("netspeed") && AgentField<IslandState>(window, "_state") == IslandState.Card,
            "其他侧岛取消置顶也保留当前展开卡片");
        PrivateCall(window, "ClearDesktopInbox");
        SettingsSemanticIconChecks();
    }

    private static void SettingsSemanticIconChecks()
    {
        var settings = new alpha_island.Settings.SettingsWindow(false);
        settings.LoadFrom(new IslandOptions().Normalized());
        var panel = (Grid)settings.Content; settings.Content = null; panel.Resources = settings.Resources;
        using var source = new HwndSource(new HwndSourceParameters("Settings semantic icons regression")
        { Width = 880, Height = 660, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        source.RootVisual = panel;
        try
        {
            var reduced = Descendants<GlyphIcon>((StackPanel)settings.FindName("ReduceEffectsRows")).Single();
            var shelf = Descendants<GlyphIcon>((Border)settings.FindName("Icon_FileTransfer")).Single();
            var memory = Descendants<GlyphIcon>((Border)settings.FindName("Icon_MemoryMonitor")).Single();
            Check(reduced.Text == GlyphArtwork.ReducedMotionGlyph && shelf.Text == GlyphArtwork.FileShelfGlyph
                && memory.Text == GlyphArtwork.MemoryMonitorGlyph, "真实设置行分别使用削减动效、文件中转和内存检测专用矢量图标");
            Check(GlyphArtwork.Titles[reduced.Text] == "削减动效" && GlyphArtwork.Titles[shelf.Text] == "文件拖拽中转站"
                && GlyphArtwork.Titles[memory.Text] == "内存占用检测", "图标目录语义与当前设置文字一致");
            Check(GlyphArtwork.Titles["\uE769"] == "暂停" && GlyphArtwork.Titles["\uE8B7"] == "小岛"
                && GlyphArtwork.Titles["\uE950"] == "CPU", "新增语义图标不改动其他位置的暂停、小岛和 CPU 图标");
            foreach (var (page, icon, name) in new[] { ("appearance", reduced, "reduced-motion"), ("behavior", shelf, "file-shelf"), ("system", memory, "memory-monitor") })
            {
                PrivateCall(settings, "SelectPageByTag", page); panel.UpdateLayout();
                var row = (FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(icon));
                var card = (FrameworkElement)VisualTreeHelper.GetParent(row);
                Check(icon.IsVisible && icon.ActualWidth > 0 && icon.ActualHeight > 0, name + " 在对应的真实设置页面可见");
                SaveThemePreview(card, "bin/settings-icon-pin-previews/" + name + ".png");
            }
        }
        finally { source.RootVisual = null; settings.ForceClose = true; settings.Close(); }
    }
}
