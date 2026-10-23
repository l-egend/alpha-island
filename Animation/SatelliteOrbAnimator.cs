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
            public double? TargetX;
            public LiquidMotionKind Kind;
            public Action? Returned;
        }

        private static readonly ConditionalWeakTable<Border, State> States = new();
        public static event Action<Border, TimeSpan>? MotionStarted;
        public static event Action<Border>? MotionFinished;

        /// <returns>是否需要重新播放入场动画。</returns>
        public static bool Show(Border orb)
        {
            var state = States.GetValue(orb, _ => new State());
            if (state.Showing && orb.Visibility == Visibility.Visible && orb.Opacity > 0.99)
                return false;

            state.Generation++;
            state.Showing = true;
            state.Hiding = false;
            state.TargetX = null; state.Returned = null;
            ResetAnimations(orb);
            MotionFinished?.Invoke(orb);
            orb.Visibility = Visibility.Visible;
            orb.Opacity = 1;
            orb.IsHitTestVisible = true;
            return true;
        }

        public static LiquidMotionKind Kind(Border orb) => States.GetValue(orb, _ => new State()).Kind;

        /// <summary>Retain an in-flight destination: the rail's next layout must not replace an emergence.</summary>
        public static void SlideOut(Border orb, double startX, double targetX)
        {
            var state = States.GetValue(orb, _ => new State());
            int generation = ++state.Generation;
            state.TargetX = targetX; state.Kind = LiquidMotionKind.Split;
            var (translate, scale) = Transforms(orb);
            if (translate == null) return;
            AnimationReset.Translate(translate); AnimationReset.Scale(scale);
            translate.X = startX; translate.Y = 0;
            if (scale != null) scale.ScaleX = scale.ScaleY = 1;
            if (MotionPolicy.InstantMorph || MotionPolicy.PowerSave)
            { translate.X = targetX; state.TargetX = null; MotionFinished?.Invoke(orb); return; }
            if (orb.Child is UIElement content)
            {
                var reveal = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(105));
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(content, UIElement.OpacityProperty, reveal);
            }
            var duration = LiquidCellMotion.SplitDuration;
            var move = CreateSplitMove(startX, targetX);
            move.Completed += (_, _) =>
            {
                if (state.Generation != generation) return;
                state.TargetX = null; MotionFinished?.Invoke(orb);
            };
            MotionStarted?.Invoke(orb, duration);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(translate, TranslateTransform.XProperty, move);
        }

        internal static DoubleAnimationUsingKeyFrames CreateSplitMove(double startX, double targetX)
        {
            double travel = targetX - startX;
            double middle = startX + travel * .43;
            double overshoot = targetX + Math.Sign(travel) * 2.4;
            // Match the derivatives on both sides of the budding shoulder. A zero-velocity knot
            // followed by a fast spline feels like a pause even at a stable frame rate.
            double incoming = travel * .43 / .29;
            double outgoing = (overshoot - middle) / .47;
            double tangent = Math.Abs(outgoing) < .001 ? .2 : Math.Clamp(.2 * incoming / outgoing, 0, 1);
            var move = new DoubleAnimationUsingKeyFrames { Duration = LiquidCellMotion.SplitDuration };
            move.KeyFrames.Add(new DiscreteDoubleKeyFrame(startX, KeyTime.FromPercent(0)));
            move.KeyFrames.Add(new SplineDoubleKeyFrame(middle, KeyTime.FromPercent(.29), new KeySpline(.32, 0, .65, .65)));
            move.KeyFrames.Add(new SplineDoubleKeyFrame(overshoot, KeyTime.FromPercent(.76), new KeySpline(.2, tangent, .35, 1)));
            move.KeyFrames.Add(new SplineDoubleKeyFrame(targetX, KeyTime.FromPercent(1), new KeySpline(.25, 0, .25, 1)));
            return move;
        }
        public static void TranslateTo(Border orb, double targetX)
        {
            var state = States.GetValue(orb, _ => new State());
            var (translate, _) = Transforms(orb);
            if (translate == null || state.TargetX is double target && Math.Abs(target - targetX) < .5) return;
            if (Math.Abs(translate.X - targetX) < .5) return;
            int generation = ++state.Generation;
            state.TargetX = targetX; state.Kind = LiquidMotionKind.Reflow;
            double from = translate.X;
            AnimationReset.Translate(translate); translate.X = targetX;
            if (MotionPolicy.InstantMorph || MotionPolicy.PowerSave) { state.TargetX = null; MotionFinished?.Invoke(orb); return; }
            var move = new DoubleAnimation(from, targetX, LiquidCellMotion.ReflowDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            move.Completed += (_, _) =>
            {
                if (state.Generation != generation) return;
                state.TargetX = null; MotionFinished?.Invoke(orb);
            };
            MotionStarted?.Invoke(orb, LiquidCellMotion.ReflowDuration);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(translate, TranslateTransform.XProperty, move);
        }

        public static void Return(Border orb, double targetX, bool immediate = false, Action? completed = null)
        {
            var state = States.GetValue(orb, _ => new State());
            if (orb.Visibility != Visibility.Visible && !state.Showing && !state.Hiding) return;
            if (state.Hiding && !immediate) return;
            int generation = ++state.Generation;
            state.Showing = false; state.Hiding = !immediate; state.TargetX = targetX; state.Kind = LiquidMotionKind.Merge;
            state.Returned = completed;
            orb.IsHitTestVisible = false;
            if (immediate || MotionPolicy.InstantMorph || MotionPolicy.PowerSave)
            { FinishHide(orb, state, generation); return; }
            var (translate, scale) = Transforms(orb);
            var duration = LiquidCellMotion.MergeDuration;
            if (translate != null)
            {
                var move = new DoubleAnimation(translate.X, targetX, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(translate, TranslateTransform.XProperty, move);
            }
            if (scale != null)
            {
                // Only the contour strains at contact; the icon stays readable until it is engulfed.
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(scale, ScaleTransform.ScaleXProperty, new DoubleAnimation(scale.ScaleX, .82, duration));
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(scale, ScaleTransform.ScaleYProperty, new DoubleAnimation(scale.ScaleY, .82, duration));
            }
            var fade = new DoubleAnimationUsingKeyFrames { Duration = duration };
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(orb.Opacity, KeyTime.FromPercent(0)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(orb.Opacity, KeyTime.FromPercent(.56)));
            fade.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromPercent(1), new KeySpline(.4, 0, .7, 1)));
            fade.Completed += (_, _) =>
            {
                if (state.Generation != generation || state.Showing) return;
                FinishHide(orb, state, generation);
            };
            MotionStarted?.Invoke(orb, duration);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(orb, UIElement.OpacityProperty, fade);
        }
        public static void Hide(Border orb, bool immediate = false)
        {
            var state = States.GetValue(orb, _ => new State());
            if (orb.Visibility != Visibility.Visible && !state.Showing && !state.Hiding) return;
            if (state.Hiding && !immediate) return; // 同步刷新不能反复从头播放淡出。

            int generation = ++state.Generation;
            state.Showing = false;
            state.Kind = LiquidMotionKind.Merge; state.TargetX = null;
            state.Hiding = !immediate;
            orb.IsHitTestVisible = false;
            if (immediate) { FinishHide(orb, state, generation); return; }

            var fade = new DoubleAnimation(orb.Opacity, 0, TimeSpan.FromMilliseconds(200));
            MotionStarted?.Invoke(orb, TimeSpan.FromMilliseconds(200));
            fade.Completed += (_, _) => FinishHide(orb, state, generation);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(orb,UIElement.OpacityProperty, fade);
        }

        public static void Collapse(Border orb, bool immediate = false)
        {
            var state = States.GetValue(orb, _ => new State());
            if (orb.Visibility != Visibility.Visible) return;
            int generation = ++state.Generation;
            state.Showing = false;
            state.Kind = LiquidMotionKind.Merge; state.TargetX = null;
            state.Hiding = !immediate;
            orb.IsHitTestVisible = false;
            if (immediate) { FinishHide(orb, state, generation); return; }

            var duration = TimeSpan.FromMilliseconds(260);
            MotionStarted?.Invoke(orb, duration);
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var (translate, scale) = Transforms(orb);
            if (translate != null)
            {
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(translate,TranslateTransform.XProperty, new DoubleAnimation(translate.X, -65, duration) { EasingFunction = ease });
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(translate,TranslateTransform.YProperty, new DoubleAnimation(translate.Y, -12, duration) { EasingFunction = ease });
            }
            if (scale != null)
            {
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(scale,ScaleTransform.ScaleXProperty, new DoubleAnimation(scale.ScaleX, 0.1, duration) { EasingFunction = ease });
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(scale,ScaleTransform.ScaleYProperty, new DoubleAnimation(scale.ScaleY, 0.1, duration) { EasingFunction = ease });
            }
            var fade = new DoubleAnimation(orb.Opacity, 0, duration) { EasingFunction = ease };
            fade.Completed += (_, _) => FinishHide(orb, state, generation);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(orb,UIElement.OpacityProperty, fade);
        }

        public static void CompleteMotion(Border orb)
        {
            if (!States.TryGetValue(orb, out var state)) return;
            if (state.Hiding) { FinishHide(orb, state, state.Generation); return; }
            var (translate, _) = Transforms(orb);
            double x = state.TargetX ?? translate?.X ?? -148, y = translate?.Y ?? 0, opacity = orb.Opacity;
            state.Generation++; state.TargetX = null;
            ResetAnimations(orb);
            if (translate != null) { translate.X = x; translate.Y = y; }
            orb.Opacity = opacity;
            MotionFinished?.Invoke(orb);
        }
        private static void FinishHide(Border orb, State state, int generation)
        {
            if (generation != state.Generation || state.Showing) return;
            state.Hiding = false; state.TargetX = null;
            var returned = state.Returned; state.Returned = null;
            ResetAnimations(orb);
            orb.Visibility = Visibility.Collapsed;
            orb.Opacity = 0;
            MotionFinished?.Invoke(orb);
            returned?.Invoke();
        }

        private static (TranslateTransform?, ScaleTransform?) Transforms(Border orb)
            => orb.RenderTransform is TransformGroup group
                ? (group.Children.OfType<TranslateTransform>().FirstOrDefault(), group.Children.OfType<ScaleTransform>().FirstOrDefault())
                : (null, null);

        private static void ResetAnimations(Border orb)
        {
            AnimationReset.Opacity(orb);
            if (orb.Child is UIElement content) { AnimationReset.Opacity(content); content.Opacity = 1; }
            var (translate, scale) = Transforms(orb);
            AnimationReset.Translate(translate);
            AnimationReset.Scale(scale);
            if (translate != null) { translate.X = -148; translate.Y = 0; }
            if (scale != null) { scale.ScaleX = 1; scale.ScaleY = 1; }
        }
    }
}
