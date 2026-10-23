using AlphaIsland.PluginSdk;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void DailyTrafficChecks(string root)
    {
        var now = new DateTimeOffset(2026, 10, 18, 18, 0, 0, TimeSpan.FromHours(8));
        var ledger = new DailyTrafficLedger();
        PluginNetworkSample Bridge(DateTimeOffset at, long? down, long? up) => new(at, 100, 20, TodayDownBytes: down, TodayUpBytes: up);
        const string traffic = "alpha.trafficmonitor/network";
        ledger.Observe(now, 1000, 200);
        Check(ledger.Current.Down == 0 && ledger.Current.Up == 0, "首次本机计数只建立基线，不包含程序启动之前的流量");
        ledger.Observe(now.AddSeconds(1), 1800, 350);
        Check(ledger.Current.Down == 800 && ledger.Current.Up == 150, "无插件时使用真实网卡字节差分");
        var sample = Bridge(now.AddSeconds(2), 10000, 2000);
        ledger.Observe(now.AddSeconds(2), 2000, 400, traffic, sample);
        Check(ledger.Current.Down == 10000 && ledger.Current.Up == 2000 && ledger.Source == "TrafficMonitor", "已连接插件的每日记录优先，不与本机计数相加");
        ledger.Observe(now.AddSeconds(3), 2300, 500, traffic, sample);
        Check(ledger.Current.Down == 10000, "重复插件帧不重复累计，也不移动本机恢复基线");
        ledger.Observe(now.AddSeconds(8), 2700, 600, traffic, sample);
        Check(ledger.Current.Down == 10700 && ledger.Current.Up == 2200 && ledger.Source == "本机采样", "连接过期时补上最后一次新插件帧之后的本机字节，不漏掉等待过期的区间");
        ledger.Observe(now.AddSeconds(9), 2800, 650, traffic, Bridge(now.AddSeconds(9), 12000, 2500));
        Check(ledger.Current.Down == 12000 && ledger.Current.Up == 2500, "插件重连后重新采用其日记录，不重复叠加断开区间");
        ledger.Observe(now.AddSeconds(10), 3000, 700, "other.provider/network", Bridge(now.AddSeconds(10), 999999, 999999));
        Check(ledger.Current.Down == 12200 && ledger.Current.Up == 2550, "其他插件不能冒充 TrafficMonitor 的每日数据来源");
        ledger.Observe(now.AddSeconds(11), 3100, 750, traffic, Bridge(now.AddSeconds(11), null, null));
        Check(ledger.Current.Down == 12300, "插件没有完整每日数据时继续本机统计");
        ledger.Observe(now.AddSeconds(12), 3200, 800, traffic, Bridge(now.AddMinutes(1), 0, 0));
        Check(ledger.Current.Down == 12400, "未来时间戳不覆盖本机日记录");
        ledger.Observe(now.AddSeconds(13), 0, 0);
        Check(ledger.Current.Down == 12400 && ledger.Current.Up == 2650, "网卡计数重建不会扣减已有流量");
        ledger.Observe(now.AddSeconds(14), 50, 20);
        Check(ledger.Current.Down == 12450 && ledger.Current.Up == 2670, "重建后继续从新基线累计");
        ledger.Observe(now.AddDays(1), 60, 30, traffic, Bridge(now.AddSeconds(14), 999999, 999999));
        Check(ledger.Current.Date == new DateOnly(2026, 10, 19) && ledger.Current.Down == 0, "跨日建立新基线，不把昨日桥接记录算入今天");
        ledger.Observe(now.AddDays(1).AddSeconds(1), 80, 40);
        Check(ledger.Current.Down == 20 && ledger.Current.Up == 10 && ledger.Export().Days.Length == 2, "跨日后分别保留每日记录");
        var restored = new DailyTrafficLedger(ledger.Export());
        restored.Observe(now.AddDays(1).AddSeconds(2), 9999999, 8888888);
        Check(restored.Current.Down == 20 && restored.Current.Up == 10, "重启恢复今日总量，不将系统累计当成新流量");
        restored.Observe(now.AddDays(1).AddSeconds(3), 10000009, 8888898);
        Check(restored.Current.Down == 30 && restored.Current.Up == 20, "恢复后从新进程基线继续累计");
        for (int day = 2; day < 70; day++) restored.Observe(now.AddDays(day), 100, 100);
        Check(restored.Export().Days.Length <= 32, "日记录容量有上限，不随使用天数无限增长");
        var path = System.IO.Path.Combine(root, "daily-traffic.json");
        using (var service = new DailyTrafficService(path))
        { service.Observe(now, 0, 0); service.Observe(now.AddSeconds(1), 1000, 200); }
        using (var service = new DailyTrafficService(path))
        { service.Observe(now.AddSeconds(2), 100000, 50000); Check(service.Current.Down == 1000 && service.Current.Up == 200, "关闭时刷入本地文件，重新打开后日记录仍保留"); }
        using var net = new NetworkSpeedService();
        net.ApplyByteDeltas(120, 40, 1); net.ResetSession(); net.ApplyByteDeltas(80, 30, 1);
        Check(net.SessionDownloadedBytes == 80 && net.ObservedDownloadedBytes == 200 && net.ObservedUploadedBytes == 70,
            "实时来源切换重置会话曲线时，不重置每日记录使用的字节证据");
    }
}
