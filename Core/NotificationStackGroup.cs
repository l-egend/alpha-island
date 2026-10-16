namespace alpha_island.Core;

/// <summary>A visual stack owns messages from one app, including its Windows/tray identities.</summary>
internal sealed record NotificationStackGroup(string Key, DesktopNotification[] Items)
{
    internal DesktopNotification Latest => Items[0];
    internal static string KeyFor(DesktopNotification item)
    {
        if (item.Kind != DesktopEventKind.Message) return "event:" + item.Key;
        return MessengerIdentity.FromNotification(item) is { } messenger ? "messenger:" + messenger
            : "app:" + (string.IsNullOrWhiteSpace(item.AppId) ? item.AppName : item.AppId).Trim().ToUpperInvariant();
    }
    internal static NotificationStackGroup[] Create(IEnumerable<DesktopNotification> messages) => messages
        .OrderByDescending(item => item.Created).GroupBy(KeyFor, StringComparer.Ordinal)
        .Select(group => new NotificationStackGroup(group.Key, group.ToArray())).ToArray();
}
