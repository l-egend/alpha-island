using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using AlphaIsland.PluginSdk;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;

namespace alpha_island;

public partial class MainWindow
{
    private UserReminderService? _userReminders;
    private DesktopInsightsService? _insights;
    private AssistantCenterWindow? _assistantCenter;
    private readonly List<IDisposable> _screenHotkeys=[];
    private string _screenHotkeyKey="",_screenHotkeyStatus="";
    private void InitializePersonalTools()
    {
        if(_userReminders!=null)return;
        _userReminders=new(_ticker,_runtimeEnabled?Path.Combine(AppPaths.Root,"user-reminders.dat"):null,DeliverUserReminder);
        _userReminders.Cancelled+=id=>
        {
            foreach(var notice in _desktopInbox.History.Where(notice=>notice.Reminder?.Rule=="user-reminder"&&notice.Reminder.Actions.Any(action=>action.Target==id)).ToArray())
                ResolveAssistantNotification(notice.Key,L.T("提醒已取消"));
            RefreshDesktopInbox();
        };
        _insights=new(_ticker,_runtimeEnabled?Path.Combine(AppPaths.Root,"desktop-insights.dat"):null,ReadInsightFrame);
        _insights.SuggestionReady+=suggestion=>PostAssistantNotification(new("habit:"+suggestion.Id,"",L.T("Alpha Island · 本地学习"),suggestion.Title,suggestion.Detail,
            DateTimeOffset.Now,DesktopEventKind.Assistant){IsPinned=true,Reminder=new("habit",true,DateTimeOffset.Now.AddDays(7),
                [new("habit-accept",L.T("采纳建议"),suggestion.Id),new("habit-dismiss",L.T("暂不采纳"),suggestion.Id),new("assistant-center",L.T("查看学习记录"),"habits")])},!NotificationQuietEffective);
        _insights.HealthReady+=warning=>PostAssistantNotification(new("health:"+warning.Id,"",L.T("Alpha Island · 健康预警"),warning.Title,warning.Detail,
            warning.At,DesktopEventKind.Assistant){IsPinned=true,Reminder=new("health",true,DateTimeOffset.Now.AddDays(1),
                [new("assistant-center",L.T("查看健康详情"),"health"),new("health-taskmgr",L.T("任务管理器"))])},!NotificationQuietEffective);
        _workspace.Focus.PhaseCompleted+=()=>{if(_pomodoro.IsBreak)_insights?.FocusCompleted(_pomodoro.Options.FocusMinutes);};
        InitializeVoiceAssistant(); ConfigurePersonalTools();
    }
    private void ConfigurePersonalTools()
    {
        _userReminders?.Configure(SettingsRegistry.GetBool(_options,"NaturalRemindersEnabled",true));
        _insights?.Configure(SettingsRegistry.GetBool(_options,"HabitLearningEnabled",true),SettingsRegistry.GetBool(_options,"SystemHealthEnabled",true),
            SettingsRegistry.GetInt(_options,"PomodoroFocusMinutes",25),SettingsRegistry.GetInt(_options,"AssistantUsageBreakMinutes",75));
        ConfigureScreenHotkeys();
        ConfigureVoiceAssistant();
    }
    private DesktopInsightFrame ReadInsightFrame()
    {
        var now=DateTimeOffset.UtcNow;
        string app=ForegroundApplication()??"";
        bool focus=_pomodoro.Running&&!_pomodoro.IsBreak;
        bool work=focus||AgentStatus.Snapshot.Any(agent=>agent.State is AgentRunState.Thinking or AgentRunState.Streaming or AgentRunState.ToolWait or AgentRunState.InputWait)
            || new[]{"code","devenv","rider64","pycharm64","idea64","clion64","notepad++","notepad","obsidian","WINWORD","EXCEL","POWERPNT","wps","typora"}.Contains(app,StringComparer.OrdinalIgnoreCase);
        var resources=_workspace.Resources.Snapshot;
        var memory=_resourceMonitor.Memory;
        bool meeting=_desktopAssistant?.PrivacyFresh==true&&_desktopAssistant.PrivacySnapshot.Any(item=>MeetingGuard.IsMeetingApp(item.AppName));
        bool expected=_gameActivity?.Current.Active==true||new[]{"msbuild","dotnet","ffmpeg","7z","7zFM","handbrake","blender"}.Contains(app,StringComparer.OrdinalIgnoreCase);
        return new(now,Environment.TickCount64,_sessionLocked||_exiting,NotificationQuietEffective||!SettingsRegistry.GetBool(_options,"AgentProactiveEnabled",true),expected,work,meeting,
            _workspace.Media.Current is { IsPlaying: true, ContentKind: MediaContentKind.Music },focus,_runtimeEnabled?Interop.UserIdleTime.ReadSeconds():null,
            _resourceMonitor.CpuEnabled&&now-_resourceMonitor.CpuSampledAt<TimeSpan.FromSeconds(60)?resources.CpuPercent:null,
            _resourceMonitor.MemoryEnabled&&now-_resourceMonitor.MemorySampledAt<TimeSpan.FromSeconds(30)?resources.MemoryPercent:null,
            memory.Valid?(long)memory.AvailableBytes:null);
    }
    private ValueTask<ToolResult> ExecuteUserReminderTool(string command,JsonElement args,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(_userReminders==null||_exiting||_sessionLocked)return ValueTask.FromResult(ToolResult.Fail(L.T("本地提醒暂不可用。")));
        return ValueTask.FromResult(command switch
        {
            "create_reminder"=>_userReminders.Create(args.GetProperty("text").GetString()??""),
            "list_reminders"=>_userReminders.List(),
            "cancel_reminder"=>args.TryGetProperty("id",out var id)?_userReminders.Cancel(id.GetString()??""):
                args.TryGetProperty("title",out var title)?_userReminders.CancelByTitle(title.GetString()??""):ToolResult.Fail(L.T("请提供提醒ID或完整事项名称。")),
            "snooze_reminder"=>_userReminders.Snooze(args.GetProperty("id").GetString()??"",args.GetProperty("minutes").GetInt32()),
            _=>ToolResult.Fail(L.T("未开放的提醒指令。"))
        });
    }
    private bool DeliverUserReminder(ReminderDelivery delivery)
    {
        if(_exiting)return false;
        string when=TimeZoneInfo.ConvertTime(delivery.Due,TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm");
        PostAssistantNotification(new(delivery.Key,"",L.T("Alpha Island · 提醒"),delivery.Title,
            (delivery.Late?L.T("错过的提醒已补记。原定时间："):L.T("原定时间："))+when,DateTimeOffset.Now,DesktopEventKind.Assistant,Importance:NotificationImportance.Important)
        {IsPinned=true,Reminder=new("user-reminder",true,DateTimeOffset.Now.AddDays(7),[new("reminder-snooze",L.T("10分钟后再提醒"),delivery.JobId),new("assistant-center",L.T("管理提醒"),"reminders")])},!delivery.Late);
        return true;
    }
    private void OpenAssistantCenter(string section="reminders")
    {
        if (section == "voice") { _ = ToggleVoiceAssistantAsync(); return; }
        if (section == "speech-settings")
        { if (!_sessionLocked) Process.Start(new ProcessStartInfo(VoiceRecognitionProblem.SpeechSettings) { UseShellExecute = true }); return; }
        if(_sessionLocked||_exiting||_userReminders==null||_insights==null)return;
        if(_assistantCenter==null)
        {
            _assistantCenter=new(_userReminders,_insights,ApplyHabitSuggestion);
            _assistantCenter.Closed+=(_,_)=>_assistantCenter=null;
        }
        _assistantCenter.SelectSection(section);Interop.UtilityWindowPresenter.Show(_assistantCenter);
    }
    private ToolResult ApplyHabitSuggestion(string id)
    {
        var suggestion=_insights?.Suggestions.FirstOrDefault(value=>value.Id==id);
        if(suggestion==null)return ToolResult.Fail(L.T("这条建议已失效或样本不足，请查看当前学习记录。"));
        if(suggestion.ReminderText.Length>0)
        {
            var result=_userReminders?.Create(suggestion.ReminderText)??ToolResult.Fail(L.T("提醒暂不可用。"));
            if(!result.Success)return result;
        }
        else
        {
            string key=suggestion.FocusMinutes>0?"PomodoroFocusMinutes":"AssistantUsageBreakMinutes";
            SettingsRegistry.Write(_options,key,suggestion.FocusMinutes>0?suggestion.FocusMinutes:suggestion.BreakMinutes);
            if(_runtimeEnabled)_options.Save();
            ApplyOptions();_settingsWindow?.LoadFrom(_options);
        }
        _insights!.Respond(id,true);ResolveAssistantNotification("habit:"+id,L.T("已采纳建议"));RefreshDesktopInbox();
        return ToolResult.Ok(L.T("已采纳建议。可在管理提醒或设置中修改。"));
    }
    private void ConfigureScreenHotkeys()
    {
        if(!_runtimeEnabled||_exiting)return;
        bool enabled=SettingsRegistry.GetBool(_options,"ScreenToolsHotkeysEnabled",true);
        string ocr=SettingsRegistry.GetText(_options,"ScreenOcrShortcut","Ctrl+Alt+O"),color=SettingsRegistry.GetText(_options,"ScreenColorShortcut","Ctrl+Alt+C");
        string key=enabled?ocr+"|"+color:"off";
        if(key==_screenHotkeyKey)return;
        foreach(var registration in _screenHotkeys)registration.Dispose();_screenHotkeys.Clear();_screenHotkeyKey=key;
        var statuses=new List<string>();
        if(enabled)foreach(var entry in new[]{(ocr,(Action)(()=>LaunchScreenCapture(false))),(color,(Action)(()=>LaunchScreenCapture(true)))})
        {
            try
            {
                if(new KeyGestureConverter().ConvertFromInvariantString(entry.Item1) is not KeyGesture gesture||gesture.Key==Key.None
                    ||(gesture.Modifiers&(ModifierKeys.Control|ModifierKeys.Alt|ModifierKeys.Windows))==0)throw new ArgumentException();
                if(HotkeyService.TryRegisterSystem(new(gesture.Modifiers,gesture.Key),entry.Item2,out var registration,out string error))
                {_screenHotkeys.Add(registration!);statuses.Add(entry.Item1+L.T(" 已注册"));}
                else statuses.Add(entry.Item1+"："+error);
            }
            catch(Exception ex)when(ex is ArgumentException or NotSupportedException or FormatException){statuses.Add(entry.Item1+L.T("：快捷键格式无效"));}
        }
        _screenHotkeyStatus=enabled?string.Join("；",statuses):L.T("识字与取色快捷键已关闭");
        DiagnosticsReport.RegisterSection("屏幕工具快捷键",()=>_screenHotkeyStatus);
        _settingsWindow?.SetScreenToolsStatus(_screenHotkeyStatus);
    }
    private async void LaunchScreenCapture(bool color)
    {
        if(_exiting||_sessionLocked)return;
        try
        {
            var tools=EnsureScreenTools();
            if(color)await tools.PickColorAsync(returnToWindow:false);else await tools.RecognizeAsync(copy:true,returnToWindow:false);
        }
        catch(Exception ex)when(ex is not OutOfMemoryException){IslandLog.Error("ScreenTools",ex);OnScreenToolCompleted(new("error",L.T("屏幕工具暂不可用，请打开翻译/扫码窗口查看状态。")));}
    }
    private void OnScreenToolCompleted(ScreenToolOutput output)
    {
        string text=SensitiveTextDetector.Inspect(output.Text).Redacted;
        if(text.Length>160)text=text[..160]+"…";
        var notice=new DesktopNotification("screen-tool:"+Guid.NewGuid().ToString("N"),"",L.T("Alpha Island · 屏幕工具"),output.Kind=="ocr"?L.T("已识别并复制原文"):output.Kind=="error"?L.T("屏幕工具未能完成"):L.T("已复制颜色"),text,DateTimeOffset.Now,DesktopEventKind.System);
        _desktopInbox.Add(notice,_desktopPolicy.CanPopup(notice,_sessionLocked),_desktopPolicy);StartNotificationUiTimer();TickDesktopNotifications();RefreshDesktopInbox();
    }
    private void StopPersonalTools()
    {
        StopVoiceAssistant();
        foreach(var registration in _screenHotkeys)registration.Dispose();_screenHotkeys.Clear();
        _assistantCenter?.Close();_assistantCenter=null;_userReminders?.Dispose();_insights?.Dispose();
    }
}
