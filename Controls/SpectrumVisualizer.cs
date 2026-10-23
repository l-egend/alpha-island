using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
// ImplicitUsings 通过 UseWindowsForms 全局引入了 System.Drawing，Color/Point/Brush 会撞名
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;

namespace alpha_island.Controls
{
    /// <summary>
    /// 音频频谱柱状可视化（灵动岛风格：以中线为轴上下镜像生长，胶囊形柱子 + 横向色相渐变）。
    ///
    /// 性能约定：
    ///  * PushFrame 可从任意线程调用（内部加锁），采集线程直接推，零 Dispatcher 开销；
    ///  * 绘制由 CompositionTarget.Rendering 驱动，IsActive=false 时完全不渲染，收起状态不耗 CPU。
    /// </summary>
    public sealed class SpectrumVisualizer : FrameworkElement
    {
        // ---------------- 依赖属性 ----------------

        public static readonly DependencyProperty BarCountProperty =
            DependencyProperty.Register(nameof(BarCount), typeof(int), typeof(SpectrumVisualizer),
                new FrameworkPropertyMetadata(40, FrameworkPropertyMetadataOptions.AffectsRender, OnBarCountChanged));

        public static readonly DependencyProperty BarGapProperty =
            DependencyProperty.Register(nameof(BarGap), typeof(double), typeof(SpectrumVisualizer),
                new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty MinBarHeightProperty =
            DependencyProperty.Register(nameof(MinBarHeight), typeof(double), typeof(SpectrumVisualizer),
                new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty MirroredProperty =
            DependencyProperty.Register(nameof(Mirrored), typeof(bool), typeof(SpectrumVisualizer),
                new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(SpectrumVisualizer),
                new FrameworkPropertyMetadata(false, OnIsActiveChanged));

        public static readonly DependencyProperty ColorLowProperty =
            DependencyProperty.Register(nameof(ColorLow), typeof(Color), typeof(SpectrumVisualizer),
                new FrameworkPropertyMetadata(Color.FromRgb(0x00, 0xF0, 0xFF), OnBrushChanged));

        public static readonly DependencyProperty ColorMidProperty =
            DependencyProperty.Register(nameof(ColorMid), typeof(Color), typeof(SpectrumVisualizer),
                new FrameworkPropertyMetadata(Color.FromRgb(0x70, 0x00, 0xFF), OnBrushChanged));

        public static readonly DependencyProperty ColorHighProperty =
            DependencyProperty.Register(nameof(ColorHigh), typeof(Color), typeof(SpectrumVisualizer),
                new FrameworkPropertyMetadata(Color.FromRgb(0xFF, 0x00, 0x7A), OnBrushChanged));

        /// <summary>
        /// 四色阶的第四色。设计原型里频谱是"每根柱子自己一条纵向渐变、
        /// 上下两端的取色在四色阶里镜像对折"，所以需要 4 个颜色而不是 3 个。
        /// </summary>
        public static readonly DependencyProperty ColorExtraProperty =
            DependencyProperty.Register(nameof(ColorExtra), typeof(Color), typeof(SpectrumVisualizer),
                new FrameworkPropertyMetadata(Color.FromRgb(0x3B, 0x82, 0xF6), OnBrushChanged));

        /// <summary>柱子上下的额外柔光半径（0 关闭）。</summary>
        public static readonly DependencyProperty GlowRadiusProperty =
            DependencyProperty.Register(nameof(GlowRadius), typeof(double), typeof(SpectrumVisualizer),
                new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>柔光层不透明度（0 关闭）。</summary>
        public static readonly DependencyProperty GlowOpacityProperty =
            DependencyProperty.Register(nameof(GlowOpacity), typeof(double), typeof(SpectrumVisualizer),
                new FrameworkPropertyMetadata(0.38, OnBrushChanged));

        /// <summary>
        /// 中间柱相对两侧的额外强调。0 = 全等；0.34 时 5 根柱的权重是
        /// {0.66, 0.92, 1.00, 0.92, 0.66}，形成"重拍时中间两根优先拉高、两侧跟随"的中间高律动。
        /// </summary>
        public static readonly DependencyProperty CenterEmphasisProperty =
            DependencyProperty.Register(nameof(CenterEmphasis), typeof(double), typeof(SpectrumVisualizer),
                new FrameworkPropertyMetadata(0.34, FrameworkPropertyMetadataOptions.AffectsRender));

        public int BarCount
        {
            get => (int)GetValue(BarCountProperty);
            set => SetValue(BarCountProperty, value);
        }

        public double BarGap
        {
            get => (double)GetValue(BarGapProperty);
            set => SetValue(BarGapProperty, value);
        }

        public double MinBarHeight
        {
            get => (double)GetValue(MinBarHeightProperty);
            set => SetValue(MinBarHeightProperty, value);
        }

        public bool Mirrored
        {
            get => (bool)GetValue(MirroredProperty);
            set => SetValue(MirroredProperty, value);
        }

        /// <summary>只有为 true 时才参与逐帧渲染。</summary>
        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
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

        public double GlowRadius
        {
            get => (double)GetValue(GlowRadiusProperty);
            set => SetValue(GlowRadiusProperty, value);
        }

        public double GlowOpacity
        {
            get => (double)GetValue(GlowOpacityProperty);
            set => SetValue(GlowOpacityProperty, value);
        }

        public static readonly DependencyProperty UniformColorProperty =
            DependencyProperty.Register(nameof(UniformColor), typeof(bool), typeof(SpectrumVisualizer),
                new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

        public bool UniformColor
        {
            get => (bool)GetValue(UniformColorProperty);
            set => SetValue(UniformColorProperty, value);
        }

        public double CenterEmphasis
        {
            get => (double)GetValue(CenterEmphasisProperty);
            set => SetValue(CenterEmphasisProperty, value);
        }

        // ---------------- 内部状态 ----------------

        private readonly object _gate = new();
        private float[] _incoming = Array.Empty<float>();
        private bool _pending;

        private float[] _target = new float[40];
        private float[] _display = new float[40];

        private Brush?[] _barBrushes = Array.Empty<Brush?>();
        private Brush?[] _glowBrushes = Array.Empty<Brush?>();
        private bool _brushesDirty = true;

        private bool _rendering;
        private double _lastTick = -1;

        public SpectrumVisualizer()
        {
            SnapsToDevicePixels = false;
            UseLayoutRounding = false;
            Loaded += (_, __) => { if (IsActive) StartRendering(); };
            Unloaded += (_, __) => StopRendering();
            IsVisibleChanged += (_, _) => { if (IsActive && IsVisible) StartRendering(); else StopRendering(); };
        }

        private static void OnBarCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var v = (SpectrumVisualizer)d;
            v.RebuildBuffers(Math.Max(1, (int)e.NewValue));
            v.InvalidateVisual();
        }

        private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var v = (SpectrumVisualizer)d;
            v._brushesDirty = true;
            v.InvalidateVisual();
        }

        private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var v = (SpectrumVisualizer)d;
            if ((bool)e.NewValue) v.StartRendering();
            else v.StopRendering();
        }

        private void RebuildBuffers(int barCount)
        {
            lock (_gate)
            {
                _target = new float[barCount];
                _display = new float[barCount];
            }
            _brushesDirty = true;
        }

        // ---------------- 输入 ----------------

        /// <summary>推入一帧频谱（取值 0..1，长度任意，会重采样到 BarCount）。线程安全。</summary>
        public void PushFrame(float[] bands)
        {
            if (bands == null) return;
            PushFrame(bands.AsSpan());
        }

        internal void PushFrame(ReadOnlySpan<float> bands)
        {
            if (bands.IsEmpty) return;
            lock (_gate)
            {
                if (_incoming.Length != bands.Length) _incoming = new float[bands.Length];
                bands.CopyTo(_incoming);
                _pending = true;
            }
        }

        /// <summary>清零（停止播放时用）。</summary>
        public void Clear()
        {
            lock (_gate)
            {
                Array.Clear(_target, 0, _target.Length);
                _pending = false;
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

            ApplyIncoming();
            Step(dt);
            InvalidateVisual();
        }

        private void ApplyIncoming()
        {
            lock (_gate)
            {
                if (!_pending || _incoming.Length == 0 || _target.Length == 0) return;
                _pending = false;

                int nSrc = _incoming.Length;
                int nBar = _target.Length;
                for (int i = 0; i < nBar; i++)
                {
                    int a = (int)((long)i * nSrc / nBar);
                    int b = (int)((long)(i + 1) * nSrc / nBar);
                    if (b <= a) b = a + 1;
                    if (b > nSrc) b = nSrc;

                    float m = 0f;
                    float sum = 0f;
                    for (int j = a; j < b; j++)
                    {
                        if (_incoming[j] > m) m = _incoming[j];
                        sum += _incoming[j];
                    }

                    // 组内不再只取峰值：13 个源频段里只要有一个冒头就把柱子顶到满格，
                    // 宽带音乐下中间几根就会一直贴顶、看不出变化。峰值与均值各半，
                    // 既保留"有能量就抬起来"的灵敏度，又让柱高随整体能量起伏。
                    int n = b - a;
                    m = n > 0 ? (m * 0.5f + (sum / n) * 0.5f) : m;

                    // 固定权重：中间高、两侧低。重拍时中间两根先被拉高，两边跟着起伏。
                    double t = nBar > 1 ? (2.0 * i / (nBar - 1)) - 1.0 : 0.0;   // -1 .. +1
                    double w = 1.0 - CenterEmphasis * t * t;
                    _target[i] = m * (float)Math.Max(0.0, Math.Min(1.0, w));
                }
            }
        }

        /// <summary>向目标值做指数插值，让柱子有"弹性"而不是硬跳。</summary>
        private void Step(double dt)
        {
            double attack = 1.0 - Math.Exp(-dt / 0.022);
            double release = 1.0 - Math.Exp(-dt / 0.085);

            for (int i = 0; i < _display.Length; i++)
            {
                float t = _target[i];
                double k = t > _display[i] ? attack : release;
                _display[i] = (float)(_display[i] + (t - _display[i]) * k);
                if (_display[i] < 0.0008f) _display[i] = 0f;
            }
        }

        // ---------------- 绘制 ----------------

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            int n = _display.Length;
            if (w <= 1 || h <= 1 || n == 0) return;

            EnsureBrushes(n);

            double slot = w / n;
            double gap = Math.Min(BarGap, slot * 0.55);
            double barW = Math.Max(1.0, slot - gap);
            // 始终按柱宽取整圆角：两端恒定是半圆，永远是胶囊形，不会出现尖角
            double radius = barW / 2.0;
            double cy = Mirrored ? h / 2.0 : h;
            double glowR = Math.Max(0, GlowRadius);
            bool useGlow = glowR > 0.01 && GlowOpacity > 0.01;

            // 原型：安静态也不是 0 高，而是留在 min..max 区间的矮端（8 / 44 ≈ 0.18）
            double maxH = Math.Max(MinBarHeight, h * 0.92);

            for (int i = 0; i < n; i++)
            {
                float v = _display[i];

                // 不做「值为 0 就跳过」：安静时也要留一颗矮胖小圆胶囊
                double barH = MinBarHeight + v * Math.Max(0.0, maxH - MinBarHeight);
                double x = i * slot + (slot - barW) / 2.0;
                double y = Mirrored ? cy - barH / 2.0 : h - barH;

                if (y < 0) { barH += y; y = 0; }
                if (barH <= 0.6) continue;

                var rect = new Rect(x, y, barW, barH);
                var barBrush = _barBrushes[i];
                if (barBrush == null) continue;

                if (useGlow)
                {
                    var glowBrush = _glowBrushes[i];
                    if (glowBrush != null)
                    {
                        var glow = rect;
                        glow.Inflate(glowR, glowR);
                        dc.DrawRoundedRectangle(glowBrush, null, glow, radius + glowR, radius + glowR);
                    }
                }

                dc.DrawRoundedRectangle(barBrush, null, rect, radius, radius);
            }
        }

        /// <summary>
        /// 每根柱子一条**自己的纵向渐变**（设计原型：<c>linear-gradient(180deg, ramp[min(i,3)], ramp[max(0,3-i)])</c>），
        /// 上下两端的取色在四色阶里镜像对折 —— 所以 5 根柱依次是
        /// <c>0→3 / 1→2 / 2→1 / 3→0 / 3→0</c>，整体呈现"从暖到冷再折回来"的对称色序。
        /// 用 RelativeToBoundingBox，柱子长高长矮都不用重建笔刷。
        /// </summary>
        private void EnsureBrushes(int n)
        {
            if (!_brushesDirty && _barBrushes.Length == n) return;
            _brushesDirty = false;

            _barBrushes = new Brush?[n];
            _glowBrushes = new Brush?[n];

            if (UniformColor)
            {
                var color = ReadableAlbumColor(ColorLow);
                var solid = new SolidColorBrush(Color.FromArgb(0xEE, color.R, color.G, color.B));
                solid.Freeze();
                var glow = new SolidColorBrush(Color.FromArgb(0x35, color.R, color.G, color.B));
                glow.Freeze();
                for (int i = 0; i < n; i++)
                {
                    _barBrushes[i] = solid;
                    _glowBrushes[i] = glow;
                }
                return;
            }

            var ramp = new[] { ReadableAlbumColor(ColorLow), ReadableAlbumColor(ColorMid),
                ReadableAlbumColor(ColorHigh), ReadableAlbumColor(ColorExtra) };

            for (int i = 0; i < n; i++)
            {
                Color top = ramp[Math.Min(i, 3)];
                Color bottom = ramp[Math.Max(0, 3 - i)];

                // 原型 alpha：柱体 e8 → 99（0.91 → 0.60），辉光 30（0.19）
                _barBrushes[i] = Vertical(top, bottom, 0xE8, 0x99);
                _glowBrushes[i] = Vertical(top, top, 0x30, 0x30);
            }
        }

        // Keep ordinary album colours unchanged. Lift only very dark tones and
        // lower near-white tones into a readable range; a neutral blend preserves
        // the source hue. This runs when cached brushes change, never per frame.
        private static Color ReadableAlbumColor(Color color)
        {
            double luma = (.2126 * color.R + .7152 * color.G + .0722 * color.B) / 255.0;
            double target = Math.Clamp(luma, .38, .74);
            if (target == luma) return color;
            double blend = target > luma ? (target - luma) / (1 - luma) : (luma - target) / luma;
            double neutral = target > luma ? 255 : 0;
            byte Tone(byte value) => (byte)Math.Clamp(Math.Round(value + (neutral - value) * blend), 0, 255);
            return Color.FromArgb(color.A, Tone(color.R), Tone(color.G), Tone(color.B));
        }

        /// <summary>纵向渐变（相对包围盒），k 是叠加到颜色自带 alpha 上的因子。</summary>
        private static LinearGradientBrush Vertical(Color top, Color bottom, int alphaTop, int alphaBottom)
        {
            Color A(Color c, int k) => Color.FromArgb((byte)(c.A * k / 255), c.R, c.G, c.B);

            var brush = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(A(top, alphaTop), 0.0),
                    new GradientStop(A(bottom, alphaBottom), 1.0)
                }
            };
            brush.Freeze();
            return brush;
        }
    }
}
