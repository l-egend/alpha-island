using System.Windows;
using System.Windows.Media;
using alpha_island.Core;
using Point = System.Windows.Point;

namespace alpha_island.Controls;

public sealed partial class SemanticIcon
{
    public static readonly DependencyProperty WeatherCodeProperty = DependencyProperty.Register(nameof(WeatherCode), typeof(int?), typeof(SemanticIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The forecast code for the displayed period, not necessarily the current weather.</summary>
    public int? WeatherCode { get => (int?)GetValue(WeatherCodeProperty); set => SetValue(WeatherCodeProperty, value); }

    private static readonly Geometry WeatherCloud = WeatherGeometry("M8,23 C1,23 1,13 8,13 C9,6 20,5 23,13 C31,12 33,23 25,23 Z");
    private static readonly Geometry WeatherHighCloud = WeatherGeometry("M8,18 C1,18 1,8 8,8 C9,1 20,0 23,8 C31,7 33,18 25,18 Z");
    private static readonly Geometry WeatherSmallCloud = WeatherGeometry("M13,27 C7,27 7,19 13,19 C14,13 23,12 25,19 C32,18 33,27 27,27 Z");
    private static readonly Geometry WeatherPartialSunClip = WeatherClip();
    private static readonly Geometry WeatherLightning = WeatherGeometry("M18,18 L12,25 L17,25 L14,31 L24,22 L18,22 L22,18 Z");
    private static readonly Point[][] WeatherSunRays = WeatherRays(16, 16, 9, 12.8);
    private static readonly Point[][] WeatherPartialRays = WeatherRays(12, 11, 7.5, 10.2);
    private static readonly Point[][] WeatherRain = [[new(10, 22), new(8, 27)], [new(18, 22), new(16, 27)], [new(26, 22), new(24, 27)]];
    private static readonly Point[][] WeatherFog = [[new(5, 23), new(27, 23)], [new(8, 28), new(24, 28)]];

    private void DrawWeather(DrawingContext dc, double progress)
    {
        // Original local vector contours avoid optional icon-font glyphs on Windows 10/11.
        // Only internal rays/precipitation reveal once; the resting icon has no timer or clock.
        var kind = WeatherIconPolicy.Resolve(WeatherCode);
        double p = _transitioning ? progress : 1;
        if (kind == WeatherIconKind.Sunny)
        {
            DrawWeatherSun(dc, new Point(16, 16), 6, WeatherSunRays, p);
            return;
        }
        if (kind == WeatherIconKind.PartlyCloudy)
        {
            dc.PushClip(WeatherPartialSunClip);
            DrawWeatherSun(dc, new Point(12, 11), 4.5, WeatherPartialRays, p);
            dc.Pop();
            // Clip the covered rays without painting over the island's translucent background.
            DrawWeatherCloud(dc, WeatherSmallCloud, p);
            return;
        }
        bool high = kind is WeatherIconKind.Fog or WeatherIconKind.Drizzle or WeatherIconKind.Rain
            or WeatherIconKind.FreezingRain or WeatherIconKind.Snow or WeatherIconKind.Thunderstorm;
        DrawWeatherCloud(dc, high ? WeatherHighCloud : WeatherCloud, p);
        switch (kind)
        {
            case WeatherIconKind.Fog:
                Stroke(dc, WeatherFog[0], Segment(p, .15, .7));
                Stroke(dc, WeatherFog[1], Segment(p, .35, 1));
                break;
            case WeatherIconKind.Drizzle:
                for (int i = 0; i < 3; i++)
                {
                    double reveal = Segment(p, .15 + i * .18, .6 + i * .18);
                    if (reveal > 0) dc.DrawEllipse(Foreground, null, new Point(9 + i * 8, 24), 1.4 * reveal, 1.4 * reveal);
                }
                break;
            case WeatherIconKind.Rain:
                for (int i = 0; i < WeatherRain.Length; i++) Stroke(dc, WeatherRain[i], Segment(p, .12 + i * .15, .65 + i * .15));
                break;
            case WeatherIconKind.FreezingRain:
                Stroke(dc, WeatherRain[0], Segment(p, .15, .75));
                DrawWeatherSnowflake(dc, new Point(22, 25), 3.5, Segment(p, .3, 1));
                break;
            case WeatherIconKind.Snow:
                DrawWeatherSnowflake(dc, new Point(10, 25), 3.5, Segment(p, .15, .8));
                DrawWeatherSnowflake(dc, new Point(23, 25), 3.5, Segment(p, .3, 1));
                break;
            case WeatherIconKind.Thunderstorm:
                dc.PushOpacity(Segment(p, .15, .85));
                dc.DrawGeometry(Foreground, null, WeatherLightning);
                dc.Pop();
                Stroke(dc, WeatherRain[0], Segment(p, .35, 1));
                break;
            case WeatherIconKind.Unknown:
                // Unknown/legacy data remains a neutral cloud; never invent a sunny forecast.
                break;
        }
    }

    private void DrawWeatherCloud(DrawingContext dc, Geometry geometry, double p)
    {
        dc.PushOpacity(.35 + .65 * Segment(p, 0, .6));
        dc.DrawGeometry(null, _pen, geometry);
        dc.Pop();
    }

    private void DrawWeatherSun(DrawingContext dc, Point center, double radius, Point[][] rays, double p)
    {
        dc.DrawEllipse(null, _pen, center, radius, radius);
        for (int i = 0; i < rays.Length; i++) Stroke(dc, rays[i], Segment(p, .05 + i * .055, .55 + i * .055));
    }

    private void DrawWeatherSnowflake(DrawingContext dc, Point center, double radius, double p)
    {
        Stroke(dc, [new(center.X, center.Y - radius), new(center.X, center.Y + radius)], p);
        Stroke(dc, [new(center.X - radius * .866, center.Y - radius * .5), new(center.X + radius * .866, center.Y + radius * .5)], p);
        Stroke(dc, [new(center.X - radius * .866, center.Y + radius * .5), new(center.X + radius * .866, center.Y - radius * .5)], p);
    }

    private static Geometry WeatherGeometry(string path)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }

    private static Geometry WeatherClip()
    {
        var geometry = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, 32, 32)), WeatherSmallCloud);
        geometry.Freeze();
        return geometry;
    }

    private static Point[][] WeatherRays(double x, double y, double inner, double outer)
    {
        var rays = new Point[8][];
        for (int i = 0; i < rays.Length; i++)
        {
            double angle = i * Math.PI / 4;
            rays[i] = [new(x + Math.Cos(angle) * inner, y + Math.Sin(angle) * inner), new(x + Math.Cos(angle) * outer, y + Math.Sin(angle) * outer)];
        }
        return rays;
    }
}
