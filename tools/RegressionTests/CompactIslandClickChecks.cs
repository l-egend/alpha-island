using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void CompactIslandClickChecks(alpha_island.MainWindow main, Grid root)
    {
        var options = AgentField<IslandOptions>(main, "_options");
        var layers = AgentField<IslandLayerHost>(main, "_layers");
        var pages = AgentField<IslandPageHost>(main, "_pages");
        var oldMedia = AgentField<SmtcService?>(main, "_smtc");
        var oldPointer = AgentField<Func<System.Windows.Point?>>(main, "_compactIslandPointer");
        var pointer = new System.Windows.Point(300, 300);
        using var media = new SmtcService(main.Dispatcher);
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(true, false);
            SetAgentField(main, "_smtc", media);
            SetAgentField(main, "_compactIslandPointer", new Func<System.Windows.Point?>(() => pointer));
            SettingsRegistry.Write(options, "MediaWindowHandoffEnabled", false);
            var info = new NowPlayingInfo { SourceApp = "fixture.exe", Title = "副岛点击回归", State = PlaybackState.Playing };
            media.SetDebugNowPlaying(info); PrivateCall(main, "ApplyMedia", info);
            layers.HideAll();
            PrivateCall(main, "SetState", IslandState.Card, true, IslandPageIds.Clipboard);
            SetAgentField(main, "_sideBubbleComponentKey", "music");
            PrivateCall(main, "UpdateCompactIslandContent", "music");
            main.SideBubble.Visibility = Visibility.Visible; main.SideBubble.Opacity = 1;
            root.UpdateLayout();
            main.SideBubble.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            // A status refresh can replace the card-side content while the pointer is held.
            SetAgentField(main, "_sideBubbleComponentKey", "clipboard");
            PrivateCall(main, "UpdateCompactIslandContent", "clipboard");
            main.SideBubble.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 1, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Check(pages.ActiveId == IslandPageIds.Music,
                "按下媒体副岛后内容刷新，松开仍只打开按下时的媒体卡片，而不是其他卡片");

            for (int i = 0; i < 12; i++)
            {
                PrivateCall(main, "SetState", IslandState.Card, true, IslandPageIds.Clipboard);
                SetAgentField(main, "_sideBubbleComponentKey", "music");
                main.SideBubble.Visibility = Visibility.Visible; root.UpdateLayout();
                ClickCompactIsland(main.SideBubble);
                Check(pages.ActiveId == IslandPageIds.Music, "媒体副岛连续切换始终打开媒体卡片 " + i);
            }
            PrivateCall(main, "SetState", IslandState.Pill, true);
            layers.ShowSeconds("system", 30); PrivateCall(main, "SwitchMainIsland", "system");
            PrivateCall(main, "SyncSatelliteOrbs"); root.UpdateLayout();
            var music = main.MusicOrbBubble;
            Check(music.Visibility == Visibility.Visible, "媒体缩略副岛实际可见");
            music.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 2, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            Check(AgentField<string>(main, "_activeMainIslandKey") == "system", "按住副岛时不重排主岛，避免同一次点击落到其他内容");
            music.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 3, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
            Check(AgentField<string>(main, "_activeMainIslandKey") == "music", "松开媒体缩略副岛后只提升媒体岛");
            Check(AgentField<object?>(main, "_compactIslandPress") == null && !music.IsMouseCaptured,
                "点击完成后释放鼠标捕获和点击目标");

            PrivateCall(main, "SwitchMainIsland", "system"); PrivateCall(main, "SyncSatelliteOrbs"); root.UpdateLayout();
            music.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 4, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            music.ReleaseMouseCapture();
            Check(AgentField<object?>(main, "_compactIslandPress") == null, "丢失鼠标捕获后自动取消副岛点击");
            music.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 5, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
            Check(AgentField<string>(main, "_activeMainIslandKey") == "system", "点击取消后松开不会展开主岛或其他岛");
            music.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 6, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            pointer.Offset(40, 0);
            music.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 7, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
            Check(AgentField<string>(main, "_activeMainIslandKey") == "system", "拖动超出容差后松开不会当作副岛点击");
            var detached = (Border)PrivateCall(main, "CreateSatelliteOrb", "volume")!;
            detached.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 8, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            Check(AgentField<object?>(main, "_compactIslandPress") == null,
                "无法捕获鼠标时清理按下状态，不会阻塞后续悬停和点击");
        }
        finally
        {
            SetAgentField(main, "_smtc", oldMedia);
            SetAgentField(main, "_compactIslandPointer", oldPointer);
            layers.HideAll();
        }
    }

    private static MouseButtonEventArgs ClickCompactIsland(FrameworkElement view)
    {
        view.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, 1, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent };
        view.RaiseEvent(up);
        return up;
    }
}
