namespace alpha_island.Core;

/// <summary>进度展示、音频证据与 SMTC 兜底探测分别调度，避免为了画进度反复做 WinRT IPC。</summary>
internal static class MediaWorkPacing
{
    internal static readonly TimeSpan AudioEvidenceInterval = TimeSpan.FromMilliseconds(250);
    internal static TimeSpan ProgressInterval(bool playing, bool expanded, bool powerSave) => playing && expanded
        ? TimeSpan.FromMilliseconds(100)
        : TimeSpan.FromMilliseconds(powerSave && !expanded ? 1000 : 500);
    internal static TimeSpan MetadataInterval(bool hasMedia, bool playing) => TimeSpan.FromMilliseconds(
        !hasMedia ? 2000 : playing ? 500 : 1000);
    internal static int WatchdogInterval(double silentSeconds, bool healthyCallback) =>
        healthyCallback && silentSeconds >= 30 ? 2000 : 400;
}
