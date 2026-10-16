namespace AlphaIsland.Plugins.OppoPods;

internal sealed record EarbudDevice(ulong Address, string Name, bool Connected, AlphaIsland.EarbudsV2.Communication.Abstractions.DeviceCandidate? Candidate = null)
{
    public override string ToString() => Name + (Connected ? " · 已连接" : " · 已配对");
}
internal sealed record EarbudOptions(bool AutoConnect = false, bool AutoReconnect = true,
    string PreferredName = "", string ModelOverride = "自动识别");
internal sealed record EarbudChoice(string Key, string Label)
{
    public override string ToString() => Label;
}
internal sealed record EarbudPeer(string Address, string Name, int ConnectionState, bool IsCurrent, bool AudioActive);
internal enum EarbudCommandKind { Anc, Eq, GameMode, GameSound, DualDevice, SpatialSound, SpatialAudio, MultiConnect, MultiDisconnect, MultiPriority, AutoPriority }
internal sealed record EarbudCommand(EarbudCommandKind Kind, string Value)
{
    internal bool Enabled => Value == "true";
}
internal readonly record struct EarbudBattery(int? Percent, bool Charging = false)
{
    internal string Text => Percent.HasValue ? Percent + "%" + (Charging ? " ⚡" : "") : "—";
}
internal sealed record EarbudSnapshot
{
    internal bool Connected { get; init; }
    internal bool SingleBattery { get; init; }
    internal bool Busy { get; init; }
    internal bool WaitingForReadback { get; init; }
    internal string DeviceName { get; init; } = "耳机控制";
    internal string ModelName { get; init; } = "";
    internal string Status { get; init; } = "点击刷新，选择已配对的耳机";
    internal string Hint { get; init; } = "先在 Windows 蓝牙设置中配对并连接耳机";
    internal EarbudBattery LeftPower { get; init; }
    internal EarbudBattery RightPower { get; init; }
    internal EarbudBattery CasePower { get; init; }
    internal string LeftBattery => LeftPower.Text;
    internal string RightBattery => RightPower.Text;
    internal string CaseBattery => CasePower.Text;
    internal string Wearing { get; init; } = "";
    internal string Firmware { get; init; } = "";
    internal string Codec { get; init; } = "";
    internal EarbudChoice[] AncChoices { get; init; } = [];
    internal string Anc { get; init; } = "?";
    internal bool CanAnc { get; init; }
    internal string[] EqChoices { get; init; } = [];
    internal string Eq { get; init; } = "?";
    internal bool CanEq { get; init; }
    internal bool CanGameMode { get; init; }
    internal bool GameMode { get; init; }
    internal bool CanGameSound { get; init; }
    internal bool GameSound { get; init; }
    internal bool CanDualDevice { get; init; }
    internal bool DualDevice { get; init; }
    internal bool CanSpatialSound { get; init; }
    internal bool SpatialSound { get; init; }
    internal EarbudChoice[] SpatialChoices { get; init; } = [];
    internal string SpatialAudio { get; init; } = "Off";
    internal bool CanSpatialAudio { get; init; }
    internal EarbudPeer[] Peers { get; init; } = [];
    internal bool CanMultiControl { get; init; }
    internal bool CanPriorityControl { get; init; }
    internal string PriorityAddress { get; init; } = "";
    internal bool AutoPriority { get; init; }

    internal static EarbudSnapshot Offline(string status, string hint = "") => new() { Status = status, Hint = hint };
    internal bool Allows(EarbudCommand command) => Connected && !Busy && !WaitingForReadback && (command.Kind switch
    {
        EarbudCommandKind.Anc => CanAnc && AncChoices.Any(c => c.Key == command.Value),
        EarbudCommandKind.Eq => CanEq && EqChoices.Contains(command.Value),
        EarbudCommandKind.SpatialAudio => CanSpatialAudio && SpatialChoices.Any(c => c.Key == command.Value),
        EarbudCommandKind.GameMode => CanGameMode && IsBoolean(command.Value),
        EarbudCommandKind.GameSound => CanGameSound && IsBoolean(command.Value),
        EarbudCommandKind.DualDevice => CanDualDevice && IsBoolean(command.Value),
        EarbudCommandKind.SpatialSound => CanSpatialSound && IsBoolean(command.Value),
        EarbudCommandKind.MultiConnect => CanMultiControl && Peers.Any(p => !p.IsCurrent && p.ConnectionState == 0 && p.Address == command.Value),
        EarbudCommandKind.MultiDisconnect => CanMultiControl && Peers.Any(p => !p.IsCurrent && p.ConnectionState == 2 && p.Address == command.Value),
        EarbudCommandKind.MultiPriority => CanPriorityControl && Peers.Any(p => p.Address == command.Value),
        EarbudCommandKind.AutoPriority => CanPriorityControl,
        _ => false
    });
    private static bool IsBoolean(string value) => value is "true" or "false";
}

internal interface IEarbudSession : IDisposable
{
    Task ConnectAsync(CancellationToken token);
    EarbudSnapshot ReadSnapshot();
    void Pump(bool refresh);
    void Execute(EarbudCommand command);
}
