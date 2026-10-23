using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Controls;
using alpha_island.Core;
using AlphaIsland.PluginSdk;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

internal static partial class Program
{
    private static void NotificationRefinementChecks(alpha_island.MainWindow window)
    {
        var icon = new SemanticIcon { Width = 64, Height = 64, Shape = IconShape.Notification, Foreground = Brushes.Cyan };
        icon.Measure(new(64, 64)); icon.Arrange(new(0, 0, 64, 64));
        var progress = (DependencyProperty)typeof(SemanticIcon).GetField("ProgressProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        byte[] Frame(double p)
        {
            SetAgentField(icon, "_transitioning", true); icon.SetValue(progress, p);
            icon.InvalidateVisual(); return SemanticIconPixels(icon);
        }
        int Ink(byte[] pixels, int x, int y) => pixels[(y * 64 + x) * 4 + 3];
        int RowInk(byte[] pixels, int y, int left, int right) => Enumerable.Range(left, right - left + 1).Count(x => Ink(pixels, x, y) > 80);
        var start = Frame(0); var first = Frame(.61); var complete = Frame(1);
        Check(RowInk(complete, 22, 18, 46) >= 24 && RowInk(complete, 34, 18, 40) >= 18,
            "默认通知图标为带两条横线的消息气泡，替代信封");
        Check(RowInk(first, 22, 18, 46) >= 22 && RowInk(first, 34, 18, 40) <= 2,
            "通知气泡的横线逐条出现，第一条完成时第二条尚未绘制");
        int Left(byte[] p) => Enumerable.Range(0, 64).First(x => Enumerable.Range(0, 64).Any(y => Ink(p, x, y) > 80));
        int Bottom(byte[] p) => Enumerable.Range(0, 64).Last(y => Enumerable.Range(0, 64).Any(x => Ink(p, x, y) > 80));
        Check(Left(complete) > Left(start) && Bottom(complete) < Bottom(start),
            "通知轮廓从左下移向中心，内部横线随轮廓一起移动");
        Check(!icon.RenderTransform.HasAnimatedProperties && !SemanticProgressHasClock(icon),
            "通知帧绘制复用内部进度，不为图标整体留下变换或空闲时钟");
        byte[] reset = GlyphPixels(new GlyphIcon { Text = "\uE777", Foreground = Brushes.White });
        Check(reset[(6 * 32 + 22) * 4 + 3] < 30,
            "设置重置箭头在右上留出明显缺口，不再画闭合回路");
        var message = new DesktopNotification("width-test", "", "应用通知", "游戏收尾 · 提醒",
            "本次已玩 1 小时 29 分钟，今日累计 1 小时 2 分钟。休息一下再继续。", DateTimeOffset.Now, DesktopEventKind.Assistant);
        var pill = AgentField<DesktopNotificationPill>(window, "_notificationSummary");
        pill.SetNotification(message); pill.SetStackCount(1);
        WriteWindowMember(window, "_state", IslandState.Bar);
        // Exercise the real presentation path so its measured width reaches the island geometry.
        PrivateCall(window, "OnSystemToastsReceived", new[] { message }, false);
        double bodyWidth = (double)typeof(alpha_island.MainWindow).GetProperty("CurrentPillWidth", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Check(pill.Width > 328 && bodyWidth > IslandMetrics.PillWidth && bodyWidth <= IslandMetrics.MaxPillWidth,
            "通知正文较长时，小岛与内容一起适度加宽，保持屏幕内的宽度上限");
        Check(pill.Detail.TextWrapping == TextWrapping.NoWrap && pill.Height <= 36,
            "通知宽度调整保留当前正常的两行摘要布局");
        var sizing = new DesktopNotificationPill(); sizing.SetNotification(message with { Body = "准备就绪" });
        double shortWidth = sizing.Width;
        sizing.SetNotification(message with { Body = new string('长', 300) });
        Check(sizing.Width > shortWidth && sizing.Width <= 504 && sizing.Detail.Text.Length == 300,
            "通知按内容长度增加宽度，超长正文保留数据并受屏幕内的最大宽度约束");
        var gallery = new StackPanel { Width = 540, Height = 220 };
        var frames = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        foreach (var (p, label) in new[] { (0d, "左下入场"), (.61, "第一条横线"), (1d, "最终状态") })
        {
            var sample = new SemanticIcon { Shape = IconShape.Notification, Width = 64, Height = 64, Foreground = Brushes.Cyan };
            SetAgentField(sample, "_transitioning", true); sample.SetValue(progress, p);
            var cell = new StackPanel { Width = 128 };
            cell.Children.Add(sample); cell.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
            frames.Children.Add(cell);
        }
        var resetCell = new StackPanel { Width = 128 };
        resetCell.Children.Add(new GlyphIcon { Text = "\uE777", Width = 48, Height = 48, Margin = new Thickness(0, 8, 0, 8), Foreground = Brushes.White });
        resetCell.Children.Add(new TextBlock { Text = "重置", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
        frames.Children.Add(resetCell); gallery.Children.Add(frames);
        var samplePill = new DesktopNotificationPill(); samplePill.SetNotification(message); samplePill.SetStackCount(3);
        gallery.Children.Add(new Border { CornerRadius = new CornerRadius(24), Padding = new Thickness(8), Margin = new Thickness(0, 16, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x32, 0x47, 0x5A)), Child = samplePill });
        gallery.Measure(new(580, double.PositiveInfinity)); gallery.Arrange(new Rect(new Point(), gallery.DesiredSize)); gallery.UpdateLayout();
        SaveNotificationContentPreview(gallery, "v138-message-reset-width.png");
        PrivateCall(window, "ClearDesktopInbox");
    }
}
