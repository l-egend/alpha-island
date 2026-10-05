using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Services;

namespace alpha_island
{
    /// <summary>
    /// 下载状态的宿主：浏览器（Chrome / Edge…）与 Steam 的下载任务，
    /// 数据来自 <see cref="DownloadMonitorService"/>（全部只读）。
    /// <para>
    /// 两条出口：① 细条态一个 **常显** 的下载图层（下载期间一直挂着，完成时弹一次「下载完成」）；
    /// ② 卡片页最多列出 3 条任务；只有可信的实时数据才显示进度、速度与剩余时间。
    /// </para>
    /// 速度是**自己差分**出来的（数据源只给已下字节数）：两次采样之间 Δ字节 / Δ时间，做一次指数平滑。
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>细条提示的停留秒数：下载中每拍都会续期，所以它实际是「停止刷新后还能挂多久」。</summary>
        private const double DownloadNotifySeconds = 6;

        /// <summary>卡片上最多列几条任务（再多也看不清，其余折叠成计数）。</summary>
        private const int DownloadCardRows = 3;

        private DownloadMonitorService? _downloads;
        private readonly Dictionary<string, (long Bytes, DateTime When)> _dlPrev = new();
        private readonly Dictionary<string, double> _dlSpeed = new();
        private string? _dlLastPromptKey;
        private DownloadState? _dlLastPromptState;
        private readonly HashSet<string> _dlSeenCompleted = new();
        private readonly Queue<string> _dlCompletedOrder = new();
        /// <summary>后台采样进行中（重入闸）：上一拍没读完就跳过这一拍，保证 UI 线程永远不等它。</summary>
        private bool _dlSampling;
        // 刚下完的提示要排队：完成那一拍可能还有别的任务在跑（细条图层被进度占着），
        // 若这一拍直接丢掉，下一拍那条完成行已经进过 _dlSeenCompleted，提示就永远不会弹了。
        private string? _dlPendingFinish;
        private bool _dlFake;
        private int _dlFakeTicks;
        private string? _dlProfileOverride;

        // ================= 服务启停（由 MainWindow.Services.cs 的可用性 lambda 驱动） =================

        internal void StartDownloadService()
        {
            _downloads ??= new DownloadMonitorService(_dlProfileOverride);
            if (!_ticker.IsRegistered("downloads"))
            {
                _ticker.Register("downloads", TimeSpan.FromSeconds(2), SampleDownloads, runImmediately: true);
            }
        }

        internal void StopDownloadService()
        {
            // 退出时 _ticker 先被 Dispose 并置空（MainWindow.xaml.cs:612），StopAll 在后（:623）→ 必须空判。
            _ticker?.Unregister("downloads");
            _downloads = null;
            _dlPrev.Clear();
            _dlSpeed.Clear();
            _dlLastPromptKey = null;
            _dlLastPromptState = null;
            _dlPendingFinish = null;
            _layers.Hide("download");
            if (IsDownloadCardVisible) SyncDownloadCard();
        }

        /// <summary>开关变化时清掉已经显示的数据（关掉就不该再看到旧值）。</summary>
        internal void OnDownloadOptionsChanged()
        {
            if (_options == null) return;
            if (!SettingsRegistry.GetBool(_options, "DownloadMonitorEnabled", true))
            {
                _dlPrev.Clear();
                _dlSpeed.Clear();
                _dlLastPromptKey = null;
                _dlLastPromptState = null;
                _dlPendingFinish = null;
                _layers.Hide("download");
            }

            if (IsDownloadCardVisible) SyncDownloadCard();
        }

        // ================= 调试钩子（ALPHA_ISLAND_FAKE_DOWNLOAD） =================

        internal void EnableFakeDownloadForDebug()
        {
            _dlFake = true;
            IslandLog.Debug("Download", "调试钩子：下载进度改用合成任务（每拍 +8%，第 12 拍后完成）");
        }

        /// <summary>
        /// 调试钩子：只把这一个目录当浏览器配置目录扫（真实 Edge/Chrome 配置完全不动）。
        /// 直接建服务而不是只记字段 —— 服务注册与调试钩子的先后顺序不该影响结果。
        /// </summary>
        internal void UseDownloadProfileForDebug(string dir)
        {
            _dlProfileOverride = dir;
            _downloads = new DownloadMonitorService(dir);
            IslandLog.Debug("Download", $"调试钩子：下载进度只扫这个浏览器配置目录 = {dir}");
        }

        // ================= 采样 =================

        private void SampleDownloads()
        {
            if (_dlFake)
            {
                _dlFakeTicks++;
                long total = 300L * 1024 * 1024;
                long received = (long)(total * Math.Min(1.0, _dlFakeTicks * 0.08));
                ApplyDownloadSample(
                    new List<DownloadTask>
                    {
                        new(
                            Kind: DownloadSourceKind.Steam,
                            Source: "Steam（合成）",
                            Name: "合成游戏包（调试）",
                            PathOnDisk: @"C:\ExampleGames\SteamLibrary\steamapps\downloading\appmanifest_999999.acf",
                            TotalBytes: total,
                            ReceivedBytes: received,
                            IsActive: received < total,
                            StartLocal: DateTime.Now.AddMinutes(-3),
                            ProgressReliable: true),
                    },
                    DateTime.UtcNow);
                return;
            }

            DownloadMonitorService? service = _downloads;
            if (service == null) return;

            // ⚠ 这一拍绝不能占 UI 线程。低级鼠标/键盘钩子的回调要靠 UI 线程抽消息才会返回，
            // UI 线程被占住多久，整机输入就冻多久（alpha_74 实测：这里同步采样 812–843ms/拍，
            // 鼠标每 2 秒冻结 800ms、键盘同理）。采样本身是纯文件/SQLite 活，扔到线程池去。
            if (_dlSampling) return; // 上一拍还没读完：跳过这一拍，不把线程池堆起来
            _dlSampling = true;

            Task.Run(() =>
            {
                List<DownloadTask> tasks;
                try
                {
                    service.Sample();
                    // 快照必须在后台线程上取：Sample() 会换掉 Current 指向的那一份。
                    tasks = service.Current.ToList();
                }
                catch (Exception ex)
                {
                    IslandLog.Error("Download", ex);
                    tasks = new List<DownloadTask>();
                }

                if (Dispatcher.HasShutdownStarted) return;

                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _dlSampling = false;
                        // 服务被停掉/换掉（退出、关开关、调试钩子换目录）时，这一拍的结果作废。
                        if (!ReferenceEquals(_downloads, service)) return;
                        ApplyDownloadSample(tasks, DateTime.UtcNow);
                    }), DispatcherPriority.Background);
                }
                catch (Exception ex)
                {
                    _dlSampling = false;
                    IslandLog.Debug("Download", "采样结果回投 UI 线程失败（可能正在退出）：" + ex.Message);
                }
            });
        }

        /// <summary>
        /// 把后台采到的这一拍应用到界面（只在 UI 线程上调用）。
        /// 采集与应用的分离见 <see cref="SampleDownloads"/> 的注释：采集绝不能占 UI 线程。
        /// </summary>
        private void ApplyDownloadSample(List<DownloadTask> tasks, DateTime now)
        {
            // ---- 速度：按任务 Key 差分（缺失的一拍不算，避免"重启后第一拍速度爆炸"） ----
            var liveKeys = new HashSet<string>(tasks.Select(t => t.Key), StringComparer.Ordinal);
            foreach (DownloadTask task in tasks)
            {
                if (!task.CanMeasureSpeed)
                {
                    _dlPrev.Remove(task.Key);
                    _dlSpeed.Remove(task.Key);
                    continue;
                }
                if (_dlPrev.TryGetValue(task.Key, out (long Bytes, DateTime When) prev))
                {
                    double seconds = (now - prev.When).TotalSeconds;
                    if (seconds > 0.2 && task.ReceivedBytes >= prev.Bytes)
                    {
                        double instant = (task.ReceivedBytes - prev.Bytes) / seconds;
                        _dlSpeed[task.Key] = _dlSpeed.TryGetValue(task.Key, out double smoothed)
                            ? smoothed * 0.55 + instant * 0.45
                            : instant;
                    }
                    else if (task.ReceivedBytes < prev.Bytes) _dlSpeed.Remove(task.Key);
                }

                _dlPrev[task.Key] = (task.ReceivedBytes, now);
            }

            foreach (string stale in _dlPrev.Keys.Where(k => !liveKeys.Contains(k)).ToList())
            {
                _dlPrev.Remove(stale);
                _dlSpeed.Remove(stale);
            }

            // ---- 日志（每拍一条，是验证脚本的判据） ----
            var active = tasks.Where(t => t.IsRunning).ToList();
            if (tasks.Count == 0)
            {
                IslandLog.Debug("Download", "下载进度：没有任务（浏览器 downloads 表 + Steam downloading 清单都空）");
            }
            else
            {
                foreach (DownloadTask task in tasks.Take(DownloadCardRows))
                {
                    IslandLog.Debug("Download", DescribeTask(task));
                }
            }

            // 只有来源明确确认完成才通知；暂停、排队、任务消失都不算完成。
            string? justFinished = null;

            foreach (DownloadTask task in tasks.Where(t => t.IsCompleted))
            {
                // 同一条下载要按「目标路径 + 开始时间」区分：同一个文件重下一次（很常见）目标路径一模一样，
                // 只按路径去重的话第二次下完就不会报"完成"了。
                string id = task.Key + "|" + task.StartLocal.Ticks.ToString(CultureInfo.InvariantCulture);
                if (!_dlSeenCompleted.Add(id)) continue;
                _dlCompletedOrder.Enqueue(id);
                while (_dlCompletedOrder.Count > 512) _dlSeenCompleted.Remove(_dlCompletedOrder.Dequeue());
                justFinished = task.Name;
            }


            // ---- 出口①：细条图层 ----
            if (justFinished is { Length: > 0 }) _dlPendingFinish = justFinished;

            if (active.Count > 0)
            {
                DownloadTask head = active[0];
                ShowDownloadNotify(head, _dlSpeed.TryGetValue(head.Key, out double speed) ? speed : 0d);
                _dlLastPromptKey = head.Key;
                _dlLastPromptState = head.Status;
            }
            else if (_dlPendingFinish is { Length: > 0 })
            {
                ShowDownloadFinished(_dlPendingFinish);
                _dlPendingFinish = null;
                _dlLastPromptKey = null;
                _dlLastPromptState = null;
            }
            else if (tasks.Any(t => !t.IsCompleted))
            {
                DownloadTask pending = tasks.First(t => !t.IsCompleted);
                // 暂停只弹一次，不每两秒重新置顶；保留在下载卡中供查看。
                if (_dlLastPromptKey != pending.Key || _dlLastPromptState != pending.Status)
                    ShowDownloadNotify(pending, 0, refreshing: false);
                _dlLastPromptKey = pending.Key;
                _dlLastPromptState = pending.Status;
            }
            else
            {
                _dlLastPromptKey = null;
                _dlLastPromptState = null;
            }

            // ---- 出口②：卡片页 ----
            if (IsDownloadCardVisible) SyncDownloadCard();
        }

        private static string DescribeTask(DownloadTask task)
        {
            string state = task.StatusText;
            if (!task.HasReliableProgress)
            {
                return $"{task.Source}：{task.Name}｜{state}｜实时进度不可用";
            }

            return $"{task.Source}：{task.Name}｜{task.Percent}%（{MemoryService.FormatSize(task.ReceivedBytes)} / " +
                   $"{MemoryService.FormatSize(task.TotalBytes)}）｜{state}";
        }

        private void ShowDownloadNotify(DownloadTask task, double bytesPerSecond, bool refreshing = true)
        {
            DownloadNotifyGlyph.Text = task.Status == DownloadState.Paused ? "\uE769" : "\uE896";
            DownloadNotifyTitle.Text = task.Source + " " + task.StatusText;

            if (!task.HasReliableProgress)
            {
                DownloadNotifyDetail.Text = task.Name;
                DownloadNotifyValue.Text = string.Empty;
                // 细条上真正显示了什么，日志里要能核对（每个任务只在第一次显示时打一条）。
                _downloads?.DebugOnce("notify:" + task.Key + ":" + task.Status,
                    $"下载提示：{DownloadNotifyTitle.Text}｜{DownloadNotifyDetail.Text}");
                ShowDownloadLayer(refreshing);
                return;
            }

            double displaySpeed = task.CanMeasureSpeed ? bytesPerSecond : 0;
            DownloadNotifyDetail.Text = task.Name
                                        + $"｜总大小 {MemoryService.FormatSize(task.TotalBytes)}"
                                        + (displaySpeed > 0 ? $"｜{NetworkSpeedService.FormatSpeedFull(displaySpeed)}" : "｜速度未知");
            DownloadNotifyValue.Text = string.Empty;
            // 细条上真正显示了什么，日志里要能核对。按「有没有速度」各记一条（同一任务的两种口径都留证据）。
            _downloads?.DebugOnce("notify:" + task.Key + (displaySpeed > 0 ? ":speed" : ":nospeed"),
                $"下载提示：{DownloadNotifyTitle.Text}｜{DownloadNotifyDetail.Text}");
            ShowDownloadLayer(refreshing);
        }

        private void ShowDownloadFinished(string name)
        {
            DownloadNotifyGlyph.Text = "\uE73E";   // 勾
            DownloadNotifyTitle.Text = "下载完成";
            DownloadNotifyDetail.Text = name;
            DownloadNotifyValue.Text = "100%";
            IslandLog.Debug("Download", $"下载完成：{name}");
            ShowDownloadLayer(refreshing: false);
        }

        /// <summary>
        /// 弹出下载提示图层（三条路径共用：进度 / 完成 / 浏览器不懂进度）。
        ///
        /// download 层的 Scope 是 PillOnly：在细条态直接 Show 的话，_layers.Sync 的 ScopeAllows
        /// 会把它挡掉 —— 通知被静默丢弃（Show 了、时间窗照走、屏幕上什么都不出现）。
        /// 形态统一交给 ShowPillNotification：细条态先展开成胶囊；用户正开着的卡片不顶掉
        /// （下载信息本来就在卡片上）。
        /// </summary>
        private void ShowDownloadLayer(bool refreshing = true)
        {
            ShowPillNotification(refreshing
                ? IslandNotification.Refreshing("download", DownloadNotifySeconds)
                : IslandNotification.Seconds("download", DownloadNotifySeconds));
        }

        /// <summary>剩余时间文案（速度未知就不猜）。</summary>
        private static string BuildEta(DownloadTask task, double bytesPerSecond)
        {
            if (task.IsCompleted) return "已完成";
            if (!task.HasReliableProgress || !task.IsRunning) return "剩余时间未知";
            if (bytesPerSecond <= 1) return "剩余时间未知";
            double seconds = Math.Max(0, task.TotalBytes - task.ReceivedBytes) / bytesPerSecond;
            if (seconds < 1) return "即将完成";
            if (seconds < 60) return $"剩余 {seconds:0} 秒";
            if (seconds < 3600) return $"剩余 {seconds / 60:0} 分";
            return $"剩余 {seconds / 3600:0.0} 小时";
        }

        // ================= 卡片页 =================

        internal bool IsDownloadCardVisible =>
            _state == IslandState.Card && _pages.ActiveId == IslandPageIds.Download;

        internal void SetDownloadCardLayerHeight(double height)
        {
            if (DownloadCardLayer != null) DownloadCardLayer.Height = height;
        }

        internal void SyncDownloadCard()
        {
            if (DownloadCardLayer == null) return;

            DownloadMonitorService? service = _downloads;
            var tasks = new List<DownloadTask>();

            if (_dlFake)
            {
                // 合成模式下卡片也照实显示合成任务（否则调卡片页看不到任何东西）。
                long total = 300L * 1024 * 1024;
                long received = (long)(total * Math.Min(1.0, _dlFakeTicks * 0.08));
                tasks.Add(new DownloadTask(DownloadSourceKind.Steam, "Steam（合成）", "合成游戏包（调试）",
                    @"C:\ExampleGames\SteamLibrary\steamapps\downloading\appmanifest_999999.acf",
                    total, received, received < total, DateTime.Now.AddMinutes(-3), ProgressReliable: true));
            }
            else if (service != null)
            {
                tasks.AddRange(service.Current);
            }

            DownloadTask[] rows = tasks.Take(DownloadCardRows).ToArray();
            int activeCount = tasks.Count(t => t.IsRunning);
            int pausedCount = tasks.Count(t => t.Status == DownloadState.Paused);

            DownloadCardSummaryText.Text = tasks.Count == 0
                ? ""
                : $"{activeCount} 个进行中" + (pausedCount > 0 ? $" · {pausedCount} 个已暂停" : "") + $" · 共 {tasks.Count} 个任务";

            DownloadEmptyText.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DownloadEmptyText.Text = service?.DegradedReason is { Length: > 0 } reason
                ? "读不到下载信息：" + reason
                : "暂无下载任务";

            for (int i = 0; i < DownloadCardRows; i++)
            {
                Border track = i switch { 0 => DlTrack1, 1 => DlTrack2, _ => DlTrack3 };
                Border fill = i switch { 0 => DlFill1, 1 => DlFill2, _ => DlFill3 };
                Grid row = i switch { 0 => DlRow1, 1 => DlRow2, _ => DlRow3 };
                TextBlock name = i switch { 0 => DlName1, 1 => DlName2, _ => DlName3 };
                TextBlock meta = i switch { 0 => DlMeta1, 1 => DlMeta2, _ => DlMeta3 };

                bool has = i < rows.Length;
                row.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
                if (!has) continue;

                DownloadTask task = rows[i];
                name.Text = task.Source + " · " + task.Name;
                double speed = task.CanMeasureSpeed && _dlSpeed.TryGetValue(task.Key, out double s) ? s : 0d;
                if (!task.HasReliableProgress)
                {
                    meta.Text = task.StatusText;
                }
                else
                {
                    meta.Text = $"{task.Percent}% · {MemoryService.FormatSize(task.ReceivedBytes)} / {MemoryService.FormatSize(task.TotalBytes)}"
                                + $" · {task.StatusText}"
                                + (speed > 0 ? $" · {NetworkSpeedService.FormatSpeedFull(speed)} · {BuildEta(task, speed)}" : "");
                }

                track.Visibility = task.HasReliableProgress ? Visibility.Visible : Visibility.Collapsed;
                meta.Margin = new Thickness(0, task.HasReliableProgress ? 0 : 5, 0, 0);
                if (!task.HasReliableProgress) { fill.Width = 0; continue; }
                // 进度条：轨道的实际宽度 × 比例。首次布局还没量到宽度就下一帧再来一次。
                double trackWidth = track.ActualWidth;
                if (trackWidth <= 0)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (IsDownloadCardVisible) SyncDownloadCard();
                    }), DispatcherPriority.Loaded);
                    trackWidth = 0;
                }

                fill.Width = Math.Max(0, trackWidth * task.Fraction);
                fill.Visibility = Visibility.Visible;
            }

            string head = rows.Length == 0
                ? "空"
                : rows[0].HasReliableProgress
                    ? $"{rows[0].Source}/{rows[0].Name} {rows[0].Percent}%"
                    : $"{rows[0].Source}/{rows[0].Name} {rows[0].StatusText}（实时进度不可用）";
            IslandLog.Debug("Download", $"下载卡：共 {tasks.Count} 个（下载中 {activeCount}）｜首项 {head}"
                + (rows.Length > 0 && DlMeta1 != null ? $"｜界面 {DlMeta1.Text}" : string.Empty));
        }

        /// <summary>设置页/README 用的一句话说明（当前能取到什么）。</summary>
        internal string BuildDownloadSummary()
        {
            DownloadMonitorService? service = _downloads;
            if (service == null) return "未启动";
            if (service.DegradedReason is { Length: > 0 } reason) return reason;
            int active = service.Current.Count(t => t.IsRunning);
            int paused = service.Current.Count(t => t.Status == DownloadState.Paused);
            return service.Current.Count > 0
                ? $"{active} 个进行中 · {paused} 个已暂停（Steam 按客户端日志识别状态）"
                : "当前没有下载任务";
        }

        /// <summary>调试/诊断用：把当前任务列成一行。</summary>
        internal string DescribeDownloads()
        {
            DownloadMonitorService? service = _downloads;
            if (service == null) return "服务未启动";
            if (service.Current.Count == 0) return "没有任务";
            return string.Join("｜", service.Current.Take(DownloadCardRows).Select(DescribeTask));
        }
    }
}
