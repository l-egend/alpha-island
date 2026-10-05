using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace alpha_island.Services
{
    /// <summary>TrafficMonitor 的「每日历史流量」汇总（只读它的 history_traffic.dat）。</summary>
    public readonly record struct TrafficMonitorSummary(
        bool Found,
        string Path,
        long TodayUp,
        long TodayDown,
        long TotalUp,
        long TotalDown,
        int Days);

    /// <summary>
    /// 任务栏流量监控软件 TrafficMonitor 的联动：它把每天的上下行总量写成纯文本
    /// <c>history_traffic.dat</c>（首行 <c>lines: "422"</c>，其后 <c>yyyy/MM/dd 上传/下载</c>，最新在上）。
    /// <para>
    /// 历史文件以 KB 保存，读取后统一转为字节。实时数据由可选 TrafficMonitor 桥接插件提供；
    /// 本服务只负责历史汇总，不能用历史文件代替实时采样。
    /// </para>
    /// </summary>
    public sealed class TrafficMonitorService
    {
        private string? _path;
        public TrafficMonitorService(string? historyPath = null) => _path = historyPath;

        public TrafficMonitorSummary Current { get; private set; }

        public string DegradedReason { get; private set; } = "";

        /// <summary>解析一次历史流量文件；文件没变也照解析（就 400 来行，且不像 vivo 那样被频繁改写）。</summary>
        public TrafficMonitorSummary Sample()
        {
            string? path = _path;
            if (path == null || !File.Exists(path))
            {
                _path = null;
                path = Locate();
                if (path == null)
                {
                    DegradedReason = "没找到 TrafficMonitor 的 history_traffic.dat（没装，或换了目录）";
                    Current = default;
                    return Current;
                }
                _path = path;
            }

            string text;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                text = reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                DegradedReason = "读取失败：" + ex.Message;
                return Current;
            }

            DateOnly today = DateOnly.FromDateTime(DateTime.Now);
            long todayUp = 0, todayDown = 0, totalUp = 0, totalDown = 0;
            int days = 0;

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;   // 首行是 lines: "422"，正好在这里被跳过
                if (!DateOnly.TryParseExact(parts[0], "yyyy/MM/dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateOnly date))
                {
                    continue;
                }
                string[] bytes = parts[1].Split('/');
                if (bytes.Length != 2) continue;
                if (!long.TryParse(bytes[0], NumberStyles.None, CultureInfo.InvariantCulture, out long up)) continue;
                if (!long.TryParse(bytes[1], NumberStyles.None, CultureInfo.InvariantCulture, out long down)) continue;
                // 历史文件保存的是 KB；实时插件接口才是字节。
                if (up > long.MaxValue / 1024 || down > long.MaxValue / 1024) continue;
                up *= 1024; down *= 1024;

                days++;
                totalUp = up > long.MaxValue - totalUp ? long.MaxValue : totalUp + up;
                totalDown = down > long.MaxValue - totalDown ? long.MaxValue : totalDown + down;
                if (date == today)
                {
                    todayUp = up;
                    todayDown = down;
                }
            }

            DegradedReason = "";
            Current = new TrafficMonitorSummary(true, path, todayUp, todayDown, totalUp, totalDown, days);
            return Current;
        }

        /// <summary>找 history_traffic.dat。便携版放哪儿的都有，所以扫一遍固定盘的一级目录。</summary>
        private static string? Locate()
        {
            foreach (string candidate in Candidates())
            {
                try
                {
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // 目录不可访问（比如无权限的 Program Files 子目录）——继续找下一个候选。
                }
            }
            return null;
        }

        private static IEnumerable<string> Candidates()
        {
            foreach (string folder in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     })
            {
                if (!string.IsNullOrEmpty(folder))
                {
                    yield return Path.Combine(folder, "TrafficMonitor", "history_traffic.dat");
                }
            }

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                string root = drive.RootDirectory.FullName;

                yield return Path.Combine(root, "TrafficMonitor", "history_traffic.dat");
                foreach (string dir in SafeDirs(root, "TrafficMonitor*"))
                {
                    yield return Path.Combine(dir, "history_traffic.dat");
                }

                foreach (string pf in new[] { "Program Files", "Program Files (x86)" })
                {
                    foreach (string dir in SafeDirs(Path.Combine(root, pf), "TrafficMonitor*"))
                    {
                        yield return Path.Combine(dir, "history_traffic.dat");
                    }
                }
            }
        }

        private static string[] SafeDirs(string path, string pattern)
        {
            try
            {
                return Directory.GetDirectories(path, pattern);
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}
