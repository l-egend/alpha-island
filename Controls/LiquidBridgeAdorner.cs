using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;

namespace alpha_island.Controls;

/// <summary>Paints only the membrane outside existing bodies. Never draws over text or receives input.</summary>
internal sealed class LiquidBridgeAdorner(UIElement scene) : Adorner(scene)
{
    private readonly Pen _rim = new(new SolidColorBrush(Color.FromArgb(110, 232, 246, 255)), 1.1);
    public Geometry? Bridges { get; set; }
    public Geometry? Surface { get; set; }
    public Geometry? Bodies { get; set; }
    public Brush? Fill { get; set; }
    protected override void OnRender(DrawingContext context)
    {
        if (Fill == null) return;
        if (Surface != null) context.DrawGeometry(Fill, null, Surface);
        if (Bridges == null || Bodies == null) return;
        context.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, Bridges, Bodies));
        context.DrawGeometry(null, _rim, Bridges);
        context.Pop();
    }
}