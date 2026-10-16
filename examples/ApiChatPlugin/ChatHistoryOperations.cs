using alpha_island.Services;
namespace AlphaIsland.Plugins.ApiChat;
internal static class ChatHistoryOperations
{
    internal static string RemoveLastTurn(List<ChatMessage> messages)
    {
        int user = messages.FindLastIndex(m => m.Role == ChatMessage.RoleUser);
        if (user < 0) return "";
        string prompt = messages[user].Content;
        messages.RemoveRange(user, messages.Count - user); return prompt;
    }
}
