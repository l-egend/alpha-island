using L = AlphaIsland.PluginSdk.Localization.UiText;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    internal static readonly Uri ProjectHomepage = new("https://github.com/l-egend/alpha-island");

    private void AddProjectAboutSettings()
    {
        AboutText.Text = L.T("alpha island 将媒体、通知和常用工具整合到桌面顶部，提供可自定义的状态提示与快捷操作。");
        ProjectHomepageButton.Content = L.T("GitHub 项目主页");
        ProjectHomepageButton.Tag = ProjectHomepage;
        ProjectHomepageButton.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ProjectHomepage.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception) { DiagnosticsHint.Text = L.T("暂时无法打开浏览器，请稍后重试。"); }
        };
        DiagnosticsHint.Text = L.T("需要排查问题时，可导出诊断包。");
    }
}
