using System.Windows.Threading;
using alpha_island.Core;

namespace alpha_island.Services;

/// <summary>Schedules local rules using existing snapshots; owns no hardware scans or API client.</summary>
internal sealed class AssistantReminderService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<AssistantFrame> _read;
    private readonly AssistantReminderEngine _engine = new(TimeZoneInfo.Local);
    private readonly LocalStateFile<AssistantRuleState>? _file;
    private readonly IslandTicker.Subscription? _tick;
    private bool _disposed, _queued, _refreshing;
    private string _saved = "";
    internal event Action<AssistantReminderDelivery[]>? ChangesReady;
    internal bool MeetingActive => _engine.MeetingActive;
    internal bool ShouldOffer(string rule) => _engine.ShouldOffer(rule, DateTimeOffset.Now);
    internal AssistantReminderService(Dispatcher dispatcher, IslandTicker ticker, Func<AssistantFrame> read, string? path)
    {
        _dispatcher = dispatcher; _read = read;
        if (path == null) return;
        _file = new(path, maximumBytes: 131072, encrypted: true);
        _engine.Restore(_file.Read(), DateTimeOffset.UtcNow);
        _tick = ticker.Subscribe("assistant-reminders", TimeSpan.FromSeconds(3), TickNow);
        AgentStatus.ActivityEnded += OnEnded;
    }
    internal void Configure(AssistantRuleOptions options) { _engine.Configure(options); QueueTick(); }
    private void OnEnded(AgentActivityEnded ended)
    {
        if (_dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            _engine.RecordEnded(ended.Owner, ended.ActivityId, ended.When, ended.Failed); TickNow();
        });
    }
    private void QueueTick()
    {
        if (_disposed || _queued || _dispatcher.HasShutdownStarted) return;
        _queued = true; _dispatcher.BeginInvoke(() => { _queued = false; TickNow(); });
    }
    internal void Respond(string rule, string action)
    { if (_disposed) return; _engine.Respond(rule, action, DateTimeOffset.Now); Save(); }
    internal void Defer(DesktopNotification notice)
    { if (_disposed) return; _engine.Defer(notice, DateTimeOffset.Now); Save(); }
    internal void TickNow()
    {
        if (_disposed || _refreshing) return;
        _refreshing = true;
        try
        {
            var frame = _read();
            var deliveries = new List<AssistantReminderDelivery>();
            foreach (var change in _engine.Evaluate(frame))
            {
                deliveries.Add(new(change, change.Notice != null && !frame.Quiet && !frame.Focus && !frame.Locked && _engine.AllowPopup(frame.Now)));
            }
            if (deliveries.Count > 0) ChangesReady?.Invoke(deliveries.ToArray());
            Save();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { IslandLog.Warn("AssistantReminders", "本地提醒暂不可用：" + ex.GetType().Name); }
        finally { _refreshing = false; }
    }
    private void Save()
    {
        if (_file == null) return;
        var snapshot = _engine.Snapshot(); string serialized = System.Text.Json.JsonSerializer.Serialize(snapshot);
        if (serialized == _saved) return;
        _saved = serialized; _file.Write(snapshot);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        AgentStatus.ActivityEnded -= OnEnded;
        _tick?.Dispose(); Save(); _file?.Dispose(); ChangesReady = null;
    }
}
