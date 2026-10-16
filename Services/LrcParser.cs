using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace alpha_island.Services
{
    /// <summary>一行歌词（已带时间戳）。</summary>
    public sealed class LyricLine
    {
        public TimeSpan Time { get; init; }
        public string Text { get; init; } = "";
        /// <summary>翻译（可能为空）。</summary>
        public string Translation { get; set; } = "";

        /// <summary>逐字结束时刻（绝对秒），来自 yrc；没有则 null，改用行内线性插值。</summary>
        public double[]? WordEnds { get; set; }
        /// <summary>每个"字/词"的字符数，与 WordEnds 一一对应，用于按字数加权算擦除进度。</summary>
        public int[]? WordChars { get; set; }
    }

    /// <summary>一首歌的完整歌词，支持按播放位置二分查当前行。</summary>
    public sealed class LyricsDocument
    {
        public IReadOnlyList<LyricLine> Lines { get; }
        public bool IsSynced { get; }
        public bool HasTranslation { get; }
        /// <summary>来源说明，用于界面提示（如 "网易云"）。</summary>
        public string Source { get; }

        public LyricsDocument(IReadOnlyList<LyricLine> lines, bool synced, bool hasTranslation, string source)
        {
            Lines = lines;
            IsSynced = synced;
            HasTranslation = hasTranslation;
            Source = source;
        }

        public bool HasLyrics => Lines.Count > 0;

        /// <summary>
        /// 把 yrc 的逐字时间挂到对应行上（按行首时间容差匹配）。
        /// 匹配不上就保持 null，界面自动退回线性插值 —— 纯装饰性信息，失败不影响功能。
        /// </summary>
        public LyricsDocument WithWordTimings(List<WordTiming>? timings)
        {
            if (timings == null || timings.Count == 0) return this;

            int ti = 0;
            foreach (var line in Lines)
            {
                double lineSec = line.Time.TotalSeconds;
                while (ti < timings.Count && timings[ti].StartSec < lineSec - 0.2) ti++;
                if (ti >= timings.Count) break;

                if (Math.Abs(timings[ti].StartSec - lineSec) <= 0.2 && timings[ti].Ends.Length > 0)
                {
                    line.WordEnds = timings[ti].Ends;
                    line.WordChars = timings[ti].Chars;
                    ti++;
                }
            }
            return this;
        }

        /// <summary>返回 position 时刻应高亮的行号；早于第一行返回 -1。</summary>
        public int IndexAt(TimeSpan position)
        {
            int n = Lines.Count;
            if (n == 0) return -1;
            if (position < Lines[0].Time) return -1;

            int lo = 0, hi = n - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (Lines[mid].Time <= position) { ans = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return ans;
        }
    }

    /// <summary>yrc 一行歌词的逐字时间。</summary>
    public sealed class WordTiming
    {
        public double StartSec;
        /// <summary>每个字/词的绝对结束秒数。</summary>
        public double[] Ends = Array.Empty<double>();
        /// <summary>每个字/词的字符数。</summary>
        public int[] Chars = Array.Empty<int>();
    }

    /// <summary>
    /// LRC 解析。覆盖常见变体：
    ///  * 一行多个时间戳 [00:01.00][00:05.00]同一句
    ///  * 时间精度 [mm:ss] / [mm:ss.x] / [mm:ss.xx] / [mm:ss.xxx]（小数是"秒的小数"，.90 = 900ms）
    ///  * 元数据标签 [ti:] [ar:] [al:] [by:] [offset:]
    ///  * 只有时间戳没有文字（间奏），保留为空行
    ///  * yrc 逐字歌词（可选，用于精确卡拉OK）
    /// </summary>
    public static class LrcParser
    {
        // 时间戳：分:秒[.或:小数]
        private static readonly Regex TimeTag = new(
            @"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]",
            RegexOptions.Compiled);

        // 需要忽略的元数据标签
        private static readonly Regex MetaTag = new(
            @"^\[(ti|ar|al|by|re|ve|length|au|encoding|offset):.*\]$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex OffsetTag = new(
            @"\[offset:\s*([+-]?\d+)\s*\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 制作人行（网易云把它当普通带时间戳歌词下发，会在开头闪一下）。
        // 角色可能用 / 、 , & 串起来，如 "作词/编曲/混音:4st"、"pv/美工:嘛佑"，
        // 所以先切出冒号前的角色串，再逐个查词表 —— 要求"每个角色都在词表里"，
        // 这样普通歌词里的冒号（"我说：你好"）不会被误杀。
        private static readonly Regex CreditTag = new(
            @"^\s*(?<roles>[^:：]{1,60})[:：]\s*\S",
            RegexOptions.Compiled);

        private static readonly HashSet<string> CreditRoles = new(StringComparer.OrdinalIgnoreCase)
        {
            "作词", "作曲", "编曲", "填词", "混音", "母带", "制作人", "出品", "监制", "录音",
            "配唱", "和声", "吉他", "贝斯", "鼓", "弦乐", "键盘", "企划", "统筹", "发行",
            "OP", "SP", "词曲", "词", "曲", "原唱", "翻唱", "后期", "封面", "PV", "曲绘",
            "调校", "策划", "美工", "绘画", "插画", "混缩", "母带处理", "人声", "伴奏",
            "文案", "设计", "视频", "剪辑", "压制", "特别感谢", "鸣谢", "版权"
        };

        // 日文假名（平假名 / 片假名 / 半角片假名）。罗马音只对日文歌有意义
        private static readonly Regex Kana = new(@"[\u3040-\u30FF\uFF66-\uFF9D]", RegexOptions.Compiled);

        // yrc 一行：[起始毫秒,持续毫秒](字起始,字持续,0)文字(字起始,...)文字…
        private static readonly Regex YrcLine = new(
            @"^\[(\d+),(\d+)\](.*)$",
            RegexOptions.Compiled);

        private static readonly Regex YrcWord = new(
            @"\((\d+),(\d+),\d+\)([^(]*)",
            RegexOptions.Compiled);

        public static LyricsDocument Parse(string? lrc, string? translation, string source = "网易云")
        {
            var lines = ParseLines(lrc, out bool synced, out long offsetMs);

            // 翻译按时间戳对齐（翻译文件通常和原文时间戳一一对应）
            bool hasTranslation = false;
            if (!string.IsNullOrWhiteSpace(translation))
            {
                var transLines = ParseLines(translation, out _, out _);
                if (transLines.Count > 0)
                {
                    var map = new Dictionary<long, string>();
                    foreach (var t in transLines)
                    {
                        if (!string.IsNullOrWhiteSpace(t.Text))
                            map[(long)t.Time.TotalMilliseconds] = t.Text;
                    }

                    foreach (var line in lines)
                    {
                        if (map.TryGetValue((long)line.Time.TotalMilliseconds, out var tr))
                        {
                            line.Translation = tr;
                            hasTranslation = true;
                        }
                    }
                }
            }

            if (offsetMs != 0)
            {
                // LRC 约定：offset 为正表示歌词整体提前
                // Decimal ticks keep even malformed 64-bit offsets from overflowing TimeSpan.
                for (int i = 0; i < lines.Count; i++)
                {
                    decimal shiftedTicks = lines[i].Time.Ticks - (decimal)offsetMs * TimeSpan.TicksPerMillisecond;
                    var t = TimeSpan.FromTicks((long)Math.Clamp(shiftedTicks, 0m, long.MaxValue));
                    lines[i] = new LyricLine { Time = t, Text = lines[i].Text, Translation = lines[i].Translation };
                }
            }

            return new LyricsDocument(lines, synced, hasTranslation, source);
        }

        private static List<LyricLine> ParseLines(string? raw, out bool synced, out long offsetMs)
        {
            var result = new List<LyricLine>();
            synced = false;
            offsetMs = 0;

            if (string.IsNullOrWhiteSpace(raw)) return result;

            foreach (var rawLine in raw.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r', ' ', '\t');
                if (line.Length == 0) continue;

                var offsetMatch = OffsetTag.Match(line);
                if (offsetMatch.Success && long.TryParse(offsetMatch.Groups[1].Value, out var off))
                {
                    offsetMs = off;
                    continue;
                }

                if (MetaTag.IsMatch(line.Trim())) continue;

                var matches = TimeTag.Matches(line);
                if (matches.Count == 0)
                {
                    // 完全没有时间戳 -> 未同步歌词，按顺序平铺
                    result.Add(new LyricLine { Time = TimeSpan.Zero, Text = line.Trim() });
                    continue;
                }

                synced = true;

                // 去掉所有时间戳后剩下的就是正文
                string text = TimeTag.Replace(line, "").Trim();

                // 制作人行会挤在 0 秒附近闪一下，直接丢掉
                if (IsCreditLine(text)) continue;

                foreach (Match m in matches)
                {
                    int mm = int.Parse(m.Groups[1].Value);
                    int ss = int.Parse(m.Groups[2].Value);
                    int fracMs = 0;
                    if (m.Groups[3].Success)
                    {
                        var frac = m.Groups[3].Value;
                        fracMs = frac.Length switch
                        {
                            1 => int.Parse(frac) * 100,
                            2 => int.Parse(frac) * 10,
                            _ => int.Parse(frac.Substring(0, 3))
                        };
                    }

                    var time = new TimeSpan(0, 0, mm, ss, fracMs);
                    result.Add(new LyricLine { Time = time, Text = text });
                }
            }

            result.Sort((a, b) => a.Time.CompareTo(b.Time));
            return result;
        }

        /// <summary>把纯文本歌词按固定时长均分（用于无时间戳的兜底展示）。</summary>
        public static LyricsDocument FromPlainText(string text, TimeSpan duration, string source)
        {
            var raw = ParseLines(text, out _, out _);
            var list = new List<LyricLine>();
            if (raw.Count == 0) return new LyricsDocument(list, false, false, source);

            double step = duration.TotalSeconds > 0 ? duration.TotalSeconds / raw.Count : 5.0;
            for (int i = 0; i < raw.Count; i++)
                list.Add(new LyricLine { Time = TimeSpan.FromSeconds(i * step), Text = raw[i].Text });

            return new LyricsDocument(list, true, false, source);
        }

        /// <summary>判断是不是"角色列表: 值"形式的制作人行（每个角色都必须在词表里）。</summary>
        public static bool IsCreditLine(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            var m = CreditTag.Match(text);
            if (!m.Success) return false;

            bool any = false;
            foreach (var part in Regex.Split(m.Groups["roles"].Value, @"[/、,，&+·]"))
            {
                var role = part.Trim();
                if (role.Length == 0) continue;
                if (!CreditRoles.Contains(role)) return false;
                any = true;
            }
            return any;
        }

        /// <summary>解析 yrc（逐字歌词）。格式：[起始毫秒,持续毫秒](字起始,字持续,0)文字(…)文字…
        /// 开头可能有 JSON 制作人员行，跳过。
        /// </summary>
        public static List<WordTiming> ParseWordTimings(string? yrc)
        {
            var result = new List<WordTiming>();
            if (string.IsNullOrWhiteSpace(yrc)) return result;

            foreach (var raw in yrc.Split('\n'))
            {
                var line = raw.TrimEnd('\r', ' ', '\t');
                if (line.Length == 0 || line[0] == '{') continue;   // JSON 制作人员行

                var lm = YrcLine.Match(line);
                if (!lm.Success) continue;

                double startSec = double.Parse(lm.Groups[1].Value) / 1000.0;
                string body = lm.Groups[3].Value;

                var ends = new List<double>();
                var chars = new List<int>();
                foreach (Match w in YrcWord.Matches(body))
                {
                    if (!double.TryParse(w.Groups[1].Value, out double ws)) continue;
                    if (!double.TryParse(w.Groups[2].Value, out double wd)) continue;
                    string text = w.Groups[3].Value;
                    if (text.Length == 0) continue;

                    ends.Add((ws + wd) / 1000.0);
                    chars.Add(text.Length);
                }

                if (ends.Count == 0) continue;
                result.Add(new WordTiming { StartSec = startSec, Ends = ends.ToArray(), Chars = chars.ToArray() });
            }

            return result;
        }

        /// <summary>归一化：全角转半角、去括号补充信息、去标点空格，用于模糊匹配。</summary>
        public static string Normalize(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";

            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                char ch = c;
                // 全角 -> 半角
                if (ch >= '\uFF01' && ch <= '\uFF5E') ch = (char)(ch - 0xFEE0);
                else if (ch == '\u3000') ch = ' ';
                sb.Append(ch);
            }

            var t = sb.ToString().ToLowerInvariant();

            // 去掉 (…) […] （…）【…】 里的内容（Live/Remix/feat 之类）
            t = Regex.Replace(t, @"[\(\[\{（【].*?[\)\]\}）】]", " ");
            // 去掉常见的版本后缀
            t = Regex.Replace(t, @"\b(feat|ft|prod|remix|live|ver|version|inst|off\s*vocal)\b\.?", " ");
            // 只保留中日韩文字、字母、数字
            t = Regex.Replace(t, @"[^\p{L}\p{Nd}]+", "");

            return t;
        }

        /// <summary>把 "A/B"、"A、B"、"A feat. B" 这类演唱者串拆成若干归一化片段。</summary>
        public static List<string> SplitArtists(string? artists)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(artists)) return list;

            foreach (var part in Regex.Split(artists, @"[/、,;&]|\sfeat\.?\s|\sft\.?\s", RegexOptions.IgnoreCase))
            {
                var n = Normalize(part);
                if (n.Length > 0) list.Add(n);
            }
            return list;
        }

        /// <summary>文本里是否含日文假名（用来决定罗马音兜底要不要启用）。</summary>
        public static bool ContainsKana(string? text)
            => !string.IsNullOrEmpty(text) && Kana.IsMatch(text);
    }
}
