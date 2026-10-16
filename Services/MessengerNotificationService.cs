using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Interop;

namespace alpha_island.Services;

/// <summary>QQ/WeChat notification adapters, independent of Windows toast permission.</summary>
internal sealed class MessengerNotificationService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IslandTimer _timer;
    private readonly MessengerWindowReader _reader;
    private readonly MessengerUnreadTracker _unread = new();
    private readonly Dictionary<MessengerClient, MessengerTrayBlinkTracker> _blink = new();
    private readonly Dictionary<MessengerClient, (long Started, DateTimeOffset BaselineUntil)> _clients = new();
    private readonly Dictionary<MessengerClient, byte[]?> _logos = new();
    private readonly Dictionary<IntPtr, string> _popups = new();
    private readonly Dictionary<MessengerClient, DateTimeOffset> _lastFlash = new();
    private readonly ConcurrentQueue<IntPtr> _flashes = new();
    private readonly ConcurrentDictionary<IntPtr, int> _popupShows = new();
    private readonly HookThread _hookThread = new("Messenger notification events");
    private readonly NativeMethods.WinEventProc _windowEvent;
    private HwndSource? _shellWindow;
    private IntPtr _hook;
    private uint _shellMessage;
    private IReadOnlySet<MessengerClient> _enabled = new HashSet<MessengerClient>();
    private IReadOnlyDictionary<uint, MessengerClient> _processIds = new Dictionary<uint, MessengerClient>();
    private IReadOnlyList<MessengerTrayIcon> _tray = Array.Empty<MessengerTrayIcon>();
    private DateTimeOffset _nextRead, _lastRead;
    private int _pulse;
    private bool _busy, _disposed, _started;
    internal string Status { get; private set; } = "QQ / 微信消息监听待启动";
    internal event Action<IReadOnlyList<DesktopNotification>, MessengerNoticeSource>? Received;
    internal event Action? StatusChanged;
    internal MessengerNotificationService(Dispatcher dispatcher, MessengerWindowReader? reader = null)
    {
        _dispatcher = dispatcher;
        _reader = reader ?? new();
        _timer = new IslandTimer(dispatcher, "messenger") { Interval = TimeSpan.FromMilliseconds(350) };
        _timer.Tick += OnTick;
        _windowEvent = (_, kind, window, objectId, childId, _, _) =>
        {
            if (window == IntPtr.Zero || childId != 0 || objectId is not (0 or -4)
                || kind is not (0x8002 or 0x8003 or 0x800C)) return;
            NativeMethods.GetWindowThreadProcessId(window, out uint process);
            if (!_processIds.ContainsKey(process)) return;
            if (kind == 0x8002 && objectId == 0)
            {
                if (_popupShows.Count > 128) _popupShows.Clear();
                _popupShows.AddOrUpdate(window, 1, (_, value) => value + 1);
            }
            Interlocked.Exchange(ref _pulse, 1);
        };
    }
    internal void SetEnabled(bool qq, bool wechat)
    {
        var enabled = new HashSet<MessengerClient>(new[] { (MessengerClient.QQ, qq), (MessengerClient.WeChat, wechat) }.Where(x => x.Item2).Select(x => x.Item1));
        if (_enabled.SetEquals(enabled)) return;
        _enabled = enabled;
        Interlocked.Exchange(ref _pulse, 1);
        _nextRead = default;
        if (_started) _ = SampleAsync();
    }
    internal void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        _shellMessage = RegisterWindowMessage("SHELLHOOK");
        _shellWindow = new HwndSource(new HwndSourceParameters("Alpha Island messenger events") { WindowStyle = unchecked((int)0x80000000), Width = 0, Height = 0 });
        _shellWindow.AddHook(OnShellMessage);
        if (!RegisterShellHookWindow(_shellWindow.Handle)) IslandLog.Warn("Messenger", "任务栏提醒监听不可用，保留弹窗和未读数检测。");
        _hookThread.Start(() => _hook = NativeMethods.SetWinEventHook(0x8002, 0x800C, IntPtr.Zero, _windowEvent, 0, 0, 2),
            () => { if (_hook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_hook); _hook = IntPtr.Zero; });
        _timer.Start(); _ = SampleAsync();
    }
    private IntPtr OnShellMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == _shellMessage && wParam.ToInt64() == 0x8006 && _flashes.Count < 20)
        { _flashes.Enqueue(lParam); _ = SampleAsync(); }
        return IntPtr.Zero;
    }
    private void OnTick(object? sender, EventArgs args) => _ = SampleAsync();
    private async Task SampleAsync()
    {
        if (_disposed || !_started || _busy) return;
        _busy = true;
        try
        {
            var enabled = _enabled;
            var now = DateTimeOffset.UtcNow;
            bool read = now >= _nextRead || Interlocked.Exchange(ref _pulse, 0) != 0 && now - _lastRead >= TimeSpan.FromMilliseconds(650);
            var deliveries = await Task.Run(() =>
            {
                var result = new List<(DesktopNotification Item, MessengerNoticeSource Source)>();
                if (read)
                {
                    var snapshot = _reader.Read(enabled);
                    _nextRead = now.AddSeconds(snapshot.Processes.Count == 0 ? 4 : 2); _lastRead = now;
                    _processIds = snapshot.Processes.ToDictionary(p => (uint)p.Id, p => p.Client);
                    _tray = snapshot.TrayIcons;
                    foreach (var client in Enum.GetValues<MessengerClient>())
                    {
                        var processes = snapshot.Processes.Where(p => p.Client == client).ToArray();
                        if (processes.Length == 0)
                        { _clients.Remove(client); _unread.Forget(client); _blink.Remove(client); _lastFlash.Remove(client); _logos.Remove(client); continue; }
                        long started = processes.Min(p => p.Started);
                        if (!_clients.TryGetValue(client, out var previous) || previous.Started != started)
                        {
                            _clients[client] = (started, now.AddSeconds(8));
                            _unread.Forget(client); _blink[client] = new();
                            _logos[client] = MessengerWindowReader.ReadLogo(processes[0].Path);
                        }
                        bool baseline = now < _clients[client].BaselineUntil;
                        if (snapshot.Unread.TryGetValue(client, out int count) && _unread.Observe(client, count, baseline))
                            result.Add((Notice(client, "unread", "收到新消息", $"当前有 {count} 条未读消息", now), MessengerNoticeSource.Unread));
                    }
                    var livePopups = new HashSet<IntPtr>();
                    foreach (var popup in snapshot.Popups)
                    {
                        livePopups.Add(popup.Window);
                        string fingerprint = popup.Title + "\n" + popup.Body + "/" + _popupShows.GetValueOrDefault(popup.Window);
                        bool changed = !_popups.TryGetValue(popup.Window, out var previous) || previous != fingerprint;
                        _popups[popup.Window] = fingerprint;
                        if (changed && now >= _clients[popup.Client].BaselineUntil)
                            result.Add((Notice(popup.Client, "popup", popup.Title, popup.Body, now), popup.HasContent ? MessengerNoticeSource.Popup : MessengerNoticeSource.Attention));
                    }
                    foreach (var expired in _popups.Keys.Where(key => !livePopups.Contains(key)).ToArray()) _popups.Remove(expired);
                }
                while (_flashes.TryDequeue(out var window))
                {
                    NativeMethods.GetWindowThreadProcessId(window, out uint pid);
                    if (!_processIds.TryGetValue(pid, out var client) || !enabled.Contains(client) || !_clients.TryGetValue(client, out var state)) continue;
                    bool continuous = _lastFlash.TryGetValue(client, out var last) && now - last < TimeSpan.FromSeconds(3);
                    _lastFlash[client] = now;
                    if (!continuous && now >= state.BaselineUntil)
                        result.Add((Notice(client, "attention", "收到新消息", "点击打开 " + MessengerIdentity.Name(client) + " 查看", now), MessengerNoticeSource.Attention));
                }
                foreach (var icon in _tray)
                {
                    if (!enabled.Contains(icon.Client) || !_clients.TryGetValue(icon.Client, out var state)) continue;
                    var pixels = MessengerWindowReader.ReadTrayPixels(icon);
                    if (pixels == null) { _blink[icon.Client].Suspend(); continue; }
                    bool blink = _blink[icon.Client].Observe(icon.Position, pixels, now);
                    if (blink && now >= state.BaselineUntil)
                        result.Add((Notice(icon.Client, "attention", "收到新消息", "点击打开 " + MessengerIdentity.Name(icon.Client) + " 查看", now), MessengerNoticeSource.Attention));
                }
                return result;
            });
            if (_disposed) return;
            _timer.Interval = TimeSpan.FromMilliseconds(_tray.Count > 0 ? 350 : _clients.Count > 0 ? 1000 : 4000);
            SetStatus(string.Join(" · ", enabled.Select(client => MessengerIdentity.Name(client) + (_clients.ContainsKey(client) ? "：监听中" : "：未运行"))));
            foreach (var group in deliveries.GroupBy(x => x.Source))
            {
                // Give the Windows listener first chance to provide the full message.
                if (group.Key is MessengerNoticeSource.Unread or MessengerNoticeSource.Attention) await Task.Delay(700);
                if (_disposed) return;
                var items = group.Select(x => x.Item).Where(item => MessengerIdentity.FromNotification(item) is { } client && _enabled.Contains(client)).ToArray();
                if (items.Length > 0) Received?.Invoke(items, group.Key);
            }
        }
        catch (Exception ex) { if (!_disposed) { SetStatus("QQ / 微信提醒暂时不可用，将自动重试"); IslandLog.Debug("Messenger", "通知表面读取失败：" + ex.GetType().Name); } }
        finally { _busy = false; }
    }
    private DesktopNotification Notice(MessengerClient client, string source, string title, string body, DateTimeOffset now) =>
        new("messenger-" + source + "-" + Guid.NewGuid().ToString("N"), MessengerIdentity.AppId(client), MessengerIdentity.Name(client), title, body, now, Logo: _logos.GetValueOrDefault(client));
    private void SetStatus(string value)
    {
        if (Status == value) return;
        Status = value; StatusChanged?.Invoke(); IslandLog.Info("Messenger", value);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer.Dispose(); _timer.Tick -= OnTick;
        _hookThread.Dispose();
        if (_shellWindow != null)
        { DeregisterShellHookWindow(_shellWindow.Handle); _shellWindow.RemoveHook(OnShellMessage); _shellWindow.Dispose(); _shellWindow = null; }
        Received = null; StatusChanged = null;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string text);
    [DllImport("user32.dll")] private static extern bool RegisterShellHookWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool DeregisterShellHookWindow(IntPtr window);
}
