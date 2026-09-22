using System;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using PixelFormat = System.Windows.Media.PixelFormat;
using PixelFormats = System.Windows.Media.PixelFormats;

namespace alpha_island.Services
{
    /// <summary>
    /// 从专辑封面提取主色调，给频谱柱 / 波浪层上色（用户要求"色彩取自专辑封面主色调"）。
    ///
    /// 取色思路：缩到 32x32 → 转 Bgra32 → 逐像素转 HSL →
    /// 丢掉几乎全黑 / 全白 / 低饱和的像素，按色相分 24 个桶累加权重（权重 = 饱和度² × 明度）→
    /// 取权重最大的桶的平均色。取不到就用霓虹默认色（并置 <see cref="IsFallback"/>）。
    /// </summary>
    public sealed class CoverPalette
    {
        /// <summary>主色：柱体/波浪的主色相。</summary>
        public Color Primary { get; private set; }

        /// <summary>主色提亮版：柱顶 / 波浪波峰。</summary>
        public Color Bright { get; private set; }

        /// <summary>主色压暗版：柱底 / 波浪底部。</summary>
        public Color Deep { get; private set; }

        /// <summary>
        /// 四色色阶：<c>暖橙红 → 洋红 → 紫罗兰 → 蓝</c>。
        ///
        /// 设计原型（Copilot 的 JSX）把这四个色写死成
        /// <c>#ff7a59 / #ff3d81 / #8b5cf6 / #3b82f6</c>；
        /// 但用户要求"色彩取自专辑封面主色调"，所以这里**以封面主色相为锚点**，
        /// 按原型调好的色相偏移展开，饱和度/明度统一抬到霓虹档。
        /// 效果：无论封面偏什么颜色，冷暖跨度都和原型一模一样，只是整体旋转到封面的色域。
        /// </summary>
        public Color[] Ramp { get; private set; } = Array.Empty<Color>();

        /// <summary>true = 没从封面取到颜色，用的是霓虹兜底色。</summary>
        public bool IsFallback { get; private set; }

        private CoverPalette() { }

        /// <summary>原型四色（取不到封面主色时用它兜底）。</summary>
        public static readonly Color[] DefaultRamp =
        {
            Color.FromRgb(0xFF, 0x7A, 0x59),
            Color.FromRgb(0xFF, 0x3D, 0x81),
            Color.FromRgb(0x8B, 0x5C, 0xF6),
            Color.FromRgb(0x3B, 0x82, 0xF6)
        };

        /// <summary>默认霓虹色（和 Theme 里的 AccentCyan / AccentViolet / AccentMagenta 同族）。</summary>
        public static CoverPalette Fallback => new CoverPalette
        {
            Primary = Color.FromRgb(0x00, 0xF0, 0xFF),
            Bright = Color.FromRgb(0x8C, 0xFB, 0xFF),
            Deep = Color.FromRgb(0x70, 0x00, 0xFF),
            Ramp = DefaultRamp,
            IsFallback = true
        };

        /// <summary>封面本身不足 4 个色相桶时，用主色附近的小偏移补位（同族，不会跳到别的色相去）。</summary>
        private static readonly double[] PadHueOffsets = { 0.0, -14.0, 16.0, -30.0 };

        private const int SampleSize = 32;
        private const int HueBuckets = 24;

        /// <summary>
        /// 真正的"从专辑图取色"：直接拿封面色相桶里最有代表性的 4 个真实颜色做四色阶。
        /// （旧版是对主色相做固定偏移 0/-33/-114/-155 度展开，蓝色封面会被推成绿色，
        ///   用户实测反馈"波浪颜色和封面不一致"，所以改成取封面自己的颜色。）
        /// </summary>
        private static Color[] RampFromBuckets(
            ReadOnlySpan<double> weight, ReadOnlySpan<double> sumR, ReadOnlySpan<double> sumG,
            ReadOnlySpan<double> sumB, int best, Color primary)
        {
            var picks = new List<(double W, Color C)>();
            double floor = weight[best] * 0.22;
            for (int i = 0; i < weight.Length; i++)
            {
                if (weight[i] <= 0 || weight[i] < floor) continue;
                picks.Add((weight[i], Color.FromRgb(
                    (byte)Math.Clamp(sumR[i] / weight[i], 0, 255),
                    (byte)Math.Clamp(sumG[i] / weight[i], 0, 255),
                    (byte)Math.Clamp(sumB[i] / weight[i], 0, 255))));
            }

            picks.Sort(static (x, y) => y.W.CompareTo(x.W));

            var ramp = new Color[4];
            int n = Math.Min(4, picks.Count);
            for (int i = 0; i < n; i++) ramp[i] = picks[i].C;

            double hue = HueOf(primary);
            for (int i = n; i < 4; i++)
            {
                double hh = hue + PadHueOffsets[i] / 360.0;
                hh -= Math.Floor(hh);
                ramp[i] = FromHsl(hh, 0.92, 0.63);
            }
            return ramp;
        }

        /// <summary>取 HSL 色相（0..1）。</summary>
        private static double HueOf(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            if (d < 1e-6) return 0.0;

            double hue;
            if (max == r) hue = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) hue = (b - r) / d + 2;
            else hue = (r - g) / d + 4;
            hue /= 6.0;
            if (hue < 0) hue += 1.0;
            return hue;
        }

        public static CoverPalette From(BitmapSource? source)
        {
            if (source == null || source.PixelWidth < 2 || source.PixelHeight < 2) return Fallback;

            byte[]? pixels = null;
            int w = 0, h = 0, stride = 0;

            try
            {
                BitmapSource small = source;
                double scale = (double)SampleSize / Math.Max(source.PixelWidth, source.PixelHeight);
                if (scale < 1.0)
                {
                    var tx = new System.Windows.Media.ScaleTransform(scale, scale);
                    tx.Freeze();
                    small = new TransformedBitmap(source, tx);
                }

                var conv = new FormatConvertedBitmap();
                conv.BeginInit();
                conv.Source = small;
                conv.DestinationFormat = PixelFormats.Bgra32;
                conv.EndInit();

                w = conv.PixelWidth;
                h = conv.PixelHeight;
                stride = w * 4;
                pixels = new byte[stride * h];
                conv.CopyPixels(pixels, stride, 0);
            }
            catch
            {
                return Fallback;
            }

            if (pixels == null || w <= 0 || h <= 0) return Fallback;

            Span<double> weight = stackalloc double[HueBuckets];
            Span<double> sumR = stackalloc double[HueBuckets];
            Span<double> sumG = stackalloc double[HueBuckets];
            Span<double> sumB = stackalloc double[HueBuckets];
            weight.Clear();
            sumR.Clear();
            sumG.Clear();
            sumB.Clear();

            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];

                // 全透明像素（很多封面是带 alpha 的方形）直接跳过
                if (a < 128) continue;

                double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
                double max = Math.Max(rd, Math.Max(gd, bd));
                double min = Math.Min(rd, Math.Min(gd, bd));
                double v = max;
                double d = max - min;
                if (d < 1e-6) continue;                    // 灰阶没有色相，跳过
                double s = d / max;                        // 明度归一后的饱和度

                // 太暗的阴影、太亮的过曝都不代表封面主色
                if (v < 0.16) continue;
                if (v > 0.96 && s < 0.10) continue;

                double hue;
                if (max == rd) hue = (gd - bd) / d + (gd < bd ? 6 : 0);
                else if (max == gd) hue = (bd - rd) / d + 2;
                else hue = (rd - gd) / d + 4;
                hue /= 6.0;                                 // 0..1
                if (hue < 0) hue += 1.0;

                int bucket = (int)(hue * HueBuckets);
                if (bucket < 0) bucket = 0;
                if (bucket >= HueBuckets) bucket = HueBuckets - 1;

                // 越鲜艳、越明亮越有代表性
                double wgt = s * s * (0.35 + 0.65 * v);
                weight[bucket] += wgt;
                sumR[bucket] += r * wgt;
                sumG[bucket] += g * wgt;
                sumB[bucket] += b * wgt;
            }

            int best = -1;
            for (int i = 0; i < HueBuckets; i++)
            {
                if (weight[i] <= 0) continue;
                if (best < 0 || weight[i] > weight[best]) best = i;
            }

            // 主色桶的总权重太小（封面几乎是灰的）→ 退回霓虹色
            if (best < 0 || weight[best] < 0.6) return Fallback;

            byte pr = (byte)Math.Clamp(sumR[best] / weight[best], 0, 255);
            byte pg = (byte)Math.Clamp(sumG[best] / weight[best], 0, 255);
            byte pb = (byte)Math.Clamp(sumB[best] / weight[best], 0, 255);

            var primary = Color.FromRgb(pr, pg, pb);

            return new CoverPalette
            {
                Primary = primary,
                Bright = Adjust(primary, 1.15, +0.18),
                Deep = Adjust(primary, 1.05, -0.20),
                Ramp = RampFromBuckets(weight, sumR, sumG, sumB, best, primary),
                IsFallback = false
            };
        }

        /// <summary>在 HSL 空间里调饱和度和明度（比直接乘 RGB 通道更不容易溢出成白/黑）。</summary>
        private static Color Adjust(Color c, double satMul, double lightDelta)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double l = (max + min) / 2.0;
            double hue = 0, s = 0;
            double d = max - min;

            if (d > 1e-6)
            {
                s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
                if (max == r) hue = (g - b) / d + (g < b ? 6 : 0);
                else if (max == g) hue = (b - r) / d + 2;
                else hue = (r - g) / d + 4;
                hue /= 6.0;
            }

            s = Math.Clamp(s * satMul, 0.0, 1.0);
            l = Math.Clamp(l + lightDelta, 0.06, 0.94);
            return FromHsl(hue, s, l);
        }

        private static Color FromHsl(double h, double s, double l)
        {
            if (s <= 0)
            {
                byte v = (byte)Math.Round(Math.Clamp(l, 0, 1) * 255);
                return Color.FromRgb(v, v, v);
            }

            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            return Color.FromRgb(
                (byte)Math.Round(HueToRgb(p, q, h + 1.0 / 3.0) * 255),
                (byte)Math.Round(HueToRgb(p, q, h) * 255),
                (byte)Math.Round(HueToRgb(p, q, h - 1.0 / 3.0) * 255));
        }

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
            return p;
        }
    }
}
