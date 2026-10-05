using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace alpha_island.Animation
{
    /// <summary>
    /// 动画复位辅助：把 <c>BeginAnimation(property, null)</c> 那套"先摘时钟、再写终值"收成一组短调用。
    ///
    /// 为什么值得单独一个类：一个元素通常同时被多条动画驱动（X / Y / Opacity…），
    /// 手抄复位时少一行就留下一个还挂着的时钟 —— 属性被时钟接管，下一次动画从残值起步，
    /// 表现是"偶尔一顿"或"元素卡在半路"，而且不报错、只在特定路径上复现。
    /// 音乐球那段复位（隐藏 / 回到原位 / 随岛收缩三条路径）就抄了三遍，本类是给这类地方用的。
    ///
    /// 用法固定两步：先 <c>Clear</c>（动画时钟归零，属性回落到本地值），再写终值。
    /// </summary>
    public static class AnimationReset
    {
        /// <summary>
        /// 摘掉某个属性上的动画时钟（<paramref name="target"/> 为空时安全忽略）。
        /// 参数类型是 <see cref="IAnimatable"/>（BeginAnimation 就在它上面）：
        /// UIElement / FrameworkElement 与各种 Transform 都实现它，Animatable 反而不是它们的基类。
        /// </summary>
        public static void Clear(IAnimatable? target, DependencyProperty property)
        {
            target?.BeginAnimation(property, null);
        }

        public static void Opacity(UIElement? element) => Clear(element, UIElement.OpacityProperty);

        public static void Width(FrameworkElement? element) => Clear(element, FrameworkElement.WidthProperty);

        public static void Height(FrameworkElement? element) => Clear(element, FrameworkElement.HeightProperty);

        public static void Translate(TranslateTransform? transform)
        {
            Clear(transform, TranslateTransform.XProperty);
            Clear(transform, TranslateTransform.YProperty);
        }

        public static void Scale(ScaleTransform? transform)
        {
            Clear(transform, ScaleTransform.ScaleXProperty);
            Clear(transform, ScaleTransform.ScaleYProperty);
        }

        public static void Rotate(RotateTransform? transform) => Clear(transform, RotateTransform.AngleProperty);

        public static void BlurRadius(BlurEffect? blur) => Clear(blur, BlurEffect.RadiusProperty);
    }
}
