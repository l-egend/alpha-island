using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace alpha_island.Services;

internal readonly record struct SteamDownloadObservation(DownloadState State, DateTime ChangedLocal);

/// <summary>只读增量消费 Steam 自己的状态事件。日志不提供连续的逐任务进度。</summary>
internal sealed class SteamDownloadLog
{
    private const int MaxReadBytes = 2 * 1024 * 1024;
    private static readonly Regex EventPattern = new(@"^\[(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] AppID (?<id>\d+) (?<event>.+)$", RegexOptions.Compiled);
    private readonly Dictionary<string, SteamDownloadObservation> _states = new(StringComparer.Ordinal);
    private string? _path;
    private long _offset;
    private DateTime _written;
    private DateTime _created;
    private string _partial = "";

    internal SteamDownloadObservation? ForApp(string appId) => _states.TryGetValue(appId, out var value) ? value : null;

    internal void Refresh(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            DateTime written = File.GetLastWriteTimeUtc(path);
            DateTime created = File.GetCreationTimeUtc(path);
            bool reset = !string.Equals(_path, path, StringComparison.OrdinalIgnoreCase) || file.Length < _offset
                || created != _created
                || (file.Length == _offset && written != _written);
            if (!reset && file.Length == _offset) return;
            if (reset || file.Length - _offset > MaxReadBytes)
            {
                _states.Clear(); _partial = "";
                _offset = Math.Max(0, file.Length - MaxReadBytes);
            }
            _path = path;
            bool skipFirstLine = _offset > 0 && _partial.Length == 0 && (reset || file.Length - _offset >= MaxReadBytes);
            file.Position = _offset;
            byte[] bytes = new byte[(int)Math.Min(MaxReadBytes, file.Length - _offset)];
            int read = 0, count;
            while (read < bytes.Length && (count = file.Read(bytes, read, bytes.Length - read)) > 0) read += count;
            _offset += read; _written = written; _created = created;
            string text = _partial + Encoding.UTF8.GetString(bytes, 0, read);
            string[] lines = text.Split('\n');
            _partial = lines[^1].Length <= 4096 ? lines[^1] : "";
            for (int i = skipFirstLine ? 1 : 0; i < lines.Length - 1; i++) ParseLine(lines[i].TrimEnd('\r'));
        }
        catch (IOException) { Reset(); }
        catch (UnauthorizedAccessException) { Reset(); }
    }

    private void Reset() { _states.Clear(); _path = null; _offset = 0; _partial = ""; }

    private void ParseLine(string line)
    {
        Match match = EventPattern.Match(line);
        if (!match.Success || !DateTime.TryParseExact(match.Groups["time"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTime time) || time > DateTime.Now.AddMinutes(1)) return;
        string id = match.Groups["id"].Value, message = match.Groups["event"].Value;
        SteamDownloadObservation? previous = ForApp(id);
        if (previous is { } old && time < old.ChangedLocal) return;
        DownloadState? state = null;
        if (message.Contains("Suspended", StringComparison.OrdinalIgnoreCase)) state = DownloadState.Paused;
        else if (message.StartsWith("state changed : ", StringComparison.Ordinal))
        {
            if (message.Contains("Update Running", StringComparison.Ordinal)) state = DownloadState.Updating;
            else if (message.Contains("Update Queued", StringComparison.Ordinal) || message.Contains("Update Required", StringComparison.Ordinal)) state = DownloadState.Queued;
            else if (message.Contains("Fully Installed", StringComparison.Ordinal)) state = DownloadState.Completed;
            else state = DownloadState.Unknown;
        }
        else if (message.StartsWith("App update changed : ", StringComparison.Ordinal))
        {
            // 暂停后的收尾会依次写 Stopping / None，不能把明确的暂停事件覆盖为运行。
            if (message.Contains("Stopping", StringComparison.Ordinal) || message.EndsWith("None", StringComparison.Ordinal))
                state = previous?.State == DownloadState.Paused ? DownloadState.Paused : DownloadState.Unknown;
            else if (message.Contains("Downloading", StringComparison.Ordinal)) state = DownloadState.Downloading;
            else if (message.Contains("Verifying", StringComparison.Ordinal)) state = DownloadState.Verifying;
            else if (message.Contains("Staging", StringComparison.Ordinal) || message.Contains("Committing", StringComparison.Ordinal)) state = DownloadState.Installing;
            else if (message.Contains("Preallocating", StringComparison.Ordinal) || message.Contains("Reconfiguring", StringComparison.Ordinal)) state = DownloadState.Preparing;
            else if (message.Contains("Running Update", StringComparison.Ordinal)) state = DownloadState.Updating;
        }
        else if (message.StartsWith("update canceled : ", StringComparison.Ordinal)) state = DownloadState.Unknown;
        if (state is { } known) _states[id] = new(known, time);
    }
}
