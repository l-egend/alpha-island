using System.Buffers.Binary;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AlphaIsland.PluginSdk;
using AlphaIsland.Plugins.TrafficMonitor;
using alpha_island.Core;
using alpha_island.Plugins;
using alpha_island.Services;

internal static partial class Program
{
    private static void MediaAndTrafficChecks(string root)
    {
        var sessions = new[] {
            new MediaSessionInfo("music", "cloudmusic.exe", "歌曲", PlaybackState.Paused, 10, true),
            new MediaSessionInfo("chrome", "chrome.exe", "网页视频", PlaybackState.Playing, 11),
            new MediaSessionInfo("edge", "msedge.exe", "另一个视频", PlaybackState.Playing, 12),
            new MediaSessionInfo("closed", "unused.exe", "", PlaybackState.Closed, 20) };
        Check(MediaSourceNames.Select(sessions, null, "music") == "edge", "自动选择最近开始播放的 Edge，不被暂停的音乐会话占用");
        Check(MediaSourceNames.Select(sessions, "chrome", "edge") == "chrome", "手动媒体来源选择优先于其他正在播放的会话");
        Check(MediaSourceNames.Select(sessions, "missing", "music") == "edge", "已关闭的手动来源自动回退到其他正在播放的媒体");
        Check(MediaSourceNames.Select(sessions.Select(s => s with { State = s.Id == "closed" ? PlaybackState.Closed : PlaybackState.Paused }).ToArray(), null, "chrome") == "chrome",
            "全部暂停时保持当前媒体来源");
        Check(MediaSourceNames.Select(new[] { sessions[3] }, null, null) == null, "只剩空闲或关闭的媒体会话时不产生默认播放器");
        Check(MediaSourceNames.DisplayName("Chrome.exe") == "Google Chrome" && MediaSourceNames.DisplayName("msedge.exe") == "Microsoft Edge", "浏览器媒体显示可读的来源名称");
        Check(MediaSourceNames.IsBrowser("Microsoft.MicrosoftEdge_8wekyb3d8bbwe!App") && MediaSourceNames.DisplayName("Microsoft.MicrosoftEdge_8wekyb3d8bbwe!App") == "Microsoft Edge",
            "Edge 的打包应用媒体标识同样可识别");
        var video = new NowPlayingInfo { SourceApp = "chrome.exe", ContentKind = MediaContentKind.Video, Title = "网页字幕", State = PlaybackState.Playing };
        Check(!video.SupportsLyrics && video.DisplayArtist == "Google Chrome", "视频不发起歌曲歌词搜索，缺少作者时显示应用来源");

        string history = Path.Combine(root, "history_traffic.dat");
        File.WriteAllText(history, $"lines: \"4\"\n{DateTime.Now:yyyy/MM/dd} 2/3\n2020/01/01 4/5\n2020/01/02 9223372036854775807/1\n2020/01/03 -2/1\n");
        var totals = new TrafficMonitorService(history).Sample();
        Check(totals.TodayUp == 2048 && totals.TodayDown == 3072 && totals.TotalUp == 6144 && totals.TotalDown == 8192 && totals.Days == 2,
            "TrafficMonitor 历史 KB 转字节且拒绝溢出和负数记录");

        var now = DateTimeOffset.UtcNow;
        Check(NetworkSpeedService.FormatSpeedFull(1024) == "1.0 KB/s" && alpha_island.Controls.NetworkSpeedPill.FormatRate(1024) == "1.0 KB/s"
            && NetworkSpeedService.FormatSpeedFull(1_048_576) == "1.0 MB/s", "所有网络视图按 1024 换算，和 TrafficMonitor 字节单位保持一致");
        var state = new ExternalNetworkState();
        var sample = new PluginNetworkSample(now, 333, 222, 1000, 500, "producer-day", 1000, 500);
        Check(state.Apply("tm", "TrafficMonitor", sample) && state.SessionDown == 0, "外部计数初次接入建立基线，历史流量不算入本次监测");
        state.Apply("tm", "TrafficMonitor", sample with { Timestamp = now.AddSeconds(1), DownloadCounterBytes = 1500, UploadCounterBytes = 900, DownBytesPerSecond = 500 });
        Check(state.SessionDown == 500 && state.SessionUp == 400 && state.PeakDown == 500, "本次流量使用原始计数差值而不是速度积分");
        state.Apply("tm", "TrafficMonitor", sample);
        state.Apply("tm", "TrafficMonitor", sample with { Timestamp = now.AddSeconds(2), DownloadCounterBytes = 1600, UploadCounterBytes = 1000 });
        Check(state.SessionDown == 600 && state.SessionUp == 500, "倒序或重复的插件快照不覆盖计数基线");
        state.Apply("tm", "TrafficMonitor", sample with { Timestamp = now.AddSeconds(3), DownloadCounterBytes = 10, UploadCounterBytes = 10, CounterEpoch = "new-day" });
        state.Apply("tm", "TrafficMonitor", sample with { Timestamp = now.AddSeconds(4), DownloadCounterBytes = 30, UploadCounterBytes = 40, CounterEpoch = "new-day" });
        Check(state.SessionDown == 620 && state.SessionUp == 530, "跨天或生产者重启后重建基线且不产生负流量");
        state.Apply("other", "另一个来源", sample);
        Check(state.SessionDown == 0 && state.PeakDown == 333 && state.Title == "另一个来源", "换数据源时隔离统计基线与峰值");
        NativeTrafficBridgeChecks(root);
        CrossProcessTrafficChecks();
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr NativeFactory();
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate int NativeApi(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate IntPtr NativeText(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate double NativeValue(IntPtr self, int item);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate void NativeInitialize(IntPtr self, IntPtr app);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate void NativeRefresh(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate void NativeMonitor(IntPtr self, IntPtr info);
    private static T Virtual<T>(IntPtr instance, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    private static void NativeTrafficProducer()
    {
        IntPtr library = NativeLibrary.Load(Path.GetFullPath("bin/TrafficMonitorBridge/x64/AlphaIsland.TrafficMonitorBridge.dll"));
        IntPtr app = Marshal.AllocHGlobal(IntPtr.Size), table = Marshal.AllocHGlobal(IntPtr.Size * 3), version = Marshal.StringToHGlobalUni("V1.86 IPC test");
        NativeApi api = _ => 0; NativeText text = _ => version;
        double down = 765432, todayDown = 87654321;
        NativeValue value = (_, item) => item switch { 0 => 345678, 1 => down, 11 => 12345678, 12 => todayDown, _ => 0 };
        Marshal.WriteIntPtr(table, 0, Marshal.GetFunctionPointerForDelegate(api)); Marshal.WriteIntPtr(table, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(text));
        Marshal.WriteIntPtr(table, IntPtr.Size * 2, Marshal.GetFunctionPointerForDelegate(value)); Marshal.WriteIntPtr(app, table);
        try
        {
            var plugin = Marshal.GetDelegateForFunctionPointer<NativeFactory>(NativeLibrary.GetExport(library, "TMPluginGetInstance"))();
            Virtual<NativeInitialize>(plugin, 14)(plugin, app); Virtual<NativeRefresh>(plugin, 2)(plugin);
            System.Console.WriteLine("READY");
            while (System.Console.ReadLine() != null)
            {
                down = 765433; todayDown += 400;
                Virtual<NativeRefresh>(plugin, 2)(plugin); System.Console.WriteLine("UPDATED");
            }
        }
        finally
        {
            NativeLibrary.Free(library); Marshal.FreeHGlobal(app); Marshal.FreeHGlobal(table); Marshal.FreeHGlobal(version);
            GC.KeepAlive(api); GC.KeepAlive(text); GC.KeepAlive(value);
        }
    }
    private static void CrossProcessTrafficChecks()
    {
        string name = "Local\\AlphaIsland.TrafficMonitor.Test." + Guid.NewGuid().ToString("N");
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        {
            WorkingDirectory = Environment.CurrentDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--traffic-producer"); start.Environment["ALPHA_ISLAND_TM_TEST_MAP"] = name;
        using var producer = System.Diagnostics.Process.Start(start)!;
        string? NextLine()
        {
            var line = producer.StandardOutput.ReadLineAsync();
            if (!line.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("跨进程桥接测试未响应");
            return line.Result;
        }
        try
        {
            Check(NextLine() == "READY", "独立进程通过原生 DLL 发布 TrafficMonitor 格式快照");
            using var reader = new BridgeReader(name);
            var sample = reader.Read();
            Check(sample is { DownBytesPerSecond: 765432, UpBytesPerSecond: 345678, TodayDownBytes: 87654321 }
                && sample.CounterEpoch.StartsWith(producer.Id + ":"), "另一个进程中的接收端读取实际共享内存速率和计数");
            var state = new ExternalNetworkState(); state.Apply("ipc", "跨进程", sample!);
            producer.StandardInput.WriteLine("update"); producer.StandardInput.Flush();
            Check(NextLine() == "UPDATED", "独立生产者再次更新共享快照");
            state.Apply("ipc", "跨进程", reader.Read()!);
            Check(state.SessionDown == 400 && state.Current.DownBytesPerSecond == 765433, "跨进程更新进入统一网络状态且按原始累计计算增量");
        }
        finally
        {
            producer.StandardInput.Close();
            if (!producer.WaitForExit(5000)) { producer.Kill(entireProcessTree: true); producer.WaitForExit(5000); }
        }
    }

    private static void NativeTrafficBridgeChecks(string root)
    {
        Check(Environment.Is64BitProcess, "原生 TrafficMonitor 桥接验证使用 x64 进程");
        string dll = Path.GetFullPath("bin/TrafficMonitorBridge/x64/AlphaIsland.TrafficMonitorBridge.dll");
        Check(File.Exists(dll), "已构建独立 TrafficMonitor 原生桥接 DLL");
        string name = "Local\\AlphaIsland.TrafficMonitor.Test." + Guid.NewGuid().ToString("N");
        string? before = Environment.GetEnvironmentVariable("ALPHA_ISLAND_TM_TEST_MAP");
        Environment.SetEnvironmentVariable("ALPHA_ISLAND_TM_TEST_MAP", name);
        IntPtr library = NativeLibrary.Load(dll), app = Marshal.AllocHGlobal(IntPtr.Size), table = Marshal.AllocHGlobal(IntPtr.Size * 3), info = Marshal.AllocHGlobal(64);
        IntPtr version = Marshal.StringToHGlobalUni("V1.86 test");
        double down = 123456, up = 65432, dayDown = 3210987, dayUp = 987654;
        NativeApi api = _ => 0; // The real V1.86 ITrafficMonitor reports zero.
        NativeText text = _ => version;
        NativeValue value = (_, item) => item switch { 0 => up, 1 => down, 11 => dayUp, 12 => dayDown, _ => 0 };
        Marshal.WriteIntPtr(table, 0, Marshal.GetFunctionPointerForDelegate(api)); Marshal.WriteIntPtr(table, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(text));
        Marshal.WriteIntPtr(table, IntPtr.Size * 2, Marshal.GetFunctionPointerForDelegate(value)); Marshal.WriteIntPtr(app, table);
        try
        {
            IntPtr plugin = Marshal.GetDelegateForFunctionPointer<NativeFactory>(NativeLibrary.GetExport(library, "TMPluginGetInstance"))();
            Check(Virtual<NativeApi>(plugin, 0)(plugin) == 7, "原生导出函数和 TrafficMonitor API 7 虚表兼容");
            using var reader = new BridgeReader(name);
            Check(reader.Read() == null, "未发布桥接时返回等待状态而不是伪造零速度");
            Marshal.Copy(new byte[64], 0, info, 64); Marshal.WriteInt64(info, 0, 4096); Marshal.WriteInt64(info, 8, 8192);
            Virtual<NativeMonitor>(plugin, 5)(plugin, info);
            var legacy = reader.Read();
            Check(legacy is { DownBytesPerSecond: 8192, UpBytesPerSecond: 4096, TodayDownBytes: null }, "兼容旧 MonitorInfo 速率回调，不伪造今日累计");
            Virtual<NativeInitialize>(plugin, 14)(plugin, app);
            Virtual<NativeRefresh>(plugin, 2)(plugin);
            var snapshot = reader.Read();
            Check(snapshot is { DownBytesPerSecond: 123456, UpBytesPerSecond: 65432, TodayDownBytes: 3210987, TodayUpBytes: 987654 },
                "实际原生 DLL 经共享内存读取原始字节数据，支持主接口版本零且不二次乘 1024");
            using var mapping = MemoryMappedFile.OpenExisting(name); using var view = mapping.CreateViewAccessor(0, 80);
            var bytes = new byte[80]; view.ReadArray(0, bytes, 0, 80);
            var now = DateTimeOffset.UtcNow;
            Check(BridgeReader.Decode(bytes, now.AddSeconds(6)) == null && BridgeReader.Decode(bytes, now.AddSeconds(-3)) == null,
                "桥接拒绝过期快照与异常未来时间");
            bytes[4] = 2; Check(BridgeReader.Decode(bytes, now) == null, "桥接拒绝未知协议版本"); bytes[4] = 1;
            bytes[8] |= 1; Check(BridgeReader.Decode(bytes, now) == null, "桥接拒绝写入中的不完整快照"); bytes[8] &= 254;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), ulong.MaxValue);
            Check(BridgeReader.Decode(bytes, now) == null, "桥接拒绝超出 SDK 字节范围的计数");
            view.Write(16, DateTime.UtcNow.AddSeconds(-6).ToFileTimeUtc());
            Check(reader.Read() == null, "生产者停止更新后接收端释放过期映射并回退");
            int priorSequence = view.ReadInt32(8);
            down = 234567; Virtual<NativeRefresh>(plugin, 2)(plugin);
            Check(view.ReadInt32(8) != priorSequence && (view.ReadInt32(8) & 1) == 0, "每次原生发布推进偶数序号，读写交错能被正确检测");
            Check(reader.Read()?.DownBytesPerSecond == 234567, "生产者恢复发布后接收端可重新连接");
            view.Write(8, view.ReadInt32(8) | 1);
            using (var torn = new BridgeReader(name)) Check(torn.Read() == null, "真正的共享内存奇数序号不会进入网络数据源");
            Virtual<NativeRefresh>(plugin, 2)(plugin);
            TrafficPluginHostChecks(root, name);
        }
        finally
        {
            NativeLibrary.Free(library); Marshal.FreeHGlobal(app); Marshal.FreeHGlobal(table); Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(version);
            GC.KeepAlive(api); GC.KeepAlive(text); GC.KeepAlive(value);
            Environment.SetEnvironmentVariable("ALPHA_ISLAND_TM_TEST_MAP", before);
        }
    }

    private static void TrafficPluginHostChecks(string root, string mapName)
    {
        Exception? failure = null;
        PluginHost? cleanup = null;
        var thread = new Thread(() =>
        {
            try
            {
                string pluginRoot = Path.Combine(root, "traffic-plugin"), pluginDir = Path.Combine(pluginRoot, "tm"); Directory.CreateDirectory(pluginDir);
                string source = Path.GetFullPath($"examples/TrafficMonitorPlugin/bin/{TestBuildConfiguration}/net8.0-windows");
                foreach (string file in Directory.GetFiles(source).Where(f => f.EndsWith(".dll") || f.EndsWith(".deps.json") || f.EndsWith("plugin.json")))
                    File.Copy(file, Path.Combine(pluginDir, Path.GetFileName(file)), true);
                var options = new IslandOptions(); var pages = new List<IIslandPage>();
                using var host = new PluginHost(System.Windows.Threading.Dispatcher.CurrentDispatcher, options, new[] { pluginRoot }, pages.Add, p => pages.Remove(p), _ => { }); cleanup = host;
                host.Discover();
                options.Extras["plugin/alpha.trafficmonitor/mapping-name"] = JsonSerializer.SerializeToElement(mapName);
                options.Extras[PluginHost.EnabledKey("alpha.trafficmonitor")] = JsonSerializer.SerializeToElement(true); host.Sync(options);
                var entry = host.Entries.Single(); PumpUntilState(() => entry.Running || entry.Error.Length>0);
                Check(entry.Running && pages.Count == 0 && entry.Context!.NetworkSources.Count == 1, "实际接收插件 DLL 仅注册网络数据源，不增加卡片或组件菜单页");
                PumpUntilState(() => host.ReadNetwork(DateTimeOffset.UtcNow)?.Sample.DownBytesPerSecond == 234567);
                var reading = host.ReadNetwork(DateTimeOffset.UtcNow);
                Check(reading?.Sample.DownBytesPerSecond == 234567 && reading?.Title == "TrafficMonitor", "没有独立卡片时仍取得真实桥接数据并提供来源名称");
                options.Extras[PluginHost.SettingKey("alpha.trafficmonitor", "network")] = JsonSerializer.SerializeToElement(false); host.Sync(options);
                PumpUntilState(() => host.ReadNetwork(DateTimeOffset.UtcNow) == null);
                Check(host.ReadNetwork(DateTimeOffset.UtcNow) == null && entry.Running, "关闭数据接管后撤销网络来源，插件仍可重新启用接管");
                options.Extras[PluginHost.EnabledKey("alpha.trafficmonitor")] = JsonSerializer.SerializeToElement(false); host.Sync(options);
                Check(!entry.Running && pages.Count == 0 && host.ReadNetwork(DateTimeOffset.UtcNow) == null, "禁用接收插件撤销数据源、卡片和读取资源");
                string fixtureDir = Path.Combine(pluginRoot, "fixture"); Directory.CreateDirectory(fixtureDir);
                foreach (string file in Directory.GetFiles(Path.GetFullPath($"tools/PluginFixture/bin/{TestBuildConfiguration}/net8.0-windows")).Where(f => f.EndsWith(".dll") || f.EndsWith(".deps.json")))
                    File.Copy(file, Path.Combine(fixtureDir, Path.GetFileName(file)), true);
                File.WriteAllText(Path.Combine(fixtureDir, "plugin.json"), JsonSerializer.Serialize(new { id = "fixture.network", name = "网络检查", version = "1.0.0", apiVersion = 1,
                    assembly = "AlphaIsland.PluginFixture.dll", entryType = "AlphaIsland.PluginFixture.ProbePlugin" }));
                host.Discover(); options.Extras[PluginHost.EnabledKey("fixture.network")] = JsonSerializer.SerializeToElement(true); host.Sync(options);
                var fixture = host.Entries.Single(e => e.Manifest?.Id == "fixture.network"); PumpUntilState(() => fixture.Running || fixture.Error.Length>0);
                Check(host.ReadNetwork(DateTimeOffset.UtcNow)?.Sample.DownBytesPerSecond == 77, "宿主优先读取声明优先级最高的网络来源");
                var proxy = (alpha_island.Plugins.Isolation.IsolatedPlugin)fixture.Instance!;
                string[] cases = { "空数据", "负速率", "NaN", "过期数据", "未来时间", "负累计计数" };
                for (int i = 1; i <= 6; i++)
                {
                    options.Extras["plugin/fixture.network/mode"] = JsonSerializer.SerializeToElement(i); host.Sync(options);
                    long version = proxy.ConfigurationVersion; PumpUntilState(() => proxy.AppliedConfigurationVersion >= version);
                    Check(host.ReadNetwork(DateTimeOffset.UtcNow)?.Sample.DownBytesPerSecond == 11, "网络数据源拒绝" + cases[i - 1] + "并回退备用来源");
                }
                options.Extras["plugin/fixture.network/mode"] = JsonSerializer.SerializeToElement(7); host.Sync(options); PumpUntilState(() => !fixture.Running);
                Check(host.ReadNetwork(DateTimeOffset.UtcNow) == null && !fixture.Running && pages.Count == 0,
                    "网络来源抛出异常时停用故障插件，不再调用它的备用来源");
                host.Dispose(); AwaitPlugin(host.ShutdownCompletion.ContinueWith(_=>true));
            }
            catch (Exception ex) { failure = ex; }
            finally { if (cleanup != null) { cleanup.Dispose(); AwaitPlugin(cleanup.ShutdownCompletion.ContinueWith(_ => true)); } }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }

    private static void MediaAndCompactUiChecks(alpha_island.MainWindow window, Grid root)
    {
        var type = window.GetType(); object? Get(string name) => ReadWindowMember(window, name);
        var oldSmtc = (SmtcService?)Get("_smtc");
        using var smtc = new SmtcService(window.Dispatcher);
        WriteWindowMember(window, "_smtc", smtc);
        var pages = (IslandPageHost)Get("_pages")!; var layers = (IslandLayerHost)Get("_layers")!;
        var oldState = (IslandState)Get("_state")!; string previous = pages.ActiveId;
        try
        {
            var video = new NowPlayingInfo { Title = "浏览器视频", SourceApp = "msedge.exe", SessionId = "edge-test", ContentKind = MediaContentKind.Video,
                State = PlaybackState.Playing, CanToggle = true, Duration = TimeSpan.FromMinutes(5), CanSeek = true };
            PrivateCall(window, "ApplyMedia", video with { ContentKind = MediaContentKind.Music });
            PrivateCall(window, "ApplyLyrics", new LyricsDocument(new List<LyricLine> { new() { Time = TimeSpan.Zero, Text = "前一个音频的歌词" } }, true, true, "离线"));
            smtc.SetDebugNowPlaying(video); PrivateCall(window, "ApplyMedia", video);
            Check(window.TitleText.Text == "浏览器视频" && window.AlbumText.Text.Contains("Microsoft Edge") && pages.Pages.Single(p => p.Id == IslandPageIds.Music).Title == "媒体播放器",
                "真实媒体卡片显示 Edge 视频与来源，组件名称为媒体播放器");
            Check(!(bool)type.GetProperty("LyricsAreaVisible", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!, "视频卡片没有误用歌曲歌词");
            Check(Get("_lyricsDoc") == null, "同一来源同一标题改为视频时也会清除先前歌词");
            PrivateCall(window, "ApplyMedia", video with { CanToggle = false });
            Check(!window.PlayPauseButton.IsEnabled, "媒体不支持播放控制时按钮禁用");
            Check(window.CardMediaPlaceholder.Visibility == Visibility.Visible && window.CardMediaPlaceholder.Text == "\uE714", "浏览器视频没有封面时显示视频图标");
            PrivateCall(window, "ApplyMedia", video);
            smtc.AudioProbeEnabled = true;
            for (int i = 0; i < 6; i++) smtc.ReportAudioEvidence(0, 0, false, 30, true);
            Check(smtc.Current?.State == PlaybackState.Playing, "浏览器静音播放不会被环回无声判定为暂停");
            layers.Request(IslandNotification.Refreshing("netspeed", 30)); layers.SetPinned("netspeed", true);
            PrivateCall(window, "SetMusicPinned", true);
            PrivateCall(window, "SetState", IslandState.Card, false, IslandPageIds.Music); PumpAnimations(300); root.UpdateLayout();
            PrivateCall(window, "UpdateSideBubbleHint");
            Check((string?)Get("_sideBubbleComponentKey") == "netspeed" && window.SideBubbleCustomContent.Content is alpha_island.Controls.NetworkSpeedPill
                && window.SideBubbleGeneric.Visibility == Visibility.Collapsed, "媒体主岛旁显示网速 CPU RAM 缩略岛而不是固定网速文字");
            PrivateCall(window, "SelectComponent", IslandPageIds.System); PumpAnimations(300); root.UpdateLayout();
            Check((string?)Get("_sideBubbleComponentKey") == "music" && window.SideBubbleDetail.Text == "浏览器视频",
                "系统与网络主岛旁改为媒体缩略岛，排除当前已经展开的网络组件");
            Check(ReferenceEquals(window.SideBubble.Background, window.IslandBody.Background) && window.SideBubble.IsHitTestVisible,
                "旁侧缩略岛跟随主题透明度并可点击切换");
            var sideCenter = window.SideBubble.PointToScreen(new System.Windows.Point(window.SideBubble.ActualWidth / 2, window.SideBubble.ActualHeight / 2));
            Check((bool)PrivateCall(window, "IsPointInsideInteractiveZone", (int)sideCenter.X, (int)sideCenter.Y)!,
                "缩略岛进入原生窗口命中区域，点击不会穿透到桌面或先触发收回");
            var mouse = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
            PrivateCall(window, "SideBubble_MouseLeftButtonUp", window.SideBubble, mouse); PumpAnimations(300);
            Check(pages.ActiveId == IslandPageIds.Music && (IslandState)Get("_state")! == IslandState.Card, "点击媒体缩略岛切回播放器后保持展开");
            typeof(SmtcService).GetProperty(nameof(SmtcService.Sessions))!.SetValue(smtc, new[] {
                new MediaSessionInfo("chrome-menu", "chrome.exe", "网页媒体", PlaybackState.Playing, 1),
                new MediaSessionInfo("edge-menu", "msedge.exe", "网页视频", PlaybackState.Paused, 2) });
            PrivateCall(window, "MediaSourcesButton_Click", window.MediaSourcesButton, new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpAnimations(30); var sourceMenu = window.MediaSourcesButton.ContextMenu; sourceMenu.UpdateLayout();
            Check(sourceMenu.IsOpen && sourceMenu.Items.Count == 4 && PresentationSource.FromVisual(sourceMenu) != null,
                "媒体来源按钮打开真实 Popup 菜单并列出 Chrome 与 Edge 会话");
            var menuCenter = sourceMenu.PointToScreen(new System.Windows.Point(sourceMenu.ActualWidth / 2, sourceMenu.ActualHeight / 2));
            Check((bool)PrivateCall(window, "IsPointInsideInteractiveZone", (int)menuCenter.X, (int)menuCenter.Y)!
                && (bool)PrivateCall(window, "IsCursorInHotZone")!, "来源菜单属于交互区域且阅读期间不会自动收回播放器");
            var queuedClick = new alpha_island.Interop.MouseButtonDownEventArgs((int)menuCenter.X, (int)menuCenter.Y, 0x0201);
            ((MenuItem)sourceMenu.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); sourceMenu.IsOpen = false;
            PrivateCall(window, "OnGlobalButtonDown", null, queuedClick); PumpAnimations(25);
            Check(smtc.SelectedSessionId == "chrome-menu" && (IslandState)Get("_state")! == IslandState.Card && (bool)Get("_componentCardAwaitingPointer")!,
                "菜单选择来源后保持播放器展开，旧全局点击不会误触发收回");
            ((MenuItem)sourceMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(smtc.SelectedSessionId == null, "媒体来源菜单可以恢复自动选择模式");
            var oldNetwork = (NetworkSpeedService?)Get("_netSpeed");
            using var network = new NetworkSpeedService();
            var external = (ExternalNetworkState)Get("_externalNetwork")!;
            try
            {
                WriteWindowMember(window, "_netSpeed", network);
                external.Apply("test/tm", "TrafficMonitor", new PluginNetworkSample(DateTimeOffset.UtcNow, 2048, 1024, 8192, 4096, "test", 8192, 4096, "桥接网卡"));
                PrivateCall(window, "SyncNetSpeedCard");
                PrivateCall(window, "UpdateNetSpeedPrimary", external.Current);
                PrivateCall(window, "UpdateCompactIslandContent", "netspeed");
                Check(window.NetSpeedCardDownText.Text == "2.0 KB/s" && window.NetSpeedCardUpText.Text == "1.0 KB/s"
                    && window.NetSpeedRates.DownloadValue.Text == "2.0KB/s" && ((alpha_island.Controls.NetworkSpeedPill)window.SideBubbleCustomContent.Content).DownloadValue.Text == "2.0KB/s",
                    "主卡片、主小岛和旁侧缩略岛共同使用桥接速率与相同换算单位");
                Check(window.NetSpeedCardStatusText.Text == "来源：TrafficMonitor" && window.NetSpeedCardHintText.Text.Contains("今日 ↓8 KB ↑4 KB"),
                    "网络主卡片标明桥接来源并使用实时今日流量");
                external.Apply("test/tm", "TrafficMonitor", new PluginNetworkSample(DateTimeOffset.UtcNow.AddMilliseconds(1), 123, 45));
                PrivateCall(window, "SyncNetSpeedCard"); PrivateCall(window, "UpdateNetSpeedPrimary", external.Current);
                Check(window.NetSpeedCardDownText.Text == "123 B/s" && window.NetSpeedRates.SessionDescription.Contains("上传 —、下载 —"),
                    "低于 512 B/s 的速率照实显示，未提供累计时显示缺省而不是伪造零流量");
                PrivateCall(window, "UpdateCompactIslandContent", "netspeed"); root.UpdateLayout();
                SaveThemePreview(window.SideBubble, "docs/screens/media-side-network.png");
            }
            finally
            {
                external.Reset(); WriteWindowMember(window, "_netSpeed", oldNetwork);
                PrivateCall(window, "UpdateSideBubbleHint");
            }
            SaveThemePreview(window.IslandBody, "docs/screens/media-player-edge.png");
        }
        finally
        {
            PrivateCall(window, "SetMusicPinned", false); layers.SetPinned("netspeed", false); layers.HideAll();
            WriteWindowMember(window, "_smtc", oldSmtc);
            PrivateCall(window, "SetState", oldState, false, previous); PumpAnimations(300);
        }
    }
}
