using System.IO;
using System.Windows;
using AlphaIsland.PluginSdk;
using alpha_island.Conversations;
using alpha_island.Services;

namespace AlphaIsland.Plugins.ApiChat;

public sealed class ApiChatPlugin : IIslandPlugin
{
    private IPluginContext _context = null!;
    private PluginChatSettings _options = null!;
    private ApiChatView? _view;
    private readonly List<WeakReference<ApiSettingsView>> _settings = new();
    public void Initialize(IPluginContext context)
    {
        _context = context; _options = new(context);
        try { new ApiPersonaLibrary(Path.Combine(context.ConfigDirectory, "ai", "personas")).EnsureDirectory(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { context.Log("人格文件夹暂不可用，可在设置中重新打开。"); }
        OnSettingsChanged();
        context.RegisterPage(new("chat", "AI 对话", () => _view = new(context), "\uE99A", 314, 100,
            Activated: () => _view?.ActivatePage()) { Available = () => _options.Enabled,
            Summary = () => _view?.Summary ?? "AI 对话" });
        context.RegisterAction(new("api-chat-open", "AI 对话", () => _view?.OpenConversation(), () => _options.Enabled));
        context.RegisterAction(new("api-chat-history", "对话历史", () => _view?.OpenHistory()));
        context.RegisterSettingsView(() =>
        {
            var view = new ApiSettingsView(context: context);
            view.AiHistoryRequested += (_, _) => _view?.OpenHistory();
            view.OptionsChanged += (_, _) => { _view?.OptionsChanged(); context.Refresh(); };
            _settings.Add(new(view)); return view;
        });
    }
    public void OnSettingsChanged()
    {
        AiConnectionState.Update(_options, Path.Combine(_context.ConfigDirectory, "ai", "secrets.dat"));
        _view?.OptionsChanged();
        _context.Refresh();
    }
    public void Dispose()
    {
        foreach (var reference in _settings) if (reference.TryGetTarget(out var view)) view.Dispose();
        _settings.Clear(); _view?.Dispose(); _view = null;
    }
}

internal sealed class PluginChatSettings(IPluginContext context) : IChatSettings
{
    public string GetText(string key, string fallback)
    {
        var value = context.ReadConfiguration<System.Text.Json.JsonElement>(key, default);
        return value.ValueKind == System.Text.Json.JsonValueKind.Undefined ? fallback : value.ToString();
    }
    public void SetValue(string key, object value) => context.WriteConfiguration(key, value);
    internal bool Enabled => context.ReadConfiguration("AiEnabled", false);
}

internal enum AgentRunState { Idle, Thinking, Streaming, ToolWait, Error }
internal sealed class Activity(IPluginContext context)
{
    internal AgentRunState State;
    internal string Model = "";
    private string _detail = "";
    internal void Set(AgentRunState state, string detail, string owner = "ai")
    {
        State = state; _detail = detail; if (state == AgentRunState.Thinking) Model = detail;
        context.ReportActivity(state switch { AgentRunState.Streaming => "streaming", AgentRunState.ToolWait => "working", AgentRunState.Error => "error", AgentRunState.Idle => "idle", _ => "thinking" }, detail, Model);
    }
    internal void Reset(string owner = "ai") => Set(AgentRunState.Idle, "");
    internal string Describe() => State == AgentRunState.Thinking ? "正在思考 · " + _detail : State == AgentRunState.Streaming ? "正在回复 · " + _detail : _detail;
}

public partial class ApiChatView : IDisposable
{
    private readonly IPluginContext _context;
    private readonly IChatSettings _options;
    private readonly Activity _activity;
    private bool _disposed;
    internal bool Ready => !_disposed && _context.ReadConfiguration("AiEnabled", false);
    public string Summary => _aiStreaming ? "正在回复" : "AI 对话";
    public ApiChatView(IPluginContext context)
    {
        _context = context; _options = new PluginChatSettings(context); _activity = new(context);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/alpha-island;component/Themes/Controls.xaml") });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/alpha-island;component/Themes/LiquidGlass.xaml") });
        InitializeComponent();
        InitializeAiFeedback();
        _chatSessions = new(Path.Combine(context.DataDirectory, "ai", "sessions"));
        _aiConfigFactory = settings => ChatConfig.From(settings, false) with { ApiKey = AiSecretStore.Load(Path.Combine(context.ConfigDirectory,"ai","secrets.dat")), SystemPrompt = ApiAssistantPrompt.PersonaFrom(settings) };
        SyncAiCardContent();
    }
    internal void ActivatePage() => SyncAiCardContent();
    internal void OptionsChanged() { if (!Ready) StopAiStreaming(); OnAiOptionsChanged(); }
    internal void OpenConversation() => OpenAiConversation();
    internal void OpenHistory() => OpenAiHistory();
    public void Dispose()
    {
        if (_disposed) return;
        StopAiStreaming(); EndAiWaiting(); ++_aiGeneration; _disposed = true; _aiErrorTimer?.Stop(); _aiStateReads.Clear();
        _modelAvatar?.Dispose(); _modelAvatar = null; _chatHistoryWindow?.Close(); _chatHistoryWindow = null; _ai.Dispose();
        _activity.Reset();
    }
}
