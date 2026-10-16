using alpha_island.Core;
using alpha_island.Services;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

internal static partial class Program
{
    // Read-only: no notification text, image paths or identifiers are printed.
    private static async Task NotificationAssetLiveChecks()
    {
        var listener = UserNotificationListener.Current;
        Check(listener.GetAccessStatus() == UserNotificationListenerAccessStatus.Allowed, "实机通知读取权限可用");
        var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
        var items = new List<DesktopNotification>();
        foreach (var n in notifications)
        {
            var binding = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric) ?? n.Notification.Visual.Bindings.FirstOrDefault();
            var text = binding?.GetTextElements().Select(t => t.Text ?? "").ToArray() ?? [];
            items.Add(SystemToastService.CreateNotification("live-" + n.Id, n.AppInfo.AppUserModelId,
                n.AppInfo.DisplayInfo.DisplayName, text, n.CreationTime, n.Id, null));
        }
        var enriched = await NotificationPayloadReader.EnrichAsync(items);
        int merged = enriched.Where((item, index) => !ReferenceEquals(item, items[index])).Count();
        Console.WriteLine($"Read-only enrichment: listener={items.Count}, databaseMatched={merged}, bodyImages={enriched.Count(x => x.ContentImage is { Length: > 0 })}");
        if (items.Count > 0) Check(merged > 0, "实机 Windows 通知数据库与官方监听条目对应，并读取原始正文数据");
    }
}
