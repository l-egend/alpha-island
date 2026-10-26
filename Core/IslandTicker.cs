using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace alpha_island.Core
{
    /// <summary>
    /// 低频采样的统一调度器。把"每个功能一个 DispatcherTimer"收成**一个**定时器 + 一组命名订阅。
    ///
    /// 为什么需要它：这一批新功能（网速、内存、功率、天气…）天然都是"每隔 N 秒看一眼系统"，
    /// 各写一个 DispatcherTimer 会变成十几个定时器互相错峰抖动，
    /// 而且每个都要自己处理"暂停/恢复/异常不要炸"。
    ///
    /// 约定：
    ///   * 回调跑在 UI 线程（DispatcherTimer 语义）——绝大多数消费者都要碰控件，
    ///     真正费时的采集请在回调里自己 Task.Run 出去，别让这一拍变长；
    ///   * 实际触发精度 = resolution（默认 250ms），即"到点后最多晚一个 resolution"；
    ///   * 没有启用的订阅时定时器自动停掉，不空转唤醒 CPU；
    ///   * 回调抛异常只记日志并继续下一拍，绝不让一个功能拖垮整条链。
    ///
    /// 注意：**不要**拿它做逐帧动画（进度条、弹簧）。那种用各自的时间轴，
    /// 走这里会因为 250ms 的粒度掉帧。
    /// </summary>
    public sealed class IslandTicker : IDisposable
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Dispatcher, IslandTicker> SharedTickers = new();
        internal static IslandTicker Shared(Dispatcher dispatcher)
        {
            dispatcher.VerifyAccess();
            if (SharedTickers.TryGetValue(dispatcher, out var current) && !current._disposed) return current;
            SharedTickers.Remove(dispatcher);
            var ticker = new IslandTicker(dispatcher, TimeSpan.FromMilliseconds(16)); SharedTickers.Add(dispatcher, ticker); return ticker;
        }
        private sealed class Entry
        {
            public string Key = string.Empty;
            public TimeSpan Interval;
            public Action Tick = static () => { };
            public long NextDueMs;
            public bool Paused;
        }

        private readonly DispatcherTimer _timer;
        private readonly TimeSpan _resolution;
        private readonly Func<long> _now;
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

        /// <summary>遍历用快照。Register/Unregister 时整体换新数组，回调里再注册不会破坏当前这一拍。</summary>
        private Entry[] _snapshot = Array.Empty<Entry>();

        private bool _disposed;
        private Func<string, bool>? _executionAllowed;

        /// <summary>上一拍的时刻（诊断用）：两次心跳差得远 = 这一拍之前 UI 线程被别的活占住了。</summary>
        private long _lastTickMs;

        public IslandTicker(Dispatcher dispatcher, TimeSpan? resolution = null)
            : this(dispatcher, resolution, () => Environment.TickCount64) { }

        internal IslandTicker(Dispatcher dispatcher, TimeSpan? resolution, Func<long> now)
        {
            var step = resolution ?? TimeSpan.FromMilliseconds(250);
            if (step < TimeSpan.FromMilliseconds(16)) step = TimeSpan.FromMilliseconds(16);
            _resolution = step;
            _now = now;

            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = step
            };
            _timer.Tick += OnTimerTick;
        }

        /// <summary>已注册的订阅数量（含暂停的）。</summary>
        public int Count => _entries.Count;
        internal TimeSpan ScheduledInterval => _timer.Interval;
        internal bool IsRunning => _timer.IsEnabled;
        public string Report() => $"统一心跳：{(_timer.IsEnabled ? "运行" : "暂停")}，下次唤醒 {_timer.Interval.TotalMilliseconds:0} ms\n"
            + string.Join("\n", _snapshot.Select(entry => $"{entry.Key}：{(!CanRun(entry) ? "已暂停" : entry.Interval.TotalSeconds.ToString("0.###") + " 秒")}"));
        public sealed class Subscription : IPausable, IDisposable
        {
            private IslandTicker? _owner;
            private readonly Entry _entry;
            private Subscription(IslandTicker owner, Entry entry) { _owner = owner; _entry = entry; }
            private bool Current => _owner != null && !_owner._disposed
                && _owner._entries.TryGetValue(_entry.Key, out var current) && ReferenceEquals(current, _entry);
            public bool IsPaused => !Current || !_owner!.CanRun(_entry);
            public void SetPaused(bool paused) { if (Current) _owner!.SetPaused(_entry.Key, paused); }
            public void SetInterval(TimeSpan interval) { if (Current) _owner!.SetInterval(_entry.Key, interval); }
            public void TriggerNow() { if (Current) _owner!.TriggerNow(_entry.Key); }
            public void Dispose() { if (Current) _owner!.Unregister(_entry.Key); _owner = null; }
            internal static Subscription Create(IslandTicker owner, string key) => new(owner, owner._entries[key]);
        }

        public Subscription Subscribe(string key, TimeSpan interval, Action tick, bool runImmediately = false)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Register(key, interval, tick);
            var subscription = Subscription.Create(this, key);
            if (runImmediately) subscription.TriggerNow();
            return subscription;
        }

        /// <summary>
        /// 注册一个周期任务。同一 key 重复注册 = 覆盖旧的 interval 和回调（方便热改设置）。
        /// </summary>
        /// <param name="runImmediately">true 时立即先跑一次，不必等第一个周期。</param>
        public void Register(string key, TimeSpan interval, Action tick, bool runImmediately = false)
        {
            if (_disposed) return;
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("key 不能为空", nameof(key));
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));

            var entry = new Entry
            {
                Key = key,
                Interval = interval,
                Tick = tick,
                NextDueMs = _now() + IntervalMs(interval),
            };
            _entries[key] = entry;
            RebuildSnapshot();
            EnsureRunning();

            if (runImmediately && CanRun(entry))
            {
                entry.NextDueMs = _now() + IntervalMs(interval);
                Invoke(entry);
            }
        }

        public void Unregister(string key)
        {
            if (_disposed) return;
            if (_entries.Remove(key))
            {
                RebuildSnapshot();
                EnsureRunning();
            }
        }

        /// <summary>暂停但不注销。适合"这个卡片没在显示时别采样"。</summary>
        public void SetPaused(string key, bool paused)
        {
            if (_disposed) return;
            if (!_entries.TryGetValue(key, out var entry)) return;
            if (entry.Paused == paused) return;

            entry.Paused = paused;
            // 恢复时把下一拍推到从现在起算的一个完整周期，避免补跑一堆过期拍
            if (!paused) entry.NextDueMs = _now() + IntervalMs(entry.Interval);
            EnsureRunning();
        }

        public bool IsRegistered(string key) => _entries.ContainsKey(key);

        internal void SetExecutionFilter(Func<string, bool>? allowed)
        {
            if (_disposed) return;
            var previous = _executionAllowed;
            _executionAllowed = allowed;
            long now = _now();
            foreach (var entry in _snapshot)
                if ((previous?.Invoke(entry.Key) == false) && (allowed?.Invoke(entry.Key) ?? true))
                    entry.NextDueMs = now + IntervalMs(entry.Interval);
            EnsureRunning();
        }

        private bool CanRun(Entry entry) => !entry.Paused && (_executionAllowed?.Invoke(entry.Key) ?? true);

        /// <summary>改周期（设置页改了秒数就调它）。</summary>
        public void SetInterval(string key, TimeSpan interval)
        {
            if (_disposed || interval <= TimeSpan.Zero) return;
            if (!_entries.TryGetValue(key, out var entry)) return;
            if (entry.Interval == interval) return;
            entry.Interval = interval;
            entry.NextDueMs = _now() + IntervalMs(interval);
            EnsureRunning();
        }

        /// <summary>立刻跑一次（不管下一拍到没到）。</summary>
        public void TriggerNow(string key)
        {
            if (_disposed) return;
            if (_entries.TryGetValue(key, out var entry) && CanRun(entry)) Invoke(entry);
        }

        public void Start() => EnsureRunning();

        public void Stop() => _timer.Stop();

        private void OnTimerTick(object? sender, EventArgs e)
        {
            RunDue();
        }

        internal void RunDue()
        {
            if (_disposed) return;
            long now = _now();

            // 诊断：本定时器挂在 Background 优先级、周期 250ms。若两次心跳相隔远超 250ms，
            // 说明这一拍之前 UI 线程被某个同步操作占住了（全局 WH_MOUSE_LL 钩子随后会把这段
            // 占住时间放大成"整机鼠标停顿"，所以这行日志是那种卡顿的第一现场）。
            long gap = _lastTickMs == 0 ? 0 : now - _lastTickMs;
            _lastTickMs = now;
            if (gap >= _timer.Interval.TotalMilliseconds + 500)
            {
                IslandLog.Debug("Ticker", $"心跳被推迟 {gap}ms（UI 线程此前被占住；本定时器周期 250ms）");
            }

            var list = _snapshot;

            for (int i = 0; i < list.Length; i++)
            {
                var entry = list[i];
                if (!_entries.TryGetValue(entry.Key, out var current) || !ReferenceEquals(current, entry)) continue;
                if (!CanRun(entry)) continue;
                if (now < entry.NextDueMs) continue;

                entry.NextDueMs = now + IntervalMs(entry.Interval);
                Invoke(entry);
            }

            EnsureRunning();
        }

        private static void Invoke(Entry entry)
        {
            long t0 = Environment.TickCount64;
            try
            {
                entry.Tick();
            }
            catch (Exception ex)
            {
                IslandLog.Error("Ticker:" + entry.Key, ex);
            }
            finally
            {
                long cost = Environment.TickCount64 - t0;
                if (cost >= 150)
                {
                    IslandLog.Debug("Ticker", $"订阅 {entry.Key} 占住 UI 线程 {cost}ms（周期 {entry.Interval.TotalSeconds:0.#}s）");
                }
            }
        }

        /// <summary>没有任何活跃订阅就把定时器停掉 —— 待机态不该有周期性唤醒。</summary>
        private void EnsureRunning()
        {
            if (_disposed) return;

            long nextDue = long.MaxValue;
            foreach (var entry in _snapshot)
            {
                if (CanRun(entry)) nextDue = Math.Min(nextDue, entry.NextDueMs);
            }
            if (nextDue == long.MaxValue) { _timer.Stop(); _lastTickMs = 0; return; }
            var interval = TimeSpan.FromMilliseconds(Math.Clamp(nextDue - _now(), _resolution.TotalMilliseconds, int.MaxValue));
            if (_timer.Interval != interval) _timer.Interval = interval;
            if (!_timer.IsEnabled) { _lastTickMs = 0; _timer.Start(); }
        }

        private static long IntervalMs(TimeSpan interval) => Math.Max(1, (long)Math.Ceiling(interval.TotalMilliseconds));

        private void RebuildSnapshot()
        {
            var arr = new Entry[_entries.Count];
            int i = 0;
            foreach (var entry in _entries.Values) arr[i++] = entry;
            _snapshot = arr;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
            _entries.Clear();
            _snapshot = Array.Empty<Entry>();
        }
    }
}
