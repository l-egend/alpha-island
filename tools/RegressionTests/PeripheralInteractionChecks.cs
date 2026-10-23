using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using AlphaIsland.PluginSdk;
using AlphaIsland.Plugins.OppoPods;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using OppoPodsManager;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using alpha_island.Interop;
using System.Runtime.InteropServices.WindowsRuntime;

internal static partial class Program
{
    private static void PeripheralInteractionChecks(alpha_island.MainWindow window, Grid root)
    {
        var failures = new List<Exception>();
        void Run(Action check) { try { check(); } catch (Exception error) { failures.Add(error); Console.WriteLine(error.Message); } }
        foreach(bool input in new[]{true,false})Run(() =>
        {
            using var memory=new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using var stream=input?(IDisposable)memory.GetInputStreamAt(0):memory.GetOutputStreamAt(0);
            using var wrapper=input?(IDisposable)new Windows.Storage.Streams.DataReader((Windows.Storage.Streams.IInputStream)stream)
                :new Windows.Storage.Streams.DataWriter((Windows.Storage.Streams.IOutputStream)stream);
            using var transport=new WindowsRfcommStreamTransport(1);
            SetAgentField(transport,input?"_reader":"_writer",wrapper);transport.Close();
            bool closed=false;
            try
            {
                if(input)AwaitPlugin(((Windows.Storage.Streams.IInputStream)stream).ReadAsync(new Windows.Storage.Streams.Buffer(1),1,Windows.Storage.Streams.InputStreamOptions.Partial).AsTask());
                else AwaitPlugin(((Windows.Storage.Streams.IOutputStream)stream).FlushAsync().AsTask());
            }
            catch(Exception error)when(error is ObjectDisposedException||error.HResult==unchecked((int)0x80000013)){closed=true;}
            Check(closed,"关闭耳机控制连接真正关闭底层"+(input?"输入":"输出")+"流，不留下分离的活动通道");
        });
        Run(() =>
        {
            using var transport=new WindowsConnectionTransport(()=>[(1UL,"fixture")],()=>[],(_,_)=>
                [()=>new FailingEarbudTransport("RFCOMM 10048"),()=>new FailingEarbudTransport("GATT unavailable")]);
            Check(!transport.Connect()&&transport.LastError?.Contains("10048")==true&&transport.LastError.Contains("GATT unavailable"),
                "耳机所有连接方式失败时保留具体通道错误，不只显示笼统失败");
        });
        Run(() =>
        {
            string text = "";
            bool threw = false;
            try { ClipboardTextWriter.Copy("#0A5078", value => { text = value; throw new ExternalException("flush failed"); }, () => text); }
            catch (ExternalException) { threw = true; }
            Check(!threw && text == "#0A5078", "剪贴板写入后持久化异常，回读确认文本一致时视为复制成功");
            var original=new ExternalException("write failed");
            bool confirmedFailure=false;
            try{ClipboardTextWriter.Copy("wanted",_=>throw original,()=>"different");}
            catch(ExternalException error){confirmedFailure=ReferenceEquals(error,original);}
            Check(confirmedFailure,"剪贴板回读不一致时仍报告原始写入失败");
        });
        Run(() =>
        {
            string copied = "";
            var tools = new ScreenToolsWindow(() => ChatConfig.From(new IslandOptions()), copyText: text => copied = text,
                completed: output => { if (output.Kind == "color") throw new ExternalException("notification fixture failure"); });
            try
            {
                tools.ApplyPickedColor(new(10, 80, 120));
                Check(copied == "#0A5078" && AgentField<TextBlock>(tools, "_status").Text.StartsWith("已复制"),
                    "颜色已复制后通知回调失败，不误报为剪贴板复制失败");
            }
            finally { tools.Close(); }
        });
        Run(() =>
        {
            var options = AgentField<IslandOptions>(window, "_options");
            bool notify = options.ClipboardNotifyEnabled;
            try
            {
                options.ClipboardNotifyEnabled = true;
                PrivateCall(window, "OnClipboardContentCopied", null, (Summary: "#0A5078", IsCut: false));
                PrivateCall(window, "BeginComponentHold", new Point(), false);
                PrivateCall(window, "OpenComponentMenu", false);
                Check(AgentField<bool>(window, "_componentMenuVisible"), "取色复制通知驻留时，长按切换条仍能打开图标菜单");
                PrivateCall(window,"SelectComponent",IslandPageIds.Volume);
                Check(AgentField<IslandPageHost>(window,"_pages").ActiveId==IslandPageIds.Volume
                    &&AgentField<IslandState>(window,"_state")==IslandState.Card&&AgentField<bool>(window,"_clipboardLocked"),
                    "复制驻留状态可选择其他卡片，同时保留剪贴板提示与数据");
            }
            finally
            {
                PrivateCall(window, "CloseComponentSwitcher");
                PrivateCall(window, "OnClipboardCopiedDismissed", null, EventArgs.Empty);
                options.ClipboardNotifyEnabled = notify;
            }
        });
        Run(() =>
        {
            using var controller = new EarbudController(_ => { }, () => [], (_, _) => new EarbudFakeSession());
            var view = new EarbudView(controller);
            view.Update(EarbudSnapshot.Offline("控制连接失败"), [new(1, "OPPO Enco Clip2", true)]);
            var frame = new Border { Width = 402, Height = 200, Child = view };
            frame.Measure(new Size(402, 200)); frame.Arrange(new Rect(0, 0, 402, 200)); frame.UpdateLayout();
            var meters = AgentField<DeviceBatteryIndicator[]>(view, "_batteryMeters");
            var labels = AgentField<TextBlock[]>(view, "_battery");
            var gaps = meters.Select((meter, i) => labels[i].TransformToAncestor(view).Transform(new Point()).X
                - meter.TransformToAncestor(view).Transform(new Point(meter.ActualWidth, 0)).X).ToArray();
            Console.WriteLine("LAYOUT: battery-label-gaps=" + string.Join(",", gaps));
            Directory.CreateDirectory("bin/peripheral-previews127");
            SaveThemePreview(frame, "bin/peripheral-previews127/earbuds-offline.png");
            Check(gaps.All(gap => gap >= 10), "耳机电池图形与未知电量占位文字之间保留清晰间隔");
        });
        if (failures.Count > 0) throw new AggregateException(failures);
    }
    private sealed class FailingEarbudTransport(string error):IPodTransport
    {
        public string DeviceName=>"fixture";
        public string LastError=>error;
        public bool IsConnected=>false;
        public event Action<PodFrame>? FrameReceived{add{}remove{}}
        public event Action? Disconnected{add{}remove{}}
        public bool Connect()=>false;
        public void Send(ushort command,byte[] payload){}
        public void Poll(int milliseconds){}
        public void Close(){}
        public void Dispose(){}
    }

    private static async Task EarbudConnectionLiveCheck()
    {
        Log.Sink = message => Console.WriteLine(System.Text.RegularExpressions.Regex.Replace(message, @"(?i)\b[0-9a-f]{12}\b", "<device>"));
        var devices = await Task.Run(DeviceDiscovery.ListConnected);
        var candidates = devices.Where(device => device.name.Contains("Clip2", StringComparison.OrdinalIgnoreCase)
            || device.name.Contains("Clip 2", StringComparison.OrdinalIgnoreCase)).ToArray();
        Console.WriteLine("LIVE: Windows-connected Enco Clip2 count=" + candidates.Length);
        Check(candidates.Length == 1, "Windows 当前枚举到已连接的 OPPO Enco Clip2");
        var device = candidates[0];
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            using var session = new ProtocolSession(new(device.addr, device.name, true), new());
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await session.ConnectAsync(limit.Token);
            var snapshot = session.ReadSnapshot();
            Check(snapshot.Connected, $"第{attempt}次建立控制通道，前次连接已完整释放");
            Console.WriteLine($"LIVE: L={snapshot.LeftPower.Percent} R={snapshot.RightPower.Percent} case={snapshot.CasePower.Percent}");
        }
        using var controller = new EarbudController(message => Console.WriteLine("CONTROLLER: " + message));
        controller.Start(CancellationToken.None, new(AutoConnect: true, PreferredName: device.name));
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!controller.Snapshot.Connected && DateTime.UtcNow < deadline) await Task.Delay(100);
            Console.WriteLine("CONTROLLER: " + controller.Snapshot.Status + " / " + controller.Snapshot.Hint);
            Check(controller.Snapshot.Connected, "使用完整设备发现和控制器也能建立耳机连接");
            await Task.Delay(18000);
            Check(controller.Snapshot.Connected && controller.Snapshot.LeftPower.Percent.HasValue,
                "控制器持续轮询18秒后保持连接和实时电量");
        }
        finally { controller.Stop(); await controller.Completion.WaitAsync(TimeSpan.FromSeconds(4)); }
    }
}
