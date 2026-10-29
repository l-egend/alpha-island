using L = AlphaIsland.PluginSdk.Localization.UiText;
namespace alpha_island.Core;

internal sealed record HealthReading(DateTimeOffset At, double? Cpu, double? Memory, long? AvailableBytes, bool ExpectedLoad = false);
internal sealed record HealthAlert(string Id, string Title, string Detail, DateTimeOffset At);
internal sealed record HealthBaseline(double[] Cpu, double[] Memory);
internal sealed record ProcessHealthSample(int Id, long Started, string Name, double? Cpu, long PrivateBytes, ulong? WrittenBytes, bool IsForeground = false);

/// <summary>Warnings describe observed trends, not diagnoses. Missing/gapped samples do not extend a trend.</summary>
internal sealed class SystemHealthMonitor
{
    private readonly Queue<double> _cpu = new(), _memory = new();
    private readonly Dictionary<string, DateTimeOffset> _since = new(), _cooldown = new();
    private readonly HashSet<string> _active = new();
    private readonly Dictionary<(int Id,long Started), List<(DateTimeOffset At, ProcessHealthSample Sample)>> _processes = new();
    private DateTimeOffset _last;
    internal int BaselineSamples => _cpu.Count;
    internal HealthBaseline Snapshot() => new(_cpu.ToArray(),_memory.ToArray());
    internal void Restore(HealthBaseline? state)
    {
        Reset();
        foreach(double value in (state?.Cpu ?? []).TakeLast(120).Where(Valid)) _cpu.Enqueue(value);
        foreach(double value in (state?.Memory ?? []).TakeLast(120).Where(Valid)) _memory.Enqueue(value);
    }
    internal HealthAlert[] Observe(HealthReading frame)
    {
        if (_last != default && (frame.At <= _last || frame.At - _last > TimeSpan.FromSeconds(90))) _since.Clear();
        if (frame.At <= _last) return [];
        _last=frame.At;
        var alerts=new List<HealthAlert>();
        double median = Median(_cpu), deviation = Median(_cpu.Select(value=>Math.Abs(value-median)));
        bool cpuHot = !frame.ExpectedLoad && frame.Cpu is double cpu && Valid(cpu)
            && (cpu >= 90 || _cpu.Count >= 30 && cpu >= Math.Max(50, median+Math.Max(20,deviation*6)));
        if (Trend("cpu",cpuHot,frame.At,90)) alerts.Add(new("cpu",_cpu.Count>=30?L.T("CPU 持续高于近期基线"):L.T("CPU 持续高负载"),L.F($"CPU 当前约 {frame.Cpu:0}%，已持续至少90秒。可查看当前进程占用；编译、更新等正常任务也可能产生高负载。"),frame.At));
        bool memoryHot = frame.Memory is double memory && Valid(memory) && (memory>=96 || memory>=90 && frame.AvailableBytes is >=0 and < 1_073_741_824);
        if (Trend("memory",memoryHot,frame.At,90)) alerts.Add(new("memory",L.T("可用内存持续偏低"),L.F($"内存占用约 {frame.Memory:0}%，已持续至少90秒。可查看占用排行并自行关闭不需要的应用。"),frame.At));
        if (!cpuHot && frame.Cpu is double validCpu && Valid(validCpu) && !frame.ExpectedLoad) Add(_cpu,validCpu);
        if (!memoryHot && frame.Memory is double validMemory && Valid(validMemory)) Add(_memory,validMemory);
        return alerts.ToArray();
    }
    internal HealthAlert[] ObserveProcesses(ProcessHealthSample[] samples, DateTimeOffset now, bool expectedLoad)
    {
        if (expectedLoad) { _processes.Clear(); return []; }
        var present=samples.Take(256).Select(sample=>(sample.Id,sample.Started)).ToHashSet();
        foreach(var key in _processes.Keys.Where(key=>!present.Contains(key)).ToArray()) _processes.Remove(key);
        var alerts=new List<HealthAlert>();
        foreach(var sample in samples.Take(256))
        {
            if (sample.Id<=0 || sample.Name is not {Length:>0 and <=80} || sample.PrivateBytes<0 || sample.Cpu is double cpu && !Valid(cpu)) continue;
            var key=(sample.Id,sample.Started);
            if (!_processes.TryGetValue(key,out var history)) _processes[key]=history=[];
            if (history.Count>0 && (now<=history[^1].At || now-history[^1].At>TimeSpan.FromSeconds(90))) history.Clear();
            // Restoring an application can fault in caches and rebuild its UI.
            // Start a fresh trend at foreground entry rather than comparing it with an idle background baseline.
            if (sample.IsForeground && history.Count > 0 && !history[^1].Sample.IsForeground) history.Clear();
            history.Add((now,sample));
            history.RemoveAll(value=>now-value.At>TimeSpan.FromMinutes(12));
            while(history.Count>30) history.RemoveAt(0);
            string memoryId=$"process-memory:{sample.Id}:{sample.Started}";
            bool growing=false;
            if (!expectedLoad && history.Count>=10 && now-history[0].At>=TimeSpan.FromMinutes(8))
            {
                long start=history[0].Sample.PrivateBytes, growth=sample.PrivateBytes-start;
                var increments=history.Zip(history.Skip(1),(a,b)=>b.Sample.PrivateBytes-a.Sample.PrivateBytes).ToArray();
                int rising=increments.Count(value=>value>=1024*1024);
                var tail=history.Where(value=>now-value.At<=TimeSpan.FromMinutes(3)).ToArray();
                long recentGrowth=sample.PrivateBytes-tail[0].Sample.PrivateBytes;
                growing=growth>=256L*1024*1024 && growth>=start*.4 && rising>=(history.Count-1)*.8
                    && increments.Max()<=growth*.5 && recentGrowth>=Math.Max(32L*1024*1024,growth*.15);
            }
            if (growing && Allow(memoryId,now)) alerts.Add(new(memoryId,sample.Name+L.T(" 的私有内存持续增长"),
                L.F($"最近约{(now-history[0].At).TotalMinutes:0}分钟从 {history[0].Sample.PrivateBytes/1048576d:0} MB 增长到 {sample.PrivateBytes/1048576d:0} MB。这是趋势提示，缓存或任务处理也可能导致增长，不能据此认定内存泄漏。"),now));
            string writeId=$"process-write:{sample.Id}:{sample.Started}";
            var recent=history.Where(value=>now-value.At<=TimeSpan.FromMinutes(3)).ToArray();
            bool writes=!expectedLoad && recent.Length>=6 && now-recent[0].At>=TimeSpan.FromSeconds(140)
                && recent.Zip(recent.Skip(1),(a,b)=> a.Sample.WrittenBytes is ulong first && b.Sample.WrittenBytes is ulong last && last>=first
                    && (last-first)/(b.At-a.At).TotalSeconds>=50*1024*1024).All(value=>value);
            if (writes && Allow(writeId,now)) alerts.Add(new(writeId,sample.Name+L.T(" 的 I/O 写入持续较高"),
                L.T("最近数分钟观察到较高的进程写入量。此指标包含非磁盘 I/O，可能是下载、缓存或正常工作，不能据此认定在大量写日志。"),now));
        }
        // Process churn must not grow cooldown dictionaries for an entire desktop session.
        foreach(string id in _cooldown.Where(pair=>now-pair.Value>TimeSpan.FromHours(1)).Select(pair=>pair.Key).ToArray()) _cooldown.Remove(id);
        return alerts.Take(3).ToArray();
    }
    private bool Trend(string id,bool active,DateTimeOffset now,int seconds)
    {
        if(!active){_since.Remove(id);_active.Remove(id);return false;}
        if(!_since.TryGetValue(id,out var since)){_since[id]=now;return false;}
        if(now-since<TimeSpan.FromSeconds(seconds) || _active.Contains(id) || !Allow(id,now)) return false;
        _active.Add(id);return true;
    }
    private bool Allow(string id,DateTimeOffset now)
    {
        if(_cooldown.TryGetValue(id,out var previous) && now-previous<TimeSpan.FromMinutes(30)) return false;
        if(_cooldown.Count>=512) _cooldown.Remove(_cooldown.MinBy(pair=>pair.Value).Key);
        _cooldown[id]=now;return true;
    }
    internal void Pause() { _since.Clear();_active.Clear();_processes.Clear();_last=default; }
    internal void Reset() { Pause();_cpu.Clear();_memory.Clear();_cooldown.Clear(); }
    private static void Add(Queue<double> values,double value){values.Enqueue(value);while(values.Count>120)values.Dequeue();}
    private static bool Valid(double value)=>double.IsFinite(value)&&value is >=0 and <=100;
    private static double Median(IEnumerable<double> values){var sorted=values.Order().ToArray();return sorted.Length==0?0:sorted[sorted.Length/2];}
}
