using System;
using System.Collections.Generic;
using System.Linq;

namespace alpha_island.Core;

public enum DesktopEventKind { Message, Volume, PowerConnected, PowerDisconnected, LowBattery, CriticalBattery, Locked, Unlocked, System, WifiRadio, BluetoothRadio, BootTiming, Weather, BluetoothLowBattery, Assistant, DeviceBatteryFull, DeviceCharging }
public enum NotificationImportance { Normal, Important, Critical }

public sealed record DesktopNotification(string Key, string AppId, string AppName, string Title, string Body,
    DateTimeOffset Created, DesktopEventKind Kind = DesktopEventKind.Message, uint? WindowsId = null, byte[]? Logo = null,
    bool? RadioEnabled = null, int? WeatherCode = null, byte[]? ContentImage = null, bool ContentImageIsHero = false,
    NotificationImportance Importance = NotificationImportance.Normal)
{
    public AssistantReminder? Reminder { get; init; }
    public AlphaIsland.PluginSdk.DeviceBatteryKind? DeviceKind { get; init; }
    public bool IsPinned { get; init; }
    public bool IsRead { get; init; }
    public string ActionFeedback { get; init; } = "";
    public bool IsCritical => Kind is DesktopEventKind.LowBattery or DesktopEventKind.CriticalBattery or DesktopEventKind.BluetoothLowBattery;
    public bool CanOpenApp => Kind == DesktopEventKind.Message && !string.IsNullOrWhiteSpace(AppId);
    /// <summary>A one-line display preview; keep the original title and body intact for history.</summary>
    public string PreviewText
    {
        get
        {
            string content = string.IsNullOrWhiteSpace(Title) ? Body : Title;
            return string.IsNullOrWhiteSpace(content) ? "收到一条通知"
                : string.Join(" ", content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}

/// <summary>应用筛选与静默只控制展示；审批不经过消息策略。</summary>
internal sealed class DesktopNotificationPolicy
{
    private readonly IslandOptions _options;
    private readonly Func<bool>? _gameQuiet;
    public DesktopNotificationPolicy(IslandOptions options, Func<bool>? gameQuiet = null) { _options = options; _gameQuiet = gameQuiet; }
    private bool MatchesApp(DesktopNotification item, string key) => Tokens(SettingsRegistry.GetText(_options, key, ""))
        .Any(token => item.AppName.Contains(token, StringComparison.OrdinalIgnoreCase) || item.AppId.Contains(token, StringComparison.OrdinalIgnoreCase));
    internal static string[] Tokens(string text) => text.Split(new[] { ';', '；', ',', '，', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public bool Accepts(DesktopNotification item)
    {
        if (item.Kind != DesktopEventKind.Message) return true;
        if (MessengerIdentity.FromNotification(item) != null
            && !SettingsRegistry.GetBool(_options, "NotificationMessengerEnabled", true)) return false;
        if (item.AppId.StartsWith("AlphaIsland.Desktop", StringComparison.OrdinalIgnoreCase) || item.AppName == "Alpha Island") return false;
        if (MatchesApp(item, "NotificationBlockedApps")) return false;
        if (Tokens(SettingsRegistry.GetText(_options, "NotificationAllowedApps", "")).Length > 0 && !MatchesApp(item, "NotificationAllowedApps")) return false;
        var words = Tokens(SettingsRegistry.GetText(_options, "NotificationKeywords", ""));
        return words.Length == 0 || words.Any(word => (item.Title + "\n" + item.Body).Contains(word, StringComparison.OrdinalIgnoreCase));
    }
    public NotificationImportance Level(DesktopNotification item) => item.IsCritical || item.Importance == NotificationImportance.Critical
        ? NotificationImportance.Critical : item.Importance == NotificationImportance.Important || item.Kind == DesktopEventKind.BootTiming
            || MatchesApp(item, "NotificationPriorityApps") ? NotificationImportance.Important : NotificationImportance.Normal;
    public int Priority(DesktopNotification item) => Level(item) switch
    { NotificationImportance.Critical => 84, NotificationImportance.Important => 82, _ => item.Kind != DesktopEventKind.Message ? 72 : 60 };
    public bool CanPopup(DesktopNotification item, bool locked)
    {
        if (locked || _gameQuiet?.Invoke() == true || !Accepts(item)) return false;
        bool priority = Priority(item) >= 82;
        if (SettingsRegistry.GetBool(_options, "NotificationQuietMode", false))
            return priority && SettingsRegistry.GetBool(_options, "NotificationQuietAllowPriority", true);
        return !SettingsRegistry.GetBool(_options, "NotificationPriorityOnly", false) || priority;
    }
}

/// <summary>有界内存记录与待展示队列。首轮同步只导入，不补弹旧消息。</summary>
internal sealed class DesktopNotificationInbox
{
    private readonly List<DesktopNotification> _history = new();
    private readonly List<DesktopNotification> _pending = new();
    internal event Action? Changed;
    public IReadOnlyList<DesktopNotification> History => _history;
    public int PendingCount => _pending.Count;
    public bool Add(DesktopNotification item, bool queue, DesktopNotificationPolicy? policy = null)
    {
        if (AssistantReminderRules.PendingGroup(item) is { } group)
        {
            static DesktopNotification Merged(DesktopNotification value) => value with
            {
                IsPinned = false, IsRead = true, ActionFeedback = "",
                Body = value.Body + "\n已合并到更新的同类提醒",
                Reminder = value.Reminder! with { Pending = false, Actions = [] }
            };
            var superseded = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < _history.Count; i++)
            {
                var previous = _history[i];
                if (previous.Key == item.Key || AssistantReminderRules.PendingGroup(previous) != group) continue;
                if (previous.Created > item.Created) { item = Merged(item); queue = false; break; }
                _history[i] = Merged(previous); superseded.Add(previous.Key);
            }
            _pending.RemoveAll(value => superseded.Contains(value.Key));
        }
        int existing = _history.FindIndex(x => x.Key == item.Key);
        if (existing >= 0)
        {
            bool requeue = queue && item.Reminder is { Pending: true } && _history[existing].Reminder is { Pending: false };
            _history[existing] = item;
            int pending = _pending.FindIndex(x => x.Key == item.Key);
            if (pending >= 0) _pending[pending] = item;
            else if (requeue) { _pending.Add(item); while (_pending.Count > 50) _pending.RemoveAt(0); }
            Changed?.Invoke();
            return false;
        }
        _history.Insert(0, item);
        while (_history.Count > 100)
        {
            int oldest = _history.FindLastIndex(x => !x.IsPinned);
            _history.RemoveAt(oldest >= 0 ? oldest : _history.Count - 1);
        }
        if (queue)
        {
            _pending.Add(item);
            while (_pending.Count > 50)
            {
                var oldestLowPriority = _pending.OrderBy(x => policy?.Priority(x) ?? (x.IsCritical ? 84 : 60)).ThenBy(x => x.Created).First();
                _pending.Remove(oldestLowPriority);
            }
        }
        Changed?.Invoke(); return true;
    }
    public DesktopNotification? TakeNext(DesktopNotificationPolicy policy, bool locked, DateTimeOffset now)
    {
        _pending.RemoveAll(x => now - x.Created > TimeSpan.FromMinutes(2) || !policy.CanPopup(x, locked));
        var next = _pending.OrderByDescending(policy.Priority).ThenBy(x => x.Created).FirstOrDefault();
        if (next != null) _pending.Remove(next);
        return next;
    }
    public void Dismiss(string key) { _history.RemoveAll(x => x.Key == key); _pending.RemoveAll(x => x.Key == key); Changed?.Invoke(); }
    internal void SetPinned(string key, bool pinned) => Update(key, item => item with { IsPinned = pinned });
    internal void MarkRead(string key) => Update(key, item => item with { IsRead = true });
    internal void MarkAllRead()
    { for (int i = 0; i < _history.Count; i++) _history[i] = _history[i] with { IsRead = true }; Changed?.Invoke(); }
    internal void ResolveReminder(string key, string result = "已处理")
    {
        Update(key, item => item.Reminder is { Pending: true } reminder
            ? item with { IsPinned = false, IsRead = true, Reminder = reminder with { Pending = false, Actions = [] }, Body = item.Body + "\n" + result, ActionFeedback = "" } : item);
        _pending.RemoveAll(item => item.Key == key);
    }
    internal void ExpireReminders(DateTimeOffset now)
    { foreach (var item in _history.Where(n => n.Reminder is { Pending: true } r && r.Expires <= now).ToArray()) ResolveReminder(item.Key, "提醒已过期，请以当前应用状态为准"); }
    private void Update(string key, Func<DesktopNotification, DesktopNotification> update)
    {
        int index = _history.FindIndex(item => item.Key == key);
        if (index < 0) return;
        var next = update(_history[index]); if (next == _history[index]) return;
        _history[index] = next;
        int pending = _pending.FindIndex(item => item.Key == key); if (pending >= 0) _pending[pending] = next;
        Changed?.Invoke();
    }
    internal DesktopNotification[] TakePendingMessages(DesktopNotificationPolicy policy, bool locked, DateTimeOffset now)
    {
        _pending.RemoveAll(item => now - item.Created > TimeSpan.FromMinutes(2) || !policy.CanPopup(item, locked));
        var messages = _pending.Where(item => item.Kind == DesktopEventKind.Message).ToArray();
        _pending.RemoveAll(item => item.Kind == DesktopEventKind.Message); return messages;
    }
    public void DropPending() => _pending.Clear();
    public void Clear(bool keepPinned = false) { if (keepPinned) _history.RemoveAll(n => !n.IsPinned); else _history.Clear(); _pending.Clear(); Changed?.Invoke(); }
}
