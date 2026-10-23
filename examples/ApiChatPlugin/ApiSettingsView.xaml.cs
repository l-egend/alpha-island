using System.IO;
using System.Windows;
using System.Windows.Controls;
using AlphaIsland.PluginSdk;
using alpha_island.Conversations;
using alpha_island.Services;

namespace AlphaIsland.Plugins.ApiChat;

public partial class ApiSettingsView : UserControl, IDisposable
{
    private IChatSettings _options;
    private readonly IPluginContext? _context;
    private readonly string? _aiSecretsFile;
    private bool _suppress = true;
    private bool _disposed;
    public event EventHandler? AiConversationRequested;
    public event EventHandler? AiHistoryRequested;
    public event EventHandler<IChatSettings>? OptionsChanged;
    public ApiSettingsView(bool persistSettings = false, string? aiSecretsFile = null, ChatService? connectionService = null, IPluginContext? context = null)
    {
        _context = context; _options = context == null ? new MemorySettings() : new PluginChatSettings(context);
        _aiSecretsFile = aiSecretsFile ?? (context == null ? null : Path.Combine(context.ConfigDirectory,"ai","secrets.dat"));
        _aiConnectionService = connectionService ?? new ChatService();
        InitializeComponent(); AddAssistantSettings(); LoadFrom(_options);
        Unloaded += (_, _) => CancelAiConnection();
    }
    internal void LoadFrom(IChatSettings settings)
    {
        _options = settings; _suppress = true;
        AiBaseUrlBox.Text = settings.GetText("AiBaseUrl","https://api.deepseek.com/v1");
        LoadPersona(settings);
        foreach (var pair in _assistantOptions) pair.Value.IsChecked = !string.Equals(settings.GetText(pair.Key, "true"), "false", StringComparison.OrdinalIgnoreCase);
        RefreshAiKeyState(); RefreshAiModelConnection(); SyncAiAutomaticState(); _suppress = false;
    }
    private void ApplyChanges()
    {
        if (_suppress || _disposed) return;
        if (AiSystemPromptBox.Text.Length > ApiAssistantPrompt.MaximumPersonaCharacters)
        {
            _suppress = true;
            try { AiSystemPromptBox.Text = AiSystemPromptBox.Text[..ApiAssistantPrompt.MaximumPersonaCharacters]; }
            finally { _suppress = false; }
            UpdatePersonaInfo();
        }
        _options.SetValue("AiBaseUrl", AiBaseUrlBox.Text.Trim());
        _options.SetValue("AiModel", AiModelBox.SelectedItem as string ?? "");
        _options.SetValue("AiPersonaPrompt", AiSystemPromptBox.Text);
        _options.SetValue("AiPersonaFile", _personaFile);
        foreach (var pair in _assistantOptions) _options.SetValue(pair.Key, pair.Value.IsChecked == true);
        _options.SetValue("AiApiKey", ""); SyncAiAutomaticState();
        OptionsChanged?.Invoke(this, _options); _context?.Refresh();
    }
    private void TextBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        if (ReferenceEquals(sender,AiBaseUrlBox)) ResetAiModelConnection();
        if (ReferenceEquals(sender,AiSystemPromptBox)) { _personaFile = ""; UpdatePersonaInfo(); }
        ApplyChanges();
    }
    private void Setting_Changed(object sender, RoutedEventArgs e) { }
    private void OpenAiConversation_Click(object sender, RoutedEventArgs e)
    { if (AiEnabledToggle.IsChecked != true) return; AiConversationRequested?.Invoke(this, EventArgs.Empty); _context?.OpenPage("chat"); }
    private void ManageAiHistory_Click(object sender, RoutedEventArgs e)
    { AiHistoryRequested?.Invoke(this, EventArgs.Empty); }
    public void Dispose() { if (_disposed) return; _disposed=true;CancelAiConnection(); _aiConnectionService.Dispose(); }
    private sealed class MemorySettings : IChatSettings
    {
        private readonly Dictionary<string,object> _values = new();
        public string GetText(string key,string fallback) => _values.TryGetValue(key,out var value) ? value.ToString()! : fallback;
        public void SetValue(string key,object value) => _values[key]=value;
    }
}
