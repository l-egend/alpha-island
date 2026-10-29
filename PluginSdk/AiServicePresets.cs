namespace AlphaIsland.PluginSdk;

public sealed record AiServicePreset(string Id, string Name, string BaseUrl, string Model = "", string Protocol = "")
{
    public string DisplayName => Localization.UiText.T(Name);
    public override string ToString() => DisplayName;
}

public static class AiServicePresets
{
    public static IReadOnlyList<AiServicePreset> Speech { get; } = Array.AsReadOnly(new[]
    {
        new AiServicePreset("mimo", "小米 MiMo ASR", "https://api.xiaomimimo.com/v1", "mimo-v2.5-asr", "mimo"),
        new AiServicePreset("dashscope-asr", "阿里云百炼 · Qwen ASR", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen3-asr-flash", "chat-audio"),
        new AiServicePreset("siliconflow-asr", "硅基流动 · SenseVoice", "https://api.siliconflow.cn/v1", "FunAudioLLM/SenseVoiceSmall", "transcription"),
        new AiServicePreset("groq-asr", "Groq · Whisper", "https://api.groq.com/openai/v1", "whisper-large-v3-turbo", "transcription"),
        new AiServicePreset("openai-asr", "OpenAI · 语音转写", "https://api.openai.com/v1", "gpt-transcribe", "transcription"),
        new AiServicePreset("chat-audio", "音频对话接口（自定义）", "", Protocol: "chat-audio"),
        new AiServicePreset("transcription", "语音转写接口（自定义）", "", Protocol: "transcription"),
        new AiServicePreset("local-endpoint", "本地语音服务", "http://localhost:8080/v1", "whisper-1", "local-endpoint"),
        new AiServicePreset("windows-local", "Windows 本机语音", "", Protocol: "windows-local")
    });

    public static IReadOnlyList<AiServicePreset> Chat { get; } = Array.AsReadOnly(new[]
    {
        new AiServicePreset("custom", "自定义兼容接口", ""),
        new AiServicePreset("deepseek", "DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat"),
        new AiServicePreset("mimo", "小米 MiMo", "https://api.xiaomimimo.com/v1"),
        new AiServicePreset("dashscope", "阿里云百炼", "https://dashscope.aliyuncs.com/compatible-mode/v1"),
        new AiServicePreset("siliconflow", "硅基流动", "https://api.siliconflow.cn/v1"),
        new AiServicePreset("zhipu", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4"),
        new AiServicePreset("moonshot", "Kimi · 月之暗面", "https://api.moonshot.cn/v1"),
        new AiServicePreset("volcengine", "火山方舟 · 豆包", "https://ark.cn-beijing.volces.com/api/v3"),
        new AiServicePreset("openai", "OpenAI", "https://api.openai.com/v1"),
        new AiServicePreset("openrouter", "OpenRouter", "https://openrouter.ai/api/v1"),
        new AiServicePreset("groq", "Groq", "https://api.groq.com/openai/v1")
    });

    public static string BaseAddress(string value)
    {
        string root = value.Trim().TrimEnd('/');
        foreach (string suffix in new[] { "/chat/completions", "/audio/transcriptions", "/models" })
            if (root.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return root[..^suffix.Length];
        return root;
    }

    public static bool SameAddress(string first, string second) => string.Equals(BaseAddress(first), BaseAddress(second), StringComparison.OrdinalIgnoreCase);
}
