using System.Globalization;

namespace alpha_island.Core;

internal static class NotificationTimestamp
{
    internal static string Format(DateTimeOffset created, DateTimeOffset now)
    {
        var local = created.ToLocalTime();
        if (AlphaIsland.PluginSdk.Localization.UiText.IsEnglish)
        {
            var date = now.ToLocalTime().Date;
            string label = local.Date == date ? "Today" : local.Date == date.AddDays(-1) ? "Yesterday"
                : local.ToString(local.Year == date.Year ? "MMM d" : "MMM d, yyyy", AlphaIsland.PluginSdk.Localization.UiText.Culture);
            return label + " " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        var today = now.ToLocalTime().Date;
        string day = local.Date == today ? "今天" : local.Date == today.AddDays(-1) ? "昨天"
            : local.Year == today.Year ? local.ToString("M月d日", CultureInfo.InvariantCulture)
            : local.ToString("yyyy年M月d日", CultureInfo.InvariantCulture);
        return day + " " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}
