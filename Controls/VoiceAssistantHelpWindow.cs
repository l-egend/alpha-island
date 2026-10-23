using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Services;
using Button = System.Windows.Controls.Button;

namespace alpha_island.Controls;

internal sealed class VoiceAssistantHelpWindow : Window
{
    internal readonly TextBlock Detail;
    internal readonly Button SettingsButton, RetryButton;
    internal VoiceAssistantHelpWindow(VoiceRecognitionProblem problem, Action openSettings, Action retry)
    {
        Title = problem.Title; Width = 460; MinWidth = 360; MaxHeight = 500;
        SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanMinimize;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/alpha-island;component/Themes/LiquidGlass.xaml") });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/alpha-island;component/Themes/Controls.xaml") });
        SetResourceReference(ForegroundProperty, "TextPrimary");
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 26, 32));
        var content = new StackPanel { Margin = new Thickness(22), Background = Background };
        content.Children.Add(new TextBlock { Text = problem.Title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        Detail = new TextBlock { Text = problem.Detail, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 22 };
        content.Children.Add(new ScrollViewer { Content = Detail, MaxHeight = 290, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var actions = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        SettingsButton = new Button { Content = "打开对应设置", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 9, 0) };
        RetryButton = new Button { Content = "重新尝试", Padding = new Thickness(12, 6, 12, 6) };
        SettingsButton.SetResourceReference(StyleProperty, "ThemedButton"); RetryButton.SetResourceReference(StyleProperty, "ThemedButton");
        SettingsButton.Click += (_, _) =>
        {
            try { openSettings(); }
            catch (Exception) { Detail.Text = problem.Detail + "\n\n无法自动打开系统设置，请按 Win+I 手动检查语音组件或麦克风权限。"; }
        };
        RetryButton.Click += (_, _) => { Close(); retry(); };
        actions.Children.Add(SettingsButton); actions.Children.Add(RetryButton); content.Children.Add(actions);
        Content = content;
        PreviewKeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Escape) { Close(); args.Handled = true; } };
    }
}
