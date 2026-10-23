using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using alpha_island.Services;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using Control = System.Windows.Controls.Control;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Binding = System.Windows.Data.Binding;
using Orientation = System.Windows.Controls.Orientation;

namespace alpha_island.Controls;

internal sealed class TimelineView : StackPanel
{
    private sealed class Row(string id, string heading) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        public string Id { get; } = id;
        public string Heading { get; } = heading;
        private string _time = "", _detail = "";
        public string Time { get => _time; set => SetProperty(ref _time, value); }
        public string Detail { get => _detail; set => SetProperty(ref _detail, value); }
    }
    private readonly System.Collections.ObjectModel.ObservableCollection<Row> _rows = new();
    private bool _updatingDays;
    private readonly DesktopAssistantService _service;
    private readonly ListBox _list = new() { Height = 240, Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _day = new() { Width = 130, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _status = Text("", 10);
    internal TimelineView(DesktopAssistantService service, CancellationToken lifetime)
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/alpha-island;component/Themes/Controls.xaml") });
        _day.Style = (Style)Resources[typeof(ComboBox)];
        _service = service; Margin = new Thickness(0, 14, 0, 0);
        Children.Add(Text("今日时间轴", 15));
        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        _day.ItemsSource = Enumerable.Range(0, 7).Select(n => DateTime.Today.AddDays(-n).ToString("yyyy-MM-dd")).ToArray(); _day.SelectedIndex = 0;
        _day.SelectionChanged += (_, _) => { if (!_updatingDays) Refresh(); }; actions.Children.Add(_day);
        Button Action(string title, Action run)
        { var button = new Button { Style = (Style)Resources["ThemedButton"], Content = title, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 3), FontSize = 11 }; button.Click += (_, _) => run(); return button; }
        actions.Children.Add(Action("导出", async () =>
        {
            var picker = new Microsoft.Win32.SaveFileDialog { Filter = "JSON 文件|*.json", FileName = "Alpha-Island-时间轴.json" };
            if (picker.ShowDialog() != true) return;
            try { await service.ExportAsync(picker.FileName, lifetime); }
            catch (OperationCanceledException) { }
            catch (Exception) { System.Windows.MessageBox.Show("导出失败，请检查所选位置是否可写。", "时间轴"); }
        }));
        actions.Children.Add(Action("清空", service.Clear)); Children.Add(actions); Children.Add(_status);
        VirtualizingPanel.SetIsVirtualizing(_list, true); VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Pixel); ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        var outer = new FrameworkElementFactory(typeof(Border)); outer.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        outer.SetValue(Border.PaddingProperty, new Thickness(10, 7, 10, 7)); outer.SetValue(Border.MarginProperty, new Thickness(0, 2, 0, 4));
        outer.SetResourceReference(Border.BackgroundProperty, "ControlSurfaceBrush");
        outer.SetValue(Border.BorderThicknessProperty, new Thickness(2, 0, 0, 0)); outer.SetResourceReference(Border.BorderBrushProperty, "AccentCyan");
        var stack = new FrameworkElementFactory(typeof(StackPanel)); outer.AppendChild(stack);
        foreach (string property in new[] { "Time", "Heading", "Detail" })
        {
            var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetBinding(TextBlock.TextProperty, new Binding(property));
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary"); label.SetValue(TextBlock.FontSizeProperty, property == "Heading" ? 12d : 10d);
            label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); if (property == "Time") label.SetValue(UIElement.OpacityProperty, .7);
            stack.AppendChild(label);
        }
        _list.ItemTemplate = new DataTemplate { VisualTree = outer };
        _list.ItemsSource = _rows;
        var itemStyle = new Style(typeof(ListBoxItem)); itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        itemStyle.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = presenter })); _list.ItemContainerStyle = itemStyle;
        _list.PreviewMouseWheel += (_, args) =>
        {
            var scroll = FindScroll(_list); scroll?.ScrollToVerticalOffset(scroll.VerticalOffset - args.Delta / 3d); args.Handled = true;
        };
        Children.Add(_list); Children.Add(Text("详细事件仅保存在本机，默认保留 7 天；未启用期间不补造活动记录。", 9));
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
        service.Changed += () => { if (IsVisible) Refresh(); }; Refresh();
    }
    private void Refresh()
    {
        string selected = _day.SelectedItem as string ?? DateTime.Today.ToString("yyyy-MM-dd");
        var days = Enumerable.Range(0, _service.RetentionDays).Select(n => DateTime.Today.AddDays(-n).ToString("yyyy-MM-dd")).ToArray();
        if (_day.Items.Count != days.Length || !Equals(_day.Items[0], days[0]))
        { _updatingDays = true; _day.ItemsSource = days; _day.SelectedItem = days.Contains(selected) ? selected : days[0]; _updatingDays = false; selected = (string)_day.SelectedItem; }
        var entries = _service.Entries.Where(e => e.Started.LocalDateTime.ToString("yyyy-MM-dd") == selected).OrderByDescending(e => e.Started).ToArray();
        _status.Text = (!_service.Enabled ? "记录已暂停 · " : "") + (entries.Length == 0 ? "这一天暂无已记录的活动。" : $"共 {entries.Length} 段活动；各类别可重叠。");
        string Kind(string value) => value switch { "boot" => "开机", "game" => "游戏", "music" => "音乐", "camera" => "摄像头", "microphone" => "麦克风", _ => "活动" };
        var ids = entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var row in _rows.Where(r => !ids.Contains(r.Id)).ToArray()) _rows.Remove(row);
        var existing = _rows.ToDictionary(r => r.Id, StringComparer.Ordinal);
        for (int i = 0; i < entries.Length; i++)
        {
            var e = entries[i]; existing.TryGetValue(e.Id, out var row);
            if (row == null) { row = new(e.Id, Kind(e.Kind) + " · " + e.Name); _rows.Insert(i, row); }
            else if (i >= _rows.Count || !ReferenceEquals(_rows[i], row)) _rows.Move(_rows.IndexOf(row), i);
            row.Time = e.Started.LocalDateTime.ToString("HH:mm:ss") + (e.Kind == "boot" ? "" : " — " + (e.Ended?.LocalDateTime.ToString("HH:mm:ss") ?? "进行中"));
            row.Detail = e.Interrupted ? "记录在上次观察时结束，未推测中断期间的活动。" : e.Kind == "boot" ? "Windows 启动事件" : $"已观察 {Math.Max(0, ((e.Ended ?? DateTimeOffset.UtcNow) - e.Started).TotalMinutes):0.#} 分钟";
        }
    }
    private static TextBlock Text(string value, double size) { var text = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) }; text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary"); return text; }
    internal static bool ContainsList(DependencyObject? node)
    { while (node != null) { if (node is TimelineView) return true; node = node is Visual ? VisualTreeHelper.GetParent(node) : null; } return false; }
    private static ScrollViewer? FindScroll(DependencyObject node)
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) { var child = VisualTreeHelper.GetChild(node, i); if (child is ScrollViewer scroll) return scroll; if (FindScroll(child) is { } nested) return nested; } return null; }
}
