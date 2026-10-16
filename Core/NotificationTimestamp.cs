using System.Globalization;

namespace alpha_island.Core;

internal static class NotificationTimestamp
{
    internal static string Format(DateTimeOffset created, DateTimeOffset now)
    {
        var local = created.ToLocalTime();
        var today = now.ToLocalTime().Date;
        string day = local.Date == today ? "今天" : local.Date == today.AddDays(-1) ? "昨天"
            : local.Year == today.Year ? local.ToString("M月d日", CultureInfo.InvariantCulture)
            : local.ToString("yyyy年M月d日", CultureInfo.InvariantCulture);
        return day + " " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}
