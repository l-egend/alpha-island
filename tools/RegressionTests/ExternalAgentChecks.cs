using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static async Task ExternalAgentChecks()
    {
        var now = DateTime.UtcNow;
        var registry = new ExternalAgentRegistry();
        try
        {
            Check(registry.Update("deepseek-harness", "session-a", "thinking", "Harness", 30, now, out _), "外部 agent 接收状态心跳");
            Check(AgentStatus.State == AgentRunState.Thinking, "外部思考状态进入现有状态层");
            AgentStatus.Set(AgentRunState.ToolWait, "审批", "approval");
            registry.Update("deepseek-harness", "session-a", "streaming", "Harness", 30, now, out _);
            Check(AgentStatus.State == AgentRunState.ToolWait, "外部输出不能覆盖待审批状态");
            AgentStatus.Reset(owner: "approval");
            Check(AgentStatus.State == AgentRunState.Streaming, "审批结束恢复外部输出状态");
            registry.Update("deepseek-harness", "session-b", "thinking", "B", 60, now, out _);
            registry.Sweep(now.AddSeconds(31));
            Check(AgentStatus.State == AgentRunState.Thinking && AgentStatus.Detail == "B", "过期只移除自己的会话状态");
            Check(registry.Snapshot.Length == 1, "断线清理过期连接");
            registry.Update("deepseek-harness", "session-b", "idle", "", 5, now.AddSeconds(31), out _);
            Check(!AgentStatus.IsBusy, "外部任务结束归还岛状态");
            Check(!registry.Update("ai/approval", "x", "thinking", "", 30, now, out _), "外部来源不能冒用内部 owner");
            Check(!registry.Update("deepseek-harness", "x", "unknown", "", 30, now, out _), "无效状态被拒绝");
            registry.Clear();
            for (int i = 0; i < 64; i++) registry.Update("limit", "s" + i, "idle", "", 30, now, out _);
            Check(!registry.Update("limit", "overflow", "idle", "", 30, now, out _), "外部会话容量受限");
            registry.Close();
            Check(!registry.Update("limit", "late", "thinking", "", 30, now, out _) && !AgentStatus.IsBusy,
                "关闭后迟到的状态不能留下悬挂图层");
        }
        finally { registry.Clear(); AgentStatus.Reset(owner: "approval"); }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var offered = new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bridge = new AgentBridgeService(port, request => { offered.TrySetResult(request); return true; });
        bridge.Start();
        using var http = new HttpClient { BaseAddress = new Uri(bridge.Endpoint), Timeout = TimeSpan.FromSeconds(10) };
        using var health = JsonDocument.Parse(await http.GetStringAsync("health"));
        Check(health.RootElement.GetProperty("statusReporting").GetBoolean() && !health.RootElement.GetProperty("apiDecisions").GetBoolean(), "桥接声明支持状态且禁止客户端放行");
        using var posted = await http.PostAsync("status", new StringContent("{\"source\":\"deepseek-harness\",\"session\":\"http\",\"state\":\"streaming\",\"detail\":\"Harness\"}", Encoding.UTF8, "application/json"));
        Check(posted.StatusCode == HttpStatusCode.OK && AgentStatus.State == AgentRunState.Streaming, "真实 HTTP 状态传入岛内");
        using var status = JsonDocument.Parse(await http.GetStringAsync("status"));
        Check(status.RootElement.GetProperty("connections").GetArrayLength() == 1, "HTTP 查询返回外部连接");
        using var invalid = await http.PostAsync("status", new StringContent("{\"source\":\"harness\",\"state\":\"evil\"}"));
        Check(invalid.StatusCode == HttpStatusCode.BadRequest, "HTTP 拒绝非法状态");
        using var malformed = await http.PostAsync("status", new StringContent("[]"));
        Check(malformed.StatusCode == HttpStatusCode.BadRequest, "HTTP 拒绝非对象状态请求");
        var approvalTask = http.PostAsync("approval", new StringContent("{\"id\":\"cancel-probe\",\"tool\":\"read-only.probe\",\"timeoutSec\":5}"));
        await offered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var cancel = await http.DeleteAsync("approval/cancel-probe");
        Check(cancel.StatusCode == HttpStatusCode.OK, "Harness 撤回审批及时取消岛上通知");
        using var decision = await approvalTask;
        using var result = JsonDocument.Parse(await decision.Content.ReadAsStringAsync());
        Check(!result.RootElement.GetProperty("approved").GetBoolean() && result.RootElement.GetProperty("decidedBy").GetString() == "client", "撤回请求只能拒绝，不能授权");
        AgentStatus.Set(AgentRunState.Thinking, "内部对话", "ai");
        bridge.Stop();
        Check(AgentStatus.State == AgentRunState.Thinking && AgentStatus.Detail == "内部对话", "停桥接只清理外部状态，保留 AI 对话");
        AgentStatus.Reset(owner: "ai");
        Check(!AgentStatus.IsBusy, "停桥接清理外部状态");
    }
}
