using alpha_island.Core;
using alpha_island.Services;

namespace alpha_island;

public partial class MainWindow
{
    private MessengerNotificationService? _messengerNotifications;
    private readonly MessengerNotificationDeduplicator _messengerNoticeDedup = new();
    private void StartMessengerNotifications()
    {
        if (_messengerNotifications != null) return;
        _messengerNotifications = new MessengerNotificationService(Dispatcher);
        OnMessengerOptionsChanged();
        _messengerNotifications.Received += OnMessengerNotificationsReceived;
        _messengerNotifications.StatusChanged += RefreshDesktopAccess;
        _messengerNotifications.Start(); StartNotificationUiTimer();
    }
    private void StopMessengerNotifications()
    {
        _messengerNotifications?.Dispose(); _messengerNotifications = null;
        _messengerNoticeDedup.Clear(); RefreshDesktopAccess(); StopNotificationTimerIfUnused();
    }
    private void OnMessengerOptionsChanged()
    {
        bool enabled = SettingsRegistry.GetBool(_options, "NotificationMessengerEnabled", true);
        _messengerNotifications?.SetEnabled(enabled, enabled);
    }
    private void OnMessengerNotificationsReceived(System.Collections.Generic.IReadOnlyList<DesktopNotification> items, MessengerNoticeSource source) =>
        ReceiveDesktopNotifications(items, false, source);
    private void ReceiveDesktopNotifications(System.Collections.Generic.IReadOnlyList<DesktopNotification> messages, bool initial, MessengerNoticeSource source)
    {
        if (_exiting) return;
        foreach (var received in messages)
        {
            if (!_desktopPolicy.Accepts(received)) continue;
            var item = initial ? received : _messengerNoticeDedup.Resolve(received, source, DateTimeOffset.UtcNow);
            bool popup = !initial && _desktopPolicy.CanPopup(received, _sessionLocked);
            if (item == null) continue;
            bool added = _desktopInbox.Add(item, popup, _desktopPolicy);
            // Both sensors may identify one accepted notification. Still remove its native banner.
            if ((added || item.Key != received.Key) && (popup || GameDoNotDisturbActive && !initial) && received.WindowsId.HasValue
                && SettingsRegistry.GetBool(_options, "NotificationReplaceSystemPopup", true)) _suppressSystemPopup?.Invoke(received);
            if (NotificationBurst.Update(item)) RefreshNotificationStackPresentation();
            else if (_desktopCurrent?.Key == item.Key)
            { _desktopCurrent = item; _notificationSummary.SetNotification(item); }
        }
        RefreshDesktopInbox(); TickDesktopNotifications();
    }
}
