using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using alpha_island.Core;
using alpha_island.Plugins;

namespace alpha_island;

public partial class MainWindow
{
    private PluginHost? _plugins;
    private void BuildPluginHost()
    {
        _plugins = new PluginHost(Dispatcher, _options,
            new[] { Path.Combine(AppContext.BaseDirectory, "plugins"), AppPaths.UserPluginsDir },
            page => { CardViewport.Children.Add(page.View); _pages.Register(page); },
            page =>
            {
                RemoveCardPinLayer(page.Id);
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(page.View,UIElement.OpacityProperty, null);
                if (page.View.RenderTransform is TranslateTransform transform) AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(transform,TranslateTransform.YProperty, null);
                _pages.Unregister(page.Id); CardViewport.Children.Remove(page.View);
            }, SelectComponent);
        _plugins.ToolDispatcher = _desktopTools;
        _plugins.DeviceBatteriesChanged += SyncDeviceBatteryCard;
        _plugins.ContextReader = ReadDesktopContext;
        _plugins.AllowPluginTools = SettingsRegistry.GetBool(_options, "AgentPluginToolsEnabled", false);
        _plugins.PageNotificationRequested += ShowPluginPageNotification;
        _plugins.ConfigurationChanged += () =>
        {
            _settingsWindow?.SynchronizePluginConfiguration(_options);
            if (_runtimeEnabled) { bool saved = _options.TrySave(allowRecovery: true); _settingsWindow?.ReportSettingsSaved(saved); }
        };
        _plugins.AvailabilityChanged += () =>
        {
            _pages.Sync(_options);
            if (_pages.IndexOf(VoiceChatPage) < 0 && _voiceAssistant?.Active == true) _ = _voiceAssistant.CancelAsync();
            ConfigureVoiceAssistant();
            foreach (string id in _cardPinViews.Values.Where(v => _pages.IndexOf(v.PageId) < 0).Select(v => v.PageId).ToArray()) RemoveCardPinLayer(id);
            SwitchCardPage(Math.Max(0, _pages.ActiveIndex), animated: false);
            SyncCardPageVisuals();
            _tray?.SetPluginActions(_plugins.AvailableActions);
        };
        _plugins.SettingsRequested += () => { ShowSettings(); _settingsWindow?.OpenPluginSettings(); };
        _plugins.Changed += () =>
        {
            SyncDeviceBatteryCard();
            _pages.Sync(_options);
            SwitchCardPage(Math.Max(0, _pages.ActiveIndex), animated: false);
            SyncCardPageVisuals();
            _tray?.SetPluginActions(_plugins.AvailableActions);
        };
        if (_runtimeEnabled) _plugins.Discover();
    }

    private void OpenAiConversation() => _plugins?.InvokeAction("api-chat-open");
    private void OpenAiHistory() => _plugins?.InvokeAction("api-chat-history");
    private string AgentActivityPage => AgentPage(IsAgentIslandKey(_activeMainIslandKey) ? _activeMainIslandKey : AgentTickerKey);

    private void ShowPluginPageNotification(string pageId, string title, string detail, double seconds)
    {
        string? key = EnsureCardPinLayer(pageId);
        if (key == null) return;
        var view = _cardPinViews[key];
        view.NoticeTitle = title; view.NoticeDetail = detail;
        view.NoticeUntilUtc = DateTime.UtcNow.AddSeconds(seconds);
        UpdateCardPinSummary(pageId);
        ShowPillNotification(IslandNotification.Seconds(key, seconds) with { Title = title, Detail = detail });
    }
}
