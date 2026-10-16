using System.Windows;
using System.Windows.Controls;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using alpha_island.ViewModels;

internal static partial class Program
{
    private static void MaintenancePresentationChecks(alpha_island.MainWindow main)
    {
        var model = new LauncherViewModel(_ => Task.CompletedTask);
        model.SetEntries([new("agent:codex", "Codex", "切换到已打开的助手", LauncherKind.Agent, "codex"),
            new("focus", "番茄钟", "岛内组件", LauncherKind.Component, "focus"),
            new("stats", "生产力统计", "岛内组件", LauncherKind.Component, "productivity")]);
        model.Status = "↑↓ 选择 · Enter 打开 · Esc 关闭";
        var launcher = new QuickLauncherWindow(model);
        try
        {
            var view = (FrameworkElement)launcher.Content; launcher.Content = null; view.DataContext = model;
            SaveConversationPreview(view, "docs/screens/v105-launcher.png", 520, 440);
            Check(Descendants<System.Windows.Controls.TextBox>(view).Any() && Descendants<System.Windows.Controls.ListBox>(view).Single().Items.Count == 3,
                "启动器实际界面显示搜索框与匹配入口");
            model.Query = "番茄";
            Check(Descendants<System.Windows.Controls.ListBox>(view).Single().Items.Count == 2, "搜索绑定立即更新组件结果和 AI 预填入口");
        }
        finally { launcher.Close(); }
        var pages = AgentField<IslandPageHost>(main, "_pages"); string previous = pages.ActiveId;
        var workspace = AgentField<IslandWorkspaceViewModel>(main, "_workspace");
        var inbox = workspace.Notifications.Inbox; var saved = inbox.History.ToArray();
        try
        {
            var service = AgentField<ProductivityService>(main, "_productivity");
            var ledger = AgentField<ProductivityLedger>(service, "_ledger");
            ledger.Restore([new(DateTime.Today.ToString("yyyy-MM-dd"), 1800, 1200, 2700, 3600)]);
            PrivateCall(main, "SelectComponent", "productivity"); PrivateCall(service, "Tick");
            PumpAnimations(60); main.IslandBody.UpdateLayout(); SaveThemePreview(main.IslandBody, "docs/screens/v105-productivity.png");
            Check(Descendants<TextBlock>(main.IslandBody).Any(text => text.Text == "1.0 小时"), "统计看板实际显示聚合时长与主题色图表");
            inbox.Clear();
            inbox.Add(new("preview-1", "QQ", "QQ", "测试联系人", "明天下午三点开会，请带上资料。", DateTimeOffset.Now), false);
            inbox.Add(new("preview-2", "wechat", "微信", "项目小组", "更新后的测试安排已发出。", DateTimeOffset.Now.AddDays(-1)), false);
            PrivateCall(main, "SelectComponent", IslandPageIds.Notifications); PumpAnimations(60); main.IslandBody.UpdateLayout();
            SaveThemePreview(main.IslandBody, "docs/screens/v105-notification-history.png");
            var history = AgentField<StackPanel>(main, "_notificationList");
            var notices = history.Children.OfType<alpha_island.Controls.DesktopNotificationCard>().ToArray();
            Check(notices.Length == 2 && notices.Any(card => card.Notification.AppName == "QQ" && card.TimestampText.Text.StartsWith("今天"))
                && notices.Any(card => card.Notification.AppName == "微信" && card.TimestampText.Text.StartsWith("昨天")), "通知卡片直接标注日期，不再用分类标题代替堆叠");
        }
        finally
        {
            inbox.Clear(); foreach (var item in saved.Reverse()) inbox.Add(item, false);
            PrivateCall(main, "SelectComponent", previous);
        }
    }
}
