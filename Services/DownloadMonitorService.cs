using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using alpha_island.Core;
using alpha_island.Interop;
using Microsoft.Win32;

namespace alpha_island.Services
{
    /// <summary>下载任务的来源大类（显示与排序用）。</summary>
    public enum DownloadSourceKind
    {
        /// <summary>Chromium 系浏览器（Chrome / Edge / Brave / Vivaldi / Opera）。</summary>
        Browser,

        /// <summary>Steam 客户端。</summary>
        Steam,
    }

    public enum DownloadState { Downloading, Paused, Queued, Preparing, Verifying, Installing, Updating, Completed, Unknown }

    /// <summary>任务状态与进度可信度分别记录；暂停、排队不代表完成。</summary>
    public readonly record struct DownloadTask(
        DownloadSourceKind Kind,
        string Source,
        string Name,
        string PathOnDisk,
        long TotalBytes,
        long ReceivedBytes,
        bool IsActive,
        DateTime StartLocal,
        bool SourceRunning = true,
        DownloadState? State = null,
        bool? ProgressReliable = null)
    {
        public DownloadState Status => State ?? (IsActive ? DownloadState.Downloading : DownloadState.Completed);
        public bool IsCompleted => Status == DownloadState.Completed;
        public bool IsRunning => IsActive && Status is DownloadState.Downloading or DownloadState.Preparing
            or DownloadState.Verifying or DownloadState.Installing or DownloadState.Updating;
        public bool HasReliableProgress => TotalKnown && (ProgressReliable ?? Kind != DownloadSourceKind.Steam);
        public bool CanMeasureSpeed => IsRunning && SourceRunning && (Kind != DownloadSourceKind.Steam || ProgressReliable == true);
        public string StatusText => Status switch
        {
            DownloadState.Downloading => "正在下载", DownloadState.Paused => "已暂停", DownloadState.Queued => "等待下载",
            DownloadState.Preparing => "准备下载", DownloadState.Verifying => "正在校验", DownloadState.Installing => "正在更新文件",
            DownloadState.Updating => "正在更新", DownloadState.Completed => "已完成", _ => "等待状态更新"
        };
        public double Fraction => TotalBytes > 0
            ? Math.Clamp((double)ReceivedBytes / TotalBytes, 0d, 1d)
            : 0d;

        public int Percent => (int)Math.Round(Fraction * 100d);

        /// <summary>
        /// 总量是否已知。浏览器「下载中」的那条路永远拿不到总量（见 <see cref="DownloadMonitorService"/> 注释），
        /// 界面遇到 false 就别显示百分比，改显示「已下 X」。
        /// </summary>
        public bool TotalKnown => TotalBytes > 0;

        /// <summary>稳定标识：同一个任务的进度差分（算速度）靠它对齐。</summary>
        public string Key => (int)Kind + "|" + Source + "|" + (PathOnDisk.Length > 0 ? PathOnDisk : Name);
    }

    /// <summary>
    /// 「下载进度」数据源（第二批③）。三种来源，全部**只读**：
    /// ① Steam 清单发现任务，content_log.txt 校正暂停、下载、校验、文件更新等状态。
    ///    ACF 字节数是保存的快照，下载与落盘处理也是两个阶段，不用它推算实时百分比、速率或 ETA；
    /// ② Chromium 系浏览器的 <c>History</c>（表 <c>downloads</c>）—— **只有下载结束才写进来**（见下面实测），
    ///    所以浏览器这条路只能给「完成提示 + 最近完成的条目」，给不了实时百分比；
    /// ③ 浏览器下载目录里的 <c>*.crdownload</c> 半成品文件 —— 能看出「有下载在跑」并拿到**已下字节**
    ///    （实测会随下载一起涨），但拿不到总量；半成品常叫「未确认 &lt;id&gt;.crdownload」，真实文件名也要等完成。
    ///
    /// 浏览器为什么拿不到实时**百分比**（alpha_55 / alpha_55b 探针 + alpha_54b 实跑，Edge 26，30 MB 下载）：
    /// 整个下载期间 <c>select … from downloads</c> 用**直连只读**（运行中报 <c>prepare rc=5 database is locked</c>）
    /// 和**复制主库+wal 再读**两种方式都是 0 行；下载一结束才出现 <c>state=1</c> 那行（History 文件同时长大）。
    /// 库里的行没有总量、盘上的半成品只有字节数 → 拼不出百分比，界面上不能承诺「浏览器下载进度」。
    ///
    /// 明确取不到的：Firefox（本机没装，未验证，所以不做）、Epic/WeGame/Battle.net（本机没装）、
    /// 浏览器的实时百分比与剩余时间、浏览器「暂停中的下载」（暂停了也只是个别扭的半成品文件）。
    /// </summary>
    public sealed class DownloadMonitorService
    {
        private const int ChromiumRowLimit = 64;
        private readonly DateTime _startedLocal = DateTime.Now;
        private const double CopyCacheSeconds = 1.5;

        /// <summary>半成品文件多久没动就不再当作「正在下载」（免得崩溃残留一直挂在卡片上）。</summary>
        private const double PartialFreshSeconds = 120;

        /// <summary>Chrome / Edge 给「还没定下文件名」的下载起的临时名（中文版是「未确认 802882」）。</summary>
        private static readonly Regex UnconfirmedNameRx = new(@"^(未确认|Unconfirmed)\s+\d+$", RegexOptions.Compiled);

        /// <summary>从 Preferences 里抠 download.default_directory（那个文件是 JSON，但我们只需要一个值）。</summary>
        private static readonly Regex DefaultDownloadDirRx = new(
            "\"default_directory\"\\s*:\\s*\"(?<p>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

        /// <summary>Chrome 的时间戳是「1601-01-01 UTC 起的微秒数」。</summary>
        private static readonly DateTime ChromeEpochUtc = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// 上一次采样的结果快照。采样跑在**后台线程**上（采样占住 UI 线程会冻结全机输入，
        /// 见 MainWindow.Downloads.cs 的说明），而卡片页/细条在 UI 线程读 <see cref="Current"/> ——
        /// 所以对外发布的是"拍完就不再改"的整份新列表，而不是一个会被边读边清空重建的 List。
        /// 引用赋值是原子的：UI 线程要么看到上一拍、要么看到这一拍，绝不会撞见"清到一半"。
        /// </summary>
        private IReadOnlyList<DownloadTask> _snapshot = Array.Empty<DownloadTask>();

        /// <summary>
        /// 浏览器库的临时副本缓存：落盘位置、复制时刻、这份副本带不带 -shm、
        /// 以及复制时**源库的指纹**（<see cref="SourceStamp"/>）。
        /// 指纹没变就直接复用旧副本、一个字节都不碰磁盘 —— 浏览器在跑时库被锁、只能读副本，
        /// 这一条把「每 2 秒复制 19 MB 的 History」压成 0（实测那是 8.8 MB/s 的进程 I/O）。
        /// </summary>
        private readonly Dictionary<string, (string CopyPath, DateTime When, bool IncludeShm, string Stamp)> _copies = new();

        /// <summary>上一拍还有半成品（= 在下载）的配置目录：这一拍没了说明刚下完，要绕开副本缓存去读完成行。</summary>
        private readonly HashSet<string> _partialProfiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _warned = new();
        private readonly HashSet<string> _debugged = new();
        private readonly string _tempRoot;
        private readonly string? _profileOverride;
        private readonly string? _steamPathOverride;
        private readonly SteamDownloadLog _steamLog = new();
        private DateTime _lastWarnUtc = DateTime.MinValue;
        private DateTime _lastDebugUtc = DateTime.MinValue;

        public DownloadMonitorService(string? profileOverride = null, string? steamPathOverride = null)
        {
            _profileOverride = string.IsNullOrWhiteSpace(profileOverride) ? null : profileOverride;
            _steamPathOverride = steamPathOverride;
            _tempRoot = Path.Combine(Path.GetTempPath(), "alpha-island-downloads");
        }

        public IReadOnlyList<DownloadTask> Current => _snapshot;

        /// <summary>降级原因（读不到浏览器库 / Steam 目录异常时的最后一次说明）。null = 一切正常。</summary>
        public string? DegradedReason { get; private set; }

        public void Sample()
        {
            var list = new List<DownloadTask>();
            DegradedReason = null;

            try
            {
                SampleChromium(list);
            }
            catch (Exception ex)
            {
                DegradedReason = "浏览器：" + ex.Message;
                WarnOnce("chromium-ex", "读浏览器下载表时抛异常：" + ex.Message);
            }

            try
            {
                SampleSteam(list);
            }
            catch (Exception ex)
            {
                DegradedReason = "Steam：" + ex.Message;
                WarnOnce("steam-ex", "读 Steam 下载清单时抛异常：" + ex.Message);
            }

            var ordered = list
                .OrderByDescending(t => t.IsActive)
                .ThenByDescending(t => t.StartLocal)
                .ToList();

            // 整份换掉（不原地清空/追加）：UI 线程随时可能读 Current，见 _snapshot 的注释。
            _snapshot = ordered;
        }

        // ==================== Chromium 浏览器 ====================

        private void SampleChromium(List<DownloadTask> into)
        {
            var scanned = new List<string>();
            foreach ((string browser, string profileDir) in EnumerateChromiumProfiles())
            {
                scanned.Add(browser);
                string dbPath = Path.Combine(profileDir, "History");

                // 先枚举下载目录里的半成品（只动目录，不读库）：既知道这一拍有没有下载在跑，
                // 也能看出「上一拍有、这一拍没了」= 刚下完。
                var partials = CollectChromiumPartials(profileDir);
                bool downloading = partials.Count > 0;
                bool justFinished = !downloading && _partialProfiles.Contains(profileDir);
                if (downloading) _partialProfiles.Add(profileDir);
                else _partialProfiles.Remove(profileDir);

                // justFinished 时强制重新复制副本：完成那一刻浏览器才写库，光看源文件指纹可能刚好错过
                // （否则一直读旧副本，永远看不到那条「已完成」）。
                if (!TryQueryChromium(dbPath, justFinished, out List<string?[]> rows, out string? error))
                {
                    WarnOnce("chromium:" + dbPath, $"读不到 {browser} 的下载表（{error}）");
                    rows = new List<string?[]>();
                }

                // 同一个文件两分钟里下了两次（重试 / 重下）时库里会有两行、target_path 一模一样：
                // 只留最新那行（查询按 start_time desc，第一行就是最新），否则卡片和日志都会重复一份。
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string?[] row in rows)
                {
                    string targetPath = WinSqlite.ColumnText(row, 0);
                    string currentPath = WinSqlite.ColumnText(row, 1);
                    long total = WinSqlite.ColumnInt64(row, 2);
                    long received = WinSqlite.ColumnInt64(row, 3);
                    long state = WinSqlite.ColumnInt64(row, 4);
                    long startMicros = WinSqlite.ColumnInt64(row, 5);
                    long endMicros = WinSqlite.ColumnInt64(row, 6);

                    // state：0=下载中 1=已完成 2=已取消 3=已中断。取消/中断的不占版面。
                    bool active = state == 0;
                    if (!active && state != 1) continue;

                    DateTime startLocal = ChromeTimeToLocal(startMicros);

                    // 已完成的老条目不显示（History 里躺着几百条历史下载，只认最近的）。
                    DateTime endLocal = ChromeTimeToLocal(endMicros);
                    if (!active && (endMicros <= 0 || endLocal < _startedLocal ||
                        (DateTime.Now - endLocal).TotalMinutes > 2)) continue;

                    if (active)
                    {
                        // 浏览器刷库有节奏（不是每个字节都写库），所以**盘上的实际长度**才是实时进度：
                        // 下载中 Chrome 把内容写在「目标路径 + .crdownload」，current_path 通常就是它。
                        long onDisk = ProbePartialLength(currentPath, targetPath);
                        if (onDisk > received) received = onDisk;
                    }

                    if (total <= 0) continue;

                    string name = SafeFileName(targetPath, currentPath);
                    if (name.Length == 0) continue;

                    string taskPath = targetPath.Length > 0 ? targetPath : currentPath;
                    if (!seenPaths.Add(taskPath)) continue;

                    into.Add(new DownloadTask(
                        Kind: DownloadSourceKind.Browser,
                        Source: browser,
                        Name: name,
                        PathOnDisk: taskPath,
                        TotalBytes: total,
                        ReceivedBytes: Math.Clamp(received, 0, total),
                        IsActive: active,
                        StartLocal: startLocal));
                }

                // 库里不可能有「下载中」的行（浏览器只在结束时写库），所以「正在下载」只能靠盘上的半成品文件。
                AddChromiumPartials(into, browser, partials);
            }

            // 扫到哪些配置目录必须能看见（否则「没有任务」到底是没下还是没扫到，日志里分不出来）。
            if (scanned.Count > 0)
            {
                DebugOnce("profiles:" + string.Join(",", scanned),
                    $"浏览器配置目录：{string.Join("、", scanned)}（{scanned.Count} 个）");
            }
        }

        /// <summary>
        /// 枚举浏览器下载目录里的 <c>*.crdownload</c> 半成品文件（只枚举，不入任务列表）。
        /// 硬事实（alpha_55b 探针 + alpha_54b 实跑）：Chrome / Edge 下载中**不写库**，
        /// 半成品文件给出的是「有下载在跑 + 已下字节」（应用侧实测会随下载一起涨），
        /// 但拿不到总量 —— 所以这些任务的 TotalBytes 一律 0，界面别显示百分比。
        /// 调用方先拿它判断「这一拍在不在下载 / 上一拍的半成品是不是刚消失」。
        /// </summary>
        private static List<(string Path, long Size, DateTime When)> CollectChromiumPartials(string profileDir)
        {
            var found = new List<(string Path, long Size, DateTime When)>();

            foreach (string dir in CandidateDownloadDirs(profileDir))
            {
                string[] partials;
                try
                {
                    partials = Directory.GetFiles(dir, "*.crdownload");
                }
                catch
                {
                    continue;
                }

                foreach (string partial in partials)
                {
                    long size;
                    DateTime when;
                    try
                    {
                        var info = new FileInfo(partial);
                        if (!info.Exists) continue;
                        size = info.Length;
                        when = info.LastWriteTime;
                    }
                    catch
                    {
                        continue;   // 刚好被浏览器改名/删掉 —— 下一拍再看
                    }

                    // 崩溃/取消留下的半成品别一直挂在卡片上。
                    if ((DateTime.Now - when).TotalSeconds > PartialFreshSeconds) continue;

                    found.Add((partial, size, when));
                }
            }

            return found;
        }

        /// <summary>把半成品文件加成任务（库里已完成的行先入的列表，同路径的跳过）。</summary>
        private void AddChromiumPartials(List<DownloadTask> into, string browser,
                                        List<(string Path, long Size, DateTime When)> partials)
        {
            foreach ((string partial, long size, DateTime when) in partials)
            {
                // 同一份下载被多个浏览器配置目录指到同一个「下载」目录时只算一次；
                // 库里的完成行（目标文件已就位）也不会和它撞路径。
                if (into.Any(t => string.Equals(t.PathOnDisk, partial, StringComparison.OrdinalIgnoreCase))) continue;

                string stem = Path.GetFileNameWithoutExtension(partial);
                string label = UnconfirmedNameRx.IsMatch(stem) ? "下载中（文件名未确认）" : stem;
                into.Add(new DownloadTask(
                    Kind: DownloadSourceKind.Browser,
                    Source: browser,
                    Name: label,
                    PathOnDisk: partial,
                    TotalBytes: 0,
                    ReceivedBytes: size,
                    IsActive: true,
                    StartLocal: when));

                DebugOnce("partial:" + partial,
                    $"浏览器半成品文件：{Path.GetFileName(partial)}（{MemoryService.FormatSize(size)}，浏览器不提供总量）");
            }
        }

        /// <summary>浏览器把下载写到哪儿：先看 Preferences 的 download.default_directory，没设就是用户「下载」目录。</summary>
        private static IEnumerable<string> CandidateDownloadDirs(string profileDir)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string? configured = TryReadDownloadDirectory(profileDir);
            if (configured is { Length: > 0 } && seen.Add(configured))
            {
                yield return configured;
            }

            string fallback;
            try
            {
                fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
            catch
            {
                yield break;
            }

            if (fallback.Length > 0 && seen.Add(fallback)) yield return fallback;
        }

        private static string? TryReadDownloadDirectory(string profileDir)
        {
            try
            {
                string prefs = Path.Combine(profileDir, "Preferences");
                if (!File.Exists(prefs)) return null;

                Match m = DefaultDownloadDirRx.Match(ReadAllTextShared(prefs));
                if (!m.Success) return null;

                string path = m.Groups["p"].Value.Replace("\\\\", "\\").Replace("\\/", "/");
                return path.Length > 0 && Directory.Exists(path) ? path : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 找一个在下载中的文件当前有多大。依次看 current_path、target+”.crdownload“、target，
        /// 取存在的那个里最大的长度（谁新用谁）。
        /// </summary>
        private static long ProbePartialLength(string currentPath, string targetPath)
        {
            long best = 0;
            foreach (string candidate in new[]
                     {
                         currentPath,
                         targetPath.Length > 0 ? targetPath + ".crdownload" : string.Empty,
                         targetPath,
                     })
            {
                if (candidate.Length == 0) continue;
                try
                {
                    var info = new FileInfo(candidate);
                    if (info.Exists && info.Length > best) best = info.Length;
                }
                catch
                {
                    // 文件刚好被浏览器改名/删除 —— 下一拍再看。
                }
            }

            return best;
        }

        private bool TryQueryChromium(string dbPath, bool forceFresh, out List<string?[]> rows, out string? error)
        {
            string sql = "select target_path, current_path, total_bytes, received_bytes, state, start_time, end_time " +
                         "from downloads order by max(start_time, end_time) desc limit " + ChromiumRowLimit.ToString(CultureInfo.InvariantCulture);

            rows = new List<string?[]>();
            error = null;

            // 先直连：浏览器没在跑的时候它能直接读。
            if (WinSqlite.TryQuery(dbPath, sql, ChromiumRowLimit, out rows, out error)) return true;

            // 浏览器在跑 = 它占着这个库（prepare 直接 database is locked），
            // 唯一办法是复制一份（连 -wal 一起）再读副本。
            // forceFresh 用于「刚下完」那一拍：浏览器是完成时才写库的，必须重读一次而不是拿旧副本。
            string? copy = TryRefreshCopy(dbPath, includeShm: false, force: forceFresh);
            if (copy != null && WinSqlite.TryQuery(copy, sql, ChromiumRowLimit, out rows, out error)) return true;

            // 副本里的 -shm 不匹配时也会失败：把 -shm 一起复制再来一次。
            copy = TryRefreshCopy(dbPath, includeShm: true, force: true);
            if (copy != null && WinSqlite.TryQuery(copy, sql, ChromiumRowLimit, out rows, out error)) return true;

            return false;
        }

        /// <summary>
        /// 源库指纹：History 本体 + <c>-journal</c> / <c>-wal</c> / <c>-shm</c> 的「长度@修改时间」。
        /// 浏览器没动过库时指纹不变 → 副本可以一直复用，不用每个 tick 重拷一遍
        /// （实测「每 2 秒复制 19 MB 的 Edge History」是 ~8.8 MB/s 的进程 I/O，见 alpha_64_io.ps1）。
        /// </summary>
        private static string SourceStamp(string dbPath)
        {
            var sb = new StringBuilder(96);
            foreach (string path in new[] { dbPath, dbPath + "-journal", dbPath + "-wal", dbPath + "-shm" })
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists) sb.Append(info.Length.ToString(CultureInfo.InvariantCulture)).Append('@').Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                    else sb.Append("-@-");
                    sb.Append('|');
                }
                catch
                {
                    sb.Append("?|");
                }
            }

            return sb.ToString();
        }

        private string? TryRefreshCopy(string dbPath, bool includeShm, bool force)
        {
            string stamp = SourceStamp(dbPath);

            if (_copies.TryGetValue(dbPath, out var cache) && File.Exists(cache.CopyPath))
            {
                if (force)
                {
                    // 上一拍读这份副本失败了（多半是复制到一半的残片）：扔掉重来。
                    _copies.Remove(dbPath);
                }
                else
                {
                    bool shmOk = !includeShm || cache.IncludeShm;

                    // 指纹没变（浏览器没写过库）就直接用旧副本，一个字节都不碰盘；
                    // 指纹变了但还没到 TTL 也先用旧的，避免浏览器狂写时反复复制整个库。
                    bool unchanged = string.Equals(stamp, cache.Stamp, StringComparison.Ordinal);
                    bool fresh = (DateTime.UtcNow - cache.When).TotalSeconds < CopyCacheSeconds;
                    if (shmOk && (unchanged || fresh)) return cache.CopyPath;
                }
            }

            try
            {
                string dir = Path.Combine(_tempRoot, Fnv1a(dbPath));
                Directory.CreateDirectory(dir);
                string dest = Path.Combine(dir, "History");

                File.Copy(dbPath, dest, overwrite: true);

                foreach (string suffix in new[] { "-wal", "-shm" })
                {
                    string src = dbPath + suffix;
                    string dst = dest + suffix;
                    bool wanted = suffix == "-wal" || includeShm;
                    if (wanted && File.Exists(src)) File.Copy(src, dst, overwrite: true);
                    else if (File.Exists(dst)) File.Delete(dst);
                }

                _copies[dbPath] = (dest, DateTime.UtcNow, includeShm, stamp);
                return dest;
            }
            catch (Exception ex)
            {
                WarnOnce("copy:" + dbPath, $"复制浏览器下载库失败（{Path.GetFileName(dbPath)}）：{ex.Message}");
                return null;
            }
        }

        private IEnumerable<(string Browser, string ProfileDir)> EnumerateChromiumProfiles()
        {
            if (_profileOverride != null)
            {
                // 调试钩子：既接受「配置目录本身含 History」（Opera 那种），也接受「用户数据目录」
                // （Chrome/Edge 的 --user-data-dir，History 在 Default / Profile N 子目录里）——
                // 临时 profile 验证时传的是后者，只认前者会一条都扫不到。
                string leaf = new DirectoryInfo(_profileOverride).Name;
                string label = leaf.Length > 0 ? leaf : "Chromium";

                if (File.Exists(Path.Combine(_profileOverride, "History")))
                {
                    yield return (label, _profileOverride);
                    yield break;
                }

                string[] subs;
                try
                {
                    subs = Directory.GetDirectories(_profileOverride);
                }
                catch
                {
                    subs = Array.Empty<string>();
                }

                bool any = false;
                foreach (string dir in subs)
                {
                    if (!File.Exists(Path.Combine(dir, "History"))) continue;
                    any = true;
                    yield return (label + "/" + Path.GetFileName(dir), dir);
                }

                if (!any)
                {
                    // 什么都不含：仍然把目录本身交出去，让上层打一条「数据库文件不存在」，比静默不扫强。
                    yield return (label, _profileOverride);
                }

                yield break;
            }

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            var roots = new List<(string Browser, string Root)>();
            void Add(string browser, string root)
            {
                try
                {
                    if (root.Length > 0 && Directory.Exists(root)) roots.Add((browser, root));
                }
                catch
                {
                    // 路径非法就当这台机器没装。
                }
            }

            Add("Chrome", Path.Combine(local, @"Google\Chrome\User Data"));
            Add("Edge", Path.Combine(local, @"Microsoft\Edge\User Data"));
            Add("Brave", Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"));
            Add("Vivaldi", Path.Combine(local, @"Vivaldi\User Data"));
            Add("Chromium", Path.Combine(local, @"Chromium\User Data"));
            Add("Opera", Path.Combine(roaming, @"Opera Software\Opera Stable"));
            Add("Opera GX", Path.Combine(roaming, @"Opera Software\Opera GX Stable"));

            foreach ((string browser, string root) in roots)
            {
                // Opera 这类：History 直接在根目录；Chrome/Edge 这类：在 Default / Profile N 子目录。
                if (File.Exists(Path.Combine(root, "History")))
                {
                    yield return (browser, root);
                    continue;
                }

                string[] dirs;
                try
                {
                    dirs = Directory.GetDirectories(root);
                }
                catch
                {
                    continue;
                }

                foreach (string dir in dirs)
                {
                    if (File.Exists(Path.Combine(dir, "History"))) yield return (browser, dir);
                }
            }
        }

        // ==================== Steam ====================

        private void SampleSteam(List<DownloadTask> into)
        {
            if (_steamPathOverride == null && !SteamClientRunning()) return;
            string? steamPath = _steamPathOverride ?? ReadSteamInstallPath();
            var libraries = new List<string>();
            var seenAppIds = new HashSet<string>(StringComparer.Ordinal);

            if (!string.IsNullOrWhiteSpace(steamPath))
            {
                libraries.Add(steamPath!);
                _steamLog.Refresh(Path.Combine(steamPath, "logs", "content_log.txt"));

                string vdf = Path.Combine(steamPath!, @"steamapps\libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    string text = ReadAllTextShared(vdf);
                    foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                    {
                        string path = match.Groups[1].Value.Replace("\\\\", "\\");
                        if (path.Length == 0) continue;
                        if (libraries.Any(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase))) continue;
                        libraries.Add(path);
                    }
                }
            }

            foreach (string library in libraries)
            {
                string steamapps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(steamapps)) continue;

                // downloading 目录也可能是暂停或退出后的残留，必须结合日志状态。
                ScanSteamManifests(steamapps, fromDownloadingDir: false, seenAppIds, into);
                ScanSteamManifests(Path.Combine(steamapps, "downloading"), fromDownloadingDir: true, seenAppIds, into);
            }
        }

        private void ScanSteamManifests(string dir, bool fromDownloadingDir, HashSet<string> seenAppIds, List<DownloadTask> into)
        {
            if (!Directory.Exists(dir)) return;

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "appmanifest_*.acf");
            }
            catch
            {
                return;
            }

            foreach (string file in files)
            {
                string text = ReadAllTextShared(file);
                if (text.Length == 0) continue;

                long total = Math.Max(0, VdfNumber(text, "BytesToDownload"));
                long received = Math.Max(0, VdfNumber(text, "BytesDownloaded"));
                long flags = VdfNumber(text, "StateFlags");
                string appId = VdfValue(text, "appid");
                SteamDownloadObservation? observation = _steamLog.ForApp(appId);
                // UpdateStarted(1024) 在暂停时仍存在，不能拿它证明正在下载；只有 UpdateRunning(256)
                // 能保守地表明正在处理更新，具体阶段由客户端日志给出。
                bool outstanding = fromDownloadingDir || (flags & (8 | 256 | 1024)) != 0;
                DownloadState state = observation?.State ?? ((flags & 256) != 0 ? DownloadState.Updating
                    : (flags & 8) != 0 ? DownloadState.Queued : DownloadState.Unknown);
                if (observation is { State: DownloadState.Completed } finished && outstanding
                    && File.GetLastWriteTime(file) > finished.ChangedLocal.AddSeconds(2))
                    state = (flags & 256) != 0 ? DownloadState.Updating : DownloadState.Unknown;
                bool completed = state == DownloadState.Completed && (flags & (2 | 8 | 256 | 1024)) == 0;
                if (!outstanding && !completed) continue;
                if (appId.Length > 0 && !seenAppIds.Add(appId)) continue;

                string name = VdfValue(text, "name");
                if (name.Length == 0) name = appId.Length > 0 ? "App " + appId : Path.GetFileNameWithoutExtension(file);

                long updatedUnix = VdfNumber(text, "LastUpdated");
                DateTime startLocal;
                try { startLocal = updatedUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(updatedUnix).LocalDateTime : _startedLocal; }
                catch (ArgumentOutOfRangeException) { startLocal = _startedLocal; }
                if (completed && (updatedUnix <= 0 || startLocal < _startedLocal || DateTime.Now - startLocal > TimeSpan.FromMinutes(2)
                    || startLocal > DateTime.Now.AddMinutes(1))) continue;
                if (state == DownloadState.Completed && !completed) state = DownloadState.Unknown;
                bool running = state is DownloadState.Downloading or DownloadState.Preparing or DownloadState.Verifying
                    or DownloadState.Installing or DownloadState.Updating;

                into.Add(new DownloadTask(
                    Kind: DownloadSourceKind.Steam,
                    Source: "Steam",
                    Name: name,
                    PathOnDisk: file,
                    TotalBytes: total,
                    ReceivedBytes: Math.Clamp(received, 0, total),
                    IsActive: running,
                    StartLocal: startLocal,
                    SourceRunning: running,
                    State: state,
                    ProgressReliable: false));
            }
        }

        private static string? ReadSteamInstallPath()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string path && path.Length > 0) return path.Replace('/', '\\');
            }
            catch
            {
                // 继续尝试安装注册项与正在运行的客户端。
            }

            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                if (key?.GetValue("InstallPath") is string path && path.Length > 0) return path;
            }
            catch { }
            string? runningPath = null;
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("steam"))
            {
                using (process)
                {
                    try { runningPath ??= Path.GetDirectoryName(process.MainModule?.FileName); }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
            }
            if (runningPath != null) return runningPath;

            return null;
        }

        private static bool SteamClientRunning()
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("steam");
            bool running = processes.Length > 0;
            foreach (var process in processes) process.Dispose();
            return running;
        }

        // ==================== 小工具 ====================

        private static string VdfValue(string text, string key)
        {
            Match match = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s+\"([^\"]*)\"");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static long VdfNumber(string text, string key)
        {
            string raw = VdfValue(text, key);
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;
        }

        private static string ReadAllTextShared(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SafeFileName(string targetPath, string currentPath)
        {
            foreach (string candidate in new[] { targetPath, currentPath })
            {
                if (candidate.Length == 0) continue;
                try
                {
                    string name = Path.GetFileName(candidate);
                    if (name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase))
                    {
                        name = name[..^".crdownload".Length];
                    }

                    if (name.Length > 0) return name;
                }
                catch
                {
                    // 路径里带非法字符（极少）→ 换下一个候选。
                }
            }

            return string.Empty;
        }

        private static DateTime ChromeTimeToLocal(long micros)
        {
            if (micros <= 0) return DateTime.MinValue;
            try
            {
                return ChromeEpochUtc.AddTicks(micros * 10).ToLocalTime();
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static string Fnv1a(string text)
        {
            uint hash = 2166136261;
            foreach (char ch in text.ToLowerInvariant())
            {
                hash ^= ch;
                hash *= 16777619;
            }

            return hash.ToString("x8", CultureInfo.InvariantCulture);
        }

        private void WarnOnce(string key, string message)
        {
            bool throttled = _warned.Contains(key) && (DateTime.UtcNow - _lastWarnUtc).TotalMinutes < 5;
            if (throttled) return;

            _warned.Add(key);
            _lastWarnUtc = DateTime.UtcNow;
            IslandLog.Warn("Download", message);
        }

        /// <summary>同一条信息 5 分钟内只打一次（每 2 秒一拍，不能每拍都刷）。宿主也用它对账界面文案。</summary>
        internal void DebugOnce(string key, string message)
        {
            bool throttled = _debugged.Contains(key) && (DateTime.UtcNow - _lastDebugUtc).TotalMinutes < 5;
            if (throttled) return;

            _debugged.Add(key);
            _lastDebugUtc = DateTime.UtcNow;
            IslandLog.Debug("Download", message);
        }
    }
}
