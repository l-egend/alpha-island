using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AlphaIsland.PluginSdk;

namespace AlphaIsland.Plugins.VivoOffice;

public sealed class VivoOfficePlugin : IIslandPlugin
{
    private readonly VivoConnectionReader _reader;
    private readonly VivoConnectionEvents _events = new();
    private IPluginContext? _context;
    private VivoConnection _current = new();
    private IPluginScheduledTask? _timer;
    private Grid? _view;
    private TextBlock? _nameLabel, _name, _method, _time, _ipLabel, _ip;
    public VivoOfficePlugin() : this(new VivoConnectionReader()) { }
    internal VivoOfficePlugin(VivoConnectionReader reader) => _reader = reader;

    public void Initialize(IPluginContext context)
    {
        _context = context;
        context.RegisterPage(new PluginPage("phone", "vivo 手机", CreateView, Glyph: "\uE8EA", Order: 110,
            Activated: Refresh) { Summary = () => _current.Summary });
        context.RegisterService(new("connection", token =>
        {
            Refresh();
            _timer = context.Schedule("connection", TimeSpan.FromSeconds(2), () => { if (token.IsCancellationRequested) Stop(); else Refresh(); });
        }, Stop));
    }
    private FrameworkElement CreateView()
    {
        _view = new Grid { Width = 390, Height = 180, VerticalAlignment = VerticalAlignment.Center };
        _view.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < 4; i++) _view.RowDefinitions.Add(new RowDefinition());
        var heading = new TextBlock { Text = "vivo 手机", FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 12) };
        _view.Children.Add(heading);
        (_nameLabel, _name) = AddRow(1, "设备名称", "VivoDeviceName");
        (_, _method) = AddRow(2, "连接方式", "VivoConnectionMethod");
        (_, _time) = AddRow(3, "连接时间", "VivoConnectionTime");
        (_ipLabel, _ip) = AddRow(4, "内网 IP", "VivoDeviceIp");
        _name.FontSize = 18; _name.FontWeight = FontWeights.SemiBold;
        Render(); return _view;
    }
    private (TextBlock Label, TextBlock Value) AddRow(int index, string label, string name)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3), VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var caption = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(173, 185, 203)) };
        var value = new TextBlock { Name = name, FontSize = 16, Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(caption); Grid.SetColumn(value, 1); row.Children.Add(value);
        Grid.SetRow(row, index); _view!.Children.Add(row); return (caption, value);
    }
    internal void Refresh()
    {
        _current = _reader.Read(); Render();
        if (_events.Observe(_current, DateTimeOffset.UtcNow))
            _context?.NotifyPage("phone", "手机连接成功", _current.DeviceName + " · "
                + (_current.ConnectionMethod.Length == 0 ? "—" : _current.ConnectionMethod) + " · " + _current.DisplayTime);
    }
    private void Render()
    {
        if (_name == null) return;
        _nameLabel!.Text = _current.IsCurrent ? "设备名称" : "最近设备";
        _name.Text = _current.DeviceName.Length > 0 ? _current.DeviceName : "暂无连接记录";
        _name.ToolTip = _current.Notice.Length > 0 ? _current.Notice : _name.Text;
        _method!.Text = _current.ConnectionMethod.Length > 0 ? _current.ConnectionMethod : "—";
        _method.ToolTip = _method.Text;
        _time!.Text = _current.DisplayTime; _time.ToolTip = "最近一次成功连接的时间";
        _ip!.Text = _current.IpAddress.Length > 0 ? _current.IpAddress : "—";
        _ipLabel!.Text = _current.IpAddress.Length > 0 && !_current.IsIpCurrent ? "最近内网 IP" : "内网 IP";
        _ip.ToolTip = _current.IpAddress.Length == 0 ? "暂无内网地址"
            : _current.IpAddress + (_current.IsIpCurrent ? "" : " · 最近一次连接记录中的地址");
    }
    private void Stop() { _timer?.Dispose(); _timer = null; }
    public void Dispose()
    {
        Stop(); _context = null; _current = new(); _view = null; _nameLabel = _name = _method = _time = _ipLabel = _ip = null;
    }
}
