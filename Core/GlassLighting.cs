using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace alpha_island.Core;

public readonly record struct GlassLight(Vector Direction, double Brightness, double Contrast, Color Tint)
{
    public static GlassLight Default => new(new Vector(-.45, -.893), .65, .15, Colors.White);
    public static GlassLight Blend(GlassLight from, GlassLight to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        Vector direction = from.Direction * (1 - amount) + to.Direction * amount;
        if (direction.LengthSquared < .001) direction = to.Direction;
        if (direction.LengthSquared < .001) direction = Default.Direction;
        direction.Normalize();
        byte Mix(byte a, byte b) => (byte)Math.Clamp(Math.Round(a + (b - a) * amount), 0, 255);
        return new(direction, from.Brightness + (to.Brightness - from.Brightness) * amount,
            from.Contrast + (to.Contrast - from.Contrast) * amount,
            Color.FromRgb(Mix(from.Tint.R, to.Tint.R), Mix(from.Tint.G, to.Tint.G), Mix(from.Tint.B, to.Tint.B)));
    }
}

public readonly record struct GlassLightSample(Vector Direction, Color Color);

public static class GlassLighting
{
    private static readonly double[] Linear = Enumerable.Range(0, 256).Select(i =>
    { double value = i / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }).ToArray();

    public static GlassLight Estimate(IReadOnlyList<GlassLightSample> samples)
    {
        if (samples.Count == 0) return GlassLight.Default;
        double L(Color c) => Linear[c.R] * .2126 + Linear[c.G] * .7152 + Linear[c.B] * .0722;
        double low = samples.Min(s => L(s.Color)), high = samples.Max(s => L(s.Color));
        double brightness = samples.Average(s => L(s.Color));
        if (high - low < .025) return GlassLight.Default with { Brightness = brightness, Contrast = 0 };
        Vector direction = default;
        double sum = 0, red = 0, green = 0, blue = 0;
        foreach (var sample in samples)
        {
            double weight = Math.Pow(Math.Max(0, L(sample.Color) - low), 2);
            direction += sample.Direction * weight;
            sum += weight; red += sample.Color.R * weight; green += sample.Color.G * weight; blue += sample.Color.B * weight;
        }
        if (direction.LengthSquared < .00001 || sum < .00001) return GlassLight.Default with { Brightness = brightness, Contrast = high - low };
        direction.Normalize();
        // Reflections remain readable even when the strongest desktop patch is saturated.
        byte WhiteMix(double channel) => (byte)Math.Clamp(Math.Round(210 + channel / sum * .175), 0, 255);
        return new(direction, brightness, high - low, Color.FromRgb(WhiteMix(red), WhiteMix(green), WhiteMix(blue)));
    }
}
