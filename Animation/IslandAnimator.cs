using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using ShapeRect = System.Windows.Shapes.Rectangle;

namespace alpha_island.Animation
{
    /// <summary>
    /// 形态切换动画器：把岛体从一套几何弹簧式地推到另一套几何，并在途中驱动材质与光学层。
    ///
    /// 为什么不用 DoubleAnimation + BackEase：
    ///   * BackEase 全程只穿越目标 1 次、峰后单调收敛（实测 Amplitude 0.32 → 过冲 5.82%），
    ///     幅度或许够，但曲线形状不对：宽高同时刻同相位到达过冲点，看起来是"整块板子弹了一下"；
    ///   * ElasticEase 要做出"小幅过冲"得把 Springiness 推到 10~20，那时余振几乎看不见，
    ///     最后还是退化成 BackEase；
    ///   * Windows.UI.Composition 的 ScalarSpringAnimation 在 WPF 上不可用
    ///     （new Compositor() 抛 UnauthorizedAccessException），所以只能自己逐帧积分。
    ///
    /// 做法（两条弹簧 + 一条单调光学相位，参数来自凝胶形变研究）：
    ///   1. <b>主通道</b> <see cref="_main"/>（ζ=0.6364 → 过冲≈7.5%）驱动 宽度 / 顶部位移 / 圆角；
    ///   2. <b>鼓胀通道</b> <see cref="_bulge"/>（ζ=0.556 → 过冲≈12%，且延迟 25ms 起步）
    ///      只驱动 高度。两条通道的过冲量和相位都不同，轮廓因此**各向异性**地鼓出来再收回 ——
    ///      这才是"胶体受外力冲击"的观感，而不是等比放大；
    ///   3. 用主通道的**速度**再叠一层挤压量 squash（低通滤波防抖），
    ///      映射成 ScaleX = 1+squash / ScaleY = 1-squash*0.6，体积近似守恒；
    ///   4. 光学：阴影随形变量呼吸（BlurRadius/ShadowDepth/Opacity）、顶边镜面高光条随形变
    ///      加长并沿顶边滑行、折射流光按**单调** CubicEaseOut 相位扫过（相位不能用弹簧，
    ///      否则光会来回抖）。
    ///
    /// 弹簧参数由标称时长反推：ω = 5/(ζ·T)，k = ω²，c = 2ζω。
    /// 系数取 5 而不是物理意义上的 4：4/(ζω) 只是"残差降到 ~1.8%"的时间，
    /// 真正的物理静止（位移 &lt; 5e-4）要到 ~2.1T，标称 460ms 的形变会被拖成 975ms，
    /// 三段式收回直接变成 2.9s。用 5 之后标称时长才和"看起来停下来了"对得上。
    /// </summary>
    public sealed class IslandAnimator
    {
        /// <summary>主通道阻尼比：过冲 ≈ 7.5%（研究实测 6.87%）。</summary>
        private const double MainZeta = 0.6364;
        /// <summary>鼓胀通道阻尼比：过冲 ≈ 12%，比主通道更"软"。</summary>
        private const double BulgeZeta = 0.556;
        /// <summary>高度通道延迟起步，制造各向异性形变。</summary>
        private const double BulgeDelaySeconds = 0.025;

        /// <summary>速度 → 挤压量的增益。弹簧峰值速度约 10/s，乘 0.0045 得 0.045 的挤压。</summary>
        private const double SquashGain = 0.0045;
        private const double MaxSquash = 0.045;
        /// <summary>挤压量低通时间常数，避免逐帧速度噪声直接抖到 Scale 上。</summary>
        private const double SquashSmooth = 0.030;
        /// <summary>各向异性偏差的归一化上限（h-t 到这个值就算"形变最大"）。</summary>
        private const double WarpFull = 0.12;

        private const double RestShadowBlur = 26.0;
        private const double RestShadowDepth = 7.0;
        private const double RestShadowOpacity = 0.55;
        private const double SheenSweep = 120.0;
        private const double SpecularBaseWidth = 90.0;
        private const double SpecularExtraWidth = 70.0;

        private readonly Border _body;
        private readonly TranslateTransform _translate;
        private readonly ScaleTransform _squashScale;
        private readonly TranslateTransform? _sheen;
        private readonly DropShadowEffect? _shadow;
        private readonly ShapeRect? _specular;
        private readonly TranslateTransform? _specularTranslate;

        private readonly GelSpring _main = new();
        private readonly GelSpring _bulge = new();

        /// <summary>
        /// 诊断用逐帧轨迹。设了环境变量 ALPHA_ISLAND_TRACE=&lt;文件路径&gt; 时，每一帧写一行
        /// "elapsedMs,t,h,width,height"。像素级验证凝胶形变（两条通道的过冲量与相位差）时用，
        /// 不设变量就完全不产生 I/O。关掉程序后直接读该文件即可。
        /// </summary>
        private static readonly System.IO.StreamWriter? Trace = OpenTrace();

        private static System.IO.StreamWriter? OpenTrace()
        {
            string? path = Environment.GetEnvironmentVariable("ALPHA_ISLAND_TRACE");
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                return new System.IO.StreamWriter(path, false) { AutoFlush = true };
            }
            catch
            {
                return null;
            }
        }

        private IslandGeometry _from;
        private IslandGeometry _to;
        private double _squash;
        private double _elapsed;
        private double _duration = 0.4;
        private IEasingFunction? _fallbackEase;
        private bool _useGel;
        private bool _running;
        private double _lastTick = -1;
        private Action? _completed;

        public IslandAnimator(Border body, TranslateTransform translate, ScaleTransform squashScale,
            TranslateTransform? sheen = null, DropShadowEffect? shadow = null,
            ShapeRect? specular = null, TranslateTransform? specularTranslate = null)
        {
            _body = body ?? throw new ArgumentNullException(nameof(body));
            _translate = translate ?? throw new ArgumentNullException(nameof(translate));
            _squashScale = squashScale ?? throw new ArgumentNullException(nameof(squashScale));
            _sheen = sheen;
            _shadow = shadow;
            _specular = specular;
            _specularTranslate = specularTranslate;
            _from = CurrentGeometry();
            _to = _from;
        }

        public bool IsRunning => _running;

        /// <summary>读回当前实际几何（含动画中途的中间值）。</summary>
        public IslandGeometry CurrentGeometry()
        {
            double width = _body.Width;
            if (double.IsNaN(width) || width <= 0) width = _body.ActualWidth > 0 ? _body.ActualWidth : 130;
            double height = _body.Height;
            if (double.IsNaN(height) || height <= 0) height = _body.ActualHeight > 0 ? _body.ActualHeight : 10;
            return new IslandGeometry(width, height, _translate.Y, _body.CornerRadius);
        }

        /// <summary>不走动画，直接落到目标几何（初始化 / 关闭形变效果时用）。</summary>
        public void SetImmediate(IslandGeometry geometry)
        {
            Stop();
            _from = geometry;
            _to = geometry;
            _squash = 0;
            ApplyProgress(1.0, 1.0, 1.0);
        }

        /// <summary>
        /// 形变到目标几何。
        /// </summary>
        /// <param name="to">目标几何。</param>
        /// <param name="gel">true = 弹簧凝胶形变；false = 传统缓动（用于"设置里关掉动效"）。</param>
        /// <param name="duration">标称时长，弹簧模式下用来反推劲度。</param>
        /// <param name="ease">缓动模式使用的缓动函数。</param>
        /// <param name="completed">完成回调（弹簧模式为'基本静止'时触发）。</param>
        public void MorphTo(IslandGeometry to, bool gel, Duration duration, IEasingFunction? ease = null,
            Action? completed = null)
        {
            Stop();

            _from = CurrentGeometry();
            _to = to;
            _useGel = gel;
            _completed = completed;
            _elapsed = 0;
            _squash = 0;
            _lastTick = -1;

            _duration = duration.HasTimeSpan ? Math.Max(0.08, duration.TimeSpan.TotalSeconds) : 0.4;
            _fallbackEase = ease;

            if (gel)
            {
                double omega = 5.0 / (MainZeta * _duration);
                _main.Stiffness = omega * omega;
                _main.Damping = 2.0 * MainZeta * omega;
                _main.Mass = 1.0;
                _main.Reset(0, 0);
                _main.Target = 1;

                _bulge.Stiffness = omega * omega;
                _bulge.Damping = 2.0 * BulgeZeta * omega;
                _bulge.Mass = 1.0;
                _bulge.Reset(0, 0);
                _bulge.Target = 1;
            }

            _running = true;
            CompositionTarget.Rendering += OnRendering;
        }

        /// <summary>中途打断（用户又动鼠标了）。会停在被调用的那一刻，不跳到终点。</summary>
        public void Stop()
        {
            if (_running)
            {
                CompositionTarget.Rendering -= OnRendering;
                _running = false;
            }
            _lastTick = -1;
            _completed = null;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            double now = (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
            double dt = _lastTick < 0 ? 1.0 / 60.0 : now - _lastTick;
            _lastTick = now;
            if (dt <= 0) dt = 1.0 / 60.0;
            if (dt > 0.1) dt = 0.1;

            double t;
            double h;
            double phase;
            bool done;

            if (_useGel)
            {
                _elapsed += dt;

                // 注意：不能用 GelSpring 自己的"物理静止"判据（位移 < 5e-4）来决定 done。
                // 那个阈值比人眼能察觉的严得多：标称 460ms 的形变要跑到 ~975ms 才满足它，
                // 而 CollapseToBar() 是靠 completed 回调串起三段的，结果三段式收回被拖成 ~2.9s。
                // 所以完成判据是"至少跑满标称时长 + 两条通道都进 ±1.2%"。
                bool mainDone = !_main.Step(dt);
                t = _main.Value;

                bool bulgeDone;
                if (_elapsed < BulgeDelaySeconds)
                {
                    h = 0.0;
                    bulgeDone = false;
                }
                else
                {
                    bulgeDone = !_bulge.Step(dt);
                    h = _bulge.Value;
                }

                done = (_elapsed >= _duration
                        && Math.Abs(t - 1.0) < 0.012
                        && Math.Abs(h - 1.0) < 0.012)
                       || (mainDone && bulgeDone)
                       || _elapsed >= _duration * 2.5;

                // 挤压量由主通道速度派生：展开时横向拉伸纵向压扁，回弹时反向。
                double raw = Math.Clamp(_main.Velocity * SquashGain, -MaxSquash, MaxSquash);
                _squash += (raw - _squash) * (1.0 - Math.Exp(-dt / SquashSmooth));

                // 光学相位必须单调，否则流光会来回抖。
                phase = EaseOutCubic(_elapsed / _duration);
            }
            else
            {
                _elapsed += dt;
                t = Math.Clamp(_elapsed / _duration, 0.0, 1.0);
                if (_fallbackEase != null) t = _fallbackEase.Ease(t);
                h = t;
                phase = t;
                done = _elapsed / _duration >= 1.0;
                _squash += (0 - _squash) * (1.0 - Math.Exp(-dt / SquashSmooth));
            }

            // t / h 允许超过 1（过冲），不夹紧 —— 这正是"小幅过冲挤压"的来源。
            ApplyProgress(t, h, phase);

            if (done)
            {
                var cb = _completed;
                Stop();
                _squash = 0;
                ApplyProgress(1.0, 1.0, 1.0);
                cb?.Invoke();
            }
        }

        /// <summary>
        /// 按两条通道的进度把几何与光学层推到该有的样子。
        /// </summary>
        /// <param name="t">主通道进度（1 = 到达目标，可过冲）。</param>
        /// <param name="h">高度通道进度（可过冲，通常滞后于 t）。</param>
        /// <param name="phase">光学相位，0→1 单调。</param>
        private void ApplyProgress(double t, double h, double phase)
        {
            var lerped = IslandGeometry.Lerp(_from, _to, t);
            var g = new IslandGeometry(
                lerped.Width,
                _from.Height + (_to.Height - _from.Height) * h,
                lerped.Top,
                lerped.Corner);

            _body.Width = Math.Max(1, g.Width);
            _body.Height = Math.Max(1, g.Height);
            _body.CornerRadius = g.Corner;

            if (Math.Abs(_translate.Y - g.Top) > 0.01) _translate.Y = g.Top;

            _squashScale.ScaleX = 1.0 + _squash;
            _squashScale.ScaleY = 1.0 - _squash * 0.6;

            if (Trace != null && _running && _useGel)
            {
                Trace.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:F1},{1:F4},{2:F4},{3:F1},{4:F1},{5:F0},{6:F0},{7:F0},{8:F0}",
                    _elapsed * 1000.0, t, h, g.Width, g.Height,
                    _from.Width, _to.Width, _from.Height, _to.Height));
            }

            // 各向异性偏差：高度通道相对主通道的领先/滞后量，0..1。
            double warp = Math.Clamp(Math.Abs(h - t) / WarpFull, 0.0, 1.0);
            double swell = Math.Min(1.0, Math.Max(Math.Abs(t - 1.0), Math.Abs(h - 1.0)) / WarpFull);

            if (_shadow != null)
            {
                _shadow.BlurRadius = RestShadowBlur + swell * 14.0 + warp * 4.0;
                _shadow.ShadowDepth = RestShadowDepth + swell * 8.0;
                _shadow.Opacity = RestShadowOpacity + swell * 0.22;
            }

            if (_sheen != null)
            {
                // 折射流光按单调相位横向扫过：形变过程中光带从左侧扫到右侧，不来回抖。
                _sheen.X = (-0.5 + phase) * SheenSweep;
            }

            if (_specular != null)
            {
                _specular.Width = SpecularBaseWidth + swell * SpecularExtraWidth;
                _specular.Opacity = Math.Min(0.9, swell * 0.85 + warp * 0.25);
            }

            if (_specularTranslate != null && _body.ActualWidth > 1)
            {
                _specularTranslate.X = (-0.5 + phase) * _body.ActualWidth * 0.55;
            }
        }

        private static double EaseOutCubic(double x)
        {
            if (x <= 0) return 0;
            if (x >= 1) return 1;
            double inv = 1.0 - x;
            return 1.0 - inv * inv * inv;
        }
    }
}
