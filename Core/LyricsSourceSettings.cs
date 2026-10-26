using System.Text.Json;
using System.IO;
using System.Text.RegularExpressions;

namespace alpha_island.Core;

public sealed record LyricsSourceDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "http";
    public bool Enabled { get; init; } = true;
    public string UrlTemplate { get; init; } = "";
    public string Format { get; init; } = "json";
    public string ResultsPath { get; init; } = "";
    public string LyricsPath { get; init; } = "syncedLyrics";
    public string PlainLyricsPath { get; init; } = "plainLyrics";
    public string TranslationPath { get; init; } = "translatedLyrics";
    public string TitlePath { get; init; } = "trackName";
    public string ArtistPath { get; init; } = "artistName";
    public string AlbumPath { get; init; } = "albumName";
    public string DurationPath { get; init; } = "duration";
}

public static class LyricsSourceSettings
{
    public const int MaxSources = 12;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public static List<LyricsSourceDefinition> Defaults() =>
    [
        new() { Id = "netease", Name = "网易云", Kind = "builtin" },
        new() { Id = "lrclib", Name = "LRCLIB", Kind = "builtin" },
        new() { Id = "kugou", Name = "酷狗", Kind = "builtin" }
    ];

    public static List<LyricsSourceDefinition> Read(IslandOptions options)
    {
        if (options.Extras?.TryGetValue("LyricsSources", out var json) != true) return Defaults();
        try { return Validate(json.Deserialize<List<LyricsSourceDefinition>>(JsonOptions) ?? [], strict: false); }
        catch (JsonException) { return Defaults(); }
    }

    public static void Write(IslandOptions options, IEnumerable<LyricsSourceDefinition> sources)
    {
        var validated = Validate(sources, strict: true);
        options.Extras ??= new();
        options.Extras["LyricsSources"] = JsonSerializer.SerializeToElement(validated, JsonOptions);
    }

    public static string Summary(IslandOptions options) => string.Join("，", Read(options).Where(s => s.Enabled).Select(s => s.Name)) is { Length: > 0 } text ? text : "未启用歌词源";

    public static bool IsDefault(LyricsSourceDefinition source) => source.Kind == "builtin";

    public static List<LyricsSourceDefinition> Import(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 1024 * 1024) throw new InvalidDataException("来源文件最多 1 MB。");
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out int number) || number != 1
            || !root.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("来源文件需要 schemaVersion: 1 和 sources 数组。");
        return Validate(sources.Deserialize<List<LyricsSourceDefinition>>(JsonOptions) ?? [], strict: true);
    }

    public static string Export(IEnumerable<LyricsSourceDefinition> sources)
        => JsonSerializer.Serialize(new { schemaVersion = 1, sources = sources.ToArray() }, JsonOptions);

    private static List<LyricsSourceDefinition> Validate(IEnumerable<LyricsSourceDefinition> sources, bool strict)
    {
        var output = new List<LyricsSourceDefinition>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in sources.Take(MaxSources + 1))
        {
            try
            {
                if (item == null) throw new InvalidDataException("来源不能为空。");
                var source = item;
                if (source.Kind == "builtin")
                {
                    var builtin = Defaults().Find(s => s.Id == source.Id) ?? throw new InvalidDataException("不支持的内置来源。");
                    source = builtin with { Enabled = source.Enabled };
                }
                else if (source.Kind == "local") source = new() { Id = "local", Name = "本地歌词", Kind = "local", Enabled = source.Enabled };
                else
                {
                    if (source.Id == null || source.Name == null || source.UrlTemplate == null) throw new InvalidDataException("来源名称、标识和地址不能为 null。");
                    if (source.Kind != "http" || source.Format is not ("json" or "lrc")) throw new InvalidDataException("仅支持 builtin、local、http 来源及 json、lrc 响应。");
                    if (source.Id.Length == 0) source = source with { Id = "http." + Guid.NewGuid().ToString("N") };
                    if (source.Id is "netease" or "lrclib" or "kugou" or "local") throw new InvalidDataException("外部来源不能使用内置来源的标识。");
                    ValidateUrl(source.UrlTemplate);
                    string[] paths = [source.ResultsPath, source.LyricsPath, source.PlainLyricsPath, source.TranslationPath, source.TitlePath, source.ArtistPath, source.AlbumPath, source.DurationPath];
                    if (paths.Any(p => p == null || p.Length > 128 || !Regex.IsMatch(p, @"^[\w.]*$"))) throw new InvalidDataException("响应字段只支持以点分隔的属性名。");
                    if (source.Format == "json" && (source.TitlePath.Length == 0 || source.ArtistPath.Length == 0 || source.LyricsPath.Length + source.PlainLyricsPath.Length == 0))
                        throw new InvalidDataException("JSON 来源需要歌曲、歌手和歌词字段。");
                }
                if (!Regex.IsMatch(source.Id, @"^[A-Za-z0-9._-]{1,80}$") || string.IsNullOrWhiteSpace(source.Name) || source.Name.Length > 60 || source.Name.Any(char.IsControl))
                    throw new InvalidDataException("来源名称限 60 个字符，标识仅可使用字母、数字、点、下划线和短横线。");
                if (!ids.Add(source.Id)) throw new InvalidDataException("来源标识重复：" + source.Id);
                if (output.Count == MaxSources) throw new InvalidDataException($"最多可配置 {MaxSources} 个歌词源。");
                output.Add(source);
            }
            catch (Exception error) when (!strict && error is InvalidDataException or ArgumentException or NullReferenceException) { }
        }
        if (output.Count == 0) { if (strict) throw new InvalidDataException("请至少保留一个来源；可以关闭暂不使用的来源。"); return Defaults(); }
        return output;
    }

    internal static void ValidateUrl(string template)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > 2048) throw new InvalidDataException("请填写 2048 字符以内的接口地址。");
        string expanded = template;
        foreach (string token in new[] { "title", "artist", "album", "duration" }) expanded = expanded.Replace("{" + token + "}", "sample", StringComparison.Ordinal);
        if (expanded.Contains('{') || expanded.Contains('}') || !Uri.TryCreate(expanded, UriKind.Absolute, out var uri)
            || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0
            || !Uri.TryCreate(template.Split('?')[0].Replace("{title}", "sample").Replace("{artist}", "sample").Replace("{album}", "sample").Replace("{duration}", "sample"), UriKind.Absolute, out _))
            throw new InvalidDataException("接口需使用 HTTPS，或本机 HTTP；不支持账号密码、片段或未知占位符。");
        int authorityEnd = template.IndexOf('/', template.IndexOf("://", StringComparison.Ordinal) + 3);
        if (authorityEnd < 0) authorityEnd = template.IndexOf('?');
        if (template[..(authorityEnd < 0 ? template.Length : authorityEnd)].Contains('{')) throw new InvalidDataException("占位符只能放在接口路径或查询参数中。");
    }
}
