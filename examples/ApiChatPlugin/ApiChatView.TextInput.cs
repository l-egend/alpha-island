using AlphaIsland.PluginSdk;

namespace AlphaIsland.Plugins.ApiChat;

public partial class ApiChatView : IPluginTextInput
{
    private string? _textInputRequest;
    public async Task<PluginTextInputResult> SubmitTextAsync(string requestId, string text, CancellationToken cancellationToken)
    {
        string prompt = text.Trim();
        if (!Ready) return new(false, "请先配置并启用 API 对话。");
        if (_aiStreaming || _textInputRequest != null) return new(false, "助手正在处理上一条消息，请稍后再试。");
        if (prompt.Length is 0 or > 12000) return new(false, "指令为空或过长。");
        cancellationToken.ThrowIfCancellationRequested();
        _textInputRequest = requestId;
        using var registration = cancellationToken.Register(() => _context.Dispatcher.BeginInvoke(new Action(() =>
        { if (_textInputRequest == requestId) StopAiStreaming(); })));
        int before = _aiMessages.Count;
        try
        {
            await SendAiAsync(prompt, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return new(true, "已停止本次指令。");
            if (_aiMessages.Count == before) return new(false, "未发送。请检查接口、密钥、模型或确认提示。");
            string answer = LastAiAssistantText();
            return new(true, answer.Length > 1000 ? answer[..1000] + "…" : answer,
                _activity.State == AgentRunState.Error);
        }
        finally { if (_textInputRequest == requestId) _textInputRequest = null; _context.Refresh(); }
    }
}
