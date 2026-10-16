using System.IO;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Services;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    private void AddFirstUseSettings()
    {
        var panel = new StackPanel { Tag = "首次使用 开箱即用 权限 授权 自动检测 联动" };
        panel.Children.Add(new TextBlock { Text = "本机联动与首次使用", Style = (Style)FindResource("W11CardTitle"), Margin = new Thickness(2, 20, 0, 8) });
        var body = new StackPanel { Margin = new Thickness(16) };
        var software = new TextBlock { Text = "打开此页时检测本机联动应用。", Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap };
        body.Children.Add(software);
        body.Children.Add(new TextBlock { Text = "媒体、网络、剪贴板、蓝牙及硬件监测使用本机系统和驱动自动发现，不需要填写软件目录。功耗只显示设备能够提供的真实读数。\n天气首次使用需允许系统定位或选择城市；通知读取需 Windows 授权；精确开机耗时在启用自启动时申请日志读取权限。\n岛内 AI 需要你提供接口与密钥；密钥使用 Windows 当前用户加密，迁移到其他电脑后需重新输入。",
            Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, action) in new (string, Action)[] {
            ("天气与定位", OpenWeatherSettings), ("通知授权", () => SelectPageByTag("notifications")),
            ("AI 与 Agent", () => SelectPageByTag("ai")), ("插件管理", () => SelectPageByTag("plugins")),
            ("自启动", () => SelectPageByTag("behavior")) })
        {
            var button = new Button { Content = label, Style = (Style)FindResource("W11Button"), Margin = new Thickness(0, 0, 8, 8) };
            button.Click += (_, _) => action(); actions.Children.Add(button);
        }
        body.Children.Add(actions);
        panel.Children.Add(new Border { Child = body, Style = (Style)FindResource("W11Card") });
        Page_About.Children.Insert(1, panel);
        Page_About.IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is not true || !_persistSettings) return;
            var detected = await Task.Run(() => (Youdao: ExternalTranslationLauncher.FindYoudao() != null,
                Vivo: File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pcsuite", "historyPhone.json"))));
            software.Text = "网易有道翻译：" + (detected.Youdao ? "已检测到，调用时可自动启动" : "未检测到，可安装后直接调用") +
                "\nvivo 办公套件：" + (detected.Vivo ? "已检测到本机连接记录，启用插件后自动读取" : "安装并连接手机后由插件自动读取") +
                "\nOPPO 耳机：插件直接识别兼容蓝牙耳机，无需运行原管理软件。";
        };
    }
}
