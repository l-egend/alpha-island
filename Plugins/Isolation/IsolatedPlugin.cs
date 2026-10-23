using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AlphaIsland.PluginSdk;
using alpha_island.Core;

namespace alpha_island.Plugins.Isolation;

internal sealed class IsolatedPlugin : IIslandPlugin
{
    private readonly Dispatcher _dispatcher;
    private readonly PluginProcessSession _session;
    private readonly Func<Dictionary<string, JsonElement>> _configuration;
    private readonly Action<Exception> _failed;
    private readonly List<WeakReference<RemotePluginView>> _views = new();
    private readonly Dictionary<string, RemotePageState> _pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _actions = new(StringComparer.Ordinal);
    private IslandTicker.Subscription? _poll;
    private IPluginContext? _context;
    private RemoteStatus? _status;
    private string? _active;
    private bool _disposed, _polling, _presentationDirty;
    private readonly Dictionary<string, CancellationTokenSource> _hostCalls = new();
    private long _configurationVersion, _sentConfigurationVersion;
    internal long ConfigurationVersion => _configurationVersion;
    internal long AppliedConfigurationVersion { get; private set; }
    internal Task ShutdownCompletion => _session.Completion;
    internal int ProcessId => _session.ProcessId;
    internal bool IsAlive => !_disposed && _session.IsAlive;
    internal event Action? Ready;
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    internal IsolatedPlugin(Dispatcher dispatcher, Func<Dictionary<string, JsonElement>> configuration, Action<Exception> failed)
    {
        _dispatcher = dispatcher; _configuration = configuration; _failed = failed;
        _session = new(dispatcher); _session.Failed += failed; _session.Event += OnEvent;
    }
    public void Initialize(IPluginContext context) => throw new InvalidOperationException("隔离插件通过异步加载接口初始化");
    internal async Task InitializeAsync(IPluginContext context, PluginManifest manifest, string directory)
    {
        _context = context;
        var registry = await _session.StartAsync(new(directory, manifest.Assembly, manifest.EntryType, manifest.Id,
            context.DataDirectory, _configuration()), context.Lifetime).ConfigureAwait(false);
        var initial = await _session.Request<RemoteStatus>("status", new { }).ConfigureAwait(false);
        await _dispatcher.InvokeAsync(() =>
        {
            if (_disposed || context.Lifetime.IsCancellationRequested) return;
            SetStatus(initial);
            foreach (var page in registry.Pages)
                context.RegisterPage(new(page.Key, page.Title, () => View(page.Key), page.Glyph, page.Height, page.Order)
                { Available = () => _pages.TryGetValue(page.Key, out var state) && state.Available,
                    Summary = () => _pages.TryGetValue(page.Key, out var state) ? state.Summary : page.Title });
            foreach (var setting in registry.Settings) context.RegisterSetting(setting);
            foreach (var action in registry.Actions)
                context.RegisterAction(new(action.Key, action.Title, () => Observe(ActionAsync(action.Key)),
                    () => _actions.TryGetValue(action.Key, out bool available) && available));
            foreach (var tool in registry.Tools ?? [])
            {
                string prefix = context.PluginId.Replace('.', '_').Replace('-', '_') + "__";
                context.RegisterTool(new RemoteTool(_session, tool, prefix));
            }
            if (registry.HasSettings) context.RegisterSettingsView(() => View("$settings"));
            // A stable host callback returns cached data; no plugin call blocks the UI thread.
            foreach (var network in registry.Networks)
                context.RegisterNetworkSource(new(network.Key, network.Title,
                    () => _status?.NetworkKey == network.Key ? _status.Network : null, network.Priority));
            _poll = IslandTicker.Shared(_dispatcher).Subscribe("plugin/" + manifest.Id + "/" + Guid.NewGuid().ToString("N"),
                TimeSpan.FromSeconds(1), () => Observe(PollAsync()));
            Ready?.Invoke();
        });
    }
    private RemotePluginView View(string key)
    {
        var view = new RemotePluginView(key, InputAsync, ReportFailure);
        _views.Add(new(view));
        view.PresentationChanged += () =>
        {
            if (_disposed) return;
            _poll?.SetInterval(TimeSpan.FromMilliseconds(_views.Any(reference => reference.TryGetTarget(out var target) && target.IsPresented) ? 500 : 2000));
            _presentationDirty = true; Observe(PollAsync());
        };
        return view;
    }
    public void OnSettingsChanged() { if (!_disposed) { _configurationVersion++; Observe(PollAsync()); } }
    private Task PollAsync() => _dispatcher.InvokeAsync(PollCoreAsync).Task.Unwrap();
    private async Task PollCoreAsync()
    {
        if (_disposed || _polling) return;
        _polling = true; _presentationDirty = false;
        try
        {
            long pulse = await _session.Request<long>("heartbeat", new { }, TimeSpan.FromSeconds(3));
            if (Environment.TickCount64 - pulse > 10_000) throw new TimeoutException("插件界面线程长时间无响应");
            if (_sentConfigurationVersion < _configurationVersion)
            {
                long version = _configurationVersion;
                await _session.Request<bool>("configuration", _configuration()); _sentConfigurationVersion = version;
            }
            var status = await _session.Request<RemoteStatus>("status", new { }, TimeSpan.FromSeconds(5));
            if (_disposed) return;
            bool changed = SetStatus(status); AppliedConfigurationVersion = _sentConfigurationVersion;
            if (changed) _context?.Refresh();
            _views.RemoveAll(reference => !reference.TryGetTarget(out _));
            var visible = _views.Select(reference => reference.TryGetTarget(out var view) ? view : null).LastOrDefault(view => view?.IsPresented == true);
            if (visible != null)
            {
                double width = Math.Clamp(visible.ActualWidth > 0 ? visible.ActualWidth : 414, 160, 1024);
                double height = Math.Clamp(visible.ActualHeight > 0 ? visible.ActualHeight : 294, 80, 1024);
                var frame = await _session.Request<RemoteFrame>("frame", new RemoteViewRequest(visible.Key, width, height));
                if (!_disposed && visible.IsPresented) visible.Apply(frame);
                _active = visible.Key;
            }
            else if (_active != null)
            {
                await _session.Request<RemoteFrame>("frame", new RemoteViewRequest(_active, 414, 294, false)); _active = null;
            }
        }
        finally { _polling = false; if (!_disposed && _presentationDirty) Observe(PollAsync()); }
    }
    private bool SetStatus(RemoteStatus status)
    {
        if (status.Pages.Length > 128 || status.Actions.Length > 128) throw new InvalidDataException("插件组件数量超过限制");
        bool changed = _status == null || !status.Pages.SequenceEqual(_status.Pages) || !status.Actions.SequenceEqual(_status.Actions);
        _status = status; _pages.Clear(); _actions.Clear();
        foreach (var page in status.Pages) _pages[page.Key] = page;
        foreach (var action in status.Actions) _actions[action.Key] = action.Available;
        return changed;
    }
    internal Task<bool> PrefillAsync(string key, string text) => _session.Request<bool>("prefill", new RemoteCommand(key, "prefill", Text: text));
    internal Task<PluginTextInputResult> SubmitTextAsync(string key, string requestId, string text)
        => _session.Request<PluginTextInputResult>("text-submit", new RemoteTextInput(key, requestId, text), TimeSpan.FromMinutes(5));
    internal Task<bool> CancelTextAsync(string key, string requestId)
        => _session.Request<bool>("text-cancel", new RemoteTextInput(key, requestId));
    private async Task ActionAsync(string key)
    {
        if (_disposed) return;
        AllowSetForegroundWindow((uint)_session.ProcessId);
        await _session.Request<bool>("action", key, TimeSpan.FromMinutes(5));
        await PollAsync();
    }
    private async Task InputAsync(RemoteCommand command)
    {
        if (_disposed) return;
        if (command.Action == "click") AllowSetForegroundWindow((uint)_session.ProcessId);
        await _session.Request<bool>("input", command, TimeSpan.FromMinutes(5));
        if (command.Action is not ("text" or "value" or "focus" or "blur")) await PollAsync();
    }
    private async void Observe(Task operation)
    {
        try { await operation; }
        catch (Exception ex) { ReportFailure(ex); }
    }
    private void ReportFailure(Exception error)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => ReportFailure(error)); return; }
        try { if (!_disposed) _failed(error); }
        catch (Exception ex) { IslandLog.Error("Plugins:故障收尾", ex); }
    }
    private void OnEvent(RemoteEvent item)
    {
        if (_disposed || _context == null || _context.Lifetime.IsCancellationRequested) return;
        switch (item.Name)
        {
            case "host-call" when item.Value is JsonElement call: Observe(HandleHostCall(PluginWire.Read<RemoteHostCall>(call))); break;
            case "host-cancel": if (_hostCalls.TryGetValue(item.Key, out var cancel)) cancel.Cancel(); break;
            case "configuration" when item.Value is JsonElement value: _context.WriteConfiguration(item.Key, value); break;
            case "open": _context.OpenPage(item.Key); break;
            case "settings": _context.OpenSettings(); break;
            case "refresh": _context.Refresh(); break;
            case "activity": _context.ReportActivity(item.Key, item.Text, item.Value is { ValueKind: JsonValueKind.String } model ? model.GetString() ?? "" : ""); break;
            case "battery" when item.Battery != null: _context.ReportBluetoothBattery(item.Battery); break;
            case "device-batteries" when item.DeviceBatteries != null: _context.ReportDeviceBatteries(item.DeviceBatteries); break;
            case "notice": _context.NotifyPage(item.Key, item.Text, item.Detail, item.Seconds); break;
            case "log": _context.Log(item.Text); break;
        }
    }
    private async Task HandleHostCall(RemoteHostCall call)
    {
        if (_context == null || call.Id.Length != 32 || _hostCalls.Count >= 16 || _hostCalls.ContainsKey(call.Id)) return;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_context.Lifetime);
        cancel.CancelAfter(TimeSpan.FromSeconds(14)); _hostCalls.Add(call.Id, cancel);
        try
        {
            object result = call.Kind switch
            {
                "tool-list" => await _context.Tools.GetToolsAsync(cancel.Token),
                "tool-execute" => await _context.Tools.ExecuteAsync(PluginWire.Read<ToolRequest>(call.Data), cancel.Token),
                "desktop-context" => await _context.GetDesktopContextAsync(PluginWire.Read<string>(call.Data), cancel.Token),
                _ => throw new InvalidDataException("未知宿主调用")
            };
            await _session.Request<bool>("host-result", new RemoteHostReply(call.Id, PluginWire.Element(result)));
        }
        catch (Exception) when (!_disposed && !_context.Lifetime.IsCancellationRequested)
        { await _session.Request<bool>("host-result", new RemoteHostReply(call.Id, PluginWire.Element(false), "宿主调用已取消或不可用")); }
        finally { _hostCalls.Remove(call.Id); }
    }
    private sealed class RemoteTool(PluginProcessSession session, ToolDefinition definition, string prefix) : IToolHandler
    {
        // Third-party contributions require the host's opt-in independently of their claimed risk.
        public ToolDefinition Definition => definition with { Name = prefix + definition.Name };
        public async ValueTask<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken)
        {
            try { return await session.Request<ToolResult>("tool-run", request with { Name = definition.Name }, TimeSpan.FromSeconds(13)).WaitAsync(cancellationToken); }
            finally { if (cancellationToken.IsCancellationRequested) { try { await session.Request<bool>("tool-cancel", request.CallId); } catch { } } }
        }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _poll?.Dispose(); _session.Dispose(); foreach (var reference in _views) if (reference.TryGetTarget(out var view)) view.Close();
        _views.Clear(); Ready = null;
    }
}
