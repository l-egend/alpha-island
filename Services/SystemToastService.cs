using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Interop;
using Windows.ApplicationModel;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace alpha_island.Services;

/// <summary>官方通知读取 API；UI 线程授权，事件加轮询兜底，首轮只建立基线。</summary>
public sealed class SystemToastService : IDisposable
{
    private readonly IslandTimer _timer;
    private readonly DispatcherTimer _bannerRetry;
    private readonly ToastBannerGuard _banners;
    private UserNotificationListener? _listener;
    private readonly Dictionary<string, byte[]?> _logos = new();
    // Windows notification IDs identify entries across applications. Keep primitive identity
    // before reading AppInfo so unchanged history does not repeat expensive WinRT metadata calls.
    private HashSet<(uint Id, long CreatedUtcTicks)> _seen = new();
    private bool _primed, _busy, _disposed, _subscribed, _refreshRequested, _replaceBanners;
    public bool BannerTakeoverActive => _banners.Running;
    public long HiddenBannerCount => _banners.HiddenCount;
    public string BannerTakeoverStatus => !_replaceBanners ? "系统横幅保留" : !Allowed ? "等待通知读取权限"
        : BannerTakeoverActive ? "原生横幅接管中" : "横幅接管未能启动";
    public string Status { get; private set; } = "等待通知授权";
    public bool Allowed { get; private set; }
    public event Action<IReadOnlyList<DesktopNotification>, bool>? Received;
    public event Action? StatusChanged;
    public SystemToastService(Dispatcher dispatcher) : this(dispatcher, new ToastBannerGuard()) { }
    internal SystemToastService(Dispatcher dispatcher, ToastBannerGuard banners)
    {
        _banners = banners;
        _timer = new IslandTimer(dispatcher, "system-toast") { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += OnTick;
        _bannerRetry = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
        _bannerRetry.Tick += (_, _) => { _bannerRetry.Stop(); _ = PollAsync(); };
        _banners.BannerHidden += OnNativeBannerHidden;
    }
    public static bool HasPackageIdentity
    {
        get { try { return !string.IsNullOrEmpty(Package.Current.Id.Name); } catch { return false; } }
    }
    private void SetStatus(string value, bool allowed = false)
    {
        if (Status != value || Allowed != allowed) IslandLog.Info("Toast", value);
        Status = value; Allowed = allowed;
        SyncBannerReplacement(); StatusChanged?.Invoke();
    }
    public void Start()
    {
        if (_disposed) return;
        try { _listener ??= UserNotificationListener.Current; CheckAccess(); }
        catch (Exception ex) { SetStatus("通知监听不可用：0x" + ex.HResult.ToString("X8") + " " + ex.Message); }
    }
    public async Task RequestAccessAsync()
    {
        if (_disposed) return;
        Start();
        if (_listener == null) return;
        try
        {
            if (_listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
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
            _timer.Stop(); _bannerRetry.Stop(); _seen.Clear(); _primed = false;
            SetStatus(access == UserNotificationListenerAccessStatus.Denied ? "通知读取已被拒绝，请到 Windows 隐私设置中允许。" : "点击“授权读取”以接收系统通知。");
            return;
        }
        if (!_subscribed)
        {
            try { _listener.NotificationChanged += OnNotificationChanged; _subscribed = true; }
            catch (Exception error) { IslandLog.Debug("Toast", "通知事件不可用，保留轮询兜底：0x" + error.HResult.ToString("X8")); }
        }
        SetStatus("系统通知监听已开启", true);
        _timer.Start();
        _ = PollAsync();
    }
    private void OnTick(object? sender, EventArgs e) => _ = PollAsync();
    public void SetBannerReplacementEnabled(bool enabled)
    {
        _replaceBanners = enabled;
        SyncBannerReplacement(); StatusChanged?.Invoke();
    }
    private void SyncBannerReplacement()
    {
        bool wasRunning = _banners.Running;
        bool requested = _replaceBanners && Allowed && !_disposed;
        bool running = _banners.SetEnabled(requested);
        if (wasRunning != running) IslandLog.Info("Toast", running ? "原生通知横幅接管已启用" : "原生通知横幅接管已关闭");
        if (requested && !running) IslandLog.Warn("Toast", "原生通知横幅接管未能启动，保留后置通知撤下处理。");
    }
    private void OnNativeBannerHidden()
    {
        if (_disposed || _timer.Dispatcher.HasShutdownStarted) return;
        try
        {
            _timer.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed || !Allowed) return;
                _ = PollAsync();
                // One retry covers a shell SHOW arriving just before history storage commits.
                _bannerRetry.Stop(); _bannerRetry.Start();
            }));
        }
        catch (InvalidOperationException) { }
    }
    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (_disposed || _timer.Dispatcher.HasShutdownStarted) return;
        try { _timer.Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) _ = PollAsync(); })); }
        catch (InvalidOperationException) { }
    }
    private async Task PollAsync()
    {
        if (_disposed || _listener == null) return;
        if (_busy) { _refreshRequested = true; return; }
        _refreshRequested = false;
        _busy = true;
        try
        {
            if (_listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed) { CheckAccess(); return; }
            var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            if (_disposed) return;
            bool initial = !_primed;
            var current = new HashSet<(uint Id, long CreatedUtcTicks)>();
            var received = new List<DesktopNotification>();
            foreach (var notification in notifications.OrderBy(x => x.CreationTime))
            {
                try
                {
                    var created = notification.CreationTime;
                    var identity = (notification.Id, created.UtcTicks);
                    current.Add(identity);
                    if (_seen.Contains(identity)) continue;
                    var appInfo = notification.AppInfo;
                    var appId = appInfo.AppUserModelId;
                    string key = $"{appId}/{identity.Id}/{identity.UtcTicks}";
                    var binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric)
                        ?? notification.Notification.Visual.Bindings.FirstOrDefault();
                    var text = binding?.GetTextElements().Select(x => x.Text ?? "").ToArray() ?? Array.Empty<string>();
                    if (text.Length == 0) continue;
                    byte[]? logo = await LogoAsync(appInfo);
                    if (_disposed) return;
                    received.Add(CreateNotification(key, appId, appInfo.DisplayInfo.DisplayName, text, created, identity.Id, logo));
                }
                catch (Exception ex) { IslandLog.Warn("Toast", "一条通知解析失败：" + ex.GetType().Name); }
            }
            _seen = current; _primed = true;
            if (Status != "系统通知监听已开启") SetStatus("系统通知监听已开启", true);
            if (received.Count > 0)
            {
                IslandLog.Debug("Toast", $"收到 {received.Count} 条系统通知；首轮={initial}");
                var enriched = await NotificationPayloadReader.EnrichAsync(received);
                if (!_disposed) Received?.Invoke(enriched, initial);
            }
        }
        catch (Exception ex) { if (!_disposed) SetStatus("通知读取暂时失败，将自动重试：" + ex.GetType().Name, true); }
        finally { _busy = false; if (_refreshRequested && !_disposed) { _refreshRequested = false; _ = PollAsync(); } }
    }
    internal static DesktopNotification CreateNotification(string key, string appId, string appName,
        IReadOnlyList<string> text, DateTimeOffset created, uint windowsId, byte[]? logo) =>
        new(key, appId, appName, text.FirstOrDefault() ?? "",
            string.Join("\n", text.Skip(1)), created, WindowsId: windowsId, Logo: logo);
    /// <summary>Only called after the island accepted a new notification for display.
    /// ToastBannerGuard independently handles presentation before this post-delivery
    /// cleanup. Only the accepted entry is removed; no global preferences are changed.</summary>
    public bool SuppressPopup(DesktopNotification notification)
    {
        if (_disposed || !Allowed || _listener == null || notification.WindowsId is not uint id) return false;
        try
        {
            _listener.RemoveNotification(id);
            return true;
        }
        catch (Exception ex)
        {
            IslandLog.Warn("Toast", "撤下已接管的系统通知失败：" + ex.GetType().Name);
            return false;
        }
    }
    private async Task<byte[]?> LogoAsync(AppInfo app)
    {
        string key = app.AppUserModelId;
        if (_logos.TryGetValue(key, out var cached)) return cached;
        byte[]? result = null;
        try
        {
            var logo = app.DisplayInfo.GetLogo(new Windows.Foundation.Size(32, 32));
            if (logo != null)
            {
                using var random = await logo.OpenReadAsync();
                if (random.Size <= 262144)
                {
                    using var stream = random.AsStreamForRead();
                    using var memory = new MemoryStream(); await stream.CopyToAsync(memory); result = memory.ToArray();
                }
            }
        }
        catch { }
        if (result is not { Length: > 0 }) result = await NotificationAppIcon.ReadAsync(key);
        if (_disposed) return result;
        if (_logos.Count >= 64) _logos.Clear();
        if (result is { Length: > 0 }) _logos[key] = result;
        return result;
    }
    public void Dispose()
    {
        _disposed = true; _timer.Dispose(); _timer.Tick -= OnTick;
        _bannerRetry.Stop(); _banners.Dispose();
        if (_subscribed && _listener != null) try { _listener.NotificationChanged -= OnNotificationChanged; } catch { }
        Received = null; StatusChanged = null; _logos.Clear();
    }
}
