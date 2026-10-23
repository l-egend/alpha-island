using System.IO;
using System.Text.Json;
using alpha_island.Conversations;

namespace alpha_island.Services;

internal sealed record ChatSessionSummary(string Id, string Title, DateTimeOffset Updated, int MessageCount)
{
    public override string ToString() => $"{Title}\n{Updated.ToLocalTime():MM-dd HH:mm} · {MessageCount} 条消息";
}

/// <summary>Local conversations, independent of API credentials and request context limits.</summary>
internal sealed class ChatSessionStore(string? directory = null)
{
    internal string DirectoryPath { get; } = Path.GetFullPath(directory ?? ConversationFiles.Sessions);
    private sealed class SessionFile
    {
        public string Session { get; set; } = ChatHistoryStore.DefaultSessionId;
        public string Title { get; set; } = "";
        public string Updated { get; set; } = "";
        public List<ChatMessage> Messages { get; set; } = new();
        public string DesktopContext { get; set; } = "";
        public bool ApiStarted { get; set; }
    }

    private string PathFor(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id is "." or ".."
            || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("无效的对话标识");
        return Path.Combine(DirectoryPath, id + ".json");
    }
    private SessionFile Read(string id)
    {
        string path = PathFor(id);
        Exception? failure = null;
        foreach (string candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var file = AtomicJsonFile.Read<SessionFile>(candidate, ConversationFiles.JsonOptions)
                    ?? throw new JsonException("对话文件为空");
                file.Messages = (file.Messages ?? new()).Where(m => m != null
                    && (m.Role is ChatMessage.RoleUser or ChatMessage.RoleAssistant)
                    && !string.IsNullOrWhiteSpace(m.Content)).ToList();
                return file;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { failure = ex; }
        }
        if (failure != null) throw new IOException("无法读取对话文件，可检查本地数据目录", failure);
        return new SessionFile { Session = id };
    }
    internal List<ChatMessage> Load(string id = ChatHistoryStore.DefaultSessionId) => Read(id).Messages;
    internal bool HasStartedApi(string id)
    {
        var file = Read(id);
        return file.ApiStarted || file.Messages.Any(m => m.Role == ChatMessage.RoleAssistant && !m.LocalOnly);
    }
    internal void MarkApiStarted(string id)
    {
        var file = Read(id);
        if (file.ApiStarted) return;
        file.ApiStarted = true; Write(id, file, true);
    }
    internal string ReadDesktopContext(string id)
    { string value = Read(id).DesktopContext ?? ""; return value.Length <= 4096 ? value : ""; }
    internal void WriteDesktopContext(string id, string context)
    { if (context.Length > 4096) throw new ArgumentException("系统状态过长"); var file = Read(id); file.DesktopContext = context; Write(id, file, false); }
    internal bool Exists(string id) => File.Exists(PathFor(id)) || File.Exists(PathFor(id) + ".bak");
    private static string Title(SessionFile file) => !string.IsNullOrWhiteSpace(file.Title) ? file.Title
        : file.Messages.FirstOrDefault(m => m.Role == ChatMessage.RoleUser)?.Content.Replace('\n', ' ').Trim() is { Length: > 0 } text
            ? text[..Math.Min(32, text.Length)] : "新对话";
    internal IReadOnlyList<ChatSessionSummary> List()
    {
        if (!Directory.Exists(DirectoryPath)) return Array.Empty<ChatSessionSummary>();
        var sessions = new List<ChatSessionSummary>();
        foreach (string id in Directory.EnumerateFiles(DirectoryPath, "*.json*")
            .Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFileName(p.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p)[..^5])
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var file = Read(id);
                DateTimeOffset.TryParse(file.Updated, out var updated);
                sessions.Add(new(id, Title(file), updated, file.Messages.Count));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Keep other conversations usable, and keep a damaged one selectable for deletion.
                sessions.Add(new(id, "无法读取的对话 · " + id, DateTimeOffset.MinValue, 0));
            }
        }
        return sessions.OrderByDescending(s => s.Updated).ToArray();
    }
    internal void Save(string id, IReadOnlyList<ChatMessage> messages, bool keepBackup = true)
    {
        var file = Read(id);
        file.Session = id;
        if (messages.Count == 0) { file.DesktopContext = ""; file.ApiStarted = false; }
        file.Messages = messages.Where(m => (m.Role is ChatMessage.RoleUser or ChatMessage.RoleAssistant)
            && !string.IsNullOrWhiteSpace(m.Content)).ToList();
        Write(id, file, keepBackup);
    }
    internal void SaveTail(string id, int prefixCount, IReadOnlyList<ChatMessage> messages)
    {
        var file = Read(id);
        if (prefixCount < 0 || prefixCount > file.Messages.Count)
            throw new IOException("对话历史已变化，请重新打开后保存");
        var prefix = file.Messages.Take(prefixCount).Select(message => message with
        { Content = SensitiveTextDetector.Inspect(message.Content).Redacted });
        file.Messages = prefix.Concat(messages).Where(message => (message.Role is ChatMessage.RoleUser or ChatMessage.RoleAssistant)
            && !string.IsNullOrWhiteSpace(message.Content)).ToList();
        if (file.Messages.Count == 0) { file.DesktopContext = ""; file.ApiStarted = false; }
        file.Session = id;
        Write(id, file, true);
    }
    private void Write(string id, SessionFile file, bool keepBackup)
    {
        file.Updated = DateTimeOffset.Now.ToString("O");
        string path = PathFor(id);
        // An explicit edit/deletion must not resurrect old content through recovery.
        if (!keepBackup && File.Exists(path + ".bak")) File.Delete(path + ".bak");
        AtomicJsonFile.Write(path, JsonSerializer.Serialize(file, ConversationFiles.JsonOptions), keepBackup);
    }
    internal string Create()
    {
        string id = Guid.NewGuid().ToString("N");
        Save(id, Array.Empty<ChatMessage>());
        return id;
    }
    internal void Rename(string id, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("对话名称不能为空");
        var file = Read(id); file.Title = title.Trim(); Write(id, file, false);
    }
    internal void EditMessage(string id, int index, string content)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("消息内容不能为空");
        var messages = Load(id);
        if ((uint)index >= messages.Count) throw new ArgumentOutOfRangeException(nameof(index));
        messages[index] = messages[index] with { Content = content.Trim() };
        Save(id, messages, keepBackup: false);
    }
    internal void DeleteMessage(string id, int index)
    {
        var messages = Load(id);
        if ((uint)index >= messages.Count) throw new ArgumentOutOfRangeException(nameof(index));
        messages.RemoveAt(index); Save(id, messages, keepBackup: false);
    }
    internal void Delete(string id)
    {
        string path = PathFor(id);
        // Remove recovery first: a partial failure leaves the primary visible for retry.
        if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        if (File.Exists(path)) File.Delete(path);
    }
}
