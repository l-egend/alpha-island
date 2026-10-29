using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AlphaIsland.PluginSdk;

/// <summary>A shared, theme-coloured vector glyph for host and plugin controls. No idle clocks.</summary>
public sealed partial class GlyphIcon : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(GlyphIcon),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnGlyphChanged));
    // Retain the small amount of TextBlock API used by existing icon bindings/templates.
    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(nameof(TextAlignment), typeof(TextAlignment), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(TextAlignment.Center));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public TextAlignment TextAlignment { get => (TextAlignment)GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }
    private Brush? _ink;
    private Pen? _pen;
    public GlyphIcon() { Focusable = false; IsHitTestVisible = false; FontSize = 16; InitializeDeletionMotion(); }
    protected override Size MeasureOverride(Size available) => new(double.IsNaN(Width) ? Math.Max(12, FontSize) : Width,
        double.IsNaN(Height) ? Math.Max(12, FontSize) : Height);
    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0 || Text.Length == 0) return;
        Brush ink = Foreground ?? Brushes.White;
        if (_pen == null || !ReferenceEquals(ink, _ink))
        {
            _ink = ink; _pen = GlyphArtwork.CreatePen(ink);
        }
        dc.PushTransform(new TranslateTransform((ActualWidth-size)/2, (ActualHeight-size)/2));
        dc.PushTransform(new ScaleTransform(size/32, size/32));
        if (Text == "\uE74D") GlyphArtwork.DrawDeletion(dc, _pen, DeletionProgress);
        else if (!GlyphArtwork.TryDraw(dc, Text, ink, _pen))
        {
            // Third-party plugins can still supply their own font glyphs.
            var text = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), 26, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point((32-text.Width)/2, (32-text.Height)/2));
        }
        dc.Pop(); dc.Pop();
    }
}
