using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;
using AlphaIsland.PluginSdk;
using alpha_island.Core;

namespace alpha_island.Plugins;

internal sealed partial class PluginHost : IDisposable
{
    internal sealed class Entry
    {
        internal required string Directory { get; init; }
        internal PluginManifest? Manifest { get; init; }
        internal string Error { get; set; } = "";
        internal string UninstallError { get; set; } = "";
        internal bool UninstallPending { get; set; }
        internal bool Attempted { get; set; }
        internal PluginLoadContext? Loader { get; set; }
        internal IIslandPlugin? Instance { get; set; }
        internal Context? Context { get; set; }
        internal List<PluginIslandPage> Pages { get; } = new();
        internal List<PluginService> Started { get; } = new();
        internal Task Teardown { get; set; } = Task.CompletedTask;
        internal bool Active { get; set; }
        internal bool GameSuspended { get; set; }
        internal bool Running => Active && Instance != null && Error.Length == 0;
    }

    internal sealed partial class Context : IPluginContext
    {
        public IToolDispatcher Tools => PluginId == "ai.api-chat" || _host.AllowPluginTools ? _host.ToolDispatcher ?? UnavailableTools.Instance : UnavailableTools.Instance;
        public void RegisterTool(IToolHandler tool)
        {
            VerifyRegistration(tool.Definition.Name);
            if (_host.ToolDispatcher is AlphaIsland.AgentTools.ToolRegistry registry)
                registry.Register(PluginId, tool, Lifetime, () => _host.AllowPluginTools);
        }
        public ValueTask<DesktopContextSnapshot> GetDesktopContextAsync(string prompt, CancellationToken cancellationToken = default)
            => (PluginId == "ai.api-chat" || _host.AllowPluginTools) ? _host.ContextReader?.Invoke(prompt, cancellationToken) ?? ValueTask.FromResult(DesktopContextSnapshot.Empty) : ValueTask.FromResult(DesktopContextSnapshot.Empty);
        private readonly PluginHost _host;
        private readonly Entry _entry;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly List<PluginPage> Pages = new();
        internal readonly List<PluginSetting> Settings = new();
        internal readonly List<PluginService> Services = new();
        internal readonly List<PluginNetworkSource> NetworkSources = new();
        internal readonly List<PluginAction> Actions = new();
        internal Func<System.Windows.FrameworkElement>? SettingsView;
        internal bool Sealed;
        internal Context(PluginHost host, Entry entry) { _host = host; _entry = entry; }
        public string PluginId => _entry.Manifest!.Id;
        public string PluginDirectory => _entry.Directory;
        public Dispatcher Dispatcher => _host._dispatcher;
        public CancellationToken Lifetime => Cancellation.Token;
        private void VerifyRegistration(string key)
        {
            Dispatcher.VerifyAccess();
            if (Sealed) throw new InvalidOperationException("只能在 Initialize 中注册组件");
            if (!PluginManifest.ValidKey(key)) throw new ArgumentException("组件 Key 仅支持小写字母、数字、点、下划线和连字符");
        }
        public void RegisterPage(PluginPage page)
        {
            ArgumentNullException.ThrowIfNull(page); VerifyRegistration(page.Key);
            if (Pages.Any(p => p.Key == page.Key) || string.IsNullOrWhiteSpace(page.Title) || page.CreateView == null
                || !double.IsFinite(page.Height) || page.Height < 214 || page.Height > 320 || page.Order < 100)
                throw new ArgumentException("页面 Key 重复，或标题、工厂、高度（214–320）、排序（≥100）无效");
            Pages.Add(page);
        }
        public void RegisterSetting(PluginSetting setting)
        {
            ArgumentNullException.ThrowIfNull(setting); VerifyRegistration(setting.Key);
            if (Settings.Any(s => s.Key == setting.Key) || string.IsNullOrWhiteSpace(setting.Label) || !Enum.IsDefined(setting.Kind))
                throw new ArgumentException("设置 Key 重复或类型无效");
            if (setting.Kind == PluginSettingKind.Number && (!double.IsFinite(setting.Minimum) || !double.IsFinite(setting.Maximum)
                || setting.Maximum <= setting.Minimum || !double.IsFinite(setting.Step) || setting.Step <= 0))
                throw new ArgumentException("数值范围或步长无效");
            if (setting.Kind == PluginSettingKind.Choice && (setting.Choices == null || setting.Choices.Count == 0
                || setting.Choices.Any(string.IsNullOrWhiteSpace) || setting.Choices.Distinct().Count() != setting.Choices.Count))
                throw new ArgumentException("选项列表不能为空或重复");
            // 固定列表副本，防止插件注册后修改已有设置结构。
            setting = setting with { Choices = setting.Choices?.ToArray() };
            NormalizeValue(setting, JsonSerializer.SerializeToElement(setting.DefaultValue), strict: true);
            Settings.Add(setting);
        }
        public void RegisterService(PluginService service)
        {
            ArgumentNullException.ThrowIfNull(service); VerifyRegistration(service.Key);
            if (Services.Any(s => s.Key == service.Key) || service.Start == null || service.Stop == null)
                throw new ArgumentException("服务 Key 重复或回调为空");
            Services.Add(service);
        }
        public void RegisterNetworkSource(PluginNetworkSource source)
        {
            ArgumentNullException.ThrowIfNull(source); VerifyRegistration(source.Key);
            if (source.Read == null || string.IsNullOrWhiteSpace(source.Title) || NetworkSources.Any(s => s.Key == source.Key))
                throw new ArgumentException("网络来源 Key 重复或读取回调无效");
            NetworkSources.Add(source);
        }
        public T GetSetting<T>(string key, T fallback)
        {
            Dispatcher.VerifyAccess();
            var definition = Settings.FirstOrDefault(s => s.Key == key);
            if (definition == null) return fallback;
            var value = _host.ReadSetting(_entry, definition);
            try { return value.Deserialize<T>() ?? fallback; } catch { return fallback; }
        }
        public void OpenPage(string key)
        {
            Dispatcher.VerifyAccess();
            if (_entry.Running && _entry.Pages.Any(p => p.Id == PageId(PluginId, key) && p.IsAvailable(_host._options))) _host._open(PageId(PluginId, key));
        }
        public void RegisterAction(PluginAction action)
        {
            VerifyRegistration(action.Key);
            if (Actions.Any(a => a.Key == action.Key) || string.IsNullOrWhiteSpace(action.Title) || action.Invoke == null) throw new ArgumentException("插件操作无效或重复");
            Actions.Add(action);
        }
        public void RegisterSettingsView(Func<System.Windows.FrameworkElement> createView)
        { VerifyRegistration("settings"); if (SettingsView != null) throw new InvalidOperationException("设置界面已注册"); SettingsView = createView; }
        public string DataDirectory => _host.DataRoot;
        public string ConfigDirectory => Path.Combine(DataDirectory, "config");
        public T ReadConfiguration<T>(string key, T fallback)
        { Dispatcher.VerifyAccess(); try { return _host._options.Extras.TryGetValue(key, out var value) ? value.Deserialize<T>() ?? fallback : fallback; } catch { return fallback; } }
        public void WriteConfiguration(string key, object value)
        {
            Dispatcher.VerifyAccess(); if (Cancellation.IsCancellationRequested || _host._disposed) return;
            if (key == "AiApiKey" && value is not ""
                && !(value is JsonElement element && element.ValueKind == JsonValueKind.String && element.GetString() == ""))
                throw new ArgumentException("密钥必须通过加密仓保存");
            var json = JsonSerializer.SerializeToElement(value);
            if (_host._options.Extras.TryGetValue(key, out var old) && old.GetRawText() == json.GetRawText()) return;
            _host._options.Extras[key] = json;
            _host.ConfigurationChanged?.Invoke();
        }
        public void Refresh() { if (!_host._disposed && !Cancellation.IsCancellationRequested) _host.AvailabilityChanged?.Invoke(); }
        public void OpenSettings() { if (_entry.Running) _host.SettingsRequested?.Invoke(); }
        public IModelAvatar CreateModelAvatar() => new PluginModelAvatar(false);
        public void ReportActivity(string state, string detail) => ReportActivity(state, detail, PluginId == "ai.api-chat" ? ReadConfiguration("AiModel", "") : "");
        public void ReportActivity(string state, string detail, string model)
        {
            Dispatcher.VerifyAccess(); if (Cancellation.IsCancellationRequested || _host._disposed) return;
            string owner = "plugin:" + PluginId;
            if (state == "idle") AgentStatus.Finish(owner);
            else AgentStatus.Set(state switch { "working" => AgentRunState.Working, "streaming" => AgentRunState.Streaming, "error" => AgentRunState.Error, "waiting" => AgentRunState.ToolWait, _ => AgentRunState.Thinking }, detail, owner, ModelArtwork.Family(model));
        }
        public void Log(string message) => IslandLog.Info("Plugin:" + PluginId, message);
        public void ReportBluetoothBattery(PluginBluetoothBattery battery)
        {
            Dispatcher.VerifyAccess();
            if (_host._disposed || !_entry.Running || Cancellation.IsCancellationRequested
                || string.IsNullOrWhiteSpace(battery.Id) || battery.Id.Length > 128
                || string.IsNullOrWhiteSpace(battery.Name) || battery.Name.Length > 256
                || battery.Percent is < 0 or > 100) return;
            CaptureLegacyBattery(battery);
            _host.DeviceBatteryReported?.Invoke(battery with { Id = PluginId + "/" + battery.Id });
        }
        public void NotifyPage(string key, string title, string detail, double seconds = 6)
        {
            Dispatcher.VerifyAccess();
            if (_host._disposed || !_entry.Running || Cancellation.IsCancellationRequested) return;
            string id = PageId(PluginId, key);
            if (!_entry.Pages.Any(p => p.Id == id)) throw new ArgumentException("只能提醒本插件已注册的页面", nameof(key));
            if (string.IsNullOrWhiteSpace(title) || !double.IsFinite(seconds) || seconds < 1 || seconds > 30)
                throw new ArgumentException("提醒标题或持续时间无效");
            static string Limit(string value) => value.Length > 256 ? value[..256] : value;
            _host.PageNotificationRequested?.Invoke(id, Limit(title), Limit(detail ?? ""), seconds);
        }
    }

    private readonly Dispatcher _dispatcher;
    private readonly string[] _roots;
    private readonly Action<PluginIslandPage> _attach;
    private readonly Action<PluginIslandPage> _detach;
    private readonly Action<string> _open;
    private readonly List<Entry> _entries = new();
    private IslandOptions _options;
    private bool _disposed;
    internal PluginHost(Dispatcher dispatcher, IslandOptions options, IEnumerable<string> roots,
        Action<PluginIslandPage> attach, Action<PluginIslandPage> detach, Action<string> open)
    { _dispatcher = dispatcher; _options = options; _roots = roots.ToArray(); _attach = attach; _detach = detach; _open = open; }
    internal IReadOnlyList<Entry> Entries => _entries;
    internal Task ShutdownCompletion => Task.WhenAll(_entries.Select(entry => entry.Teardown));
    internal event Action? Changed;
    internal event Action<PluginBluetoothBattery>? DeviceBatteryReported;
    internal event Action<string, string, string, double>? PageNotificationRequested;
    internal string DataRoot { get; set; } = AppPaths.Root;
    internal event Action? ConfigurationChanged;
    internal event Action? AvailabilityChanged;
    internal event Action? SettingsRequested;
    private (Entry Owner, PluginAction Action)[] AvailableActionEntries()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return Array.Empty<(Entry, PluginAction)>();
        var available = new List<(Entry, PluginAction)>();
        foreach (var entry in _entries.Where(e => e.Running && e.Context != null).ToArray())
        {
            try
            {
                var actions = entry.Context!.Actions.ToArray().Where(action => action.Available?.Invoke() ?? true).ToArray();
                if (entry.Running) available.AddRange(actions.Select(action => (entry, action)));
            }
            catch (Exception ex) { Fail(entry, ex); }
        }
        return available.ToArray();
    }
    internal IReadOnlyList<PluginAction> AvailableActions => AvailableActionEntries().Select(item => item.Action).ToArray();
    internal bool InvokeAction(string key)
    {
        var item = AvailableActionEntries().FirstOrDefault(item => item.Action.Key == key);
        if (item.Owner == null || !item.Owner.Running) return false;
        try { item.Action.Invoke(); return true; }
        catch (Exception ex) { Fail(item.Owner, ex); return false; }
    }
    internal static string EnabledKey(string id) => $"plugin/{id}/enabled";
    internal static string SettingKey(string id, string key) => $"plugin/{id}/setting/{key}";
    internal async Task<bool> PrefillAsync(string pageId, string text)
    {
        var entry = _entries.FirstOrDefault(entry => entry.Running && entry.Pages.Any(page => page.Id == pageId && page.IsAvailable(_options)));
        if (entry?.Instance is not Isolation.IsolatedPlugin proxy || entry.Context == null || entry.Manifest == null) return false;
        var page = entry.Context.Pages.FirstOrDefault(page => PageId(entry.Manifest.Id, page.Key) == pageId);
        if (page == null) return false;
        try { return await proxy.PrefillAsync(page.Key, text); }
        catch (Exception ex) { if (ReferenceEquals(entry.Instance, proxy)) Fail(entry, ex); return false; }
    }
    internal static string PageId(string id, string key) => $"plugin:{id}/{key}";
    internal string? PageForActivity(string owner) => _entries.Where(e=>e.Running&&owner=="plugin:"+e.Manifest!.Id)
        .SelectMany(e=>e.Pages).OrderBy(p=>p.Order).FirstOrDefault(p=>p.IsAvailable(_options))?.Id;
    internal static bool IsEnabled(IslandOptions options, string id) => options.Extras.TryGetValue(EnabledKey(id), out var value)
        ? value.ValueKind == JsonValueKind.True : id is "ai.api-chat" or "ai.local-agents" or "alpha.device-battery";

    internal void Discover()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _roots)
        {
            if (!Directory.Exists(root)) continue;
            string[] folders;
            try { folders = Directory.GetDirectories(root); }
            catch (Exception ex) { IslandLog.Error("Plugins:无法扫描插件目录", ex); continue; }
            foreach (string folder in folders.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var existing = _entries.FirstOrDefault(e => string.Equals(e.Directory, folder, StringComparison.OrdinalIgnoreCase));
                // 删除失败的目录保留重试入口，不再加载已部分删除的程序集。
                if (existing?.UninstallPending == true) { found.Add(folder); continue; }
                string manifestFile = Path.Combine(folder, "plugin.json");
                if (!File.Exists(manifestFile)) continue;
                found.Add(folder);
                // 运行中的模块不重载 DLL，替换文件后需重启宿主。
                if (existing?.Running == true) continue;
                if (existing != null) { Stop(existing); _entries.Remove(existing); }
                try
                {
                    var manifest = PluginManifest.Read(manifestFile);
                    if (_entries.Any(e => e.Manifest?.Id == manifest.Id)) throw new InvalidDataException($"插件 Id 重复：{manifest.Id}");
                    _entries.Add(new Entry { Directory = folder, Manifest = manifest });
                }
                catch (Exception ex) { _entries.Add(new Entry { Directory = folder, Error = ex.Message }); }
            }
        }
        foreach (var missing in _entries.Where(e => !found.Contains(e.Directory)).ToArray()) { Stop(missing); _entries.Remove(missing); }
        Sync(_options);
        Changed?.Invoke();
    }

    internal void Sync(IslandOptions options)
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _options = options;
        bool changed = false;
        foreach (var entry in _entries.ToArray())
        {
            if (entry.Manifest == null || entry.UninstallPending) continue;
            bool enabled = IsEnabled(options, entry.Manifest.Id);
            if (!enabled && entry.GameSuspended) { entry.GameSuspended = false; changed = true; }
            if (!enabled && entry.Attempted)
            { Stop(entry); entry.Attempted = false; entry.Error = ""; changed = true; }
            else if (enabled && !entry.Attempted && _gameSuspended) { entry.GameSuspended = true; }
            else if (enabled && !entry.Attempted) { Start(entry); changed = true; }
            else if (entry.Running)
            {
                try { entry.Instance!.OnSettingsChanged(); }
                catch (Exception ex) { Fail(entry, ex); }
            }
        }
        if (changed) Changed?.Invoke();
    }

    private void Start(Entry entry)
    {
        entry.Attempted = true;
        entry.Context = new Context(this, entry);
        Isolation.IsolatedPlugin? proxy = null;
        proxy = new Isolation.IsolatedPlugin(_dispatcher,
            () => _options.Extras.Where(pair => pair.Value.ValueKind != JsonValueKind.Undefined).ToDictionary(pair => pair.Key, pair => pair.Value.Clone()),
            ex => { if (!_disposed && ReferenceEquals(entry.Instance, proxy)) Fail(entry, ex); });
        entry.Instance = proxy;
        _ = StartIsolated(entry, proxy);
    }
    private async Task StartIsolated(Entry entry, Isolation.IsolatedPlugin proxy)
    {
        try
        {
            var context = entry.Context ?? throw new InvalidOperationException("插件上下文未创建");
            var token = context.Lifetime;
            await entry.Teardown.ConfigureAwait(false);
            var initialization = await _dispatcher.InvokeAsync(() =>
                _disposed || !ReferenceEquals(entry.Instance, proxy) || token.IsCancellationRequested
                    ? Task.CompletedTask : proxy.InitializeAsync(context, entry.Manifest!, entry.Directory));
            await initialization.ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || !ReferenceEquals(entry.Instance, proxy) || token.IsCancellationRequested) return;
                context.Sealed = true;
                foreach (var page in context.Pages) entry.Pages.Add(new PluginIslandPage(entry.Manifest!.Id, page, ex => QueueFailure(entry, ex)));
                foreach (var page in entry.Pages) _attach(page);
                entry.GameSuspended = false;
                entry.Active = true; proxy.OnSettingsChanged();
                IslandLog.Info("Plugins", $"已在独立进程启用 {entry.Manifest!.Id} pid={proxy.ProcessId}");
                Changed?.Invoke();
            });
        }
        catch (Exception ex)
        {
            if (!_dispatcher.HasShutdownStarted)
                await _dispatcher.InvokeAsync(() => { if (!_disposed && ReferenceEquals(entry.Instance, proxy)) Fail(entry, ex); });
        }
    }
    private void QueueFailure(Entry entry, Exception ex)
    {
        var failedInstance = entry.Instance;
        _dispatcher.BeginInvoke(new Action(() =>
        { if (!_disposed && entry.Running && ReferenceEquals(entry.Instance, failedInstance)) Fail(entry, ex); }));
    }
    private void Fail(Entry entry, Exception ex)
    {
        entry.GameSuspended = false;
        entry.Error = ex.GetBaseException().Message;
        IslandLog.Error($"Plugins:{entry.Manifest?.Id} 已停止", ex);
        Stop(entry); Changed?.Invoke();
    }
    private void Stop(Entry entry)
    {
        if (ToolDispatcher is AlphaIsland.AgentTools.ToolRegistry registry && entry.Manifest != null) registry.RemoveOwner(entry.Manifest.Id);
        entry.Active = false;
        try { entry.Context?.Cancellation.Cancel(); } catch (Exception ex) { IslandLog.Error("Plugins:取消插件任务失败", ex); }
        foreach (var page in entry.Pages.ToArray()) TryCleanup(() => _detach(page));
        entry.Pages.Clear();
        for (int i = entry.Started.Count - 1; i >= 0; i--) TryCleanup(entry.Started[i].Stop);
        entry.Started.Clear();
        if (entry.Instance != null) TryCleanup(entry.Instance.Dispose);
        if (entry.Instance is Isolation.IsolatedPlugin stopped) entry.Teardown = stopped.ShutdownCompletion;
        entry.Instance = null;
        if (entry.Manifest != null) AgentStatus.Reset(owner:"plugin:" + entry.Manifest.Id);
        entry.Context?.Cancellation.Dispose(); entry.Context = null;
        entry.Loader?.Unload(); entry.Loader = null;
    }
    private static void TryCleanup(Action cleanup)
    { try { cleanup(); } catch (Exception ex) { IslandLog.Error("Plugins:插件清理失败", ex); } }

    internal JsonElement ReadSetting(Entry entry, PluginSetting setting)
    {
        string key = SettingKey(entry.Manifest!.Id, setting.Key);
        return NormalizeValue(setting, _options.Extras.TryGetValue(key, out var value) ? value : JsonSerializer.SerializeToElement(setting.DefaultValue));
    }
    internal (string Id, string Title, PluginNetworkSample Sample)? ReadNetwork(DateTimeOffset now, string? ownerId = null)
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return null;
        foreach (var item in _entries.Where(e => e.Running && e.Context != null && (ownerId == null || e.Manifest?.Id == ownerId))
            .SelectMany(e => e.Context!.NetworkSources.Select(s => (Entry: e, Source: s))).OrderByDescending(p => p.Source.Priority).ToArray())
        {
            try
            {
                if (!item.Entry.Running) continue;
                var sample = item.Source.Read();
                if (!item.Entry.Running || !PluginNetworkRules.IsUsable(sample, now)) continue;
                return ($"{item.Entry.Manifest!.Id}/{item.Source.Key}", item.Source.Title, sample!);
            }
            catch (Exception ex) { Fail(item.Entry, ex); }
        }
        return null;
    }
    internal static JsonElement NormalizeValue(PluginSetting setting, JsonElement value, bool strict = false)
    {
        object? normalized = null;
        switch (setting.Kind)
        {
            case PluginSettingKind.Boolean:
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) normalized = value.GetBoolean();
                break;
            case PluginSettingKind.Number:
                if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number))
                    normalized = Math.Clamp(number, setting.Minimum, setting.Maximum);
                break;
            case PluginSettingKind.Text:
                if (value.ValueKind == JsonValueKind.String) normalized = value.GetString();
                break;
            case PluginSettingKind.Choice:
                if (value.ValueKind == JsonValueKind.String && setting.Choices?.Contains(value.GetString()!) == true) normalized = value.GetString();
                break;
        }
        if (normalized != null) return JsonSerializer.SerializeToElement(normalized);
        if (strict) throw new ArgumentException($"设置 {setting.Key} 的默认值与类型不匹配");
        return NormalizeValue(setting, JsonSerializer.SerializeToElement(setting.DefaultValue), strict: true);
    }
    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _gameBudgetPoll?.Dispose(); _gameBudgetPoll = null;
        foreach (var entry in _entries) Stop(entry);
        Changed = null;
    }
}
