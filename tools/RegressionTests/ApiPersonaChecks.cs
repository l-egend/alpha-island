using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using AlphaIsland.PluginSdk;
using alpha_island.Core;
using alpha_island.Services;
using AlphaIsland.Plugins.ApiChat;

internal static partial class Program
{
    private static void ApiPersonaUiChecks(string root)
    {
        ApiPersonaFileChecks(root);
        var options = new IslandOptions(); SettingsRegistry.SetExtra(options, "AiEnabled", true);
        SettingsRegistry.SetExtra(options, "AiSystemPrompt", "旧版温和人格");
        int reads = 0;
        var context = new ConversationPluginFixture(options, Path.Combine(root, "on-demand-persona")) {
            ContextRequested = _ => { reads++; return new(DateTimeOffset.Now, new() { ["deviceBatteries"] = "SHOULD_ONLY_READ_WHEN_TOOL_CALLED" }); }
        };
        using var view = new ApiChatView(context);
        var factory = AgentField<Func<alpha_island.Conversations.IChatSettings, ChatConfig>>(view, "_aiConfigFactory");
        SetAgentField(view, "_aiConfigFactory", new Func<alpha_island.Conversations.IChatSettings, ChatConfig>(settings => factory(settings) with { BaseUrl = "http://127.0.0.1/v1", ApiKey = TestApiKey, Model = "fixture" }));
        using var handler = new AgentHttpFixture((_, _) => AgentResponse(AgentSse(new { choices = new[] { new { delta = new { content = "本地测试完成" }, finish_reason = "stop" } } })));
        using var http = new HttpClient(handler); using var chat = new ChatService(http, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        SetAgentField(view, "_ai", chat);
        void Send(string text) => AwaitPlugin(((Task)PrivateCall(view, "SendAiAsync", text)!).ContinueWith(_ => true));
        Send("我的鼠标电量是多少？");
        Check(reads == 0 && !handler.Bodies.Single().Contains("SHOULD_ONLY_READ_WHEN_TOOL_CALLED"), "没有工具调用时不读取、不附加任何系统状态");
        using (var request = JsonDocument.Parse(handler.Bodies[0]))
        {
            var system = request.RootElement.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "system").GetProperty("content").GetString()!;
            Check(system.StartsWith(ApiAssistantPrompt.DefaultSystemPrompt) && system.Contains("旧版温和人格"), "内置默认系统规则与旧版人格偏好分离，实际请求仍兼容旧配置");
        }
        var store = AgentField<ChatSessionStore>(view, "_chatSessions");
        store.WriteDesktopContext("default", JsonSerializer.Serialize(new DesktopContextSnapshot(DateTimeOffset.Now, new() { ["deviceBatteries"] = "SHOULD_ONLY_READ_WHEN_TOOL_CALLED" })));
        Send("我的鼠标现在还有多少电？");
        Check(reads == 0 && !handler.Bodies[^1].Contains("SHOULD_ONLY_READ_WHEN_TOOL_CALLED"), "升级前保存的状态包也不会随请求重发");
        Check(!handler.Bodies[^1].Contains("旧版温和人格") && handler.Bodies[^1].Contains("Alpha Island"), "同一会话后续提问不再发送人格，内置规则仍在");
        using var settingsView = new ApiSettingsView(context: context);
        Check(settingsView.AiSystemPromptBox.Text == "旧版温和人格", "人格编辑框保留旧版自定义提示词");
        Check(settingsView.AiPersonaTitleText.Text == "人格提示词", "设置标签显示人格提示词");
        string role = Path.Combine(root, "导入角色.txt"); File.WriteAllText(role, "你叫晨星，回答简洁自然。", new UTF8Encoding(false));
        settingsView.PersonaFilePicker = () => role;
        settingsView.AiPersonaImportButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(context.ReadConfiguration("AiPersonaPrompt", "").Contains("晨星") && context.ReadConfiguration("AiPersonaFile", "").EndsWith(".txt"), "点击导入按钮将 TXT 人格即时保存到设置");
        Check(File.Exists(Path.Combine(context.ConfigDirectory, "ai", "personas", context.ReadConfiguration("AiPersonaFile", ""))), "导入的人格副本存放在可打开的人格文件夹");
        Check(context.ReadConfiguration("AiSystemPrompt", "") == "旧版温和人格", "导入新人格不覆盖旧配置，旧文本可恢复");
        Check(settingsView.AiPersonaStatusText.Text.Contains("会话首次") && settingsView.AiPersonaStatusText.Text.Contains("8000"), "人格编辑显示字符上限与会话首次发送说明");
        // The test context intentionally has no real credential; keep connection readiness separate from personality behavior.
        SettingsRegistry.SetExtra(options, "AiEnabled", true);
        Send("你好");
        Check(!handler.Bodies[^1].Contains("晨星") && !handler.Bodies[^1].Contains("旧版温和人格"), "人格修改不追加入已开始的会话");
        PrivateCall(view, "NewAiConversation"); Send("你好");
        using (var request = JsonDocument.Parse(handler.Bodies[^1]))
        {
            var system = request.RootElement.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "system").GetProperty("content").GetString()!;
            Check(system.StartsWith(ApiAssistantPrompt.DefaultSystemPrompt) && system.Contains("晨星") && !system.Contains("旧版温和人格"), "导入人格应用到下一次真实请求，默认系统规则保持固定");
        }
        settingsView.PersonaFilePicker = () => Path.Combine(root, "missing-role.txt");
        settingsView.AiPersonaImportButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(context.ReadConfiguration("AiPersonaPrompt", "").Contains("晨星") && settingsView.AiPersonaStatusText.Text.Contains("导入失败"), "导入失败显示原因并保留正在使用的人格");
        settingsView.AiSystemPromptBox.Text = new string('人', ApiAssistantPrompt.MaximumPersonaCharacters + 1);
        Check(settingsView.AiSystemPromptBox.Text.Length == ApiAssistantPrompt.MaximumPersonaCharacters && context.ReadConfiguration("AiPersonaPrompt", "").Length == ApiAssistantPrompt.MaximumPersonaCharacters,
            "程序化或隔离输入超长时也限制人格长度，避免绕过文本框上限");
        settingsView.AiSystemPromptBox.Text = "";
        Check(ApiAssistantPrompt.PersonaFrom(new PluginChatSettings(context)).Length == 0 && context.ReadConfiguration("AiPersonaFile", "x") == "", "清空人格明确覆盖旧配置，不会意外回退旧人设");
        SettingsRegistry.SetExtra(options, "AiEnabled", true);
        Send("你好");
        Check(!handler.Bodies[^1].Contains("晨星") && !handler.Bodies[^1].Contains("旧版温和人格"), "人格修改不追加入已开始的会话");
        PrivateCall(view, "NewAiConversation"); Send("你好");
        using (var request = JsonDocument.Parse(handler.Bodies[^1]))
        {
            string system = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            Check(system == ApiAssistantPrompt.DefaultSystemPrompt, "清空后只发送默认系统规则");
        }
    }
    private static void ApiPersonaFileChecks(string root)
    {
        string folder = Path.Combine(root, "personality-library");
        var library = new ApiPersonaLibrary(folder); library.EnsureDirectory();
        string guide = Path.Combine(folder, ApiPersonaLibrary.GuideFileName);
        Check(File.Exists(guide) && File.Exists(Path.Combine(folder, ApiPersonaLibrary.ExampleFileName)), "人格文件夹自动包含编写规范与示例 TXT");
        string guideText = File.ReadAllText(guide);
        Check(guideText.Contains("token") && guideText.Contains("8000") && guideText.Contains("不会自动附加"), "编写规范说明长度、计费与按需状态读取");
        File.WriteAllText(guide, "用户保留的规范版本"); library.EnsureDirectory();
        Check(File.ReadAllText(guide) == "用户保留的规范版本", "后续启动不会覆盖用户修改的人格规范");
        string source = Path.Combine(root, "中文角色.txt"); string role = "你叫小岛，回答清楚简短。";
        File.WriteAllText(source, role, new UTF8Encoding(false));
        var imported = library.Import(source);
        Check(imported.Text == role && File.ReadAllText(source) == role && File.ReadAllText(Path.Combine(folder, imported.FileName)) == role, "UTF-8 人格复制成功且不改变源文件");
        int count = Directory.GetFiles(folder).Length;
        Check(library.Import(source).FileName == imported.FileName && Directory.GetFiles(folder).Length == count, "重复导入相同外部文件不会堆积副本");
        Check(library.Import(Path.Combine(folder, imported.FileName)).FileName == imported.FileName && Directory.GetFiles(folder).Length == count, "从人格文件夹切换已有 TXT 不会再次复制");
        File.WriteAllText(source, "新的角色偏好"); var changed = library.Import(source);
        Check(changed.FileName != imported.FileName && File.ReadAllText(Path.Combine(folder, imported.FileName)) == role, "同名不同内容新建文件，保留原人格副本");
        string unicode = Path.Combine(root, "unicode.txt"); File.WriteAllText(unicode, role, Encoding.Unicode);
        Check(library.Import(unicode).Text == role, "支持带 BOM 的 UTF-16 人格文本");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string legacy = Path.Combine(root, "gbk.txt"); File.WriteAllText(legacy, "你好，回答简短。", Encoding.GetEncoding(936));
        Check(library.Import(legacy).Text == "你好，回答简短。", "支持常见中文 GBK 人格文本");
        void Reject(string fileName, string content, string description)
        {
            string path = Path.Combine(root, fileName); File.WriteAllText(path, content, new UTF8Encoding(false));
            int before = Directory.GetFiles(folder).Length; bool rejected = false;
            try { library.Import(path); } catch (ArgumentException) { rejected = true; }
            Check(rejected && Directory.GetFiles(folder).Length == before, description);
        }
        Reject("empty.txt", "  \r\n ", "拒绝空人格且不产生副本");
        Reject("binary.txt", "text\0binary", "拒绝含空字节的非纯文本");
        Reject("wrong.json", role, "拒绝非 TXT 文件");
        Reject("long.txt", new string('人', ApiAssistantPrompt.MaximumPersonaCharacters + 1), "拒绝超过字符上限的人格，避免意外发送长篇资料");
        Reject("big.txt", new string('a', ApiPersonaLibrary.MaximumFileBytes + 1), "读取前拒绝超过 64 KB 的文件");
        bool guideRejected = false; try { library.Import(guide); } catch (ArgumentException) { guideRejected = true; }
        Check(guideRejected, "编写规范不会被误导入为人格");
        Check(ApiAssistantPrompt.Compose("") == ApiAssistantPrompt.DefaultSystemPrompt && ApiAssistantPrompt.Compose("测试人格").StartsWith(ApiAssistantPrompt.DefaultSystemPrompt), "内置系统规则固定存在，空人格使用默认助手");
    }
}
