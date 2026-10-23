using System.Text.Json;

namespace alpha_island.Core;

/// <summary>Idempotent migration of retired switches into the consolidated settings.</summary>
internal static class SettingsMigration
{
    private const string VersionKey = "SettingsSchemaVersion";
    internal static bool Required(IslandOptions options) => options.Extras == null
        || !options.Extras.TryGetValue(VersionKey, out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int value) || value < 1
        || options.Extras.Keys.Any(key => key is "AirplaneDisplayEnabled" or "AirplaneBarEnabled" or "AirplaneNotifyEnabled")
        || options.UpNextEnabled != options.PauseNotifyEnabled || options.UpNextEnabled != options.PlayNotifyEnabled;

    internal static void Apply(IslandOptions options)
    {
        options.Extras ??= new();
        // Keep the old on-disk fields compatible while exposing one setting and one value.
        bool playback = options.UpNextEnabled || options.PauseNotifyEnabled || options.PlayNotifyEnabled;
        options.UpNextEnabled = options.PauseNotifyEnabled = options.PlayNotifyEnabled = playback;
        Merge(options, "ReduceEffectsEnabled", false, "ReduceMotionEnabled", "PowerSaveEnabled");
        Merge(options, "NotificationMessengerEnabled", true, "NotificationQQEnabled", "NotificationWeChatEnabled");
        foreach (string key in new[] { "AirplaneDisplayEnabled", "AirplaneBarEnabled", "AirplaneNotifyEnabled" }) options.Extras.Remove(key);
        Merge(options, "DailyTrafficEnabled", true, "TrafficMonitorLinkEnabled");
        options.Extras.Remove("Cover3DEnabled");
        options.Extras[VersionKey] = JsonSerializer.SerializeToElement(1);
    }
    private static void Merge(IslandOptions options, string key, bool fallback, params string[] retired)
    {
        if (!options.Extras.ContainsKey(key))
        {
            var values = retired.Where(options.Extras.ContainsKey).Select(name => options.Extras[name]).ToArray();
            options.Extras[key] = JsonSerializer.SerializeToElement(values.Length == 0 ? fallback
                : values.Any(value => SettingDescriptor.ToBool(value, fallback)));
        }
        foreach (string old in retired) options.Extras.Remove(old);
    }
}
