using alpha_island.Core;
using alpha_island.Services;

namespace alpha_island;

public partial class MainWindow
{
    private bool _bootTimingRequested;
    private readonly string _bootTimingKey = "startup-boot-" + Guid.NewGuid().ToString("N");
    private async void ShowAutoStartupBootTiming()
    {
        if (_bootTimingRequested || !App.IsAutoStartup || _exiting || !SettingsRegistry.GetBool(_options, "StartupBootTimeNotify", true)) return;
        _bootTimingRequested = true;
        PresentBootTiming(new(null, null, "", "等待 Windows 提交精确记录", Waiting: true));
        try
        {
            BootTimingReading? shown = null;
            var reading = await BootTimingService.ReadWhenReadyAsync(() => _exiting, available =>
            {
                if (_exiting || !SettingsRegistry.GetBool(_options, "StartupBootTimeNotify", true)) return;
                PresentBootTiming(available); shown = available;
            });
            if (_exiting || !SettingsRegistry.GetBool(_options, "StartupBootTimeNotify", true)) return;
            if (shown != reading) PresentBootTiming(reading);
        }
        catch (Exception ex)
        {
            IslandLog.Warn("BootTiming", "开机耗时读取失败：" + ex.Message);
            if (!_exiting) PresentBootTiming(new(null, null, "系统启动记录不可读取", "Windows 启动事件"));
        }
    }

    private void PresentBootTiming(BootTimingReading reading)
    {
        var item = new DesktopNotification(_bootTimingKey, "", "Windows", "本次开机", reading.DisplayText,
            DateTimeOffset.Now, DesktopEventKind.BootTiming);
        // A late Event 100 must update the waiting popup or reappear after that popup has expired.
        if (_desktopCurrent?.Key == item.Key)
        {
            _desktopCurrent = item;
            _notificationSummary.SetNotification(item);
            _desktopExpires = DateTimeOffset.Now.AddSeconds(15);
            _desktopInbox.Add(item, false, _desktopPolicy);
        }
        else
        {
            _desktopInbox.Dismiss(item.Key);
            _desktopInbox.Add(item, _desktopPolicy.CanPopup(item, _sessionLocked), _desktopPolicy);
        }
        StartNotificationUiTimer();
        RefreshDesktopInbox(); TickDesktopNotifications();
        IslandLog.Info("BootTiming", reading.DisplayText + "；" + reading.Source);
    }
}
