using System.IO;
using System.Text.Json;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void PortableSetupChecks(string root)
    {
        string path = Path.Combine(root, "portable-config", "options.json");
        Check(IslandOptions.Load(path).IsFreshInstallation, "没有设置文件时识别首次安装");
        var fresh = IslandOptions.Load(path);
        Check(SettingsRegistry.GetBool(fresh, "AgentBridgeEnabled", false), "未安装外部 Agent 的首次运行也准备好桥接，稍后安装无需手动打开");
        Check(!fresh.Extras.Keys.Any(key => key.StartsWith("plugin/", StringComparison.Ordinal)), "首次安装不预置插件配置");
        Check(!alpha_island.Plugins.PluginHost.IsEnabled(fresh, "oppo.pods") && !alpha_island.Plugins.PluginHost.IsEnabled(fresh, "vivo.office"), "首次安装不自动启用未安装的设备插件");
        fresh.Save(path);
        Check(SettingsRegistry.GetBool(IslandOptions.Load(path), "AgentBridgeEnabled", false), "重启后保留首次安装准备的桥接");
        var options = new IslandOptions();
        SettingsRegistry.SetExtra(options, "AgentBridgeEnabled", false);
        options.Extras[alpha_island.Plugins.PluginHost.EnabledKey("oppo.pods")] = JsonSerializer.SerializeToElement(false);
        options.Save(path);
        var existing = IslandOptions.Load(path);
        Check(!existing.IsFreshInstallation && !SettingsRegistry.GetBool(existing, "AgentBridgeEnabled", true), "更新保留用户明确关闭的桥接");
        Check(!alpha_island.Plugins.PluginHost.IsEnabled(existing, "oppo.pods"), "升级不重新启用已停用的设备插件");
        File.WriteAllText(path, "{broken");
        Check(!IslandOptions.Load(path).IsFreshInstallation, "损坏配置不能被误认为首次安装并启用功能");
        File.Delete(path + ".bak");
        File.WriteAllText(path, "{broken"); // The preceding load deliberately recovered the main file from its backup.
        var corrupt = IslandOptions.Load(path);
        Check(!SettingsRegistry.GetBool(corrupt, "AgentBridgeEnabled", false) && !alpha_island.Plugins.PluginHost.IsEnabled(corrupt, "vivo.office"), "无有效备份的损坏配置不自动开启联动");
        Check(!JsonSerializer.Serialize(existing).Contains("IsFreshInstallation", StringComparison.Ordinal), "首次运行标记不写入可导出的设置");
        Check(ExternalTranslationLauncher.IsYoudao("网易有道翻译") && ExternalTranslationLauncher.IsYoudao("Youdao"), "识别有道中英文名称");
        Check(!ExternalTranslationLauncher.IsYoudao("其他翻译软件"), "通用快捷键调用保留其他翻译软件设置");
        string folder = Path.Combine(root, "another disk and 中文", "Youdao", "Dict");
        Directory.CreateDirectory(folder);
        string executable = Path.Combine(folder, "YoudaoDict.exe");
        File.WriteAllText(executable, "discovery fixture, never executed");
        Check(ExternalTranslationLauncher.FindInDirectory(folder) == executable, "发现任意盘符与中文目录中的有道可执行文件");
        Check(ExternalTranslationLauncher.FindInDirectory(Path.GetDirectoryName(folder)) == executable, "发现注册表所指安装根目录中的子目录");
        Check(ExternalTranslationLauncher.FindInDirectory(null) == null && ExternalTranslationLauncher.FindInDirectory("relative") == null,
            "未知或相对安装目录不被当成有效应用路径");
        Check(new AgentIntegrationInfo("codex", "Codex", true, "scope-required", "", "", "").StatusText.Contains("项目目录"), "项目范围需要选择时有明确入口提示");
        Check(new AgentIntegrationInfo("dsh", "DSH", false, "not-installed", "", "", "").StatusText.Contains("未检测"), "可选应用缺失时显示正常未安装状态");
    }
}
