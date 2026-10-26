using alpha_island.Services;

namespace alpha_island.Core;

internal readonly record struct LyricRestSpan(int PreviousIndex, int NextIndex, double StartSeconds, double EndSeconds);

internal static class LyricRestTimeline
{
    internal static bool IsRestMarker(string? text)
    {
        string value = (text ?? "").Trim().Trim(' ', '\t', '[', ']', '(', ')', '（', '）', '【', '】', '·', '—', '-', '~', '…', '.', '♪', '♫', '♬', '♩');
        return value.Length == 0 || value.ToLowerInvariant() is "前奏" or "间奏" or "尾奏" or "纯音乐" or "演奏" or "间奏中" or "instrumental" or "interlude" or "intro" or "outro" or "solo";
    }

    internal static LyricRestSpan? At(LyricsDocument? document, TimeSpan position, TimeSpan duration)
    {
        if (document is not { HasLyrics: true, IsSynced: true } || position < TimeSpan.Zero || duration > TimeSpan.Zero && position >= duration) return null;
        var lines = document.Lines;
        int index = document.IndexAt(position);
        double end = duration > TimeSpan.Zero ? duration.TotalSeconds : double.MaxValue;
        if (index < 0)
        {
            int next = 0;
            while (next < lines.Count && IsRestMarker(lines[next].Text)) next++;
            if (next < lines.Count) end = lines[next].Time.TotalSeconds;
            return end >= 1.5 ? new(-1, next < lines.Count ? next : -1, 0, end) : null;
        }
        if (IsRestMarker(lines[index].Text))
        {
            int previous = index - 1, next = index + 1;
            while (previous >= 0 && IsRestMarker(lines[previous].Text)) previous--;
            while (next < lines.Count && IsRestMarker(lines[next].Text)) next++;
            if (next < lines.Count) end = lines[next].Time.TotalSeconds;
            double start = lines[previous + 1].Time.TotalSeconds;
            return end - start >= 1.5 ? new(previous, next < lines.Count ? next : -1, start, end) : null;
        }
        var line = lines[index];
        if (line.WordEnds is not { Length: > 0 } ends) return null;
        double vocalEnd = ends[^1];
        if (!double.IsFinite(vocalEnd) || vocalEnd < line.Time.TotalSeconds || vocalEnd > TimeSpan.MaxValue.TotalSeconds) return null;
        int following = index + 1;
        while (following < lines.Count && IsRestMarker(lines[following].Text)) following++;
        if (following < lines.Count) end = lines[following].Time.TotalSeconds;
        if (end == double.MaxValue || end - vocalEnd < 4 || position.TotalSeconds < vocalEnd + .6) return null;
        return new(index, following < lines.Count ? following : -1, vocalEnd + .6, end);
    }
}
