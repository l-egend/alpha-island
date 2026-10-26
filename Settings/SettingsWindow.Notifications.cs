using System;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    public event EventHandler? NotificationCenterRequested;
    private void AddNotificationSettings()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "通知与事件", Style = (Style)FindResource("W11CardTitle"), Margin = new Thickness(0, 26, 0, 12) });
        var group = new StackPanel();
        foreach (var descriptor in SettingsRegistry.All.Where(x => x.Key != "NotificationSoundPath" && (x.Key.StartsWith("Notification", StringComparison.Ordinal)
            || x.Key is "SystemToastEnabled" or "SessionEventNotifyEnabled" or "CpuMonitorEnabled" or "BluetoothLowBatteryEnabled" or "DeviceBatteryFullEnabled" or "DeviceBatteryChargingEnabled")))
        {
            var row = new Grid { Style = (Style)FindResource("W11Row") };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = descriptor.Label, Style = (Style)FindResource("W11RowLabel") });
            if (!string.IsNullOrWhiteSpace(descriptor.Hint))
                text.Children.Add(new TextBlock { Text = descriptor.Hint, Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap, MaxWidth = 370, HorizontalAlignment = HorizontalAlignment.Left });
            row.Children.Add(text);
            FrameworkElement control;
            if (descriptor.Kind == SettingKind.Bool)
            {
                var toggle = new CheckBox { Style = (Style)FindResource("W11Switch"), VerticalAlignment = VerticalAlignment.Center };
                toggle.Checked += Setting_Changed; toggle.Unchecked += Setting_Changed; control = toggle;
            }
            else if (descriptor.Kind == SettingKind.Int)
            {
                var slider = new Slider { Style = (Style)FindResource("W11Slider"), Minimum = descriptor.Min, Maximum = descriptor.Max, Width = 100, IsSnapToTickEnabled = true, TickFrequency = 1 };
                slider.ValueChanged += (s, e) => Setting_Changed(s, e); control = slider;
                var label = new TextBlock { Name = descriptor.ValueTextName, Style = (Style)FindResource("W11RowHint"), HorizontalAlignment = HorizontalAlignment.Right };
                RegisterName(label.Name, label); text.Children.Add(label);
            }
            else
            {
                var box = new TextBox { Style = (Style)FindResource("W11TextBox"), Width = 180, MinHeight = 27, VerticalAlignment = VerticalAlignment.Center };
                box.TextChanged += (s, e) => Setting_Changed(s, e); control = box;
            }
            control.Name = descriptor.ControlName; RegisterName(control.Name, control); Grid.SetColumn(control, 1); row.Children.Add(control);
            if (descriptor.PageId == SettingsPageIds.System)
            {
                Page_System.Children.Insert(1, new TextBlock { Text = "处理器", Style = (Style)FindResource("W11CardTitle"), Margin = new Thickness(0, 26, 0, 12) });
                Page_System.Children.Insert(2, SettingCard(row));
            }
            else
            {
                group.Children.Add(SettingCard(row));
            }
        }
        AddNotificationSoundSettings(group);
        var open = new Button { Style = (Style)FindResource("W11Button"), Content = "通知中心与权限", Margin = new Thickness(14, 12, 14, 12), Padding = new Thickness(10, 6, 10, 6), HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) => NotificationCenterRequested?.Invoke(this, EventArgs.Empty); group.Children.Add(open);
        panel.Children.Add(group);
        Page_Notifications.Children.Insert(1, panel);
    }
}
