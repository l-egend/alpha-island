using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AlphaIsland.PluginSdk;
using AlphaIsland.Plugins.LocalAgents;
using alpha_island.Core;
using alpha_island.Plugins;
using alpha_island.Services;

internal static partial class Program
{
    private static string[] AttachmentFiles(string root)
    {
        Directory.CreateDirectory(root);
        string image = Path.Combine(root, "图片 & reference.png"), text = Path.Combine(root, "notes.txt");
        File.WriteAllBytes(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1sAAAAASUVORK5CYII="));
        File.WriteAllText(text, "fixture file, no real user data");
        return [image, text];
    }

    private static async Task AgentAttachmentProtocolChecks(string root)
    {
        string[] files = AttachmentFiles(Path.Combine(root, "附件 folder"));
        Check(PluginFileInput.Validate([files[0], files[0], files[1]]).Length == 2, "附件按真实路径去重，不读取文件内容");
        bool missing = false; try { PluginFileInput.Validate([Path.Combine(root, "missing.txt")]); } catch (IOException) { missing = true; }
        Check(missing, "文件已删除时阻止附件提交");
        bool large = false; try { PluginFileInput.Validate(Enumerable.Repeat(files[0], 17).ToArray()); } catch (ArgumentException) { large = true; }
        Check(large, "附件数量有明确上限");
        using var input = JsonDocument.Parse(JsonSerializer.Serialize(AgentAttachments.CodexInput(AgentAttachments.WithPaths("查看文件", files), files)));
        Check(input.RootElement.GetArrayLength() == 2 && input.RootElement[1].GetProperty("type").GetString() == "localImage"
            && input.RootElement[1].GetProperty("path").GetString() == files[0], "Codex 图片作为原生 localImage 输入，路径正确保留中文和特殊字符");
        var session = new AgentSession { Start = (_, _, directory) => new RpcProcess(Environment.ProcessPath!, ["--agent-rpc-fixture", "codex-files"], directory) };
        string answer = "";
        await foreach (var chunk in session.RunAsync(new("codex", "Codex", "fixture"), root, "查看附件", "", root,
            Path.Combine(root, "config"), 8765, (_, _) => Task.FromResult(false), CancellationToken.None, files)) answer += chunk;
        Check(answer == "附件协议已核验", "实际子进程收到图片附件和文档路径，发送链路保持原有审批策略");
        Check(files.All(File.Exists), "发送附件不移动、删除或改写原文件");
    }

    private static void AgentFileTransferUiChecks(alpha_island.MainWindow window, Grid root, string testRoot)
    {
        string[] files = AttachmentFiles(Path.Combine(testRoot, "staged-files"));
        var options = AgentField<IslandOptions>(window, "_options");
        var pages = AgentField<IslandPageHost>(window, "_pages");
        string pluginRoot = Path.Combine(testRoot, "plugins"), folder = Path.Combine(pluginRoot, "local-agent");
        Directory.CreateDirectory(folder);
        string source = ExternalPluginSource($"LocalAgentsPlugin/bin/{TestBuildConfiguration}/net8.0-windows");
        foreach (string file in Directory.GetFiles(source).Where(file => Path.GetExtension(file) == ".dll" || file.EndsWith(".deps.json") || file.EndsWith("plugin.json")))
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        var original = AgentField<PluginHost?>(window, "_plugins");
        var transfer = AgentField<FileTransferService?>(window, "_fileTransfer");
        using var host = new PluginHost(window.Dispatcher, options, [pluginRoot],
            page => { pages.Register(page); window.CardViewport.Children.Add(page.View); },
            page => { pages.Unregister(page.Id); window.CardViewport.Children.Remove(page.View); }, _ => { }) { DataRoot = Path.Combine(testRoot, "worker-data") };
        try
        {
            options.Extras[PluginHost.EnabledKey("ai.local-agents")] = JsonSerializer.SerializeToElement(true);
            SetAgentField(window, "_plugins", host); host.Discover();
            PumpUntilState(() => host.Entries.Single().Running);
            string diagnosis = host.DiagnosticsText();
            Check(diagnosis.Contains("CODEX_CLI_") && diagnosis.Contains("authentication=not-probed") && diagnosis.Contains("requested=True"),
                "主程序诊断能读取隔离插件的程序发现结果，并区分启用状态与账号验证");
            var staging = new FileTransferService(window.Dispatcher); staging.AddFiles(files, true); SetAgentField(window, "_fileTransfer", staging);
            PrivateCall(window, "SyncFileTransferContent"); PrivateCall(window, "SetState", IslandState.Tray, true, null); root.UpdateLayout();
            var send = (System.Windows.Controls.Button)window.FindName("SendTransferFilesToAgentButton");
            Check(send.IsVisible && send.IsEnabled, "真实中转站提供发送至本机 Agent 的可用按钮");
            var toolbar = (FrameworkElement)send.Parent;
            Check(toolbar.ActualHeight <= window.FileListGrid.ActualHeight
                && Descendants<System.Windows.Controls.Button>(toolbar).All(button =>
                    button.TranslatePoint(new System.Windows.Point(), window.FileListGrid).Y + button.ActualHeight <= window.FileListGrid.ActualHeight + 0.5),
                "中转站的附件、收起、清空按钮完整放在可见范围内");
            bool accepted = AwaitPlugin((Task<bool>)PrivateCall(window, "SendTransferFilesToAgentAsync")!);
            Check(accepted && pages.ActiveId == "plugin:ai.local-agents/tasks", "中转站经实际插件进程附加文件后打开本机 Agent 对话页");
            Check((bool)PrivateCall(window, "HoldSelectedComponentCard", false)!, "打开附件对话后等待鼠标进入，避免卡片立即自动缩回");
            Check(staging.Count == 2 && staging.Items.All(item => item.IsCut) && files.All(File.Exists), "附加成功后保留中转条目、剪切标记和原文件");
            var proxy = (alpha_island.Plugins.Isolation.IsolatedPlugin)host.Entries.Single().Instance!;
            Check(!AwaitPlugin(proxy.PrepareGameSuspensionAsync()), "未发送的附件草稿会阻止插件被游戏模式释放");
            host.SetGameSuspended(true);
            var paused = AwaitPlugin(host.AttachFilesAsync("plugin:ai.local-agents/tasks", files));
            Check(!paused.Accepted, "游戏暂停期间不会通过附件入口唤醒 Agent");
        }
        finally
        {
            host.Dispose(); AwaitPlugin(host.ShutdownCompletion.ContinueWith(_ => true));
            SetAgentField(window, "_plugins", original); SetAgentField(window, "_fileTransfer", transfer);
        }
    }
}
