using System.ComponentModel;
using System.Windows;
using System.Windows.Documents;
using alpha_island.Controls;
using alpha_island.Services;

namespace alpha_island;

public partial class MainWindow
{
    private GreetingPortraitAdorner? _greetingPortrait;
    private AdornerLayer? _greetingPortraitLayer;
    private DependencyPropertyDescriptor? _greetingOpacityObserver;

    private void InitializeGreetingPortrait()
    {
        GreetingCustomImage.Loaded += (_, _) => SyncGreetingPortrait();
        GreetingCustomImage.Unloaded += (_, _) => RemoveGreetingPortrait();
        GreetingCustomImage.IsVisibleChanged += (_, _) => SyncGreetingPortrait();
        _greetingOpacityObserver = DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(UIElement));
        _greetingOpacityObserver.AddValueChanged(GreetingLayer, OnGreetingPortraitChanged);
        _greetingOpacityObserver.AddValueChanged(IslandBody, OnGreetingPortraitChanged);
    }

    private void OnGreetingPortraitChanged(object? sender, EventArgs e) => SyncGreetingPortrait();

    private void SyncGreetingPortrait()
    {
        bool enabled = GreetingPresetCatalog.IsBuiltInAnimation(_greetingImagePath);
        GreetingCustomImage.Width = enabled ? 48 : 32;
        if (!enabled || !_greetingPresentationVisible || !GreetingCustomImage.IsLoaded || !GreetingCustomImage.IsVisible)
        { RemoveGreetingPortrait(); return; }
        if (_greetingPortrait == null && AdornerLayer.GetAdornerLayer(GreetingCustomImage) is { } layer)
        {
            _greetingPortraitLayer = layer;
            var viewbox = _greetingImagePath switch
            {
                GreetingPresetCatalog.AmiyaMedia => new Rect(12d / 128, 0, 104d / 128, 90d / 128),
                GreetingPresetCatalog.HoshinoSwimsuitMedia => new Rect(0, 0, 1, 1),
                _ => new Rect(24d / 128, 0, 80d / 128, 70d / 128)
            };
            _greetingPortrait = new GreetingPortraitAdorner(GreetingCustomImage, IslandBody, () => GreetingLayer.Opacity * IslandBody.Opacity, viewbox)
            { IsHitTestVisible = false, Focusable = false, ClipToBounds = false };
            layer.Add(_greetingPortrait);
        }
        GreetingCustomImage.Opacity = _greetingPortrait != null ? 0 : 1;
        if (_greetingPortrait != null)
        {
            _greetingPortrait.Visibility = GreetingLayer.Opacity * IslandBody.Opacity > .001 ? Visibility.Visible : Visibility.Collapsed;
            _greetingPortrait.InvalidateVisual();
        }
    }

    private void RemoveGreetingPortrait()
    {
        if (_greetingPortrait != null) _greetingPortraitLayer?.Remove(_greetingPortrait);
        _greetingPortrait = null; _greetingPortraitLayer = null;
        GreetingCustomImage.Opacity = 1;
    }

    private void DisposeGreetingPortrait()
    {
        _greetingOpacityObserver?.RemoveValueChanged(GreetingLayer, OnGreetingPortraitChanged);
        _greetingOpacityObserver?.RemoveValueChanged(IslandBody, OnGreetingPortraitChanged);
        _greetingOpacityObserver = null; RemoveGreetingPortrait();
    }
}
