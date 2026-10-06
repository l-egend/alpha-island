using System;
using System.Windows;
using Point = System.Windows.Point;

namespace alpha_island.Core;

internal enum MediaWindowKind { None, DesktopPlayer, Browser }

internal static class MediaHandoffPolicy
{
    private static readonly (string Process, string Name, string[] Aliases)[] Players =
    {
        ("cloudmusic", "网易云音乐", new[] { "cloudmusic" }),
        ("QQMusic", "QQ音乐", new[] { "qqmusic" }),
        ("Spotify", "Spotify", new[] { "spotify", "spotifymusic" }),
        ("KuGou", "酷狗音乐", new[] { "kugou" }),
        ("KwMusic", "酷我音乐", new[] { "kwmusic", "kuwomusic" }),
        ("AppleMusic", "Apple Music", new[] { "applemusic", "applemusicwin" }),
    };

    internal static (MediaWindowKind Kind, string Process) Source(string source)
    {
        source = SourceId(source);
        foreach (string browser in new[] { "msedge", "chrome", "firefox", "brave", "opera" })
            if (HasSourceToken(source, browser)) return (MediaWindowKind.Browser, browser);
        if (HasSourceToken(source, "microsoftedge")) return (MediaWindowKind.Browser, "msedge");
        foreach (var player in Players)
            if (Array.Exists(player.Aliases, alias => HasSourceToken(source, alias)))
                return (MediaWindowKind.DesktopPlayer, player.Process);
        return (MediaWindowKind.None, "");
    }

    internal static string? PlayerName(string source)
    {
        source = SourceId(source);
        foreach (var player in Players)
            if (Array.Exists(player.Aliases, alias => HasSourceToken(source, alias))) return player.Name;
        return null;
    }

    private static string SourceId(string source) => source.Trim().Trim('"')
        .Replace('\\', '/').Split('/')[^1];

    // SMTC returns either an executable name or a packaged app ID. Match whole
    // name components so helpers such as QQMusicUpdater are never treated as players.
    private static bool HasSourceToken(string source, string token)
    {
        if (source.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return source[..^4].Equals(token, StringComparison.OrdinalIgnoreCase);
        int start = 0;
        while (start < source.Length)
        {
            int at = source.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            int end = at + token.Length;
            if ((at == 0 || !char.IsLetterOrDigit(source[at - 1]))
                && (end == source.Length || !char.IsLetterOrDigit(source[end]))) return true;
            start = at + 1;
        }
        return false;
    }

    // Pull the edge nearest the island first, then draw the rest of the textured surface in.
    // All rows stay ordered in Y, including on monitors with negative screen coordinates.
    // The caller reverses progress for opening, so opening and closing share one surface.
    internal static Point GeniePoint(Rect window, Rect island, double progress, double u, double v)
    {
        double t = Math.Clamp(progress, 0, 1);
        if (t == 0) return new Point(window.X + window.Width * u, window.Y + window.Height * v);
        if (t == 1) return new Point(island.X + island.Width * u, island.Y + island.Height * v);

        bool above = island.Y + island.Height / 2 <= window.Y + window.Height / 2;
        double distance = above ? v : 1 - v;
        double pinch = Smooth((t - .22 * Smooth(distance)) / (.60 + .18 * distance));
        double width = window.Width + (island.Width - window.Width) * pinch;
        double center = window.X + window.Width / 2
            + (island.X + island.Width / 2 - window.X - window.Width / 2) * pinch;
        double near = Smooth(t / .62), far = Smooth((t - .22) / .78);
        double top = window.Top + (island.Top - window.Top) * (above ? near : far);
        double bottom = window.Bottom + (island.Bottom - window.Bottom) * (above ? far : near);
        return new Point(center + (u - .5) * width, top + (bottom - top) * v);
    }

    internal static double Smooth(double progress)
    {
        double t = Math.Clamp(progress, 0, 1);
        return t * t * (3 - 2 * t);
    }

    internal static double HandoffTravelProgress(double progress, bool opening)
    {
        double t = Math.Clamp(progress, 0, 1);
        if (t == 0 || t == 1) return t;
        return opening ? 1 - (1 - t) * (1 - t) : t * t;
    }

    internal static Point HandoffPoint(Rect window, Rect island, double progress, bool opening, double u, double v)
    {
        double travel = HandoffTravelProgress(progress, opening);
        return GeniePoint(window, island, opening ? 1 - travel : travel, u, v);
    }

}
