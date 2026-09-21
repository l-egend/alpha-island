using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicIslandDemo
{
    public partial class MainWindow : Window
    {
        private bool _isExpanded = false;
        private static Mutex? _mutex;

        // 几何参数配置
        private const double BaseWidth = 130;
        private const double BaseHeight = 10;
        private const double ExpandedWidth = 320;
        private const double ExpandedHeight = 60;
        private const double CircleSize = 46; // 聚拢成小圆球的直径
        private const double OrbExpandedOffsetX = -131; // Siri 球展开在左侧时的中心偏移量

        public MainWindow()
        {
            _mutex = new Mutex(true, "DynamicIslandDemo_SingleInstance_Mutex", out bool isNewInstance);
            if (!isNewInstance)
            {
                Application.Current.Shutdown();
                return;
            }

            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            this.Left = (SystemParameters.PrimaryScreenWidth - this.Width) / 2;
            this.Top = 0;
        }

        private void IslandBody_MouseEnter(object sender, MouseEventArgs e)
        {
            if (!_isExpanded)
            {
                ExpandIsland();
            }
        }

        private void IslandBody_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_isExpanded)
            {
                CollapseIsland();
            }
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            if (_isExpanded)
            {
                CollapseIsland();
            }
        }

        /// <summary>
        /// 展开动画：圆润舒展与 Siri 球向左滑移
        /// </summary>
        private void ExpandIsland()
        {
            _isExpanded = true;
            this.Activate();

            var duration = TimeSpan.FromMilliseconds(360);
            var bounceEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.32 };
            var smoothEase = new CubicEase { EasingMode = EasingMode.EaseOut };

            // 岛体尺寸与下落
            IslandBody.BeginAnimation(WidthProperty, new DoubleAnimation(ExpandedWidth, duration) { EasingFunction = bounceEase });
            IslandBody.BeginAnimation(HeightProperty, new DoubleAnimation(ExpandedHeight, duration) { EasingFunction = bounceEase });
            IslandTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, duration) { EasingFunction = bounceEase });

            IslandBody.BeginAnimation(Border.CornerRadiusProperty,
                new CornerRadiusAnimation(new CornerRadius(ExpandedHeight / 2), duration) { EasingFunction = smoothEase });

            // Siri 图标淡入并弹向左侧
            SiriOrb.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
            SiriOrbTranslate.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(OrbExpandedOffsetX, duration) { EasingFunction = bounceEase });

            // 文字内容稍后淡入
            ExpandedContent.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220))
            {
                BeginTime = TimeSpan.FromMilliseconds(100)
            });
        }

        /// <summary>
        /// 三段式平滑收起：坍缩聚球 -> 向上吸顶 -> 舒展为待机条
        /// </summary>
        private void CollapseIsland()
        {
            if (!_isExpanded) return;
            _isExpanded = false;

            // ==================== 阶段 1：向中心坍缩成圆球，Siri 图标移至球心 ====================
            var stage1Duration = TimeSpan.FromMilliseconds(260);
            var stage1Ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

            // 文字迅速淡出
            ExpandedContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(100)));

            // Siri 图标平滑滑向正中心 (X = 0)，作为这个球体的心脏
            SiriOrbTranslate.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, stage1Duration) { EasingFunction = stage1Ease });

            // 宽高与圆角同步聚拢成正圆
            var widthToCircle = new DoubleAnimation(CircleSize, stage1Duration) { EasingFunction = stage1Ease };
            var heightToCircle = new DoubleAnimation(CircleSize, stage1Duration) { EasingFunction = stage1Ease };
            var dropToCircle = new DoubleAnimation(18, stage1Duration) { EasingFunction = stage1Ease };
            var cornerToCircle = new CornerRadiusAnimation(new CornerRadius(CircleSize / 2), stage1Duration) { EasingFunction = stage1Ease };

            // 阶段 1 完成后，触发阶段 2
            cornerToCircle.Completed += (s, e) =>
            {
                if (_isExpanded) return; // 若中途重新唤出则中断

                // ==================== 阶段 2：小球先纯位移向上贴紧屏幕顶边 ====================
                var stage2Duration = TimeSpan.FromMilliseconds(220);
                var stage2Ease = new CubicEase { EasingMode = EasingMode.EaseIn };

                // 向上吸附归零
                var flyUpAnim = new DoubleAnimation(0, stage2Duration) { EasingFunction = stage2Ease };

                // Siri 光球随吸附过程平滑淡出
                SiriOrb.BeginAnimation(OpacityProperty, new DoubleAnimation(0, stage2Duration) { EasingFunction = stage2Ease });

                // 阶段 2 吸顶完成后，触发阶段 3
                flyUpAnim.Completed += (s2, e2) =>
                {
                    if (_isExpanded) return;

                    // ==================== 阶段 3：贴顶后，从圆球逐渐横向拉伸变回待机扁长条 ====================
                    var stage3Duration = TimeSpan.FromMilliseconds(220);
                    var stage3Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

                    // 横向拉开宽度
                    IslandBody.BeginAnimation(WidthProperty, new DoubleAnimation(BaseWidth, stage3Duration) { EasingFunction = stage3Ease });
                    // 高度压平成细条
                    IslandBody.BeginAnimation(HeightProperty, new DoubleAnimation(BaseHeight, stage3Duration) { EasingFunction = stage3Ease });
                    // 圆角过渡回贴顶微直角
                    IslandBody.BeginAnimation(Border.CornerRadiusProperty,
                        new CornerRadiusAnimation(new CornerRadius(0, 0, 10, 10), stage3Duration) { EasingFunction = stage3Ease });
                };

                IslandTranslate.BeginAnimation(TranslateTransform.YProperty, flyUpAnim);
            };

            // 启动阶段 1
            IslandBody.BeginAnimation(WidthProperty, widthToCircle);
            IslandBody.BeginAnimation(HeightProperty, heightToCircle);
            IslandTranslate.BeginAnimation(TranslateTransform.YProperty, dropToCircle);
            IslandBody.BeginAnimation(Border.CornerRadiusProperty, cornerToCircle);
        }

        protected override void OnClosed(EventArgs e)
        {
            _mutex?.ReleaseMutex();
            base.OnClosed(e);
        }
    }

    /// <summary>
    /// CornerRadius 属性平滑插值类
    /// </summary>
    public class CornerRadiusAnimation : AnimationTimeline
    {
        public override Type TargetPropertyType => typeof(CornerRadius);
        public CornerRadius? From { get; set; }
        public CornerRadius To { get; set; }
        public IEasingFunction? EasingFunction { get; set; }

        public CornerRadiusAnimation(CornerRadius to, Duration duration)
        {
            To = to;
            Duration = duration;
        }

        protected override Freezable CreateInstanceCore() => new CornerRadiusAnimation(To, Duration);

        public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
        {
            var from = From ?? (CornerRadius)defaultOriginValue;
            double progress = animationClock.CurrentProgress.Value;

            if (EasingFunction != null)
                progress = EasingFunction.Ease(progress);

            return new CornerRadius(
                from.TopLeft + (To.TopLeft - from.TopLeft) * progress,
                from.TopRight + (To.TopRight - from.TopRight) * progress,
                from.BottomRight + (To.BottomRight - from.BottomRight) * progress,
                from.BottomLeft + (To.BottomLeft - from.BottomLeft) * progress
            );
        }
    }
}