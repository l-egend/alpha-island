namespace alpha_island.Core;

internal sealed class NotificationSoundPolicy
{
    private long? _lastSound;
    internal static bool Enabled(IslandOptions options, bool sessionLocked, bool gameQuiet)
        => !sessionLocked && !gameQuiet && !SettingsRegistry.GetBool(options, "NotificationQuietMode", false)
            && SettingsRegistry.GetBool(options, "NotificationSoundEnabled", true)
            && SettingsRegistry.GetInt(options, "NotificationSoundVolume", 25) > 0;

    internal bool TryAccept(IslandNotification notification, bool alreadyActive, long now)
    {
        if (!notification.OpensIsland || notification.Key is "greeting" or "volume" or "brightness" or "clipboard" or "netspeed" or "status"
            || notification.Key.StartsWith("agentstatus", StringComparison.Ordinal)
            || notification.Key.StartsWith("charging", StringComparison.Ordinal)) return false;
        if (alreadyActive && notification.Lifetime != NotificationLifetime.Timed) return false;
        if (_lastSound is long previous && now - previous < 1200) return false;
        _lastSound = now;
        return true;
    }
}
