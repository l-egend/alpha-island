using AlphaIsland.Plugins.OppoPods;
using AlphaIsland.EarbudsV2.Assets.Vivo;
using AlphaIsland.EarbudsV2.Communication.Abstractions;
using AlphaIsland.EarbudsV2.Communication.Windows;
using AlphaIsland.EarbudsV2.Control.Abstractions;
using AlphaIsland.EarbudsV2.Control.Brands.Vivo;
using AlphaIsland.EarbudsV2.Control.Brands.Huawei;
using AlphaIsland.EarbudsV2.Control.Brands.Apple;
using AlphaIsland.EarbudsV2.Control.Core.Models;
using AlphaIsland.EarbudsV2.Control.Core.Features;
using System.Windows;
using System.Windows.Controls;

internal static partial class Program
{
    private static Task MultiBrandEarbudsChecks() => OnMediaDispatcher(async dispatcher =>
    {
        var catalog = VivoDeviceModelData.LoadCatalog();
        Check(catalog.Models.Count == 43, "导入用户新版 EXE 中的 43 个 vivo/iQOO 显示型号");
        foreach (var model in catalog.Models)
            Check(MultiBrandSupport.Find(model.DisplayName)?.Brand == "Vivo", "自动识别："+model.DisplayName);
        Check(catalog.Find("vivo TWS Air3 Pro 长续航")?.DisplayName == "vivo TWS Air3 Pro 长续航", "vivo 名称优先匹配准确版本");
        Check(MultiBrandSupport.ModelNames().Contains("AirPods Pro 3") && MultiBrandSupport.ModelNames().Contains("HUAWEI FreeBuds Pro 5"),
            "手动型号列表包含 Apple 与华为新增型号");
        Check(OppoPodsManager.DeviceCapabilities.GetModelNames().All(MultiBrandSupport.ModelNames().Contains),
            "旧 OPPO、一加、realme 型号完整保留");
        foreach (var name in new[] { "vivo X200", "iQOO 13", "Xiaomi 14", "Redmi K70", "OnePlus 12RT", "Soundcore Liberty Air" })
            Check(MultiBrandSupport.Find(name) == null, "不把手机或其他品牌错识别为新增耳机："+name);
        Check(MultiBrandSupport.Find("Redmi Buds 6")?.Brand == "XIAOMI", "识别 Redmi Buds 耳机");
        Check(MultiBrandSupport.Find("EDIFIER NeoBuds Pro")?.Brand == "Edifier", "识别漫步者耳机");
        Check(MultiBrandSupport.Find("AirPods Pro")?.Brand == "Apple", "AirPods 使用独立广播后端");
        Check(MultiBrandSupport.Find("HUAWEI FreeClip2") is HuaweiManagerFactory, "华为开放式耳机使用华为协议");
        var codec = new VivoFrameCodec(3);
        Check(Convert.ToHexString(codec.Encode(VivoConstants.QueryBattery, [])) == "FF040000001B0207",
            "旧代 vivo 型号的电量命令仍用已核对的 v4 GAIA 帧");
        var encoded = codec.Encode(VivoConstants.ReportBattery, [1, 83, 72, 59]);
        Check(!codec.Decode(encoded.AsSpan(0, 4)).Any() && codec.Decode(encoded.AsSpan(4)).Single().Payload.Span.SequenceEqual(new byte[] {1,83,72,59}),
            "vivo 分段帧按真实协议解码");
        var state = new BusinessState();
        state.SetConnected("vivo TWS 5");
        state.SetBattery(new(83, false), new(72, true), new(59, false));
        var controls = new HashSet<string> { "game-mode", "dual-device", "equalizer" };
        var presentation = new BrandPresentation("vivo TWS 5", true, false, false, true, false, [], -6, 6, ["Bass"],
            controls, new Dictionary<string,bool> {["game-mode"]=true}, new Dictionary<string,bool>(),
            [new("Off", NoiseMode.Off, 0, [])], "Off");
        var snapshot = MultiBrandSession.MapSnapshot(state.Snapshot(), presentation, "vivo TWS 5", eqLabel: _ => "低音");
        Check(snapshot.LeftPower.Percent == 83 && snapshot.RightPower.Percent == 72 && snapshot.RightPower.Charging && snapshot.CasePower.Percent == 59,
            "新品牌的分路电量与充电状态传入岛 SDK");
        Check(snapshot.CanAnc && snapshot.CanGameMode && snapshot.GameMode && snapshot.CanDualDevice && snapshot.CanEq,
            "按新品牌实际展示能力启用对应控制");
        Check(snapshot.EqChoices.SequenceEqual(new[] {"低音"}), "新品牌 EQ 显示名称不暴露协议键");
        var stale = state.Snapshot() with { BatteryUpdatedUtc = DateTimeOffset.UtcNow.AddSeconds(-11), LastUpdatedUtc = DateTimeOffset.UtcNow };
        Check(MultiBrandSession.MapSnapshot(stale,presentation,"vivo TWS 5").LeftPower.Percent == null,
            "其他状态刷新不会延长过期电量的寿命");
        var batteryTimestamp = state.Snapshot().BatteryUpdatedUtc;
        state.SetNoise(new(NoiseMode.Deep, null));
        Check(state.Snapshot().BatteryUpdatedUtc == batteryTimestamp, "功能更新不改写电量时间戳");
        var unknown = MultiBrandSession.MapSnapshot(state.Snapshot(), presentation with { IsKnownModel=false }, "未知耳机");
        Check(!unknown.CanAnc && !unknown.CanEq && !unknown.CanGameMode && !unknown.CanDualDevice, "未确认型号不开放控制");
        var single = snapshot with { SingleBattery = true };
        using var controller = new EarbudController(_=>{}, () => []);
        var view = new EarbudView(controller);
        view.Update(single, []);
        var batteryRow = AgentField<Grid>(view,"_batteryRow");
        Check(batteryRow.Children[1].Visibility == Visibility.Collapsed && batteryRow.Children[2].Visibility == Visibility.Collapsed,
            "单电池耳机显示整机电量，隐藏左右耳和盒的空栏");
        view.Update(snapshot, []);
        Check(batteryRow.Children[1].Visibility == Visibility.Visible && batteryRow.Children[2].Visibility == Visibility.Visible,
            "切回真无线耳机恢复三路电量布局");
        int references = WinsockLease.References;
        using (var one = WinsockLease.Acquire())
        {
            using (var two = WinsockLease.Acquire()) Check(WinsockLease.References == references+2,"新增传输分别拥有 Winsock 引用");
            Check(WinsockLease.References == references+1,"关闭一条传输不影响剩余连接");
        }
        Check(WinsockLease.References == references,"停用后释放新增传输的 Winsock 引用");
        await MultiBrandVivoProtocolCheck();
        var late = new TaskCompletionSource<IRawConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new MultiBrandConnectionProbe();
        using var session = new MultiBrandSession(new(1,"vivo TWS 5",true),new(),new VivoManagerFactory(),(_,_,_)=>late.Task);
        var opening = session.ConnectAsync(CancellationToken.None);
        session.Dispose();
        late.SetResult(connection);
        try { await opening; } catch(OperationCanceledException) { }
        await session.Cleanup.WaitAsync(TimeSpan.FromSeconds(2));
        Check(connection.Disposed && !session.ReadSnapshot().Connected, "取消连接后也释放迟到的底层连接，不让旧状态重新出现");
    });

    private static async Task MultiBrandVivoProtocolCheck()
    {
        var raw = new VivoRawProbe();
        using var session = new MultiBrandSession(new(1, "vivo TWS 5", true), new(), new VivoManagerFactory(),
            (_, _, _) => Task.FromResult<IRawConnection>(raw));
        try
        {
            await session.ConnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            var snapshot = session.ReadSnapshot();
            Check(snapshot.Connected && snapshot.ModelName == "vivo TWS 5",
                "真实 vivo 工厂和管理器通过协议握手建立岛会话");
            Check(snapshot.LeftPower.Percent == 83 && snapshot.RightPower.Percent == 72 && snapshot.CasePower.Percent == 59,
                "原始 vivo 电量响应经真实解析和适配后显示三路电量");
            Check(raw.Commands.Contains(VivoConstants.QueryBattery) && !raw.Commands.Contains(0x0130),
                "连接时读取 vivo 状态，不自动更改降噪");
        }
        finally { session.Dispose(); await session.Cleanup.WaitAsync(TimeSpan.FromSeconds(3)); }
        Check(raw.Disposed, "真实新增品牌管理器断开时释放原始连接和轮询");
    }
    private sealed class VivoRawProbe : IRawConnection
    {
        private readonly VivoFrameCodec _codec = new();
        internal readonly List<ushort> Commands = [];
        internal bool Disposed;
        public bool IsConnected => !Disposed;
        public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
        public event EventHandler? Disconnected;
        public Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken token) { Disconnected?.Invoke(this,EventArgs.Empty); return Task.CompletedTask; }
        public Task SendAsync(ReadOnlyMemory<byte> data,CancellationToken token)
        {
            foreach (var frame in _codec.Decode(data.Span))
            {
                Commands.Add(frame.Command);
                byte[] payload = frame.Command switch
                {
                    VivoConstants.QueryBattery => [0,83,72,59,0],
                    VivoConstants.QueryNoiseMode => [0,1,0],
                    _ => [0,0]
                };
                DataReceived?.Invoke(this,_codec.Encode((ushort)(frame.Command | 0x8000),payload));
            }
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class MultiBrandConnectionProbe : IRawConnection
    {
        internal bool Disposed;
        public bool IsConnected => !Disposed;
        public event EventHandler<ReadOnlyMemory<byte>>? DataReceived { add {} remove {} }
        public event EventHandler? Disconnected { add {} remove {} }
        public Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task SendAsync(ReadOnlyMemory<byte> data,CancellationToken token) => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed=true; return ValueTask.CompletedTask; }
    }
}
