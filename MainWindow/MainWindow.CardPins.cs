using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Core;
using alpha_island.Controls;
using alpha_island.Plugins;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using FontFamily = System.Windows.Media.FontFamily;
using Panel = System.Windows.Controls.Panel;

namespace alpha_island;

public partial class MainWindow
{
    private sealed record CardPinView(string PageId, Grid Root, TextBlock Title, TextBlock Detail, SemanticIcon Icon, TextBlock? Value)
    {
        internal string NoticeTitle = "", NoticeDetail = "";
        internal string MotionDetail = "";
        internal DateTime NoticeUntilUtc;
    }
    private readonly Dictionary<string, CardPinView> _cardPinViews = new(StringComparer.Ordinal);
    private static string? CardPageFromPinKey(string key) => key.StartsWith("card/", StringComparison.Ordinal) ? key[5..] : null;

    private string? EnsureCardPinLayer(string pageId)
    {
        var page = _pages.Pages.FirstOrDefault(p => p.Id == pageId);
        if (page == null) return null;
        string key = "card/" + page.Id;
        if (!_cardPinViews.ContainsKey(key))
        {
            var root = new Grid { Width = 328, Height = 32, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            var glyph = new SemanticIcon { Width = 28, Height = 28, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 21, Foreground = ResolveAccentBrush(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            alpha_island.Animation.ActionIconMotion.SetKind(glyph, alpha_island.Animation.ActionIconAnimation.Pulse);
            root.Children.Add(glyph);
            var text = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
            var detail = new TextBlock { FontSize = 10, Foreground = System.Windows.Media.Brushes.White, Opacity = .75, TextTrimming = TextTrimming.CharacterEllipsis };
            text.Children.Add(title); text.Children.Add(detail); Grid.SetColumn(text, 1); root.Children.Add(text);
            TextBlock? value = null;
            if (pageId == "focus")
            {
                root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                title.FontSize = 13;
                value = new TextBlock
                {
                    Name = "FocusPinTime", FontSize = 26, FontWeight = FontWeights.SemiBold,
                    Foreground = System.Windows.Media.Brushes.White, VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 0, 0)
                };
                Grid.SetColumn(value, 2); root.Children.Add(value);
            }
            _cardPinViews.Add(key, new(page.Id, root, title, detail, glyph, value));
            ((Panel)NetSpeedNotifyLayer.Parent).Children.Add(root);
            _layers.Register(new IslandLayerSpec(key, root, Priority: 44));
        }
        UpdateCardPinSummary(pageId);
        return key;
    }
    private string CardPinGlyph(string pageId) => pageId switch
    {
        "focus" => "\uE916", "weather" => "\uE9BD", "screen-tools" => AlphaIsland.PluginSdk.GlyphArtwork.ScreenToolsGlyph,
        "productivity" => AlphaIsland.PluginSdk.GlyphArtwork.ProductivityGlyph,
        _ => (_pages.Pages.FirstOrDefault(p => p.Id == pageId) as PluginIslandPage)?.Glyph ?? "\uE700"
    };
    private string? CardPinDisplayName(string key) => _cardPinViews.TryGetValue(key, out var view) ? view.Title.Text : null;
    private void UpdateCardPinSummary(string pageId)
    {
        if (!_cardPinViews.TryGetValue("card/" + pageId, out var view)) return;
        view.Title.Text = _pages.Pages.FirstOrDefault(p => p.Id == pageId)?.Title ?? view.Title.Text;
        int? weatherCode = pageId == "weather" ? _weatherSnapshot?.Code : null;
        bool weatherChanged = pageId == "weather" && view.Icon.WeatherCode != weatherCode;
        view.Icon.Text = CardPinGlyph(pageId);
        view.Icon.Shape = pageId switch
        {
            "focus" => IconShape.Timer,
            "weather" => IconShape.Weather,
            _ => IconShape.Auto,
        };
        view.Icon.Active = pageId != "focus" || _pomodoro.Running;
        view.Icon.WeatherCode = weatherCode;
        if (weatherChanged)
        {
            alpha_island.Animation.ActionIconMotion.Stop(view.Icon);
            alpha_island.Animation.ActionIconMotion.Play(view.Icon);
            if (_satelliteOrbViews.TryGetValue("card/weather", out var weatherOrb))
                UpdateSatelliteOrbVisual("card/weather", weatherOrb);
        }
        string detail = pageId switch
        {
            "focus" => (_pomodoro.CycleFinished ? L.T("循环完成") : _pomodoro.IsLongBreak ? L.T("长休息") : _pomodoro.IsBreak ? L.T("短休息") : L.T("专注"))
                + (_pomodoro.Running ? "" : L.T(" · 已暂停")),
            "weather" => _weatherValue?.Text ?? L.T("点击查看天气"),
            "screen-tools" => L.T("识字 · 取色 · 翻译 · 字幕 · 扫码"),
            _ => (_pages.Pages.FirstOrDefault(p => p.Id == pageId) as PluginIslandPage)?.ReadSummary() ?? L.T("点击打开卡片")
        };
        if (view.MotionDetail != detail && pageId == "focus")
        {
            alpha_island.Animation.ActionIconMotion.Stop(view.Icon);
            alpha_island.Animation.ActionIconMotion.Play(view.Icon);
            if (_satelliteOrbViews.TryGetValue("card/focus", out var focusOrb) && focusOrb.Child is Grid focusGrid)
            {
                var focusIcon = focusGrid.Children.OfType<SemanticIcon>().FirstOrDefault(t => t.Name == "OrbGlyph");
                if (focusIcon != null)
                {
                    focusIcon.Active = _pomodoro.Running;
                    alpha_island.Animation.ActionIconMotion.Play(focusIcon);
                }
            }
        }
        view.MotionDetail = detail;
        view.Detail.Text = detail;
        if (view.Value != null)
        {
            view.Value.Text = _pomodoro.DisplayRemaining();
            if (_satelliteOrbViews.TryGetValue("card/focus", out var orb))
            {
                bool resized = orb.Width != FocusSatelliteWidth;
                UpdateSatelliteOrbVisual("card/focus", orb);
                if (resized) LayoutSatelliteRail();
            }
        }
        if (DateTime.UtcNow < view.NoticeUntilUtc)
        {
            view.Title.Text = view.NoticeTitle;
            view.Detail.Text = view.NoticeDetail;
        }
        if (_sideBubbleComponentKey == "card/" + pageId) UpdateCompactIslandContent(_sideBubbleComponentKey);
    }
    private void RemoveCardPinLayer(string pageId)
    {
        string key = "card/" + pageId;
        if (!_cardPinViews.TryGetValue(key, out var view)) return;
        if (_layers.IsPinned(key)) SetLayerPinned(key, false);
        _layers.Unregister(key);
        ((Panel)view.Root.Parent).Children.Remove(view.Root);
        _cardPinViews.Remove(key); _activeSatelliteOrder.Remove(key);
        if (_satelliteOrbViews.Remove(key, out var orb))
        {
            ((Panel)orb.Parent).Children.Remove(orb);
            _satellites.Unregister("orb-" + key);
        }
        if (_activeMainIslandKey == key) _activeMainIslandKey = "music";
        SyncNotificationLayers(); SyncSatelliteOrbs();
    }
}
