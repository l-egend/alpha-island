using System.Windows;
using System.Windows.Controls;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    private Border SettingCard(FrameworkElement content) => new()
    {
        Style = (Style)FindResource("W11SettingCard"),
        Child = content
    };
}
