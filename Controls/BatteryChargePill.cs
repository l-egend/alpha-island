using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using alpha_island.Animation;
using alpha_island.Services;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace alpha_island.Controls;

/// <summary>Charging ring and computer outline, with the percentage to the right.</summary>
public sealed class BatteryChargePill : Grid
{
    private readonly ChargingBatteryRing _ring;
    private bool _compact;
    public bool Compact
    {
        get => _compact;
        set
        {
            _compact = value;
            _ring.Width = _ring.Height = value ? 28 : 32;
            PercentText.FontSize = value ? 15 : 18;
            StateText.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        }
    }
    internal TextBlock PercentText { get; }
    internal TextBlock StateText { get; }
    public BatteryChargePill() : this(false) { }
    public BatteryChargePill(bool compact)
    {
        Margin = new Thickness(8, 0, 8, 0);
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _ring = new ChargingBatteryRing { Width = compact ? 28 : 32, Height = compact ? 28 : 32, VerticalAlignment = VerticalAlignment.Center };
        Children.Add(_ring);
        var text = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        PercentText = new TextBlock { Text = "—", FontSize = compact ? 15 : 18, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextAlignment = TextAlignment.Center };
        StateText = new TextBlock { FontSize = 10, Foreground = Brushes.LightGray, TextAlignment = TextAlignment.Center, Visibility = compact ? Visibility.Collapsed : Visibility.Visible };
        text.Children.Add(PercentText); text.Children.Add(StateText); SetColumn(text, 1); Children.Add(text);
        Compact = compact;
    }

    internal void SetBattery(BatteryStatusInfo? info)
    {
        bool connected = info is { HasBattery: true } && (info.IsOnAcPower == true || info.IsCharging);
        PercentText.Text = info is { HasBattery: true, Percent: >= 0 and <= 100 } ? $"{info.Percent}%" : "—";
        StateText.Text = info == null ? L.T("等待电池状态") : !info.HasBattery ? L.T("未检测到电池")
            : info.IsEnergySaverEnabled ? L.T("节电模式") : !connected ? L.T("电池供电") : info.Percent == 100 ? L.T("电池已充满")
            : info.SmartCharging == SmartChargingState.Active ? L.T("智能充电")
            : info.ChargeType == BatteryChargeType.ConservationMode ? L.T("电池保养") : info.IsCharging ? L.T("正在充电") : L.T("已连接电源");
        bool smartCharging = info is { HasBattery: true, SmartCharging: SmartChargingState.Active };
        _ring.SetPowerState(connected, info?.Percent ?? -1, info is { HasBattery: true, IsEnergySaverEnabled: true }, smartCharging);
        _ring.SetLevel(info is { HasBattery: true, Percent: >= 0 and <= 100 } ? info.Percent / 100d : 0);
        ToolTip = L.T("电池 ") + PercentText.Text + " · " + StateText.Text
            + (smartCharging && StateText.Text != "智能充电" ? L.T(" · 智能充电") : "")
            + (connected ? " · " + BatteryService.FormatChargeType(info!.ChargeType) : "");
    }
}

internal sealed class ChargingBatteryRing : FrameworkElement
{
    internal bool ComputerVisible { get; init; } = true;
    private static readonly DependencyProperty LevelProperty = DependencyProperty.Register("Level", typeof(double), typeof(ChargingBatteryRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly Pen Track = FrozenPen(Color.FromArgb(48, 255, 255, 255), 3);
    private static readonly Pen Charge = FrozenPen(Color.FromRgb(49, 215, 92), 3);
    private static readonly Pen Discharge = FrozenPen(Color.FromRgb(34, 211, 238), 3);
    private static readonly Pen EnergySaver = FrozenPen(Color.FromRgb(255, 208, 64), 3);
    private static readonly Pen Low = FrozenPen(Color.FromRgb(255, 170, 59), 3);
    private static readonly Pen Critical = FrozenPen(Color.FromRgb(255, 92, 106), 3);
    private static readonly Pen Computer = FrozenPen(Color.FromRgb(245, 250, 255), 1.6);
    private static readonly Geometry Bolt = Geometry.Parse("M17,0 L12,6 H16 L14,10 L21,3 H17 Z");
    private static readonly Geometry Heart = Geometry.Parse("M24,29 C22,27.5 19.5,25.5 19.5,23.5 C19.5,21 23,20.5 24,23 C25,20.5 28.5,21 28.5,23.5 C28.5,25.5 26,27.5 24,29 Z");
    private double _target;
    private bool _connected;
    private bool _energySaver;
    private bool _smartCharging;
    private int _severity;
    internal void SetPowerState(bool connected, int percent, bool energySaver = false, bool smartCharging = false)
    {
        int severity = percent is >= 0 and <= 10 ? 2 : percent is >= 0 and <= 20 ? 1 : 0;
        if (_connected == connected && _severity == severity && _energySaver == energySaver && _smartCharging == smartCharging) return;
        _connected = connected; _severity = severity; _energySaver = energySaver; _smartCharging = smartCharging; InvalidateVisual();
    }
    public ChargingBatteryRing()
    {
        Bolt.Freeze(); Heart.Freeze(); IsHitTestVisible = false;
        Unloaded += (_, _) => AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(this,LevelProperty, null);
        IsVisibleChanged += (_, _) => { if (!IsVisible) AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(this,LevelProperty, null); };
    }
    internal void SetLevel(double value)
    {
        if (Math.Abs(value - _target) < .0001) return;
        double from = (double)GetValue(LevelProperty); _target = value;
        AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(this,LevelProperty, null); SetValue(LevelProperty, value);
        if (!IsVisible || MotionPolicy.InstantMorph || MotionPolicy.PowerSave) return;
        var animation = new DoubleAnimation(from, value, TimeSpan.FromMilliseconds(280))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) => AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(this,LevelProperty, null);
        AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(this,LevelProperty, animation);
    }
    private static Pen FrozenPen(Color color, double width)
    {
        var pen = new Pen(new SolidColorBrush(color), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze(); return pen;
    }
    protected override void OnRender(DrawingContext dc)
    {
        double extent = Math.Min(ActualWidth, ActualHeight); if (extent <= 0) return;
        dc.PushTransform(new ScaleTransform(extent / 32, extent / 32));
        dc.DrawEllipse(null, Track, new Point(16, 16), 13, 13);
        var ink = _energySaver ? EnergySaver : _severity == 2 ? Critical : _severity == 1 ? Low : _connected ? Charge : Discharge;
        double level = Math.Clamp((double)GetValue(LevelProperty), 0, 1);
        if (level >= .999) dc.DrawEllipse(null, ink, new Point(16, 16), 13, 13);
        else if (level > .001)
        {
            double angle = level * Math.PI * 2 - Math.PI / 2;
            var path = new StreamGeometry();
            using (var context = path.Open())
            { context.BeginFigure(new Point(16, 3), false, false); context.ArcTo(new Point(16 + 13 * Math.Cos(angle), 16 + 13 * Math.Sin(angle)), new Size(13, 13), 0, level > .5, SweepDirection.Clockwise, true, false); }
            path.Freeze(); dc.DrawGeometry(null, ink, path);
        }
        if (ComputerVisible)
        {
            dc.DrawRoundedRectangle(null, Computer, new Rect(10, 11, 12, 9), 1.8, 1.8);
            dc.DrawLine(Computer, new Point(8, 22), new Point(24, 22));
        }
        if (_connected) dc.DrawGeometry(Brushes.White, null, Bolt);
        if (_smartCharging) dc.DrawGeometry(Brushes.White, null, Heart);
        dc.Pop();
    }
}
