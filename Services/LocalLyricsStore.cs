using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using alpha_island.Core;

namespace alpha_island.Services;

public sealed record LocalLyricsEntry(string File, string Title, string Artist, string Album, double Duration);

public sealed class LocalLyricsStore
{
    public const int MaxFileBytes = 1024 * 1024;
    public const int MaxEntries = 1000;
    public string DirectoryPath { get; }
    private string IndexFile => Path.Combine(DirectoryPath, "index.json");
    private readonly object _sync = new();
    private LocalLyricsEntry[]? _entries;
    public LocalLyricsStore(string? directory = null) => DirectoryPath = Path.GetFullPath(directory ?? Path.Combine(AppPaths.ConfigDir, "lyrics", "local"));

    public IReadOnlyList<LocalLyricsEntry> Entries
    {
        get
        {
            lock (_sync)
            {
                if (_entries != null) return _entries;
                try
                {
                    if (!File.Exists(IndexFile) || new FileInfo(IndexFile).Length > 8 * MaxFileBytes) return _entries = [];
                    _entries = (JsonSerializer.Deserialize<LocalLyricsEntry[]>(File.ReadAllText(IndexFile)) ?? [])
                        .Where(e => e != null && e.File is { Length: 68 } && Regex.IsMatch(e.File, @"^[A-F0-9]{64}\.lrc$") && e.Title is { Length: > 0 and <= 300 } && e.Artist is { Length: <= 300 }
                            && e.Album is { Length: <= 300 } && double.IsFinite(e.Duration) && e.Duration >= 0).Take(MaxEntries).ToArray();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { _entries = []; }
                return _entries;
            }
        }
    }

    public void Reload() { lock (_sync) _entries = null; }

    public int Import(IEnumerable<string> files)
    {
        lock (_sync)
        {
            var entries = Entries.ToList();
            var pending = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = entries.Sum(e => File.Exists(Path.Combine(DirectoryPath, e.File)) ? new FileInfo(Path.Combine(DirectoryPath, e.File)).Length : 0);
            var inputs = files.Take(101).ToArray();
            if (inputs.Length > 100) throw new InvalidDataException("每次最多导入 100 份歌词。");
            foreach (string path in inputs)
            {
                if (!string.Equals(Path.GetExtension(path), ".lrc", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("本地歌词请使用 .lrc 文件。");
                string text = ReadText(path);
                if (LrcParser.Parse(text, null) is not { HasLyrics: true, IsSynced: true }) throw new InvalidDataException(Path.GetFileName(path) + " 不包含带时间戳的歌词。");
                var metadata = Metadata(text, Path.GetFileNameWithoutExtension(path));
                if (metadata.Title.Length == 0 || metadata.Title.Length > 300 || metadata.Artist.Length > 300 || metadata.Album.Length > 300)
                    throw new InvalidDataException("歌曲、歌手和专辑标签各限 300 个字符。");
                byte[] content = Encoding.UTF8.GetBytes(text);
                if (content.Length > MaxFileBytes) throw new InvalidDataException("每份歌词转换为 UTF-8 后最多 1 MB。");
                string name = Convert.ToHexString(SHA256.HashData(content)) + ".lrc";
                if (entries.Any(e => e.File == name)) continue;
                if (entries.Count >= MaxEntries || totalBytes + content.Length > 64L * MaxFileBytes)
                    throw new InvalidDataException("本地歌词库最多 1000 首、64 MB。");
                entries.Add(new(name, metadata.Title, metadata.Artist, metadata.Album, metadata.Duration));
                pending[name] = content;
                totalBytes += content.Length;
            }
            if (pending.Count == 0) return 0;
            Directory.CreateDirectory(DirectoryPath);
            var created = new List<string>();
            try
            {
                foreach (var (name, content) in pending)
                {
                    string destination = Path.Combine(DirectoryPath, name);
                    if (!File.Exists(destination)) { File.WriteAllBytes(destination, content); created.Add(destination); }
                }
                AtomicJsonFile.Write(IndexFile, JsonSerializer.Serialize(entries));
                _entries = entries.ToArray();
                return pending.Count;
            }
            catch { foreach (string path in created) { try { File.Delete(path); } catch (IOException) { } } throw; }
        }
    }

    public LyricsDocument? Find(string title, string artist, string album, TimeSpan duration)
    {
        var matches = Entries.Reverse().Where(e => LrcParser.Normalize(e.Title) == LrcParser.Normalize(title))
            .Where(e => artist.Length == 0 || e.Artist.Length > 0 && LrcParser.SplitArtists(artist).Intersect(LrcParser.SplitArtists(e.Artist)).Any())
            .Where(e => album.Length == 0 || e.Album.Length == 0 || LrcParser.Normalize(e.Album) == LrcParser.Normalize(album))
            .Where(e => e.Duration <= 0 || duration.TotalSeconds <= 0 || Math.Abs(e.Duration - duration.TotalSeconds) <= Math.Max(5, duration.TotalSeconds * .05))
            .OrderByDescending(e => e.Artist.Length > 0).ThenByDescending(e => e.Album.Length > 0).ToArray();
        if (artist.Length == 0 && matches.Select(e => LrcParser.Normalize(e.Artist)).Distinct().Count() > 1) return null;
        foreach (var entry in matches)
        {
            try { var doc = LrcParser.Parse(ReadText(Path.Combine(DirectoryPath, entry.File)), null, "本地歌词"); if (doc.HasLyrics) return doc; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        return null;
    }

    internal static string ReadText(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxFileBytes) throw new InvalidDataException("每份歌词最多 1 MB。");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        try { return reader.ReadToEnd(); }
        catch (DecoderFallbackException) { throw new InvalidDataException("歌词请保存为 UTF-8 或带 BOM 的 Unicode 编码。"); }
    }

    internal static (string Title, string Artist, string Album, double Duration) Metadata(string text, string fallback = "")
    {
        string Tag(string name) => Regex.Match(text, @"\[" + name + @":([^\]\r\n]*)\]", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
        string title = Tag("ti"), artist = Tag("ar");
        if (title.Length == 0)
        {
            string[] pieces = fallback.Split(" - ", 2, StringSplitOptions.TrimEntries);
            title = pieces[^1]; if (artist.Length == 0 && pieces.Length == 2) artist = pieces[0];
        }
        double seconds = TimeSpan.TryParseExact(Tag("length"), [@"m\:ss", @"mm\:ss", @"hh\:mm\:ss"], CultureInfo.InvariantCulture, out var length) ? length.TotalSeconds : 0;
        return (title, artist, Tag("al"), seconds);
    }
}
