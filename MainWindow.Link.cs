using System;
using alpha_island.Core;
using alpha_island.Services;

namespace alpha_island
{
    public partial class MainWindow
    {
        private TrafficMonitorService? _traffic;
        private bool _trafficFake;
        private TrafficMonitorSummary _trafficSummary;
        internal void StartTrafficLinkService()
        {
            _traffic ??= new TrafficMonitorService();
            if (!_ticker.IsRegistered("traffic"))
                _ticker.Register("traffic", TimeSpan.FromSeconds(15), SampleTraffic, runImmediately: true);
        }
        internal void StopTrafficLinkService() => _ticker?.Unregister("traffic");
        internal void OnTrafficOptionsChanged()
        {
            if (!SettingsRegistry.GetBool(_options, "TrafficMonitorLinkEnabled", true)) _trafficSummary = default;
            if (IsNetSpeedCardVisible) SyncNetSpeedCard();
        }
        internal void EnableFakeTrafficForDebug()
        {
            _trafficFake = true;
            IslandLog.Debug("Traffic", "TrafficMonitor 使用合成流量（非真实值）");
        }
        private void SampleTraffic()
        {
            _trafficSummary = _trafficFake
                ? new TrafficMonitorSummary(
                    Found: true,
                    Path: @"C:\ExampleTools\TrafficMonitor\history_traffic.dat",
                    // 故意用一眼能认出来的非真实值：这样日志里一眼能分清「读到了真文件」还是「走了合成数据」。
                    TodayUp: 1_111_111,
                    TodayDown: 2_222_222,
                    TotalUp: 1_111_111_111,
                    TotalDown: 2_222_222_222,
                    Days: 999)
                : _traffic?.Sample() ?? default;

            if (_trafficSummary.Found)
            {
                IslandLog.Debug("Traffic",
                    $"每日流量：今日 ↓{MemoryService.FormatSize(_trafficSummary.TodayDown)} ↑{MemoryService.FormatSize(_trafficSummary.TodayUp)}" +
                    $"｜累计 ↓{MemoryService.FormatSize(_trafficSummary.TotalDown)} ↑{MemoryService.FormatSize(_trafficSummary.TotalUp)}" +
                    $"｜{_trafficSummary.Days} 天｜{_trafficSummary.Path}");
            }
            else
            {
                IslandLog.Debug("Traffic",
                    "每日流量：没找到 TrafficMonitor 历史记录；实时速度优先使用已连接的桥接插件，否则使用本机网卡采样");
            }

            if (IsNetSpeedCardVisible) SyncNetSpeedCard();
        }

        /// <summary>网速卡片提示行上的 TrafficMonitor 后缀（空串 = 这一行不提流量）。</summary>
        internal string BuildTrafficHint()
        {
            if (_externalNetwork.Reading is { TodayDownBytes: long down, TodayUpBytes: long up })
                return $" · 今日 ↓{MemoryService.FormatSize(down)} ↑{MemoryService.FormatSize(up)}";
            if (!_trafficSummary.Found) return "";
            return $"｜今日 ↓{MemoryService.FormatSize(_trafficSummary.TodayDown)} ↑{MemoryService.FormatSize(_trafficSummary.TodayUp)}（TrafficMonitor）";
        }
    }
}
