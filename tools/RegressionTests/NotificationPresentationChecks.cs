using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using AlphaIsland.PluginSdk;
using Grid = System.Windows.Controls.Grid;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

internal static partial class Program
{
    private static void NotificationPresentationChecks(alpha_island.MainWindow window, Grid root)
    {
        var now = DateTimeOffset.Now;
        Check(NotificationTimestamp.Format(now, now).StartsWith("今天 "), "当天通知在时间前显示今天");
        Check(NotificationTimestamp.Format(now.AddDays(-1), now).StartsWith("昨天 "), "前一天通知在时间前显示昨天");
        Check(NotificationTimestamp.Format(now.AddDays(-3), now).Contains("月")
            && !NotificationTimestamp.Format(now.AddDays(-3), now).Contains("今天"), "更早通知显示具体月份和日期");
        Check(NotificationTimestamp.Format(now.AddYears(-1), now).Contains("年"), "跨年通知显示年份以避免混淆");
        var panel = new StackPanel { Width = 340 };
        var review = new Border { Child = panel, Padding = new Thickness(10), Width = 360,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x24, 0x26, 0x2F)),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        root.Children.Add(review);
        bool reduced = MotionPolicy.ReduceMotion, power = MotionPolicy.PowerSave; int tier = MotionPolicy.RenderTier;
        try
        {
            var cards = new List<DesktopNotificationCard>();
            foreach (int day in new[] { 0, -1, -3 })
            {
                var card = new DesktopNotificationCard(new DesktopNotification("date-" + day, "qq", "QQ", "联系人", "通知内容保持完整，日期让消息更容易区分。", now.AddDays(day)));
                cards.Add(card); panel.Children.Add(card);
            }
            PumpAnimations(60); root.UpdateLayout();
            var first = cards[0];
            var glyph = (GlyphIcon)first.DismissButton.Content;
            Check(first.TimestampText.Text.StartsWith("今天 ") && first.TimestampText.ActualWidth > 45,
                "真实卡片日期与时间均有足够布局空间");
            Check(first.DismissButton.ActualWidth == 28 && glyph.ActualWidth == 18 && glyph.Text == "\uE74D",
                "通知删除按钮使用固定 28 像素命中区和清晰的 18 像素垃圾桶");
            Check(!glyph.DeletionAnimating && glyph.DeletionProgress == 0, "删除图标静止时没有常驻动画");
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            var originalSize = first.DismissButton.RenderSize;
            first.DismissButton.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            PumpAnimations(150); root.UpdateLayout();
            Check(!SystemParameters.ClientAreaAnimation || glyph.DeletionAnimating && glyph.DeletionProgress > .4,
                "悬停删除按钮实际触发轻量桶盖动画");
            Check(first.DismissButton.RenderSize == originalSize, "动画不移动按钮或改变点击区域");
            SaveNotificationContentPreview(first, "v97-delete-hover.png");
            PumpAnimations(600);
            Check(!glyph.DeletionAnimating && glyph.DeletionProgress == 0, "动画播放一次后结束并释放时钟");
            glyph.PlayDeletionAnimation(); MotionPolicy.Apply(true, false);
            Check(!glyph.DeletionAnimating && glyph.DeletionProgress == 0, "开启减弱动效立即停止删除动画");
            MotionPolicy.Apply(false, true); glyph.PlayDeletionAnimation();
            Check(!glyph.DeletionAnimating, "节能模式不启动删除动画");
            first.RefreshTimestamp(now.AddDays(1));
            Check(first.TimestampText.Text.StartsWith("昨天 "), "跨过午夜后现有卡片可刷新相对日期");
            first.RefreshTimestamp(now);
            int dismissed = 0; first.DismissRequested += _ => dismissed++;
            first.DismissButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(dismissed == 1 && cards[1].TimestampText.Text.StartsWith("昨天 "), "删除动作触发一次且不改动其他通知日期");
            review.Measure(new System.Windows.Size(360, 600));
            review.Arrange(new Rect(0, 0, 360, review.DesiredSize.Height)); review.UpdateLayout();
            SaveNotificationContentPreview(review, "v97-dates-and-delete.png");
            panel.Children.Clear();
            Check(!glyph.DeletionAnimating, "卡片卸载不保留动画时钟");
        }
        finally
        {
            root.Children.Remove(review);
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduced, power);
        }
    }
}
