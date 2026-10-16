using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
// ImplicitUsings 通过 UseWindowsForms 全局引入了 System.Drawing，Color/Point 会撞名
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace alpha_island.Controls
{
    /// <summary>
    /// 卡片态的「连续流体渐变波浪」。
    ///
    /// 这个类是照着 Copilot 那份 JSX 原型 1:1 复刻的 —— 原型用
    /// <c>&lt;canvas&gt;</c> + <c>ctx.filter = "blur(14px)"</c> 叠三层半透明渐变带：
    ///
    /// <code>
    /// layers = [
    ///   { color: rgba(255, 86,124,.66), base: 0.80, amp: 37, speed: 1.05, offset: 0.0 },
    ///   { color: rgba(139, 92,246,.58), base: 0.76, amp: 48, speed: 0.78, offset: 2.1 },
    ///   { color: rgba( 52,128,255,.48), base: 0.84, amp: 30, speed: 1.30, offset: 4.2 },
    /// ]
    /// centerLift = exp(-((nx - 0.52) / 0.26)^2)          // 中偏右的高斯重心
    /// wave       = sin(nx*7.2 + t*speed + offset)*0.48
    ///            + sin(nx*13.7 - t*0.62 + offset)*0.22
    /// peak       = centerLift * energy * amp * 1.55
    /// y          = h*base - wave*amp*energy - peak
    /// </code>
    ///
    /// WPF 里没有 canvas filter，等价物是给每个 <see cref="Path"/> 挂 <see cref="BlurEffect"/>
    /// （GPU 侧的高斯模糊）。所以这个控件从 <c>FrameworkElement</c> 改成 <c>Grid</c>：
    /// 它自己当容器，装三条互不遮挡、各自带模糊的填充路径。
    ///
    /// 颜色不写死：原型那四个色（暖橙红 / 洋红 / 紫罗兰 / 蓝）由
    /// <see cref="alpha_island.Services.CoverPalette.Ramp"/> 按封面主色相旋转生成，
    /// 满足"色彩取自专辑封面主色调"的要求。
    /// </summary>
    public sealed class WaveVisualizer : Grid
    {
        // ---------------- 原型常量（改这里就能整体调节观感） ----------------

        private const int LayerCount = 3;

        /// <summary>每层静止基线（占高度的比例）。原型 base: 0.80 / 0.76 / 0.84。</summary>
        private static readonly double[] LayerBase = { 0.80, 0.76, 0.84 };

        /// <summary>每层振幅系数（占高度的比例）。原型 amp: 37 / 48 / 30 px（画布高 520）。</summary>
        private static readonly double[] LayerAmp = { 37.0 / 520.0, 48.0 / 520.0, 30.0 / 520.0 };

        /// <summary>每层行波速度。原型 speed: 1.05 / 0.78 / 1.30。</summary>
        private static readonly double[] LayerSpeed = { 1.05, 0.78, 1.30 };

        /// <summary>每层相位偏移。原型 offset: 0 / 2.1 / 4.2。</summary>
        private static readonly double[] LayerOffset = { 0.0, 2.1, 4.2 };

        /// <summary>每层三点渐变的取色索引（对应四色阶 Ramp 的下标）。</summary>
        private static readonly int[][] GradIndex =
        {
            new[] { 0, 1, 2 },
            new[] { 2, 2, 2 },
            new[] { 3, 3, 3 }
        };

        /// <summary>每层三点渐变的 alpha。原型分别是 .66/.57/.42、.58/.58/.42、.48/.48/.58。</summary>
        private static readonly double[][] GradAlpha =
        {
            new[] { 0.66, 0.57, 0.42 },
            new[] { 0.58, 0.58, 0.42 },
            new[] { 0.48, 0.48, 0.58 }
        };

        private static readonly double[] GradStop = { 0.0, 0.52, 1.0 };

        /// <summary>渐变轴：原型 <c>createLinearGradient(0, h*0.3, w, h)</c>。</summary>
        private const double GradStartY = 0.30;

        /// <summary>中部隆起的高斯重心与宽度。原型 <c>exp(-((nx-0.52)/0.26)^2)</c>。</summary>
        private const double LiftCenter = 0.52;
        private const double LiftWidth = 0.26;

        /// <summary>原型 <c>phase += playing ? 0.022 : 0.007</c>（每帧 @60fps）。</summary>
        private const double PhaseIdle = 0.42;
        private const double PhaseActive = 0.95;

        // ---------------- 依赖属性 ----------------

        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(WaveVisualizer),
                new FrameworkPropertyMetadata(false, OnIsActiveChanged));

        /// <summary>横向采样点数（原型按 4px 步进，整卡约 98 点）。</summary>
        public static readonly DependencyProperty PointCountProperty =
            DependencyProperty.Register(nameof(PointCount), typeof(int), typeof(WaveVisualizer),
                new FrameworkPropertyMetadata(56, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>三层振幅的整体倍率。1.0 = 原型的原始比例。</summary>
        public static readonly DependencyProperty AmplitudeProperty =
            DependencyProperty.Register(nameof(Amplitude), typeof(double), typeof(WaveVisualizer),
                new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>保留字段（原型没有这条）；当前不影响绘制。</summary>
        public static readonly DependencyProperty MinAmplitudeProperty =
            DependencyProperty.Register(nameof(MinAmplitude), typeof(double), typeof(WaveVisualizer),
                new FrameworkPropertyMetadata(0.05, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>保留字段（原型是纯填充、无描边）。</summary>
        public static readonly DependencyProperty LineThicknessProperty =
            DependencyProperty.Register(nameof(LineThickness), typeof(double), typeof(WaveVisualizer),
                new FrameworkPropertyMetadata(1.3, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>三层 alpha 的整体倍率（原型 canvas 的 <c>opacity-95</c>）。</summary>
        public static readonly DependencyProperty FillOpacityProperty =
            DependencyProperty.Register(nameof(FillOpacity), typeof(double), typeof(WaveVisualizer),
                new FrameworkPropertyMetadata(1.0, OnBrushChanged));

        /// <summary>整个波浪层的透明度（原型 canvas 的 <c>opacity-95</c>）。</summary>
        public static readonly DependencyProperty GlowOpacityProperty =
            DependencyProperty.Register(nameof(GlowOpacity), typeof(double), typeof(WaveVisualizer),
                new FrameworkPropertyMetadata(0.95, OnBrushChanged));

        /// <summary>高斯模糊半径，单位设备无关像素。原型 <c>ctx.filter = "blur(14px)"</c>。</summary>
        public static readonly DependencyProperty GlowRadiusProperty =
            DependencyProperty.Register(nameof(GlowRadius), typeof(double), typeof(WaveVisualizer),
                new FrameworkPropertyMetadata(14.0, OnBrushChanged));

        public static readonly DependencyProperty ColorLowProperty =
            DependencyProperty.Register(nameof(ColorLow), typeof(Color), typeof(WaveVisualizer),
                new FrameworkPropertyMetadata(Color.FromRgb(0xFF, 0x7A, 0x59), OnBrushChanged));

        public static readonly DependencyProperty ColorMidProperty =
            DependencyProperty.Register(nameof(ColorMid), typeof(Color), typeof(WaveVisualizer),
                new FrameworkPropertyMetadata(Color.FromRgb(0xFF, 0x3D, 0x81), OnBrushChanged));

        public static readonly DependencyProperty ColorHighProperty =
            DependencyProperty.Register(nameof(ColorHigh), typeof(Color), typeof(WaveVisualizer),
                new FrameworkPropertyMetadata(Color.FromRgb(0x8B, 0x5C, 0xF6), OnBrushChanged));

        public static readonly DependencyProperty ColorExtraProperty =
            DependencyProperty.Register(nameof(ColorExtra), typeof(Color), typeof(WaveVisualizer),
                new FrameworkPropertyMetadata(Color.FromRgb(0x3B, 0x82, 0xF6), OnBrushChanged));

        /// <summary>保留字段（三层带子都是贴底填充，没有镜像轴）。</summary>
        public static readonly DependencyProperty MirroredProperty =
            DependencyProperty.Register(nameof(Mirrored), typeof(bool), typeof(WaveVisualizer),
                new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>能量包络的平滑时间常数（越大越懒）。</summary>
        public static readonly DependencyProperty SmoothingProperty =
            DependencyProperty.Register(nameof(Smoothing), typeof(double), typeof(WaveVisualizer),
                new FrameworkPropertyMetadata(0.35, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>重鼓点时中部隆起额外放大的幅度。</summary>
        public static readonly DependencyProperty BassBoostProperty =
            DependencyProperty.Register(nameof(BassBoost), typeof(double), typeof(WaveVisualizer),
                new FrameworkPropertyMetadata(0.55, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>
        /// 音频灵敏度：对送进来的频带做整体增益，1.0 = 原型原始响应。
        /// 调高会让波浪对音乐的反应更明显（代价是大声时容易饱和成一块）。
        /// </summary>
        public static readonly DependencyProperty SensitivityProperty =
            DependencyProperty.Register(nameof(Sensitivity), typeof(double), typeof(WaveVisualizer),
                new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender,
                    OnSensitivityChanged));

        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        public int PointCount
        {
            get => (int)GetValue(PointCountProperty);
            set => SetValue(PointCountProperty, value);
        }

        public double Amplitude
        {
            get => (double)GetValue(AmplitudeProperty);
            set => SetValue(AmplitudeProperty, value);
        }

        public double MinAmplitude
        {
            get => (double)GetValue(MinAmplitudeProperty);
            set => SetValue(MinAmplitudeProperty, value);
        }

        public double LineThickness
        {
            get => (double)GetValue(LineThicknessProperty);
            set => SetValue(LineThicknessProperty, value);
        }

        public double FillOpacity
        {
            get => (double)GetValue(FillOpacityProperty);
            set => SetValue(FillOpacityProperty, value);
        }

        public double GlowOpacity
        {
            get => (double)GetValue(GlowOpacityProperty);
            set => SetValue(GlowOpacityProperty, value);
        }

        public double GlowRadius
        {
            get => (double)GetValue(GlowRadiusProperty);
            set => SetValue(GlowRadiusProperty, value);
        }

        public Color ColorLow
        {
            get => (Color)GetValue(ColorLowProperty);
            set => SetValue(ColorLowProperty, value);
        }

        public Color ColorMid
        {
            get => (Color)GetValue(ColorMidProperty);
            set => SetValue(ColorMidProperty, value);
        }

        public Color ColorHigh
        {
            get => (Color)GetValue(ColorHighProperty);
            set => SetValue(ColorHighProperty, value);
        }

        public Color ColorExtra
        {
            get => (Color)GetValue(ColorExtraProperty);
            set => SetValue(ColorExtraProperty, value);
        }

        public bool Mirrored
        {
            get => (bool)GetValue(MirroredProperty);
            set => SetValue(MirroredProperty, value);
        }

        public double Smoothing
        {
            get => (double)GetValue(SmoothingProperty);
            set => SetValue(SmoothingProperty, value);
        }

        public double BassBoost
        {
            get => (double)GetValue(BassBoostProperty);
            set => SetValue(BassBoostProperty, value);
        }

        public double Sensitivity
        {
            get => (double)GetValue(SensitivityProperty);
            set => SetValue(SensitivityProperty, value);
        }

        private static void OnSensitivityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            // PushFrame 可能来自音频线程，DP 不适合跨线程读 —— 缓存成普通字段。
            var self = (WaveVisualizer)d;
            double v = (double)e.NewValue;
            self._sensitivity = (float)(double.IsFinite(v) ? Math.Clamp(v, 0.1, 6.0) : 1.0);
        }

        // ---------------- 内部状态 ----------------

        private readonly object _gate = new();
        private volatile float _sensitivity = 1f;
        private float[] _incoming = Array.Empty<float>();

        private readonly Path[] _paths = new Path[LayerCount];
        private readonly StreamGeometry[] _geoms = new StreamGeometry[LayerCount];
        private readonly LinearGradientBrush[] _brushes = new LinearGradientBrush[LayerCount];
        private readonly BlurEffect[] _blurs = new BlurEffect[LayerCount];

        private double _phase;
        private double _energy = 0.18;
        private double _energyTarget = 0.18;
        private double _punch;
        private double _punchTarget;

        private bool _rendering;
        private double _lastTick = -1;
        private double _lastW = -1;
        private double _lastH = -1;

        public WaveVisualizer()
        {
            ClipToBounds = false;
            SnapsToDevicePixels = false;
            UseLayoutRounding = false;
            IsHitTestVisible = false;
            Background = null;

            for (int i = 0; i < LayerCount; i++)
            {
                _geoms[i] = new StreamGeometry();
                _brushes[i] = new LinearGradientBrush
                {
                    MappingMode = BrushMappingMode.Absolute,
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop(Colors.Transparent, GradStop[0]),
                        new GradientStop(Colors.Transparent, GradStop[1]),
                        new GradientStop(Colors.Transparent, GradStop[2])
                    }
                };
                _blurs[i] = new BlurEffect
                {
                    Radius = 14.0,
                    KernelType = KernelType.Gaussian,
                    RenderingBias = RenderingBias.Performance
                };

                var p = new Path
                {
                    Data = _geoms[i],
                    Fill = _brushes[i],
                    Effect = _blurs[i],
                    Stretch = Stretch.None,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    VerticalAlignment = System.Windows.VerticalAlignment.Top,
                    IsHitTestVisible = false,
                    SnapsToDevicePixels = false
                };
                _paths[i] = p;
                Children.Add(p);
            }

            Loaded += (_, __) => { if (IsActive) StartRendering(); };
            Unloaded += (_, __) => StopRendering();
            IsVisibleChanged += (_, _) => { if (IsActive && IsVisible) StartRendering(); else StopRendering(); };
        }

        private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var v = (WaveVisualizer)d;
            if ((bool)e.NewValue) v.StartRendering();
            else v.StopRendering();
        }

        private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((WaveVisualizer)d).ApplyBrushes();
        }

        /// <summary>
        /// 不参与父级 Auto 行的尺寸计算 —— 波浪是纯背景层，大小永远由卡片决定。
        /// （三条 Path 的 DesiredSize 等于各自几何的包围盒，直接放行会把卡片撑变形。）
        /// </summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (UIElement child in InternalChildren)
                child.Measure(availableSize);
            return new Size(0.0, 0.0);
        }

        // ---------------- 输入 ----------------

        /// <summary>推入一帧频谱（取值 0..1，长度任意）。线程安全。</summary>
        public void PushFrame(float[] bands)
        {
            if (bands == null) return;
            PushFrame(bands.AsSpan());
        }

        internal void PushFrame(ReadOnlySpan<float> bands)
        {
            if (bands.IsEmpty) return;

            float sens = _sensitivity;

            float bass = 0f, all = 0f;
            int bassN = Math.Max(1, bands.Length / 6);
            for (int i = 0; i < bands.Length; i++)
            {
                float v = bands[i] * sens;
                if (v > 1f) v = 1f;
                all += v;
                if (i < bassN && v > bass) bass = v;
            }
            all /= bands.Length;
            if (bass < 0f) bass = 0f;
            if (bass > 1f) bass = 1f;

            lock (_gate)
            {
                if (_incoming.Length != bands.Length) _incoming = new float[bands.Length];
                for (int i = 0; i < bands.Length; i++)
                {
                    float v = bands[i] * sens;
                    _incoming[i] = v > 1f ? 1f : v;
                }

                // 原型 idle=0.18、播放中 0.48±0.18 上下浮动 —— 这里保留同样的"底线不归零"
                double e = Math.Clamp(all * 1.35, 0.0, 1.0);
                _energyTarget = 0.18 + 0.82 * e;
                _punchTarget = bass;
            }
        }

        /// <summary>停止播放时用：能量按原型的静默值收尾。</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _energyTarget = 0.18;
                _punchTarget = 0.0;
            }
        }

        // ---------------- 渲染循环 ----------------

        private void StartRendering()
        {
            if (_rendering || !IsLoaded || !IsVisible) return;
            _rendering = true;
            _lastTick = -1;
            CompositionTarget.Rendering += OnRendering;
        }

        private void StopRendering()
        {
            if (!_rendering) return;
            _rendering = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            double now = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
            double dt = _lastTick < 0 ? 1.0 / 60.0 : now - _lastTick;
            _lastTick = now;
            if (dt <= 0) dt = 1.0 / 60.0;
            if (dt > 0.12) dt = 0.12;

            Step(dt);
            Render();
        }

        private void Step(double dt)
        {
            double e, pt;
            lock (_gate) { e = _energyTarget; pt = _punchTarget; }

            // 原型：energy += (target - energy) * 0.045（每帧 @60fps）→ 时间常数约 0.36s
            double k = 1.0 - Math.Exp(-dt / (0.16 + Smoothing * 0.45));
            _energy += (e - _energy) * k;

            double pa = 1.0 - Math.Exp(-dt / 0.018);
            double pr = 1.0 - Math.Exp(-dt / (0.16 + Smoothing * 0.20));
            _punch += (pt - _punch) * (pt > _punch ? pa : pr);

            // 原型：phase += playing ? 0.022 : 0.007（每帧 @60fps）
            _phase += dt * (PhaseIdle + _energy * PhaseActive);
        }

        // ---------------- 绘制 ----------------

        private void Render()
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 2 || h <= 2) return;

            if (Math.Abs(w - _lastW) > 0.01 || Math.Abs(h - _lastH) > 0.01)
            {
                _lastW = w;
                _lastH = h;
                for (int i = 0; i < LayerCount; i++)
                {
                    // 显式钉住 Path 尺寸 = 整张卡片：否则 BlurEffect 会被"几何包围盒"裁掉，
                    // 浪尖上沿那圈高斯羽化就没了。
                    _paths[i].Width = w;
                    _paths[i].Height = h;
                }
                ApplyBrushes();
            }

            int n = Math.Max(2, PointCount);
            double ampScale = Amplitude * (h / 234.0);   // 原型画布高 520，这里按实际高度等比缩放
            double punchMul = 1.0 + BassBoost * _punch * 1.6;

            for (int L = 0; L < LayerCount; L++)
            {
                double baseY = h * LayerBase[L];
                double amp = h * LayerAmp[L] * ampScale;
                double speed = LayerSpeed[L];
                double off = LayerOffset[L];
                double dx = w / (n - 1);

                // StreamGeometry 不能先 Freeze 再 Open，Clear 必须在 Open 之前
                _geoms[L].Clear();
                using (var ctx = _geoms[L].Open())
                {
                    var pts = new Point[n];                    for (int i = 0; i < n; i++)
                    {
                        double nx = (double)i / (n - 1);

                        // 原型：centerLift = exp(-((nx - 0.52) / 0.26)^2)
                        double d = (nx - LiftCenter) / LiftWidth;
                        double centerLift = Math.Exp(-(d * d));

                        double wave = Math.Sin(nx * 7.2 + _phase * speed + off) * 0.48
                                    + Math.Sin(nx * 13.7 - _phase * 0.62 + off) * 0.22;

                        // 原型：peak = centerLift * energy * amp * 1.55；这里叠上低频包络（重鼓点中部优先隆起）
                        double peak = centerLift * _energy * punchMul * amp * 1.55;

                        double y = baseY - wave * amp * _energy - peak;
                        if (y < 0) y = 0;
                        if (y > h) y = h;
                        pts[i] = new Point(i * dx, y);
                    }

                    ctx.BeginFigure(new Point(0, h), true, true);
                    ctx.PolyLineTo(pts, true, false);
                    ctx.LineTo(new Point(w, h), true, false);
                }
            }
        }

        /// <summary>按四色阶重建三层的三点渐变（只在尺寸/配色变化时调）。</summary>
        private void ApplyBrushes()
        {
            double w = _lastW > 2 ? _lastW : 1;
            double h = _lastH > 2 ? _lastH : 1;

            var ramp = new[] { ColorLow, ColorMid, ColorHigh, ColorExtra };
            double fill = Math.Clamp(FillOpacity, 0.0, 1.0);

            for (int L = 0; L < LayerCount; L++)
            {
                var brush = _brushes[L];
                brush.StartPoint = new Point(0, h * GradStartY);
                brush.EndPoint = new Point(w, h);

                for (int s = 0; s < 3; s++)
                {
                    Color c = ramp[Math.Clamp(GradIndex[L][s], 0, 3)];
                    double a = Math.Clamp(GradAlpha[L][s] * fill, 0.0, 1.0);
                    byte alpha = (byte)Math.Round(c.A * a);
                    brush.GradientStops[s].Color = Color.FromArgb(alpha, c.R, c.G, c.B);
                }

                _blurs[L].Radius = Math.Max(0.0, GlowRadius);
                _paths[L].Opacity = Math.Clamp(GlowOpacity, 0.0, 1.0);
            }
        }
    }
}
