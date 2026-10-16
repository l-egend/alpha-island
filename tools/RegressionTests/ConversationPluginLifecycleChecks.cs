using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AlphaIsland.Plugins.ApiChat;
using AlphaIsland.Plugins.LocalAgents;
using alpha_island.Core;
using alpha_island.Plugins;
using alpha_island.Services;
using UserControl=System.Windows.Controls.UserControl;
using Button=System.Windows.Controls.Button;
using TextBox=System.Windows.Controls.TextBox;
using AgentRunState=alpha_island.Core.AgentRunState;

internal static partial class Program
{
    private static void ConversationPluginLifecycleChecks(alpha_island.MainWindow main,string root)
    {
        string pluginRoot=Path.Combine(root,"conversation-plugin-load");Directory.CreateDirectory(pluginRoot);
        foreach(var item in new[]{(Id:"ai.api-chat",Type:typeof(ApiChatPlugin)),(Id:"ai.local-agents",Type:typeof(LocalAgentsPlugin))})
        {
            string folder=Path.Combine(pluginRoot,item.Id);Directory.CreateDirectory(folder);
            string assembly=Path.GetFileName(item.Type.Assembly.Location);
            File.Copy(item.Type.Assembly.Location,Path.Combine(folder,assembly));
            File.WriteAllText(Path.Combine(folder,"plugin.json"),JsonSerializer.Serialize(new{id=item.Id,name=item.Id,version="0.1.26",apiVersion=1,wpfResources=item.Id=="ai.api-chat",assembly,entryType=item.Type.FullName}));
        }
        var options=new IslandOptions();var pages=new IslandPageHost();var views=new Grid();string opened="";
        using var host=new PluginHost(main.Dispatcher,options,new[]{pluginRoot},p=>{pages.Register(p);views.Children.Add(p.View);},p=>{pages.Unregister(p.Id);views.Children.Remove(p.View);},id=>opened=id)
            {DataRoot=Path.Combine(root,"conversation-plugin-data")};
        host.AvailabilityChanged+=()=>pages.Sync(options);host.Changed+=()=>pages.Sync(options);
        host.Discover(); PumpUntilState(() => host.Entries.All(entry => entry.Running || entry.Error.Length > 0));
        var api=host.Entries.Single(e=>e.Manifest?.Id=="ai.api-chat");var local=host.Entries.Single(e=>e.Manifest?.Id=="ai.local-agents");
        Check(api.Running&&local.Running,"两个实际 DLL 可通过插件加载器独立启动，共享库和 XAML 正常解析");
        AgentStatus.Set(AgentRunState.Thinking,"其他任务","regression-other");
        local.Context!.ReportActivity("streaming","插件回复");
        Check(host.PageForActivity(AgentStatus.Owner)=="plugin:ai.local-agents/tasks","正在处理的本机 Agent 状态指向对应插件卡片，便于回到任务或审批");
        local.Context.ReportActivity("idle","");
        Check(AgentStatus.State==AgentRunState.Thinking&&AgentStatus.Detail=="其他任务","插件结束准确移除自己的状态，不留下思考状态且保留其他任务");
        AgentStatus.Reset(owner:"regression-other");
        Check(pages.IndexOf("plugin:ai.api-chat/chat")<0&&!host.InvokeAction("api-chat-open")&&host.AvailableActions.Any(a=>a.Key=="api-chat-history"),"未连接时不注册可见对话入口，历史操作仍独立保留");
        var settings=api.Context!.SettingsView!();
        using var settingsSource = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Remote API lifecycle")
            { Width=700, Height=700, PositionX=-32000, PositionY=-32000, WindowStyle=unchecked((int)0x90000000) });
        settingsSource.RootVisual=settings; settings.Measure(new System.Windows.Size(700,700)); settings.Arrange(new Rect(0,0,700,700));
        PumpUntilState(() => settings.FindName("ManageAiHistoryButton") is Button);
        Check(settings.FindName("ManageAiHistoryButton") is Button historyButton && System.Windows.Automation.AutomationProperties.GetName(historyButton)=="管理对话数据","真正的插件设置页包含连接和历史管理，不依赖宿主固定控件");
        string secret=Path.Combine(host.DataRoot,"config","ai","secrets.dat");
        Check(AiSecretStore.TrySave(secret,TestApiKey),"插件生命周期测试使用独立 DPAPI 测试密钥");
        var store=new ChatSessionStore(Path.Combine(host.DataRoot,"ai","sessions"));store.Save("default",new[]{new ChatMessage("user","保留此历史")});
        string history=Path.Combine(store.DirectoryPath,"default.json");byte[] secretBytes=File.ReadAllBytes(secret),historyBytes=File.ReadAllBytes(history);
        var config=new ChatConfig("https://example.invalid/v1",TestApiKey,"test-model","",.7,1024);
        SettingsRegistry.Write(options,"AiBaseUrl",config.BaseUrl);SettingsRegistry.Write(options,"AiModel",config.Model);
        AiConnectionState.Verify(options,secret,config);host.Sync(options); PumpUntilState(() => pages.IndexOf("plugin:ai.api-chat/chat")>=0 && host.AvailableActions.Any(action=>action.Key=="api-chat-open"));
        host.InvokeAction("api-chat-open"); PumpUntilState(() => opened=="plugin:ai.api-chat/chat");
        Check(pages.IndexOf("plugin:ai.api-chat/chat")>=0&&host.InvokeAction("api-chat-open")&&opened=="plugin:ai.api-chat/chat","有效连接证明同步启用插件页面及托盘操作");
        api.Context.ReportActivity("thinking","待取消的插件任务");
        var lifecycle=api.Context.Lifetime;options.Extras[PluginHost.EnabledKey("ai.api-chat")]=JsonSerializer.SerializeToElement(false);host.Sync(options);
        Check(lifecycle.IsCancellationRequested&&!api.Running&&local.Running&&pages.IndexOf("plugin:ai.api-chat/chat")<0&&!host.AvailableActions.Any(a=>a.Key.StartsWith("api-chat-")),"禁用 API 插件取消生命周期并撤下所有入口，其他插件继续运行");
        AwaitPlugin(api.Teardown.ContinueWith(_=>true));
        Check(!settings.IsEnabled,"禁用插件释放并停止旧设置界面的操作入口");
        Check(AgentStatus.State==AgentRunState.Idle,"禁用插件同时撤下自己的 Agent 状态，不留下永久思考提示");
        options.Extras[PluginHost.EnabledKey("ai.api-chat")]=JsonSerializer.SerializeToElement(true);host.Sync(options); PumpUntilState(()=>api.Running && host.AvailableActions.Any(action=>action.Key=="api-chat-open"));
        Check(api.Running&&host.InvokeAction("api-chat-open"),"重新启用插件可复用已有验证结果，无需重写密钥");
        Check(AwaitPlugin(host.UninstallAsync(api))&&!Directory.Exists(api.Directory)&&local.Running,"API 插件可真实卸载且本机 Agent 插件保持独立");
        Check(File.ReadAllBytes(secret).SequenceEqual(secretBytes)&&File.ReadAllBytes(history).SequenceEqual(historyBytes),"禁用、重新启用和卸载都完整保留原加密密钥与历史文件");

        host.Dispose(); AwaitPlugin(host.ShutdownCompletion.ContinueWith(_=>true));
        var fixture=new ConversationPluginFixture(new IslandOptions(),Path.Combine(root,"local-plugin-history"));
        string cwd=Path.Combine(root,"agent workspace");Directory.CreateDirectory(cwd);
        fixture.WriteConfiguration("plugin/ai.local-agents/workspace",cwd);
        fixture.WriteConfiguration("plugin/ai.local-agents/provider","codex");
        string localHistory=Path.Combine(fixture.ConfigDirectory,"plugins","ai.local-agents","last-conversation.json");Directory.CreateDirectory(Path.GetDirectoryName(localHistory)!);
        File.WriteAllText(localHistory,JsonSerializer.Serialize(new{provider="codex",directory=cwd,session="saved-thread-123",text="你：测试\nCodex：原有回复"}));
        using var view=new LocalAgentsView(fixture,new[]{new LocalAgent("codex","Codex","fixture.exe")});
        Check(AgentField<System.Windows.Controls.ComboBox>(view,"_provider").Style!=null&&AgentField<TextBox>(view,"_input").Style!=null,"本机 Agent 提供方与输入框明确使用岛的深色控件样式，避免白底白字");
        Check(AgentField<System.Windows.Controls.ComboBox>(view,"_provider").Background is System.Windows.Media.SolidColorBrush {Color.R:<80,Color.G:<80,Color.B:<80},"全局浅色主题不会覆盖本机 Agent 下拉框的深色背景");
        Check(AgentField<AgentSession>(view,"_session").NativeSession=="saved-thread-123"&&AgentField<TextBox>(view,"_output").Text.Contains("原有回复"),"本机 Agent 记录与原生会话标识一起恢复，可继续当前上下文");
        SaveConversationPreview(new Border{Background=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20,24,30)),Child=view},"docs/screens/local-agent-card.png",392,294);
    }
}
