using System.IO;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Plugins;
using alpha_island.Services;
using AlphaIsland.Plugins.LocalAgents;
using CheckBox = System.Windows.Controls.CheckBox;

internal static partial class Program
{
    private static void DiagnosticPrivacyChecks(string root)
    {
        const string secret = "fixture-private-api-value";
        string localError = AgentProtocol.Redact("Authorization: Bearer " + secret + "\n{\"api_key\":\"" + secret + "\"}");
        Check(!DiagnosticRedactor.Redact(localError).Contains(secret), "本机 Agent 错误先脱敏再导出时不会遗留失去字段标识的令牌");
        DiagnosticsReport.RegisterSection("隐私回归样例", () => "api_key=" + secret + "\nC:\\Users\\FixturePrivateUser\\PrivateProject\\source.cs");
        string report = DiagnosticsReport.BuildText();
        Check(!report.Contains(secret) && !report.Contains("FixturePrivateUser"), "复制诊断报告会隐藏凭据和用户路径");
        string file = Path.Combine(root, "error-fixture.log");
        File.WriteAllText(file, "HTTP 401 Authorization: Bearer " + secret + "\nD:\\PrivateProject\\private.cs");
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true)) DiagnosticsReport.AddSharedLog(zip, file);
        memory.Position = 0;
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.Entries.Single().Open());
        string log = reader.ReadToEnd();
        Check(!log.Contains(secret) && !log.Contains("PrivateProject") && log.Contains("401"), "诊断 ZIP 内日志也脱敏，同时保留错误码");
        string settings = DiagnosticsReport.MaskJson(JsonSerializer.Serialize(new { apiKey = secret,
            note = "Bearer " + secret, prompt = "fixture-private-persona", workspace = "C:\\Users\\FixturePrivateUser\\ClientProject", enabled = true }));
        Check(!settings.Contains(secret) && !settings.Contains("fixture-private-persona") && !settings.Contains("FixturePrivateUser")
            && settings.Contains("true"), "配置导出隐藏提示词、字段内凭据和个人路径，保留开关");

        using var jsonMemory = new MemoryStream();
        using (var zip = new ZipArchive(jsonMemory, ZipArchiveMode.Create, true))
            typeof(DiagnosticsReport).GetMethod("AddText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, [zip, "options.masked.json", settings]);
        jsonMemory.Position = 0;
        using var jsonArchive = new ZipArchive(jsonMemory, ZipArchiveMode.Read);
        using var jsonReader = new StreamReader(jsonArchive.Entries.Single().Open());
        using var parsed = JsonDocument.Parse(jsonReader.ReadToEnd());
        Check(parsed.RootElement.GetProperty("enabled").GetBoolean(), "导出包中的配置脱敏后仍是有效 JSON，保留可诊断的开关值");

        string matrix = DiagnosticRedactor.Redact("Authorization: Basic fixtureBase64\n--api-key fixtureArgument\n"
            + "https://fixture:fixtureUrlPassword@example.test/path?token=fixtureQuery#fixtureFragment\n"
            + "-----BEGIN PRIVATE KEY-----\nfixturePem\n-----END PRIVATE KEY-----\n"
            + "client=192.168.2.20 owner=fixture@example.test mac=00:11:22:33:44:55\n"
            + "persona=fixturePersona\n at Fixture.Frame in C:\\PrivateProject\\file.cs:line 73\nHTTP 403 CODEX_CLI_NOT_FOUND");
        Check(!new[] { "fixtureBase64", "fixtureArgument", "fixtureUrlPassword", "fixtureQuery", "fixtureFragment", "fixturePem", "192.168.2.20", "fixture@example.test", "00:11:22:33:44:55", "fixturePersona", "PrivateProject" }.Any(matrix.Contains)
            && matrix.Contains(":line 73") && matrix.Contains("403") && matrix.Contains("CODEX_CLI_NOT_FOUND"),
            "常见凭据、查询参数和个人标识脱敏，同时保留调用帧行号与定位代码");

        string largeLog = Path.Combine(root, "error-large.log");
        File.WriteAllText(largeLog, "fixture-old-sensitive-content\n" + new string('x', 1_100_000) + "\nHTTP 429 token=" + secret);
        using var boundedMemory = new MemoryStream();
        using (var zip = new ZipArchive(boundedMemory, ZipArchiveMode.Create, true)) DiagnosticsReport.AddSharedLog(zip, largeLog);
        boundedMemory.Position = 0;
        using var boundedArchive = new ZipArchive(boundedMemory, ZipArchiveMode.Read);
        using var boundedReader = new StreamReader(boundedArchive.Entries.Single().Open());
        string bounded = boundedReader.ReadToEnd();
        Check(bounded.Length < 1_048_700 && !bounded.Contains("fixture-old-sensitive-content") && !bounded.Contains(secret) && bounded.Contains("429"),
            "大日志仅打包有限尾部，依然执行脱敏并保留最新错误");

        string native = Path.Combine(root, "native"), data = Path.Combine(root, "data"), output = Path.Combine(root, "export");
        Directory.CreateDirectory(native); Directory.CreateDirectory(Path.Combine(data, "config"));
        File.WriteAllText(Path.Combine(native, "config.toml"), "[features]\nhooks = false\n");
        File.WriteAllText(Path.Combine(native, "hooks.json"), "{\"hooks\":{}}");
        using var reserve = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); reserve.Start();
        int diagnosticPort = ((System.Net.IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        using var diagnosticBridge = new AgentBridgeService(diagnosticPort, _ => false) { NativeStatusAllowed = () => false }; diagnosticBridge.Start();
        using var diagnosticHttp = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { UseProxy = false });
        using var stateReport = diagnosticHttp.PostAsync(diagnosticBridge.Endpoint + "status", new System.Net.Http.StringContent(
            "{\"source\":\"codex\",\"session\":\"diagnostic-input\",\"state\":\"input-wait\",\"detail\":\"Codex\"}", System.Text.Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        Check(stateReport.IsSuccessStatusCode, "诊断样本接入独立的等待回复状态通道");
        File.WriteAllText(Path.Combine(data, "config", "options.json"), JsonSerializer.Serialize(new { Extras = new { AgentBridgeEnabled = true, AgentStatusEnabled = true, AgentBridgePort = diagnosticPort, apiKey = secret } }));
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.GetFullPath("tools/Collect-CodexIslandDiagnostics.ps1"),
            "-CodexDirectory", native, "-DataDirectory", data, "-OutputDirectory", output }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(15000)) { process.Kill(true); throw new TimeoutException("Isolated diagnostics exporter timed out."); }
        Check(process.ExitCode == 0, "隔离执行实际 Codex 诊断导出脚本");
        using var exported = ZipFile.OpenRead(Directory.GetFiles(output, "*.zip").Single());
        using var exportedReader = new StreamReader(exported.Entries.Single().Open());
        string exportedText = exportedReader.ReadToEnd(); using var exportedJson = JsonDocument.Parse(exportedText);
        Check(exportedJson.RootElement.GetProperty("HookFile").GetProperty("Exists").ValueKind == JsonValueKind.True
            && exportedJson.RootElement.GetProperty("UserFeatureFlags").GetProperty("Hooks").ValueKind == JsonValueKind.False,
            "脚本脱敏后文件存在与原生开关仍保留准确布尔值");
        Check(!exportedText.Contains(secret) && !exportedText.Contains(root, StringComparison.OrdinalIgnoreCase)
            && exportedJson.RootElement.GetProperty("IslandHandlerCount").GetInt32() == 0, "脚本诊断包隐藏个人配置并保留处理器数量");
        Check(exportedJson.RootElement.GetProperty("Bridge").GetProperty("Sources").EnumerateArray().Any(s => s.GetProperty("State").GetString() == "input-wait"),
            "诊断能区分等待用户回复与未知状态");
    }

    private static void AgentDiscoveryPortabilityChecks(string root)
    {
        string commandFolder = Path.Combine(root, "npm");
        string executable = Path.Combine(commandFolder, "node_modules", "@openai", "codex", "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "fixture executable, never launched");
        File.WriteAllText(Path.Combine(commandFolder, "codex.cmd"), "@echo off\r\nnode \"%~dp0node_modules\\@openai\\codex\\bin\\codex.js\" %*\r\n");
        string? previous = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", commandFolder);
            var agents = AgentDiscovery.Find(Path.Combine(root, "config"));
            Check(agents.Any(agent => agent.Id == "codex" && string.Equals(agent.Executable, executable, StringComparison.OrdinalIgnoreCase)),
                "其他电脑通过 npm 安装的 Codex 解析到原生程序，无需将 exe 手动加入 PATH");
        }
        finally { Environment.SetEnvironmentVariable("PATH", previous); }

        string desktop = Path.Combine(root, "Store Package"), packaged = Path.Combine(desktop, "app", "resources", "codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(packaged)!); File.WriteAllText(packaged, "fixture, never launched");
        Check(AgentDiscovery.PackagedCodex(desktop) == packaged, "桌面和 MSIX 的 app/resources 布局可解析到 CLI");
        string config = Path.Combine(root, "manual-config");
        string desktopApp = Path.Combine(desktop, "app", "Codex.exe"); File.WriteAllText(desktopApp, "fixture GUI, never launched");
        AgentDiscovery.SetCodexExecutable(config, desktopApp);
        Check(AgentDiscovery.Find(config).Single(agent => agent.Id == "codex").Executable == packaged,
            "手动选择桌面主程序会解析其 CLI，支持含空格路径");
        AgentDiscovery.SetCodexExecutable(config, null);
        Check(!File.Exists(AgentDiscovery.ManualFile(config)), "重置程序路径后恢复自动发现");
    }

    private static void PluginEnableStateChecks(alpha_island.MainWindow window, string root)
    {
        var options = new IslandOptions();
        options.Extras[PluginHost.EnabledKey("ai.local-agents")] = JsonSerializer.SerializeToElement(true);
        using var host = new PluginHost(window.Dispatcher, options, Array.Empty<string>(), _ => { }, _ => { }, _ => { });
        var entry = new PluginHost.Entry { Directory = root,
            Manifest = PluginManifest.Read(ExternalPluginSource($"LocalAgentsPlugin/bin/{TestBuildConfiguration}/net8.0-windows/plugin.json")), Attempted = true };
        AgentField<List<PluginHost.Entry>>(host, "_entries").Add(entry);
        var settings = new alpha_island.Settings.SettingsWindow(false);
        try
        {
            settings.LoadFrom(options); settings.BindPluginHost(host);
            var rows = (StackPanel)settings.FindName("PluginRows");
            string Text() => string.Join(" ", Descendants<TextBlock>(rows).Select(text => text.Text));
            Check(Descendants<CheckBox>(rows).Single().IsChecked == true && !Text().Contains("未启用") && Text().Contains("启动"),
                "插件开关已开启、工作进程尚在启动时显示启动状态，不误报未启用");
            entry.GameSuspended = true; entry.Attempted = false;
            PrivateCall(settings, "RebuildPluginRows");
            Check(Text().Contains("游戏") && !Text().Contains("未启用"), "游戏模式释放插件进程时仍显示已启用及暂停原因");
            Check(Descendants<System.Windows.Controls.Button>(rows).Any(button => button.Tag is string tag && tag.StartsWith("plugin-settings:", StringComparison.Ordinal)),
                "插件游戏暂停后仍保留可打开详细设置的入口");
            entry.GameSuspended = false; entry.Error = "fixture load failure";
            PrivateCall(settings, "RebuildPluginRows");
            Check(Text().Contains("加载失败") && Descendants<CheckBox>(rows).Single().IsChecked == true,
                "加载失败明确显示原因，保留用户开启插件的选择");
            options.Extras[PluginHost.EnabledKey("ai.local-agents")] = JsonSerializer.SerializeToElement(false);
            settings.LoadFrom(options); entry.Error = ""; PrivateCall(settings, "RebuildPluginRows");
            Check(Text().Contains("未启用") && Descendants<CheckBox>(rows).Single().IsChecked == false,
                "插件关闭时说明与开关一致");
            Check(Descendants<System.Windows.Controls.Button>(rows).Any(button => button.Tag is string tag && tag.StartsWith("plugin-settings:", StringComparison.Ordinal)),
                "插件关闭后仍可以按需打开详细设置");
        }
        finally { settings.ForceClose = true; settings.Close(); }
    }
}
