using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Plugins;
using alpha_island.Plugins.Isolation;
using AlphaIsland.PluginSdk;
using AlphaIsland.Plugins.ApiChat;

internal static partial class Program
{
    private static void ApiModelAvatarChecks(alpha_island.MainWindow main, Grid root, string testRoot)
    {
        const string output = "bin/RuntimeReliability153/previews"; Directory.CreateDirectory(output);
        foreach (var pair in new[] { ("deepseek-flash", "deepseek"), ("DeepSeek/DeepSeek-R1", "deepseek"),
            ("google/gemini-2.5-pro", "gemini"), ("anthropic/claude-sonnet-4-5", "claude"), ("openai/gpt-5.6", "codex"),
            ("o3-mini", "codex"), ("qwen3", ""), ("my-gpt-router", ""), ("", "") })
            Check(ModelArtwork.Family(pair.Item1) == pair.Item2, "模型形象匹配 " + pair.Item1);
        var options = new IslandOptions(); SettingsRegistry.Write(options, "AiEnabled", true);
        SettingsRegistry.Write(options, "AiBaseUrl", "http://127.0.0.1:19731/v1"); SettingsRegistry.Write(options, "AiModel", "deepseek-flash");
        using var view = new ApiChatView(new ConversationPluginFixture(options, Path.Combine(testRoot, "model-avatar")));
        var owner = new Window { Width = 414, Height = 314, Content = view, Left = -32000, Top = -32000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, AllowsTransparency = true, Opacity = 0 };
        bool reduce = MotionPolicy.ReduceMotion, save = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false); AgentAvatarMotion.SetSuspended(false);
            owner.Show(); PumpAnimations(80); view.UpdateLayout();
            var header = (Border)view.FindName("AiAvatarHost"); var icon = (SemanticIcon)header.Child;
            Check(icon.Source is BitmapSource && AgentAvatarMotion.IsAssigned(icon), "实际 API 卡片左上角显示模型对应的既有 Q 版素材");
            Check(AgentAvatarMotion.ClockRunning, "透明离屏插件窗口仍播放可见卡片形象，不受宿主窗口零透明度影响");
            AgentAvatarMotion.Advance(0); var idle = icon.Source; AgentAvatarMotion.Advance(.4);
            Check(!ReferenceEquals(icon.Source, idle), "模型空闲形象使用已有动画帧");
            SaveThemePreview(view, output + "/api-deepseek-idle.png");
            var activity = AgentField<object>(view, "_activity");
            var stateType = activity.GetType().GetField("State", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.FieldType;
            PrivateCall(activity, "Set", Enum.Parse(stateType, "Thinking"), "deepseek-flash", "ai");
            SetAgentField(view, "_aiStreaming", true); PrivateCall(view, "UpdateAiStatusText");
            AgentAvatarMotion.Advance(0); var thinking = icon.Source;
            using var reference = new PluginModelAvatar(true); reference.Update("deepseek-chat", "Thinking");
            Check(ReferenceEquals(icon.Source, ((SemanticIcon)reference.View).Source) && !ReferenceEquals(idle, thinking), "API 思考状态准确复用 DeepSeek 思考素材和缓存");
            SaveThemePreview(view, output + "/api-deepseek-thinking.png");
            var surface = new PluginWorkerSurface(view); var frame = surface.Capture(414, 314);
            var remote = new RemotePluginView("chat", _ => Task.CompletedTask, error => throw error);
            remote.Apply(frame); remote.Measure(new System.Windows.Size(414, 314)); remote.Arrange(new Rect(0, 0, 414, 314)); remote.UpdateLayout();
            SaveThemePreview(remote, output + "/api-model-host.png"); remote.Close();
            MotionPolicy.Apply(true, false);
            Check(!AgentAvatarMotion.ClockRunning && icon.Source != null, "削减动效时保留模型形象并停止播放时钟");
            SetAgentField(view, "_aiStreaming", false); PrivateCall(activity, "Reset", "ai");
            SettingsRegistry.Write(options, "AiModel", "qwen3"); view.OptionsChanged();
            Check(icon.Source == null && !AgentAvatarMotion.IsAssigned(icon), "未知模型恢复默认图标且释放旧形象播放引用");
            SaveThemePreview(view, output + "/api-model-default.png");
            foreach (string model in new[] { "gpt-5.6", "gemini-2.5-pro", "claude-sonnet-4-5" })
            {
                SettingsRegistry.Write(options, "AiModel", model); view.OptionsChanged();
                Check(icon.Source is BitmapSource, "切换模型立即更新既有形象 " + model);
                SaveThemePreview(view, output + "/api-" + ModelArtwork.Family(model) + ".png");
            }
            var events = new List<RemoteEvent>();
            var start = new PluginStart(testRoot, "", "", "ai.api-chat", testRoot, new());
            var worker = new PluginWorkerContext(start, Dispatcher.CurrentDispatcher, events.Add, CancellationToken.None);
            worker.ReportActivity("thinking", "request", "deepseek-flash"); worker.ReportActivity("thinking", "request", "deepseek-flash");
            Check(events.Count == 1 && events[0].Value?.GetString() == "deepseek-flash", "真实插件传输携带当前请求模型，同状态重复上报不增加事件");
            worker.ReportActivity("thinking", "request", "gpt-5.6");
            Check(events.Count == 2, "只切换模型也会更新主岛形象，不被状态去重丢弃");

            using var host = new PluginHost(main.Dispatcher, options, [], _ => { }, _ => { }, _ => { });
            var entry = new PluginHost.Entry { Directory = testRoot, Manifest = new PluginManifest { Id = "ai.api-chat", Name = "API", Version = "0.1.38", ApiVersion = 5 } };
            var context = new PluginHost.Context(host, entry);
            context.ReportActivity("thinking", "fixture", "deepseek-flash");
            Check(AgentStatus.Snapshot.Single(a => a.Owner == "plugin:ai.api-chat").ArtworkFamily == "deepseek", "主宿主把 API 当前模型用于思考提示形象");
            PrivateCall(main, "UpdateAgentStatusUi"); root.UpdateLayout(); PumpAnimations(80);
            var key = (string)PrivateCall(main, "AgentSessionKey", "plugin:ai.api-chat")!;
            var layer = (Grid)PrivateCall(main, "GetNotifyLayerElement", key)!;
            var badge = (SemanticIcon)layer.Children.OfType<Border>().Single().Child;
            Check(AgentAvatarMotion.IsAssigned(badge) && badge.Source != null, "API 思考胶囊实际使用模型 Q 版图标");
            context.ReportActivity("thinking", "fixture", "qwen3");
            PrivateCall(main, "UpdateAgentStatusUi");
            Check(!AgentAvatarMotion.IsAssigned(badge) && badge.Source == null, "从已知模型切到未知模型时，思考胶囊清除旧形象并恢复默认图标");
            context.ReportActivity("idle", "", "qwen3");
        }
        finally
        {
            AgentStatus.Reset(owner: "plugin:ai.api-chat"); owner.Content = null; owner.Close(); view.Dispose();
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduce, save);
        }
    }
}
