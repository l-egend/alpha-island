using AlphaIsland.PluginSdk;

namespace alpha_island.Core;
internal static class PluginNetworkRules
{
    internal static bool IsUsable(PluginNetworkSample? sample, DateTimeOffset now) => sample != null
        && now - sample.Timestamp <= TimeSpan.FromSeconds(5) && sample.Timestamp - now <= TimeSpan.FromSeconds(1)
        && double.IsFinite(sample.DownBytesPerSecond) && double.IsFinite(sample.UpBytesPerSecond)
        && sample.DownBytesPerSecond >= 0 && sample.UpBytesPerSecond >= 0 && sample.DownloadCounterBytes is not < 0
        && sample.UploadCounterBytes is not < 0 && sample.TodayDownBytes is not < 0 && sample.TodayUpBytes is not < 0;
}
