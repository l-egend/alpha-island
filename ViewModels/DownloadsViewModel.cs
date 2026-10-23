using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using alpha_island.Services;

namespace alpha_island.ViewModels;

internal sealed record DownloadPresentation(DownloadTask? Task = null, double Speed = 0, bool Refreshing = false, string? Finished = null);
internal sealed class DownloadsViewModel : ObservableObject
{
    private readonly Dictionary<string, (long Bytes, DateTime When)> _previous = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _speeds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
    private readonly Queue<string> _completionOrder = new();
    private string? _lastKey, _pendingFinish;
    private DownloadState? _lastState;
    public IReadOnlyList<DownloadTask> Current { get; private set; } = [];
    internal DateTime SampledUtc { get; private set; }
    internal double Speed(DownloadTask task) => task.CanMeasureSpeed ? _speeds.GetValueOrDefault(task.Key) : 0;
    internal DownloadPresentation Update(IReadOnlyList<DownloadTask> tasks, DateTime now)
    {
        Current = tasks.ToArray(); SampledUtc = now.Kind == DateTimeKind.Local ? now.ToUniversalTime() : DateTime.SpecifyKind(now, DateTimeKind.Utc); OnPropertyChanged(nameof(Current));
        var live = tasks.Select(task => task.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (!task.CanMeasureSpeed) { _previous.Remove(task.Key); _speeds.Remove(task.Key); continue; }
            if (_previous.TryGetValue(task.Key, out var previous))
            {
                double seconds = (now - previous.When).TotalSeconds;
                if (seconds > .2 && seconds <= 30 && task.ReceivedBytes >= previous.Bytes)
                {
                    double instant = (task.ReceivedBytes - previous.Bytes) / seconds;
                    _speeds[task.Key] = _speeds.TryGetValue(task.Key, out double smoothed) ? smoothed * .55 + instant * .45 : instant;
                }
                else if (seconds <= 0 || seconds > 30 || task.ReceivedBytes < previous.Bytes) _speeds.Remove(task.Key);
            }
            _previous[task.Key] = (task.ReceivedBytes, now);
        }
        foreach (var key in _previous.Keys.Where(key => !live.Contains(key)).ToArray()) { _previous.Remove(key); _speeds.Remove(key); }
        foreach (var task in tasks.Where(task => task.IsCompleted))
        {
            string id = task.Key + "|" + task.StartLocal.Ticks.ToString(CultureInfo.InvariantCulture);
            if (!_completed.Add(id)) continue;
            _completionOrder.Enqueue(id);
            while (_completionOrder.Count > 512) _completed.Remove(_completionOrder.Dequeue());
            _pendingFinish = task.Name;
        }
        var active = tasks.Where(task => task.IsRunning).Select(task => (DownloadTask?)task).FirstOrDefault();
        if (active is { } running)
        { _lastKey = running.Key; _lastState = running.Status; return new(running, Speed(running), true); }
        if (_pendingFinish is { Length: > 0 } finished)
        { _pendingFinish = null; _lastKey = null; _lastState = null; return new(Finished: finished); }
        var pending = tasks.Where(task => !task.IsCompleted).Select(task => (DownloadTask?)task).FirstOrDefault();
        bool announce = pending is { } waiting && (_lastKey != waiting.Key || _lastState != waiting.Status);
        _lastKey = pending?.Key; _lastState = pending?.Status;
        return announce ? new(pending) : new();
    }
    internal void Reset()
    {
        _previous.Clear(); _speeds.Clear(); _lastKey = null; _lastState = null; _pendingFinish = null;
        Current = []; SampledUtc = default; OnPropertyChanged(nameof(Current));
    }
}
