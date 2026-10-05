using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AlphaIsland.PluginSdk;
using AlphaIsland.Plugins.OppoPods;
using OppoPodsManager;
using alpha_island.Core;
using alpha_island.Plugins;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;
using ComboBox = System.Windows.Controls.ComboBox;

// This dedicated branch checks Windows 10 2004 availability before running.
#pragma warning disable CA1416
internal static partial class Program
{
    private static async Task EarbudsPluginChecks(string root)
    {
        var spp = new SppFrameCodec();
        var wire = spp.Encode(OppoProtocol.CmdBattery, []);
        Check(Convert.ToHexString(wire) == "AA0700000601F00000", "SPP 电量请求与已核对的协议帧一致");
        var split = wire.Take(5).ToList();
        Check(!spp.TryDecode(split, out _), "SPP 收到半帧时保留缓冲");
        split.AddRange(wire.Skip(5)); split.AddRange(spp.Encode(OppoProtocol.CmdAncResp, [1, 1, 16, 0]));
        Check(spp.TryDecode(split, out var first) && first.Cmd == OppoProtocol.CmdBattery
            && spp.TryDecode(split, out var second) && second.Cmd == OppoProtocol.CmdAncResp && split.Count == 0,
            "SPP 半帧续收与连帧按命令分别解析");
        var malformed = wire.ToList(); malformed[7] = 50;
        Check(!spp.TryDecode(malformed, out _), "SPP 拒绝载荷长度超过实际帧体的数据");
        bool tooLarge = false; try { spp.Encode(1, new byte[249]); } catch (ArgumentOutOfRangeException) { tooLarge = true; }
        Check(tooLarge, "SPP 拒绝单字节长度溢出");
        var gatt = new GattFrameCodec(); var gattWire = gatt.Encode(0x0106, [1, 2]);
        Check(Convert.ToHexString(gattWire) == "06010002000102" && gatt.Encode(0x0106, [])[2] == 1,
            "BLE 使用独立帧头并递增事务号");
        Check(gatt.TryDecode(gattWire.ToList(), out var decoded) && decoded.Payload.SequenceEqual(new byte[] { 1, 2 }), "BLE 载荷正确解码");
        bool gattTooLarge = false; try { gatt.Encode(1, new byte[4097]); } catch (ArgumentOutOfRangeException) { gattTooLarge = true; }
        Check(gattTooLarge, "BLE 编码与收帧上限一致");
        var caps = DeviceCapabilities.ForceModel("OnePlus Buds 3");
        Check(DeviceCapabilities.GetModelNames().Count >= 130 && caps.AncOptions.Count > 0 && caps.EqPresets.Count > 0,
            "插件内嵌型号和 EQ 数据，无需原 EXE 或外部分析目录");
        Check(!DeviceCapabilities.Detect("vivo TWS unrelated").IsSupported, "未知品牌不借用 OPPO 私有控制能力");
        using (var native = new SppTransport(new FixedDeviceLocator(1, "测试")))
        {
            PrivateCall(native, "EnsureWsaStarted");
            native.Dispose();
            bool ownsReference = (bool)typeof(SppTransport).GetField("_wsaAcquired", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(native)!;
            Check(!ownsReference, "卸载回退传输时平衡释放 Winsock 引用，未建立硬件连接");
        }
        using (var transport = new EarbudProbeTransport())
        using (var manager = new PodManager(transport))
        {
            transport.Emit(new(OppoProtocol.CmdBatteryResp, [0, 4, 83, 0, 72, 1, 59, 0], 0));
            Check(manager.State.Battery["L"]?.Level == 83 && manager.State.Battery["R"]?.Charging == true
                && manager.State.Battery["C"]?.Level == 59, "实际协议解析器读取左右耳与盒电量及充电状态");
            transport.Emit(new(OppoProtocol.CmdBatteryResp, [1, 25, 2, 0x80 | 44, 3, 67], 0));
            Check(manager.State.Battery["L"]?.Level == 25 && manager.State.Battery["R"]?.Charging == true,
                "实际解析器兼容 BLE 电量列表格式");
        }
        using (var transport = new EarbudProbeTransport())
        using (var session = new ProtocolSession(new(1, "OnePlus Buds 3", true), new(), transport))
        {
            await session.ConnectAsync(CancellationToken.None);
            var initial = session.ReadSnapshot();
            Check(initial.Connected && initial.CanAnc && initial.LeftBattery == "83%", "实际会话通过模拟响应握手并产生状态快照");
            Check(initial.LeftPower.Percent == 83 && initial.RightPower.Percent == 72 && initial.RightPower.Charging
                && initial.CasePower.Percent == 59, "协议电量和充电状态作为独立数据传给 UI，避免解析显示文本");
            Check(!transport.Writes.Any(w => w.Cmd is 0x0403 or 0x0404 or 0x0406 or 0x0422 or 0x0429),
                "初次连接不改写耳机用户设置");
            var target = initial.AncChoices.First(c => c.Key != initial.Anc).Key;
            session.Execute(new(EarbudCommandKind.Anc, target));
            var pending = session.ReadSnapshot();
            Check(pending.WaitingForReadback && pending.Anc == initial.Anc, "设置发送后保持旧值，等待真实回读");
            transport.Emit(new(OppoProtocol.CmdAncResp, [], 0));
            Check(session.ReadSnapshot().WaitingForReadback, "空响应不能解除设置回读等待");
            transport.Emit(new(OppoProtocol.CmdAncResp, [0, 4, 16, 0, 0], 0));
            Check(session.ReadSnapshot().WaitingForReadback, "智能降噪实时档位不能代替主模式设置回读");
            session.Pump(true);
            var readback = session.ReadSnapshot();
            if (readback.WaitingForReadback || readback.Anc != target)
                System.Console.WriteLine($"ANC probe: before={initial.Anc}, requested={target}, actual={readback.Anc}, pending={readback.WaitingForReadback}, writes={string.Join(';', transport.Writes.Where(w => w.Cmd is 0x0404 or 0x010C).Select(w => $"{w.Cmd:X4}:{Convert.ToHexString(w.Payload)}"))}");
            Check(!readback.WaitingForReadback && readback.Anc == target, "降噪查询响应完成设置回读同步");
            int writes = transport.Writes.Count;
            bool rejected = false; try { session.Execute(new(EarbudCommandKind.Anc, "invalid-mode")); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && transport.Writes.Count == writes, "无效设置不发送给设备");
            session.Execute(new(EarbudCommandKind.GameMode, "true"));
            transport.Emit(new(OppoProtocol.CmdBatchQueryResp, [OppoProtocol.FeatureDualDevice, 0], 0));
            Check(session.ReadSnapshot().WaitingForReadback, "其他功能的局部响应不能解除游戏设置回读等待");
            session.Pump(true);
            Check(!session.ReadSnapshot().WaitingForReadback && session.ReadSnapshot().GameMode, "完整功能响应回读游戏开关真实值");
        }
        await EarbudBatteryFreshnessChecks();
        await EarbudControllerChecks();
        Exception? failure = null;
        var thread = new Thread(() => { try { EarbudUiAndHostChecks(root); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }
    private static async Task EarbudUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        if (!condition()) throw new Exception("耳机插件异步检查超时");
    }
    private static async Task EarbudBatteryFreshnessChecks()
    {
        var now = DateTime.UtcNow;
        var transport = new EarbudProbeTransport();
        using var session = new ProtocolSession(new(1, "OnePlus Buds 3", true), new(), transport, () => now);
        await session.ConnectAsync(CancellationToken.None);
        Check(session.ReadSnapshot().CaseBattery == "59%", "握手读取实时盒电量");
        transport.Emit(new(OppoProtocol.CmdBatteryResp, [1, 90, 2, 70], 0));
        Check(session.ReadSnapshot().LeftBattery == "90%" && session.ReadSnapshot().CaseBattery == "—",
            "完整电量响应不再包含耳机盒时立即清除历史盒电量");
        transport.Emit(new(OppoProtocol.CmdActiveReport, [OppoProtocol.EvtBattery, 1, 3, 45], 0));
        now = now.AddSeconds(5);
        transport.Emit(new(OppoProtocol.CmdActiveReport, [OppoProtocol.EvtBattery, 1, 1, 85], 0));
        Check(session.ReadSnapshot().LeftBattery == "85%" && session.ReadSnapshot().CaseBattery == "45%",
            "单耳通知实时更新对应电量并保留仍有效的其他分路");
        now = now.AddSeconds(6);
        transport.Emit(new(OppoProtocol.CmdAncResp, [1, 1, 1, 0], 0));
        Check(session.ReadSnapshot().Connected && session.ReadSnapshot().LeftBattery == "85%" && session.ReadSnapshot().CaseBattery == "—",
            "持续收到耳机其他响应时盒电量仍按独立时限过期");
        transport.Emit(new(OppoProtocol.CmdBatteryResp, [0, 4, 80, 0, 75, 0, 255, 0], 0));
        Check(session.ReadSnapshot().CaseBattery == "—" && session.ReadSnapshot().RightBattery == "75%", "离线哨兵值不会显示为 255% 电量");
        transport.Emit(new(OppoProtocol.CmdBatteryResp, [1, 80, 2, 75, 3, 42], 0));
        Check(session.ReadSnapshot().CaseBattery == "42%", "盒重新回报电量后恢复显示");
        transport.Emit(new(OppoProtocol.CmdBatteryResp, [1, 90, 2], 0));
        Check(session.ReadSnapshot().CaseBattery == "42%", "不完整电量帧不会误清除其他有效电量");
        now = now.AddSeconds(10);
        transport.Emit(new(OppoProtocol.CmdAncResp, [1, 1, 1, 0], 0));
        Check(session.ReadSnapshot().Connected && session.ReadSnapshot().LeftBattery == "—" && session.ReadSnapshot().RightBattery == "—",
            "没有电量更新的左右耳也清除过期数值");
        now = now.AddSeconds(16);
        Check(!session.ReadSnapshot().Connected && session.ReadSnapshot().CaseBattery == "—", "整个控制连接停止响应后转离线并清空电量");
        transport.Close();
        Check(!session.ReadSnapshot().Connected && session.ReadSnapshot().LeftBattery == "—", "传输层断开立即清空所有电量");
    }
    private static async Task EarbudControllerChecks()
    {
        var device = new EarbudDevice(1, "测试耳机", true);
        var sessions = new List<EarbudFakeSession>(); int discoveries = 0;
        using (var controller = new EarbudController(_ => { }, () => { Interlocked.Increment(ref discoveries); return new[] { device }; },
            (d, o) => { var session = new EarbudFakeSession(); lock (sessions) sessions.Add(session); return session; }))
        {
            controller.Start(CancellationToken.None, new()); await Task.Delay(180);
            Check(discoveries == 0 && sessions.Count == 0, "默认启用卡片不扫描或自动打开耳机连接");
            controller.Refresh(); await EarbudUntil(() => controller.Devices.Count == 1);
            Check(!controller.Snapshot.Connected, "刷新只枚举，默认不擅自连接");
            controller.Connect(device); await EarbudUntil(() => controller.Snapshot.Connected);
            var first = sessions[0];
            controller.Execute(new(EarbudCommandKind.Anc, "Deep")); await EarbudUntil(() => first.Commands.Count == 1);
            Check(controller.Snapshot.Anc == "Deep", "控制器串行执行命令并发布回读状态");
            controller.Connect(new(2, "另一副耳机", true)); await EarbudUntil(() => sessions.Count == 2 && controller.Snapshot.Connected);
            Check(first.Disposed && sessions[1].ConnectCount == 1, "切换设备前释放旧会话");
            controller.Disconnect(); await EarbudUntil(() => !controller.Snapshot.Connected);
            Check(sessions[1].Disposed, "手动断开立即释放私有连接");
            controller.Stop(); await controller.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!controller.Execute(new(EarbudCommandKind.Anc, "Deep")) && !controller.Refresh(), "停用后拒绝新命令和新扫描");
            controller.Stop();
        }
        using (var controller = new EarbudController(_ => { }, () => new[] { device, new EarbudDevice(2, "第二副", true) }, (d, o) => new EarbudFakeSession()))
        {
            controller.Start(CancellationToken.None, new(AutoConnect: true));
            await EarbudUntil(() => controller.Devices.Count == 2);
            Check(!controller.Snapshot.Connected, "多副耳机同时在线时不任意选择目标");
            controller.SetOptions(new(AutoConnect: true, PreferredName: "测试耳机"));
            controller.Refresh(); await EarbudUntil(() => controller.Snapshot.Connected);
            Check(controller.Snapshot.Connected, "自动连接可锁定唯一匹配的首选耳机");
            controller.Stop(); await controller.Completion;
        }
        var stalled = new EarbudFakeSession { StallConnect = true };
        using (var controller = new EarbudController(_ => { }, () => new[] { device }, (d, o) => stalled))
        {
            controller.Start(CancellationToken.None, new()); controller.Connect(device);
            await EarbudUntil(() => stalled.ConnectCount == 1);
            controller.Disconnect(); await EarbudUntil(() => stalled.Disposed && !controller.Snapshot.Busy);
            Check(!controller.Snapshot.Connected, "连接尚未完成时可立即取消");
            controller.Stop(); await controller.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }
        var disabled = new EarbudFakeSession { StallConnect = true };
        using (var controller = new EarbudController(_ => { }, () => new[] { device }, (d, o) => disabled))
        {
            controller.Start(CancellationToken.None, new()); controller.Connect(device);
            await EarbudUntil(() => disabled.ConnectCount == 1);
            controller.Stop(); await controller.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Check(disabled.Disposed && !controller.Snapshot.Connected, "禁用时取消未完成的连接并结束工作任务");
        }
    }
    private static void EarbudUiAndHostChecks(string root)
    {
        var sample = EarbudFakeSession.Sample with
        {
            DeviceName = "OnePlus Buds 3", ModelName = "OnePlus Buds 3", Status = "已连接", LeftPower = new(83), RightPower = new(72, true), CasePower = new(59),
            Hint = "模拟设备预览", EqChoices = ["默认", "人声"], Eq = "默认", CanEq = true,
            CanGameMode = true, CanDualDevice = true, CanSpatialAudio = true,
            SpatialChoices = [new("Off", "关闭"), new("Fixed", "固定"), new("Track", "头部追踪")],
            Peers = [new("AA:BB:CC:DD:EE:FF", "此电脑", 2, true, true), new("11:22:33:44:55:66", "手机", 2, false, false)], CanMultiControl = true
        };
        using var controller = new EarbudController(_ => { }, () => [], (d, o) => new EarbudFakeSession());
        var view = new EarbudView(controller); view.Update(sample, [new(1, "OnePlus Buds 3", true)]);
        var surface = new Border { Width = 402, Height = 300, Background = new SolidColorBrush(Color.FromRgb(24, 35, 52)), Child = view };
        surface.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/alpha-island;component/Themes/Controls.xaml", UriKind.Relative) });
        surface.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/alpha-island;component/Themes/Win11Settings.xaml", UriKind.Relative) });
        using var hwnd = new HwndSource(new HwndSourceParameters("EarbudPluginProbe") { Width = 420, Height = 320, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000) });
        hwnd.RootVisual = surface; surface.Measure(new Size(402, 300)); surface.Arrange(new Rect(0, 0, 402, 300)); surface.UpdateLayout();
        Check(view.ActualWidth <= 402 && Descendants<TextBlock>(view).Any(t => t.Text == "72%"), "WPF 卡片在岛宽度内显示分路电量");
        var anc = Descendants<ComboBox>(view).First(c => System.Windows.Automation.AutomationProperties.GetName(c) == "降噪");
        Check(anc.IsEnabled && anc.SelectedItem is EarbudChoice { Key: "Off" }, "降噪选择与快照一致，渲染不会提交设置");
        view.Update(sample with { WaitingForReadback = true }, []);
        Check(!anc.IsEnabled, "等待回读时禁用重复设置");
        view.Update(sample with { Connected = false, CanAnc = false, CanEq = false, CanGameMode = false, CanDualDevice = false, CanSpatialAudio = false }, []);
        Check(((FrameworkElement)anc.Parent).Visibility == Visibility.Collapsed, "断开或不支持时隐藏控制项");
        view.Update(sample, [new(1, "OnePlus Buds 3", true)]);
        var batteries = Descendants<DeviceBatteryIndicator>(view).ToArray();
        Check(batteries.Length == 3 && batteries.Single(b => b.Name == "LeftBatteryMeter").Percent == 83
            && batteries.Single(b => b.Name == "CaseBatteryMeter").Percent == 59
            && batteries.Single(b => b.Name == "RightBatteryMeter").Percent == 72
            && batteries.Single(b => b.Name == "RightBatteryMeter").IsCharging,
            "耳机卡片三个电池条分别显示真实分路电量和充电状态（" + string.Join(",", batteries.Select(b => b.Name + ":" + b.Percent + "/" + b.IsCharging)) + ", connected=" + sample.Connected + "）");
        view.Update(sample with { CasePower = default }, [new(1, "OnePlus Buds 3", true)]);
        Check(batteries.Single(b => b.Name == "CaseBatteryMeter").Percent == null
            && batteries.Single(b => b.Name == "LeftBatteryMeter").Percent == 83, "盒电量过期只清空盒的图形，保留有效耳机电量");
        view.Update(EarbudSnapshot.Offline("已断开"), []);
        Check(batteries.All(b => b.Percent == null && !b.IsCharging), "耳机断联清空全部电池条与充电状态");
        view.Update(sample, [new(1, "OnePlus Buds 3", true)]);
        surface.UpdateLayout();
        SaveThemePreview(surface, "docs/screens/plugin-earbuds.png");

        string source = Path.GetFullPath("examples/OppoPodsPlugin/bin/Debug/net8.0-windows10.0.19041.0");
        string pluginRoot = Path.Combine(root, "earbud-plugins"), folder = Path.Combine(pluginRoot, "oppo.pods"); Directory.CreateDirectory(folder);
        foreach (string name in new[] { "AlphaIsland.Plugins.OppoPods.dll", "AlphaIsland.Plugins.OppoPods.deps.json", "plugin.json" }) File.Copy(Path.Combine(source, name), Path.Combine(folder, name));
        var options = new IslandOptions(); var viewport = new Grid();
        using var host = new PluginHost(Dispatcher.CurrentDispatcher, options, new[] { pluginRoot },
            p => viewport.Children.Add(p.View), p => viewport.Children.Remove(p.View), _ => { });
        host.Discover();
        Check(host.Entries.Single().Instance == null && viewport.Children.Count == 0, "真实插件清单默认禁用，无后台服务");
        options.Extras[PluginHost.EnabledKey("oppo.pods")] = JsonSerializer.SerializeToElement(true); host.Sync(options);
        var entry = host.Entries.Single();
        Check(entry.Running && entry.Pages.Count == 1 && entry.Context!.Settings.Count == 4 && entry.Started.Count == 1,
            "真实独立 DLL 由插件宿主加载，注册卡片、设置及服务");
        var pluginView = (FrameworkElement)entry.Pages[0].View; pluginView.Visibility = Visibility.Visible;
        viewport.Measure(new Size(402, 300)); viewport.Arrange(new Rect(0, 0, 402, 300)); viewport.UpdateLayout();
        var selector = Descendants<ComboBox>(pluginView).First();
        Check(entry.Pages[0].KeepsWheel(selector, 120), "插件下拉框保留滚轮，不触发宿主翻页");
        var scroll = Descendants<ScrollViewer>(pluginView).First();
        // The loaded empty card may fit; use a long synthetic plugin page for the actual scroll boundary contract.
        var scrollPage = new PluginIslandPage("probe", new("scroll", "scroll", () => new ScrollViewer { Content = new Border { Height = 900 }, Height = 200 }), _ => { });
        scrollPage.View.Visibility = Visibility.Visible;
        scrollPage.View.Measure(new Size(402, 300)); scrollPage.View.Arrange(new Rect(0, 0, 402, 300)); scrollPage.View.UpdateLayout();
        var pageScroll = Descendants<ScrollViewer>(scrollPage.View).First();
        Check(scrollPage.KeepsWheel(pageScroll, -120) && !scrollPage.KeepsWheel(pageScroll, 120), "插件长内容先滚动，顶部边界才允许向上翻页");
        options.Extras[PluginHost.EnabledKey("oppo.pods")] = JsonSerializer.SerializeToElement(false); host.Sync(options);
        Check(!entry.Running && entry.Instance == null && viewport.Children.Count == 0 && entry.Started.Count == 0,
            "插件开关撤下卡片，释放实例和后台服务");
        host.Sync(options); File.Delete(Path.Combine(folder, "plugin.json")); host.Discover();
        Check(host.Entries.Count == 0, "移除插件清单并刷新即可移除插件入口");
    }
    private sealed class EarbudFakeSession : IEarbudSession
    {
        internal static EarbudSnapshot Sample => new() { Connected = true, CanAnc = true, Anc = "Off", AncChoices = [new("Off", "关闭"), new("Deep", "深度")] };
        internal readonly ConcurrentQueue<EarbudCommand> Commands = new();
        internal int ConnectCount;
        internal volatile bool Disposed;
        internal bool StallConnect;
        private EarbudSnapshot _state = Sample;
        public async Task ConnectAsync(CancellationToken token)
        { Interlocked.Increment(ref ConnectCount); if (StallConnect) await Task.Delay(Timeout.Infinite, token); }
        public EarbudSnapshot ReadSnapshot() => Disposed ? EarbudSnapshot.Offline("已断开") : Volatile.Read(ref _state);
        public void Pump(bool refresh) { }
        public void Execute(EarbudCommand command) { Commands.Enqueue(command); Volatile.Write(ref _state, _state with { Anc = command.Value }); }
        public void Dispose() => Disposed = true;
    }
    private sealed class EarbudProbeTransport : IPodTransport
    {
        private readonly Queue<PodFrame> _frames = new();
        internal readonly List<(ushort Cmd, byte[] Payload)> Writes = [];
        private byte[] _anc = [1, 1, 1, 0];
        private byte _gameMode;
        public string DeviceName => "OnePlus Buds 3";
        public string? LastError => null;
        public bool IsConnected { get; private set; }
        public event Action<PodFrame>? FrameReceived;
        public event Action? Disconnected;
        public bool Connect() { IsConnected = true; return true; }
        internal void Emit(PodFrame frame) => FrameReceived?.Invoke(frame);
        public void Send(ushort cmd, byte[] payload)
        {
            Writes.Add((cmd, payload.ToArray()));
            byte[]? response = cmd switch
            {
                OppoProtocol.CmdQueryCapability => new byte[] { 0 }.Concat(Enumerable.Repeat((byte)255, 10)).ToArray(),
                OppoProtocol.CmdBattery => [0, 4, 83, 0, 72, 1, 59, 0],
                OppoProtocol.CmdQueryAnc => _anc.ToArray(),
                OppoProtocol.CmdQueryEq => [0, 0],
                OppoProtocol.CmdBatchQuery => [OppoProtocol.FeatureGameLL, _gameMode, OppoProtocol.FeatureDualDevice, 0, OppoProtocol.FeatureSpatial, 0],
                _ => null
            };
            if (cmd == OppoProtocol.CmdAnc) _anc = payload.Length == 3 ? payload.Concat(new byte[] { 0 }).ToArray() : payload.ToArray();
            if (cmd == OppoProtocol.CmdSetFeature && payload[0] == OppoProtocol.FeatureGameLL) _gameMode = payload[1];
            if (response != null) _frames.Enqueue(new((ushort)(cmd | 0x8000), response, 0));
        }
        public void Poll(int timeoutMs) { while (_frames.TryDequeue(out var frame)) Emit(frame); }
        public void Close() { IsConnected = false; Disconnected?.Invoke(); }
        public void Dispose() { Close(); FrameReceived = null; Disconnected = null; }
    }
}
