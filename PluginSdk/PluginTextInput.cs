namespace AlphaIsland.PluginSdk;

/// <summary>Explicit user text input, handled inside the plugin's process and existing conversation.</summary>
public interface IPluginTextInput
{
    Task<PluginTextInputResult> SubmitTextAsync(string requestId, string text, CancellationToken cancellationToken);
}

public sealed record PluginTextInputResult(bool Accepted, string Message, bool Failed = false);
