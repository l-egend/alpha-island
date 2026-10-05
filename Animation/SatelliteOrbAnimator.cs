using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace alpha_island.Animation
{
    /// <summary>卫星小岛的显示生命周期：再次显示时取消旧动画，过期回调不能收起新一轮的小岛。</summary>
    internal static class SatelliteOrbAnimator
    {
        private sealed class State
        {
            public int Generation;
            public bool Showing;
            public bool Hiding;
        }

        private static readonly ConditionalWeakTable<Border, State> States = new();

        /// <returns>是否需要重新播放入场动画。</returns>
        public static bool Show(Border orb)
        {
            var state = States.GetValue(orb, _ => new State());
            if (state.Showing && orb.Visibility == Visibility.Visible && orb.Opacity > 0.99)
                return false;

            state.Generation++;
            state.Showing = true;
            state.Hiding = false;
            ResetAnimations(orb);
            orb.Visibility = Visibility.Visible;
            orb.Opacity = 1;
            orb.IsHitTestVisible = true;
            return true;
        }

        public static void Hide(Border orb, bool immediate = false)
        {
            var state = States.GetValue(orb, _ => new State());
            if (orb.Visibility != Visibility.Visible && !state.Showing && !state.Hiding) return;
            if (state.Hiding && !immediate) return; // 同步刷新不能反复从头播放淡出。

            int generation = ++state.Generation;
            state.Showing = false;
            state.Hiding = !immediate;
            orb.IsHitTestVisible = false;
            if (immediate) { FinishHide(orb, state, generation); return; }

            var fade = new DoubleAnimation(orb.Opacity, 0, TimeSpan.FromMilliseconds(200));
            fade.Completed += (_, _) => FinishHide(orb, state, generation);
            orb.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        public static void Collapse(Border orb, bool immediate = false)
        {
            var state = States.GetValue(orb, _ => new State());
            if (orb.Visibility != Visibility.Visible) return;
            int generation = ++state.Generation;
            state.Showing = false;
            state.Hiding = !immediate;
            orb.IsHitTestVisible = false;
            if (immediate) { FinishHide(orb, state, generation); return; }

            var duration = TimeSpan.FromMilliseconds(260);
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var (translate, scale) = Transforms(orb);
            if (translate != null)
            {
                translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(translate.X, -65, duration) { EasingFunction = ease });
                translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(translate.Y, -12, duration) { EasingFunction = ease });
            }
            if (scale != null)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale.ScaleX, 0.1, duration) { EasingFunction = ease });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale.ScaleY, 0.1, duration) { EasingFunction = ease });
            }
            var fade = new DoubleAnimation(orb.Opacity, 0, duration) { EasingFunction = ease };
            fade.Completed += (_, _) => FinishHide(orb, state, generation);
            orb.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private static void FinishHide(Border orb, State state, int generation)
        {
            if (generation != state.Generation || state.Showing) return;
            state.Hiding = false;
            ResetAnimations(orb);
            orb.Visibility = Visibility.Collapsed;
            orb.Opacity = 0;
        }

        private static (TranslateTransform?, ScaleTransform?) Transforms(Border orb)
            => orb.RenderTransform is TransformGroup group
                ? (group.Children.OfType<TranslateTransform>().FirstOrDefault(), group.Children.OfType<ScaleTransform>().FirstOrDefault())
                : (null, null);

        private static void ResetAnimations(Border orb)
        {
            AnimationReset.Opacity(orb);
            var (translate, scale) = Transforms(orb);
            AnimationReset.Translate(translate);
            AnimationReset.Scale(scale);
            if (translate != null) { translate.X = -148; translate.Y = 0; }
            if (scale != null) { scale.ScaleX = 1; scale.ScaleY = 1; }
        }
    }
}
