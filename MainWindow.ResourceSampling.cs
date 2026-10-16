using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.ViewModels;
using Binding = System.Windows.Data.Binding;

namespace alpha_island;

public partial class MainWindow
{
    private ResourceSamplingProfile? _lastResourceProfile;
    private bool _syncingResourceSampling;

    private void BindResourceState()
    {
        var values = _resourceMonitor.Values;
        void Bind(TextBlock view, string property) => view.SetBinding(TextBlock.TextProperty,
            new Binding(property) { Source = values, Mode = BindingMode.OneWay });
        Bind(SysCardCpuValue, nameof(SystemResourcesViewModel.CpuText));
        Bind(SysCardMemValue, nameof(SystemResourcesViewModel.MemoryText));
        Bind(SysCardMemDetail, nameof(SystemResourcesViewModel.MemoryDetail));
        Bind(SysCardPowerValue, nameof(SystemResourcesViewModel.PowerText));
        Bind(SysCardPowerDetail, nameof(SystemResourcesViewModel.PowerDetail));
        SysCardPowerDetail.SetBinding(FrameworkElement.ToolTipProperty,
            new Binding(nameof(SystemResourcesViewModel.PowerDescription)) { Source = values });
    }

    private void SyncResourceSampling()
    {
        if (_resourceMonitor == null || _exiting || _syncingResourceSampling) return;
        _syncingResourceSampling = true;
        try
        {
            bool SatelliteVisible(string key) => _satelliteOrbViews.TryGetValue(key, out var orb)
                && orb.IsVisible && orb.Opacity > 0.05;
            bool resources = IsSystemCardVisible || (_settingsWindow?.IsVisible == true)
                || (_state != IslandState.Bar && (_activeMainIslandKey == "status"
                    || SatelliteVisible("status") || _sideBubbleComponentKey == "status"));
            bool network = IsNetSpeedCardVisible || _barSpeedVisible
                || (_state != IslandState.Bar && (_activeMainIslandKey == "netspeed" || SatelliteVisible("netspeed")))
                || SettingsRegistry.GetBool(_options, "NetSpeedNotifyEnabled", false);
            var profile = ResourceSamplingPolicy.For(resources, network,
                IsLoaded && (!IsVisible || WindowState == WindowState.Minimized),
                GameDoNotDisturbActive, MotionPolicy.PowerSave);
            _resourceMonitor.ApplyProfile(profile);
            if (_ticker != null)
            {
                _ticker.SetInterval("netspeed", profile.Network);
                // A slow background window must not be presented as sixty one-second points.
                if (_lastResourceProfile is { } previous && profile.Network < previous.Network)
                {
                    ClearNetSpeedHistory();
                }
            }
            _lastResourceProfile = profile;
        }
        finally { _syncingResourceSampling = false; }
    }
}
