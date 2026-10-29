using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Services;
using TextBox = System.Windows.Controls.TextBox;

internal static partial class Program
{
    private static async Task DistributionDefaultCoreChecks(string root)
    {
        var fresh=IslandOptions.Load(System.IO.Path.Combine(root,"fresh","options.json"));
        var translation=ExternalTranslationOptions.From(fresh);
        var input=new ShortcutInputStub();
        var result=await ExternalTranslationLauncher.SendAsync(translation,new ShortcutSimulationService(input),default);
        Check(!translation.Enabled && translation.Name.Length==0 && translation.Shortcut.Length==0 && !result.Sent && input.Batches.Count==0,
            "全新配置不指定外部应用或快捷键，也不会发送系统按键");
        Check(!fresh.Extras.Keys.Any(key=>key.StartsWith("plugin/",StringComparison.Ordinal)),"全新配置不包含可选插件的预置项");
        SettingsRegistry.Write(fresh,"ExternalTranslationEnabled",true);
        SettingsRegistry.Write(fresh,"ExternalTranslationName","Configured app");
        SettingsRegistry.Write(fresh,"ExternalTranslationShortcut","Ctrl+Shift+T");
        string file=System.IO.Path.Combine(root,"existing.json");fresh.Save(file);
        var existing=ExternalTranslationOptions.From(IslandOptions.Load(file));
        Check(existing.Enabled && existing.Name=="Configured app" && existing.Shortcut=="Ctrl+Shift+T","已有外部调用配置独立保留");
        var sections=DiagnosticsReport.Sections();
        Check(sections.Any(s=>s.Title=="运行环境") && sections.Any(s=>s.Title=="能力检测") && sections.Any(s=>s.Title=="最近日志"),
            "关于页精简后诊断仍保留运行环境、能力和日志");
    }

    private static void DistributionDefaultUiChecks(alpha_island.MainWindow window, Grid root)
    {
        var settings=new alpha_island.Settings.SettingsWindow(false){ForceClose=true};
        try{
            settings.LoadFrom(new IslandOptions());
            Check(((TextBox)settings.FindName("ExternalTranslationNameBox")).Text.Length==0
                && ((TextBox)settings.FindName("ExternalTranslationShortcutBox")).Text.Length==0,"默认设置页展示空白应用名和快捷键");
            PrivateCall(settings,"SelectPageByTag",SettingsPageIds.About);PumpAnimations(40);
            var about=(StackPanel)settings.FindName("Page_About");
            string text=string.Join(" ",Descendants<TextBlock>(about).Select(t=>t.Text));
            Check(!new[]{"网易","OPPO","vivo","运行环境","能力检测","服务状态","插件"}.Any(text.Contains),"关于页只展示项目介绍和操作入口");
            var homepage=(System.Windows.Controls.Button)settings.FindName("ProjectHomepageButton");
            Check(homepage.Tag is Uri uri && uri.Scheme=="https" && uri.Host=="github.com" && uri.AbsolutePath=="/l-egend/alpha-island",
                "GitHub 主页按钮使用已确认的项目地址");
            Check(settings.FindName("CapabilityText")==null && settings.FindName("ServiceText")==null && settings.FindName("LogText")==null,
                "运行状态详情不再加载到关于页");
            using var source=ThemeWindowSource(settings,880,680,out var host);
            host.UpdateLayout();PumpAnimations(40);host.UpdateLayout();
            SaveThemePreview(host,"bin/SettingsDistributionAudit/about-zh.png");
        }finally{settings.Close();}
    }
}
