namespace AlphaIsland.PluginSdk;

/// <summary>Model names identify artwork, independently of API endpoint or gateway.</summary>
public static class ModelArtwork
{
    public static string Family(string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Length > 512) return "";
        foreach (string part in model.ToLowerInvariant().Split(['/', '\\', ':', '@'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            bool Starts(string name) => part == name || part.StartsWith(name + "-", StringComparison.Ordinal)
                || part.StartsWith(name + "_", StringComparison.Ordinal) || part.StartsWith(name + ".", StringComparison.Ordinal);
            if (Starts("deepseek")) return "deepseek";
            if (Starts("claude")) return "claude";
            if (Starts("gemini")) return "gemini";
            if (Starts("gpt") || Starts("chatgpt") || Starts("codex") || Starts("o1") || Starts("o3") || Starts("o4")) return "codex";
        }
        return "";
    }

    public static string Source(string family) => family switch
    {
        "deepseek" => "deepseek-harness", "claude" => "claude-code", "gemini" => "antigravity", "codex" => "codex", _ => "internal"
    };
}

public interface IModelAvatar : IDisposable
{
    System.Windows.FrameworkElement View { get; }
    void Update(string model, string state);
}
