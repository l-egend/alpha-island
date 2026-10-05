using alpha_island.Services;

namespace alpha_island.Core;

internal static class ChatHistoryOperations
{
    // 仅移除最后一个用户回合；失败时也不能删掉上一个回合的成功回复。
    internal static string RemoveLastTurn(List<ChatMessage> messages)
    {
        int user = messages.FindLastIndex(m => m.Role == ChatMessage.RoleUser);
        if (user < 0) return "";
        string prompt = messages[user].Content;
        messages.RemoveRange(user, messages.Count - user);
        return prompt;
    }
}
