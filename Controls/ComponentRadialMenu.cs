using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using alpha_island.Animation;
using alpha_island.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace alpha_island.Controls;

internal sealed class ComponentRadialMenu : Canvas
{
    private readonly List<(string Id, string Title, Border Circle, AlphaIsland.PluginSdk.GlyphIcon Icon)> _items = new();
    private readonly TextBlock _caption = new()
    { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 104 };
    private readonly Border _label;
    private readonly Brush _accent;
    private string _activeId = "";
    private bool _layoutInitialized;
    internal ComponentMenuLayout Layout { get; private set; } = ComponentMenuLayout.Create(0);
    internal IReadOnlyList<string> ItemIds => _items.Select(x => x.Id).ToArray();
    internal string CaptionText => _caption.Text;
    internal event Action<string>? Clicked;

    public ComponentRadialMenu(Brush background, Brush accent)
    {
        Background = Brushes.Transparent;
        _accent = accent;
        _label = new Border { Background = new SolidColorBrush(Color.FromArgb(238, 20, 20, 28)), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 5, 8, 5), Child = _caption, IsHitTestVisible = false };
    }

    internal void SetPages(IReadOnlyList<IIslandPage> pages, string activeId, Brush background)
    {
        Children.Clear(); _items.Clear(); _activeId = activeId; _layoutInitialized = false;
        foreach (var page in pages)
        {
            var icon = new AlphaIsland.PluginSdk.GlyphIcon { Text = page is Plugins.PluginIslandPage plugin ? plugin.Glyph : Glyph(page.Id), FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = page.Id == "screen-tools" ? 20 : page.Id == IslandPageIds.NetSpeed ? 20 : 17,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            var circle = new Border { Width = ComponentMenuLayout.Diameter, Height = ComponentMenuLayout.Diameter,
                CornerRadius = new CornerRadius(ComponentMenuLayout.Diameter / 2), Background = background,
                BorderThickness = new Thickness(1.2), Child = icon, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = page.Title };
            string id = page.Id;
            circle.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (ItemAt(e.GetPosition(this)) is { } visibleId) Clicked?.Invoke(visibleId);
            };
            System.Windows.Automation.AutomationProperties.SetName(circle, page.Title);
            _items.Add((id, page.Title, circle, icon)); Children.Add(circle);
        }
        Children.Add(_label); ArrangeArc(false, false); Highlight(null);
    }

    internal void ArrangeArc(bool left, bool up, double contourHeight = 48, double cornerRadius = 24, double verticalBias = 0)
    {
        var next = ComponentMenuLayout.Create(_items.Count, left, up, contourHeight, cornerRadius, verticalBias);
        if (_layoutInitialized && Layout.Left == next.Left && Layout.Up == next.Up && Layout.Size == next.Size && Layout.Anchor == next.Anchor
            && Layout.Centers.SequenceEqual(next.Centers)) return;
        Layout = next;
        _layoutInitialized = true;
        Width = Layout.Size.Width; Height = Layout.Size.Height;
        for (int i = 0; i < _items.Count; i++)
        {
            SetLeft(_items[i].Circle, Layout.Centers[i].X - ComponentMenuLayout.Diameter / 2);
            SetTop(_items[i].Circle, Layout.Centers[i].Y - ComponentMenuLayout.Diameter / 2);
        }
        // Put the readable name toward the island, leaving the compact icon arcs unobstructed.
        SetLeft(_label, left ? Layout.Anchor.X + 18 : Layout.Anchor.X - ComponentMenuLayout.CaptionSpace - 10);
        SetTop(_label, Layout.Anchor.Y - 12);
    }

    internal string? ItemAt(Point position)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            var circle = _items[i].Circle;
            // RenderTransform 动画可能让排列标记暂时失效，仍应沿用已经绘制的实际位置。
            Point center = circle.RenderSize.Width > 0 ? circle.TranslatePoint(new Point(DiameterHalf, DiameterHalf), this) : Layout.Centers[i];
            double radius = DiameterHalf + 2;
            if (circle.RenderTransform is TransformGroup group && group.Children[0] is ScaleTransform scale) radius *= scale.ScaleX;
            if (circle.Opacity > .25 && (position - center).Length <= radius) return _items[i].Id;
        }
        return null;
    }

    private const double DiameterHalf = ComponentMenuLayout.Diameter / 2;

    internal void Reveal(bool animated = true)
    {
        bool animate = animated && !MotionPolicy.InstantMorph && !MotionPolicy.PowerSave;
        for (int i = 0; i < _items.Count; i++)
        {
            var circle = _items[i].Circle;
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(circle,OpacityProperty, null); circle.Opacity = 1;
            var scale = new ScaleTransform(1, 1);
            var translate = new TranslateTransform();
            var transform = new TransformGroup(); transform.Children.Add(scale); transform.Children.Add(translate);
            circle.RenderTransformOrigin = new Point(.5, .5); circle.RenderTransform = transform;
            if (!animate) continue;
            var delay = TimeSpan.FromMilliseconds(i * 10);
            DoubleAnimationUsingKeyFrames Tween(double from, double to)
            {
                var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
                animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
                animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(180)))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                return animation;
            }
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(circle,OpacityProperty, Tween(0, 1));
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(scale,ScaleTransform.ScaleXProperty, Tween(.65, 1));
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(scale,ScaleTransform.ScaleYProperty, Tween(.65, 1));
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(translate,TranslateTransform.XProperty, Tween((Layout.Anchor.X - Layout.Centers[i].X) * .45, 0));
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(translate,TranslateTransform.YProperty, Tween((Layout.Anchor.Y - Layout.Centers[i].Y) * .45, 0));
        }
        AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(_label,OpacityProperty, null); _label.Opacity = 1;
        if (animate) AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(_label,OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
    }

    internal void Highlight(string? id)
    {
        foreach (var item in _items)
        {
            bool active = item.Id == _activeId, hovered = item.Id == id;
            item.Circle.BorderBrush = active || hovered ? _accent : new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
            item.Circle.BorderThickness = new Thickness(hovered ? 2.5 : active ? 1.8 : 1);
            item.Icon.Foreground = active || hovered ? _accent : Brushes.White;
        }
        _caption.Text = _items.FirstOrDefault(x => x.Id == (id ?? _activeId)).Title ?? "切换组件";
    }

    private static string Glyph(string id) => id switch
    {
        IslandPageIds.Music => "\uE8D6", IslandPageIds.Clipboard => "\uE8C8", IslandPageIds.Volume => "\uE767",
        IslandPageIds.NetSpeed => AlphaIsland.PluginSdk.GlyphArtwork.NetworkTransferGlyph,
        IslandPageIds.Ai => "\uE99A", IslandPageIds.System => "\uE9D9", IslandPageIds.Download => "\uE896",
        IslandPageIds.DeviceBatteries => "\uE83F", IslandPageIds.Notifications => "\uE7E7", "weather" => "\uE706",
        "focus" => "\uE916", "screen-tools" => AlphaIsland.PluginSdk.GlyphArtwork.ScreenToolsGlyph,
        "productivity" => AlphaIsland.PluginSdk.GlyphArtwork.ProductivityGlyph, _ => "\uE80F"
    };
}
