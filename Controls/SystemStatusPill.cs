using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Core;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using FontFamily = System.Windows.Media.FontFamily;

namespace alpha_island.Controls;

/// <summary>Resource-only two-line status display; network rates have their own view.</summary>
public sealed class SystemStatusPill : Grid
{
    private readonly StackPanel _lines;
    private string[] _selection = ["cpu", "memory", "power"];
    private double? _cpu, _memory, _watts;
    private string _powerDescription = "功耗等待采样";
    internal TextBlock ResourceText { get; }
    internal TextBlock PowerText { get; }
    internal string[] DisplayedItems => _selection.ToArray();

    public SystemStatusPill() : this(false) { }
    public SystemStatusPill(bool compact)
    {
        Height = compact ? 36 : 34;
        Margin = new Thickness(compact ? 10 : 14, 0, compact ? 10 : 14, 0);
        _lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        ResourceText = Line(compact); PowerText = Line(compact);
        Children.Add(_lines); Refresh();
    }

    private static TextBlock Line(bool compact) => new()
    {
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), FontSize = compact ? 10.5 : 12.5,
        FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
        TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
    };

    internal void SetDisplayItems(IEnumerable<string> selection)
    {
        _selection = selection.Where(key => key is "cpu" or "memory" or "power").Distinct().ToArray();
        Refresh();
    }

    internal void SetResources(double? cpu, double? memory)
    { _cpu = cpu; _memory = memory; Refresh(); }

    internal void SetPower(double? watts, string description)
    { _watts = watts; _powerDescription = description; Refresh(); }

    private void Refresh()
    {
        ResourceText.Text = string.Join("  ·  ", _selection.Where(key => key != "power")
            .Select(key => (key == "cpu" ? "CPU " : "RAM ") + Percent(key == "cpu" ? _cpu : _memory)));
        PowerText.Text = "CPU＋GPU 功耗 " + (_watts is double watts && double.IsFinite(watts) ? $"{watts:0.0} W" : "— W");
        ResourceText.Visibility = ResourceText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PowerText.Visibility = _selection.Contains("power") ? Visibility.Visible : Visibility.Collapsed;
        bool powerFirst = _selection.Length > 0 && _selection[0] == "power";
        var first = powerFirst ? PowerText : ResourceText;
        if (_lines.Children.Count != 2 || !ReferenceEquals(_lines.Children[0], first))
        {
            _lines.Children.Clear(); _lines.Children.Add(first);
            _lines.Children.Add(powerFirst ? ResourceText : PowerText);
        }
        if (_selection.Length == 0) { ResourceText.Text = "系统状态"; ResourceText.Visibility = Visibility.Visible; }
        ToolTip = _powerDescription;
    }

    private static string Percent(double? value) => value is double number && double.IsFinite(number) ? $"{number:0}%" : "—";
}
