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
    private CpuUsageService? _cpu;
    private bool _cpuSampling;
    private TextBlock _cpuCardText = null!;
    private HardwarePowerService? _hardwarePower;
    private bool _hardwarePowerSampling;
    private bool _hardwarePowerElevatedEnabled;
    private string? _hardwarePowerLastReason;
    private double StatusPillWidth => StatusDisplayOptions.Width(StatusDisplayOptions.Selected(_options).Length);
    private double StatusSatelliteWidth => StatusDisplayOptions.Width(StatusDisplayOptions.Selected(_options).Length, compact: true);
    private void StartHardwarePowerService()
    {
        if (_hardwarePower != null) return;
        _hardwarePower = new HardwarePowerService();
        _hardwarePowerElevatedEnabled = false;
        ConfigureHardwarePowerReader();
        _ticker.Register("hardwarepower", TimeSpan.FromSeconds(2), SampleHardwarePower, runImmediately: true);
    }
    private void StopHardwarePowerService()
    {
        _ticker?.Unregister("hardwarepower");
        var service = _hardwarePower; _hardwarePower = null;
        _hardwarePowerElevatedEnabled = false;
        if (service != null) _ = System.Threading.Tasks.Task.Run(service.Dispose);
        RefreshCombinedResources();
    }
    private async void SampleHardwarePower()
    {
        var service = _hardwarePower;
        if (service == null || _hardwarePowerSampling) return;
        _hardwarePowerSampling = true;
        try { await System.Threading.Tasks.Task.Run(service.Sample).ConfigureAwait(false); }
        catch (Exception ex) { IslandLog.Error("HardwarePower", ex); }
        finally
        {
            if (!Dispatcher.HasShutdownStarted)
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                {
                    _hardwarePowerSampling = false;
                    if (_exiting || !ReferenceEquals(_hardwarePower, service)) return;
                    _settingsWindow?.UpdateHardwarePowerState(service.Current);
                    string reason = service.Current.TotalWatts.HasValue ? "CPU＋GPU 功耗传感器已就绪" : service.Current.Reason;
                    if (reason != _hardwarePowerLastReason)
                    {
                        _hardwarePowerLastReason = reason;
                        var reading = service.Current;
                        string detail = reading.TotalWatts is double total ? $"；CPU {reading.CpuWatts:0.0} W · GPU {reading.GpuWatts:0.0} W · 总计 {total:0.0} W；{reading.Source}" : "";
                        IslandLog.Info("HardwarePower", reason + detail);
                    }
                    RefreshCombinedResources();
                }));
        }
    }
    private void OnStatusDisplayOptionsChanged()
    {
        ConfigureHardwarePowerReader();
        NetSpeedNotifyLayer.Width = StatusPillWidth - 8;
        if (_satelliteOrbViews.TryGetValue("netspeed", out var orb)) orb.Width = StatusSatelliteWidth;
        RefreshCombinedResources();
        if (_sideNetworkView != null) UpdateCombinedStatus(_sideNetworkView, CurrentNetworkSample);
        if (_sideBubbleComponentKey == "netspeed") UpdateCompactIslandContent("netspeed");
        UpdatePillLyricScroll();
        TryMorphPillWidth();
        SyncSatelliteOrbs();
    }
    private async void ConfigureHardwarePowerReader()
    {
        var service = _hardwarePower;
        if (service == null) return;
        bool enabled = SettingsRegistry.GetBool(_options, "HardwarePowerElevatedReader", false);
        if (enabled == _hardwarePowerElevatedEnabled) return;
        _hardwarePowerElevatedEnabled = enabled;
        if (!enabled) { service.DisableElevated(); return; }
        try { IslandLog.Info("HardwarePower", await service.EnableElevatedAsync()); }
        catch (Exception ex) { IslandLog.Error("HardwarePower", ex); }
    }
    private async Task<(bool Enabled, string Message)> EnableHardwarePowerFromSettingsAsync()
    {
        try
        {
            await HardwareDriverSetup.InstallAsync();
            var service = _hardwarePower;
            if (service == null) return (false, "请先勾选状态栏中的 CPU＋GPU 功耗");
            string message = await service.EnableElevatedAsync();
            return (service.HasElevatedReading, message);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { return (false, "已取消 Windows 管理员授权，可稍后重试"); }
        catch (Exception ex) { return (false, "硬件功耗启用失败：" + ex.Message); }
    }
    private void StartCpuService()
    {
        if (_cpu != null) return;
        _cpu = new CpuUsageService();
        _ticker.Register("cpu", CpuUsageService.SampleInterval, SampleCpu, runImmediately: true);
        RefreshCombinedResources();
    }
    private void StopCpuService()
    {
        _ticker?.Unregister("cpu");
        var service = _cpu; _cpu = null;
        if (service != null) _ = System.Threading.Tasks.Task.Run(service.Dispose);
        RefreshCombinedResources();
    }
    private async void SampleCpu()
    {
        var service = _cpu;
        if (service == null || _cpuSampling) return;
        _cpuSampling = true;
        try { await System.Threading.Tasks.Task.Run(service.Sample).ConfigureAwait(false); }
        catch (Exception ex) { IslandLog.Error("Cpu", ex); }
        finally
        {
            if (!Dispatcher.HasShutdownStarted)
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                {
                    _cpuSampling = false;
                    if (!_exiting && ReferenceEquals(_cpu, service)) RefreshCombinedResources();
                }));
        }
    }
    private void RefreshCombinedResources()
    {
        UpdateNetSpeedPrimary(CurrentNetworkSample);
        UpdateNetSpeedSatelliteVisual(CurrentNetworkSample);
        if (_cpuCardText != null)
        {
            string cpu = _cpu?.CurrentPercent is double value ? $"{value:0}%" : "未开启 / 等待采样";
            string ram = _memory?.Current.Valid == true ? $"{_memory.Current.UsedPercent:0}%" : "未开启 / 等待采样";
            var power = _hardwarePower?.Current;
            _cpuCardText.Text = $"CPU {cpu}  ·  RAM {ram}\nCPU＋GPU 功耗 " + (power?.TotalWatts is double watts ? $"{watts:0.0} W" : "无法读取 / 未开启");
            _cpuCardText.ToolTip = power?.Description;
        }
    }
    private void UpdateCombinedStatus(NetworkSpeedPill view, NetworkSpeedSample sample)
    {
        view.SetDisplayItems(StatusDisplayOptions.Selected(_options));
        if (_netSpeed == null) view.SetUnavailable();
        else
        {
            view.SetRates(sample.UpBytesPerSecond, sample.DownBytesPerSecond);
            view.SetSessionInfo(_externalNetwork.Active && _externalNetwork.Reading!.UploadCounterBytes == null ? null : NetworkSessionUp,
                _externalNetwork.Active && _externalNetwork.Reading!.DownloadCounterBytes == null ? null : NetworkSessionDown,
                NetworkPeakUp, NetworkPeakDown, _netSpeedDownHistory);
        }
        view.SetResources(_cpu?.CurrentPercent, _memory?.Current.Valid == true ? _memory.Current.UsedPercent : null);
        var power = _hardwarePower?.Current;
        view.SetPower(power?.TotalWatts, power?.Description ?? "硬件功耗监测未开启");
    }
    private void RegisterMergedSystemPage()
    {
        var networkParent = (Panel)NetSpeedCardLayer.Parent; networkParent.Children.Remove(NetSpeedCardLayer);
        ((Panel)SystemCardLayer.Parent).Children.Remove(SystemCardLayer);
        foreach (var child in new[] { NetSpeedCardLayer, SystemCardLayer })
        {
            child.RenderTransform = new TranslateTransform(); child.Opacity = 1; child.IsHitTestVisible = true;
            child.Height = 194; child.VerticalAlignment = VerticalAlignment.Top;
        }
        var content = new StackPanel();
        _cpuCardText = UtilityText("CPU 等待采样 · RAM 等待采样", 12); _cpuCardText.Margin = new Thickness(7, 4, 7, 5);
        content.Children.Add(_cpuCardText); content.Children.Add(NetSpeedCardLayer); content.Children.Add(SystemCardLayer);
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.PreviewMouseWheel += (_, e) => { scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta / 3.0); e.Handled = true; };
        var view = new Grid { Height = 194, VerticalAlignment = VerticalAlignment.Center, RenderTransform = new TranslateTransform() };
        view.Children.Add(scroll); networkParent.Children.Add(view);
        _pages.Register(new DelegateIslandPage(IslandPageIds.System, "系统状态与网络", view, 4,
            IslandMetrics.CardHeightLyrics, activated: () => { SyncNetSpeedCard(); SyncSystemCard(); RefreshCombinedResources(); },
            viewportChanged: height => view.Height = height));
    }
}
