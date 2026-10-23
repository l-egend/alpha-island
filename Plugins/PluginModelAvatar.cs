using System.Windows;
using System.Windows.Media;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using AlphaIsland.PluginSdk;

namespace alpha_island.Plugins;

/// <summary>Plugin surfaces reuse the host's bounded sprite cache and playback clock.</summary>
internal sealed class PluginModelAvatar : IModelAvatar
{
    private readonly bool _snapshotSurface;
    private readonly SemanticIcon _icon = new() { Shape = IconShape.Agent, Text = "\uE99A", Width = 42, Height = 42,
        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 211, 238)) };
    private bool _disposed;
    public FrameworkElement View => _icon;
    internal PluginModelAvatar(bool snapshotSurface) => _snapshotSurface = snapshotSurface;
    public void Update(string model, string state)
    {
        if (_disposed) return;
        string family = ModelArtwork.Family(model);
        var activity = state.ToLowerInvariant() switch
        { "thinking" => AgentRunState.Thinking, "streaming" => AgentRunState.Streaming, "working" => AgentRunState.Working,
            "toolwait" or "tool-wait" or "waiting" => AgentRunState.ToolWait, "error" => AgentRunState.Error, _ => AgentRunState.Idle };
        _icon.ToolTip = family.Length > 0 ? model : "AI 对话";
        _icon.Text = activity == AgentRunState.Error ? "\uE783" : "\uE99A";
        try { AgentAvatarMotion.Configure(_icon, ModelArtwork.Source(family), activity, null, _snapshotSurface); }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or System.Text.Json.JsonException
            or InvalidOperationException or ArgumentException)
        { AgentAvatarMotion.Release(_icon); _icon.Source = null; }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; AgentAvatarMotion.Release(_icon); _icon.Source = null; }
}
