namespace alpha_island.Core;

/// <summary>A bounded group of popup messages; history and system-event scheduling remain independent.</summary>
internal sealed class DesktopNotificationBurst
{
    internal const int Capacity = 50;
    private readonly List<DesktopNotification> _items = new();
    internal IReadOnlyList<DesktopNotification> Items => _items;
    internal int Count => _items.Count;
    internal DateTimeOffset Started { get; private set; }
    private DateTimeOffset _lastArrival;
    internal bool CanMerge(DateTimeOffset now) => Count > 0 && now >= _lastArrival
        && now - _lastArrival <= TimeSpan.FromSeconds(6) && now - Started < TimeSpan.FromSeconds(30);
    internal void Begin(DesktopNotification first, DateTimeOffset now)
    { Clear(); Started = _lastArrival = now; Add(first, now); }
    internal void Add(DesktopNotification item, DateTimeOffset now)
    {
        if (item.Kind != DesktopEventKind.Message) return;
        if (Update(item)) return;
        _items.Add(item); _lastArrival = now;
        while (_items.Count > Capacity) _items.RemoveAt(0);
    }
    internal bool Update(DesktopNotification item)
    {
        int index = _items.FindIndex(value => value.Key == item.Key);
        if (index < 0) return false; _items[index] = item; return true;
    }
    internal DesktopNotification? Top(DesktopNotificationPolicy policy) => _items
        .OrderByDescending(policy.Priority).ThenByDescending(item => item.Created).FirstOrDefault();
    internal void Retain(Func<DesktopNotification, bool> predicate) => _items.RemoveAll(item => !predicate(item));
    internal void Remove(string key) => _items.RemoveAll(item => item.Key == key);
    internal void Clear() { _items.Clear(); Started = _lastArrival = default; }
}
