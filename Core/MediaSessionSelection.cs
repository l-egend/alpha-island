using System;
using System.Collections.Generic;
using System.Linq;
using alpha_island.Services;

namespace alpha_island.Core;

public enum MediaContentKind { Music, Video, Browser }
public sealed record MediaSessionInfo(string Id, string SourceApp, string Title, PlaybackState State, long ActivatedOrder, bool SystemCurrent = false)
{
    public string SourceName => MediaSourceNames.DisplayName(SourceApp);
}
public static class MediaSourceNames
{
    public static bool IsBrowser(string source) => source.Contains("chrome", StringComparison.OrdinalIgnoreCase)
        || source.Contains("msedge", StringComparison.OrdinalIgnoreCase) || source.Contains("microsoftedge", StringComparison.OrdinalIgnoreCase)
        || source.Contains("firefox", StringComparison.OrdinalIgnoreCase)
        || source.Contains("brave", StringComparison.OrdinalIgnoreCase) || source.Contains("opera", StringComparison.OrdinalIgnoreCase);
    public static string DisplayName(string source)
    {
        if (source.Contains("msedge", StringComparison.OrdinalIgnoreCase) || source.Contains("microsoftedge", StringComparison.OrdinalIgnoreCase)) return "Microsoft Edge";
        if (source.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "Google Chrome";
        if (source.Contains("firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (source.Contains("brave", StringComparison.OrdinalIgnoreCase)) return "Brave";
        if (MediaHandoffPolicy.PlayerName(source) is { } playerName) return playerName;
        return string.IsNullOrWhiteSpace(source) ? "系统媒体" : System.IO.Path.GetFileNameWithoutExtension(source);
    }
    public static string? Select(IReadOnlyList<MediaSessionInfo> sessions, string? selected, string? previous)
    {
        var usable = sessions.Where(s => s.State is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Changing).ToArray();
        if (usable.Any(s => s.Id == selected)) return selected;
        var playing = usable.Where(s => s.State == PlaybackState.Playing).OrderByDescending(s => s.ActivatedOrder)
            .ThenByDescending(s => s.SystemCurrent).ThenByDescending(s => s.Id == previous).FirstOrDefault();
        if (playing != null) return playing.Id;
        if (usable.Any(s => s.Id == previous)) return previous;
        return usable.OrderByDescending(s => s.SystemCurrent).ThenByDescending(s => s.ActivatedOrder).FirstOrDefault()?.Id;
    }
}
