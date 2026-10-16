using System.Windows;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;

namespace alpha_island.Controls;

public sealed partial class SemanticIcon
{
    private static readonly Geometry NetworkCloud = Path("M7,21 C2,21 0,18 1,14 C1,11 3,9 6,9 C6,4 10,2 14,3 C17,3 19,5 20,7 C24,5 28,8 28,12 C32,13 33,17 30,20 C29,21 27,21 25,21 Z");
    private static readonly Geometry NetworkUp = Path("M8,28 V17 H4 Q3,17 4,16 L11,8 Q12,7 13,8 L20,16 Q21,17 20,17 H16 V28 Q16,29 15,29 H9 Q8,29 8,28 Z");
    private static readonly Geometry NetworkDown = Path("M22,12 H27 Q28,12 28,13 V24 H31 Q32,24 31,25 L25,32 Q24,33 23,32 L17,25 Q16,24 17,24 H21 V13 Q21,12 22,12 Z");
    private static readonly Brush NetworkBlue = Ink(0x3D, 0x9C, 0xF5), NetworkCoral = Ink(0xFF, 0x74, 0x5D);
    private void DrawNetwork(DrawingContext dc, double progress)
    {
        dc.DrawGeometry(Brushes.White, null, NetworkCloud);
        double movement = _transitioning ? Math.Sin(progress * Math.PI) * 2 : 0;
        dc.PushTransform(new TranslateTransform(0, -movement)); dc.DrawGeometry(Active ? NetworkBlue : Brushes.Gray, null, NetworkUp); dc.Pop();
        dc.PushTransform(new TranslateTransform(0, movement)); dc.DrawGeometry(Active ? NetworkCoral : Brushes.Gray, null, NetworkDown); dc.Pop();
    }
}
