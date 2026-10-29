using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Text.Json;
using AlphaIsland.PluginSdk;
using alpha_island.Services;

namespace AlphaIsland.Plugins.ApiChat;

public partial class ApiChatView
{
    private static bool IsLocalReminderInput(string prompt) => ReminderTextParser.IsListRequest(prompt)
        || ReminderTextParser.CancellationTitle(prompt)!=null || ReminderTextParser.Parse(prompt,DateTimeOffset.Now,TimeZoneInfo.Local).IsRequest;
    private async Task<bool> TryHandleLocalReminderAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var parsed=ReminderTextParser.Parse(prompt,DateTimeOffset.Now,TimeZoneInfo.Local);
        bool list=ReminderTextParser.IsListRequest(prompt);
        string? cancel=ReminderTextParser.CancellationTitle(prompt);
        if(!parsed.IsRequest&&!list&&cancel==null)return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _context.Lifetime);
        _aiCts=linked;
        _aiStreaming=true;
        AiStopButton.Visibility=System.Windows.Visibility.Visible;
        UpdateAiInputState();
        ToolResult result;
        try
        {
            if(!_context.ReadConfiguration("AiToolsEnabled",true)) result=ToolResult.Fail(L.T("此对话的工具已关闭。可在屏幕工具的“管理提醒”中直接添加，无需 API。"));
            else if(!list&&cancel==null&&parsed.Draft==null) result=ToolResult.Fail(parsed.Error);
            else
            {
                string command=list?"list_reminders":cancel!=null?"cancel_reminder":"create_reminder";
                var args=list?JsonSerializer.SerializeToElement(new{}):cancel!=null?JsonSerializer.SerializeToElement(new{title=cancel}):JsonSerializer.SerializeToElement(new{text=prompt});
                result=await _context.Tools.ExecuteAsync(new(Guid.NewGuid().ToString("N"),command,args,_aiSessionId){TurnId=Guid.NewGuid().ToString("N")},linked.Token);
            }
        }
        catch(Exception ex)when(ex is not OutOfMemoryException){result=ToolResult.Fail(L.T("本机提醒暂时不可用，请打开管理提醒查看；没有自动重试。"));}
        finally
        {
            _aiStreaming=false;
            if(ReferenceEquals(_aiCts,linked))_aiCts=null;
            if(!_disposed)
            {
                AiStopButton.Visibility=System.Windows.Visibility.Collapsed;
                UpdateAiInputState();
            }
        }
        if(_disposed || linked.IsCancellationRequested)return true;
        string user=SensitiveTextDetector.Inspect(prompt).Redacted;
        string answer=SensitiveTextDetector.Inspect(result.Message).Redacted;
        _aiMessages.Add(new(ChatMessage.RoleUser,user){LocalOnly=true});
        _aiMessages.Add(new(ChatMessage.RoleAssistant,L.T("本地提醒：")+answer){LocalOnly=true});
        AddAiBubble(user,isUser:true);AddAiBubble(L.T("本地提醒：")+answer,isUser:false);
        AiInputBox.Clear();SaveAiHistory();_chatHistoryWindow?.Reload(_aiSessionId);UpdateAiInputState();
        return true;
    }
}
