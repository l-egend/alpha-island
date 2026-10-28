using System.Windows.Threading;
using alpha_island.Core;
using AlphaIsland.PluginSdk;

namespace alpha_island;

public partial class MainWindow
{
    private Controls.DeviceBatteryCard? _deviceBatteryCard;
    private DispatcherTimer? _deviceBatteryViewTimer;
    private readonly DeviceBatteryReminderPolicy _deviceBatteryReminders = new();
    private void RegisterDeviceBatteryPage()
    {
        _deviceBatteryCard = new(); CardViewport.Children.Add(_deviceBatteryCard);
        _deviceBatteryCard.RefreshRequested += async () => await RefreshDeviceBatteryCardAsync(true);
        _deviceBatteryViewTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _deviceBatteryViewTimer.Tick += async (_, _) => await RefreshDeviceBatteryCardAsync(false);
        _pages.Register(new DelegateIslandPage(IslandPageIds.DeviceBatteries, "电池", _deviceBatteryCard, 7, 294,
            activated: () => { if (GameDoNotDisturbActive) return; SyncDeviceBatteryCard(); _deviceBatteryViewTimer.Start(); _ = RefreshDeviceBatteryCardAsync(true); },
            deactivated: () => { if (!_runtimeEnabled) _deviceBatteryViewTimer.Stop(); }));
        if (_runtimeEnabled) _deviceBatteryViewTimer.Start();
        Closed += (_, _) => _deviceBatteryViewTimer.Stop();
    }
    private async Task RefreshDeviceBatteryCardAsync(bool refreshReceiver)
    {
        if (_exiting || GameDoNotDisturbActive) return;
        if (refreshReceiver) _plugins?.InvokeAction("device-battery-refresh");
        if (_bluetooth != null) await _bluetooth.RefreshConnectionStateAsync();
        if (!_exiting) SyncDeviceBatteryCard();
    }
    private void SyncDeviceBatteryCard()
    {
        if (_deviceBatteryCard == null) return;
        var native = _bluetooth?.DeviceBatterySamples ?? Array.Empty<DeviceBatterySample>();
        var now = DateTimeOffset.UtcNow;
        var groups = DeviceBatteryCatalog.Group(native.Concat(_plugins?.DeviceBatteries ?? []), now);
        ObserveDeviceBatteryGroups(groups, now);
        if (_state == IslandState.Card && _pages.ActiveId == IslandPageIds.DeviceBatteries) _deviceBatteryCard.UpdateGroups(groups);
    }

    private void ObserveDeviceBatteryGroups(IReadOnlyList<DeviceBatteryGroup> groups, DateTimeOffset now)
    {
        if (_exiting) return;
        foreach (var alert in _deviceBatteryReminders.Observe(groups, now))
        {
            if (!SettingsRegistry.GetBool(_options, alert.Charging ? "DeviceBatteryChargingEnabled"
                : alert.Full ? "DeviceBatteryFullEnabled" : "BluetoothLowBatteryEnabled", true)) continue;
            var item = new DesktopNotification("device-battery-" + Guid.NewGuid().ToString("N"), "", "设备电量",
                alert.Name + (alert.Charging ? " · 正在充电" : alert.Full ? " · 充满提醒" : " · 电量低"), alert.Detail, now.ToLocalTime(),
                alert.Charging ? DesktopEventKind.DeviceCharging : alert.Full ? DesktopEventKind.DeviceBatteryFull : DesktopEventKind.BluetoothLowBattery)
                { DeviceKind = alert.Kind };
            _desktopInbox.Add(item, _desktopPolicy.CanPopup(item, _sessionLocked), _desktopPolicy);
            StartNotificationUiTimer(); RefreshDesktopInbox(); TickDesktopNotifications();
        }
    }
}
