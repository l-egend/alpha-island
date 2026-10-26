using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using alpha_island.Core;

namespace alpha_island.Services;

public sealed partial class LyricsService
{
    private async Task<LyricsDocument?> FetchSourceSafelyAsync(LyricsSourceDefinition source, string title, string artist, TimeSpan duration, string album, CancellationToken ct)
    {
        try
        {
            return source.Kind switch
            {
                "local" => _local.Find(title, artist, album, duration),
                "http" => await FetchExternalSourceAsync(source, title, artist, duration, album, ct).ConfigureAwait(false),
                "builtin" when source.Id == "netease" => await FetchNetEaseAsync(title, artist, duration, ct, album).ConfigureAwait(false),
                "builtin" when source.Id == "lrclib" => await FetchLrclibLyricsAsync(title, artist, duration, ct, album).ConfigureAwait(false),
                "builtin" when source.Id == "kugou" => await FetchKugouLyricsAsync(title, artist, duration, ct, album).ConfigureAwait(false),
                _ => null
            };
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception e) when (e is HttpRequestException or System.IO.IOException or JsonException or InvalidOperationException or FormatException or ArgumentException)
        { System.Diagnostics.Debug.WriteLine("[Lyrics] 来源不可用：" + source.Name); return null; }
    }

    private async Task<LyricsDocument?> FetchExternalSourceAsync(LyricsSourceDefinition source, string title, string artist, TimeSpan duration, string album, CancellationToken ct)
    {
        string url = source.UrlTemplate;
        foreach (var (key, value) in new[] { ("title", title), ("artist", artist), ("album", album), ("duration", duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)) })
            url = url.Replace("{" + key + "}", Uri.EscapeDataString(value), StringComparison.Ordinal);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > LocalLyricsStore.MaxFileBytes) return null;
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new System.IO.MemoryStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > LocalLyricsStore.MaxFileBytes) return null;
            memory.Write(buffer, 0, read);
        }
        string text = Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length).TrimStart('\uFEFF');
        if (source.Format == "lrc")
        {
            var data = LocalLyricsStore.Metadata(text);
            if (!Matches(data.Title, data.Artist, data.Album, data.Duration)) return null;
            var lyrics = LrcParser.Parse(text, null, source.Name);
            return lyrics.HasLyrics ? lyrics : null;
        }
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 24 });
        JsonElement root = At(document.RootElement, source.ResultsPath);
        var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().Take(100) : new[] { root }.AsEnumerable();
        foreach (var item in items)
        {
            string hitTitle = StringAt(item, source.TitlePath), hitArtist = StringAt(item, source.ArtistPath), hitAlbum = StringAt(item, source.AlbumPath);
            var seconds = At(item, source.DurationPath);
            double hitDuration = seconds.ValueKind == JsonValueKind.Number && seconds.TryGetDouble(out double value) && double.IsFinite(value) ? Math.Max(0, value) : 0;
            if (!Matches(hitTitle, hitArtist, hitAlbum, hitDuration)) continue;
            string synced = StringAt(item, source.LyricsPath), plain = StringAt(item, source.PlainLyricsPath);
            var doc = synced.Length > 0 ? LrcParser.Parse(synced, StringAt(item, source.TranslationPath), source.Name)
                : duration > TimeSpan.Zero ? LrcParser.FromPlainText(plain, duration, source.Name) : null;
            if (doc?.HasLyrics == true) return doc;
        }
        return null;

        bool Matches(string hitTitle, string hitArtist, string hitAlbum, double seconds)
            => hitTitle.Length > 0 && (artist.Length == 0 || hitArtist.Length > 0)
                && seconds <= TimeSpan.MaxValue.TotalSeconds / 2
                && EvaluateCandidateScore(title, CleanTitle(title), artist, duration, hitTitle, hitArtist, (long)(seconds * 1000), album, hitAlbum) >= MinScore;
    }

    private static JsonElement At(JsonElement root, string path)
    {
        if (path.Length == 0) return root;
        foreach (string name in path.Split('.'))
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out root)) return default;
        return root;
    }

    private static string StringAt(JsonElement root, string path)
    {
        if (path.Length == 0) return "";
        var value = At(root, path); return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }
}
