using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace alpha_island.Services
{
    /// <summary>
    /// 网易云歌词获取：用 SMTC 给的「曲名 + 艺术家 + 时长」去搜索歌曲 ID，再拉同步歌词（LRC）。
    ///
    /// 为什么走网络而不是读本地缓存：
    ///  * 网易云 3.x 的歌词不再落地成 .lrc 文件（%LOCALAPPDATA%\Netease\CloudMusic 下翻不到），
    ///    只存在它的私有 webdb.dat 里，格式随版本变，解析不值当。
    ///  * 官方/非官方公开的 web API 稳定且无需登录即可取歌词，还能顺带拿到翻译。
    ///
    /// 只在换曲时请求一次，结果按「曲名|艺术家」缓存，不轮询。
    /// </summary>
    public sealed class LyricsService : IDisposable
    {
        private const string SearchUrl = "https://music.163.com/api/search/get?s={0}&type=1&limit=10";
        // 注意：lv/kv/tv/rv/yv 都是"只要出现就返回对应段落"，取值本身被忽略。
        // 少写 rv 就拿不到 romalrc（罗马音），少写 tv 就拿不到 tlyric（翻译）。
        private const string LyricUrl = "https://music.163.com/api/song/lyric?id={0}&lv=-1&kv=-1&tv=-1&rv=-1&yv=-1";

        private readonly HttpClient _http;
        private readonly ConcurrentDictionary<string, LyricsDocument?> _cache = new();

        /// <summary>低于这个匹配分就不显示歌词，避免张冠李戴。</summary>
        public double MinScore { get; set; } = 55.0;

        public bool Enabled { get; set; } = true;

        public LyricsService()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            // 网易云对空 UA 会返回 403/HTML
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://music.163.com/");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        }

        public bool TryGetCached(string title, string artist, out LyricsDocument? doc)
            => _cache.TryGetValue(CacheKey(title, artist), out doc);

        private static string CacheKey(string title, string artist)
            => LrcParser.Normalize(title) + "|" + LrcParser.Normalize(artist);

        /// <summary>
        /// 取歌词。任何失败（无网络 / 匹配不上 / 纯音乐）都返回 null，不抛异常。
        /// </summary>
        public async Task<LyricsDocument?> GetLyricsAsync(string title, string artist, TimeSpan duration, CancellationToken ct)
        {
            if (!Enabled) return null;
            if (string.IsNullOrWhiteSpace(title)) return null;

            string key = CacheKey(title, artist);
            if (_cache.TryGetValue(key, out var cached)) return cached;

            LyricsDocument? doc = null;
            try
            {
                long songId = await SearchSongIdAsync(title, artist, duration, ct).ConfigureAwait(false);
                if (songId > 0)
                    doc = await FetchLyricAsync(songId, duration, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine("[Lyrics] 获取失败: " + ex.Message);
            }

            // 只缓存成功结果？不行 —— 失败的歌会每次换曲都重试。缓存 null 也一样，
            // 但纯音乐/找不到属于稳定结论，缓存起来是合理的。
            _cache[key] = doc;
            return doc;
        }

        // ---------------- 搜索 ----------------

        private async Task<long> SearchSongIdAsync(string title, string artist, TimeSpan duration, CancellationToken ct)
        {
            // 先用「曲名 + 艺术家」精确搜，搜不到再退回只搜曲名
            string[] queries = string.IsNullOrWhiteSpace(artist)
                ? new[] { title }
                : new[] { title + " " + artist.Split('/', '、')[0], title };

            foreach (var q in queries)
            {
                var songs = await SearchAsync(q, ct).ConfigureAwait(false);
                if (songs.Count == 0) continue;

                long bestId = 0;
                double bestScore = -1;
                foreach (var s in songs)
                {
                    double score = Score(title, artist, duration, s);
                    if (score > bestScore) { bestScore = score; bestId = s.Id; }
                }

                if (bestId > 0 && bestScore >= MinScore)
                {
                    Debug.WriteLine($"[Lyrics] 命中 \"{title}\" -> id={bestId} score={bestScore:F0} (query=\"{q}\")");
                    return bestId;
                }
            }

            Debug.WriteLine($"[Lyrics] 未匹配到 \"{title} - {artist}\"");
            return 0;
        }

        private sealed class SongHit
        {
            public long Id;
            public string Name = "";
            public string Artists = "";
            public long DurationMs;
        }

        private async Task<System.Collections.Generic.List<SongHit>> SearchAsync(string query, CancellationToken ct)
        {
            var list = new System.Collections.Generic.List<SongHit>();
            string url = string.Format(SearchUrl, Uri.EscapeDataString(query));

            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return list;

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("result", out var result)) return list;
            if (result.ValueKind != JsonValueKind.Object) return list;
            if (!result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array) return list;

            foreach (var s in songs.EnumerateArray())
            {
                var hit = new SongHit();
                if (s.TryGetProperty("id", out var idEl)) hit.Id = idEl.GetInt64();
                if (s.TryGetProperty("name", out var nameEl)) hit.Name = nameEl.GetString() ?? "";
                if (s.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number)
                    hit.DurationMs = durEl.GetInt64();

                if (s.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
                {
                    var names = new System.Collections.Generic.List<string>();
                    foreach (var a in artists.EnumerateArray())
                        if (a.TryGetProperty("name", out var an)) names.Add(an.GetString() ?? "");
                    hit.Artists = string.Join("/", names);
                }

                if (hit.Id > 0) list.Add(hit);
            }
            return list;
        }

        /// <summary>给候选打分：曲名一致最重要，其次艺术家重叠，再用时长排除同名异曲。</summary>
        private static double Score(string title, string artist, TimeSpan duration, SongHit hit)
        {
            string nt = LrcParser.Normalize(title);
            string nh = LrcParser.Normalize(hit.Name);
            if (nt.Length == 0 || nh.Length == 0) return -1;

            double score = 0;

            if (nt == nh) score += 55;
            else if (nh.Contains(nt) || nt.Contains(nh)) score += 34;
            else score += 12.0 * Similarity(nt, nh);

            var wanted = LrcParser.SplitArtists(artist);
            var got = LrcParser.SplitArtists(hit.Artists);
            if (wanted.Count > 0)
            {
                int overlap = 0;
                foreach (var w in wanted)
                    foreach (var g in got)
                        if (w == g || g.Contains(w) || w.Contains(g)) { overlap++; break; }

                score += 30.0 * overlap / wanted.Count;
            }
            else
            {
                score += 12;
            }

            if (duration > TimeSpan.Zero && hit.DurationMs > 0)
            {
                double deltaSec = Math.Abs(duration.TotalSeconds - hit.DurationMs / 1000.0);
                if (deltaSec <= 2) score += 25;
                else if (deltaSec <= 5) score += 15;
                else if (deltaSec <= 12) score += 5;
                else if (deltaSec > 40) score -= 25;
            }

            return score;
        }

        /// <summary>很轻量的相似度：二元组 Dice 系数，足够区分同名不同版本。</summary>
        private static double Similarity(string a, string b)
        {
            if (a.Length < 2 || b.Length < 2) return a == b ? 1 : 0;
            var setA = new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < a.Length - 1; i++) setA.Add(a.Substring(i, 2));
            int hit = 0, total = 0;
            var seen = new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < b.Length - 1; i++)
            {
                var g = b.Substring(i, 2);
                if (!seen.Add(g)) continue;
                total++;
                if (setA.Contains(g)) hit++;
            }
            return total == 0 ? 0 : 2.0 * hit / (setA.Count + total);
        }

        // ---------------- 取歌词 ----------------

        private async Task<LyricsDocument?> FetchLyricAsync(long songId, TimeSpan duration, CancellationToken ct)
        {
            string url = string.Format(LyricUrl, songId);
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            // 实测 823 首：这个接口从不返回 nolyric，纯音乐用 pureMusic 标记
            if (IsTrue(doc.RootElement, "pureMusic")) return null;
            if (IsTrue(doc.RootElement, "nolyric")) return null;      // 保险，理论上有
            if (IsTrue(doc.RootElement, "uncollected")) return null;  // 未收录

            string? lrc = GetLyricText(doc.RootElement, "lrc");
            string? tlyric = GetLyricText(doc.RootElement, "tlyric");
            string? romalrc = GetLyricText(doc.RootElement, "romalrc");
            string? yrc = GetLyricText(doc.RootElement, "yrc");

            if (string.IsNullOrWhiteSpace(lrc)) return null;

            var parsed = LrcParser.Parse(lrc, tlyric);

            // 没有时间戳（"sgc":true 的未同步歌词）时按总时长均分，至少能滚起来
            if (!parsed.IsSynced)
                parsed = LrcParser.FromPlainText(lrc, duration, parsed.Source);

            // 日文歌没有中文翻译时，退而用罗马音填充副行。
            // 但罗马音只对日文有意义 —— 中文/粤语歌的 romalrc 是拼音/粤拼，显示出来只是噪音。
            if (!parsed.HasTranslation && LrcParser.ContainsKana(lrc) && !string.IsNullOrWhiteSpace(romalrc))
                parsed = LrcParser.Parse(lrc, romalrc);

            // 逐字歌词：能拿到就用它的精确断句驱动卡拉OK 擦除
            if (!string.IsNullOrWhiteSpace(yrc))
                parsed = parsed.WithWordTimings(LrcParser.ParseWordTimings(yrc));

            return parsed.HasLyrics ? parsed : null;
        }

        private static bool IsTrue(JsonElement root, string field)
            => root.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.True;

        private static string? GetLyricText(JsonElement root, string field)
        {
            if (!root.TryGetProperty(field, out var node)) return null;
            if (node.ValueKind != JsonValueKind.Object) return null;
            if (!node.TryGetProperty("lyric", out var lyric)) return null;
            return lyric.ValueKind == JsonValueKind.String ? lyric.GetString() : null;
        }

        public void Dispose() => _http.Dispose();
    }
}
