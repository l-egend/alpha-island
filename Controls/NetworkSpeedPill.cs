using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace alpha_island.Controls;

/// <summary>两行状态仪表：左侧上传/下载，右侧 CPU/RAM；累计量在悬停详情中。</summary>
public sealed class NetworkSpeedPill : Grid
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };
    private string _rateTip = "网速监测未开启", _sessionTip = "", _resourceTip = "CPU / RAM 等待采样";
    internal TextBlock UploadValue { get; }
    internal TextBlock DownloadValue { get; }
    internal TextBlock CpuValue { get; }
    internal TextBlock RamValue { get; }
    internal string SessionDescription => _sessionTip;
    public NetworkSpeedPill() : this(false) { }
    public NetworkSpeedPill(bool compact)
    {
        Margin = new Thickness(compact ? 5 : 7, 0, compact ? 5 : 7, 0);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7) });
        ColumnDefinitions.Add(new ColumnDefinition());
        var network = TwoRows(); var resources = TwoRows();
        network.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        network.ColumnDefinitions.Add(new ColumnDefinition());
        resources.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(compact ? 26 : 29) });
        resources.ColumnDefinitions.Add(new ColumnDefinition());
        Children.Add(network); SetColumn(resources, 2); Children.Add(resources);
        UploadValue = AddNetwork(network, 0, "↑", compact);
        DownloadValue = AddNetwork(network, 1, "↓", compact);
        CpuValue = AddResource(resources, 0, "CPU", compact);
        RamValue = AddResource(resources, 1, "RAM", compact);
        var divider = new Border { Width = 1, Height = 28, Background = new SolidColorBrush(Color.FromArgb(0x35, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center };
        SetColumn(divider, 1); Children.Add(divider);
        SetUnavailable(); SetResources(null, null);
    }
    private static Grid TwoRows()
    {
        var grid = new Grid { Height = 32, VerticalAlignment = VerticalAlignment.Center };
        grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition()); return grid;
    }
    private static TextBlock Value(bool compact) => new()
    {
        FontFamily = new FontFamily("Segoe UI"), FontSize = compact ? 10.5 : 12,
        FontWeight = FontWeights.Bold, Foreground = Brushes.White, TextWrapping = TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center
    };
    private static TextBlock AddNetwork(Grid grid, int row, string arrow, bool compact)
    {
        var glyph = new TextBlock { Text = arrow, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        SetRow(glyph, row); grid.Children.Add(glyph);
        var value = Value(compact);
        var fit = new Viewbox { Child = value, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Right };
        SetRow(fit, row); SetColumn(fit, 1); grid.Children.Add(fit); return value;
    }
    private static TextBlock AddResource(Grid grid, int row, string name, bool compact)
    {
        var badge = new Border
        {
            CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x25, 255, 255, 255)),
            Margin = new Thickness(0, 1, 0, 1), Padding = new Thickness(2, 0, 2, 0),
            Child = new TextBlock { Text = name, FontSize = compact ? 8 : 9, FontWeight = FontWeights.Bold, Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }
        };
        SetRow(badge, row); grid.Children.Add(badge);
        var value = Value(compact);
        var fit = new Viewbox { Child = value, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(2, 0, 0, 0) };
        SetColumn(fit, 1); SetRow(fit, row); grid.Children.Add(fit); return value;
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
    public void SetSessionInfo(long? uploaded, long? downloaded, double peakUpload, double peakDownload, IReadOnlyList<double> history)
    {
        _sessionTip = $"本次监测上传 {(uploaded.HasValue ? FormatBytes(uploaded.Value) : "—")}、下载 {(downloaded.HasValue ? FormatBytes(downloaded.Value) : "—")}\n上传峰值 {FormatRate(peakUpload)}、下载峰值 {FormatRate(peakDownload)}";
        RefreshTip();
    }
    public void SetUnavailable() { UploadValue.Text = "—"; DownloadValue.Text = "—"; _rateTip = "网速监测未开启"; _sessionTip = ""; RefreshTip(); }
    private void RefreshTip() => ToolTip = string.Join("\n", new[] { _rateTip, _resourceTip, _sessionTip }.Where(x => x.Length > 0));
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
