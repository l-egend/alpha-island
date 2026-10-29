using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Services;

namespace alpha_island.Controls;

internal partial class ChatHistoryWindow : Window
{
    private readonly ChatSessionStore _store;
    private readonly Func<bool> _canModify;
    private bool _refreshing;
    private sealed record MessageRow(int Index, string Role, string Preview);
    internal event Action<string>? ContinueRequested;
    internal event Action<string>? DataChanged;
    private string? SelectedId => (SessionList.SelectedItem as ChatSessionSummary)?.Id;

    internal ChatHistoryWindow(ChatSessionStore store, string activeId, Func<bool> canModify)
    {
        _store = store; _canModify = canModify;
        InitializeComponent();
        Reload(activeId);
    }
    internal void Reload(string? selectedId = null)
    {
        selectedId ??= SelectedId;
        int selectedMessage = SelectedId == selectedId ? Math.Max(0, MessageList.SelectedIndex) : 0;
        try
        {
            _refreshing = true;
            var sessions = _store.List();
            SessionList.ItemsSource = sessions;
            SessionList.SelectedItem = sessions.FirstOrDefault(s => s.Id == selectedId) ?? sessions.FirstOrDefault();
        }
        catch (Exception)
        {
            SessionList.ItemsSource = null;
            LoadSelected();
            StatusText.Text = L.T("无法读取历史，请检查本地数据目录权限");
            return;
        }
        finally { _refreshing = false; }
        LoadSelected(selectedMessage);
    }
    private void Session_Changed(object sender, SelectionChangedEventArgs e) { if (!_refreshing) LoadSelected(); }
    private void LoadSelected(int selectedMessage = 0)
    {
        TitleBox.Text = (SessionList.SelectedItem as ChatSessionSummary)?.Title ?? "";
        MessageList.ItemsSource = null; MessageEditor.Text = "";
        bool selected = SelectedId != null, changes = selected && _canModify();
        ContinueButton.IsEnabled = RenameButton.IsEnabled = DeleteSessionButton.IsEnabled = changes;
        SaveMessageButton.IsEnabled = DeleteMessageButton.IsEnabled = false;
        TitleBox.IsReadOnly = MessageEditor.IsReadOnly = !changes;
        if (!selected) { StatusText.Text = L.T("暂无历史；在岛上新建对话后记录会显示在这里"); return; }
        try
        {
            var messages = _store.Load(SelectedId!);
            MessageList.ItemsSource = messages.Select((m, i) => new MessageRow(i,
                m.Role == ChatMessage.RoleUser ? L.T("你") : L.T("助手"), m.Content[..Math.Min(m.Content.Length, 180)])).ToArray();
            MessageList.SelectedIndex = messages.Count > 0 ? Math.Min(selectedMessage, messages.Count - 1) : -1;
            StatusText.Text = changes ? L.T("修改后点击保存；继续对话时将使用修改后的记录") : L.T("正在生成回复，可以查看历史；生成结束后点击刷新即可编辑");
        }
        catch (Exception)
        {
            ContinueButton.IsEnabled = RenameButton.IsEnabled = false;
            StatusText.Text = L.T("此对话无法读取；可以打开数据目录检查或删除此对话");
        }
    }
    private void Message_Changed(object sender, SelectionChangedEventArgs e)
    {
        bool selected = SelectedId != null && MessageList.SelectedItem is MessageRow;
        SaveMessageButton.IsEnabled = DeleteMessageButton.IsEnabled = selected && _canModify();
        MessageEditor.Text = "";
        if (!selected) return;
        try { MessageEditor.Text = _store.Load(SelectedId!)[((MessageRow)MessageList.SelectedItem).Index].Content; }
        catch (Exception) { StatusText.Text = L.T("消息读取失败，请刷新后重试"); }
    }
    private void Mutate(Action<string> action)
    {
        if (SelectedId is not { } id) return;
        if (!_canModify()) { StatusText.Text = L.T("正在生成回复，请结束生成后再修改记录"); return; }
        try
        {
            action(id); DataChanged?.Invoke(id); Reload(id); StatusText.Text = L.T("已保存到本机");
        }
        catch (Exception) { StatusText.Text = L.T("操作失败，未报告保存成功；请检查输入或数据目录权限"); }
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();
    private void Rename_Click(object sender, RoutedEventArgs e) => Mutate(id => _store.Rename(id, TitleBox.Text));
    private void SaveMessage_Click(object sender, RoutedEventArgs e)
    {
        if (MessageList.SelectedItem is MessageRow row) Mutate(id => _store.EditMessage(id, row.Index, MessageEditor.Text));
    }
    private void DeleteMessage_Click(object sender, RoutedEventArgs e)
    {
        if (MessageList.SelectedItem is MessageRow row) Mutate(id => _store.DeleteMessage(id, row.Index));
    }
    private void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedId == null || !_canModify()) return;
        if (System.Windows.MessageBox.Show(this, L.T("删除选中的对话及本地备份？"), L.T("删除对话"),
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Mutate(_store.Delete);
    }
    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedId is not { } id || !_canModify()) return;
        ContinueRequested?.Invoke(id); Close();
    }
    private void OpenDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_store.DirectoryPath);
            Process.Start(new ProcessStartInfo(_store.DirectoryPath) { UseShellExecute = true });
        }
        catch (Exception) { StatusText.Text = L.T("无法打开数据目录"); }
    }
}
