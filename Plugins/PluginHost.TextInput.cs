using AlphaIsland.PluginSdk;

namespace alpha_island.Plugins;

internal sealed partial class PluginHost
{
    internal async Task<PluginTextInputResult> SubmitTextAsync(string pageId, string requestId, string text)
    {
        var entry = _entries.FirstOrDefault(item => item.Running && item.Pages.Any(page => page.Id == pageId && page.IsAvailable(_options)));
        if (entry?.Manifest == null || entry.Instance is not Isolation.IsolatedPlugin proxy || entry.Context == null)
            return new(false, "请先配置并启用 API 对话插件。");
        var page = entry.Context.Pages.FirstOrDefault(page => PageId(entry.Manifest.Id, page.Key) == pageId);
        if (page == null || text.Length is 0 or > 12000) return new(false, "语音指令为空或过长。");
        try { return await proxy.SubmitTextAsync(page.Key, requestId, text); }
        catch (Exception error)
        {
            if (ReferenceEquals(entry.Instance, proxy)) Fail(entry, error);
            return new(false, "助手连接中断，请重新打开 API 对话检查。");
        }
    }
    internal async Task CancelTextAsync(string pageId, string requestId)
    {
        var entry = _entries.FirstOrDefault(item => item.Running && item.Pages.Any(page => page.Id == pageId));
        if (entry?.Manifest == null || entry.Instance is not Isolation.IsolatedPlugin proxy || entry.Context == null) return;
        var page = entry.Context.Pages.FirstOrDefault(page => PageId(entry.Manifest.Id, page.Key) == pageId);
        if (page == null) return;
        try { await proxy.CancelTextAsync(page.Key, requestId); }
        catch (Exception error) { if (ReferenceEquals(entry.Instance, proxy)) Fail(entry, error); }
    }
}
