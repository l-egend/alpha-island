using System.Windows;
using System.Windows.Threading;
using AlphaIsland.PluginSdk;
using AlphaIsland.Plugins.DeviceBattery;

internal static partial class Program
{
    private static async Task ReceiverLatencyLiveProbe()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var inventory = HidInventory.Enumerate();
        Console.WriteLine($"Inventory: {inventory.Count} interfaces, {watch.ElapsedMilliseconds} ms");
        for (int i = 0; i < 3; i++)
        {
            watch.Restart();
            var readings = await BatteryScanner.ReadAsync(timeout.Token, inventory);
            Console.WriteLine($"State scan {i + 1}: {watch.ElapsedMilliseconds} ms, {readings.Count} devices");
            foreach (var item in readings) Console.WriteLine($"  {item.Name}: connected={item.Connected}, battery={item.Percent?.ToString() ?? "unknown"}, charging={item.Charging}");
        }
    }
    private static void ReceiverLatencyChecks()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var context = new ReceiverTimingContext(cancelled.Token);
        using var plugin = new DeviceBatteryPlugin();
        plugin.Initialize(context);
        context.Services.Single().Start(cancelled.Token);
        Check(context.Tasks.Any(task => task.Interval <= TimeSpan.FromSeconds(3)),
            "外设状态变化使用至多 3 秒的实际调度周期，不等待原有 60 秒电量轮询");
        context.Services.Single().Stop();
    }

    private sealed class ReceiverTimingContext(CancellationToken lifetime) : IPluginContext
    {
        internal readonly List<PluginService> Services = [];
        internal readonly List<ReceiverTimingTask> Tasks = [];
        public string PluginId => "alpha.device-battery";
        public string PluginDirectory => AppContext.BaseDirectory;
        public Dispatcher Dispatcher => Dispatcher.CurrentDispatcher;
        public CancellationToken Lifetime => lifetime;
        public void RegisterPage(PluginPage page) { }
        public void RegisterSetting(PluginSetting setting) { }
        public void RegisterAction(PluginAction action) { }
        public void RegisterService(PluginService service) => Services.Add(service);
        public T GetSetting<T>(string key, T fallback) => fallback;
        public void OpenPage(string key) { }
        public void Log(string message) { }
        public IPluginScheduledTask Schedule(string key, TimeSpan interval, Action callback, bool initiallyPaused = false)
        { var task = new ReceiverTimingTask(interval); Tasks.Add(task); return task; }
    }
    private sealed class ReceiverTimingTask(TimeSpan interval) : IPluginScheduledTask
    {
        internal TimeSpan Interval = interval;
        public bool IsPaused { get; private set; }
        public void SetPaused(bool paused) => IsPaused = paused;
        public void SetInterval(TimeSpan interval) => Interval = interval;
        public void Dispose() { }
    }
}
