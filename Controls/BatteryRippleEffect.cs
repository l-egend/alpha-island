using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace alpha_island.Controls;

/// <summary>A finite green/amber bloom and three circular waves emitted from the battery badge.</summary>
public sealed class BatteryRippleEffect : FrameworkElement
{
    private bool _isCharging;
    private double _progress = 1;
    private bool _animating;
    private long _startTimestamp;
    private double _lastFrameSeconds, _visibleSeconds;
    internal bool IsAnimating => _animating;
    private Point? _origin;
    private const double DurationSeconds = 2.0;
    private static readonly Color ChargingGreen = Color.FromRgb(0x00, 0xE6, 0x82);
    private static readonly Color DischargingAmber = Color.FromRgb(0xF5, 0x82, 0x20);
    // Frozen brushes avoid allocating gradient brushes and stops on every frame.
    private static readonly RadialGradientBrush ChargingBloom = CreateBloom(ChargingGreen);
    private static readonly RadialGradientBrush DischargingBloom = CreateBloom(DischargingAmber);
    private static readonly RadialGradientBrush ChargingWave = CreateWave(ChargingGreen);
    private static readonly RadialGradientBrush DischargingWave = CreateWave(DischargingAmber);

    public BatteryRippleEffect()
    {
        IsHitTestVisible = false;
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) => { if (!IsVisible) Stop(); };
    }

    public void Trigger(bool isCharging, Point? origin = null)
    {
        _isCharging = isCharging;
        _origin = origin;
        _progress = 0;
        _startTimestamp = Stopwatch.GetTimestamp();
        _lastFrameSeconds = _visibleSeconds = 0;
        if (!_animating)
        {
            _animating = true;
            CompositionTarget.Rendering += OnRendering;
        }
        InvalidateVisual();
    }

    public void Stop()
    {
        if (_animating)
        {
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
        }
        _progress = 1;
        InvalidateVisual();
    }

    private void OnRendering(object? sender, EventArgs args)
    {
        double seconds = (double)(Stopwatch.GetTimestamp() - _startTimestamp) / Stopwatch.Frequency;
        _visibleSeconds += Math.Clamp(seconds - _lastFrameSeconds, 0, .05);
        _lastFrameSeconds = seconds;
        _progress = _visibleSeconds / DurationSeconds;
        if (_progress >= 1) { Stop(); return; }
        InvalidateVisual();
    }

    private static RadialGradientBrush CreateBloom(Color color)
    {
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(108, color.R, color.G, color.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(54, color.R, color.G, color.B), .48));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
        brush.Freeze(); return brush;
    }

    private static RadialGradientBrush CreateWave(Color color)
    {
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), .68));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(48, color.R, color.G, color.B), .80));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(96, color.R, color.G, color.B), .90));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(38, color.R, color.G, color.B), .97));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
        brush.Freeze(); return brush;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!_animating && _progress >= 1) return;
        double width = ActualWidth, height = ActualHeight;
        if (width <= 4 || height <= 4) return;
        var origin = _origin ?? new Point(Math.Min(24, width / 2), height / 2);
        var center = new Point(Math.Clamp(origin.X, 0, width), Math.Clamp(origin.Y, 0, height));
        // Keep the ripple circular and local to the badge instead of stretching across the pill.
        double maxRadius = Math.Min(width, Math.Max(48, height * 1.7));
        if (_progress < .6)
        {
            dc.PushOpacity(Math.Sin(_progress / .6 * Math.PI));
            double bloomRadius = Math.Min(maxRadius, 32);
            dc.DrawEllipse(_isCharging ? ChargingBloom : DischargingBloom, null, center, bloomRadius, bloomRadius);
            dc.Pop();
        }
        for (int index = 0; index < 3; index++)
        {
            double offset = index * .22;
            double phase = (_progress - offset) / (1 - offset);
            if (phase < 0 || phase > 1) continue;
            double ease = 1 - Math.Pow(1 - phase, 2.2);
            double weight = index switch { 0 => 1, 1 => .72, _ => .52 };
            double opacity = Math.Sin(phase * Math.PI) * (1 - phase * .25) * weight;
            if (opacity < .01) continue;
            dc.PushOpacity(opacity);
            double radius = 16 + ease * (maxRadius - 16);
            dc.DrawEllipse(_isCharging ? ChargingWave : DischargingWave, null, center, radius, radius);
            dc.Pop();
        }
    }
}
