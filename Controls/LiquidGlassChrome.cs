using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using alpha_island.Animation;
using alpha_island.Core;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Pen = System.Windows.Media.Pen;

namespace alpha_island.Controls;

/// <summary>Vector Fresnel rim. It draws inside the real outline and never intercepts input.</summary>
internal sealed class LiquidGlassChrome : Adorner
{
    private readonly Border _body;
    private readonly LiquidIslandShape? _shape;
    private readonly RectangleGeometry _rounded = new();
    private readonly RadialGradientBrush _key = new() { RadiusX = .47, RadiusY = .8 };
    private readonly RadialGradientBrush _opposite = new() { RadiusX = .46, RadiusY = .75 };
    private readonly LinearGradientBrush _edge = new() { StartPoint = new(0, 0), EndPoint = new(1, 1) };
    private readonly Pen _keyPen, _oppositePen, _edgePen;
    private GlassLight _light = GlassLight.Default;
    private bool _enabled = true;
    private double _materialOpacity = .08;
    internal Geometry? JoinMask { get; set; }
    private Geometry? _cachedMask, _joinClip;
    private long _cachedRevision;
    private Rect _cachedRect;
    private double _cachedRadius;

    public LiquidGlassChrome(Border body, LiquidIslandShape? shape = null) : base(body)
    {
        _body = body; _shape = shape; IsHitTestVisible = false; Focusable = false;
        _key.GradientStops.Add(new GradientStop(Colors.White, 0));
        _key.GradientStops.Add(new GradientStop(Color.FromArgb(145, 230, 245, 255), .32));
        _key.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        _opposite.GradientStops.Add(new GradientStop(Color.FromArgb(100, 130, 220, 255), 0));
        _opposite.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        _edge.GradientStops.Add(new GradientStop(Color.FromArgb(100, 255, 255, 255), 0));
        _edge.GradientStops.Add(new GradientStop(Color.FromArgb(18, 255, 255, 255), .48));
        _edge.GradientStops.Add(new GradientStop(Color.FromArgb(75, 210, 230, 255), 1));
        _keyPen = new Pen(_key, 3.2); _oppositePen = new Pen(_opposite, 2.0); _edgePen = new Pen(_edge, 1.15);
        ApplyLight(GlassLight.Default, false);
    }

    public void SetMaterial(Color accent, double opacity, bool enabled)
    {
        _enabled = enabled; _materialOpacity = Math.Clamp(opacity, .05, .98);
        _opposite.GradientStops[0].Color = Color.FromArgb(95, accent.R, accent.G, accent.B);
        _key.Opacity = .46 + (1 - _materialOpacity) * .36;
        _edge.Opacity = .6 + _materialOpacity * .25;
        InvalidateVisual();
    }

    public void SetLight(GlassLight light, bool animate)
    {
        double dot = Vector.Multiply(_light.Direction, light.Direction);
        if (dot > .995 && Math.Abs(_light.Brightness - light.Brightness) < .035 && Math.Abs(_light.Contrast - light.Contrast) < .04) return;
        _light = light; ApplyLight(light, animate);
    }

    private void ApplyLight(GlassLight light, bool animate)
    {
        var key = new Point(.5 + light.Direction.X * .54, .5 + light.Direction.Y * .54);
        var opposite = new Point(1 - key.X, 1 - key.Y);
        void Position(RadialGradientBrush brush, Point point)
        {
            var from = brush.Center;
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(brush,RadialGradientBrush.CenterProperty, null);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(brush,RadialGradientBrush.GradientOriginProperty, null);
            brush.Center = brush.GradientOrigin = point;
            if (animate && (from - point).LengthSquared > .0003 && !MotionPolicy.InstantMorph && !MotionPolicy.PowerSave)
            {
                var duration = TimeSpan.FromMilliseconds(260);
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(brush,RadialGradientBrush.CenterProperty, new PointAnimation(from, point, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(brush,RadialGradientBrush.GradientOriginProperty, new PointAnimation(from, point, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        }
        Position(_key, key); Position(_opposite, opposite);
        _key.GradientStops[0].Color = light.Tint;
        _key.GradientStops[1].Color = Color.FromArgb((byte)Math.Clamp(95 + light.Brightness * 100, 95, 195), light.Tint.R, light.Tint.G, light.Tint.B);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        if (!_enabled || RenderSize.Width < 2 || RenderSize.Height < 2) return;
        _rounded.Rect = new Rect(RenderSize);
        _rounded.RadiusX = _rounded.RadiusY = Math.Min(_body.CornerRadius.TopLeft, Math.Min(RenderSize.Width, RenderSize.Height) / 2);
        Geometry outline = _shape != null ? _shape.Geometry : _rounded;
        Geometry clip = outline;
        if (JoinMask != null)
        {
            if (_joinClip == null || !ReferenceEquals(_cachedMask, JoinMask) || _cachedRevision != (_shape?.Revision ?? 0)
                || _cachedRect != _rounded.Rect || _cachedRadius != _rounded.RadiusX)
            {
                _joinClip = LiquidGeometrySnapshot.Combine(outline, JoinMask, GeometryCombineMode.Exclude);
                _cachedMask = JoinMask; _cachedRevision = _shape?.Revision ?? 0;
                _cachedRect = _rounded.Rect; _cachedRadius = _rounded.RadiusX;
            }
            clip = _joinClip;
        }
        else { _cachedMask = null; _joinClip = null; }
        context.PushClip(clip);
        context.DrawGeometry(null, _edgePen, outline);
        context.DrawGeometry(null, _keyPen, outline);
        context.DrawGeometry(null, _oppositePen, outline);
        context.Pop();
    }
}
