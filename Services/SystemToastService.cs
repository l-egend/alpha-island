using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using alpha_island.Core;
using Windows.ApplicationModel;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace alpha_island.Services;

/// <summary>官方通知读取 API；UI 线程授权，事件加轮询兜底，首轮只建立基线。</summary>
public sealed class SystemToastService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private UserNotificationListener? _listener;
    private readonly Dictionary<string, byte[]?> _logos = new();
    private HashSet<string> _seen = new(StringComparer.Ordinal);
    private bool _primed, _busy, _disposed, _subscribed;
    public string Status { get; private set; } = "等待通知授权";
    public bool Allowed { get; private set; }
    public event Action<IReadOnlyList<DesktopNotification>, bool>? Received;
    public event Action? StatusChanged;
    public SystemToastService(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += OnTick;
    }
    public static bool HasPackageIdentity
    {
        get { try { return !string.IsNullOrEmpty(Package.Current.Id.Name); } catch { return false; } }
    }
    private void SetStatus(string value, bool allowed = false) { Status = value; Allowed = allowed; StatusChanged?.Invoke(); }
    public void Start()
    {
        if (_disposed) return;
        if (!HasPackageIdentity) { SetStatus("系统通知读取需要安装 MSIX 版本；普通 exe 可使用系统事件通知。"); return; }
        try { _listener ??= UserNotificationListener.Current; CheckAccess(); }
        catch (Exception ex) { SetStatus("通知监听不可用：" + ex.Message); }
    }
    public async Task RequestAccessAsync()
    {
        if (_disposed) return;
        Start();
        if (_listener == null) return;
        try
        {
            await _listener.RequestAccessAsync();
            if (!_disposed) CheckAccess();
        }
        catch (Exception ex) { if (!_disposed) SetStatus("授权失败：" + ex.Message); }
    }
    private void CheckAccess()
    {
        if (_listener == null || _disposed) return;
        var access = _listener.GetAccessStatus();
        if (access != UserNotificationListenerAccessStatus.Allowed)
        {
            _timer.Stop(); _seen.Clear(); _primed = false;
            SetStatus(access == UserNotificationListenerAccessStatus.Denied ? "通知读取已被拒绝，请到 Windows 隐私设置中允许。" : "点击“授权读取”以接收系统通知。");
            return;
        }
        if (!_subscribed)
        {
            try { _listener.NotificationChanged += OnNotificationChanged; _subscribed = true; }
            catch { /* 某些桌面环境不支持事件，定时同步继续工作。 */ }
        }
        SetStatus("系统通知监听已开启", true);
        _timer.Start();
        _ = PollAsync();
    }
    private void OnTick(object? sender, EventArgs e) => _ = PollAsync();
    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (_disposed || _timer.Dispatcher.HasShutdownStarted) return;
        try { _timer.Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) _ = PollAsync(); })); }
        catch (InvalidOperationException) { }
    }
    private async Task PollAsync()
    {
        if (_disposed || _busy || _listener == null) return;
        _busy = true;
        try
        {
            if (_listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed) { CheckAccess(); return; }
            var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            if (_disposed) return;
            bool initial = !_primed;
            var current = new HashSet<string>(StringComparer.Ordinal);
            var received = new List<DesktopNotification>();
            foreach (var notification in notifications.OrderBy(x => x.CreationTime))
            {
                try
                {
                    var appId = notification.AppInfo.AppUserModelId;
                    string key = $"{appId}/{notification.Id}/{notification.CreationTime.UtcTicks}";
                    current.Add(key);
                    if (_seen.Contains(key)) continue;
                    var binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric)
                        ?? notification.Notification.Visual.Bindings.FirstOrDefault();
                    var text = binding?.GetTextElements().Select(x => x.Text ?? "").ToArray() ?? Array.Empty<string>();
                    if (text.Length == 0) continue;
                    byte[]? logo = await LogoAsync(notification.AppInfo);
                    if (_disposed) return;
                    received.Add(new(key, appId, notification.AppInfo.DisplayInfo.DisplayName, Limit(text[0], 300),
                        Limit(string.Join("\n", text.Skip(1)), 4000), notification.CreationTime, WindowsId: notification.Id, Logo: logo));
                }
                catch (Exception ex) { IslandLog.Warn("Toast", "一条通知解析失败：" + ex.GetType().Name); }
            }
            _seen = current; _primed = true;
            if (Status != "系统通知监听已开启") SetStatus("系统通知监听已开启", true);
            if (received.Count > 0) Received?.Invoke(received, initial);
        }
        catch (Exception ex) { if (!_disposed) SetStatus("通知读取暂时失败，将自动重试：" + ex.GetType().Name, true); }
        finally { _busy = false; }
    }
    private static string Limit(string text, int length) => text.Length > length ? text[..length] : text;
    private async Task<byte[]?> LogoAsync(AppInfo app)
    {
        string key = app.AppUserModelId;
        if (_logos.TryGetValue(key, out var cached)) return cached;
        byte[]? result = null;
        try
        {
            using var random = await app.DisplayInfo.GetLogo(new Windows.Foundation.Size(32, 32)).OpenReadAsync();
            if (random.Size <= 262144)
            {
                using var stream = random.AsStreamForRead();
                using var memory = new MemoryStream(); await stream.CopyToAsync(memory); result = memory.ToArray();
            }
        }
        catch { }
        if (_logos.Count >= 64) _logos.Clear();
        _logos[key] = result; return result;
    }
    public void Dispose()
    {
        _disposed = true; _timer.Stop(); _timer.Tick -= OnTick;
        if (_subscribed && _listener != null) try { _listener.NotificationChanged -= OnNotificationChanged; } catch { }
        Received = null; StatusChanged = null; _logos.Clear();
    }
}
