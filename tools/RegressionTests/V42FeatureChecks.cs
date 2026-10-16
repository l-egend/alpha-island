using System.Net;
using System.Net.Http;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static async Task V42LyricsAlbumChecks()
    {
        using var service = new LyricsService(new V42AlbumHandler()) { Enabled = true };
        var lyrics = await service.GetLyricsAsync("Same Title", "Same Artist", TimeSpan.FromSeconds(180), CancellationToken.None, "Studio Album");
        Check(lyrics?.Lines.FirstOrDefault()?.Text == "correct studio lyrics", "真实歌词加载链选择当前专辑版本，不能被先返回的同名异版歌词抢占");
    }

    private sealed class V42AlbumHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string json = request.RequestUri!.Host.Contains("lrclib")
                ? "[{\"trackName\":\"Same Title\",\"artistName\":\"Same Artist\",\"albumName\":\"Other Album\",\"duration\":180,\"syncedLyrics\":\"[00:00.00]wrong version lyrics\"},{\"trackName\":\"Same Title\",\"artistName\":\"Same Artist\",\"albumName\":\"Studio Album\",\"duration\":180,\"syncedLyrics\":\"[00:00.00]correct studio lyrics\"}]"
                : "{\"result\":{\"songs\":[]},\"data\":{\"info\":[]}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private static async Task V42ModeLiveCheck()
    {
        await OnMediaDispatcher(async dispatcher =>
        {
            using var service = new SmtcService(dispatcher);
            await service.InitializeAsync();
            Check(service.Current?.SourceApp == "cloudmusic.exe", "实机模式检查选中当前网易云媒体会话");
            Check(service.Current is { CanShuffle: true, CanRepeat: true }, "网易云声明支持的播放模式不因缺少回读值而被隐藏");
            var original = service.Current;
            for (int i = 0; i < 3; i++)
            {
                bool accepted = await service.CyclePlaybackOrderAsync();
                Console.WriteLine($"MODE_REQUEST accepted={accepted} mode={service.Current?.PlaybackOrder} shuffle={service.Current?.CanShuffle} repeat={service.Current?.CanRepeat}");
                if (!accepted) break;
            }
            if (original!.PlaybackOrderKnown)
                for (int i = 0; i < 3 && service.Current?.PlaybackOrder != original.PlaybackOrder; i++) await service.CyclePlaybackOrderAsync();
        });
    }
}
