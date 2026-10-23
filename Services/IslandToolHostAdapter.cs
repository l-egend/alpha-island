using System.Text.Json;
using System.Windows.Threading;
using AlphaIsland.AgentTools.IslandTools;
using AlphaIsland.PluginSdk;
using alpha_island.Core;
using alpha_island.ViewModels;

namespace alpha_island.Services;

/// <summary>Owns desktop execution and verification; the window only supplies existing service instances.</summary>
internal sealed class IslandToolHostAdapter(Dispatcher dispatcher, Func<VolumeService?> volume,
    Func<SmtcService?> media, FocusViewModel focus, Func<PomodoroOptions> options,
    Func<CancellationToken, ValueTask<DesktopContextSnapshot>> context,
    Func<string, JsonElement, CancellationToken, ValueTask<ToolResult>>? reminders = null,
    Func<IReadOnlyList<string>, CancellationToken, ValueTask<DesktopContextSnapshot>>? information = null) : IIslandToolHost
{
    public async ValueTask<ToolResult> ExecuteAsync(string command, JsonElement arguments, CancellationToken cancellationToken)
        => await (await dispatcher.InvokeAsync(() => ExecuteCore(command, arguments, cancellationToken)));
    private async Task<ToolResult> ExecuteCore(string command, JsonElement args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        switch (command)
        {
            case "set_volume":
            case "set_mute":
                var output = volume();
                int? percent = command == "set_volume" ? args.GetProperty("percent").GetInt32() : null;
                bool? muted = command == "set_mute" ? args.GetProperty("muted").GetBoolean() : null;
                if (output == null)
                {
                    return await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                        using var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Console);
                        var endpoint = device.AudioEndpointVolume;
                        if (percent.HasValue) { endpoint.MasterVolumeLevelScalar = percent.Value / 100f; if (percent.Value > 0) endpoint.Mute = false; }
                        if (muted.HasValue) endpoint.Mute = muted.Value;
                        return ToolResult.Ok($"系统扬声器：{(int)Math.Round(endpoint.MasterVolumeLevelScalar * 100)}% · {(endpoint.Mute ? "已静音" : "未静音")}");
                    }, token);
                }
                if (percent.HasValue) output.SetVolume(percent.Value);
                if (muted.HasValue && output.IsMuted != muted.Value) output.ToggleMute();
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    await Task.Delay(100, token);
                    var actual = await Task.Run(() =>
                    {
                        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                        using var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Console);
                        return (Percent: (int)Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100), Muted: device.AudioEndpointVolume.Mute);
                    }, token);
                    if ((!percent.HasValue || Math.Abs(actual.Percent - percent.Value) <= 1) && (!muted.HasValue || actual.Muted == muted.Value))
                        return ToolResult.Ok($"系统扬声器：{actual.Percent}% · {(actual.Muted ? "已静音" : "未静音")}");
                }
                return ToolResult.Fail("已提交音量指令，系统未确认目标状态；未自动重试");
            case "media_control":
                var player = media();
                if (player?.Current == null) return ToolResult.Fail("当前没有可控制的媒体播放器");
                string action = args.GetProperty("action").GetString()!;
                if (!await player.TryControlAsync(action).WaitAsync(token)) return ToolResult.Fail("播放器未接受此操作，未自动重试");
                return ToolResult.Ok(action is "next" or "previous" ? "播放器已接受切歌指令，最终曲目以播放器为准" : action == "play" ? "播放器已确认播放指令" : "播放器已确认暂停指令");
            case "pomodoro_control":
                string operation = args.GetProperty("action").GetString()!;
                var timer = focus.Timer;
                if (operation != "start" && args.TryGetProperty("minutes", out _)) return ToolResult.Fail("只有 start 可以指定分钟数");
                if (operation == "start")
                {
                    if (timer.HasStarted || timer.Running) return ToolResult.Fail("已有番茄钟，请选择继续、暂停或重置");
                    int minutes = args.TryGetProperty("minutes", out var value) ? value.GetInt32() : timer.Options.FocusMinutes;
                    timer.Configure(timer.Options with { FocusMinutes = minutes }); timer.Start(DateTime.UtcNow);
                }
                else if (operation == "pause") timer.Pause(DateTime.UtcNow);
                else if (operation == "resume") timer.Start(DateTime.UtcNow);
                else if (operation == "reset") { timer.Reset(); timer.Configure(options()); }
                focus.Refresh();
                return ToolResult.Ok($"番茄钟：{timer.DisplayRemaining()} · {(timer.Running ? "运行中" : "已暂停")}");
            case "get_desktop_state": return ToolResult.Ok("当前可用的最小系统状态", await context(token));
            case "get_island_info":
                if (information == null) return ToolResult.Fail("宿主尚未提供岛内信息读取接口");
                var snapshot = await information([args.GetProperty("section").GetString()!], token);
                return snapshot.Fields.Count == 0 ? ToolResult.Fail("系统状态访问已关闭、会话已锁定或所选信息不可用")
                    : ToolResult.Ok("岛内最新可用状态；采样时间见返回数据，未上报的数值保持未知", snapshot);
            case "create_reminder":
            case "list_reminders":
            case "cancel_reminder":
            case "snooze_reminder": return reminders == null ? ToolResult.Fail("本机提醒不可用") : await reminders(command, args, token);
            default: return ToolResult.Fail("未开放的桌面指令");
        }
    }
}
