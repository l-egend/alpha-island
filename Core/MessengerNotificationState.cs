using System.Text.RegularExpressions;

namespace alpha_island.Core;

internal enum MessengerClient { QQ, WeChat }
internal enum MessengerNoticeSource { Windows, Popup, Unread, Attention }

internal static class MessengerIdentity
{
    internal static string AppId(MessengerClient client) => "AlphaIsland.Messenger." + client;
    internal static string Name(MessengerClient client) => client == MessengerClient.QQ ? "QQ" : "微信";
    internal static MessengerClient? FromProcess(string name) => name.ToLowerInvariant() switch
    { "qq" => MessengerClient.QQ, "wechat" or "weixin" => MessengerClient.WeChat, _ => null };
    internal static MessengerClient? FromNotification(DesktopNotification item)
    {
        foreach (var client in Enum.GetValues<MessengerClient>())
            if (item.AppId.Equals(AppId(client), StringComparison.OrdinalIgnoreCase)) return client;
        if (Regex.IsMatch(item.AppName.Trim(), @"^(QQ|腾讯QQ)$", RegexOptions.IgnoreCase)
            || Regex.IsMatch(item.AppId, @"(^|[.\\/!_-])(QQ|QQNT)([.\\/!_-]|$)", RegexOptions.IgnoreCase)) return MessengerClient.QQ;
        if (Regex.IsMatch(item.AppName.Trim(), @"^(微信|WeChat|Weixin)$", RegexOptions.IgnoreCase)
            || Regex.IsMatch(item.AppId, @"(^|[.\\/!_-])(WeChat|Weixin)([.\\/!_-]|$)", RegexOptions.IgnoreCase)) return MessengerClient.WeChat;
        return null;
    }
    internal static MessengerClient? FromTrayName(string name)
    {
        name = Regex.Replace(name.Trim(), @"^\p{Cf}+|\p{Cf}+$", "").Trim();
        // Match the application, not QQ Music or text from an unrelated tray icon.
        if (Regex.IsMatch(name, @"^(QQ|腾讯QQ)(?=$|[\s:：\[(（\d])", RegexOptions.IgnoreCase)) return MessengerClient.QQ;
        if (Regex.IsMatch(name, @"^(微信|WeChat|Weixin)(?=$|[\s:：\[(（\d])", RegexOptions.IgnoreCase)) return MessengerClient.WeChat;
        return null;
    }
    internal static int? UnreadCount(string text)
    {
        text = text.Trim();
        var match = Regex.Match(text, @"(?:(\d+)\s*(?:条\s*)?(?:未读(?:消息)?|新消息|unread(?: messages?)?|new messages?)|(?:未读(?:消息)?|新消息|unread(?: messages?)?|new messages?)\s*[:：]?\s*(\d+))", RegexOptions.IgnoreCase);
        if (!match.Success) match = Regex.Match(text, @"^(?:QQ|腾讯QQ|微信|WeChat|Weixin)\s*[\[(（]\s*(\d+)\s*[\])）]", RegexOptions.IgnoreCase);
        foreach (var group in match.Groups.Cast<Group>().Skip(1))
            if (group.Success && int.TryParse(group.Value, out int count)) return Math.Clamp(count, 0, 99999);
        return Regex.IsMatch(text, @"^(QQ|腾讯QQ|微信|WeChat|Weixin)$", RegexOptions.IgnoreCase) ? 0 : null;
    }
}

/// <summary>Only a rise after a baseline is new. Reading/decreasing/resetting is silent.</summary>
internal sealed class MessengerUnreadTracker
{
    private readonly Dictionary<MessengerClient, int> _counts = new();
    internal bool Observe(MessengerClient client, int count, bool baseline)
    {
        bool known = _counts.TryGetValue(client, out int previous);
        _counts[client] = Math.Max(0, count);
        return !baseline && known && count > previous;
    }
    internal void Forget(MessengerClient client) => _counts.Remove(client);
}

/// <summary>Join two sensors for one message; never merge different messages from one sensor.</summary>
internal sealed class MessengerNotificationDeduplicator
{
    private sealed record Delivery(MessengerClient Client, MessengerNoticeSource Source, int Channels, DesktopNotification Item, DateTimeOffset Seen);
    private readonly List<Delivery> _recent = new();
    internal DesktopNotification? Resolve(DesktopNotification item, MessengerNoticeSource source, DateTimeOffset now)
    {
        if (MessengerIdentity.FromNotification(item) is not { } client) return item;
        _recent.RemoveAll(x => now - x.Seen > TimeSpan.FromSeconds(6));
        bool generic = source is MessengerNoticeSource.Unread or MessengerNoticeSource.Attention;
        int channels = 1 << (int)source;
        var recent = _recent.LastOrDefault(x => x.Client == client);
        if (generic && recent != null)
        {
            if (recent.Source is MessengerNoticeSource.Windows or MessengerNoticeSource.Popup) return null;
            if (now - recent.Seen < TimeSpan.FromSeconds(2)) item = item with { Key = recent.Item.Key };
        }
        if (!generic)
        {
            var fallback = _recent.LastOrDefault(x => x.Client == client && x.Source is MessengerNoticeSource.Unread or MessengerNoticeSource.Attention);
            var duplicate = _recent.LastOrDefault(x => x.Client == client && (x.Channels & channels) == 0
                && x.Source is MessengerNoticeSource.Windows or MessengerNoticeSource.Popup && SameMessage(x.Item, item));
            if (duplicate != null) { item = item with { Key = duplicate.Item.Key }; channels |= duplicate.Channels; }
            else if (fallback != null) { item = item with { Key = fallback.Item.Key }; channels |= fallback.Channels; }
            _recent.RemoveAll(x => x.Client == client && x.Source is MessengerNoticeSource.Unread or MessengerNoticeSource.Attention);
        }
        _recent.RemoveAll(x => x.Item.Key == item.Key);
        _recent.Add(new(client, source, channels, item, now));
        while (_recent.Count > 100) _recent.RemoveAt(0);
        return item;
    }
    private static bool SameMessage(DesktopNotification left, DesktopNotification right) =>
        Regex.Replace(left.Title + "\n" + left.Body, @"\s+", " ").Trim()
            .Equals(Regex.Replace(right.Title + "\n" + right.Body, @"\s+", " ").Trim(), StringComparison.Ordinal);
    internal void Clear() => _recent.Clear();
}

/// <summary>Recognize repeated icon A/B flashing, rather than one theme/hover/icon change.</summary>
internal sealed class MessengerTrayBlinkTracker
{
    private readonly Queue<(string Position, byte[] Pixels, DateTimeOffset Time)> _samples = new();
    private DateTimeOffset _stableSince;
    private bool _alerted;
    internal bool Observe(string position, byte[] pixels, DateTimeOffset now)
    {
        if (_samples.Count > 0 && now - _samples.Last().Time > TimeSpan.FromSeconds(1.5)) Suspend();
        if (_samples.Count > 0 && _samples.Peek().Position != position) Suspend();
        if (_samples.Count == 0 || Difference(_samples.Last().Pixels, pixels) > 4) _stableSince = now;
        if (_alerted && now - _stableSince >= TimeSpan.FromSeconds(4)) _alerted = false;
        _samples.Enqueue((position, pixels, now));
        while (_samples.Count > 12) _samples.Dequeue();
        var transitions = new List<byte[]>();
        foreach (var sample in _samples)
            if (transitions.Count == 0 || Difference(transitions[^1], sample.Pixels) > 12) transitions.Add(sample.Pixels);
        if (transitions.Count < 5) return false;
        var last = transitions.TakeLast(5).ToArray();
        bool blinking = Difference(last[0], last[2]) <= 4 && Difference(last[0], last[4]) <= 4
            && Difference(last[1], last[3]) <= 4 && Difference(last[0], last[1]) > 12;
        if (!blinking) return false;
        if (_alerted) return false;
        _alerted = true; return true;
    }
    private static int Difference(byte[] a, byte[] b) => a.Length != b.Length ? 64 : a.Zip(b).Count(pair => pair.First != pair.Second);
    internal void Suspend() { _samples.Clear(); _stableSince = default; }
    internal void Reset() { Suspend(); _alerted = false; }
}
