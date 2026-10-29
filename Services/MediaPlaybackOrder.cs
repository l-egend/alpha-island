using L = AlphaIsland.PluginSdk.Localization.UiText;
using Windows.Media;

namespace alpha_island.Services;

public enum MediaPlaybackOrder { Sequential, Shuffle, RepeatTrack }

internal static class MediaPlaybackOrders
{
    internal static NowPlayingInfo ApplyNative(NowPlayingInfo info, bool shuffleEnabled, bool repeatEnabled,
        bool? shuffled, MediaPlaybackAutoRepeatMode? repeat, MediaPlaybackOrder? accepted = null)
    {
        bool canShuffle = info.SupportsLyrics && shuffleEnabled, canRepeat = info.SupportsLyrics && repeatEnabled;
        bool known = (!canShuffle || shuffled.HasValue) && (!canRepeat || repeat.HasValue)
            && !(shuffled == true && repeat == MediaPlaybackAutoRepeatMode.Track);
        var mode = repeat == MediaPlaybackAutoRepeatMode.Track ? MediaPlaybackOrder.RepeatTrack
            : shuffled == true ? MediaPlaybackOrder.Shuffle : known ? MediaPlaybackOrder.Sequential : accepted ?? MediaPlaybackOrder.Sequential;
        return info with { CanShuffle = canShuffle, CanRepeat = canRepeat, PlaybackOrder = mode,
            PlaybackOrderKnown = known || accepted.HasValue };
    }

    internal static MediaPlaybackOrder[] Supported(NowPlayingInfo info) => !info.SupportsLyrics ? []
        : !info.CanShuffle && !info.CanRepeat ? []
        : new[] { MediaPlaybackOrder.Sequential }
            .Concat(info.CanShuffle ? [MediaPlaybackOrder.Shuffle] : Array.Empty<MediaPlaybackOrder>())
            .Concat(info.CanRepeat ? [MediaPlaybackOrder.RepeatTrack] : Array.Empty<MediaPlaybackOrder>()).ToArray();

    internal static MediaPlaybackOrder Next(NowPlayingInfo info)
    {
        var modes = Supported(info);
        return modes.Length == 0 ? info.PlaybackOrder : modes[(Array.IndexOf(modes, info.PlaybackOrder) + 1) % modes.Length];
    }

    internal static string Name(MediaPlaybackOrder mode) => mode switch
    { MediaPlaybackOrder.Shuffle => L.T("随机播放"), MediaPlaybackOrder.RepeatTrack => L.T("单曲循环"), _ => L.T("顺序播放") };
    internal static string Glyph(MediaPlaybackOrder mode) => mode switch
    { MediaPlaybackOrder.Shuffle => "\uE8B1", MediaPlaybackOrder.RepeatTrack => "\uE8ED", _ => "\uE8EF" };

    internal static async Task<bool> ChangeAsync(NowPlayingInfo info, MediaPlaybackOrder target,
        Func<bool, Task<bool>> shuffle, Func<MediaPlaybackAutoRepeatMode, Task<bool>> repeat, Func<bool> isCurrent)
    {
        if (!Supported(info).Contains(target) || !isCurrent()) return false;
        if (info.PlaybackOrderKnown && info.PlaybackOrder == target) return true;
        // Unknown state needs both commands. A confirmed mode only needs the changes
        // that differ, while still clearing a conflicting mode before enabling one.
        if (target == MediaPlaybackOrder.Shuffle)
        {
            if (info.CanRepeat && (!info.PlaybackOrderKnown || info.PlaybackOrder == MediaPlaybackOrder.RepeatTrack)
                && (!await repeat(MediaPlaybackAutoRepeatMode.None) || !isCurrent())) return false;
            return isCurrent() && await shuffle(true) && isCurrent();
        }
        if (info.CanShuffle && (!info.PlaybackOrderKnown || info.PlaybackOrder == MediaPlaybackOrder.Shuffle)
            && (!await shuffle(false) || !isCurrent())) return false;
        if (info.CanRepeat && (!info.PlaybackOrderKnown
                || (info.PlaybackOrder == MediaPlaybackOrder.RepeatTrack) != (target == MediaPlaybackOrder.RepeatTrack)))
            return await repeat(target == MediaPlaybackOrder.RepeatTrack
                ? MediaPlaybackAutoRepeatMode.Track : MediaPlaybackAutoRepeatMode.None) && isCurrent();
        return isCurrent();
    }
}

/// <summary>Tracks successful requests separately from unchanged, cached player reports.</summary>
internal sealed class MediaPlaybackOrderTracker
{
    private readonly record struct Accepted(MediaPlaybackOrder Mode, bool? Shuffle, MediaPlaybackAutoRepeatMode? Repeat, bool Known);
    private readonly Dictionary<string, Accepted> _accepted = new();

    internal void Accept(string sessionId, MediaPlaybackOrder mode, bool? previousShuffle,
        MediaPlaybackAutoRepeatMode? previousRepeat) => _accepted[sessionId] = new(mode, previousShuffle, previousRepeat, true);

    internal NowPlayingInfo Observe(NowPlayingInfo info, bool shuffleEnabled, bool repeatEnabled,
        bool? shuffled, MediaPlaybackAutoRepeatMode? repeat)
    {
        bool hasAccepted = _accepted.TryGetValue(info.SessionId, out var accepted);
        var updated = MediaPlaybackOrders.ApplyNative(info, shuffleEnabled, repeatEnabled, shuffled, repeat,
            hasAccepted ? accepted.Mode : null);
        if (!hasAccepted || !updated.CanShuffle && !updated.CanRepeat) return updated;
        bool reported = (!updated.CanShuffle || shuffled.HasValue) && (!updated.CanRepeat || repeat.HasValue);
        if ((!reported && repeat != MediaPlaybackAutoRepeatMode.Track)
            || shuffled == accepted.Shuffle && repeat == accepted.Repeat)
            return updated with { PlaybackOrder = accepted.Mode, PlaybackOrderKnown = accepted.Known };

        // A changed report is authoritative, including mode changes made in the player.
        bool known = reported && !(shuffled == true && repeat == MediaPlaybackAutoRepeatMode.Track);
        _accepted[info.SessionId] = new(updated.PlaybackOrder, shuffled, repeat, known);
        return updated with { PlaybackOrderKnown = known };
    }
}
