using System;
using System.Collections.Generic;
using System.Linq;

namespace alpha_island.Core;

public enum DesktopEventKind { Message, Volume, PowerConnected, PowerDisconnected, LowBattery, CriticalBattery, Locked, Unlocked, System, WifiRadio, BluetoothRadio }

public sealed record DesktopNotification(string Key, string AppId, string AppName, string Title, string Body,
    DateTimeOffset Created, DesktopEventKind Kind = DesktopEventKind.Message, uint? WindowsId = null, byte[]? Logo = null,
    bool? RadioEnabled = null)
{
    public bool IsCritical => Kind is DesktopEventKind.LowBattery or DesktopEventKind.CriticalBattery;
    public bool CanOpenApp => Kind == DesktopEventKind.Message && !string.IsNullOrWhiteSpace(AppId);
}

/// <summary>应用筛选与静默只控制展示；审批不经过消息策略。</summary>
internal sealed class DesktopNotificationPolicy
{
    private readonly IslandOptions _options;
    public DesktopNotificationPolicy(IslandOptions options) => _options = options;
    private bool MatchesApp(DesktopNotification item, string key) => Tokens(SettingsRegistry.GetText(_options, key, ""))
        .Any(token => item.AppName.Contains(token, StringComparison.OrdinalIgnoreCase) || item.AppId.Contains(token, StringComparison.OrdinalIgnoreCase));
    internal static string[] Tokens(string text) => text.Split(new[] { ';', '；', ',', '，', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public bool Accepts(DesktopNotification item)
    {
        if (item.Kind != DesktopEventKind.Message) return true;
        if (item.AppId.StartsWith("AlphaIsland.Desktop", StringComparison.OrdinalIgnoreCase) || item.AppName == "Alpha Island") return false;
        if (MatchesApp(item, "NotificationBlockedApps")) return false;
        if (Tokens(SettingsRegistry.GetText(_options, "NotificationAllowedApps", "")).Length > 0 && !MatchesApp(item, "NotificationAllowedApps")) return false;
        var words = Tokens(SettingsRegistry.GetText(_options, "NotificationKeywords", ""));
        return words.Length == 0 || words.Any(word => (item.Title + "\n" + item.Body).Contains(word, StringComparison.OrdinalIgnoreCase));
    }
    public int Priority(DesktopNotification item) => item.IsCritical ? 84 :
        item.Kind != DesktopEventKind.Message ? 72 : MatchesApp(item, "NotificationPriorityApps") ? 82 : 60;
    public bool CanPopup(DesktopNotification item, bool locked)
    {
        if (locked || !Accepts(item)) return false;
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
    public IReadOnlyList<DesktopNotification> History => _history;
    public int PendingCount => _pending.Count;
    public bool Add(DesktopNotification item, bool queue, DesktopNotificationPolicy? policy = null)
    {
        int existing = _history.FindIndex(x => x.Key == item.Key);
        if (existing >= 0)
        {
            _history[existing] = item;
            int pending = _pending.FindIndex(x => x.Key == item.Key);
            if (pending >= 0) _pending[pending] = item;
            return false;
        }
        _history.Insert(0, item);
        while (_history.Count > 100) _history.RemoveAt(_history.Count - 1);
        if (queue)
        {
            _pending.Add(item);
            while (_pending.Count > 50)
            {
                var oldestLowPriority = _pending.OrderBy(x => policy?.Priority(x) ?? (x.IsCritical ? 84 : 60)).ThenBy(x => x.Created).First();
                _pending.Remove(oldestLowPriority);
            }
        }
        return true;
    }
    public DesktopNotification? TakeNext(DesktopNotificationPolicy policy, bool locked, DateTimeOffset now)
    {
        _pending.RemoveAll(x => now - x.Created > TimeSpan.FromMinutes(2) || !policy.CanPopup(x, locked));
        var next = _pending.OrderByDescending(policy.Priority).ThenBy(x => x.Created).FirstOrDefault();
        if (next != null) _pending.Remove(next);
        return next;
    }
    public void Dismiss(string key) { _history.RemoveAll(x => x.Key == key); _pending.RemoveAll(x => x.Key == key); }
    public void DropPending() => _pending.Clear();
    public void Clear() { _history.Clear(); _pending.Clear(); }
}
