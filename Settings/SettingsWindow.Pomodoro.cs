using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using alpha_island.Core;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Binding = System.Windows.Data.Binding;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    private FrameworkElement _pomodoroSettingsPanel = null!;
    private void AddPomodoroSettings()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };

        var group = new StackPanel();
        foreach (var descriptor in SettingsRegistry.All.Where(x => x.Key.StartsWith("Pomodoro", StringComparison.Ordinal)))
        {
            var row = new Grid { Style = (Style)FindResource("W11Row") };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = descriptor.Label, Style = (Style)FindResource("W11RowLabel") });
            text.Children.Add(new TextBlock { Text = descriptor.Hint, Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Left, TextAlignment = TextAlignment.Left });
            row.Children.Add(text);
            FrameworkElement control, controls;
            if (descriptor.Kind == SettingKind.Bool)
            {
                var toggle = new CheckBox { Style = (Style)FindResource("W11Switch"), VerticalAlignment = VerticalAlignment.Center };
                toggle.Checked += Setting_Changed; toggle.Unchecked += Setting_Changed;
                control = controls = toggle;
            }
            else if (descriptor.Kind == SettingKind.Int)
            {
                var box = new TextBox { Style = (Style)FindResource("W11TextBox"), Width = 54, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
                box.TextChanged += (s, e) => Setting_Changed(s, e);
                var strip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                foreach (int delta in new[] { -1, 1 })
                {
                    var step = new Button { Content = delta < 0 ? "−" : "+", Style = (Style)FindResource("W11Button"), MinWidth = 26, Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(3, 0, 3, 0) };
                    step.Click += (_, _) => box.Text = ((int)Math.Clamp(
                        SettingDescriptor.ToDouble(box.Text, Convert.ToDouble(descriptor.DefaultValue, CultureInfo.InvariantCulture)) + delta,
                        descriptor.Min, descriptor.Max)).ToString(CultureInfo.InvariantCulture);
                    if (delta < 0) strip.Children.Add(step);
                    else { strip.Children.Add(box); strip.Children.Add(step); }
                }
                strip.Children.Add(new TextBlock { Text = descriptor.Unit, Style = (Style)FindResource("W11RowHint"), VerticalAlignment = VerticalAlignment.Center });
                control = box; controls = strip;
            }
            else
            {
                var choice = new ComboBox { ItemsSource = new[] { "提示", "完成", "警告" }, Width = 86, MinHeight = 28, VerticalAlignment = VerticalAlignment.Center };
                choice.SetResourceReference(StyleProperty, typeof(ComboBox));
                choice.SelectionChanged += (s, e) => Setting_Changed(s, e);
                var strip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                strip.Children.Add(choice);
                var preview = new Button { Content = "试听", Style = (Style)FindResource("W11Button"), Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 4, 8, 4) };
                preview.Click += (_, _) => PomodoroSound.Play(choice.SelectedItem as string ?? "提示");
                strip.Children.Add(preview); control = choice; controls = strip;
            }
            control.Name = descriptor.ControlName; RegisterName(control.Name, control);
            string dependency = descriptor.Key == "PomodoroSound" ? "PomodoroPlaySound" : descriptor.EnabledByKey;
            if (SettingsRegistry.Find(dependency) is { } source)
                row.SetBinding(IsEnabledProperty, new Binding("IsChecked") { Source = FindName(source.ControlName), TargetNullValue = false });
            Grid.SetColumn(controls, 1); row.Children.Add(controls);
            group.Children.Add(SettingCard(row));
        }
        panel.Children.Add(group);
        _pomodoroSettingsPanel = panel;
        Page_Focus.Children.Add(panel);
    }
    internal void OpenPomodoroSettings()
    {
        SelectPageByTag(SettingsPageIds.Focus);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            _pomodoroSettingsPanel.BringIntoView(new Rect(0, 0, Math.Max(1, _pomodoroSettingsPanel.ActualWidth), 40))));
    }
}
