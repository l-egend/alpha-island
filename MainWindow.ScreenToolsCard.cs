using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Controls;
using alpha_island.Core;

using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
namespace alpha_island;

public partial class MainWindow
{
    private Grid _screenToolsCard = null!;

    private void RegisterScreenToolsCard()
    {
        var view = new Grid { Height = 272, VerticalAlignment = VerticalAlignment.Center, RenderTransform = new TranslateTransform() };
        _screenToolsCard = view;
        view.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        view.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        view.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        view.Children.Add(new TextBlock { Text = "屏幕工具", FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White, Margin = new Thickness(4, 2, 4, 8) });
        var actions = new Grid();
        actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (double height in new[] { 58d, 58d, 40d, 40d }) actions.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });
        ToolActionButton Add(int row, int column, string title, string glyph, string? detail, Action action)
        {
            var button = new ToolActionButton(title, glyph, detail, action);
            Grid.SetRow(button, row); Grid.SetColumn(button, column); actions.Children.Add(button); return button;
        }
        Add(0, 0, "识字并复制", "\uE8A5", "框选文字 · 自动复制", () => LaunchScreenCapture(false));
        Add(0, 1, "颜色吸管", "\uE790", "获取 HEX / RGB 色值", () => LaunchScreenCapture(true));
        Add(1, 0, "翻译与字幕", "\uE8C1", "文本翻译 · 字幕 · 扫码", OpenScreenTools);
        Add(1, 1, "快速启动", "\uE8A7", "打开应用、文件与网址", ShowQuickLauncher);
        Add(2, 0, "管理提醒", "\uE916", null, () => OpenAssistantCenter());
        Add(2, 1, "学习与健康", "\uE9D9", null, () => OpenAssistantCenter("habits"));
        _externalTranslationButton = Add(3, 0, "调用外部翻译", "\uE8C1", null, async () => await InvokeExternalTranslationAsync());
        Add(3, 1, "快捷键设置", "\uE765", null, OpenExternalTranslationSettings);
        Grid.SetRow(actions, 1); view.Children.Add(actions);
        _externalTranslationHint = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5, 6, 5, 0), VerticalAlignment = VerticalAlignment.Top };
        Grid.SetRow(_externalTranslationHint, 2); view.Children.Add(_externalTranslationHint);
        RefreshExternalTranslation();
        CardViewport.Children.Add(view);
        _pages.Register(new DelegateIslandPage("screen-tools", "屏幕工具", view, 10, 292,
            viewportChanged: height => view.Height = height));
    }
}
