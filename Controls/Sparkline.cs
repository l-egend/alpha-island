using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
// 项目里同时能看见 System.Drawing（隐式命名空间），Brush/Pen/Point 会撞名 —— 显式消歧。
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

// 迷你折线图（网速卡片页的实时曲线）。
//
// 为什么不用 WaveVisualizer：那是为音频频谱写的（镜像频谱条 + 发光效果 + 逐帧动画），
// 拿来画"每秒一个采样点的历史曲线"要额外喂假频谱，还会每帧触发一次离屏渲染。
// 这个控件的原则是**有数据才重绘**：SetValues 写进缓冲区后只调一次 InvalidateVisual，
// 由 WPF 合成线程按需重画；没有动画、没有 Effect、没有 DispatcherTimer。
namespace alpha_island.Controls
{
    /// <summary>
    /// 把一串数值画成一条折线（可选曲线下方填充）。X 轴按"窗口内点数"均分，
    /// Y 轴按 <see cref="FixedMax"/> 或当前数据的最大值自适应（留 10% 顶空）。
    /// </summary>
    public sealed class Sparkline : FrameworkElement
    {
        private static readonly Pen FallbackPen;

        static Sparkline()
        {
            FallbackPen = new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)), 1.5);
            FallbackPen.Freeze();
        }

        public Sparkline()
        {
            SnapsToDevicePixels = true;
            IsHitTestVisible = false;
        }

        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
            nameof(Stroke), typeof(Brush), typeof(Sparkline),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>折线颜色。null 时用回退青色（XAML 一般写 {StaticResource AccentCyan}）。</summary>
        public Brush? Stroke
        {
            get => (Brush?)GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
            nameof(Fill), typeof(Brush), typeof(Sparkline),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>曲线下方的面积填充（可空 = 只画线）。</summary>
        public Brush? Fill
        {
            get => (Brush?)GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
            nameof(StrokeThickness), typeof(double), typeof(Sparkline),
            new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender));

        public double StrokeThickness
        {
            get => (double)GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }

        public static readonly DependencyProperty FixedMaxProperty = DependencyProperty.Register(
            nameof(FixedMax), typeof(double), typeof(Sparkline),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Y 轴固定上限（0 = 按数据自适应）。固定上限能让"慢"和"快"有可比的斜率。</summary>
        public double FixedMax
        {
            get => (double)GetValue(FixedMaxProperty);
            set => SetValue(FixedMaxProperty, value);
        }

        public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
            nameof(Capacity), typeof(int), typeof(Sparkline),
            new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>窗口内最多画多少个点（超出只画最近这些点）。</summary>
        public int Capacity
        {
            get => (int)GetValue(CapacityProperty);
            set => SetValue(CapacityProperty, value);
        }

        /// <summary>曲线起点的透明度（0 = 透明，从左往右淡入）。</summary>
        public static readonly DependencyProperty FadeInProperty = DependencyProperty.Register(
            nameof(FadeIn), typeof(bool), typeof(Sparkline),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        public bool FadeIn
        {
            get => (bool)GetValue(FadeInProperty);
            set => SetValue(FadeInProperty, value);
        }

        private readonly List<double> _values = new(128);

        /// <summary>当前缓冲区的只读视图（诊断日志用，避免为了打印而复制）。</summary>
        public IReadOnlyList<double> Values => _values;

        /// <summary>
        /// 整批替换数据。只在这里 InvalidateVisual —— 调用方每秒来一次即可。
        /// </summary>
        public void SetValues(IReadOnlyList<double> values)
        {
            _values.Clear();
            for (int i = 0; i < values.Count; i++) _values.Add(values[i]);
            InvalidateVisual();
        }

        /// <summary>追加一个点，超过 Capacity 就丢掉最老的（曲线整体左移一格）。</summary>
        public void Push(double value)
        {
            _values.Add(value);

            int capacity = Capacity;
            if (capacity < 2) capacity = 2;
            while (_values.Count > capacity) _values.RemoveAt(0);

            InvalidateVisual();
        }

        public void Clear()
        {
            if (_values.Count == 0) return;
            _values.Clear();
            InvalidateVisual();
        }

        /// <summary>当前窗口内的峰值（卡片页脚显示"峰值 x MB/s"）。</summary>
        public double Peak
        {
            get
            {
                double peak = 0;
                for (int i = 0; i < _values.Count; i++)
                {
                    if (_values[i] > peak) peak = _values[i];
                }
                return peak;
            }
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 1 || height <= 1) return;

            int count = _values.Count;
            if (count < 2) return;   // 一个点画不出斜率：等第二拍

            // 只画最近 Capacity 个点（缓冲区本来就被 Push 裁剪，这里再兜一次底）。
            int capacity = Capacity < 2 ? 2 : Capacity;
            int start = count > capacity ? count - capacity : 0;
            int points = count - start;
            if (points < 2) return;

            double max = FixedMax;
            if (max <= 0)
            {
                for (int i = start; i < count; i++)
                {
                    if (_values[i] > max) max = _values[i];
                }
                // 留 10% 顶空，避免峰值贴着上边界；全 0 时给一个下限防止除零。
                max *= 1.1;
                if (max < 1.0) max = 1.0;
            }

            double pad = StrokeThickness;                 // 线宽的一半会被裁掉，留一点内边距
            double usableW = width - pad * 2;
            double usableH = height - pad * 2;
            if (usableW <= 0 || usableH <= 0) return;

            double step = usableW / (points - 1);

            // 填充和描线分开两次 DrawGeometry：合成一条路径的话，填充的底边也会被描一次，
            // 曲线下方会多出一条横向的线。
            if (Fill != null)
            {
                var area = new StreamGeometry();
                using (StreamGeometryContext ctx = area.Open())
                {
                    ctx.BeginFigure(new Point(pad, height - pad), true, true);
                    for (int i = 0; i < points; i++)
                    {
                        ctx.LineTo(PointAt(i, start, step, pad, usableH, max), true, false);
                    }
                    ctx.LineTo(new Point(pad + usableW, height - pad), true, false);
                }
                area.Freeze();
                drawingContext.DrawGeometry(Fill, null, area);
            }

            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                ctx.BeginFigure(PointAt(0, start, step, pad, usableH, max), false, false);
                for (int i = 1; i < points; i++)
                {
                    ctx.LineTo(PointAt(i, start, step, pad, usableH, max), true, false);
                }
            }

            geometry.Freeze();

            var pen = Stroke != null
                ? new Pen(Stroke, StrokeThickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }
                : FallbackPen;
            if (pen.CanFreeze) pen.Freeze();

            if (FadeIn)
            {
                // 渐变只作用在线上：左端透明 → 右端不透明，观感上像"新数据从右边写进来"。
                var brush = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 0)
                };
                if (Stroke is SolidColorBrush solid)
                {
                    brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x30, solid.Color.R, solid.Color.G, solid.Color.B), 0));
                    brush.GradientStops.Add(new GradientStop(solid.Color, 1));
                }
                brush.Freeze();
                pen = new Pen(brush, StrokeThickness)
                {
                    LineJoin = PenLineJoin.Round,
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };
                pen.Freeze();
            }

            drawingContext.DrawGeometry(Fill, pen, geometry);
        }

        private Point PointAt(int i, int start, double step, double pad, double usableH, double max)
        {
            double v = _values[start + i];
            double y = pad + usableH * (1.0 - Math.Clamp(v / max, 0.0, 1.0));
            return new Point(pad + i * step, y);
        }
    }
}
