using System.Diagnostics;
using System.Runtime.InteropServices;
using alpha_island.Core;
using alpha_island.Interop;

namespace alpha_island.Services;

internal sealed class ProcessHealthReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes; }
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr handle,out IoCounters counters);
    private readonly Dictionary<(int,long),(DateTimeOffset At,TimeSpan Cpu)> _previous=new();
    internal ProcessHealthSample[] Read(DateTimeOffset now)
    {
        var result=new List<ProcessHealthSample>();
        NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(),out uint foreground);
        var processes=Process.GetProcesses();
        foreach(var process in processes)
        {
            using(process)
            {
                if(result.Count>=256)continue;
                try
                {
                    if(process.Id<=4 || process.HasExited)continue;
                    long start=process.StartTime.ToUniversalTime().Ticks;
                    var total=process.TotalProcessorTime;
                    var key=(process.Id,start);
                    double? cpu=null;
                    if(_previous.TryGetValue(key,out var previous) && (now-previous.At).TotalSeconds is >0 and <=90)
                        cpu=Math.Clamp((total-previous.Cpu).TotalSeconds/(now-previous.At).TotalSeconds/Environment.ProcessorCount*100,0,100);
                    ulong? written=GetProcessIoCounters(process.Handle,out var io)?io.WriteBytes:null;
                    string name=process.ProcessName;
                    if(name.Length>80)name=name[..80];
                    result.Add(new(process.Id,start,name,cpu,Math.Max(0,process.PrivateMemorySize64),written,process.Id==foreground));
                    _previous[key]=(now,total);
                }
                catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException){ }
            }
        }
        var present=result.Select(sample=>(sample.Id,sample.Started)).ToHashSet();
        foreach(var key in _previous.Keys.Where(key=>!present.Contains(key)).ToArray())_previous.Remove(key);
        return result.ToArray();
    }
}
