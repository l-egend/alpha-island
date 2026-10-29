using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void AgentInputWaitChecks(alpha_island.MainWindow window, Grid root)
    {
        using var lifetime = new RegistryCleanup(); var registry = lifetime.Registry;
        var options = AgentField<IslandOptions>(window, "_options");
        SettingsRegistry.Write(options, "AgentStatusEnabled", true);
        SettingsRegistry.Write(options, "MediaWindowHandoffEnabled", false);
        const string owner = "external:codex/input-fixture";
        void Report(string state)
        {
            Check(registry.Update("codex", "input-fixture", state, state == "input-wait" ? "Codex · 等待你的回复" : "Codex", 300,
                DateTime.UtcNow, out _, nativeThreadId:"input-thread", nativeTurnId:"input-turn"), "状态通道接受 " + state);
            PrivateCall(window, "UpdateAgentStatusUi"); PumpAnimations(100); root.UpdateLayout();
        }
        try
        {
            Report("thinking");
            long activity = AgentStatus.Snapshot.Single(a => a.Owner == owner).ActivityId;
            PrivateCall(window, "SetState", IslandState.Bar, true);
            Report("input-wait");
            string key = (string)PrivateCall(window, "AgentSessionKey", owner)!;
            var layer = (Grid)PrivateCall(window, "GetNotifyLayerElement", key)!;
            Check(AgentStatus.Snapshot.Single(a => a.Owner == owner).State.ToString() == "InputWait", "提问与工具审批使用不同的等待状态");
            Check(Descendants<TextBlock>(layer).Any(t => t.Text.Contains("等待回复")), "助手岛清楚显示等待回复，不显示执行中或等待授权");
            Check(AgentField<IslandState>(window, "_state") != IslandState.Bar, "已收起的助手收到输入请求时重新弹出提示");
            Check(AgentField<ApprovalRequest?>(window, "_approvalCurrent") == null, "输入提醒不进入允许或拒绝工具的权限队列");
            AgentStatus.Set(AgentRunState.ToolWait, "Fixture approval", "approval");
            Check(AgentStatus.State == AgentRunState.ToolWait, "等待回复不压过真实的工具权限审批");
            AgentStatus.Reset(owner:"approval");
            registry.ObserveNativeCodex("native.input", "input-thread", "input-turn", Environment.ProcessId, DateTime.UtcNow, DateTime.UtcNow, "Codex");
            Check(AgentStatus.Snapshot.Single(a => a.Owner == owner).State.ToString() == "InputWait", "原生运行状态补充读取不能盖掉等待回复");
            Report("input-wait");
            Check(AgentStatus.Snapshot.Single(a => a.Owner == owner).ActivityId == activity, "同一次输入请求重复报送不会产生新任务");
            Report("thinking");
            Check(!Descendants<TextBlock>(layer).Any(t => t.Text.Contains("等待回复")), "用户回复后撤下等待提示并恢复运行状态");
            Report("idle"); Report("input-wait");
            Check(!AgentStatus.Snapshot.Any(a => a.Owner == owner), "任务结束后迟到的输入请求不能重新弹出助手岛");
        }
        finally { registry.Clear(); AgentStatus.Reset(owner:"approval"); PrivateCall(window, "UpdateAgentStatusUi"); }
    }
}
