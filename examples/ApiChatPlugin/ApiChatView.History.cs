using System.Windows;
using alpha_island.Conversations;
using alpha_island.Services;

namespace AlphaIsland.Plugins.ApiChat;

public partial class ApiChatView
{
    private void OpenAiConversation()
    {
        if (!Ready) return;
        _context.OpenPage("chat"); AiInputBox.Focus();
    }
    private void AiEnableButton_Click(object sender, RoutedEventArgs e)
    {
        _context.OpenSettings();
    }
    private void AiHistoryButton_Click(object sender, RoutedEventArgs e) => OpenAiHistory();
    private void AiNewButton_Click(object sender, RoutedEventArgs e) => NewAiConversation();
    private void NewAiConversation()
    {
        if (_aiStreaming) return;
        try { ActivateAiConversation(_chatSessions.Create()); }
        catch (Exception ex) { _context.Log("新建对话失败: " + ex.Message); AiStatusText.Text = "新建失败，请检查数据目录权限"; }
    }
    private void ActivateAiConversation(string id)
    {
        if (_aiStreaming) return;
        _aiSessionId = id;
        _aiLoaded = true;
        _options.SetValue("AiActiveSession", id);
        _context.Refresh();
        LoadAiHistory();
        AiInputBox.Text = "";
        UpdateAiInputState();
        
        _chatHistoryWindow?.Reload(id);
    }
    private void OpenAiHistory()
    {
        SyncAiCardContent();
        if (_chatHistoryWindow is { IsVisible: true }) { _chatHistoryWindow.Activate(); return; }
        _chatHistoryWindow = CreateAiHistoryWindow();
        _chatHistoryWindow.Show();
        _chatHistoryWindow.Activate();
    }
    private alpha_island.Controls.ChatHistoryWindow CreateAiHistoryWindow()
    {
        var history = new alpha_island.Controls.ChatHistoryWindow(_chatSessions, _aiSessionId, () => !_aiStreaming);
        if (Window.GetWindow(this) is { IsLoaded: true } owner) history.Owner = owner;
        history.ContinueRequested += id =>
        {
            ActivateAiConversation(id);
            OpenAiConversation();
        };
        history.DataChanged += id =>
        {
            if (id != _aiSessionId || _aiStreaming) return;
            if (!_chatSessions.Exists(id)) ActivateAiConversation(ChatHistoryStore.DefaultSessionId);
            else { LoadAiHistory(); UpdateAiInputState();  }
        };
        history.Closed += (_, _) => _chatHistoryWindow = null;
        return history;
    }
}
