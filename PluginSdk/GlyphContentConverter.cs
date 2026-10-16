using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AlphaIsland.PluginSdk;

/// <summary>Preserves text/element content and renders known glyph-only button content as a vector.</summary>
public sealed class GlyphContentConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string text && GlyphArtwork.Titles.ContainsKey(text) ? new GlyphIcon { Text=text } : value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
