using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using alpha_island.Controls;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Services;
using Button = System.Windows.Controls.Button;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;

internal static partial class Program
{
    private static void WeatherContentLayoutChecks(alpha_island.MainWindow window, Grid root)
    {
        string folder = "bin/WeatherStack170/previews/" + typeof(alpha_island.App).Assembly.GetName().Version!.ToString(3);
        Directory.CreateDirectory(folder); MotionPolicy.Apply(true, false);
        SaveStackDepthPreviews(window, folder);
        SetAgentField(window, "_weatherBusy", true);
        try
        {
            PrivateCall(window, "SetState", IslandState.Card, true, "weather"); PumpAnimations(30);
            var city = new WeatherCity("广州市", 23.13, 113.26);
            var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)).AddDays(1);
            var weather = new WeatherSnapshot(city, 26, 28, 3, 22, 29, DateTime.UtcNow)
            { UtcOffsetSeconds = 28800, Days = [new(tomorrow, 63, 21, 27, 80, 8, 12, 18)] };
            var query = AgentField<TextBox>(window, "_weatherQuery"); query.Text = city.Name;
            DependencyObject view = query;
            while (VisualTreeHelper.GetParent(view) is { } parent && !ReferenceEquals(parent, window.CardViewport)) view = parent;
            bool contained = true;
            foreach (var (source, name) in new[] { ("已更新 13:30", "loaded"), (string.Join("；", Enumerable.Repeat("连接暂时失败，可点击刷新重试", 8)), "long-status") })
            {
                PrivateCall(window, "ShowWeather", weather, source); root.UpdateLayout();
                foreach (var button in Descendants<Button>(view))
                {
                    var bounds = button.TransformToAncestor(window.CardViewport).TransformBounds(new Rect(button.RenderSize));
                    bool fits = bounds.Top >= 0 && bounds.Bottom <= window.CardViewport.ActualHeight - 10;
                    contained &= fits;
                    Console.WriteLine($"Weather {name}/{button.Content}: bottom={bounds.Bottom:0.0}, viewport={window.CardViewport.ActualHeight:0.0}, safe={fits}");
                }
                SaveThemePreview(window.IslandBody, folder + "/weather-" + name + ".png");
            }
            Check(contained, "完整天气与较长状态说明下，所有操作按钮均位于卡片底部安全边距内");
        }
        finally { SetAgentField(window, "_weatherBusy", false); }
    }

    private static void SaveStackDepthPreviews(alpha_island.MainWindow main, string folder)
    {
        var messages = Enumerable.Range(0, 3).Select(index => new DesktopNotification("depth-" + index, "fixture", "通知演示",
            "设备上线提醒", "host 已上线", DateTimeOffset.Now.AddMinutes(-index))).ToArray();
        var group = NotificationStackGroup.Create(messages).Single();
        foreach (var (color, name) in new[] { (System.Windows.Media.Color.FromRgb(28, 29, 33), "dark"), (System.Windows.Media.Color.FromRgb(139, 144, 148), "light") })
        {
            var stack = new DesktopNotificationStackCard(group, _ => throw new InvalidOperationException("Preview stays collapsed"));
            var backdrop = new Border { Background = new SolidColorBrush(color), Padding = new Thickness(18), Child = stack, Resources = main.Resources };
            using var host = new HwndSource(new HwndSourceParameters("Stack depth preview")
            { Width = 430, Height = 150, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
            host.RootVisual = backdrop; backdrop.UpdateLayout();
            SaveThemePreview(backdrop, folder + "/notification-depth-" + name + ".png");
            host.RootVisual = null;
        }
    }
}
