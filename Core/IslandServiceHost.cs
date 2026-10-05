using System;
using System.Collections.Generic;
using System.Text;

namespace alpha_island.Core
{
    /// <summary>
    /// 服务的装配状态。
    /// <para><see cref="Running"/> 的含义是「实例已创建、并已请求启动」——不保证硬件/网络已经就绪
    /// （例如 <c>StartInBackground</c> 里还在做 COM 绑定）。真正「用不了」的情况请用 <see cref="Degraded"/>。</para>
    /// </summary>
    public enum IslandServiceState
    {
        /// <summary>已注册，还没决定启停（<see cref="IslandServiceHost.Sync"/> 之前）。</summary>
        Registered = 0,

        /// <summary>正在启动（防止重入）。</summary>
        Starting = 1,

        /// <summary>已创建并已请求启动。</summary>
        Running = 2,

        /// <summary>启动失败或依赖能力不可用；<c>Reason</c> 里有原因。</summary>
        Degraded = 3,

        /// <summary>按设置关闭（或从未被启动过）。</summary>
        Disabled = 4,

        /// <summary>已停止并释放（退出流程）。</summary>
        Stopped = 5,
    }

    /// <summary>
    /// 一个可被宿主管理的服务。实现方负责「创建 + 订阅 + 把阻塞的 Start() 丢后台」，
    /// 宿主只负责状态机、启停秩序、失败降级与状态报告。
    /// </summary>
    public interface IIslandService
    {
        /// <summary>稳定 Id（用 <see cref="IslandServiceIds"/> 里的常量，不要写字面量）。</summary>
        string Id { get; }

        /// <summary>设置页/诊断页上显示的中文名。</summary>
        string DisplayName { get; }

        /// <summary>
        /// true 表示「一直在」的基础设施（SMTC / 频谱 / 剪切板…）：
        /// <see cref="IslandServiceHost.Sync"/> 永远不会把它停掉，即使 <see cref="IsEnabled"/> 返回 false。
        /// </summary>
        bool Required { get; }

        /// <summary>
        /// true 表示允许 <see cref="IslandServiceHost.Sync"/> 在选项打开时**主动创建**它。
        /// false 只由启动序列里的显式 <see cref="IslandServiceHost.EnsureStarted"/> 拉起
        /// （重型服务必须晚于问候动画，不能被构造函数里的 ApplyOptions 提前拽起来）。
        /// </summary>
        bool AutoStart { get; }

        /// <summary>当前设置下是否应该活着。</summary>
        bool IsEnabled(IslandOptions options);

        /// <summary>创建实例、订阅事件、请求启动。必须幂等（已启动就直接返回）。</summary>
        void Start();

        /// <summary>退订、释放、置空。必须幂等。</summary>
        void Stop();

        /// <summary>设置变化但服务保持存活时调用（例如歌词开关只是切 Enabled，不重建实例）。</summary>
        void OnOptionsChanged(IslandOptions options);
    }

    /// <summary>
    /// 用委托拼出来的服务条目：真正的装配代码留在 MainWindow 那侧（那里才有 Dispatcher、XAML 字段与事件处理器），
    /// 宿主只持有 <see cref="IIslandService"/>。
    /// </summary>
    public sealed class DelegateIslandService : IIslandService
    {
        private readonly Func<IslandOptions, bool> _isEnabled;
        private readonly Action _start;
        private readonly Action _stop;
        private readonly Action<IslandOptions>? _onOptionsChanged;

        public DelegateIslandService(
            string id,
            string displayName,
            Func<IslandOptions, bool> isEnabled,
            Action start,
            Action stop,
            Action<IslandOptions>? onOptionsChanged = null,
            bool required = false,
            bool autoStart = false)
        {
            Id = id;
            DisplayName = displayName;
            Required = required;
            AutoStart = autoStart;
            _isEnabled = isEnabled;
            _start = start;
            _stop = stop;
            _onOptionsChanged = onOptionsChanged;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public bool Required { get; }

        public bool AutoStart { get; }

        public bool IsEnabled(IslandOptions options) => _isEnabled(options);

        public void Start() => _start();

        public void Stop() => _stop();

        public void OnOptionsChanged(IslandOptions options) => _onOptionsChanged?.Invoke(options);
    }

    /// <summary>
    /// 服务宿主（阶段 3.3）。装配路径从「散在各处的 if (开关 &amp;&amp; 实例 == null) new …」
    /// 收敛成「注册一张表 + <see cref="Sync"/>」：
    /// <list type="bullet">
    ///   <item>每个服务独立 try/catch，失败进 <see cref="IslandServiceState.Degraded"/> 并带原因，</item>
    ///   <item>重复创建会被状态机挡住（历史事故：第二个 <c>VolumeService</c> 永不注销 COM 回调），</item>
    ///   <item><see cref="Report"/> 直接喂给「关于」页的只读诊断区块。</item>
    /// </list>
    /// </summary>
    public sealed class IslandServiceHost
    {
        private sealed class Entry
        {
            public required IIslandService Service { get; init; }

            public IslandServiceState State { get; set; } = IslandServiceState.Registered;

            public string Reason { get; set; } = string.Empty;

            /// <summary>被 <see cref="EnsureStarted(string, bool)"/> 以 force 拉起过：<see cref="Sync"/> 不再停它。</summary>
            public bool Pinned { get; set; }
        }

        private readonly List<Entry> _order = new();
        private readonly Dictionary<string, Entry> _byId = new(StringComparer.OrdinalIgnoreCase);
        private IslandOptions? _lastOptions;

        /// <summary>状态发生变化（启动/停止/降级）时触发，供设置页刷新。</summary>
        public event Action? Changed;

        /// <summary>注册一个服务。同一个 <see cref="IIslandService.Id"/> 只允许注册一次。</summary>
        public void Register(IIslandService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            if (string.IsNullOrWhiteSpace(service.Id)) throw new ArgumentException("服务 Id 不能为空", nameof(service));

            if (_byId.ContainsKey(service.Id))
            {
                throw new InvalidOperationException($"服务 '{service.Id}' 重复注册");
            }

            var entry = new Entry { Service = service };
            _byId[service.Id] = entry;
            _order.Add(entry);
        }

        /// <summary>
        /// 把当前设置应用到所有服务：该活的起来、该停的停掉、活着的收一次 <see cref="IIslandService.OnOptionsChanged"/>。
        /// 可以直接反复调用（<c>ApplyOptions</c> 的收敛点）。
        /// </summary>
        public void Sync(IslandOptions options)
        {
            _lastOptions = options;

            bool changed = false;
            foreach (Entry entry in _order)
            {
                bool want = entry.Service.Required || entry.Service.IsEnabled(options);

                if (!want)
                {
                    // Pinned = 被「按需创建」路径显式拉起过（音量卡），即使开关关着也不许 Sync 收走它。
                    if (entry.Pinned)
                    {
                        if (entry.State == IslandServiceState.Running) SafeOptionsChanged(entry, options);
                        continue;
                    }

                    changed |= StopCore(entry);
                    continue;
                }

                if (entry.Service.AutoStart)
                {
                    changed |= EnsureStartedCore(entry, options);
                }
                else if (entry.State == IslandServiceState.Running)
                {
                    SafeOptionsChanged(entry, options);
                }
            }

            if (changed) Changed?.Invoke();
        }

        /// <summary>
        /// 显式拉起一个服务（启动序列与「按需创建」路径用）。返回它最终是否处于 Running。
        /// 选项不允许时不会创建；<paramref name="force"/> = true 时无视选项，并把服务标记为 Pinned
        /// （之后 <see cref="Sync"/> 不会因为开关关着就把它停掉 —— 音量卡就是这么用的）。
        /// </summary>
        public bool EnsureStarted(string id, bool force = false)
        {
            if (!_byId.TryGetValue(id, out Entry? entry)) return false;

            IslandOptions? options = _lastOptions;
            if (!force && options != null && !entry.Service.Required && !entry.Service.IsEnabled(options))
            {
                return false;
            }

            if (force) entry.Pinned = true;

            bool changed = EnsureStartedCore(entry, options);
            if (changed) Changed?.Invoke();
            return entry.State == IslandServiceState.Running;
        }

        /// <summary>给宿主外的装配路径用：标记某个服务已经活着（例如未来由别处创建的实例）。</summary>
        public void MarkRunning(string id)
        {
            if (!_byId.TryGetValue(id, out Entry? entry)) return;
            if (entry.State == IslandServiceState.Running) return;

            entry.State = IslandServiceState.Running;
            entry.Reason = string.Empty;
            Changed?.Invoke();
        }

        /// <summary>标记降级（能力不可用、初始化失败等），原因会显示在「关于」页。</summary>
        public void MarkDegraded(string id, string reason)
        {
            if (!_byId.TryGetValue(id, out Entry? entry)) return;

            entry.State = IslandServiceState.Degraded;
            entry.Reason = reason ?? string.Empty;
            IslandLog.Warn("Service:" + entry.Service.Id, $"降级：{entry.Reason}");
            Changed?.Invoke();
        }

        /// <summary>标记为已停止（不动实例，只改状态）。</summary>
        public void MarkStopped(string id)
        {
            if (!_byId.TryGetValue(id, out Entry? entry)) return;
            entry.State = IslandServiceState.Stopped;
            entry.Reason = string.Empty;
            Changed?.Invoke();
        }

        public bool IsRegistered(string id) => _byId.ContainsKey(id);

        public bool IsRunning(string id) => _byId.TryGetValue(id, out Entry? entry) && entry.State == IslandServiceState.Running;

        public bool IsDegraded(string id) => _byId.TryGetValue(id, out Entry? entry) && entry.State == IslandServiceState.Degraded;

        public IslandServiceState StateOf(string id) =>
            _byId.TryGetValue(id, out Entry? entry) ? entry.State : IslandServiceState.Registered;

        public string ReasonOf(string id) => _byId.TryGetValue(id, out Entry? entry) ? entry.Reason : string.Empty;

        /// <summary>已注册的服务数（诊断用）。</summary>
        public int Count => _order.Count;

        /// <summary>全部停掉（退出流程）。逐个 try/catch：一个服务释放失败不能连累其余。</summary>
        public void StopAll()
        {
            foreach (Entry entry in _order)
            {
                if (entry.State == IslandServiceState.Stopped ||
                    entry.State == IslandServiceState.Registered ||
                    entry.State == IslandServiceState.Disabled)
                {
                    continue;
                }

                try
                {
                    entry.Service.Stop();
                }
                catch (Exception ex)
                {
                    IslandLog.Error("Service:" + entry.Service.Id + ".Stop", ex);
                }

                entry.State = IslandServiceState.Stopped;
                entry.Reason = string.Empty;
            }

            Changed?.Invoke();
        }

        /// <summary>「关于」页的服务状态区块文本。没注册任何服务时返回空串（调用方据此显示占位文案）。</summary>
        public string Report()
        {
            if (_order.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            foreach (Entry entry in _order)
            {
                sb.Append("- ").Append(entry.Service.DisplayName).Append('：').Append(StateText(entry.State));

                if (entry.State == IslandServiceState.Degraded && entry.Reason.Length > 0)
                {
                    sb.Append("（").Append(entry.Reason).Append('）');
                }

                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        private static string StateText(IslandServiceState state) => state switch
        {
            IslandServiceState.Registered => "未启动",
            IslandServiceState.Starting => "启动中",
            IslandServiceState.Running => "运行中",
            IslandServiceState.Degraded => "降级",
            IslandServiceState.Disabled => "已关闭",
            IslandServiceState.Stopped => "已停止",
            _ => state.ToString(),
        };

        private bool EnsureStartedCore(Entry entry, IslandOptions? options)
        {
            if (entry.State == IslandServiceState.Running)
            {
                if (options != null) SafeOptionsChanged(entry, options);
                return false;
            }

            if (entry.State == IslandServiceState.Starting) return false;

            entry.State = IslandServiceState.Starting;
            entry.Reason = string.Empty;

            try
            {
                entry.Service.Start();
                entry.State = IslandServiceState.Running;
                IslandLog.Info("Service:" + entry.Service.Id, $"已启动（{entry.Service.DisplayName}）");
                return true;
            }
            catch (Exception ex)
            {
                entry.State = IslandServiceState.Degraded;
                entry.Reason = ex.GetType().Name + ": " + ex.Message;
                IslandLog.Error("Service:" + entry.Service.Id, ex);
                return true;
            }
        }

        private bool StopCore(Entry entry)
        {
            if (entry.State == IslandServiceState.Registered)
            {
                entry.State = IslandServiceState.Disabled;
                return true;
            }

            if (entry.State == IslandServiceState.Disabled || entry.State == IslandServiceState.Stopped)
            {
                return false;
            }

            try
            {
                entry.Service.Stop();
                IslandLog.Info("Service:" + entry.Service.Id, $"已停用（{entry.Service.DisplayName}）");
            }
            catch (Exception ex)
            {
                IslandLog.Error("Service:" + entry.Service.Id + ".Stop", ex);
            }

            entry.State = IslandServiceState.Disabled;
            entry.Reason = string.Empty;
            return true;
        }

        private static void SafeOptionsChanged(Entry entry, IslandOptions options)
        {
            try
            {
                entry.Service.OnOptionsChanged(options);
            }
            catch (Exception ex)
            {
                IslandLog.Error("Service:" + entry.Service.Id + ".Options", ex);
            }
        }
    }
}
