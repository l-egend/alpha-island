using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Text;
using alpha_island.Core;

namespace alpha_island.Plugins;

internal sealed partial class PluginHost
{
    internal static string StateText(Entry entry, bool requested)
    {
        if (entry.UninstallError.Length > 0) return L.T("卸载失败：") + DiagnosticRedactor.Redact(entry.UninstallError);
        if (entry.UninstallPending) return L.T("正在卸载");
        if (entry.Error.Length > 0) return (requested ? L.T("开关已开启 · 加载失败：") : L.T("加载失败：")) + DiagnosticRedactor.Redact(entry.Error);
        if (!requested) return !entry.Teardown.IsCompleted ? L.T("已关闭 · 正在停止") : L.T("未启用");
        if (entry.GameSuspended) return L.T("已启用 · 游戏模式暂停");
        if (entry.Running) return L.T("已启用 · 运行中");
        if (!entry.Teardown.IsCompleted) return L.T("已启用 · 等待旧进程退出");
        return entry.Attempted ? L.T("已启用 · 正在启动") : L.T("已启用 · 等待启动");
    }

    internal string DiagnosticsText()
    {
        var text = new StringBuilder();
        foreach (var entry in _entries)
        {
            bool requested = entry.Manifest != null && IsEnabled(_options, entry.Manifest.Id);
            text.Append(entry.Manifest?.Id ?? "无效清单").Append(" v").Append(entry.Manifest?.Version ?? "未知")
                .Append(": ").Append(StateText(entry, requested))
                .Append("; requested=").Append(requested).Append("; running=").Append(entry.Running)
                .Append("; worker=").Append(entry.Instance is Isolation.IsolatedPlugin proxy ? proxy.ProcessId : 0).AppendLine();
        }
        foreach (var entry in _entries)
            if (entry.Instance is Isolation.IsolatedPlugin remote && remote.Diagnostics.Length > 0)
                text.Append(entry.Manifest?.Id).Append(": ").AppendLine(remote.Diagnostics);
        return text.Length > 0 ? text.ToString() : L.T("尚未发现插件；检查插件目录或刷新列表。");
    }
}
