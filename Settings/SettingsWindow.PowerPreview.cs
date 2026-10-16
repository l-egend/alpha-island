using System;
using System.Windows;
using alpha_island.Services;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    // Temporary preview commands; no preference or simulated battery state is persisted.
    public event EventHandler<BatteryPowerEvent>? BatteryAnimationPreviewRequested;
    public event EventHandler? ChargingIslandPreviewRequested;

    private void PreviewChargingIsland_Click(object sender, RoutedEventArgs e) =>
        ChargingIslandPreviewRequested?.Invoke(this, EventArgs.Empty);

    private void PreviewCharging_Click(object sender, RoutedEventArgs e) =>
        BatteryAnimationPreviewRequested?.Invoke(this, BatteryPowerEvent.PluggedIn);

    private void PreviewUnplugged_Click(object sender, RoutedEventArgs e) =>
        BatteryAnimationPreviewRequested?.Invoke(this, BatteryPowerEvent.Unplugged);
}
