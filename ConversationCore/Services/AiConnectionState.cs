using System.IO;
using System.Security.Cryptography;
using alpha_island.Conversations;

namespace alpha_island.Services;

/// <summary>Persist successful connection for the current endpoint and encrypted credential file.</summary>
internal static class AiConnectionState
{
    internal static bool Update(IChatSettings options, string? secretFile)
    {
        bool ready = false;
        try
        {
            var config = ChatConfig.From(options, includeKey: false);
            ready = config.IsUsable && secretFile != null && File.Exists(secretFile)
                && options.GetText("AiVerifiedEndpoint", "") == config.ModelsEndpoint
                && options.GetText("AiVerifiedKeyFile", "") == FileHash(secretFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        options.SetValue("AiEnabled", ready);
        return ready;
    }
    internal static bool Verify(IChatSettings options, string? secretFile, ChatConfig config)
    {
        Invalidate(options);
        if (!config.HasKey || !config.IsUsable || secretFile == null || !File.Exists(secretFile)) return false;
        try
        {
            options.SetValue("AiVerifiedEndpoint", config.ModelsEndpoint);
            // This hashes the DPAPI ciphertext; no credential or hash of its plaintext is stored in settings.
            options.SetValue("AiVerifiedKeyFile", FileHash(secretFile));
            return Update(options, secretFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Invalidate(options); return false; }
    }
    internal static void Invalidate(IChatSettings options)
    {
        options.SetValue("AiVerifiedEndpoint", "");
        options.SetValue("AiVerifiedKeyFile", "");
        options.SetValue("AiEnabled", false);
    }
    private static string FileHash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }
}
