namespace alpha_island.Core;

internal static class GameResourcePolicy
{
    internal static readonly string[] SuspendedServices =
    [
        IslandServiceIds.Cpu, IslandServiceIds.MemoryMonitor, IslandServiceIds.HardwarePower,
        IslandServiceIds.NetworkSpeed, IslandServiceIds.TrafficMonitorLink, IslandServiceIds.DownloadMonitor,
        IslandServiceIds.Telemetry, IslandServiceIds.PrivacyIndicator, IslandServiceIds.Bluetooth,
        IslandServiceIds.Spectrum, IslandServiceIds.MessengerNotifications,
        IslandServiceIds.Volume
    ];

    internal static bool AllowsTick(string key) => key is "focus" or "productivity" or "user-reminders"
        or "assistant-reminders" or "game-plugin-budget";
}
