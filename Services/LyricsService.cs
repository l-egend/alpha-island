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
        public double MinScore { get; set; } = 42.0;

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

                // 若网易云无版权/纯音乐未收录/匹配失败，无缝回退到酷狗音乐源
                if (doc == null || !doc.HasLyrics)
                {
                    doc = await FetchKugouLyricsAsync(title, artist, duration, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine("[Lyrics] 获取失败: " + ex.Message);
            }

            // 仅对成功拿到歌词的结果持久缓存；网络抖动或偶发未匹配不缓存 null，保留后续重试能力
            if (doc != null && doc.HasLyrics)
            {
                _cache[key] = doc;
            }
            return doc;
        }

        // ---------------- 搜索 ----------------

        private static string CleanTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";
            // 1. 移除 (Live), [Remastered], (feat. xxx), (伴奏), 【Official MV】 等装饰性后缀
            string cleaned = System.Text.RegularExpressions.Regex.Replace(title,
                @"\s*[\(\[\{（【][^\)\]\}）】]*(live|remaster|feat|ft\.|ver|version|edit|explicit|instrumental|伴奏|现场|重置|原版|官方|高清|mv|audio)[^\)\]\}）】]*[\)\]\}）】]",
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // 2. 移除尾部的 " - Live", " - 现场版", " - 伴奏", " - Remastered" 等后缀
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned,
                @"\s*[-—／/]\s*(live|remaster.*|acoustic|instrumental|伴奏|现场.*|原版)$",
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return cleaned.Trim();
        }

        private static bool ContainsAny(string text, params string[] keywords)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var kw in keywords)
            {
                if (text.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 多解打分评估器：全面评估原始曲名、清洗后曲名、以及从曲名拆分出的「歌手 - 歌名」各种排布，
        /// 返回最高置信度得分，彻底解决 SMTC 标题包含歌手时得分被严重稀释导致歌词不匹配的问题。
        /// </summary>
        private static double EvaluateCandidateScore(string title, string clean, string? artist, TimeSpan duration,
            string hitName, string hitArtists, long hitDurationMs)
        {
            double best = ScoreCandidate(title, artist, duration, hitName, hitArtists, hitDurationMs);
            if (clean.Length > 0 && clean != title)
            {
                double s = ScoreCandidate(clean, artist, duration, hitName, hitArtists, hitDurationMs);
                if (s > best) best = s;
            }

            // 智能拆分："歌手 - 歌名" 或 "歌名 - 歌手"
            string[] separators = new[] { " - ", " – ", " — ", " / " };
            foreach (var sep in separators)
            {
                int idx = title.IndexOf(sep, StringComparison.Ordinal);
                if (idx > 0 && idx < title.Length - sep.Length)
                {
                    string p1 = title.Substring(0, idx).Trim();
                    string p2 = title.Substring(idx + sep.Length).Trim();
                    if (p1.Length > 0 && p2.Length > 0)
                    {
                        // 假设 p1 为歌手，p2 为歌名
                        string effArtist1 = !string.IsNullOrWhiteSpace(artist) ? artist : p1;
                        double s1 = ScoreCandidate(p2, effArtist1, duration, hitName, hitArtists, hitDurationMs);
                        if (s1 > best) best = s1;

                        // 假设 p1 为歌名，p2 为歌手
                        string effArtist2 = !string.IsNullOrWhiteSpace(artist) ? artist : p2;
                        double s2 = ScoreCandidate(p1, effArtist2, duration, hitName, hitArtists, hitDurationMs);
                        if (s2 > best) best = s2;
                    }
                }
            }

            return best;
        }

        private async Task<long> SearchSongIdAsync(string title, string artist, TimeSpan duration, CancellationToken ct)
        {
            string clean = CleanTitle(title);
            var artistList = LrcParser.SplitArtists(artist);
            string primaryArtist = artistList.Count > 0 ? artistList[0] : (artist ?? "").Trim();

            var queries = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(primaryArtist))
            {
                queries.Add(title + " " + primaryArtist);
                if (clean.Length > 0 && clean != title) queries.Add(clean + " " + primaryArtist);
            }

            // 处理很多播放器 Title 包含 "歌手 - 歌名" 的常见情况
            int dashIdx = title.IndexOf(" - ", StringComparison.Ordinal);
            if (dashIdx > 0 && dashIdx < title.Length - 3)
            {
                string part1 = title.Substring(0, dashIdx).Trim();
                string part2 = title.Substring(dashIdx + 3).Trim();
                if (part1.Length > 0 && part2.Length > 0)
                {
                    queries.Add(part2 + " " + part1);
                    queries.Add(part2);
                    queries.Add(part1 + " " + part2);
                }
            }

            queries.Add(title);
            if (clean.Length > 0 && clean != title) queries.Add(clean);

            long globalBestId = 0;
            double globalBestScore = -1;

            var seenQueries = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var q in queries)
            {
                if (string.IsNullOrWhiteSpace(q) || !seenQueries.Add(q)) continue;

                var songs = await SearchAsync(q, ct).ConfigureAwait(false);
                if (songs.Count == 0) continue;

                foreach (var s in songs)
                {
                    double score = EvaluateCandidateScore(title, clean, artist, duration, s.Name, s.Artists, s.DurationMs);

                    if (score > globalBestScore)
                    {
                        globalBestScore = score;
                        globalBestId = s.Id;
                    }
                }

                // 若已命中高置信度（>= 80分）结果，无需多余网络往返，直接采纳
                if (globalBestScore >= 80.0)
                {
                    Debug.WriteLine($"[Lyrics] 高置信命中 \"{title}\" -> id={globalBestId} score={globalBestScore:F0} (query=\"{q}\")");
                    return globalBestId;
                }
            }

            if (globalBestId > 0 && globalBestScore >= MinScore)
            {
                Debug.WriteLine($"[Lyrics] 命中 \"{title}\" -> id={globalBestId} score={globalBestScore:F0}");
                return globalBestId;
            }

            Debug.WriteLine($"[Lyrics] 网易云未匹配到 \"{title} - {artist}\" (最高分={globalBestScore:F0}, 阈值={MinScore})");
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

        /// <summary>
        /// 严格候选打分：曲名精确度 > 歌手吻合度 > 时长误差排除同名异曲。
        /// 带有强力伴奏排除、现场版隔离和歌手零重叠惩罚，彻底消除张冠李戴。
        /// </summary>
        public static double ScoreCandidate(string title, string? artist, TimeSpan duration, string hitName, string hitArtists, long hitDurationMs)
        {
            string nt = LrcParser.Normalize(title);
            string nh = LrcParser.Normalize(hitName);
            if (nt.Length == 0 || nh.Length == 0) return -1;

            double score = 0;

            // 1. 伴奏/纯音乐识别与重罚（原曲非伴奏时绝不能错配伴奏）
            bool targetIsInst = ContainsAny(title, "伴奏", "instrumental", "inst", "karaoke", "纯音乐", "off vocal");
            bool hitIsInst = ContainsAny(hitName, "伴奏", "instrumental", "inst", "karaoke", "纯音乐", "off vocal");
            if (!targetIsInst && hitIsInst)
            {
                score -= 60; // 严厉扣分，排除伴奏混淆
            }

            // 2. Live 现场版区分
            bool targetIsLive = ContainsAny(title, "live", "现场", "演唱会");
            bool hitIsLive = ContainsAny(hitName, "live", "现场", "演唱会");
            if (!targetIsLive && hitIsLive) score -= 20;
            else if (targetIsLive && hitIsLive) score += 10;

            // 3. 曲名匹配
            if (nt == nh)
            {
                score += 55;
            }
            else if (nh.Contains(nt) || nt.Contains(nh))
            {
                // 按字数长度比加权，防止"爱"匹配"爱如潮水"这类短串混淆
                double ratio = (double)Math.Min(nt.Length, nh.Length) / Math.Max(nt.Length, nh.Length);
                score += 15.0 + 30.0 * ratio;
            }
            else
            {
                score += 30.0 * Similarity(nt, nh);
            }

            // 4. 艺术家重叠比对
            var wanted = LrcParser.SplitArtists(artist);
            var got = LrcParser.SplitArtists(hitArtists);
            if (wanted.Count > 0)
            {
                int overlap = 0;
                foreach (var w in wanted)
                {
                    foreach (var g in got)
                    {
                        if (w == g || g.Contains(w) || w.Contains(g))
                        {
                            overlap++;
                            break;
                        }
                    }
                }

                if (overlap > 0)
                {
                    score += 35.0 * overlap / wanted.Count;
                }
                else
                {
                    // 目标明确指定了歌手，但候选完全对不上时严厉惩罚，防同名翻唱
                    score -= 35.0;
                }
            }
            else
            {
                // 未提供歌手时保守加微弱分数
                score += 5.0;
            }

            // 5. 时长严格比对
            if (duration > TimeSpan.Zero && hitDurationMs > 0)
            {
                double deltaSec = Math.Abs(duration.TotalSeconds - hitDurationMs / 1000.0);
                if (deltaSec <= 2.0) score += 25;
                else if (deltaSec <= 5.0) score += 16;
                else if (deltaSec <= 10.0) score += 8;
                else if (deltaSec <= 18.0) score -= 8;
                else if (deltaSec > 30.0) score -= 35; // 时长差异过大通常为不同版本或不同曲目
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

        private async Task<LyricsDocument?> FetchKugouLyricsAsync(string title, string artist, TimeSpan duration, CancellationToken ct)
        {
            try
            {
                string clean = CleanTitle(title);
                string q = string.IsNullOrWhiteSpace(artist) ? clean : (clean + " " + artist);
                string searchUrl = $"http://mobilecdn.kugou.com/api/v3/search/song?format=json&keyword={Uri.EscapeDataString(q)}&page=1&pagesize=10";

                using var sResp = await _http.GetAsync(searchUrl, ct).ConfigureAwait(false);
                if (!sResp.IsSuccessStatusCode) return null;

                string sJson = await sResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var sDoc = JsonDocument.Parse(sJson);

                if (!sDoc.RootElement.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("info", out var infoArr) ||
                    infoArr.ValueKind != JsonValueKind.Array) return null;

                string bestHash = "";
                long bestDurMs = (long)duration.TotalMilliseconds;
                double bestScore = -1;

                foreach (var item in infoArr.EnumerateArray())
                {
                    if (item.TryGetProperty("hash", out var h) && h.ValueKind == JsonValueKind.String)
                    {
                        string hash = h.GetString() ?? "";
                        if (string.IsNullOrEmpty(hash)) continue;

                        string songName = item.TryGetProperty("songname", out var sn) ? sn.GetString() ?? "" : "";
                        string singerName = item.TryGetProperty("singername", out var sgn) ? sgn.GetString() ?? "" : "";
                        long durMs = 0;
                        if (item.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number)
                        {
                            durMs = durEl.GetInt64() * 1000;
                        }

                        double score = EvaluateCandidateScore(title, clean, artist, duration, songName, singerName, durMs);

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestHash = hash;
                            bestDurMs = durMs > 0 ? durMs : (long)duration.TotalMilliseconds;
                        }
                    }
                }

                if (string.IsNullOrEmpty(bestHash) || bestScore < MinScore)
                {
                    Debug.WriteLine($"[Lyrics] 酷狗无合规匹配 (最高分={bestScore:F0}, 阈值={MinScore})");
                    return null;
                }

                // 2. 搜索歌词候选
                string candKeyword = Uri.EscapeDataString(clean.Length > 0 ? clean : title);
                string candUrl = $"http://krcs.kugou.com/search?ver=1&man=yes&client=mobi&keyword={candKeyword}&duration={bestDurMs}&hash={bestHash}";
                using var cResp = await _http.GetAsync(candUrl, ct).ConfigureAwait(false);
                if (!cResp.IsSuccessStatusCode) return null;

                string cJson = await cResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var cDoc = JsonDocument.Parse(cJson);

                if (!cDoc.RootElement.TryGetProperty("candidates", out var cands) ||
                    cands.ValueKind != JsonValueKind.Array) return null;

                string candId = "";
                string candKey = "";
                double bestCandScore = -1;

                foreach (var c in cands.EnumerateArray())
                {
                    if (c.TryGetProperty("id", out var idEl) && c.TryGetProperty("accesskey", out var keyEl))
                    {
                        string id = idEl.GetString() ?? idEl.GetRawText().Trim('"');
                        string key = keyEl.GetString() ?? "";
                        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(key)) continue;

                        string cSong = c.TryGetProperty("song", out var cs) ? cs.GetString() ?? "" : "";
                        string cSinger = c.TryGetProperty("singer", out var csi) ? csi.GetString() ?? "" : "";
                        long cDur = 0;
                        if (c.TryGetProperty("duration", out var cd) && cd.ValueKind == JsonValueKind.Number) cDur = cd.GetInt64();

                        double cScore = EvaluateCandidateScore(title, clean, artist, duration, cSong, cSinger, cDur);
                        if (cScore > bestCandScore)
                        {
                            bestCandScore = cScore;
                            candId = id;
                            candKey = key;
                        }
                    }
                }

                if (string.IsNullOrEmpty(candId) || string.IsNullOrEmpty(candKey) || bestCandScore < MinScore)
                {
                    Debug.WriteLine($"[Lyrics] 酷狗歌词候选未达标 (最高分={bestCandScore:F0}, 阈值={MinScore})");
                    return null;
                }

                // 3. 下载歌词
                string dlUrl = $"http://krcs.kugou.com/download?ver=1&client=mobi&fmt=lrc&charset=utf8&id={candId}&accesskey={candKey}";
                using var dlResp = await _http.GetAsync(dlUrl, ct).ConfigureAwait(false);
                if (!dlResp.IsSuccessStatusCode) return null;

                string dlJson = await dlResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var dlDoc = JsonDocument.Parse(dlJson);

                if (!dlDoc.RootElement.TryGetProperty("content", out var contentEl)) return null;
                string base64 = contentEl.GetString() ?? "";
                if (string.IsNullOrEmpty(base64)) return null;

                byte[] lrcBytes = Convert.FromBase64String(base64);
                string lrcText = System.Text.Encoding.UTF8.GetString(lrcBytes);

                var parsed = LrcParser.Parse(lrcText, null, "酷狗");
                if (!parsed.IsSynced && duration > TimeSpan.Zero)
                {
                    parsed = LrcParser.FromPlainText(lrcText, duration, "酷狗");
                }

                if (parsed.HasLyrics)
                {
                    Debug.WriteLine($"[Lyrics] 酷狗源命中 \"{title}\" (score={bestScore:F0})");
                    return parsed;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine("[Lyrics] 酷狗回退异常: " + ex.Message);
            }
            return null;
        }

        public void Dispose() => _http.Dispose();
    }
}
