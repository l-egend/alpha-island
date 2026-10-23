using System.IO;
using System.Text.Json;

namespace alpha_island.Services;

/// <summary>Retain only the parsed directory, never a browser's full Preferences document.</summary>
internal sealed class BrowserDownloadDirectoryCache
{
    private sealed record Entry(long Length, DateTime Modified, string? Path, long Used);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private long _use;
    internal string? Read(string profile)
    {
        lock (_entries)
        {
            string file = Path.Combine(profile, "Preferences");
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists) { _entries.Remove(file); return null; }
                if (!_entries.TryGetValue(file, out var entry) || entry.Length != info.Length || entry.Modified != info.LastWriteTimeUtc)
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var json = JsonDocument.Parse(stream);
                    string? path = json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("download", out var download)
                        && download.ValueKind == JsonValueKind.Object && download.TryGetProperty("default_directory", out var directory) && directory.ValueKind == JsonValueKind.String
                        ? directory.GetString() : null;
                    entry = new(info.Length, info.LastWriteTimeUtc, path, ++_use);
                }
                else entry = entry with { Used = ++_use };
                _entries[file] = entry;
                if (_entries.Count > 64) _entries.Remove(_entries.MinBy(pair => pair.Value.Used).Key);
                return !string.IsNullOrEmpty(entry.Path) && Directory.Exists(entry.Path) ? entry.Path : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                // Preferences can be replaced midway through a browser write. Retry next sample.
                return null;
            }
        }
    }
}
