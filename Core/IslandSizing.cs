using System;

namespace alpha_island.Core
{
    internal static class IslandSizing
    {
        public static bool LyricsCanResize(string mainKey, bool lyricsEnabled, bool hasForegroundContent)
            => mainKey == "music" && lyricsEnabled && !hasForegroundContent;

        public static double PillWidth(bool lyricsCanResize, double lyricWidth)
            => lyricsCanResize ? Math.Clamp(lyricWidth, IslandMetrics.PillWidth, IslandMetrics.MaxPillWidth) : IslandMetrics.PillWidth;

        // 两行网速与 CPU/RAM 仪表，不跟随歌词尺寸。
        public const double SystemPillWidth = 292;
        public const double NetworkSatelliteWidth = 256;
        public const double NetworkSatelliteHeight = 40;
    }
}
