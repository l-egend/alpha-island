using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Core;
using alpha_island.Services;
using alpha_island.ViewModels;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace alpha_island;
public partial class MainWindow
{
    private ProductivityService? _productivity;
    private ProductivityViewModel? _productivityViewModel;
    private void RegisterProductivityPage()
    {
        InitializeDesktopAssistant();
        var service = new ProductivityService(Dispatcher, IslandTicker.Shared(Dispatcher),
            _runtimeEnabled ? Path.Combine(AppPaths.Root, "productivity.json") : null,
            SettingsRegistry.GetBool(_options, "ProductivityEnabled", true));
        _productivity = service; var model = new ProductivityViewModel(service); _productivityViewModel = model;
        var panel = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };
        panel.Children.Add(UtilityText("生产力统计", 15));
        var period = UtilityText("", 10); panel.Children.Add(period);
        var rows = new StackPanel();
        var span = new ComboBox { ItemsSource = new[] { "按天", "最近 7 天" }, SelectedIndex = 0, Width = 84, Margin = new Thickness(0, 2, 5, 2) };
        span.SelectionChanged += (_, _) => { model.Span(span.SelectedIndex == 1 ? 7 : 1); Render(); };
        var navigation = UtilityButtons(UtilityButton("上一段", () => { model.Move(-1); Render(); }),
            UtilityButton("今天", () => { model.Today(); Render(); }), UtilityButton("下一段", () => { model.Move(1); Render(); }));
        navigation.Children.Insert(0, span); panel.Children.Add(navigation);
        panel.Children.Add(rows);
        var enabled = new CheckBox { Content = "记录本机活动时长", IsChecked = service.Enabled, Margin = new Thickness(0, 8, 0, 4) };
        enabled.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimary");
        void ChangeEnabled()
        {
            SettingsRegistry.Write(_options, "ProductivityEnabled", enabled.IsChecked == true);
            service.SetEnabled(enabled.IsChecked == true); if (_runtimeEnabled) _options.Save(allowRecovery: true);
        }
        enabled.Checked += (_, _) => ChangeEnabled(); enabled.Unchecked += (_, _) => ChangeEnabled(); panel.Children.Add(enabled);
        panel.Children.Add(UtilityButtons(UtilityButton("导出 CSV", async () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = "Alpha-Island-活动统计.csv" };
            if (dialog.ShowDialog() != true) return;
            try { await service.ExportAsync(dialog.FileName, _utilitiesLifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Windows.MessageBox.Show("导出失败：" + ex.Message, "活动统计"); }
        }), UtilityButton("清除统计", service.Clear)));
        panel.Children.Add(UtilityText("仅保留本机最近 90 天时长；不记录聊天内容。各类别可重叠，不能相加作为电脑使用总时长。", 9));
        if (_desktopAssistant != null) panel.Children.Add(new alpha_island.Controls.TimelineView(_desktopAssistant, _utilitiesLifetime.Token));
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.PreviewMouseWheel += (_, args) => { if (alpha_island.Controls.TimelineView.ContainsList(args.OriginalSource as DependencyObject)) return; scroll.ScrollToVerticalOffset(scroll.VerticalOffset - args.Delta / 3d); args.Handled = true; };
        var view = new Grid { Height = 324, VerticalAlignment = VerticalAlignment.Center, RenderTransform = new TranslateTransform() };
        view.Children.Add(scroll); CardViewport.Children.Add(view);
        _pages.Register(new DelegateIslandPage("productivity", "生产力统计", view, 12, 354,
            activated: () => { model.Refresh(); Render(); }, viewportChanged: height => view.Height = height));
        void Render()
        {
            period.Text = model.Period; rows.Children.Clear();
            foreach (var item in model.Rows)
            {
                var row = new Grid { Margin = new Thickness(0, 4, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(UtilityText(item.Name, 11));
                var duration = UtilityText(item.Duration, 11); Grid.SetColumn(duration, 1); row.Children.Add(duration); rows.Children.Add(row);
                var track = new Grid { Height = 4, Margin = new Thickness(0, 0, 0, 3) };
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.0001, item.Fraction), GridUnitType.Star) });
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.0001, 1 - item.Fraction), GridUnitType.Star) });
                var fill = new Border { CornerRadius = new CornerRadius(2), Opacity = item.Fraction > 0 ? 1 : 0 };
                fill.SetResourceReference(Border.BackgroundProperty, "AccentCyan"); track.Children.Add(fill); rows.Children.Add(track);
            }
        }
        service.Changed += () => { model.Refresh(); if (_pages.ActiveId == "productivity") Render(); };
        _workspace.Media.PropertyChanged += OnProductivityMediaChanged;
        _workspace.Focus.Changed += OnProductivityFocusChanged;
        AgentStatus.Changed += OnProductivityAgentChanged;
        model.Refresh(); Render(); OnProductivityFocusChanged(); OnProductivityAgentChanged(null, EventArgs.Empty);
    }
    private void OnProductivityMediaChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        _productivity?.SetActivity(ProductivityActivity.Music, _workspace.Media.Current is { IsPlaying: true, ContentKind: MediaContentKind.Music });
        _desktopAssistant?.NotifyActivityChanged();
    }
    private void OnProductivityFocusChanged()
    { _productivity?.SetActivity(ProductivityActivity.Focus, _pomodoro.Running && !_pomodoro.IsBreak); _assistantReminders?.TickNow(); }
    private void OnProductivityAgentChanged(object? sender, EventArgs args)
    {
        if (!Dispatcher.CheckAccess()) { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => OnProductivityAgentChanged(sender, args)); return; }
        _productivity?.SetActivity(ProductivityActivity.Agent, AgentStatus.Snapshot.Any(activity => activity.State is AgentRunState.Thinking or AgentRunState.Working or AgentRunState.Streaming or AgentRunState.ToolWait));
        _assistantReminders?.TickNow();
    }
    private void StopProductivity()
    {
        _assistantReminders?.Dispose(); _assistantReminders = null;
        _desktopAssistant?.Dispose(); _desktopAssistant = null; _desktopTools?.Dispose();
        _workspace.Media.PropertyChanged -= OnProductivityMediaChanged; _workspace.Focus.Changed -= OnProductivityFocusChanged;
        AgentStatus.Changed -= OnProductivityAgentChanged; _productivity?.Dispose(); _productivity = null;
    }
}
