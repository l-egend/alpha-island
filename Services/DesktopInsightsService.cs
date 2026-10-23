using alpha_island.Core;

namespace alpha_island.Services;

internal sealed record DesktopInsightFrame(DateTimeOffset Now,long Stamp,bool Locked,bool Quiet,bool ExpectedLoad,
    bool Work,bool Meeting,bool Media,bool Focus,double? IdleSeconds,double? Cpu,double? Memory,long? AvailableBytes);
internal sealed record DesktopInsightsState(HabitLearningState Habits,HealthBaseline Health);

/// <summary>Coarse learning and health trends share one cadence. Process reads are bounded background work.</summary>
internal sealed class DesktopInsightsService : IDisposable
{
    private readonly HabitLearner _habits=new(TimeZoneInfo.Local);
    private readonly SystemHealthMonitor _health=new();
    private readonly ProcessHealthReader _processReader=new();
    private readonly Func<DesktopInsightFrame> _read;
    private readonly LocalStateFile<DesktopInsightsState>? _file;
    private readonly IslandTicker.Subscription? _tick;
    private bool _learning=true,_healthEnabled=true,_disposed,_busy;
    private int _focusMinutes=25,_breakMinutes=75;
    private DateTimeOffset _lastProcess,_lastSave;
    private DesktopInsightFrame? _frame;
    private ProcessHealthSample[] _processes=[];
    private readonly List<HealthAlert> _recent=[];
    internal event Action? Changed;
    internal event Action<HabitSuggestion>? SuggestionReady;
    internal event Action<HealthAlert>? HealthReady;
    internal HabitSuggestion[] Suggestions=>_learning?_habits.Suggestions(DateTimeOffset.Now,_focusMinutes,_breakMinutes):[];
    internal ProcessHealthSample[] Processes=>_processes.OrderByDescending(sample=>sample.Cpu).ThenByDescending(sample=>sample.PrivateBytes).Take(12).ToArray();
    internal HealthAlert[] Alerts=>_recent.AsEnumerable().Reverse().Take(20).ToArray();
    internal string LearningStatus
    {get{_habits.Prune(DateTimeOffset.Now);return !_learning?"习惯学习已关闭":$"已观察 {_habits.ObservedDays} 天 · {_habits.CompletedSessions} 段有效时长；至少5个稳定观察日后才提出时段建议。";}}
    internal string HealthStatus=>!_healthEnabled?"系统健康预警已关闭":$"CPU {_frame?.Cpu?.ToString("0")??"—"}% · 内存 {_frame?.Memory?.ToString("0")??"—"}% · 基线样本 {_health.BaselineSamples}/120";
    internal bool LearningEnabled=>_learning;
    internal bool HealthEnabled=>_healthEnabled;
    internal DesktopInsightsService(IslandTicker ticker,string? path,Func<DesktopInsightFrame> read)
    {
        _read=read;
        if(path==null)return;
        _file=new(path,maximumBytes:256*1024,encrypted:true);
        var state=_file.Read();_habits.Restore(state?.Habits,DateTimeOffset.Now);_health.Restore(state?.Health);
        _tick=ticker.Subscribe("desktop-insights",TimeSpan.FromSeconds(15),TickNow);
    }
    internal void Configure(bool learning,bool health,int focusMinutes,int breakMinutes)
    {
        _habits.Prune(DateTimeOffset.Now);
        if(_learning&&!learning)_habits.Pause();
        if(_healthEnabled&&!health){_health.Pause();_processes=[];}
        _learning=learning;_healthEnabled=health;_focusMinutes=focusMinutes;_breakMinutes=breakMinutes;
        _tick?.SetPaused(!learning&&!health);Changed?.Invoke();
    }
    internal void FocusCompleted(int minutes)
    {if(!_learning||_disposed)return;_habits.FocusCompleted(minutes,DateTimeOffset.Now);Save();Changed?.Invoke();}
    internal void ClearLearning(){_habits.Clear();Save();Changed?.Invoke();}
    internal void ResetBaseline(){_health.Reset();_recent.Clear();_processes=[];_lastProcess=default;Save();Changed?.Invoke();}
    internal void Respond(string id,bool accepted)
    {_habits.Respond(id,accepted,DateTimeOffset.Now);Save();Changed?.Invoke();}
    internal async void TickNow()
    {
        if(_disposed||_busy||!_learning&&!_healthEnabled)return;
        _busy=true;
        try
        {
            var frame=_read();_frame=frame;
            if(frame.Locked){_habits.Pause();_health.Pause();_processes=[];return;}
            if(_learning)_habits.Observe(new(frame.Now,frame.Stamp,frame.IdleSeconds is >=0 and <300,
                frame.Work,frame.Meeting,frame.Media,frame.Focus,frame.IdleSeconds));
            if(_healthEnabled)
            {
                foreach(var warning in _health.Observe(new(frame.Now,frame.Cpu,frame.Memory,frame.AvailableBytes,frame.ExpectedLoad)))Publish(warning);
                if(!frame.ExpectedLoad && frame.Now-_lastProcess>=TimeSpan.FromSeconds(30))
                {
                    _lastProcess=frame.Now;
                    var samples=await Task.Run(()=>_processReader.Read(frame.Now));
                    if(_disposed||!_healthEnabled||_read().Locked)return;
                    _processes=samples;
                    foreach(var warning in _health.ObserveProcesses(samples,frame.Now,false))Publish(warning);
                }
                else if(frame.ExpectedLoad){_health.ObserveProcesses([],frame.Now,true);_processes=[];}
            }
            if(_learning && !frame.Quiet && !frame.ExpectedLoad && !frame.Focus)
            {
                var suggestion=Suggestions.FirstOrDefault(value=>_habits.CanOffer(value.Id,frame.Now));
                if(suggestion!=null){_habits.Offered(suggestion.Id,frame.Now);Save();SuggestionReady?.Invoke(suggestion);}
            }
            if(frame.Now-_lastSave>=TimeSpan.FromMinutes(1))Save();
            Changed?.Invoke();
        }
        catch(Exception ex) when(ex is not OutOfMemoryException){IslandLog.Warn("DesktopInsights","本地分析暂不可用："+ex.GetType().Name);}
        finally{_busy=false;}
    }
    private void Publish(HealthAlert warning)
    {_recent.Add(warning);while(_recent.Count>20)_recent.RemoveAt(0);HealthReady?.Invoke(warning);}
    private void Save(){_lastSave=DateTimeOffset.Now;_habits.Prune(_lastSave);_file?.Write(new(_habits.Snapshot(),_health.Snapshot()));}
    public void Dispose()
    {if(_disposed)return;_disposed=true;_tick?.Dispose();Save();_file?.Dispose();Changed=null;SuggestionReady=null;HealthReady=null;}
}
