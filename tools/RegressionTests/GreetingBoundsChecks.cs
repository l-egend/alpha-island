using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using Grid = System.Windows.Controls.Grid;

internal static partial class Program
{
    private static void GreetingBoundsChecks(alpha_island.MainWindow main, Grid root, string previews)
    {
        var options = AgentField<IslandOptions>(main, "_options");
        MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
        try
        {
            foreach (string id in new[] { GreetingPresetCatalog.Nailong, GreetingPresetCatalog.Amiya, GreetingPresetCatalog.HoshinoSwimsuit, GreetingPresetCatalog.Logo })
            {
                GreetingPresetCatalog.Select(options, id);
                PrivateCall(main, "ApplyGreetingMedia"); PrivateCall(main, "PreviewStartupGreeting");
                PumpAnimations(400); root.UpdateLayout();
                AgentField<System.Windows.Threading.DispatcherTimer?>(main, "_greetingImageTimer")?.Stop();
                var portrait = AgentField<GreetingPortraitAdorner?>(main, "_greetingPortrait");
                var body = main.IslandBody.TransformToAncestor(root).TransformBounds(new Rect(main.IslandBody.RenderSize));
                var frames = AgentField<GreetingGifFrame[]>(main, "_greetingFrames");
                var source = main.GreetingCustomImage.TransformToAncestor(root).TransformBounds(new Rect(main.GreetingCustomImage.RenderSize));
                double radius = Math.Min(main.IslandBody.CornerRadius.BottomLeft, body.Height / 2);
                int width = (int)Math.Ceiling(root.ActualWidth), height = (int)Math.Ceiling(root.ActualHeight);
                int outside = 0, above = 0;
                BitmapSource Capture()
                {
                    root.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root); return bitmap;
                }
                foreach (int index in new[] { 0, frames.Length / 3, frames.Length * 2 / 3, Math.Max(0, frames.Length - 1) }.Distinct())
                {
                    if (frames.Length > 0) main.GreetingCustomImage.Source = frames[index].Image;
                    portrait?.InvalidateVisual();
                    if (portrait != null) portrait.Visibility = Visibility.Visible; else main.GreetingIcon.Opacity = 1;
                    var visible = Capture(); var pixels = new byte[width * height * 4]; visible.CopyPixels(pixels, width * 4, 0);
                    if (portrait != null) portrait.Visibility = Visibility.Collapsed; else main.GreetingIcon.Opacity = 0;
                    var baseline = Capture(); var hidden = new byte[pixels.Length]; baseline.CopyPixels(hidden, width * 4, 0);
                    for (int y = Math.Max(0, (int)body.Top - 25); y < Math.Min(height, body.Bottom + 18); y++)
                    for (int x = Math.Max(0, (int)body.Left - 24); x < Math.Min(width, source.Right + 28); x++)
                    {
                        int offset = (y * width + x) * 4;
                        if (!Enumerable.Range(0, 4).Any(channel => Math.Abs(pixels[offset + channel] - hidden[offset + channel]) > 3)) continue;
                        if (y + .5 < body.Top - 1) above++;
                        if (y + .5 < body.Top + body.Height / 2) continue;
                        double cx = Math.Clamp(x + .5, body.Left + radius, body.Right - radius), cy = body.Bottom - radius;
                        bool escaped = y + .5 > body.Bottom + 1 || x + .5 < body.Left - 1 || x + .5 > body.Right + 1
                            || y + .5 > cy && Math.Pow(x + .5 - cx, 2) + Math.Pow(y + .5 - cy, 2) > Math.Pow(radius + 1, 2);
                        if (escaped) outside++;
                    }
                    if (portrait != null) portrait.Visibility = Visibility.Visible; else main.GreetingIcon.Opacity = 1;
                    if (index == 0)
                    {
                        int left = Math.Max(0, (int)body.Left - 24), top = Math.Max(0, (int)body.Top - 25);
                        var crop = new CroppedBitmap(visible, new Int32Rect(left, top,
                            Math.Min(width - left, (int)body.Width + 48), Math.Min(height - top, (int)body.Bottom - top + 20)));
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(crop));
                        using var file = File.Create(Path.Combine(previews, "greeting-" + id + ".png")); encoder.Save(file);
                    }
                }
                Console.WriteLine($"Welcome {id}: lower escaped pixels={outside}, upper protruding pixels={above}");
                Check(outside == 0, id + " 欢迎形象的下半部分始终位于圆角轮廓内");
                if (portrait != null)
                {
                    var clip = (Geometry)PrivateCall(portrait, "LowerContourClip")!;
                    var upper = main.IslandBody.TranslatePoint(new System.Windows.Point(30, -4), main.GreetingCustomImage);
                    Check(clip.FillContains(upper), id + " 的裁切保留上方出框区域");
                }
                else Check(main.GreetingIcon.ActualHeight <= body.Height, "默认项目 Logo保留原有尺寸与安全边距");
                PrivateCall(main, "DismissGreeting");
            }
        }
        finally { PrivateCall(main, "DismissGreeting"); MotionPolicy.Apply(true, false); }
    }
}
