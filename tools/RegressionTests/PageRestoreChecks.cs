using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using alpha_island.Core;
using alpha_island.Services;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

internal static partial class Program
{
    private static void PageRestoreUiChecks(alpha_island.MainWindow window, Grid root)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        var savedExtras = new Dictionary<string, JsonElement>(options.Extras);
        var pages = AgentField<IslandPageHost>(window, "_pages");
        string oldPage = pages.ActiveId;
        bool oldForeground = AgentField<bool>(window, "_mediaSourceForeground");
        var oldSmtc = AgentField<SmtcService?>(window, "_smtc");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        void Open() => PrivateCall(window, "IslandBody_MouseEnter", window.IslandBody,
            new MouseEventArgs(Mouse.PrimaryDevice, 0));
        void Click() => PrivateCall(window, "IslandBody_MouseLeftButtonDown", window.IslandBody,
            new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
        void Close() => PrivateCall(window, "CollapseToBar", true);
        void View(string id) => PrivateCall(window, "SetState", IslandState.Card, true, id);
        bool At(string id) => AgentField<IslandState>(window, "_state") == IslandState.Card && pages.ActiveId == id;
        try
        {
            PrivateCall(window, "DismissActiveNotifications");
            layers.HideAll(); SetAgentField(window, "_smtc", null);
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.System);
            PrivateCall(window, "CollapseToBar", true);
            Open(); PumpAnimations(100); root.UpdateLayout();
            Check(AgentField<IslandState>(window, "_state") == IslandState.Card
                && pages.ActiveId == IslandPageIds.System
                && pages.ActivePage!.View.Visibility == Visibility.Visible && window.CardCarouselHost.Opacity > .9,
                "没有媒体时，正常悬停恢复上次系统页面，不显示空胶囊");
            Check(SettingsRegistry.GetText(options, "LastOpenedCardPage", "") == IslandPageIds.System,
                "收起时保存稳定页面 Id，配置可导入导出");
            Close(); Click(); Check(At(IslandPageIds.System), "点击收起的小岛也恢复上次页面");
            SetAgentField(window, "_hoverOutTicks", 1000);
            PrivateCall(window, "HoverTimer_Tick", null, EventArgs.Empty);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "离开无媒体卡片后直接收起，不产生空白胶囊中间态");
            Open(); Check(At(IslandPageIds.System), "自动收起后的下一次悬停仍恢复页面");
            View("focus"); Close(); Open(); Check(At("focus"), "再次关闭与悬停恢复最新查看的番茄钟页面");
            Close();
            using var media = new SmtcService(window.Dispatcher);
            media.SetDebugNowPlaying(new NowPlayingInfo { Title = "隔离页面恢复音乐", State = PlaybackState.Playing });
            SetAgentField(window, "_smtc", media);
            Open(); Check(At("focus"), "播放中的音乐不会覆盖普通打开的页面记忆");
            PrivateCall(window, "SetState", IslandState.Pill, true);
            Check(SettingsRegistry.GetText(options, "LastOpenedCardPage", "") == "focus",
                "卡片自动降为胶囊也会记录页面");
            Click(); Check(At("focus"), "从普通媒体胶囊点击展开恢复上次页面");
            Close();
            SettingsRegistry.SetExtra(options, "LastOpenedCardPage", IslandPageIds.Music);
            Open(); Check(AgentField<IslandState>(window, "_state") == IslandState.Pill,
                "上次是播放器且仍有媒体时保留两级展开");
            Click(); Check(At(IslandPageIds.Music), "有媒体的播放器胶囊点击恢复媒体页面");
            Close(); SetAgentField(window, "_mediaSourceForeground", true);
            Open(); Check(AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "前台媒体隐藏策略仍阻止重复打开媒体岛");
            SettingsRegistry.SetExtra(options, "LastOpenedCardPage", "focus");
            Open(); Check(At("focus"), "媒体在前台时仍可恢复其他正常页面");
            Close(); SetAgentField(window, "_mediaSourceForeground", false);
            SetAgentField(window, "_smtc", null);
            SettingsRegistry.SetExtra(options, "LastOpenedCardPage", IslandPageIds.Music);
            Open(); Check(At(IslandPageIds.System), "媒体会话消失后使用有内容的系统页兜底");
            Close(); SettingsRegistry.SetExtra(options, "LastOpenedCardPage", "removed.plugin.page");
            Open(); Check(At(IslandPageIds.System), "原插件页面不存在时安全恢复系统页");
            Close(); options.Extras.Remove("LastOpenedCardPage");
            Open(); Check(At(IslandPageIds.System), "首次使用且没有媒体也不会展开空白胶囊");
            View("focus"); Close();
            var persisted = JsonSerializer.Deserialize<IslandOptions>(JsonSerializer.Serialize(options))!.Normalized();
            var restarted = new alpha_island.MainWindow(persisted);
            try
            {
                PrivateCall(restarted, "ExpandIslandFromBar");
                Check(AgentField<IslandState>(restarted, "_state") == IslandState.Card
                    && AgentField<IslandPageHost>(restarted, "_pages").ActiveId == "focus",
                    "配置重新加载到新窗口后仍恢复上次页面");
                PrivateCall(restarted, "SetState", IslandState.Card, true, IslandPageIds.System);
                PrivateCall(restarted, "ExitApplication");
                Check(SettingsRegistry.GetText(persisted, "LastOpenedCardPage", "") == IslandPageIds.System,
                    "直接退出程序也记录当前页面");
            }
            finally { restarted.Close(); }
            PrivateCall(window, "OnSystemToastsReceived", new[] {
                new DesktopNotification("restore-toast", "regression.app", "页面回归", "通知标题", "正文", DateTimeOffset.Now) }, false);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill,
                "自动通知仍以小岛显示，不被普通页面恢复扩大");
            Click(); Check(At(IslandPageIds.Notifications), "点击自动通知仍进入通知详情页面");
            PrivateCall(window, "ClearDesktopInbox");
        }
        finally
        {
            PrivateCall(window, "CollapseToBar", true);
            options.Extras = savedExtras; SetAgentField(window, "_smtc", oldSmtc); SetAgentField(window, "_mediaSourceForeground", oldForeground);
            PrivateCall(window, "SwitchCardPage", pages.IndexOfOrDefault(oldPage), false);
        }
    }
}
