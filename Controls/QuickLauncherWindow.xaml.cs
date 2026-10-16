using System.Windows;
using System.Windows.Input;
using alpha_island.ViewModels;

namespace alpha_island.Controls;
internal partial class QuickLauncherWindow : Window
{
    private readonly LauncherViewModel _model;
    internal QuickLauncherWindow(LauncherViewModel model)
    {
        _model = model; InitializeComponent(); DataContext = model;
        Deactivated += (_, _) => { if (!model.RunCommand.IsRunning) Hide(); };
        PreviewKeyDown += async (_, args) =>
        {
            if (args.Key == Key.Escape) { Hide(); args.Handled = true; }
            else if (args.Key == Key.Enter) { args.Handled = true; await model.RunCommand.ExecuteAsync(null); }
            else if (args.Key is Key.Down or Key.Up && SearchBox.IsKeyboardFocusWithin)
            {
                int index = ResultList.SelectedIndex + (args.Key == Key.Down ? 1 : -1);
                ResultList.SelectedIndex = Math.Clamp(index, 0, Math.Max(0, ResultList.Items.Count - 1));
                if (ResultList.SelectedItem != null) ResultList.ScrollIntoView(ResultList.SelectedItem); args.Handled = true;
            }
        };
        ResultList.MouseDoubleClick += async (_, _) => await model.RunCommand.ExecuteAsync(null);
        IsVisibleChanged += (_, _) => { if (IsVisible) { SearchBox.Focus(); SearchBox.SelectAll(); } };
    }
}
