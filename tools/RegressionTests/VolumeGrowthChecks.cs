using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void VolumeGrowthUiChecks(alpha_island.MainWindow window, Grid root)
    {
        bool reduce = MotionPolicy.ReduceMotion, power = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        var icon = new SemanticIcon { Shape = IconShape.Volume, Width = 64, Height = 64, Active = true, Level = 0,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        root.Children.Add(icon); root.UpdateLayout(); PumpAnimations(30);
        var transform = icon.RenderTransform;
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            foreach (var (percent, expected) in new[] { (1, 1), (25, 1), (32, 1), (33, 2), (50, 2), (65, 2), (66, 3), (90, 3), (100, 3) })
            {
                icon.Level = percent / 100d; ActionIconMotion.Stop(icon); root.UpdateLayout(); PumpAnimations(20);
                var stable = SemanticIconPixels(icon);
                SaveVolumeBandPreview(icon, $"bin/icon-previews/volume-band-{percent}.png");
                Check(VolumeWaveRuns(stable) == expected, $"音量 {percent}% 稳定显示 {expected} 条声波（实际 {VolumeWaveRuns(stable)}）");
            }
            icon.Level = 0; ActionIconMotion.Stop(icon); root.UpdateLayout(); PumpAnimations(20);
            _ = SemanticIconPixels(icon);
            icon.Level = 1; ActionIconMotion.Play(icon, ActionIconAnimation.Tick);
            var progress = (DependencyProperty)typeof(SemanticIcon).GetField("ProgressProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            icon.BeginAnimation(progress, null);
            byte[]? earlier = null;
            foreach (double fraction in new[] { .28, .52, .8 })
            {
                icon.SetValue(progress, fraction); root.UpdateLayout();
                // A fixed timer can return before WPF has rebuilt the retained drawing.
                icon.InvalidateVisual();
                icon.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                var pixels = SemanticIconPixels(icon);
                int top = 0, bottom = 0;
                for (int y = 0; y < 64; y++) for (int x = 34; x < 64; x++)
                    if (pixels[(y * 64 + x) * 4 + 3] > 80) { if (y < 32) top++; else bottom++; }
                Check(top > 4 && bottom > 4 && Math.Abs(top - bottom) <= Math.Max(5, (top + bottom) * .06),
                    $"音量过渡 {fraction:F2} 的每条声波保留完整对称轮廓（上 {top} / 下 {bottom}）");
                Check(VolumeWaveRuns(pixels) == (fraction < 1d / 3 ? 1 : fraction < 2d / 3 ? 2 : 3),
                    $"声波过渡 {fraction:F2} 按由内到外的顺序逐条出现（实际 {VolumeWaveRuns(pixels)}，进度 {icon.TransitionProgress:0.00}）");
                if (earlier != null) Check(SemanticPixelDifference(earlier, pixels) > 10, "连续音量阶段有实际几何变化");
                earlier = pixels;
                SaveVolumeBandPreview(icon, $"bin/icon-previews/volume-growth-{fraction:F2}.png");
            }
            ActionIconMotion.Stop(icon); _ = SemanticIconPixels(icon);
            icon.Active = false; ActionIconMotion.Play(icon, ActionIconAnimation.Disconnect); PumpAnimations(1550);
            Check(!icon.HasAnimatedProperties && !ActionIconMotion.GetIsAnimating(icon)
                && ReferenceEquals(icon.RenderTransform, transform), "静音过渡结束释放时钟，喇叭与图标位置保持稳定");
            VolumeLiveDragChecks(window, root);
            var muted = SemanticIconPixels(icon);
            Check(VolumeInk(muted, 64, 53, 20, 61, 27) > 4 && VolumeInk(muted, 64, 35, 37, 44, 46) > 4,
                "静音喇叭右侧是两条交叉笔画组成的 ×");
            SaveVolumeBandPreview(icon, "bin/icon-previews/volume-muted-x.png");
            icon.Active = true; icon.Level = 1; ActionIconMotion.Stop(icon); root.UpdateLayout(); PumpAnimations(20);
            var loud = SemanticIconPixels(icon);
            Check(VolumeWaveRuns(loud) == 3, "最大音量时喇叭右侧显示三条独立完整声波");
            SaveVolumeBandPreview(icon, "bin/icon-previews/volume-three-waves.png");
            icon.Level = .2; ActionIconMotion.Play(icon, ActionIconAnimation.Tick); icon.BeginAnimation(progress, null);
            foreach (var (fraction, expected) in new[] { (.25, 3), (.75, 2), (1d, 1) })
            {
                icon.SetValue(progress, fraction); root.UpdateLayout();
                // A fixed timer can return before WPF has rebuilt the retained drawing.
                icon.InvalidateVisual();
                icon.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                Check(VolumeWaveRuns(SemanticIconPixels(icon)) == expected,
                    $"降低音量过渡 {fraction:F2} 由外到内逐条收回，保留 {expected} 条声波");
            }
            ActionIconMotion.Stop(icon);
        }
        finally
        {
            ActionIconMotion.Stop(icon); root.Children.Remove(icon);
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduce, power);
        }
    }

    private static int VolumeWaveRuns(byte[] pixels)
    {
        int waves = 0; bool ink = false;
        for (int x = 33; x < 64; x++)
        {
            bool next = pixels[(32 * 64 + x) * 4 + 3] > 80;
            if (next && !ink) waves++;
            ink = next;
        }
        return waves;
    }

    private static void SaveVolumeBandPreview(SemanticIcon icon, string path)
    {
        // Render local 64px coordinates directly. Auto-normalizing a VisualBrush to
        // its ink bounds changes the apparent speaker width when wave count changes.
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(icon);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static int VolumeInk(byte[] pixels, int width, int left, int top, int right, int bottom)
    {
        int count = 0;
        for (int y = top; y < bottom; y++) for (int x = left; x < right; x++)
            if (pixels[(y * width + x) * 4 + 3] > 80) count++;
        return count;
    }

    private static void VolumeLiveDragChecks(alpha_island.MainWindow window, Grid root)
    {
        var oldService = AgentField<VolumeService?>(window, "_volumeService");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var pages = AgentField<IslandPageHost>(window, "_pages");
        var oldState = AgentField<IslandState>(window, "_state");
        string oldPage = pages.ActiveId;
        using var service = new VolumeService(window.Dispatcher); // Unstarted: never changes real system audio.
        try
        {
            SetAgentField(window, "_volumeService", service);
            foreach (bool compact in new[] { false, true })
            {
                layers.HideAll(); service.SetVolume(20);
                PrivateCall(window, "SetState", compact ? IslandState.Pill : IslandState.Card, true, IslandPageIds.Volume);
                if (compact) PrivateCall(window, "ShowVolumeNotify", 20, false);
                else PrivateCall(window, "SyncVolumeCardContent");
                root.UpdateLayout(); PumpAnimations(250);
                var track = compact ? window.VolumeTrackContainer : window.CardVolumeSliderTrack;
                var owner = compact ? window.VolumeIcon : window.CardVolumeIcon;
                foreach (bool increase in new[] { true, false })
                {
                    int start = increase ? 20 : 90;
                    PrivateCall(window, "BeginVolumeDrag", track, track.ActualWidth * start / 100, false);
                    ActionIconMotion.Stop(owner); root.UpdateLayout(); PumpAnimations(20);
                    var before = MotionIconPixels(owner);
                    for (int step = 1; step <= 7; step++)
                    {
                        int percent = start + (increase ? 10 : -10) * step;
                        PrivateCall(window, "UpdateVolumeFromSliderPosition", track.ActualWidth * percent / 100);
                        root.UpdateLayout(); PumpAnimations(20);
                    }
                    var during = MotionIconPixels(owner);
                    int delta = SemanticPixelDifference(before, during);
                    Check(AgentField<bool>(window, "_isDraggingVolumeSlider") && delta > 8,
                        $"{(compact ? "小岛" : "卡片")}连续{(increase ? "增加" : "减少")}音量时，松手前图标已有明显过渡（变化像素 {delta}）");
                    SaveThemePreview(owner, $"bin/icon-previews/volume-live-{(compact ? "pill" : "card")}-{(increase ? "up" : "down")}.png");
                    PrivateCall(window, "EndVolumeDrag"); PumpAnimations(350);
                    Check(!ActionIconMotion.GetIsAnimating(owner) && !owner.HasAnimatedProperties,
                        "调节结束后及时释放声波动画时钟");
                }
            }
        }
        finally
        {
            PrivateCall(window, "EndVolumeDrag");
            ActionIconMotion.Stop(window.VolumeIcon); ActionIconMotion.Stop(window.CardVolumeIcon);
            SetAgentField(window, "_volumeService", oldService);
            layers.HideAll(); PrivateCall(window, "SetState", oldState, true, oldPage);
        }
    }
}
