using System.Windows;
using System.Windows.Controls;

namespace AlphaIsland.Plugins.ApiChat;

public partial class ApiSettingsView
{
    private readonly Dictionary<string, CheckBox> _assistantOptions = new();
    private void AddAssistantSettings()
    {
        var content = new StackPanel { Margin = new Thickness(16) };
        foreach (var option in new[] {
            ("AiToolsEnabled", "允许助手调用工具", "仅在需要时查询信息或按你的指令控制桌面；宿主总开关关闭时停止执行。"),
            ("AiDesktopContextEnabled", "允许按需读取岛内状态", "需模型支持工具调用；仅调用时提供相关数据，不自动附加状态包，读取结果可在对话中查看。"),
            ("AiSensitiveSendGuardEnabled", "发送敏感内容前提醒", "本地检测密钥、银行卡和身份证；历史保存脱敏版本。") })
        {
            var check = new CheckBox { Name = option.Item1 + "Toggle", Style = (Style)FindResource("W11Switch"), Margin = new Thickness(0, 6, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Left, ToolTip = option.Item3 };
            var row = new Grid(); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var labels = new StackPanel(); labels.Children.Add(new TextBlock { Text = option.Item2, Style = (Style)FindResource("AiLabel") });
            labels.Children.Add(new TextBlock { Text = option.Item3, Style = (Style)FindResource("AiHint"), MaxWidth = 400, Margin = new Thickness(0, 2, 15, 10) });
            row.Children.Add(labels); Grid.SetColumn(check, 1); row.Children.Add(check); content.Children.Add(row);
            _assistantOptions.Add(option.Item1, check);
            check.Checked += (_, _) => ApplyChanges(); check.Unchecked += (_, _) => ApplyChanges();
        }
        AiSettingsRoot.Children.Add(new Border { Style = (Style)FindResource("W11Card"), Child = content, Margin = new Thickness(0, 12, 0, 0) });
    }
}
