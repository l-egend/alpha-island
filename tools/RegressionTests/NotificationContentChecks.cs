using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Core;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

internal static partial class Program
{
    private static void NotificationContentUiChecks(alpha_island.MainWindow window, Grid root)
    {
        var message = new DesktopNotification("no-subtitle", "MAA.App", "MAA", " \r\n ", "\r\n已是最新版本～\r\n", DateTimeOffset.Now);
        PrivateCall(window, "ClearDesktopInbox");
        PrivateCall(window, "SetState", IslandState.Pill, true);
        PrivateCall(window, "OnSystemToastsReceived", new[] { message }, false);
        PumpAnimations(100); root.UpdateLayout();
        var pill = AgentField<alpha_island.Controls.DesktopNotificationPill>(window, "_notificationSummary");
        Check(pill.Title.Text == "MAA" && pill.Detail.Text == "已是最新版本～",
            "没有小标题的真实通知提示使用正文，且不显示空白首行");
        SaveNotificationContentPreview(root, "notification-no-subtitle-pill.png", window.IslandBody);
        pill.SetNotification(message with { Title = "更新完成", Body = "完整正文" });
        Check(pill.Detail.Text == "更新完成", "有小标题时优先显示原通知标题");
        pill.SetNotification(message with { Title = "", Body = "\n已是最新版本～\n下载完成" });
        Check(pill.Detail.Text == "已是最新版本～ 下载完成", "多行正文合并为适合小岛的一行摘要");
        pill.SetNotification(message with { Title = "", Body = " \n\t" });
        Check(!string.IsNullOrWhiteSpace(pill.Detail.Text), "标题正文均为空时仍有明确通知提示");
        var card = new alpha_island.Controls.DesktopNotificationCard(message);
        card.Measure(new Size(350, double.PositiveInfinity));
        Check(card.TitleText.Visibility == Visibility.Collapsed && card.BodyText.Text == "已是最新版本～"
            && card.BodyText.Margin.Top >= 8,
            "通知记录没有小标题时不保留空标题行，正文接在应用栏之后");
        card.Arrange(new Rect(new Point(), card.DesiredSize)); card.UpdateLayout();
        SaveNotificationContentPreview(card, "notification-no-subtitle-card.png");
        var titleOnly = new alpha_island.Controls.DesktopNotificationCard(message with { Title = "下载完成", Body = "" });
        Check(titleOnly.TitleText.Text == "下载完成" && titleOnly.BodyText.Visibility == Visibility.Collapsed,
            "只有标题的通知不显示额外空正文行");
        var empty = new alpha_island.Controls.DesktopNotificationCard(message with { Title = "", Body = "" });
        Check(empty.BodyText.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(empty.BodyText.Text),
            "空内容通知在记录中也显示明确提示");
        Check(message.Title == " \r\n " && message.Body == "\r\n已是最新版本～\r\n",
            "显示兜底不改写原始通知内容");
        PrivateCall(window, "ClearDesktopInbox");
    }

    private static void ComponentHitAreaUiChecks(alpha_island.MainWindow window, Grid root)
    {
        foreach (var state in new[] { IslandState.Pill, IslandState.Card })
        {
            PrivateCall(window, "SetState", state, true); PumpAnimations(100); root.UpdateLayout();
            var handle = window.ComponentHandle;
            var bounds = handle.HitBounds;
            Check(bounds.Width == 28 && bounds.Height <= 60.1, state + " 判定收紧为线条周围的窄区域，不占整条卡片边缘");
            var points = new[] { new Point(20, bounds.Top + 2), new Point(26, handle.ActualHeight / 2), new Point(20, bounds.Bottom - 2) };
            foreach (var point in points)
            {
                var rootPoint = handle.TranslatePoint(point, root);
                var screen = handle.PointToScreen(point);
                int sx = (int)Math.Round(screen.X), sy = (int)Math.Round(screen.Y);
                var packed = new IntPtr(unchecked((sy & 0xffff) << 16 | (sx & 0xffff)));
                bool contains = handle.ContainsHandlePoint(point);
                var hit = VisualTreeHelper.HitTest(root, rootPoint)?.VisualHit;
                bool nativeContains = (bool)PrivateCall(window, "IsPointOnComponentHandle", sx, sy)!;
                var nativeHit = (IntPtr)PrivateCall(window, "WndProc", IntPtr.Zero, 0x0084, IntPtr.Zero, packed, false)!;
                Check(contains && hit == handle && nativeContains && nativeHit != new IntPtr(-1),
                    state + " 右侧收紧后的上下触发范围同时通过 WPF 与原生窗口命中 " + point);
            }
            Check(!handle.ContainsHandlePoint(new Point(-2, handle.ActualHeight / 2))
                && !handle.ContainsHandlePoint(new Point(32, handle.ActualHeight / 2)),
                state + " 收紧侧边判定不抢岛内按钮或旁边桌面操作");
        }
        ComponentHandleMotionChecks(window, root);
        PrivateCall(window, "SetState", IslandState.Bar, true); root.UpdateLayout();
        Check(window.ComponentHandle.Visibility == Visibility.Collapsed, "完全收起时不留下额外侧边触发区");
    }

    private static void SaveNotificationContentPreview(FrameworkElement visual, string fileName, FrameworkElement? island = null)
    {
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),
            (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        if (island == null)
        {
            var backdrop = new DrawingVisual();
            using (var drawing = backdrop.RenderOpen())
            {
                var area = new Rect(new Point(), visual.RenderSize);
                drawing.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1D, 0x1D, 0x22)), null, area);
                drawing.DrawRectangle(new VisualBrush(visual) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = area,
                    ViewportUnits = BrushMappingMode.Absolute, Viewport = area }, null, area);
            }
            bitmap.Render(backdrop);
        }
        else bitmap.Render(visual);
        System.Windows.Media.Imaging.BitmapSource output = bitmap;
        if (island != null)
        {
            var bounds = island.TransformToVisual(visual).TransformBounds(new Rect(new Point(), island.RenderSize));
            int left = Math.Max(0, (int)Math.Floor(bounds.Left) - 16), top = Math.Max(0, (int)Math.Floor(bounds.Top));
            int right = Math.Min(bitmap.PixelWidth, (int)Math.Ceiling(bounds.Right) + 58);
            int bottom = Math.Min(bitmap.PixelHeight, (int)Math.Ceiling(bounds.Bottom) + 12);
            output = new System.Windows.Media.Imaging.CroppedBitmap(bitmap, new Int32Rect(left, top, right - left, bottom - top));
        }
        string directory = Path.GetFullPath("bin/notification-previews"); Directory.CreateDirectory(directory);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(output));
        using var stream = File.Create(Path.Combine(directory, fileName)); encoder.Save(stream);
    }
}
