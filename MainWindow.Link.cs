using System;
using alpha_island.Core;
using alpha_island.Services;

namespace alpha_island
{
    public partial class MainWindow
    {
        private DailyTrafficService? _dailyTraffic;
        private bool _trafficFake;
        private TrafficMonitorSummary _trafficSummary;
        internal void StartTrafficLinkService()
        {
            _dailyTraffic ??= new DailyTrafficService(_runtimeEnabled
                ? System.IO.Path.Combine(AppPaths.ConfigDir, "daily-traffic.json") : null);
            _dailyTraffic.Observe(DateTimeOffset.Now, _netSpeed?.ObservedDownloadedBytes ?? 0, _netSpeed?.ObservedUploadedBytes ?? 0);
            SampleTraffic();
        }
        internal void StopTrafficLinkService()
        {
            _ticker?.Unregister("traffic");
            _dailyTraffic?.Dispose(); _dailyTraffic = null;
        }
        internal void OnTrafficOptionsChanged()
        {
            if (!SettingsRegistry.GetBool(_options, "DailyTrafficEnabled", true)) _trafficSummary = default;
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
                : default;

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
                    "每日流量：已启用本机持久记录，有效插件连接时自动切换数据源");
            }

            if (IsNetSpeedCardVisible) SyncNetSpeedCard();
        }

        /// <summary>网速卡片提示行上的 TrafficMonitor 后缀（空串 = 这一行不提流量）。</summary>
        internal string BuildTrafficHint()
        {
            if (!SettingsRegistry.GetBool(_options, "DailyTrafficEnabled", true)) return "";
            if (_trafficFake && _trafficSummary.Found)
                return $" · 今日 ↓{MemoryService.FormatSize(_trafficSummary.TodayDown)} ↑{MemoryService.FormatSize(_trafficSummary.TodayUp)}（预览）";
            if (_dailyTraffic == null) return "";
            var day = _dailyTraffic.Current;
            return $" · 今日 ↓{MemoryService.FormatSize(day.Down)} ↑{MemoryService.FormatSize(day.Up)}";
        }
    }
}
