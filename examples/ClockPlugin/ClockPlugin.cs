using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AlphaIsland.PluginSdk;

namespace AlphaIsland.Examples.ClockPlugin;

public sealed class ClockPlugin : IIslandPlugin
{
    private IPluginContext? _context;
    private StackPanel? _view;
    private TextBlock? _caption, _time, _date;
    private IPluginScheduledTask? _timer;
    private bool _started, _active;
    public void Initialize(IPluginContext context)
    {
        _context = context;
        context.RegisterSetting(new("caption", "卡片标题", PluginSettingKind.Text, "现在的时间"));
        context.RegisterSetting(new("seconds", "显示秒数", PluginSettingKind.Boolean, true));
        context.RegisterSetting(new("format", "时间格式", PluginSettingKind.Choice, "24 小时", Choices: new[] { "24 小时", "12 小时" }));
        context.RegisterSetting(new("size", "时间字号", PluginSettingKind.Number, 36d, Minimum: 28, Maximum: 44, Step: 2));
        context.RegisterPage(new("clock", "时钟", CreateView, Glyph: "\uE917", Order: 100,
            Activated: () => { _active = true; Update(); SyncTimer(); },
            Deactivated: () => { _active = false; SyncTimer(); }));
        context.RegisterService(new("ticker", token =>
        {
            _started = true;
            _timer = context.Schedule("clock-ui", TimeSpan.FromSeconds(1), () => { if (token.IsCancellationRequested) _timer?.SetPaused(true); else Update(); }, initiallyPaused: true);
            _timer.SetPaused(true); SyncTimer();
        }, () => { _started = false; _timer?.Dispose(); _timer = null; }));
    }
    private FrameworkElement CreateView()
    {
        _view = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _caption = new TextBlock { FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(178, 190, 209)), HorizontalAlignment = HorizontalAlignment.Center };
        _time = new TextBlock { FontSize = 36, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 6) };
        _date = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(178, 190, 209)), HorizontalAlignment = HorizontalAlignment.Center };
        _view.Children.Add(_caption); _view.Children.Add(_time); _view.Children.Add(_date);
        _view.IsVisibleChanged += VisibilityChanged; Update(); return _view;
    }
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { Update(); SyncTimer(); }
    private void SyncTimer()
    { if (_started && _active && _view?.IsVisible == true) _timer?.SetPaused(false); else _timer?.SetPaused(true); }
    private void Update()
    {
        if (_context == null || _time == null) return;
        _caption!.Text = _context.GetSetting("caption", "现在的时间");
        bool seconds = _context.GetSetting("seconds", true);
        bool twelve = _context.GetSetting("format", "24 小时") == "12 小时";
        _time.Text = DateTime.Now.ToString((twelve ? "hh:mm" : "HH:mm") + (seconds ? ":ss" : "") + (twelve ? " tt" : ""), CultureInfo.CurrentCulture);
        _time.FontSize = _context.GetSetting("size", 36d);
        _date!.Text = DateTime.Now.ToString("M月d日 · dddd", CultureInfo.GetCultureInfo("zh-CN"));
    }
    public void OnSettingsChanged() => Update();
    public void Dispose()
    {
        _started = false; _timer?.Dispose(); _timer = null;
        if (_view != null) _view.IsVisibleChanged -= VisibilityChanged;
        _context = null; _view = null; _caption = _time = _date = null;
    }
}
