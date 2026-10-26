using System.IO;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AlphaIsland.PluginSdk;
using alpha_island.Core;
using C = System.Windows.Controls;

namespace alpha_island.Plugins.Isolation;

internal sealed class PluginWorkerServer : IDisposable
{
    private readonly System.Windows.Application _application;
    private readonly NamedPipeClientStream _pipe;
    private readonly string _nonce;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Dictionary<string, PluginWorkerSurface> _surfaces = new(StringComparer.Ordinal);
    private readonly List<PluginService> _started = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _toolCalls = new();
    private readonly Window _owner;
    private IIslandPlugin? _plugin;
    private PluginWorkerContext? _context;
    private IslandTicker.Subscription? _pulse;
    private long _lastPulse;
    private int _disposed;
    private int _eventWrites;
    private string? _active;
    private bool _gameSuspensionPrepared;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _textCalls = new();
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    internal PluginWorkerServer(System.Windows.Application application, string pipe, string nonce)
    {
        _application = application; _nonce = nonce;
        _pipe = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        // Opacity alone leaves a non-layered HWND opaque when a dialog anchor moves on screen.
        _owner = new Window { Width = 414, Height = 294, AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent, Opacity = 0, ShowInTaskbar = false,
            ShowActivated = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Left = -30000, Top = -30000, Title = "Alpha Island plugin worker" };
        _owner.SourceInitialized += (_, _) =>
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(_owner).Handle;
            int style = Interop.NativeMethods.GetWindowLong(handle, -20);
            Interop.NativeMethods.SetWindowLong(handle, -20, style | 0x08000000 | 0x20 | 0x80);
        };
    }
    internal async Task RunAsync()
    {
        SetErrorMode(0x8003); IslandLog.FileSinkEnabled = false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await _pipe.ConnectAsync(deadline.Token);
            await PluginWire.Write(_pipe, new(0, "hello", PluginWire.Element(_nonce)), _write, _lifetime.Token);
            _pulse = IslandTicker.Shared(_application.Dispatcher).Subscribe("worker-pulse", TimeSpan.FromSeconds(1),
                () => Interlocked.Exchange(ref _lastPulse, Environment.TickCount64), runImmediately: true);
            _owner.Show();
            while (!_lifetime.IsCancellationRequested)
            {
                var packet = await PluginWire.Receive(_pipe, _lifetime.Token).ConfigureAwait(false);
                if (packet.Kind == "shutdown")
                { await _application.Dispatcher.InvokeAsync(() => { Dispose(); _application.Shutdown(); }); break; }
                _ = Handle(packet); // A native dialog or hung UI must not block heartbeat reads.
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidDataException)
        { }
        finally
        {
            if (!_application.Dispatcher.HasShutdownStarted)
                await _application.Dispatcher.InvokeAsync(() => { Dispose(); _application.Shutdown(); });
        }
    }
    private async Task Handle(PluginPacket packet)
    {
        try
        {
            object result;
            if (packet.Kind == "tool-run")
                result = await (await _application.Dispatcher.InvokeAsync(() => RunToolAsync(PluginWire.Read<ToolRequest>(packet.Data))));
            else if (packet.Kind == "text-submit")
                result = await (await _application.Dispatcher.InvokeAsync(() => SubmitTextAsync(PluginWire.Read<RemoteTextInput>(packet.Data))));
            else result = packet.Kind == "heartbeat" ? Interlocked.Read(ref _lastPulse)
                : await _application.Dispatcher.InvokeAsync(() => Dispatch(packet));
            await PluginWire.Write(_pipe, new(packet.Id, "result", PluginWire.Element(result)), _write, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                string error = ex.GetBaseException().Message;
                await PluginWire.Write(_pipe, new(packet.Id, "result", PluginWire.Element(false), error[..Math.Min(error.Length, 1024)]), _write, _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception writeError) when (writeError is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
    private object Dispatch(PluginPacket packet)
    {
        if (_gameSuspensionPrepared && packet.Kind is "input" or "prefill" or "action") return false;
        return packet.Kind switch
    {
        "prepare-game-suspension" => PrepareGameSuspension(),
        "resume-game-preparation" => ResumeGamePreparation(),
        "initialize" => Initialize(PluginWire.Read<PluginStart>(packet.Data)),
        "status" => Status(),
        "frame" => Frame(PluginWire.Read<RemoteViewRequest>(packet.Data)),
        "input" => Input(PluginWire.Read<RemoteCommand>(packet.Data)),
        "prefill" => Prefill(PluginWire.Read<RemoteCommand>(packet.Data)),
        "text-cancel" => CancelText(PluginWire.Read<RemoteTextInput>(packet.Data)),
        "action" => Action(PluginWire.Read<string>(packet.Data)),
        "configuration" => Configure(PluginWire.Read<Dictionary<string, JsonElement>>(packet.Data)),
        "host-result" => Context.CompleteHostCall(PluginWire.Read<RemoteHostReply>(packet.Data)),
        "tool-cancel" => CancelTool(PluginWire.Read<string>(packet.Data)),
        _ => throw new InvalidDataException("未知插件操作")
    };
    }
    private bool PrepareGameSuspension()
    {
        if (_active == "$settings" || _plugin is not IPluginSuspendGuard { CanSuspend: true } || !_toolCalls.IsEmpty || !_textCalls.IsEmpty
            || _application.Windows.Cast<Window>().Any(window => !ReferenceEquals(window, _owner) && window.IsVisible)) return false;
        _gameSuspensionPrepared = true;
        Deactivate(); _owner.Content = null;
        return true;
    }
    private bool ResumeGamePreparation() { _gameSuspensionPrepared = false; return true; }
    private RemoteRegistry Initialize(PluginStart start)
    {
        if (_plugin != null) throw new InvalidOperationException("插件已经启动");
        string directory = Path.GetFullPath(start.Directory);
        string assembly = Path.GetFullPath(Path.Combine(directory, start.Assembly));
        if (!assembly.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !PluginManifest.ValidKey(start.PluginId)) throw new InvalidDataException("插件路径或标识无效");
        _context = new(start, _application.Dispatcher, Publish, _lifetime.Token); _context.Update(start.Configuration);
        var loader = new PluginLoadContext(assembly);
        var type = loader.LoadPluginAssembly(assembly, wpfResources: true).GetType(start.EntryType, true)
            ?? throw new InvalidDataException("找不到插件入口");
        _plugin = Activator.CreateInstance(type) as IIslandPlugin ?? throw new InvalidDataException("插件未实现 IIslandPlugin");
        _plugin.Initialize(_context); _context.Sealed = true;
        foreach (var service in _context.Services) { _started.Add(service); service.Start(_lifetime.Token); }
        return new(_context.Pages.Select(page => new RemotePage(page.Key, page.Title, page.Glyph, page.Height, page.Order)).ToArray(),
            _context.Settings.ToArray(), Actions(), _context.SettingsView != null,
            _context.Networks.Select(source => new RemoteNetwork(source.Key, source.Title, source.Priority)).ToArray(),
            _context.ToolHandlers.Select(t => t.Definition).ToArray(), _plugin is IPluginSuspendGuard);
    }
    private bool CancelTool(string id) { if (_toolCalls.TryGetValue(id, out var cancel)) cancel.Cancel(); return true; }
    private async Task<ToolResult> RunToolAsync(ToolRequest request)
    {
        if (_gameSuspensionPrepared) return ToolResult.Fail("插件正在进入游戏休眠。");
        var tool = Context.ToolHandlers.FirstOrDefault(t => t.Definition.Name == request.Name);
        if (tool == null) return ToolResult.Fail("插件工具不存在");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        cancel.CancelAfter(TimeSpan.FromSeconds(12));
        if (!_toolCalls.TryAdd(request.CallId, cancel)) return ToolResult.Fail("调用标识正在使用");
        try { return await tool.ExecuteAsync(request, cancel.Token); }
        catch (OperationCanceledException) { return ToolResult.Fail("插件工具已取消"); }
        catch (Exception) { return ToolResult.Fail("插件工具执行失败"); }
        finally { _toolCalls.TryRemove(request.CallId, out _); }
    }
    private RemoteAction[] Actions() => Context.Actions.Select(action => new RemoteAction(action.Key, action.Title, action.Available?.Invoke() ?? true)).ToArray();
    private PluginWorkerContext Context => _context ?? throw new InvalidOperationException("插件尚未初始化");
    private RemoteStatus Status()
    {
        PluginNetworkSample? network = null; PluginNetworkSource? selected = null;
        foreach (var source in Context.Networks.OrderByDescending(source => source.Priority))
            if (source.Read() is { } sample && PluginNetworkRules.IsUsable(sample, DateTimeOffset.UtcNow)) { network = sample; selected = source; break; }
        return new(Context.Pages.Select(page => new RemotePageState(page.Key, page.Available?.Invoke() ?? true, page.Summary?.Invoke() ?? page.Title)).ToArray(),
            Actions(), network, selected?.Title, selected?.Key, selected?.Priority ?? 0, Interlocked.Read(ref _lastPulse));
    }
    private RemoteFrame Frame(RemoteViewRequest request)
    {
        if (!request.Active)
        {
            if (_active == request.Key) { Deactivate(); _owner.Content = null; }
            return new(request.Width, request.Height, "", [], 0);
        }
        var surface = Surface(request.Key);
        if (_active != request.Key)
        {
            Deactivate(); _owner.Content = null; _owner.Content = surface.View; _active = request.Key;
            Context.Pages.FirstOrDefault(page => page.Key == request.Key)?.Activated?.Invoke();
        }
        double width = Math.Clamp(request.Width, 160, 1024), height = Math.Clamp(request.Height, 80, 1024);
        if (request.Key == "$settings")
        {
            // Settings are document content, not fixed-height island pages. Keep the
            // existing scroll limit for long forms without reserving 650px for short ones.
            var document = surface.View is C.ScrollViewer { Content: FrameworkElement content } ? content : surface.View;
            document.Measure(new System.Windows.Size(width, double.PositiveInfinity));
            height = Math.Clamp(Math.Ceiling(document.DesiredSize.Height) + 2, 80, 650);
        }
        _owner.Width = width; _owner.Height = height;
        return surface.Capture(width, height);
    }
    private void Deactivate()
    {
        if (_active != null) Context.Pages.FirstOrDefault(page => page.Key == _active)?.Deactivated?.Invoke();
        _active = null;
    }
    private bool Input(RemoteCommand command)
    {
        if (!_surfaces.TryGetValue(command.View, out var surface)) return false;
        if (command.X > -10000 && command.Y > -10000 && command.Action == "position")
        { _owner.Left = command.X; _owner.Top = command.Y; return true; }
        return surface.Input(command);
    }
    private PluginWorkerSurface Surface(string key)
    {
        if (_surfaces.TryGetValue(key, out var existing)) return existing;
        FrameworkElement view = key == "$settings"
            ? new C.ScrollViewer { Content = Context.SettingsView?.Invoke() ?? throw new InvalidOperationException("插件没有设置界面"), VerticalScrollBarVisibility = C.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = C.ScrollBarVisibility.Disabled }
            : Context.Pages.FirstOrDefault(page => page.Key == key)?.CreateView() ?? throw new InvalidDataException("插件页面不存在");
        if (view.Parent != null) throw new InvalidDataException("插件页面已经属于另一个容器");
        var surface = new PluginWorkerSurface(view);
        _surfaces.Add(key, surface);
        return surface;
    }
    private bool Prefill(RemoteCommand command) => Surface(command.View).Prefill(command.Text ?? "");
    private async Task<PluginTextInputResult> SubmitTextAsync(RemoteTextInput request)
    {
        if (_gameSuspensionPrepared) return new(false, "游戏期间暂停接收新的语音指令。");
        if (!Guid.TryParseExact(request.RequestId, "N", out _) || request.Text.Length is 0 or > 12000
            || Context.Pages.FirstOrDefault(page => page.Key == request.View)?.Available?.Invoke() == false)
            return new(false, "输入不可用。");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        if (!_textCalls.TryAdd(request.RequestId, cancellation)) return new(false, "重复请求已忽略。");
        try
        {
            return Surface(request.View).View is IPluginTextInput input
                ? await input.SubmitTextAsync(request.RequestId, request.Text, cancellation.Token)
                : new(false, "此插件不支持直接接收语音指令。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { return new(true, "已停止本次指令。"); }
        finally { _textCalls.TryRemove(request.RequestId, out _); }
    }
    private bool CancelText(RemoteTextInput request)
    {
        if (!_textCalls.TryGetValue(request.RequestId, out var cancellation)) return false;
        cancellation.Cancel(); return true;
    }
    private bool Action(string key)
    {
        var action = Context.Actions.FirstOrDefault(action => action.Key == key);
        if (action == null || action.Available?.Invoke() == false) return false;
        // Existing plugins may implement actions against fields populated by
        // CreateView. Preserve that contract, but allocate only after user input.
        foreach (var page in Context.Pages) Surface(page.Key);
        action.Invoke(); return true;
    }
    private bool Configure(Dictionary<string, JsonElement> configuration)
    { Context.Update(configuration); _plugin?.OnSettingsChanged(); return true; }
    private void Publish(RemoteEvent value)
    {
        if (_lifetime.IsCancellationRequested) return;
        if (Interlocked.Increment(ref _eventWrites) > 256)
        { _application.Dispatcher.BeginInvoke(() => _application.Shutdown(95)); return; }
        _ = SendEvent(value);
    }
    private async Task SendEvent(RemoteEvent value)
    {
        try { await PluginWire.Write(_pipe, new(0, "event", PluginWire.Element(value)), _write, _lifetime.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { Interlocked.Decrement(ref _eventWrites); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel(); _pulse?.Dispose();
        foreach (var service in _started.AsEnumerable().Reverse()) { try { service.Stop(); } catch { } }
        try { _plugin?.Dispose(); } catch { }
        _surfaces.Clear(); _owner.Content = null; _owner.Close(); _pipe.Dispose();
    }
}
