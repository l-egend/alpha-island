using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace alpha_island.Animation
{
    /// <summary>
    /// 让 Border.CornerRadius 可以被动画驱动 —— WPF 没有内置的 CornerRadius 动画类型。
    ///
    /// 语义要点（照搬旧实现，别改）：<see cref="From"/> **可以为 null**，
    /// 这时用 WPF 传进来的 defaultOriginValue（即属性当前值）作为起点。
    /// 收回动画的第一段就是靠这个"从当前圆角开始收"，如果 From 默认成 (0,0,0,0)，
    /// 阶段 1 会先从方角变圆再缩，观感全错。
    /// </summary>
    public class CornerRadiusAnimation : AnimationTimeline
    {
        public override Type TargetPropertyType => typeof(CornerRadius);

        /// <summary>起点圆角。null = 使用属性当前值。</summary>
        public CornerRadius? From { get; set; }

        public CornerRadius To { get; set; }

        public IEasingFunction? EasingFunction { get; set; }

        public CornerRadiusAnimation(CornerRadius to, Duration duration)
        {
            To = to;
            Duration = duration;
        }

        public CornerRadiusAnimation(CornerRadius from, CornerRadius to, Duration duration, IEasingFunction? easing = null)
        {
            From = from;
            To = to;
            Duration = duration;
            EasingFunction = easing;
        }

        // 这几个属性是普通 CLR 属性（不是 DP），所以克隆时必须手动带上，
        // 否则 Freezable 的默认克隆只会复制 DP，缓动和起止值会丢失。
        protected override Freezable CreateInstanceCore() => new CornerRadiusAnimation(To, Duration)
        {
            From = From,
            EasingFunction = EasingFunction
        };

        public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue,
            AnimationClock animationClock)
        {
            CornerRadius from = From ?? (CornerRadius)defaultOriginValue;
            double progress = animationClock.CurrentProgress ?? 0.0;

            if (EasingFunction != null) progress = EasingFunction.Ease(progress);

            return new CornerRadius(
                from.TopLeft + (To.TopLeft - from.TopLeft) * progress,
                from.TopRight + (To.TopRight - from.TopRight) * progress,
                from.BottomRight + (To.BottomRight - from.BottomRight) * progress,
                from.BottomLeft + (To.BottomLeft - from.BottomLeft) * progress);
        }
    }
}
