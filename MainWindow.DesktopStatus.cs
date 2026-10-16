using alpha_island.Animation;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using AlphaIsland.Hardware;
using Panel = System.Windows.Controls.Panel;

namespace alpha_island;

public partial class MainWindow
{
    private readonly SystemResourceMonitor _resourceMonitor;
    private SystemResourceSnapshot _resourceSnapshot => _resourceMonitor.Values.Snapshot;
    private double StatusPillWidth => StatusDisplayOptions.Width(StatusDisplayOptions.NetworkSelected(_options).Length);
    private double StatusSatelliteWidth => StatusDisplayOptions.Width(StatusDisplayOptions.NetworkSelected(_options).Length, compact: true);
    private void StartHardwarePowerService() => _resourceMonitor.SetPowerEnabled(true);
    private void StopHardwarePowerService() => _resourceMonitor.SetPowerEnabled(false);
    private double ResourcePillWidth => StatusDisplayOptions.ResourceWidth(_options);
    private double ResourceSatelliteWidth => StatusDisplayOptions.ResourceWidth(_options, compact: true);
    private void OnStatusDisplayOptionsChanged()
    {
        NetSpeedNotifyLayer.Width = StatusPillWidth - 8;
        ResourceStatusNotifyLayer.Width = ResourcePillWidth - 8;
        if (_satelliteOrbViews.TryGetValue("status", out var statusOrb)) statusOrb.Width = ResourceSatelliteWidth;
        if (_satelliteOrbViews.TryGetValue("netspeed", out var orb)) orb.Width = StatusSatelliteWidth;
        RefreshCombinedResources();
        if (_sideNetworkView != null) UpdateCombinedStatus(_sideNetworkView, CurrentNetworkSample);
        if (_sideBubbleComponentKey is "netspeed" or "status") UpdateCompactIslandContent(_sideBubbleComponentKey);
        UpdatePillLyricScroll();
        TryMorphPillWidth();
        SyncSatelliteOrbs();
    }
    private void StartCpuService() => _resourceMonitor.SetCpuEnabled(true);
    private void StopCpuService() => _resourceMonitor.SetCpuEnabled(false);
    private void RefreshCombinedResources()
    {
        UpdateNetSpeedPrimary(CurrentNetworkSample);
        UpdateNetSpeedSatelliteVisual(CurrentNetworkSample);
        _resourceMonitor.Refresh();
    }
    private void UpdateResourceSnapshot(SystemResourceSnapshot snapshot)
    {
        _resourceMonitor.Values.Update(snapshot);
        UpdateResourceStatus(ResourceStatusRates);
        if (_sideStatusView != null) UpdateResourceStatus(_sideStatusView);
        if (_satelliteOrbViews.TryGetValue("status", out var orb) && orb.Child is Grid grid
            && grid.Children.OfType<SystemStatusPill>().FirstOrDefault() is { } view) UpdateResourceStatus(view);
        if (IsSystemCardVisible) SyncSystemCard();
    }
    private void UpdateResourceStatus(SystemStatusPill view)
    {
        view.SetDisplayItems(StatusDisplayOptions.ResourcesSelected(_options));
        view.SetResources(_resourceSnapshot.CpuPercent, _resourceSnapshot.MemoryPercent);
        view.SetPower(_resourceSnapshot.TotalWatts, _resourceSnapshot.PowerDetail ?? "等待功耗采样");
    }
    private void UpdateCombinedStatus(NetworkSpeedPill view, NetworkSpeedSample sample)
    {
        view.SetDisplayItems(StatusDisplayOptions.NetworkSelected(_options));
        if (_netSpeed == null && !_externalNetwork.Active) view.SetUnavailable();
        else
        {
            view.SetRates(sample.UpBytesPerSecond, sample.DownBytesPerSecond);
            view.SetSessionInfo(_externalNetwork.Active && _externalNetwork.Reading!.UploadCounterBytes == null ? null : NetworkSessionUp,
                _externalNetwork.Active && _externalNetwork.Reading!.DownloadCounterBytes == null ? null : NetworkSessionDown,
                NetworkPeakUp, NetworkPeakDown, _netSpeedDownHistory);
        }
    }
    private void RegisterSeparateStatusPages()
    {
        _pages.Register(new DelegateIslandPage(IslandPageIds.NetSpeed, "实时网速", NetSpeedCardLayer, 4,
            244, activated: () => { SyncNetSpeedCard(); ActionIconMotion.Play(NetworkCardIcon); }, viewportChanged: SetNetSpeedCardLayerHeight));
        _pages.Register(new DelegateIslandPage(IslandPageIds.System, "系统状态", SystemCardLayer, 5,
            270, activated: () => { RefreshCombinedResources(); SyncSystemCard(); }, viewportChanged: SetSystemCardLayerHeight));
    }
}
