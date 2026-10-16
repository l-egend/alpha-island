using System.IO;
using System.Text.Json;
using alpha_island.Services;

internal static partial class Program
{
    private static void BrowserDownloadChecks(string root)
    {
        string downloads = Path.Combine(root, "共享下载目录"); Directory.CreateDirectory(downloads);
        string partial = Path.Combine(downloads, "未确认 168168.crdownload");
        File.WriteAllBytes(partial, new byte[1024]);
        string Profile(string browser)
        {
            string profile = Path.Combine(root, browser); Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, "Preferences"), JsonSerializer.Serialize(new
                { download = new { default_directory = downloads } }, new JsonSerializerOptions
                { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Sql(Path.Combine(profile, "History"), "create table downloads(target_path text,current_path text,total_bytes integer,received_bytes integer,state integer,start_time integer,end_time integer);");
            return profile;
        }
        string chrome = Profile("Chrome"), edge = Profile("Edge");
        var monitor = new DownloadMonitorService(() => new[] { ("Chrome", chrome), ("Edge", edge) });
        monitor.Sample();
        var task = monitor.Current.Single(t => t.PathOnDisk == partial);
        Check(task.Source == "浏览器", "共享下载目录没有真实来源证据时不因 Chrome 先扫描就误标为 Chrome");
        monitor = new DownloadMonitorService(() => new[] { ("Chrome", chrome), ("Edge", edge) }, owner: _ => "Edge");
        monitor.Sample();
        Check(monitor.Current.Single(t => t.PathOnDisk == partial).Source == "Edge", "同一共享目录根据真实文件持有者识别 Edge");
        var labels = new[] { "下载", "Kazumi_windows_2.3.9.msix", "386 KB/s - 18.0 MB/45.4 MB，剩余 1 分钟" };
        var readings = BrowserDownloadProbe.ParseLabels("Edge", labels);
        Check(readings is [{ Name: "Kazumi_windows_2.3.9.msix", ReceivedBytes: 18_874_368, TotalBytes: 47_605_350 }],
            "解析截图中的 Edge 下载文件名、已下载大小和总大小");
        using (var file = new FileStream(partial, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) file.SetLength(18 * 1_048_576);
        bool showFlyout = true;
        monitor = new DownloadMonitorService(() => new[] { ("Chrome", chrome), ("Edge", edge) },
            () => showFlyout ? readings : Array.Empty<BrowserDownloadReading>(), _ => "Edge");
        monitor.Sample(); task = monitor.Current.Single(t => t.PathOnDisk == partial);
        Check(task.Source == "Edge" && task.Name == "Kazumi_windows_2.3.9.msix" && task.HasReliableProgress && task.Percent == 40,
            "真实采样入口合并 Edge 实时元数据和盘上字节，显示正确文件名与进度");
        showFlyout = false;
        using (var file = new FileStream(partial, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) file.SetLength(30 * 1_048_576);
        monitor.Sample(); task = monitor.Current.Single(t => t.PathOnDisk == partial);
        Check(task.Source == "Edge" && task.Percent == 66 && task.Name == "Kazumi_windows_2.3.9.msix",
            "Edge 下载面板收起后保留真实总大小，并继续更新下载进度");
        Check(BrowserDownloadProbe.ParseLabels("Edge", new[] { "资料.txt", "hello 18 MB/45 MB" }).Count == 0,
            "普通网页文本不会冒充下载进度");
        Check(BrowserDownloadProbe.ParseLabels("Edge", new[] { "Downloads", "sample.zip", "1,812 KB/22.9 MB - 500 KB/s" })
            is [{ ReceivedBytes: 1_855_488, TotalBytes: 24_012_390 }], "真实 Edge 千位分隔符和 1024 进制大小解析准确");
        File.WriteAllText(Path.Combine(edge, "Preferences"), JsonSerializer.Serialize(new { download = new { default_directory = downloads } }));
        monitor.Sample();
        Check(monitor.Current.Any(x => x.PathOnDisk == partial), "正确读取包含转义中文字符的浏览器下载目录");
        string target = Path.Combine(downloads, "unknown.zip");
        string sqlPath = partial.Replace("'", "''"), sqlTarget = target.Replace("'", "''");
        Sql(Path.Combine(edge, "History"), $"insert into downloads values('{sqlTarget}','{sqlPath}',0,10,0,0,0);");
        monitor.Sample();
        Check(monitor.Current.Any(x => x.PathOnDisk == partial), "History 记录暂时缺少总大小时仍保留正在下载的文件任务");
    }

    private static void BrowserDownloadUiChecks(alpha_island.MainWindow window, System.Windows.Controls.Grid root, string testRoot)
    {
        string folder = Path.Combine(testRoot, "Edge-ui"); Directory.CreateDirectory(folder);
        string partial = Path.Combine(folder, "package.zip.crdownload");
        File.WriteAllText(Path.Combine(folder, "Preferences"), JsonSerializer.Serialize(new { download = new { default_directory = folder } }));
        Sql(Path.Combine(folder, "History"), "create table downloads(target_path text,current_path text,total_bytes integer,received_bytes integer,state integer,start_time integer,end_time integer);");
        using (var file = File.Create(partial)) file.SetLength(40_000);
        bool metadata = true;
        var monitor = new DownloadMonitorService(() => new[] { ("Edge", folder) },
            () => metadata ? new[] { new BrowserDownloadReading("Edge", "package.zip", 40_000, 100_000) } : [], _ => "Edge");
        SetAgentField(window, "_downloads", monitor);
        try
        {
            monitor.Sample(); PrivateCall(window, "ApplyDownloadSample", monitor.Current.ToList(), DateTime.UtcNow);
            PrivateCall(window, "SetState", alpha_island.Core.IslandState.Card, false, alpha_island.Core.IslandPageIds.Download);
            PumpAnimations(70); root.UpdateLayout(); PrivateCall(window, "SyncDownloadCard");
            Check(window.DlName1.Text == "Edge · package.zip" && window.DlMeta1.Text.Contains("40%"), "真实下载卡显示 Edge、具体文件名及正确百分比");
            Check(window.DlTrack1.Visibility == System.Windows.Visibility.Visible && window.DlFill1.Width > 0,
                "取得 Edge 总大小后真实卡片进度条可见且有填充");
            Check(window.DownloadNotifyTitle.Text == "Edge 正在下载" && window.DownloadNotifyValue.Text == "40%",
                "Edge 下载小岛也显示进度百分比");
            metadata = false;
            using (var file = new FileStream(partial, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) file.SetLength(70_000);
            monitor.Sample(); PrivateCall(window, "ApplyDownloadSample", monitor.Current.ToList(), DateTime.UtcNow.AddSeconds(2)); root.UpdateLayout();
            Check(window.DlMeta1.Text.Contains("70%") && window.DownloadNotifyValue.Text == "70%", "面板收起后卡片和小岛继续更新真实盘上进度");
            var unknown = new DownloadMonitorService(() => new[] { ("Edge", folder) }); unknown.Sample();
            SetAgentField(window, "_downloads", unknown); PrivateCall(window, "ApplyDownloadSample", unknown.Current.ToList(), DateTime.UtcNow.AddSeconds(4)); root.UpdateLayout();
            Check(window.DlMeta1.Text.Contains("已下载") && !window.DlMeta1.Text.Contains('%')
                && window.DlTrack1.Visibility == System.Windows.Visibility.Collapsed, "浏览器未提供总大小时显示已下载大小且不伪造百分比");
            Check(window.DlName1.Text.StartsWith("浏览器 ·") && !window.DlName1.Text.Contains("Chrome"), "来源暂未确认时卡片不冒充 Chrome");
        }
        finally { SetAgentField(window, "_downloads", null); }
    }
}
