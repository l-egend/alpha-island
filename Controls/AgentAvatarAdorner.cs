using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace alpha_island.Controls;

// Only the transparent artwork escapes the rounded material clip. Input continues
// to belong to the stable island beneath it, and the shared avatar clock supplies frames.
internal sealed class AgentAvatarAdorner : Adorner
{
    internal const double ArtworkSize = 60;
    private readonly SemanticIcon _icon;
    private readonly Func<double> _opacity;
    internal AgentAvatarAdorner(SemanticIcon icon, Func<double> opacity) : base(icon)
    {
        _icon = icon; _opacity = opacity;
        IsHitTestVisible = false; Focusable = false; ClipToBounds = false;
    }
    protected override void OnRender(DrawingContext context)
    {
        if (_icon.Source == null || _icon.ActualWidth <= 0 || _icon.ActualHeight <= 0) return;
        context.PushOpacity(_opacity());
        context.DrawImage(_icon.Source, new Rect((_icon.ActualWidth - ArtworkSize) / 2,
            (_icon.ActualHeight - ArtworkSize) / 2, ArtworkSize, ArtworkSize));
        context.Pop();
    }
}
