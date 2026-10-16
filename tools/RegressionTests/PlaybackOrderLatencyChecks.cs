using System.Diagnostics;
using alpha_island.Services;
using Windows.Media;
using Windows.Media.Control;

internal static partial class Program
{
    private static async Task PlaybackOrderLatencyLiveCheck(bool longReadback = false)
    {
        await OnMediaDispatcher(async dispatcher =>
        {
            using var service = new SmtcService(dispatcher);
            await service.InitializeAsync();
            Check(service.Current is { SourceApp: "cloudmusic.exe", CanShuffle: true, CanRepeat: true }, "播放模式计时使用实机网易云会话");
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = manager.GetSessions().First(x => x.SourceAppUserModelId == "cloudmusic.exe");
            var originalPlayback = session.GetPlaybackInfo();
            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            app.Resources["BoolToVisConverter"] = new System.Windows.Controls.BooleanToVisibilityConverter();
            app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                { Source = new Uri("pack://application:,,,/alpha-island;component/Themes/LiquidGlass.xaml") });
            app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                { Source = new Uri("pack://application:,,,/alpha-island;component/Themes/Win11Settings.xaml") });
            var window = new alpha_island.MainWindow(new alpha_island.Core.IslandOptions
                { LyricsEnabled = false, GreetingEnabled = false });
            SetAgentField(window, "_smtc", service);
            PrivateCall(window, "ApplyMedia", service.Current);
            var clock = new Stopwatch();
            bool responsive = true;
            MediaPlaybackOrder target = default;
            long visibleMs = -1;
            service.NowPlayingChanged += (_, info) =>
            {
                PrivateCall(window, "ApplyMedia", info);
                if (clock.IsRunning && info?.PlaybackOrder == target && info.PlaybackOrderKnown && visibleMs < 0)
                    visibleMs = clock.ElapsedMilliseconds;
            };
            try
            {
            for (int i = 0; i < 3; i++)
            {
                target = MediaPlaybackOrders.Next(service.Current!);
                visibleMs = -1;
                clock.Restart();
                PrivateCall(window, "PlaybackOrderButton_Click", window.PlaybackOrderButton, new System.Windows.RoutedEventArgs());
                long commandMs = clock.ElapsedMilliseconds;
                while ((!window.PlaybackOrderButton.IsEnabled || visibleMs < 0) && clock.ElapsedMilliseconds < 2500) await Task.Delay(1);
                clock.Stop();
                var native = session.GetPlaybackInfo();
                responsive &= visibleMs is >= 0 and < 150;
                Console.WriteLine($"SERVICE_MODE target={target} buttonEnabled={window.PlaybackOrderButton.IsEnabled} clickReturnMs={commandMs} visibleMs={visibleMs} current={service.Current?.PlaybackOrder} shuffle={native.IsShuffleActive} repeat={native.AutoRepeatMode}");
                Check(window.PlaybackOrderButton.IsEnabled && (string)window.PlaybackOrderButton.Content == MediaPlaybackOrders.Glyph(target),
                    "真实按钮点击调用播放器并立即更新图标 " + target);
                for (int poll = 0; poll < (longReadback ? 80 : 10); poll++)
                {
                    service.CheckFastPlaybackState();
                    Check(service.Current?.PlaybackOrder == target && (string)window.PlaybackOrderButton.Content == MediaPlaybackOrders.Glyph(target),
                        "旧模式回读不使按钮反跳 " + target + " " + poll);
                    await Task.Delay(longReadback ? 50 : 20);
                }
            }
            Check(responsive, "播放模式无需等待曲目信息刷新，150毫秒内通知岛上按钮");
            }
            finally
            {
                try
                {
                    if (originalPlayback.AutoRepeatMode is MediaPlaybackAutoRepeatMode originalRepeat)
                        await session.TryChangeAutoRepeatModeAsync(originalRepeat).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                    if (originalPlayback.IsShuffleActive is bool originalShuffle)
                        await session.TryChangeShuffleActiveAsync(originalShuffle).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                }
                finally { SetAgentField(window, "_smtc", null); window.Close(); app.Shutdown(); }
            }
        });
    }
}
