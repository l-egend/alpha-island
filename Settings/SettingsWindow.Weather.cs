using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using alpha_island.Core;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Binding = System.Windows.Data.Binding;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    private FrameworkElement _weatherSettingsPanel = null!;
    private void AddWeatherSettings()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "天气与提醒", Style = (Style)FindResource("W11CardTitle"), Margin = new Thickness(0, 24, 0, 12) });
        var group = new StackPanel();
        foreach (var descriptor in SettingsRegistry.All.Where(x => x.Key.StartsWith("Weather", StringComparison.Ordinal) && x.ControlName.Length > 0))
        {
            var row = new Grid { Style = (Style)FindResource("W11Row") };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = descriptor.Label, Style = (Style)FindResource("W11RowLabel") });
            text.Children.Add(new TextBlock { Text = descriptor.Hint, Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Left, TextAlignment = TextAlignment.Left }); row.Children.Add(text);
            FrameworkElement control, controls;
            if (descriptor.Kind == SettingKind.Bool)
            {
                var toggle = new CheckBox { Style = (Style)FindResource("W11Switch"), VerticalAlignment = VerticalAlignment.Center };
                toggle.Checked += Setting_Changed; toggle.Unchecked += Setting_Changed; control = controls = toggle;
            }
            else
            {
                var box = new TextBox { Style = (Style)FindResource("W11TextBox"), Width = 54, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
                box.TextChanged += (s, e) => Setting_Changed(s, e);
                var strip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                foreach (int delta in new[] { -1, 1 })
                {
                    var step = new Button { Content = delta < 0 ? "−" : "+", Style = (Style)FindResource("W11Button"), MinWidth = 26, Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(3, 0, 3, 0) };
                    step.Click += (_, _) => box.Text = ((int)Math.Clamp(SettingDescriptor.ToDouble(box.Text,
                        Convert.ToDouble(descriptor.DefaultValue, CultureInfo.InvariantCulture)) + delta, descriptor.Min, descriptor.Max)).ToString(CultureInfo.InvariantCulture);
                    if (delta < 0) strip.Children.Add(step); else { strip.Children.Add(box); strip.Children.Add(step); }
                }
                strip.Children.Add(new TextBlock { Text = descriptor.Unit, Style = (Style)FindResource("W11RowHint"), VerticalAlignment = VerticalAlignment.Center });
                control = box; controls = strip;
            }
            control.Name = descriptor.ControlName; RegisterName(control.Name, control);
            if (SettingsRegistry.Find(descriptor.EnabledByKey) is { } source)
                row.SetBinding(IsEnabledProperty, new Binding("IsChecked") { Source = FindName(source.ControlName), TargetNullValue = false });
            Grid.SetColumn(controls, 1); row.Children.Add(controls);
            group.Children.Add(SettingCard(row));
        }
        panel.Children.Add(group);
        _weatherSettingsPanel = panel; Page_Behavior.Children.Add(panel);
    }
    internal void OpenWeatherSettings()
    {
        SelectPageByTag(SettingsPageIds.Behavior);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            _weatherSettingsPanel.BringIntoView(new Rect(0, 0, Math.Max(1, _weatherSettingsPanel.ActualWidth), 40))));
    }
}
