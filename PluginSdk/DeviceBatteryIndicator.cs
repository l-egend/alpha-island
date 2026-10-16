using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AlphaIsland.PluginSdk;

/// <summary>按实际百分比绘制电池；充电标记只由设备报告驱动。</summary>
public sealed class DeviceBatteryIndicator : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(nameof(Percent), typeof(int?),
        typeof(DeviceBatteryIndicator), new FrameworkPropertyMetadata(null, OnStateChanged,
            (_, value) => value is int percent && percent is >= 0 and <= 100 ? percent : null));
    public static readonly DependencyProperty IsChargingProperty = DependencyProperty.Register(nameof(IsCharging), typeof(bool),
        typeof(DeviceBatteryIndicator), new FrameworkPropertyMetadata(false, OnStateChanged));
    private static readonly DependencyProperty DisplayFractionProperty = DependencyProperty.Register(nameof(DisplayFraction), typeof(double),
        typeof(DeviceBatteryIndicator), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty ChargeOpacityProperty = DependencyProperty.Register(nameof(ChargeOpacity), typeof(double),
        typeof(DeviceBatteryIndicator), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static bool _animationsAllowed = true;
    private static event Action? AnimationPolicyChanged;
    private static readonly Brush Normal = Paint(0x4C, 0xD9, 0x95), Medium = Paint(0xFF, 0xD1, 0x66), Low = Paint(0xFF, 0x61, 0x5D);
    private static readonly Brush Muted = Paint(0x99, 0xA1, 0xAF), Outline = Paint(0xBF, 0xC6, 0xD0);
    private static readonly Pen OutlinePen = Stroke(Outline, 1.2), UnknownPen = Stroke(Muted, 1.5), BoltPen = Stroke(Paint(0x18, 0x23, 0x34), 0.55);
    private static readonly StreamGeometry Bolt = CreateBolt();
    private bool _chargingAnimation;

    public int? Percent { get => (int?)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public bool IsCharging { get => (bool)GetValue(IsChargingProperty); set => SetValue(IsChargingProperty, value); }
    private double DisplayFraction => (double)GetValue(DisplayFractionProperty);
    private double ChargeOpacity => (double)GetValue(ChargeOpacityProperty);

    /// <summary>宿主统一设置动效策略，已加载控件立即响应减弱动效及节能设置。仅在 UI 线程调用。</summary>
    public static void ConfigureAnimations(bool allowed)
    {
        if (_animationsAllowed == allowed) return;
        _animationsAllowed = allowed; AnimationPolicyChanged?.Invoke();
    }

    public DeviceBatteryIndicator()
    {
        Width = 30; Height = 16; IsHitTestVisible = false; SnapsToDevicePixels = true;
        Loaded += (_, _) => { AnimationPolicyChanged += OnPolicyChanged; SyncPolicy(); };
        Unloaded += (_, _) => { AnimationPolicyChanged -= OnPolicyChanged; StopAnimations(); };
        IsVisibleChanged += (_, _) => SyncPolicy();
        UpdateDescription();
    }
    private static void OnStateChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var indicator = (DeviceBatteryIndicator)owner;
        indicator.UpdateDescription();
        if (args.Property == PercentProperty) indicator.UpdateFill(animate: true);
        indicator.SyncCharging(); indicator.InvalidateVisual();
    }
    private bool CanAnimate => _animationsAllowed && IsLoaded && IsVisible;
    private void OnPolicyChanged()
    {
        if (Dispatcher.CheckAccess()) SyncPolicy();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(SyncPolicy));
    }
    private void SyncPolicy()
    {
        if (!CanAnimate) StopAnimations();
        else { UpdateFill(animate: false); SyncCharging(); }
    }
    private void UpdateFill(bool animate)
    {
        double previous = DisplayFraction, target = (Percent ?? 0) / 100d;
        AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(this,DisplayFractionProperty, null); SetValue(DisplayFractionProperty, target);
        if (animate && CanAnimate && Percent.HasValue && Math.Abs(previous - target) > 0.001)
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(this,DisplayFractionProperty, new DoubleAnimation(previous, target, TimeSpan.FromMilliseconds(380))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }
    private void SyncCharging()
    {
        bool animate = CanAnimate && IsCharging && Percent.HasValue;
        if (animate == _chargingAnimation) return;
        _chargingAnimation = animate;
        AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(this,ChargeOpacityProperty, null); SetValue(ChargeOpacityProperty, 1d);
        if (animate) AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(this,ChargeOpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(850))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }
    private void StopAnimations()
    { UpdateFill(animate: false); _chargingAnimation = false; AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(this,ChargeOpacityProperty, null); SetValue(ChargeOpacityProperty, 1d); }
    private void UpdateDescription()
    {
        string text = Percent.HasValue ? $"电量 {Percent}%" + (IsCharging ? "，充电中" : "") : "电量未知";
        ToolTip = text; AutomationProperties.SetName(this, text);
    }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        drawing.PushTransform(new ScaleTransform(ActualWidth / 30, ActualHeight / 16));
        drawing.DrawRoundedRectangle(null, OutlinePen, new Rect(1, 2, 25, 12), 2, 2);
        drawing.DrawRoundedRectangle(Outline, null, new Rect(27.4, 5, 1.8, 6), 0.8, 0.8);
        if (Percent.HasValue)
        {
            Brush color = Percent <= 20 ? Low : Percent <= 50 ? Medium : Normal;
            double width = 21 * Math.Clamp(DisplayFraction, 0, 1);
            if (width > 0) drawing.DrawRoundedRectangle(color, null, new Rect(3, 4, width, 8), 1, 1);
            if (IsCharging)
            {
                drawing.PushOpacity(ChargeOpacity);
                drawing.DrawGeometry(Brushes.White, BoltPen, Bolt);
                drawing.Pop();
            }
        }
        else drawing.DrawLine(UnknownPen, new Point(10, 8), new Point(18, 8));
        drawing.Pop();
    }
    private static Brush Paint(byte red, byte green, byte blue)
    { var brush = new SolidColorBrush(Color.FromRgb(red, green, blue)); brush.Freeze(); return brush; }
    private static Pen Stroke(Brush brush, double width)
    { var pen = new Pen(brush, width); pen.Freeze(); return pen; }
    private static StreamGeometry CreateBolt()
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(16, 2.5), isFilled: true, isClosed: true);
            context.PolyLineTo(new[] { new Point(11, 8.5), new Point(14, 8.5), new Point(12.5, 13.5),
                new Point(18, 6.5), new Point(15, 6.5) }, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze(); return geometry;
    }
}
