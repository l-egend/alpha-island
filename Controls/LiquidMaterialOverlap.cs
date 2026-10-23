using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using alpha_island.Animation;
using Brush = System.Windows.Media.Brush;

namespace alpha_island.Controls;

/// <summary>Draw shared translucent material once at a join, while child icons/text keep their own visibility.</summary>
internal static class LiquidMaterialOverlap
{
    private sealed class State
    {
        public Brush? Brush;
        public BindingBase? Binding;
        public bool Active;
    }
    private static readonly ConditionalWeakTable<Border, State> States = new();

    public static void Apply(Border body, Geometry? overlap)
    {
        if (overlap == null)
        {
            if (!States.TryGetValue(body, out var prior) || !prior.Active) return;
            prior.Active = false;
            if (prior.Binding != null) BindingOperations.SetBinding(body, Border.BackgroundProperty, prior.Binding);
            else body.SetCurrentValue(Border.BackgroundProperty, prior.Brush);
            prior.Brush = null; prior.Binding = null;
            return;
        }
        var state = States.GetValue(body, _ => new State());
        if (!state.Active)
        {
            state.Brush = body.Background; state.Binding = BindingOperations.GetBindingBase(body, Border.BackgroundProperty); state.Active = true;
        }
        var rect = new Rect(0, 0, Math.Max(1, body.ActualWidth), Math.Max(1, body.ActualHeight));
        var drawing = new DrawingGroup { ClipGeometry = LiquidGeometrySnapshot.Combine(new RectangleGeometry(rect), overlap, GeometryCombineMode.Exclude) };
        drawing.Children.Add(new GeometryDrawing(state.Brush, null, new RectangleGeometry(rect)));
        body.SetCurrentValue(Border.BackgroundProperty, new DrawingBrush(drawing)
        { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = rect, ViewportUnits = BrushMappingMode.RelativeToBoundingBox, Viewport = new Rect(0, 0, 1, 1), Stretch = Stretch.Fill });
    }
}
