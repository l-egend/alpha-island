using System.Windows;

namespace alpha_island.Animation
{
    /// <summary>
    /// 一个形态的几何描述（宽 / 高 / 距屏幕顶部的位移 / 圆角）。
    /// 把"形态"抽成一个值对象，动画器就只需要在两个值之间插值，
    /// 加第四种形态时不用再改动画器。
    /// </summary>
    public readonly struct IslandGeometry
    {
        public IslandGeometry(double width, double height, double top, CornerRadius corner)
        {
            // 宽/高/圆角一律钳到非负。插值的 t 允许超过 1（凝胶过冲），
            // 而 CornerRadius 的外插会算出负数 —— WPF 的 Border.CornerRadius 直接抛
            // ArgumentException（"不是属性 CornerRadius 的有效值"），
            // 且是在 CompositionTarget.Rendering 里抛，等于每帧都炸、动画永远收不了尾。
            Width = width < 0 ? 0 : width;
            Height = height < 0 ? 0 : height;
            Top = top;
            Corner = new CornerRadius(
                NonNegative(corner.TopLeft),
                NonNegative(corner.TopRight),
                NonNegative(corner.BottomRight),
                NonNegative(corner.BottomLeft));
        }

        private static double NonNegative(double v) => v < 0 ? 0 : v;

        public double Width { get; }
        public double Height { get; }
        public double Top { get; }
        public CornerRadius Corner { get; }

        public static IslandGeometry Lerp(in IslandGeometry a, in IslandGeometry b, double t)
        {
            return new IslandGeometry(
                a.Width + (b.Width - a.Width) * t,
                a.Height + (b.Height - a.Height) * t,
                a.Top + (b.Top - a.Top) * t,
                new CornerRadius(
                    a.Corner.TopLeft + (b.Corner.TopLeft - a.Corner.TopLeft) * t,
                    a.Corner.TopRight + (b.Corner.TopRight - a.Corner.TopRight) * t,
                    a.Corner.BottomRight + (b.Corner.BottomRight - a.Corner.BottomRight) * t,
                    a.Corner.BottomLeft + (b.Corner.BottomLeft - a.Corner.BottomLeft) * t));
        }
    }
}
