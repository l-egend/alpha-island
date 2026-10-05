using System.Windows;
using System.Windows.Threading;
using AlphaIsland.PluginSdk;
using OppoPodsManager;

namespace AlphaIsland.Plugins.OppoPods;

public sealed class OppoPodsPlugin : IIslandPlugin
{
    private IPluginContext? _context;
    private EarbudController? _controller;
    private EarbudView? _view;
    private readonly Func<Action<string>, EarbudController> _factory;
    private bool _active;
    private int _renderQueued;
    public OppoPodsPlugin() : this(log => new EarbudController(log)) { }
    internal OppoPodsPlugin(Func<Action<string>, EarbudController> factory) => _factory = factory;

    public void Initialize(IPluginContext context)
    {
        _context = context;
        context.RegisterSetting(new("auto-connect", "自动连接", PluginSettingKind.Boolean, false,
            "自动连接已在 Windows 中连接的耳机；多副耳机同时在线时需指定首选耳机"));
        context.RegisterSetting(new("auto-reconnect", "断线重连", PluginSettingKind.Boolean, true,
            "私有控制连接断开后重试；手动断开和禁用插件时停止"));
        context.RegisterSetting(new("preferred-name", "首选耳机", PluginSettingKind.Text, "",
            "填写耳机在 Windows 中的名称；留空时只自动选择唯一在线的支持型号"));
        context.RegisterSetting(new("model", "耳机型号", PluginSettingKind.Choice, "自动识别",
            "识别错误时指定准确型号；不会自动改变耳机设置",
            Choices: new[] { "自动识别" }.Concat(DeviceCapabilities.GetModelNames().Distinct().OrderBy(n => n)).ToArray()));
        _controller = _factory(context.Log);
        _controller.Changed += QueueRender;
        context.RegisterPage(new("earbuds", "耳机", CreateView, Glyph: "\uE7F6", Height: 320, Order: 120,
            Activated: () => { _active = true; Render(); }, Deactivated: () => _active = false));
        context.RegisterService(new("bluetooth", token =>
        {
            Log.Sink = context.Log;
            _controller?.Start(token, ReadOptions());
        }, () => { _controller?.Stop(); Log.Sink = null; }));
    }
    private EarbudOptions ReadOptions() => _context == null ? new() : new(
        _context.GetSetting("auto-connect", false), _context.GetSetting("auto-reconnect", true),
        _context.GetSetting("preferred-name", "").Trim(), _context.GetSetting("model", "自动识别"));
    private FrameworkElement CreateView()
    {
        _view = new EarbudView(_controller!);
        _view.IsVisibleChanged += VisibilityChanged;
        Render();
        return _view;
    }
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    { if (_view?.IsVisible == true) Render(); }
    private void QueueRender()
    {
        var context = _context;
        if (context == null || context.Lifetime.IsCancellationRequested || Interlocked.Exchange(ref _renderQueued, 1) != 0) return;
        if (context.Dispatcher.HasShutdownStarted) { Interlocked.Exchange(ref _renderQueued, 0); return; }
        context.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _renderQueued, 0);
            if (_context == context && !context.Lifetime.IsCancellationRequested && _active && _view?.IsVisible == true) Render();
        }));
    }
    private void Render() { if (_view != null && _controller != null) _view.Update(_controller.Snapshot, _controller.Devices); }
    public void OnSettingsChanged() => _controller?.SetOptions(ReadOptions());
    public void Dispose()
    {
        _active = false;
        if (_controller != null) { _controller.Changed -= QueueRender; _controller.Dispose(); }
        if (_view != null) _view.IsVisibleChanged -= VisibilityChanged;
        Log.Sink = null;
        _view = null; _controller = null; _context = null;
    }
}
