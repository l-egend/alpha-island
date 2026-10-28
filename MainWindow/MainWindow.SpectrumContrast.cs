using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Services;
using alpha_island.Interop;
using AlphaIsland.PluginSdk;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace alpha_island;

public partial class MainWindow
{
    private Color[] _spectrumAlbumRamp = CoverPalette.DefaultRamp;
    private double? _spectrumBackdropBrightness;
    private bool _spectrumPaletteReady;
    private bool SpectrumBackdropNeeded => _options.VisualizerEnabled && _smtc?.Current is not null
        && ((PillSpectrum.IsActive && PillSpectrum.IsVisible) || (CardWave.IsActive && CardWave.IsVisible));

    private Rect? SpectrumSurfaceProbe(Rect island)
    {
        if (!PillSpectrum.IsActive || !PillSpectrum.IsVisible || PillSpectrum.ActualWidth < 12) return null;
        try
        {
            // Stay above the bars and inside the material, away from its rim.
            double center = PillSpectrum.ActualWidth / 2;
            var patch = new Rect(PillSpectrum.PointToScreen(new Point(center - 3, -7)),
                PillSpectrum.PointToScreen(new Point(center + 3, -3)));
            return island.Contains(patch) ? patch : null;
        }
        catch (InvalidOperationException) { return null; }
    }

    private void ApplySpectrumSample(DesktopLightSampler.Result sample)
        => ApplySpectrumBackdrop(sample.SurfaceBrightness ?? sample.Light.Brightness);

    private void ApplySpectrumBackdrop(double brightness)
    {
        if (!double.IsFinite(brightness) || brightness is < 0 or > 1) return;
        if (_spectrumBackdropBrightness is double old && Math.Abs(brightness - old) < .015) return;
        _spectrumBackdropBrightness = brightness;
        UpdateSpectrumColors(animate: true);
    }

    private void UpdateSpectrumColors(bool animate)
    {
        bool adjusted = _spectrumBackdropBrightness.HasValue;
        if (adjusted && !PillSpectrum.ColorsAdaptedToBackground && animate)
        {
            // Start the first adaptive fade at the colour actually being drawn,
            // before disabling the legacy fallback lightness limit.
            foreach (var property in new[] { SpectrumVisualizer.ColorLowProperty, SpectrumVisualizer.ColorMidProperty,
                SpectrumVisualizer.ColorHighProperty, SpectrumVisualizer.ColorExtraProperty })
            {
                var rendered = SpectrumVisualizer.ReadableAlbumColor((Color)PillSpectrum.GetValue(property));
                AnimationClockLifetime.Begin(PillSpectrum, property, null);
                PillSpectrum.SetCurrentValue(property, rendered);
            }
        }
        PillSpectrum.ColorsAdaptedToBackground = adjusted;
        Color Resolve(int index) => _spectrumBackdropBrightness is double background
            ? CoverPalette.AgainstBackground(_spectrumAlbumRamp[index], background) : _spectrumAlbumRamp[index];
        Color low = Resolve(0), mid = Resolve(1), high = Resolve(2), extra = Resolve(3);
        SetSpectrumColor(PillSpectrum, SpectrumVisualizer.ColorLowProperty, low, animate);
        SetSpectrumColor(PillSpectrum, SpectrumVisualizer.ColorMidProperty, mid, animate);
        SetSpectrumColor(PillSpectrum, SpectrumVisualizer.ColorHighProperty, high, animate);
        SetSpectrumColor(PillSpectrum, SpectrumVisualizer.ColorExtraProperty, extra, animate);
        SetSpectrumColor(CardWave, WaveVisualizer.ColorLowProperty, low, animate);
        SetSpectrumColor(CardWave, WaveVisualizer.ColorMidProperty, mid, animate);
        SetSpectrumColor(CardWave, WaveVisualizer.ColorHighProperty, high, animate);
        SetSpectrumColor(CardWave, WaveVisualizer.ColorExtraProperty, extra, animate);
    }

    private static void SetSpectrumColor(FrameworkElement target, DependencyProperty property, Color color, bool animate)
    {
        var previous = (Color)target.GetValue(property);
        if (previous == color && !target.HasAnimatedProperties) return;
        if (!animate || !target.IsVisible || MotionPolicy.InstantMorph || MotionPolicy.PowerSave)
        {
            AnimationClockLifetime.Begin(target, property, null);
            target.SetCurrentValue(property, color);
            return;
        }
        AnimationClockLifetime.Begin(target, property, null);
        target.SetCurrentValue(property, previous);
        AnimationClockLifetime.Begin(target, property, new ColorAnimation(previous, color, TimeSpan.FromMilliseconds(480))
        { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }
}
