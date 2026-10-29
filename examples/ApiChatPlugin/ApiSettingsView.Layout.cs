using System.Windows;
using System.Windows.Controls;

namespace AlphaIsland.Plugins.ApiChat;

public partial class ApiSettingsView
{
    private readonly List<(Grid Row, FrameworkElement Labels, FrameworkElement Editor, Thickness Margin, GridLength Width)> _responsiveRows = new();
    private bool? _compactLayout;

    private void InitializeResponsiveLayout()
    {
        void Visit(DependencyObject node)
        {
            if (node is Grid { ColumnDefinitions.Count: 3 } row)
            {
                var labels = row.Children.OfType<FrameworkElement>().FirstOrDefault(child => Grid.GetColumn(child) == 1);
                var editor = row.Children.OfType<FrameworkElement>().FirstOrDefault(child => Grid.GetColumn(child) == 2);
                if (labels != null && editor != null && row.ColumnDefinitions[0].Width.IsAuto)
                {
                    row.RowDefinitions.Add(new() { Height = GridLength.Auto });
                    row.RowDefinitions.Add(new() { Height = GridLength.Auto });
                    _responsiveRows.Add((row, labels, editor, editor.Margin, row.ColumnDefinitions[2].Width));
                }
            }
            if (node is Panel panel) foreach (UIElement child in panel.Children) Visit(child);
            else if (node is Border { Child: { } child }) Visit(child);
        }
        Visit(AiSettingsRoot);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        bool compact = constraint.Width < 540;
        if (_compactLayout != compact)
        {
            _compactLayout = compact;
            foreach (var item in _responsiveRows)
            {
                item.Row.ColumnDefinitions[2].Width = compact ? new GridLength(0) : item.Width;
                Grid.SetColumnSpan(item.Labels, compact ? 2 : 1);
                Grid.SetRow(item.Editor, compact ? 1 : 0);
                Grid.SetColumn(item.Editor, compact ? 1 : 2);
                Grid.SetColumnSpan(item.Editor, compact ? 2 : 1);
                item.Editor.Margin = compact ? new Thickness(14, 8, 0, 0) : item.Margin;
            }
            foreach (var actions in new[] { AiKeyActions, AiModelActions })
            {
                actions.Width = compact ? double.NaN : 248;
                actions.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
                actions.Margin = new Thickness(compact ? 62 : 0, 0, 16, actions.Margin.Bottom);
            }
        }
        return base.MeasureOverride(constraint);
    }
}
