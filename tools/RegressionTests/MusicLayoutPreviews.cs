using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Core;
using alpha_island.Services;
using Point = System.Windows.Point;

internal static partial class Program
{
    // Visual QA fixture: uses the real page, without playback/network/clipboard side effects.
    private static void MusicLayoutPreviews(alpha_island.MainWindow window, Grid root)
    {
        string folder = Path.GetFullPath("bin/music-layout-previews"); Directory.CreateDirectory(folder);
        var options = AgentField<IslandOptions>(window, "_options");
        var oldMedia = AgentField<SmtcService?>(window, "_smtc");
        using var media = new SmtcService(window.Dispatcher);
        SetAgentField(window, "_smtc", media);
        void Save(string name)
        {
            PumpAnimations(70); root.UpdateLayout();
            var full = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); full.Render(root);
            var origin = window.IslandBody.TransformToAncestor(root).Transform(new Point());
            var crop = new CroppedBitmap(full, new Int32Rect((int)Math.Round(origin.X), (int)Math.Round(origin.Y),
                (int)window.IslandBody.ActualWidth, (int)window.IslandBody.ActualHeight));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(crop));
            using var output = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(output);
            Console.WriteLine($"Preview {name}: card={crop.PixelWidth}x{crop.PixelHeight}, titleWidth={window.TitleText.ActualWidth:0}, mode={window.PlaybackOrderButton.Visibility}");
        }
        try
        {
            var info = new NowPlayingInfo
            {
                Title = "夜色之间 · 把这一刻轻轻留在心里", Artist = "Alpha Island / 示例歌手",
                Album = "舒展的音乐时光", SourceApp = "cloudmusic.exe", SessionId = "layout-preview",
                ContentKind = MediaContentKind.Music, State = PlaybackState.Playing,
                Position = TimeSpan.FromSeconds(88), Duration = TimeSpan.FromSeconds(222),
                CanPrevious = true, CanNext = true, CanToggle = true, CanShuffle = true, CanRepeat = true
            };
            media.SetDebugNowPlaying(info); PrivateCall(window, "ApplyMedia", info);
            PrivateCall(window, "ApplyLyrics", (object?)null);
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.Music);
            PrivateCall(window, "SetMusicProgress", .40);
            window.PositionText.Text = "1:28"; window.DurationText.Text = "3:42";
            Save("music-no-lyrics");
            var lines = new[]
            {
                new LyricLine { Time = TimeSpan.Zero, Text = "窗外的云慢慢走远" },
                new LyricLine { Time = TimeSpan.FromSeconds(80), Text = "把这一刻轻轻留在心里", Translation = "Keep this moment close to your heart" },
                new LyricLine { Time = TimeSpan.FromSeconds(100), Text = "让音乐陪我们继续前行" }
            };
            options.ShowTranslation = true;
            PrivateCall(window, "ApplyLyrics", new LyricsDocument(lines, true, true, "离线视觉预览"));
            PrivateCall(window, "ApplyLyricTexts", lines, 1);
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.Music);
            Save("music-with-translation");
            info = info with { Title = "视频与网页媒体 · 播放模式不可用", ContentKind = MediaContentKind.Video,
                SourceApp = "msedge.exe", State = PlaybackState.Paused, CanShuffle = false, CanRepeat = false };
            media.SetDebugNowPlaying(info); PrivateCall(window, "ApplyMedia", info);
            PrivateCall(window, "ApplyLyrics", (object?)null);
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.Music);
            Save("media-without-order");
        }
        finally { SetAgentField(window, "_smtc", oldMedia); }
    }
}
