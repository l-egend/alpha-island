using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using alpha_island.Animation;
using alpha_island.Core;
using Grid = System.Windows.Controls.Grid;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;

internal static partial class Program
{
    private static void NotificationWeatherLayoutChecks(alpha_island.MainWindow window, Grid root)
    {
        const string previews = "bin/NotificationWeather169/previews";
        Directory.CreateDirectory(previews); MotionPolicy.Apply(true, false);
        var options = AgentField<IslandOptions>(window, "_options");
        SettingsRegistry.Write(options, "NotificationRememberHistory", false);
        SettingsRegistry.Write(options, "NotificationQuietMode", false);
        SettingsRegistry.Write(options, "NotificationBlockedApps", "");
        SettingsRegistry.Write(options, "NotificationAllowedApps", "");
        SettingsRegistry.Write(options, "NotificationKeywords", "");
        bool fullThumb = true;
        foreach (int count in new[] { 5, 50, 100 })
        {
            PrivateCall(window, "ClearDesktopInbox");
            var messages = Enumerable.Range(0, count).Select(i => new DesktopNotification("layout-" + i, "fixture", "布局检查",
                "通知 " + i, "用于检查通知列表滚动条的完整显示。", DateTimeOffset.Now.AddMinutes(-i), DesktopEventKind.System)).ToArray();
            PrivateCall(window, "OnSystemToastsReceived", messages, true);
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.Notifications);
            PumpAnimations(50); root.UpdateLayout();
            var view = AgentField<Grid>(window, "_notificationsView");
            var scroll = view.Children.OfType<ScrollViewer>().Single();
            var bar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
            var thumb = ((Track)bar.Template.FindName("PART_Track", bar)).Thumb;
            foreach (double progress in new[] { 0d, .5, 1d })
            {
                scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * progress); root.UpdateLayout();
                var surface = (Border)thumb.Template.FindName("ThumbSurface", thumb);
                var bounds = surface.TransformToAncestor(thumb).TransformBounds(new Rect(surface.RenderSize));
                Geometry? clip = LayoutInformation.GetLayoutClip(thumb);
                bool fits = clip == null || clip.FillContainsWithDetail(new RectangleGeometry(bounds)) == IntersectionDetail.FullyContains;
                fullThumb &= fits;
                Console.WriteLine($"Notifications {count}/{progress:0.0}: viewport={scroll.ViewportHeight:0.0}, extent={scroll.ExtentHeight:0.0}, slot={LayoutInformation.GetLayoutSlot(thumb).Height:0.0}, thumb={thumb.ActualHeight:0.0}, paint={bounds.Height:0.0}, clip={clip?.Bounds.Height:0.0}, full={fits}");
                if (progress == 0) SaveThemePreview(window.IslandBody, previews + $"/notifications-{count}.png");
            }
            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight / 2); root.UpdateLayout();
            double previous = scroll.VerticalOffset;
            thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            thumb.RaiseEvent(new DragDeltaEventArgs(0, 12) { RoutedEvent = Thumb.DragDeltaEvent });
            thumb.RaiseEvent(new DragCompletedEventArgs(0, 12, false) { RoutedEvent = Thumb.DragCompletedEvent });
            root.UpdateLayout();
            Check(scroll.VerticalOffset > previous && AgentField<IslandState>(window, "_state") == IslandState.Card,
                $"{count} 条通知时滑块拖动仍可阅读列表，卡片保持展开");
        }
        Check(fullThumb, "通知数量较多时，滚动滑块的顶部与底部均完整显示");
        using (var host = new HwndSource(new HwndSourceParameters("Dense scroll thumb layout")
        { Width = 110, Height = 110, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 }))
        {
            var dense = new ScrollViewer { Content = new Border { Width = 20000, Height = 20000 },
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            host.RootVisual = dense; dense.UpdateLayout();
            foreach (string part in new[] { "PART_VerticalScrollBar", "PART_HorizontalScrollBar" })
            {
                var bar = (ScrollBar)dense.Template.FindName(part, dense);
                var thumb = ((Track)bar.Template.FindName("PART_Track", bar)).Thumb;
                var surface = (Border)thumb.Template.FindName("ThumbSurface", thumb);
                var bounds = surface.TransformToAncestor(thumb).TransformBounds(new Rect(surface.RenderSize));
                var clip = LayoutInformation.GetLayoutClip(thumb);
                Check(clip == null || clip.FillContainsWithDetail(new RectangleGeometry(bounds)) == IntersectionDetail.FullyContains,
                    part + " 在大范围内容中仍完整显示圆角滑块");
                bool vertical = bar.Orientation == System.Windows.Controls.Orientation.Vertical;
                thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                thumb.RaiseEvent(new DragDeltaEventArgs(vertical ? 0 : 500, vertical ? 500 : 0) { RoutedEvent = Thumb.DragDeltaEvent });
                thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                dense.UpdateLayout();
                Check(vertical ? Math.Abs(dense.VerticalOffset - dense.ScrollableHeight) < .5 : Math.Abs(dense.HorizontalOffset - dense.ScrollableWidth) < .5,
                    part + " 拖动后可以到达内容末尾");
            }
            host.RootVisual = null;
        }
        SetAgentField(window, "_weatherBusy", true);
        PrivateCall(window, "SetState", IslandState.Card, true, "weather"); root.UpdateLayout();
        SaveThemePreview(window.IslandBody, previews + "/weather.png");
        SetAgentField(window, "_weatherBusy", false);
    }
}
