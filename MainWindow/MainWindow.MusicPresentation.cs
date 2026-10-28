using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using alpha_island.Animation;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace alpha_island
{
    public partial class MainWindow
    {
        private double _musicProgressRatio;
        private bool _musicProgressHovered;

        private void ResetLyricPresentation()
        {
            foreach (var row in new FrameworkElement[] { LyricPrev, LyricCurrentHost, LyricTranslation, LyricNext })
            {
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(row,UIElement.OpacityProperty, null);
                row.Opacity = 1;
                if (row.RenderTransform is TranslateTransform translate)
                {
                    AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(translate,TranslateTransform.YProperty, null);
                    translate.Y = 0;
                }
                if (row.Effect is BlurEffect blur)
                    AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(blur,BlurEffect.RadiusProperty, null);
            }
            LyricCurrentHost.Effect = null;
            double radius = MotionPolicy.SoftwareRendering ? 0 : 2.2;
            if (LyricPrev.Effect is BlurEffect previous) previous.Radius = radius;
            if (LyricNext.Effect is BlurEffect next) next.Radius = radius;
        }

        private void AnimateLyricChange(bool hadLyrics)
        {
            ResetLyricPresentation();
            if (!hadLyrics || _state != Core.IslandState.Card || _pages.ActiveId != Core.IslandPageIds.Music
                || MotionPolicy.InstantMorph || MotionPolicy.PowerSave) return;

            // 每次换句只启动短动画，共享当前行的变换，逐字高亮层与底层始终重合。
            var duration = TimeSpan.FromMilliseconds(360);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            DoubleAnimation Tween(double from, double to) => new(from, to, duration)
            { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            foreach (var row in new FrameworkElement[] { LyricPrev, LyricCurrentHost, LyricTranslation, LyricNext })
            {
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(((TranslateTransform)row.RenderTransform),TranslateTransform.YProperty, Tween(9, 0));
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(row,UIElement.OpacityProperty, Tween(.65, 1));
                if (row.Effect is BlurEffect blur)
                    AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(blur,BlurEffect.RadiusProperty, Tween(4, blur.Radius));
            }

            var focusBlur = new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
            LyricCurrentHost.Effect = focusBlur;
            var focusAnimation = Tween(3.2, 0);
            focusAnimation.Completed += (_, _) =>
            {
                // 快速跳曲/跳句时，旧动画不得清掉新一轮效果。
                if (ReferenceEquals(LyricCurrentHost.Effect, focusBlur)) LyricCurrentHost.Effect = null;
            };
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(focusBlur,BlurEffect.RadiusProperty, focusAnimation);
        }

        private void SetMusicProgress(double ratio)
        {
            _musicProgressRatio = double.IsFinite(ratio) ? Math.Clamp(ratio, 0, 1) : 0;
            ProgressFill.Width = ProgressCapsule.ActualWidth * _musicProgressRatio;
        }

        private void ProgressCapsule_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 只裁切整条轨道的外轮廓，填充段末端保持直线；100% 时自然呈现完整圆角。
            double height = ProgressCapsule.ActualHeight;
            ProgressCapsule.Clip = new RectangleGeometry(new Rect(0, 0, ProgressCapsule.ActualWidth, height), height / 2, height / 2);
            SetMusicProgress(_musicProgressRatio);
        }

        private void SetMusicProgressHovered(bool hovered)
        {
            _musicProgressHovered = hovered;
            AnimateTrackThickness(ProgressCapsule, hovered);
        }

        private static void AnimateTrackThickness(FrameworkElement capsule, bool hovered)
        {
            double height = hovered ? 12 : 5;
            double from = capsule.ActualHeight;
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(capsule,FrameworkElement.HeightProperty, null);
            capsule.Height = height;
            if (MotionPolicy.InstantMorph || MotionPolicy.PowerSave || from <= 0) return;
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(capsule,FrameworkElement.HeightProperty,
                new DoubleAnimation(from, height, TimeSpan.FromMilliseconds(150))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }

        private void ProgressTrack_MouseEnter(object sender, MouseEventArgs e) => SetMusicProgressHovered(true);
        private void ProgressTrack_MouseLeave(object sender, MouseEventArgs e) => SetMusicProgressHovered(false);
    }
}
