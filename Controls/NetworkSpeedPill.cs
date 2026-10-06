using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Core;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace alpha_island.Controls;

/// <summary>可选择、可排序的两行状态仪表；分项与累计量在悬停详情中。</summary>
public sealed class NetworkSpeedPill : Grid
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };
    private string _rateTip = "网速监测未开启", _sessionTip = "", _resourceTip = "CPU / RAM 等待采样", _powerTip = "功耗等待采样";
    private readonly bool _compact;
    private readonly Dictionary<string, FrameworkElement> _items = new();
    private string[] _selection = [];
    internal TextBlock UploadValue { get; }
    internal TextBlock DownloadValue { get; }
    internal TextBlock CpuValue { get; }
    internal TextBlock RamValue { get; }
    internal TextBlock PowerValue { get; }
    internal string[] DisplayedItems => _selection.ToArray();
    internal SolidColorBrush UploadColor { get; } = new(Color.FromRgb(0x5C, 0xDF, 0xAA));
    internal SolidColorBrush DownloadColor { get; } = new(Color.FromRgb(0x63, 0xBD, 0xFF));
    internal string SessionDescription => _sessionTip;
    public NetworkSpeedPill() : this(false) { }
    public NetworkSpeedPill(bool compact)
    {
        _compact = compact;
        Margin = new Thickness(compact ? 10 : 16, 0, compact ? 10 : 16, 0);
        VerticalAlignment = VerticalAlignment.Center; Height = 32;
        RowDefinitions.Add(new RowDefinition()); RowDefinitions.Add(new RowDefinition());
        UploadValue = AddItem("upload", "↑", UploadColor);
        DownloadValue = AddItem("download", "↓", DownloadColor);
        CpuValue = AddItem("cpu", "CPU", Brushes.LightGray);
        RamValue = AddItem("memory", "RAM", Brushes.LightGray);
        PowerValue = AddItem("power", "⚡", new SolidColorBrush(Color.FromRgb(0xFF, 0xCF, 0x66)));
        SetDisplayItems(StatusDisplayOptions.All);
        SetUnavailable(); SetResources(null, null); SetPower(null, "功耗等待采样");
    }
    internal void SetDisplayItems(IEnumerable<string> selected)
    {
        string[] items = selected.Where(_items.ContainsKey).Distinct().ToArray();
        if (_selection.SequenceEqual(items) && Children.Count > 0) return;
        _selection = items; Children.Clear(); ColumnDefinitions.Clear();
        int columns = Math.Max(1, (items.Length + 1) / 2);
        for (int column = 0; column < columns; column++)
        {
            if (column > 0) ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9) });
            ColumnDefinitions.Add(new ColumnDefinition());
        }
        for (int i = 0; i < items.Length; i++)
        {
            var item = _items[items[i]]; SetColumn(item, (i / 2) * 2); SetRow(item, i % 2);
            SetRowSpan(item, i == items.Length - 1 && items.Length % 2 == 1 ? 2 : 1);
            Children.Add(item);
        }
        for (int column = 1; column < columns; column++)
        {
            var divider = new Border { Width = 1, Height = 24, VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x25, 255, 255, 255)) };
            SetColumn(divider, column * 2 - 1); SetRowSpan(divider, 2); Children.Add(divider);
        }
        if (items.Length == 0)
        {
            var empty = new TextBlock { Text = "系统状态", Foreground = Brushes.LightGray, FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            SetRowSpan(empty, 2); Children.Add(empty);
        }
        RefreshTip();
    }
    private static TextBlock Value(bool compact) => new()
    {
        FontFamily = new FontFamily("Segoe UI"), FontSize = compact ? 10.5 : 12,
        FontWeight = FontWeights.Bold, Foreground = Brushes.White, TextWrapping = TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center
    };
    private TextBlock AddItem(string key, string label, System.Windows.Media.Brush color)
    {
        var line = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(new TextBlock { Text = label, FontSize = label.Length == 3 ? (_compact ? 8 : 9) : 13,
            Foreground = color, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
        var value = Value(_compact); line.Children.Add(value);
        _items[key] = new Viewbox { Child = line, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return value;
    }
    public void SetRates(double upload, double download)
    {
        UploadValue.Text = FormatRate(upload).Replace(" ", ""); DownloadValue.Text = FormatRate(download).Replace(" ", "");
        _rateTip = $"上传 {FormatRate(upload)} · 下载 {FormatRate(download)}"; RefreshTip();
    }
    public void SetResources(double? cpu, double? ram)
    {
        cpu = ValidPercent(cpu); ram = ValidPercent(ram);
        CpuValue.Text = cpu.HasValue ? $"{cpu:0}%" : "—";
        RamValue.Text = ram.HasValue ? $"{ram:0}%" : "—";
        CpuValue.Foreground = new SolidColorBrush(cpu >= 90 ? Color.FromRgb(0xF5, 0x9E, 0x0B) : Colors.White);
        RamValue.Foreground = new SolidColorBrush(ram >= 85 ? Color.FromRgb(0xFF, 0x55, 0x55) : Colors.White);
        _resourceTip = $"CPU {CpuValue.Text} · RAM {RamValue.Text}"; RefreshTip();
    }
    private static double? ValidPercent(double? value) => value.HasValue && double.IsFinite(value.Value) && value >= 0 && value <= 100 ? value : null;
    internal void SetPower(double? watts, string description)
    {
        watts = AlphaIsland.Hardware.HardwarePowerPolicy.ValidWatts(watts);
        PowerValue.Text = watts.HasValue ? $"{watts:0.0}W" : "—W";
        _powerTip = description; RefreshTip();
    }
    public void SetSessionInfo(long? uploaded, long? downloaded, double peakUpload, double peakDownload, IReadOnlyList<double> history)
    {
        _sessionTip = $"本次监测上传 {(uploaded.HasValue ? FormatBytes(uploaded.Value) : "—")}、下载 {(downloaded.HasValue ? FormatBytes(downloaded.Value) : "—")}\n上传峰值 {FormatRate(peakUpload)}、下载峰值 {FormatRate(peakDownload)}";
        RefreshTip();
    }
    public void SetUnavailable() { UploadValue.Text = "—"; DownloadValue.Text = "—"; _rateTip = "网速监测未开启"; _sessionTip = ""; RefreshTip(); }
    private void RefreshTip() => ToolTip = string.Join("\n", new[] { _rateTip, _resourceTip,
        _selection.Contains("power") ? _powerTip : "", _sessionTip }.Where(x => x.Length > 0));
    internal static string FormatShortRate(double speed)
    {
        var (value, unit) = Normalize(speed);
        return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + new[] { "B", "K", "M", "G", "T" }[unit];
    }
    internal static string FormatRate(double speed)
    {
        var (value, unit) = Normalize(speed);
        return value.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + Units[unit] + "/s";
    }
    private static string FormatBytes(long bytes)
    {
        var (value, unit) = Normalize(bytes); return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + Units[unit];
    }
    private static (double Value, int Unit) Normalize(double bytes)
    {
        double value = double.IsFinite(bytes) ? Math.Max(0, bytes) : 0; int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1) { value /= 1024; unit++; } return (value, unit);
    }
}
