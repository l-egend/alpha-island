using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using alpha_island.Core;
using alpha_island.ViewModels;
using Point = System.Windows.Point;

namespace alpha_island.Controls;
internal partial class QuickLauncherWindow : Window
{
    private readonly LauncherViewModel _model;
    private LauncherEntry? _pressedEntry;
    private Point _pressedPosition;
    private string? _lastPointerEntry;
    private long _lastPointerLaunch;
    internal QuickLauncherWindow(LauncherViewModel model)
    {
        _model = model; InitializeComponent(); DataContext = model;
        Deactivated += (_, _) => { if (!model.RunCommand.IsRunning) Hide(); };
        PreviewKeyDown += async (_, args) =>
        {
            if (args.Key == Key.Escape) { Hide(); args.Handled = true; }
            else if (args.Key == Key.Enter) { args.Handled = true; await RunSelectionAsync(); }
            else if (args.Key is Key.Down or Key.Up && SearchBox.IsKeyboardFocusWithin)
            {
                int index = ResultList.SelectedIndex + (args.Key == Key.Down ? 1 : -1);
                ResultList.SelectedIndex = Math.Clamp(index, 0, Math.Max(0, ResultList.Items.Count - 1));
                if (ResultList.SelectedItem != null) ResultList.ScrollIntoView(ResultList.SelectedItem); args.Handled = true;
            }
        };
        ResultList.PreviewMouseLeftButtonDown += (_, args) =>
        {
            _pressedEntry = EntryAt(args.OriginalSource);
            _pressedPosition = args.GetPosition(ResultList);
        };
        ResultList.PreviewMouseLeftButtonUp += async (_, args) =>
        {
            var pressed = _pressedEntry;
            _pressedEntry = null;
            var position = args.GetPosition(ResultList);
            if (pressed == null || EntryAt(args.OriginalSource) != pressed
                || Math.Abs(position.X - _pressedPosition.X) > SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(position.Y - _pressedPosition.Y) > SystemParameters.MinimumVerticalDragDistance) return;
            args.Handled = true;
            if (_model.RunCommand.IsRunning || (_lastPointerEntry == pressed.Id
                && Environment.TickCount64 - _lastPointerLaunch < System.Windows.Forms.SystemInformation.DoubleClickTime)) return;
            _lastPointerEntry = pressed.Id;
            _lastPointerLaunch = Environment.TickCount64;
            _model.Selected = pressed;
            await RunSelectionAsync();
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) { SearchBox.Focus(); SearchBox.SelectAll(); } };
    }

    private LauncherEntry? EntryAt(object source) => source is DependencyObject node
        && ItemsControl.ContainerFromElement(ResultList, node) is ListBoxItem { Content: LauncherEntry entry } ? entry : null;

    private Task RunSelectionAsync() => _model.RunCommand.IsRunning ? Task.CompletedTask : _model.RunCommand.ExecuteAsync(null);
}
