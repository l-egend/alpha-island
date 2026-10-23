using System.Windows;
using System.Windows.Controls;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using Point = System.Windows.Point;

internal static partial class Program
{
    private static void HintBoundsChecks(alpha_island.MainWindow window, Grid root)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        SettingsRegistry.Write(options, "NotificationGameQuietEnabled", true);
        SettingsRegistry.Write(options, "AssistantGameWrapEnabled", false);
        try
        {
            PrivateCall(window, "ApplyGameActivity", new GameActivityState(1, true, "测试游戏", "fixture", 1));
            root.UpdateLayout();
            var game = AgentField<DesktopNotificationPill>(window, "_gameQuietNotice");
            double bodyWidth = (double)ReadWindowMember(window, "CurrentPillWidth")!;
            Console.WriteLine($"Game intro content={game.Width:0.##}; body={bodyWidth:0.##}; text={game.Detail.Text}");
            Check(bodyWidth >= game.Width + 16 - .1,
                "游戏模式进入提示的岛体容纳完整提示控件，不裁掉左右文字");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill,
                "游戏模式仍先显示进入提示，再自动收为最小态");
            game.SetNotification(new("long-game-hint", "", "Alpha Island", "检测到游戏开启",
                "免打扰模式已开启 · 即将收起 · 一个名称比较长的游戏测试项目", DateTimeOffset.Now, DesktopEventKind.System));
            PrivateCall(window, "SetState", IslandState.Pill, true); root.UpdateLayout();
            Check((double)ReadWindowMember(window, "CurrentPillWidth")! >= game.Width + 15.9
                && game.Width + 16 <= IslandMetrics.MaxPillWidth,
                "长游戏名称也同步扩展岛体，并保持屏幕内的尺寸上限");
            Check(window.GreetingText.TextTrimming == TextTrimming.CharacterEllipsis
                && window.GreetingText.ToolTip != null && window.DownloadNotifyTitle.TextTrimming == TextTrimming.CharacterEllipsis
                && window.DownloadNotifyTitle.ToolTip != null,
                "长欢迎语和下载标题不会越过相邻控件，并保留完整文字查看入口");
            var gallery = new StackPanel { Width = 550 };
            foreach (var (kind, title, body) in new[]
            {
                (DesktopEventKind.System, "检测到游戏开启", "免打扰模式已开启 · 即将收起 · 鸣潮"),
                (DesktopEventKind.Assistant, "游戏收尾 · 鸣潮", "本次已玩 1 小时 29 分钟，今日累计 2 小时。\n当前保留未读 4 条。"),
                (DesktopEventKind.Weather, "天气提醒", "今天有小雨，出门请记得带伞。"),
                (DesktopEventKind.BluetoothLowBattery, "耳机电量低", "OPPO Enco Clip2 · 左耳 15% · 右耳 20%"),
                (DesktopEventKind.DeviceCharging, "鼠标正在充电", "MCHOSE K5 Ultra · 当前 40%"),
                (DesktopEventKind.Message, "应用消息", "较长通知需要完整阅读时，可点击进入通知中心查看全文。")
            })
            {
                var pill = new DesktopNotificationPill();
                pill.SetNotification(new("layout-" + kind, "", "示例应用", title, body, DateTimeOffset.Now, kind));
                pill.SetStackCount(kind == DesktopEventKind.Message ? 3 : 1);
                pill.Measure(new(pill.Width, pill.Height)); pill.Arrange(new Rect(0, 0, pill.Width, pill.Height)); pill.UpdateLayout();
                var detailTop = pill.Detail.TranslatePoint(new Point(), pill).Y;
                Check(!pill.Detail.Text.Contains('\n') && detailTop >= -.1 && detailTop + pill.Detail.ActualHeight <= pill.Height + .2,
                    kind + " 提示的两行文字不裁掉半行，长正文只在行尾省略");
                Check(pill.Width + 16 <= IslandMetrics.MaxPillWidth && pill.Title.ActualWidth > 0 && pill.Detail.ActualWidth > 0,
                    kind + " 提示在图标、天气图及通知计数占位后仍保留文字空间");
                gallery.Children.Add(new Border { Child = pill, Padding = new Thickness(8), Margin = new Thickness(0, 5, 0, 5),
                    CornerRadius = new CornerRadius(24), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 56, 74)) });
            }
            gallery.Measure(new(550, double.PositiveInfinity)); gallery.Arrange(new Rect(new Point(), gallery.DesiredSize)); gallery.UpdateLayout();
            SaveNotificationContentPreview(gallery, "v141-hint-layouts.png");
        }
        finally
        {
            PrivateCall(window, "ApplyGameActivity", GameActivityState.Inactive);
            AgentField<IslandLayerHost>(window, "_layers").HideAll();
            PrivateCall(window, "SetState", IslandState.Bar, true);
        }
    }
}
