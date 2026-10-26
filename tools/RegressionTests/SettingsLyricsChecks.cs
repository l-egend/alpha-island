using System.IO;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Services;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Image = System.Windows.Controls.Image;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBlock = System.Windows.Controls.TextBlock;
using Point = System.Windows.Point;

internal static partial class Program
{
    private static void SettingsLyricsLayoutChecks(string testRoot)
    {
        var settings = new alpha_island.Settings.SettingsWindow(false, greetingAssetsDirectory: Path.Combine(testRoot, "greeting"));
        try
        {
            var options = new IslandOptions(); GreetingPresetCatalog.Select(options, GreetingPresetCatalog.HoshinoSwimsuit);
            settings.LoadFrom(options);
            using var source = ThemeWindowSource(settings, 880, 700, out var host);
            PrivateCall(settings, "SelectPageByTag", "focus"); host.UpdateLayout();
            var title = Descendants<TextBlock>(host).First(t => t.Text == "番茄钟" && t.FontSize > 20);
            var first = Descendants<TextBlock>(host).First(t => t.Text == "专注时长");
            DependencyObject parent = first;
            while (parent is not Border) parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
            var card = (Border)parent;
            double gap = card.TranslatePoint(new Point(), host).Y - title.TranslatePoint(new Point(0, title.ActualHeight), host).Y;
            Console.WriteLine($"Pomodoro title-to-card gap={gap:0.0}px");
            Check(gap >= 16, "番茄钟标题与第一张设置卡片至少保留 16px 间距");
            SaveThemePreview(host, "bin/SettingsLyrics175/previews/focus.png");
            PrivateCall(settings, "SelectPageByTag", "appearance");
            ((Button)settings.FindName("LyricsSourcesCustomize")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            settings.LyricsSourceCard.BringIntoView(); host.UpdateLayout();
            Check(!settings.LyricsSourceText.Text.Contains('→') && settings.LyricsSourceText.Text.Contains("网易云，LRCLIB，酷狗"), "来源列表使用逗号显示默认来源");
            PrivateCall(settings, "MoveLyricsSource", 2, -1);
            Check(settings.LyricsSourceText.Text.StartsWith("网易云，酷狗，LRCLIB"), "设置中的上移操作立即改变来源顺序和摘要");
            PrivateCall(settings, "SetLyricsSourceEnabled", "netease", false);
            Check(settings.LyricsSourceText.Text.StartsWith("酷狗，LRCLIB"), "关闭来源后从启用摘要中移除");
            PrivateCall(settings, "ResetLyricsSources");
            Check(AgentField<IslandOptions>(settings, "_options") is { } reset && LyricsSourceSettings.Read(reset).Select(s => s.Id).SequenceEqual(new[] { "netease", "lrclib", "kugou" }), "重置恢复三个默认来源及顺序");
            foreach (string name in new[] { "歌词源导入规范.md", "歌词源示例.json", "本地歌词示例.lrc" })
            {
                using var resource = typeof(alpha_island.App).Assembly.GetManifestResourceStream("AlphaIsland.Lyrics." + name);
                Check(resource is { CanRead: true }, "安装资源包含 " + name);
            }
            settings.SettingsContentScroll.ScrollToVerticalOffset(settings.SettingsContentScroll.VerticalOffset + settings.LyricsSourceCard.TranslatePoint(new Point(), settings.SettingsContentScroll).Y - 12);
            PumpAnimations(50); host.UpdateLayout();
            SaveThemePreview(host, "bin/SettingsLyrics175/previews/lyrics.png");

            PrivateCall(settings, "SelectPageByTag", "notifications");
            var imageHint = AgentField<TextBlock>(settings, "_greetingImageHint");
            var soundHint = AgentField<TextBlock>(settings, "_greetingSoundHint");
            Check(imageHint.Visibility == Visibility.Collapsed && soundHint.Visibility == Visibility.Collapsed, "欢迎设置不再显示划去的内置图片与日配说明");
            foreach (double width in new[] { 780d, 880d, 1100d })
            {
                host.Width = width; host.UpdateLayout();
                var titles = new[] { "启动欢迎组合", "欢迎图片", "欢迎音效", "启动音效文件", "通知音效文件" }
                    .Select(text => Descendants<TextBlock>(host).First(t => t.Text == text)).ToArray();
                double x = titles[0].TranslatePoint(new Point(), host).X;
                Check(titles.All(t => Math.Abs(t.TranslatePoint(new Point(), host).X - x) < .6), $"{width:0}px 窗口的欢迎与通知音效文字统一对齐");
                var cardGrid = (Grid)((StackPanel)titles[^1].Parent).Parent;
                Check(cardGrid.Children.OfType<Border>().Single().Child is AlphaIsland.PluginSdk.GlyphIcon { Text: AlphaIsland.PluginSdk.GlyphArtwork.NotificationSoundGlyph }, "通知音效文件左侧显示铃铛图标");
                foreach (var text in titles.Where(t => t.Text != "欢迎音效"))
                {
                    var grid = (Grid)((StackPanel)text.Parent).Parent;
                    var labels = (FrameworkElement)text.Parent;
                    var actions = grid.Children.OfType<FrameworkElement>().Single(c => Grid.GetColumn(c) == 2);
                    var labelRight = labels.TranslatePoint(new Point(labels.ActualWidth, 0), grid).X;
                    var actionLeft = actions.TranslatePoint(new Point(), grid).X;
                    Check(labelRight + 10 <= actionLeft && actionLeft + actions.ActualWidth <= grid.ActualWidth + .6,
                        $"{width:0}px 下{text.Text}说明与按钮有间距且不溢出");
                }
            }
            host.Width = 880; host.UpdateLayout();
            var welcome = Descendants<TextBlock>(host).First(t => t.Text == "启动欢迎组合");
            settings.SettingsContentScroll.ScrollToVerticalOffset(settings.SettingsContentScroll.VerticalOffset + welcome.TranslatePoint(new Point(), settings.SettingsContentScroll).Y - 30);
            PumpAnimations(50); host.UpdateLayout();
            SaveThemePreview(host, "bin/SettingsLyrics175/previews/welcome.png");
        }
        finally { settings.ForceClose = true; settings.Close(); }
    }

    private static async Task LyricsSourcesBehaviorChecks(string root)
    {
        var options = new IslandOptions();
        Check(LyricsSourceSettings.Read(options).Select(s => s.Id).SequenceEqual(new[] { "netease", "lrclib", "kugou" }), "旧设置自动使用三个默认来源");
        var first = new LyricsSourceDefinition { Id = "http.first", Name = "来源甲", UrlTemplate = "https://lyrics.example/first?title={title}&artist={artist}&album={album}&duration={duration}" };
        var second = first with { Id = "http.second", Name = "来源乙", UrlTemplate = "https://lyrics.example/second?title={title}&artist={artist}" };
        var imported = LyricsSourceSettings.Import(LyricsSourceSettings.Export(new[] { first, second }));
        LyricsSourceSettings.Write(options, imported);
        var copy = new IslandOptions(); copy.CopyFrom(options);
        string path = Path.Combine(root, "lyrics-options.json"); options.Save(path);
        Check(LyricsSourceSettings.Read(copy).Select(s => s.Name).SequenceEqual(new[] { "来源甲", "来源乙" })
            && LyricsSourceSettings.Read(IslandOptions.Load(path)).Select(s => s.Id).SequenceEqual(imported.Select(s => s.Id)), "自定义来源可复制、保存并重新载入");

        string title = "歌 曲 & /?#", artist = "测试歌手", album = "专辑甲";
        string Json(string lyric = "测试歌词") => JsonSerializer.Serialize(new { trackName = title, artistName = artist, albumName = album, duration = 180, syncedLyrics = "[00:01.00]" + lyric, translatedLyrics = "[00:01.00]译文" });
        var fixture = new LyricsHttpFixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json(), Encoding.UTF8, "application/json") }));
        using var service = new LyricsService(fixture, new LocalLyricsStore(Path.Combine(root, "local")));
        service.Configure(options);
        var doc = await service.GetLyricsAsync(title, artist, TimeSpan.FromSeconds(180), CancellationToken.None, album);
        Check(doc?.Source == "来源甲" && doc.HasTranslation, "标准 JSON 来源按首项优先选择并解析翻译");
        Check(fixture.Requests.Any(u => Uri.UnescapeDataString(u.Query).Contains(title) && u.Query.Contains("duration=180")), "接口中的歌曲占位符正确编码，时长使用秒");
        int calls = fixture.Requests.Count;
        await service.GetLyricsAsync(title, artist, TimeSpan.FromSeconds(180), CancellationToken.None, album);
        Check(fixture.Requests.Count == calls, "已匹配歌曲使用缓存，不重复请求来源");
        LyricsSourceSettings.Write(options, new[] { second, first }); service.Configure(options);
        doc = await service.GetLyricsAsync(title, artist, TimeSpan.FromSeconds(180), CancellationToken.None, album);
        Check(doc?.Source == "来源乙", "调整来源顺序后丢弃旧缓存，并使用新的优先来源");
        LyricsSourceSettings.Write(options, new[] { second with { Enabled = false }, first with { Enabled = false } }); service.Configure(options); calls = fixture.Requests.Count;
        Check(await service.GetLyricsAsync(title, artist, TimeSpan.FromSeconds(180), CancellationToken.None, album) == null && fixture.Requests.Count == calls, "全部来源关闭后不产生网络请求");

        var fallback = new LyricsHttpFixture((request, _) => Task.FromResult(new HttpResponseMessage(request.RequestUri!.AbsolutePath == "/first" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK) { Content = new StringContent(Json()) }));
        using (var failing = new LyricsService(fallback))
        {
            LyricsSourceSettings.Write(options, new[] { first, second }); failing.Configure(options);
            Check((await failing.GetLyricsAsync(title, artist, TimeSpan.FromSeconds(180), CancellationToken.None, album))?.Source == "来源乙", "首个接口失败时由其他已启用来源补充");
        }
        var mismatch = new LyricsHttpFixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { trackName = title, artistName = "完全不同的歌手", duration = 180, syncedLyrics = "[00:01]错误版本" })) }));
        using (var wrong = new LyricsService(mismatch))
        {
            LyricsSourceSettings.Write(options, new[] { first }); wrong.Configure(options);
            Check(await wrong.GetLyricsAsync(title, artist, TimeSpan.FromSeconds(180), CancellationToken.None, album) == null, "外部来源返回同名但不同歌手时拒绝错配");
        }

        using (var raw = new LyricsService(new LyricsHttpFixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("[ti:" + title + "]\n[ar:" + artist + "]\n[length:03:00]\n[00:01.00]外部 LRC") }))))
        {
            LyricsSourceSettings.Write(options, new[] { first with { Format = "lrc" } }); raw.Configure(options);
            Check((await raw.GetLyricsAsync(title, artist, TimeSpan.FromSeconds(180), CancellationToken.None, album))?.Lines[0].Text == "外部 LRC", "外部接口支持带元数据的 LRC 文本响应");
        }
        using (var nested = new LyricsService(new LyricsHttpFixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { data = new { items = new[] { new { meta = new { title, artist }, lrc = "[00:01.00]嵌套字段" } } } })) }))))
        {
            LyricsSourceSettings.Write(options, new[] { first with { ResultsPath = "data.items", TitlePath = "meta.title", ArtistPath = "meta.artist", LyricsPath = "lrc" } }); nested.Configure(options);
            Check((await nested.GetLyricsAsync(title, artist, TimeSpan.FromSeconds(180), CancellationToken.None))?.Lines[0].Text == "嵌套字段", "外部来源支持嵌套字段与结果数组映射");
        }
        using (var slow = new LyricsService(new LyricsHttpFixture(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/first") await Task.Delay(10000, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json()) };
        })))
        {
            LyricsSourceSettings.Write(options, new[] { first, second }); slow.Configure(options);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            doc = await slow.GetLyricsAsync(title, artist, TimeSpan.FromSeconds(180), CancellationToken.None, album);
            Check(doc?.Source == "来源乙" && watch.Elapsed.TotalSeconds < 3, "首项响应慢时在优先等待窗口后采用其他有效来源");
        }
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var changed = new LyricsService(new LyricsHttpFixture(async (_, token) =>
        {
            await release.Task.WaitAsync(token); return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json()) };
        })))
        {
            LyricsSourceSettings.Write(options, new[] { first }); changed.Configure(options);
            var oldRequest = changed.GetLyricsAsync(title, artist, TimeSpan.FromSeconds(180), CancellationToken.None, album);
            LyricsSourceSettings.Write(options, new[] { second }); changed.Configure(options); release.SetResult();
            Check(await oldRequest == null && !changed.TryGetCached(title, artist, TimeSpan.FromSeconds(180), out _, album), "请求途中改变来源时，旧请求不会回填新配置的缓存");
        }

        var local = new LocalLyricsStore(Path.Combine(root, "local"));
        string input = Path.Combine(root, "歌手 - 歌名.lrc");
        File.WriteAllText(input, "[ti:" + title + "]\n[ar:" + artist + "]\n[al:" + album + "]\n[length:03:00]\n[00:01.00]本地第一句\n[00:04.00]本地第二句", new UTF8Encoding(false));
        Check(local.Import(new[] { input }) == 1 && local.Import(new[] { input }) == 0, "本地 LRC 导入并按内容去重");
        File.Delete(input); local.Reload();
        Check(local.Find(title, artist, album, TimeSpan.FromSeconds(180))?.Lines.Count == 2, "原始 LRC 移走后仍可从本机副本读取");
        Check(local.Find(title, "其他歌手", album, TimeSpan.FromSeconds(180)) == null && local.Find(title, artist, album, TimeSpan.FromSeconds(240)) == null, "本地歌词通过歌手与版本时长过滤错误结果");
        LyricsSourceSettings.Write(options, new[] { new LyricsSourceDefinition { Id = "local", Name = "本地歌词", Kind = "local" }, first }); service.Configure(options); calls = fixture.Requests.Count;
        doc = await service.GetLyricsAsync(title, artist, TimeSpan.FromSeconds(180), CancellationToken.None, album);
        Check(doc?.Source == "本地歌词" && fixture.Requests.Count == calls, "本地来源排首项且命中时完全跳过网络");
        var bad = Path.Combine(root, "invalid.lrc"); File.WriteAllText(bad, "没有时间轴");
        bool rejected = false; try { local.Import(new[] { bad }); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && local.Entries.Count == 1, "无时间戳的本地文件被拒绝且保留原有歌词库");
        string huge = Path.Combine(root, "large.lrc"); File.WriteAllBytes(huge, new byte[LocalLyricsStore.MaxFileBytes + 1]);
        rejected = false; try { local.Import(new[] { huge }); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "本地导入限制单文件大小");
        File.WriteAllText(input, "[ti:" + title + "]\n[ar:" + artist + "]\n[al:" + album + "]\n[length:03:00]\n[00:01.00]更新后的本地歌词");
        local.Import(new[] { input });
        SettingsRegistry.SetExtra(options, "LyricsLibraryRevision", "updated"); service.Configure(options);
        Check((await service.GetLyricsAsync(title, artist, TimeSpan.FromSeconds(180), CancellationToken.None, album))?.Lines[0].Text == "更新后的本地歌词", "导入同曲新歌词后刷新索引与缓存，并优先显示新文件");
        foreach (string template in new[] { "file:///C:/example.lrc", "http://remote.example/lyrics", "https://{title}.example/lyrics", "https://lyrics.example/?q={unknown}" })
        {
            rejected = false; try { LyricsSourceSettings.Import(LyricsSourceSettings.Export(new[] { first with { UrlTemplate = template } })); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "拒绝不符合导入规范的接口地址 " + template);
        }
        LyricsSourceSettings.Write(options, new[] { first });
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); service.Configure(options);
        rejected = false; try { await service.GetLyricsAsync(title, artist, TimeSpan.FromSeconds(180), cancel.Token, album); } catch (OperationCanceledException) { rejected = true; }
        Check(rejected && !service.TryGetCached(title, artist, TimeSpan.FromSeconds(180), out _, album), "取消的歌词请求不写入结果缓存");
    }

    private sealed class LyricsHttpFixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests.Add(request.RequestUri!); return reply(request, token); }
    }
}
