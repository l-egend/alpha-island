using AlphaIsland.PluginSdk;

namespace AlphaIsland.Plugins.ApiChat;

public partial class ApiChatView
{
    private IModelAvatar? _modelAvatar;
    private void SyncModelAvatar()
    {
        if (_disposed) return;
        _modelAvatar ??= _context.CreateModelAvatar();
        if (_modelAvatar == null) return;
        if (!ReferenceEquals(AiAvatarHost.Child, _modelAvatar.View)) AiAvatarHost.Child = _modelAvatar.View;
        string model = _aiStreaming && _activity.Model.Length > 0 ? _activity.Model : _options.GetText("AiModel", "");
        _modelAvatar.Update(model, _activity.State.ToString());
    }
}
