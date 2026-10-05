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
                page.View.BeginAnimation(UIElement.OpacityProperty, null);
                if (page.View.RenderTransform is TranslateTransform transform) transform.BeginAnimation(TranslateTransform.YProperty, null);
                _pages.Unregister(page.Id); CardViewport.Children.Remove(page.View);
            }, SelectComponent);
        _plugins.PageNotificationRequested += ShowPluginPageNotification;
        _plugins.Changed += () =>
        {
            SwitchCardPage(Math.Max(0, _pages.ActiveIndex), animated: false);
            SyncCardPageVisuals();
        };
        if (_runtimeEnabled) _plugins.Discover();
    }

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
