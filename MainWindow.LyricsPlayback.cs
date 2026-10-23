using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;
using alpha_island.Settings;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;

// Lyric load / karaoke wipe / translation.
namespace alpha_island
{
    public partial class MainWindow
    {
        // ================= 歌词 =================

        private async void StartLyricsFetch(NowPlayingInfo info)
        {
            var service = _lyrics;
            if (service == null || !_options.LyricsEnabled || !info.SupportsLyrics) return;

            string title = info.Title ?? "";
            string artist = info.Artist ?? "";
            if (title.Length == 0) return;

            string key = LyricsIdentity(info);
            // 同一首歌只发一次请求：已经有文档，或者上一次请求还在飞，都直接早退。
            // 有两条触发路径（ApplyOptions 里补抓 与 SMTC 首帧）会在几毫秒内各调一次本方法，
            // 不早退的话第二次会把第一次的 CTS 取消掉 —— 等于白跑一遍、歌词白等一轮。
            if (key == _lyricsKey && (_lyricsDoc != null || _lyricsFetchingKey == key)) return;
            _lyricsKey = key;
            _lyricsFetchingKey = key;

            _lyricsCts?.Cancel();
            _lyricsCts?.Dispose();
            var cts = new CancellationTokenSource();
            _lyricsCts = cts;

            ApplyLyrics(null);

            try
            {
                // SMTC often delivers the new title before its album/duration. Let that
                // short burst settle, and reject results for any superseded metadata.
                await Task.Delay(250, cts.Token);
                if (_smtc?.Current is not { } stable || LyricsIdentity(stable) != key) return;
                if (service.TryGetCached(title, artist, info.Duration, out var cached, info.Album))
                {
                    ApplyLyrics(cached); return;
                }
                // 首次拿不到歌词 ≠ 这首歌没有歌词：网络抖动、某个源临时超时、换曲瞬间 SMTC 给的时长
                // 不可靠（可能是 0 或上一首的）都会导致一次失败。这里最多再重试 2 次、间隔 5s，
                // 期间一旦换歌或被取消就放弃（旧实现一次失败就整首歌不再重试）。
                var duration = info.Duration;
                for (int attempt = 0; ; attempt++)
                {
                    var doc = await service.GetLyricsAsync(title, artist, duration, cts.Token, info.Album);
                    if (cts.IsCancellationRequested) return;

                    // 回来时可能已经换歌了，核对一下再应用
                    var current = _smtc?.Current;
                    if (current == null || !current.SupportsLyrics || LyricsIdentity(current) != key || _lyricsKey != key) return;

                    if (doc != null && doc.HasLyrics)
                    {
                        ApplyLyrics(doc);
                        return;
                    }

                    if (attempt >= LyricsFetchMaxRetry) return;

                    await Task.Delay(LyricsFetchRetryDelayMs, cts.Token);
                    if (cts.IsCancellationRequested) return;

                    // 重试前尽量用最新的时长：换曲瞬间 SMTC 报的 Duration 常为 0 或上一首的，
                    // 那会让打分里的时长项把正确候选扣到阈值以下。
                    var latest = _smtc?.Current;
                    if (latest == null || latest.Title != title) return;
                    if (latest.Duration > TimeSpan.Zero) duration = latest.Duration;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Lyrics] " + ex.Message);
            }
            finally
            {
                // 请求收尾（成功 / 失败 / 换歌被取消）都要放开"在飞"标记，
                // 否则这首歌后续再也发不出请求（比如第一次没命中、需要靠 _lyricsDoc == null 再补抓的场景）。
                if (ReferenceEquals(_lyricsCts, cts) && _lyricsFetchingKey == key) _lyricsFetchingKey = "";
            }
        }

        private static string LyricsIdentity(NowPlayingInfo info) =>
            LyricsService.CacheKey(info.Title, info.Artist, info.Duration, info.Album)
                + "|" + info.SourceApp + "|" + info.SessionId + "|" + info.ContentKind;

        /// <summary>
        /// 应用歌词文档。
        ///
        /// 防抖要点：只有"有无歌词"真的翻转时才去重定向动画。
        /// 曾经因为 SMTC 元数据抖动导致反复 force 重定向，卡片一直停在淡入中途，
        /// 看起来就是个半透明空壳。
        /// </summary>
        private void ApplyLyrics(LyricsDocument? doc)
        {
            _lyricsDoc = doc;
            _lyricIndex = -2;
            _lyricWipe.Rect = Rect.Empty;
            ResetLyricPresentation();

            bool show = LyricsAreaVisible;
            LyricsArea.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            if (show && doc != null)
            {
                RefreshLyricPosition();
            }
            else
            {
                LyricPrev.Text = "";
                LyricCurrentDim.Text = "";
                LyricCurrentBright.Text = "";
                LyricTranslation.Text = "";
                LyricTranslation.Visibility = Visibility.Collapsed;
                LyricNext.Text = "";
                SetPillLyric(_smtc?.Current?.DisplayTitle ?? "Alpha Island");
            }

            if (_lyricsAreaVisible != show)
            {
                _lyricsAreaVisible = show;
                // 只在已经处于卡片态时调整高度；不要因为歌词到了就把用户从胶囊顶进卡片
                if (_state == IslandState.Card && _pages.ActiveId == IslandPageIds.Music)
                    SetState(IslandState.Card, force: true, preferredPage: IslandPageIds.Music);
            }

            SyncCardViewportHeight();
            UpdateKaraokeHook();
        }

        private void UpdateKaraokeHook()
        {
            // AllowsFrameLoop()：细条态挂逐帧逐字歌词没有意义（那里根本没有歌词行），
            // 节能模式下把它写成显式条件，见 MainWindow.Motion.cs。
            bool should = (_state == IslandState.Card || _state == IslandState.Pill)
                          && AllowsFrameLoop()
                          && _options.LyricsEnabled
                          && _lyricsDoc != null
                          && _lyricsDoc.HasLyrics
                          && (_smtc?.Current?.IsPlaying == true);

            if (should && !_karaokeHooked)
            {
                CompositionTarget.Rendering += OnKaraokeTick;
                _karaokeHooked = true;
            }
            else if (!should && _karaokeHooked)
            {
                StopKaraoke();
            }
        }

        private void StopKaraoke()
        {
            if (_karaokeHooked)
            {
                CompositionTarget.Rendering -= OnKaraokeTick;
                _karaokeHooked = false;
            }
            ResetLyricPresentation(); ResetPillLyricRoll();
        }

        private void OnKaraokeTick(object? sender, EventArgs e)
        {
            if (_smtc?.Current?.IsPlaying != true) return;
            RefreshLyricPosition();
        }

        private void RefreshLyricPosition()
        {
            var doc = _lyricsDoc;
            var media = _smtc;
            if (doc == null || !doc.HasLyrics || media == null) return;

            var lines = doc.Lines;
            if (lines.Count == 0) return;

            TimeSpan position = media.EstimatedPosition;
            if (_options.LyricsOffsetMs != 0)
            {
                position += TimeSpan.FromMilliseconds(_options.LyricsOffsetMs);
                if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            }
            int index = Math.Clamp(doc.IndexAt(position), 0, lines.Count - 1);

            if (index != _lyricIndex)
            {
                _lyricIndex = index;
                ApplyLyricTexts(lines, index);
            }
            else SyncLyricTranslation(lines[index]);

            if (_state != IslandState.Card) return;

            var line = lines[index];
            double progress = WordProgress(line.Time.TotalSeconds, line.WordEnds, line.WordChars,
                position.TotalSeconds);

            if (progress < 0)
            {
                double start = line.Time.TotalSeconds;
                double end = index + 1 < lines.Count ? lines[index + 1].Time.TotalSeconds : start + 4.0;
                double span = end - start;
                progress = span > 0.08 ? Math.Clamp((position.TotalSeconds - start) / span, 0, 1) : 0;
            }

            // 擦除宽度取 Bright 层的实际文本宽度；两者文本必须完全一致，否则会错位
            double w = LyricCurrentBright.ActualWidth;
            if (w <= 0) w = LyricCurrentDim.ActualWidth;
            double h = LyricCurrentBright.ActualHeight;
            if (h <= 0) h = IslandMetrics.LyricCurrentHeight;

            _lyricWipe.Rect = new Rect(0, 0, w * progress, h);
        }

        private void ApplyLyricTexts(System.Collections.Generic.IReadOnlyList<LyricLine> lines, int index)
        {
            bool hadLyrics = !string.IsNullOrEmpty(LyricCurrentDim.Text);
            var line = lines[index];
            string main = string.IsNullOrWhiteSpace(line.Text) ? "♪" : line.Text;

            // 两层文本必须逐字相同
            LyricCurrentDim.Text = main;
            LyricCurrentBright.Text = main;

            LyricPrev.Text = index > 0 ? Tidy(lines[index - 1].Text) : "";
            LyricNext.Text = index + 1 < lines.Count ? Tidy(lines[index + 1].Text) : "";
            SyncLyricTranslation(line);
            AnimateLyricChange(hadLyrics);

            // 一级胶囊里只显示当前这一句（用户明确说不要上下句预览）
            SetPillLyric(main);

            // 两层宽度可能因为字体度量差一点，重新裁一次避免首帧错位
            _lyricWipe.Rect = new Rect(0, 0, 0, IslandMetrics.LyricCurrentHeight);
        }

        private static string Tidy(string? text) => string.IsNullOrWhiteSpace(text) ? "♪" : text;

        private void SyncLyricTranslation(LyricLine line)
        {
            string text = _options.ShowTranslation ? (line.Translation ?? "") : "";
            if (LyricTranslation.Text != text) LyricTranslation.Text = text;
            LyricTranslation.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SyncCurrentLyricTranslation()
        {
            if (_lyricsDoc != null && _lyricIndex >= 0 && _lyricIndex < _lyricsDoc.Lines.Count)
                SyncLyricTranslation(_lyricsDoc.Lines[_lyricIndex]);
        }

        /// <summary>
        /// 逐字歌词（网易云 yrc）的擦除进度：按每个字的时长/字数加权，
        /// 字与字之间做线性插值，所以看起来是匀速刷过去的而不是跳。
        /// 返回 -1 表示这首歌没有逐字时间，调用方退回整行线性插值。
        /// </summary>
        private static double WordProgress(double lineStart, double[]? ends, int[]? chars, double position)
        {
            if (ends == null || chars == null || ends.Length == 0 || chars.Length == 0) return -1;

            double total = 0;
            for (int i = 0; i < chars.Length; i++) total += chars[i];
            if (total <= 0) return -1;

            double done = 0;
            int count = Math.Min(ends.Length, chars.Length);
            for (int i = 0; i < count; i++)
            {
                if (position >= ends[i])
                {
                    done += chars[i];
                    continue;
                }

                double segmentStart = i == 0 ? lineStart : ends[i - 1];
                double segmentEnd = ends[i];
                double span = segmentEnd - segmentStart;
                double fraction = span > 0.001 ? Math.Clamp((position - segmentStart) / span, 0, 1) : 0;
                done += chars[i] * fraction;
                break;
            }

            return Math.Clamp(done / total, 0, 1);
        }

    }
}
