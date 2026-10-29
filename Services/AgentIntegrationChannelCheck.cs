using System.Net.Http;
using System.Text.Json;
using L = AlphaIsland.PluginSdk.Localization.UiText;

namespace alpha_island.Services;

internal static class AgentIntegrationChannelCheck
{
    internal static async Task<string> CheckAsync(int port)
    {
        try
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
            using var json = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/health").ConfigureAwait(false));
            var health = json.RootElement;
            if (!health.TryGetProperty("service", out var service) || service.GetString() != "alpha-island-agent-bridge")
                return L.T("状态通道被其他程序占用 · 请检查桥接端口");
            if (!health.TryGetProperty("codexNativeStatus", out var native)) return L.T("状态通道已连接 · 请更新运行中的 Alpha Island");
            string mode = native.TryGetProperty("mode", out var value) ? value.GetString() ?? "" : "";
            return L.T(mode switch
            {
                "observing" => "状态通道已连接 · 本地任务状态可用；请在 Codex 发送新指令验证",
                "paused" => "状态通道已连接 · 状态显示关闭或游戏模式中，补充检测已暂停",
                "project-scope" => "状态通道已连接 · 当前仅接入指定项目，需依靠该项目的 Hooks",
                "disabled" => "状态通道已连接 · Codex 接入已停用",
                "waiting-for-codex" => "状态通道已连接 · 等待 Codex 启动",
                "endpoint-mismatch" => "Codex 保存的端口与岛不一致 · 请修复接入",
                "not-configured" => "状态通道已连接 · 尚未登记 Codex 接入",
                "native-schema-unsupported" or "native-store-unavailable" => "状态通道已连接 · 当前 Codex 无可用任务状态表，请检查 Hooks 信任",
                _ => "状态通道已连接 · 本地任务状态暂不可读，请检查 Hooks 接入或导出诊断"
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        { return L.T("状态通道未连接 · 请启用 Agent 桥接并检查端口"); }
    }
}
