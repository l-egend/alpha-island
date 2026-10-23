using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using Border = System.Windows.Controls.Border;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

internal static partial class Program
{
    private static void WeatherIconUiChecks(alpha_island.MainWindow window, FrameworkElement rootView)
    {
        var root = (Grid)rootView;
        bool reduced = MotionPolicy.ReduceMotion, powerSave = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        var previousState = AgentField<IslandState>(window, "_state");
        var pages = AgentField<IslandPageHost>(window, "_pages");
        string previousPage = pages.ActiveId;
        var previousWeather = AgentField<WeatherSnapshot?>(window, "_weatherSnapshot");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var probes = new List<FrameworkElement>();
        try
        {
            MotionPolicy.Apply(true, false);
            var samples = new (int Code, WeatherIconKind Kind, string Label)[]
            {
                (0, WeatherIconKind.Sunny, "晴"), (2, WeatherIconKind.PartlyCloudy, "晴间多云"),
                (3, WeatherIconKind.Cloudy, "阴"), (45, WeatherIconKind.Fog, "雾"),
                (53, WeatherIconKind.Drizzle, "毛毛雨"), (63, WeatherIconKind.Rain, "雨 / 阵雨"),
                (67, WeatherIconKind.FreezingRain, "冻雨"), (75, WeatherIconKind.Snow, "雪 / 阵雪"),
                (97, WeatherIconKind.Thunderstorm, "雷雨")
            };
            var pixels = new Dictionary<WeatherIconKind, byte[]>();
            var gallery = new UniformGrid { Columns = 3, Rows = 3 };
            var galleryFrame = new Border { Width = 540, Height = 300, Padding = new Thickness(12), CornerRadius = new CornerRadius(24),
                Background = new SolidColorBrush(Color.FromRgb(16, 25, 43)), Child = gallery };
            probes.Add(galleryFrame); root.Children.Add(galleryFrame);
            foreach (var sample in samples)
            {
                Check(WeatherIconPolicy.Resolve(sample.Code) == sample.Kind, sample.Label + "预报码选择正确天气轮廓");
                var icon = new SemanticIcon { Width = 64, Height = 64, Shape = IconShape.Weather, WeatherCode = sample.Code,
                    FontFamily = new FontFamily("AlphaIsland Deliberately Missing Icon Font"), Foreground = Brushes.White };
                var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                cell.Children.Add(icon);
                cell.Children.Add(new TextBlock { Text = sample.Label, FontSize = 12, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
                gallery.Children.Add(cell); root.UpdateLayout();
                var bitmap = MotionIconPixels(icon);
                Check(Enumerable.Range(0, bitmap.Length / 4).Count(i => bitmap[i * 4 + 3] > 100) > 70,
                    sample.Label + "实际 WPF 矢量有可见笔划，不依赖图标字体");
                foreach (var earlier in pixels)
                    Check(SemanticPixelDifference(earlier.Value, bitmap) > 25, sample.Label + "与" + earlier.Key + "实际像素不同");
                pixels.Add(sample.Kind, bitmap);
            }
            foreach (int code in new[] { 51, 55, 56, 57, 61, 65, 66, 71, 73, 77, 80, 81, 82, 85, 86, 95, 96, 99, 1, 48 })
                Check(WeatherIconPolicy.Resolve(code) != WeatherIconKind.Unknown, "已有天气描述的代码有对应轮廓: " + code);
            Check(WeatherIconPolicy.Resolve(null) == WeatherIconKind.Unknown && WeatherIconPolicy.Resolve(4) == WeatherIconKind.Unknown,
                "缺失或未知天气码不冒充晴天");
            Directory.CreateDirectory("bin/icon-previews");
            SaveThemePreview(galleryFrame, "bin/icon-previews/weather-icon-gallery.png");

            var sunny = new WeatherReminder("evening", "广州市 · 明日天气", "明天 晴 · 20°～31°。", 0).ToNotification(DateTimeOffset.Now);
            var preview = new DesktopNotificationPill(); preview.SetNotification(sunny);
            var previewFrame = new Border { Width = 380, Height = 72, CornerRadius = new CornerRadius(36),
                BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromRgb(68, 92, 121)),
                Background = new SolidColorBrush(Color.FromRgb(16, 25, 43)), Child = preview };
            probes.Add(previewFrame); root.Children.Add(previewFrame); root.UpdateLayout();
            SaveThemePreview(previewFrame, "bin/icon-previews/weather-notification.png");
            Check(preview.IconBadge.Visibility == Visibility.Collapsed && preview.ColumnDefinitions[0].ActualWidth == 0
                && WeatherIcons(preview).Count(icon => icon.IsVisible) == 1 && preview.WeatherArtwork.IsVisible,
                "天气提示只显示右侧天气图标，左侧图标与占位一起移除");
            Check(preview.WeatherArtwork.Visibility == Visibility.Visible && preview.WeatherArtwork.WeatherCode == 0
                && preview.WeatherArtwork.TranslatePoint(new System.Windows.Point(), preview).X > preview.Detail.TranslatePoint(new System.Windows.Point(), preview).X,
                "明日天气小岛右侧显示对应太阳图标，标题与温度仍留在左侧");
            var legacy = new SemanticIcon { Width = 64, Height = 64, Text = "\uE9BD", FontFamily = new FontFamily("Missing Weather Font") };
            probes.Add(legacy); root.Children.Add(legacy); root.UpdateLayout();
            var unknownPixels = MotionIconPixels(legacy);
            legacy.WeatherCode = 3; root.UpdateLayout();
            Check(legacy.ResolvedShape == IconShape.Weather && SemanticPixelDifference(unknownPixels, MotionIconPixels(legacy)) <= 3
                && Enumerable.Range(0, unknownPixels.Length / 4).Count(i => unknownPixels[i * 4 + 3] > 100) > 70,
                "旧通知缺少天气码时仍用云形矢量安全降级，不显示缺字方框");

            layers.HideAll(); PrivateCall(window, "ClearDesktopInbox");
            PrivateCall(window, "SetState", IslandState.Bar, true);
            PrivateCall(window, "OnSystemToastsReceived", new[] { sunny }, false); root.UpdateLayout();
            var actual = AgentField<DesktopNotificationPill>(window, "_notificationSummary");
            Check(actual.Icon.Shape == IconShape.Weather && actual.Icon.WeatherCode == 0 && actual.Icon.Source == null
                && actual.Title.Text.Contains("明日天气") && actual.Detail.Text.Contains("20°～31°"),
                "正式通知队列与主岛保留明日晴码和温度，太阳由独立轮廓绘制");
            var history = new DesktopNotificationCard(sunny);
            var historyIcon = WeatherIcons(history).Single();
            Check(historyIcon.Shape == IconShape.Weather && historyIcon.WeatherCode == 0,
                "通知历史也显示明日太阳而非字体方框");
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.System);
            PrivateCall(window, "UpdateSideBubbleHint"); root.UpdateLayout();
            Check(window.SideBubbleIcon.Shape == IconShape.Weather && window.SideBubbleIcon.WeatherCode == 0,
                "卡片旁侧天气通知同步明日天气码");
            var orb = (Border)PrivateCall(window, "CreateSatelliteOrb", "desktoptoast")!;
            probes.Add(orb); root.Children.Add(orb); orb.Visibility = Visibility.Visible; orb.Opacity = 1; root.UpdateLayout();
            PrivateCall(window, "UpdateSatelliteOrbVisual", "desktoptoast", orb);
            var orbIcon = WeatherIcons(orb).Single();
            Check(orbIcon.Shape == IconShape.Weather && orbIcon.WeatherCode == 0, "通知卫星也传递明日晴码");

            var snapshot = new WeatherSnapshot(new("广州", 23.4, 113.46), 26, 27, 63, 20, 28, DateTime.UtcNow);
            SetAgentField(window, "_weatherSnapshot", snapshot);
            PrivateCall(window, "EnsureCardPinLayer", "weather");
            var pin = AgentField<System.Collections.IDictionary>(window, "_cardPinViews")["card/weather"]!;
            var pinIcon = (SemanticIcon)pin.GetType().GetProperty("Icon")!.GetValue(pin)!;
            Check(pinIcon.Shape == IconShape.Weather && pinIcon.WeatherCode == 63,
                "顶置天气卡片显示当前雨码，与明日通知各自保留正确预报时段");
            var weatherOrb = (Border)PrivateCall(window, "CreateSatelliteOrb", "card/weather")!;
            Check(WeatherIcons(weatherOrb).Single().WeatherCode == 63, "天气卡片卫星使用当前天气码");
            preview.SetNotification(sunny with { Kind = DesktopEventKind.WifiRadio, RadioEnabled = true });
            Check(preview.Icon.Shape == IconShape.Wifi && preview.Icon.WeatherCode == null, "通知槽切换到非天气事件清除残留天气码");
            Check(preview.IconBadge.Visibility == Visibility.Visible, "切换为普通通知后恢复左侧图标");
            Check(preview.WeatherArtwork.Visibility == Visibility.Collapsed && preview.WeatherArtwork.WeatherCode == null,
                "切换普通通知后收起右侧天气专用图标，不占用通知文字宽度");

            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            var motionIcon = new SemanticIcon { Width = 64, Height = 64, Shape = IconShape.Weather, WeatherCode = 0 };
            probes.Add(motionIcon); root.Children.Add(motionIcon); root.UpdateLayout(); PumpAnimations(20);
            var originalTransform = motionIcon.RenderTransform;
            ActionIconMotion.Play(motionIcon, ActionIconAnimation.Pulse); PumpAnimations(120);
            var first = MotionIconPixels(motionIcon); PumpAnimations(550);
            int rayDifference = SemanticPixelDifference(first, MotionIconPixels(motionIcon));
            Check(ActionIconMotion.GetIsAnimating(motionIcon) && rayDifference > 12,
                "太阳内部光芒按有限进度显现，不是整个图标旋转或缩放");
            PumpAnimations(900);
            Check(!ActionIconMotion.GetIsAnimating(motionIcon) && !motionIcon.HasAnimatedProperties
                && ReferenceEquals(originalTransform, motionIcon.RenderTransform), "天气短动画结束释放时钟，整体变换不变");
        }
        finally
        {
            foreach (var probe in probes) { ActionIconMotion.Stop(probe); root.Children.Remove(probe); }
            PrivateCall(window, "HideSideBubbleHint", true); PrivateCall(window, "ClearDesktopInbox"); layers.HideAll();
            SetAgentField(window, "_weatherSnapshot", previousWeather);
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduced, powerSave);
            PrivateCall(window, "SetState", previousState, true, previousPage);
        }
    }

    private static IEnumerable<SemanticIcon> WeatherIcons(DependencyObject root)
    {
        if (root is SemanticIcon icon) yield return icon;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in WeatherIcons(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
