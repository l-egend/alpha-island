using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using alpha_island.Core;
using L = AlphaIsland.PluginSdk.Localization.UiText;

namespace alpha_island.Services;

internal sealed record CodexHookHandler(string Event, bool Enabled, string Trust);
internal sealed record CodexNativeInspection(bool Available, bool ConfigurationChecked, bool? HooksEnabled, bool? UserHooksAllowed,
    int Errors, int Warnings, CodexHookHandler[]? Handlers);
internal sealed record AgentIntegrationReadiness(string State, int RegisteredHandlers, bool ConfigurationChecked, bool TrustVerified, CodexNativeInspection? NativeInspection);
internal sealed record AgentIntegrationInfo(string Id, string Name, bool Detected, string Status, string Detail, string Scope, string Profile, bool AllProjects = false, AgentIntegrationReadiness? Readiness = null)
{
    public string StatusText => Status switch
    {
        "configured" => Id == "codex" ? CodexReadinessText() : Id == "claude-code" ? L.T("已接入 · 新会话生效，原生授权保留") : L.T("已接入"),
        "available" => L.T("已检测到 · 可接入"), "disabled" => L.T("已停用"), "removed" => L.T("接入已被移除 · 可手动恢复"),
        "not-installed" => L.T("未检测到应用 · 安装并启动一次后重新检测"),
        "profile-required" => L.T("已检测到 Codex · 请先打开并完成初始化后重新检测"),
        "runtime-missing" => L.T("运行环境缺失 · 请修复安装"), "scope-required" => L.T("已检测到 · 请选择接入的项目目录"),
        _ => L.F($"接入失败：{Detail}")
    };
    private string CodexReadinessText()
    {
        if (Readiness?.NativeInspection is { Available: true } native)
        {
            if (native.HooksEnabled == false) return L.T("已登记 · Codex 已关闭 Hooks；基础状态由本地任务补充");
            if (native.UserHooksAllowed == false) return L.T("已登记 · Codex 策略限制用户 Hooks");
            if (native.Errors > 0) return L.T("已登记 · Codex 报告 Hooks 配置错误，请修复接入");
            if (native.Handlers is { Length: 10 } handlers && handlers.Select(h => h.Event).ToHashSet(StringComparer.Ordinal).SetEquals(new[]
                { "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest", "PreCompact", "PostCompact", "Stop", "Interrupt", "SessionEnd" })
                && handlers.All(h => h.Enabled && h.Trust == "trusted") && native.ConfigurationChecked)
                return L.T("Hooks 已启用且已信任 · 请新开一轮对话验证状态");
            return L.T("已登记 · 请在 Codex 的 /hooks 中启用并确认信任，再重开对话");
        }
        return L.T(Readiness?.State switch
        {
            "hooks-disabled" => "已登记 · Codex 已关闭 Hooks；基础状态由本地任务补充",
            "policy-blocked" => "已登记 · Codex 策略限制用户 Hooks",
            "repair-required" => "接入文件、端口或处理器不完整 · 请修复接入",
            "configuration-unavailable" => "无法读取 Codex 接入配置 · 请检查文件权限",
            _ => "已登记 · 需在 Codex 确认 Hooks 信任；可检查状态通道"
        });
    }
}
internal sealed record AgentIntegrationReport(bool RuntimeAvailable, AgentIntegrationInfo[] Providers, string? Error = null);

/// <summary>统一接入入口；安装器与设置共用同一份检测、保留用户选择和更新逻辑。</summary>
internal static class AgentIntegrationService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static AgentIntegrationReport? _lastReport;
    private static AgentIntegrationReport Remember(AgentIntegrationReport report) { _lastReport = report; return report; }
    internal static string DiagnosticsText() => _lastReport is not { } report ? "尚未完成自动检测"
        : (report.RuntimeAvailable ? "接入运行环境已就绪" : "接入运行环境缺失") + "\n"
            + (report.Error ?? string.Join("\n", report.Providers.Select(p => p.Name + "：" + p.StatusText + (p.Detail.Length > 0 ? "；" + p.Detail : ""))));
    internal static async Task<AgentIntegrationReport> RunAsync(string mode, int port, string provider = "all", string? projectRoot = null,
        string? harnessHome = null, string? installDirectory = null, string? dataRoot = null, CancellationToken cancellationToken = default, bool allProjects = false)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string directory = installDirectory ?? AppContext.BaseDirectory;
            string script = Path.Combine(directory, "agent-integrations", "setup.ps1");
            if (!File.Exists(script))
            {
                // 开发构建只在有脚本的项目目录探测；发布版始终使用随包文件。
                string cursor = directory;
                while (!File.Exists(script) && Path.GetDirectoryName(cursor) is { } parent)
                { cursor = parent; script = Path.Combine(cursor, "packaging", "agent-setup.ps1"); }
                if (File.Exists(script)) directory = Path.GetDirectoryName(Path.GetDirectoryName(script)!)!;
                else return Remember(new(false, [], "接入组件缺失，请修复安装。"));
            }
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                "-Mode", mode, "-Provider", provider, "-InstallDirectory", directory, "-DataRoot", dataRoot ?? AppPaths.Root,
                "-Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
            if (!string.IsNullOrWhiteSpace(projectRoot)) { start.ArgumentList.Add("-ProjectRoot"); start.ArgumentList.Add(projectRoot); }
            if (allProjects) start.ArgumentList.Add("-AllProjects");
            if (!string.IsNullOrWhiteSpace(harnessHome)) { start.ArgumentList.Add("-HarnessHome"); start.ArgumentList.Add(harnessHome); }
            using var process = Process.Start(start) ?? throw new IOException("无法启动接入组件。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch { try { process.Kill(entireProcessTree: true); } catch { } throw; }
            string json = await output.ConfigureAwait(false);
            _ = await error.ConfigureAwait(false); // 不把第三方配置或 PowerShell 的详细路径写入日志。
            return Remember(JsonSerializer.Deserialize<AgentIntegrationReport>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new(false, [], "接入组件没有返回检测结果。"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Remember(new(false, [], "检测超时，可重新检测。")); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Remember(new(false, [], "检测失败：" + ex.Message)); }
        finally { Gate.Release(); }
    }
}
