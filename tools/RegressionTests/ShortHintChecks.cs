using System.Windows;
using System.Windows.Controls;
using alpha_island.Controls;
using alpha_island.Core;

internal static partial class Program
{
    private static void ShortHintChecks(alpha_island.MainWindow window)
    {
        var pill = AgentField<DesktopNotificationPill>(window, "_notificationSummary");
        var shortNotice = new DesktopNotification("short-radio-hint", "", "Alpha Island", "蓝牙", "已开启", DateTimeOffset.Now,
            DesktopEventKind.BluetoothRadio) { RadioEnabled = true };
        pill.SetNotification(shortNotice); pill.SetStackCount(1);
        PrivateCall(window, "ClearDesktopInbox");
        PrivateCall(window, "OnSystemToastsReceived", new[] { shortNotice }, false);
        Console.WriteLine($"Short Bluetooth hint width={pill.Width:0.##}; default={IslandMetrics.PillContentWidth}");
        Check(Math.Abs(pill.Width - IslandMetrics.PillContentWidth) < .1,
            "蓝牙等短提示使用默认内容宽度，不因最小宽度设置而被整体加长");
        Check(Math.Abs((double)ReadWindowMember(window, "CurrentPillWidth")! - IslandMetrics.PillWidth) < .1,
            "短提示的实际岛体同样恢复默认长度");
        foreach (var kind in new[] { DesktopEventKind.WifiRadio, DesktopEventKind.System, DesktopEventKind.DeviceBatteryFull })
        {
            pill.SetNotification(shortNotice with { Kind = kind, Title = "准备就绪", Body = "已完成", RadioEnabled = null });
            Check(Math.Abs(pill.Width - IslandMetrics.PillContentWidth) < .1, kind + " 短提示不使用加长的最小尺寸");
        }
        pill.SetNotification(shortNotice with { Kind = DesktopEventKind.Assistant, RadioEnabled = null, Body = new string('长', 80) });
        Check(pill.Width > IslandMetrics.PillContentWidth && pill.Width + 16 <= IslandMetrics.MaxPillWidth,
            "长通知仍按实际内容加宽，之前的游戏提示裁切修复保留");
        pill.SetNotification(shortNotice);
        Check(Math.Abs(pill.Width - IslandMetrics.PillContentWidth) < .1, "长通知结束后下一条短提示恢复默认宽度，不沿用旧尺寸");
        var options = new IslandOptions();
        SettingsRegistry.SetExtra(options, "SettingsSchemaVersion", 1);
        SettingsRegistry.SetExtra(options, "AirplaneDisplayEnabled", true);
        SettingsRegistry.SetExtra(options, "AirplaneBarEnabled", true);
        SettingsRegistry.SetExtra(options, "AirplaneNotifyEnabled", false);
        SettingsRegistry.Write(options, "NotificationWifiRadio", false);
        SettingsRegistry.Write(options, "NotificationBluetoothRadio", true);
        SettingsRegistry.SetExtra(options, "plugin/example/enabled", true);
        Check(SettingsMigration.Required(options), "已使用新版设置结构的旧用户配置也会触发飞行模式清理");
        var normalized = options.Normalized();
        Check(!normalized.Extras.Keys.Any(k => k is "AirplaneDisplayEnabled" or "AirplaneBarEnabled" or "AirplaneNotifyEnabled")
            && !SettingsMigration.Required(normalized), "飞行模式旧配置被完整移除，迁移完成后不会每次启动重复处理");
        Check(!SettingsRegistry.GetBool(normalized, "NotificationWifiRadio", true)
            && SettingsRegistry.GetBool(normalized, "NotificationBluetoothRadio", false)
            && SettingsRegistry.GetBool(normalized, "plugin/example/enabled", false),
            "移除飞行模式时保留 Wi-Fi、蓝牙和其他插件的原有偏好");
        var settings = new alpha_island.Settings.SettingsWindow(false);
        try
        {
            settings.LoadFrom(normalized);
            Check(settings.FindName("AirplaneDisplayToggle") == null && SettingsRegistry.Find("AirplaneDisplayEnabled") == null,
                "设置界面与设置注册表都不再提供无法识别状态的飞行模式开关");
        }
        finally { settings.ForceClose = true; settings.Close(); }
        Check(window.FindName("BarAirText") == null && typeof(alpha_island.MainWindow).GetMethod("ApplyAirplaneState",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) == null,
            "运行时不保留飞行模式细条图标或仅供伪造状态的通知入口");
        PrivateCall(window, "ClearDesktopInbox");
    }
}
