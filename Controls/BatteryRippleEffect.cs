using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace alpha_island.Controls
{
    /// <summary>
    /// 充放电时在灵动岛内部播放的流体水波涟漪特效组件。
    ///  - 充电状态：如水滴入池，青翠柔和的水波涟漪从岛体中心向四周层层漾开、自然消融；
    ///  - 耗电/拔出状态：暖橙色水波柔和漾开并快速淡出，提示电源已断开。
    ///  - 特性：完全摒弃同心硬边描边（Pen=null），全流程采用 RadialGradientBrush 径向高斯光晕渲染，
    ///    边缘 100% 渐隐至透明，绝无同心线框层纹；且具备硬件加速与极低开销。
    /// </summary>
    public sealed class BatteryRippleEffect : FrameworkElement
    {
        private bool _isCharging;
        private double _progress; // 0.0 -> 1.0
        private bool _animating;
        private long _startTimestamp;
        private const double DurationSeconds = 1.6; // 持续约 1.6 秒，如静水微澜般舒缓自然

        // 柔和水绿（充电）与暖橙（断电）
        private static readonly Color ChargingGreen = Color.FromArgb(0xFF, 0x00, 0xE6, 0x82);
        private static readonly Color DischargingAmber = Color.FromArgb(0xFF, 0xF5, 0x82, 0x20);

        public BatteryRippleEffect()
        {
            IsHitTestVisible = false;
        }

        /// <summary>触发水波涟漪特效。</summary>
        /// <param name="isCharging">true 为充电水波；false 为拔出橙波。</param>
        public void Trigger(bool isCharging)
        {
            _isCharging = isCharging;
            _progress = 0.0;
            _startTimestamp = Stopwatch.GetTimestamp();

            if (!_animating)
            {
                _animating = true;
                CompositionTarget.Rendering += OnRendering;
            }

            InvalidateVisual();
        }

        /// <summary>强行停止并清空波纹。</summary>
        public void Stop()
        {
            if (_animating)
            {
                CompositionTarget.Rendering -= OnRendering;
                _animating = false;
            }
            _progress = 1.0;
            InvalidateVisual();
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            double elapsed = (double)(Stopwatch.GetTimestamp() - _startTimestamp) / Stopwatch.Frequency;
            _progress = elapsed / DurationSeconds;

            if (_progress >= 1.0)
            {
                _progress = 1.0;
                _animating = false;
                CompositionTarget.Rendering -= OnRendering;
            }

            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            if (!_animating && _progress >= 1.0) return;

            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 4 || height <= 4) return;

            Point center = new Point(width / 2.0, height / 2.0);
            double maxRx = (width / 2.0) * 1.25;
            double maxRy = (height / 2.0) * 1.40;

            Color baseColor = _isCharging ? ChargingGreen : DischargingAmber;

            // 0. 中心水滴晕影：在动画前半段（0 ~ 0.5）如晨露滴入般从中心泛起淡淡微光
            if (_progress < 0.55)
            {
                double centerBloomProgress = _progress / 0.55;
                double centerAlphaFactor = Math.Sin(centerBloomProgress * Math.PI);
                byte centerAlpha = (byte)Math.Clamp(Math.Round(22 * centerAlphaFactor), 0, 255);
                if (centerAlpha > 1)
                {
                    var centerBrush = new RadialGradientBrush
                    {
                        Center = new Point(0.5, 0.5),
                        GradientOrigin = new Point(0.5, 0.5),
                        RadiusX = 0.5,
                        RadiusY = 0.5
                    };
                    centerBrush.GradientStops.Add(new GradientStop(Color.FromArgb(centerAlpha, baseColor.R, baseColor.G, baseColor.B), 0.0));
                    centerBrush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(centerAlpha * 0.45), baseColor.R, baseColor.G, baseColor.B), 0.45));
                    centerBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, baseColor.R, baseColor.G, baseColor.B), 1.0));
                    centerBrush.Freeze();

                    double cbRx = Math.Min(width * 0.4, 65.0);
                    double cbRy = Math.Min(height * 0.65, 24.0);
                    dc.DrawEllipse(centerBrush, null, center, cbRx, cbRy);
                }
            }

            // 1. 绘制 3 层自然接力扩散的水波涟漪（平滑高斯钟形包络，完全消除同心环硬边层纹）
            const int waveCount = 3;
            for (int i = 0; i < waveCount; i++)
            {
                double phaseOffset = i * 0.22;
                double waveProgress = (_progress - phaseOffset) / (1.0 - phaseOffset);
                if (waveProgress < 0.0 || waveProgress > 1.0) continue;

                // 平滑水波缓动扩展
                double ease = 1.0 - Math.Pow(1.0 - waveProgress, 2.2);
                double rx = 18.0 + ease * (maxRx - 18.0);
                double ry = 9.0 + ease * (maxRy - 9.0);

                // 峰值透明度包络：随扩散先升后降，极其柔和淡出，层级依次递减
                double waveWeight = i switch { 0 => 1.0, 1 => 0.65, _ => 0.40 };
                double alphaFactor = Math.Sin(waveProgress * Math.PI) * (1.0 - waveProgress * 0.3) * waveWeight;
                byte peakAlpha = (byte)Math.Clamp(Math.Round(20 * alphaFactor), 0, 255);
                if (peakAlpha <= 1) continue;

                // 纯径向流体水波涟漪画刷：采用宽幅高斯平滑连续过渡，彻底消除同心圆硬边与层纹
                var waveBrush = new RadialGradientBrush
                {
                    Center = new Point(0.5, 0.5),
                    GradientOrigin = new Point(0.5, 0.5),
                    RadiusX = 0.5,
                    RadiusY = 0.5
                };
                waveBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, baseColor.R, baseColor.G, baseColor.B), 0.0));
                waveBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, baseColor.R, baseColor.G, baseColor.B), 0.35));
                waveBrush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(peakAlpha * 0.45), baseColor.R, baseColor.G, baseColor.B), 0.65));
                waveBrush.GradientStops.Add(new GradientStop(Color.FromArgb(peakAlpha, baseColor.R, baseColor.G, baseColor.B), 0.80));
                waveBrush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(peakAlpha * 0.35), baseColor.R, baseColor.G, baseColor.B), 0.92));
                waveBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, baseColor.R, baseColor.G, baseColor.B), 1.0));
                waveBrush.Freeze();

                dc.DrawEllipse(waveBrush, null, center, rx, ry);
            }
        }
    }
}
