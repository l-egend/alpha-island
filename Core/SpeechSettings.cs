using System.IO;
using AlphaIsland.Speech;
using AlphaIsland.PluginSdk;
using alpha_island.Services;

namespace alpha_island.Core;

internal static class SpeechSettings
{
    internal const string DefaultUrl = "https://api.xiaomimimo.com/v1", DefaultModel = "mimo-v2.5-asr";
    internal static string SecretPath(string? root = null) => Path.Combine(root ?? AppPaths.Root, "config", "speech", "secret.dat");
    internal static bool KeyMatches(IslandOptions options) => SettingsRegistry.GetText(options, "SpeechSecretEndpoint", "") is not { Length: > 0 } endpoint
        || AiServicePresets.SameAddress(endpoint, SettingsRegistry.GetText(options, "SpeechBaseUrl", DefaultUrl));
    internal static SpeechRecognitionOptions Read(IslandOptions options, string? root = null)
    {
        string provider = SettingsRegistry.GetText(options, "SpeechProvider", "mimo");
        provider = AiServicePresets.Speech.FirstOrDefault(preset => preset.Id == provider)?.Protocol ?? provider;
        return new(provider, SettingsRegistry.GetText(options, "SpeechBaseUrl", DefaultUrl),
            SettingsRegistry.GetText(options, "SpeechModel", DefaultModel),
            KeyMatches(options) ? AiSecretStore.Load(SecretPath(root)) : "", SettingsRegistry.GetText(options, "SpeechLanguage", "auto"));
    }
    internal static bool SaveKey(string key, string? root = null) => AiSecretStore.TrySave(SecretPath(root), key);
    internal static void Validate(SpeechRecognitionOptions options)
    {
        if (options.Provider == "windows-local") return;
        _ = options.Endpoint(options.Provider is "mimo" or "chat-audio" ? "/chat/completions" : "/audio/transcriptions");
        if (options.Provider != "local-endpoint" && string.IsNullOrWhiteSpace(options.ApiKey))
            throw new SpeechRecognitionException("请在设置 → 智能助手 → 语音识别中保存语音接口的 API Key；它与对话模型的密钥独立。");
    }
}
