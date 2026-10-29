using L = AlphaIsland.PluginSdk.Localization.UiText;
using CommunityToolkit.Mvvm.ComponentModel;
using alpha_island.Core;
using alpha_island.Services;

namespace alpha_island.ViewModels;

internal sealed record ProductivityRow(string Name, string Duration, double Fraction);
internal sealed class ProductivityViewModel(ProductivityService service) : ObservableObject
{
    private DateOnly _day = DateOnly.FromDateTime(DateTime.Today);
    private int _span = 1;
    private bool _followToday = true;
    public string Period => _span == 1 ? _day.ToString("yyyy-MM-dd") : L.F($"{_day.AddDays(1 - _span):yyyy-MM-dd} 至 {_day:yyyy-MM-dd}");
    public bool Enabled => service.Enabled;
    public IReadOnlyList<ProductivityRow> Rows { get; private set; } = [];
    internal void Move(int direction) { _followToday = false; _day = _day.AddDays(direction * _span); Refresh(); }
    internal void Today() { _followToday = true; _day = DateOnly.FromDateTime(DateTime.Today); Refresh(); }
    internal void Span(int days) { _span = days == 7 ? 7 : 1; Refresh(); }
    internal void Refresh()
    {
        if (_followToday) _day = DateOnly.FromDateTime(DateTime.Today);
        var days = service.Days.Where(day => DateOnly.TryParse(day.Date, out var date) && date <= _day && date > _day.AddDays(-_span)).ToArray();
        var categories = new[] { (ProductivityActivity.Focus, L.T("专注")), (ProductivityActivity.Agent, L.T("助手运行")), (ProductivityActivity.Music, L.T("音乐播放")), (ProductivityActivity.Game, L.T("游戏")) };
        var totals = categories.Select(pair => days.Sum(day => day.Seconds(pair.Item1))).ToArray();
        double maximum = Math.Max(1, totals.Max());
        Rows = categories.Select((pair, index) => new ProductivityRow(pair.Item2, Format(totals[index]), totals[index] / maximum)).ToArray();
        OnPropertyChanged(nameof(Rows)); OnPropertyChanged(nameof(Period)); OnPropertyChanged(nameof(Enabled));
    }
    private static string Format(double seconds) => seconds < 60 ? L.F($"{seconds:0} 秒") : seconds < 3600 ? L.F($"{seconds / 60:0} 分钟") : L.F($"{seconds / 3600:0.0} 小时");
}
