using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AlphaIsland.PluginSdk;
using alpha_island.Core;

namespace alpha_island.Plugins;

internal sealed class PluginIslandPage : IIslandPage
{
    private readonly PluginPage _page;
    private readonly Action<Exception> _failed;
    internal PluginIslandPage(string owner, PluginPage page, Action<Exception> failed)
    {
        _page = page; _failed = failed; Id = $"plugin:{owner}/{page.Key}";
        var view = page.CreateView() ?? throw new InvalidOperationException("插件页面工厂返回空视图");
        if (view.Parent != null) throw new InvalidOperationException("插件页面视图已属于其它容器");
        var container = new Grid { RenderTransform = new TranslateTransform(), Height = page.Height - 20,
            VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        container.Children.Add(view); View = container;
    }
    public string Id { get; }
    public string Title => _page.Title;
    public string Glyph => _page.Glyph;
    internal string? ReadSummary()
    {
        try { return _page.Summary?.Invoke(); }
        catch (Exception ex) { _failed(ex); return null; }
    }
    public int Order => _page.Order;
    public double PreferredHeight => _page.Height;
    public UIElement View { get; }
    public bool IsAvailable(IslandOptions options)
    {
        try { return _page.Available?.Invoke() ?? true; }
        catch (Exception ex) { _failed(ex); return false; }
    }
    public void OnActivated() => Invoke(_page.Activated);
    public void OnDeactivated() => Invoke(_page.Deactivated);
    public void OnViewportHeightChanged(double height) { if (View is FrameworkElement element) element.Height = height; }
    private void Invoke(Action? callback)
    { try { callback?.Invoke(); } catch (Exception ex) { _failed(ex); } }
}
