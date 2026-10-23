using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using AlphaIsland.PluginSdk;

namespace AlphaIsland.Plugins.ApiChat;

public partial class ApiChatView
{
    private readonly List<string> _aiStateReads = new();
    private void RecordAiStateRead(ToolResult result)
    {
        if (_disposed || result.Data == null || _aiStateReads.Count >= 4) return;
        string text = JsonSerializer.Serialize(result.Data.Value, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        _aiStateReads.Add(text[..Math.Min(text.Length, 16000)]);
    }
    private void AiContextButton_Click(object sender, RoutedEventArgs args)
    {
        string state = !_context.ReadConfiguration("AiDesktopContextEnabled", true) ? "按需状态读取已关闭。"
            : _aiStateReads.Count == 0 ? "最近一次 API 对话未读取岛内状态。" : string.Join("\n\n", _aiStateReads);
        var usage = _ai.LastUsage;
        MessageBox.Show(state + "\n\n" + (usage == null ? "接口未返回本次用量。"
            : $"本次请求合计：输入 {usage.InputTokens}，输出 {usage.OutputTokens}，缓存命中 {usage.CachedTokens} token。")
            + "\n状态仅在工具调用时提供，不自动附加旧状态包。人格仅在会话首次成功请求中附加；内置规则、工具说明和聊天历史仍计入输入用量。", "状态读取与用量");
    }
}
