using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Controls;
using alpha_island.Services;

internal static partial class Program
{
    private static void VoiceFailureUiChecks(Grid root)
    {
        const string reason = "请先在 Windows 设置 → 隐私和安全性 → 语音中启用联机语音识别。";
        var pill = new VoiceAssistantPill(); root.Children.Add(pill);
        try
        {
            pill.Update(new(VoiceAssistantState.Error, "", reason)); root.UpdateLayout();
            var text = pill.Transcript;
            var measured = new FormattedText(text.Text, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
                System.Windows.Media.Brushes.White, VisualTreeHelper.GetDpi(text).PixelsPerDip);
            Check(measured.WidthIncludingTrailingWhitespace <= text.ActualWidth + 1,
                "语音失败时的操作说明完整显示，未被录音频谱挤成省略号");
            Check(pill.Wave.Visibility == Visibility.Collapsed && pill.ToolTip is TextBlock tip
                && tip.Text == reason && tip.TextWrapping == TextWrapping.Wrap,
                "失败状态腾出频谱空间，悬停完整原因支持换行");
            foreach (string detail in new[] { "麦克风已断开或被独占，本次录音已停止。", "没有识别到内容，未发送任何 API 请求。", "请先配置并启用 API 对话；锁屏、游戏或审批期间无法唤醒。" })
            {
                pill.Update(new(VoiceAssistantState.Error, "", detail)); root.UpdateLayout();
                var size = new FormattedText(pill.Transcript.Text, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
                    new Typeface(pill.Transcript.FontFamily, pill.Transcript.FontStyle, pill.Transcript.FontWeight, pill.Transcript.FontStretch),
                    pill.Transcript.FontSize, System.Windows.Media.Brushes.White, VisualTreeHelper.GetDpi(pill.Transcript).PixelsPerDip);
                Check(size.WidthIncludingTrailingWhitespace <= pill.Transcript.ActualWidth + 1, "不同语音错误的简短说明均完整可读");
            }
            bool languageMissing = false;
            try { using var unused = VoiceRecognitionService.CreateRecognizer(CultureInfo.GetCultureInfo("qps-ploc")); }
            catch (VoiceRecognitionException error) { languageMissing = error.Problem.SettingsUri == VoiceRecognitionProblem.SpeechSettings && error.Problem.Detail.Contains("语音识别"); }
            Check(languageMissing, "没有对应语言引擎时给出安装说明和正确系统入口，不伪装成麦克风权限错误");
        }
        finally { root.Children.Remove(pill); }
    }
}
