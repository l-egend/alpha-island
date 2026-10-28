using AlphaIsland.PluginSdk;

namespace alpha_island.Plugins;

internal sealed partial class PluginHost
{
    internal async Task<PluginFileInputResult> AttachFilesAsync(string pageId, IReadOnlyList<string> files)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _gameSuspended) return new(false, "游戏模式已暂停本机 Agent，请退出游戏模式后重试。");
        string[] paths;
        try { paths = PluginFileInput.Validate(files); }
        catch (Exception error) when (error is ArgumentException or System.IO.IOException or UnauthorizedAccessException)
        { return new(false, error.Message); }
        var entry = _entries.FirstOrDefault(entry => entry.Running && entry.Pages.Any(page => page.Id == pageId));
        if (entry?.Instance is not Isolation.IsolatedPlugin proxy || entry.Context == null || entry.Manifest == null)
            return new(false, "请先启用本机 Agent 插件，并等待启动完成。");
        var page = entry.Context.Pages.FirstOrDefault(page => PageId(entry.Manifest.Id, page.Key) == pageId);
        if (page == null) return new(false, "本机 Agent 页面暂不可用。");
        try { return await proxy.AttachFilesAsync(page.Key, paths); }
        catch (Exception error)
        { return new(false, "附件未送达：" + Core.DiagnosticRedactor.Redact(error.Message)); }
    }
}
