using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using Panel = System.Windows.Controls.Panel;

namespace alpha_island;

public partial class MainWindow
{
    private CpuUsageService? _cpu;
    private TextBlock _cpuCardText = null!;
    private void StartCpuService()
    {
        if (_cpu != null) return;
        _cpu = new CpuUsageService(); _cpu.Sample();
        _ticker.Register("cpu", TimeSpan.FromSeconds(2), SampleCpu, runImmediately: false);
        RefreshCombinedResources();
    }
    private void StopCpuService() { _ticker?.Unregister("cpu"); _cpu = null; RefreshCombinedResources(); }
    private void SampleCpu() { _cpu?.Sample(); RefreshCombinedResources(); }
    private void RefreshCombinedResources()
    {
        UpdateNetSpeedPrimary(CurrentNetworkSample);
        UpdateNetSpeedSatelliteVisual(CurrentNetworkSample);
        if (_cpuCardText != null)
        {
            string cpu = _cpu?.CurrentPercent is double value ? $"{value:0}%" : "未开启 / 等待采样";
            string ram = _memory?.Current.Valid == true ? $"{_memory.Current.UsedPercent:0}%" : "未开启 / 等待采样";
            _cpuCardText.Text = $"CPU {cpu}  ·  RAM {ram}";
        }
    }
    private void UpdateCombinedStatus(NetworkSpeedPill view, NetworkSpeedSample sample)
    {
        if (_netSpeed == null) view.SetUnavailable();
        else
        {
            view.SetRates(sample.UpBytesPerSecond, sample.DownBytesPerSecond);
            view.SetSessionInfo(_externalNetwork.Active && _externalNetwork.Reading!.UploadCounterBytes == null ? null : NetworkSessionUp,
                _externalNetwork.Active && _externalNetwork.Reading!.DownloadCounterBytes == null ? null : NetworkSessionDown,
                NetworkPeakUp, NetworkPeakDown, _netSpeedDownHistory);
        }
        view.SetResources(_cpu?.CurrentPercent, _memory?.Current.Valid == true ? _memory.Current.UsedPercent : null);
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
