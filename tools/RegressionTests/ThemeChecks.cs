using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Core;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using Orientation = System.Windows.Controls.Orientation;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

internal static partial class Program
{
    private static void ThemeControls(alpha_island.MainWindow window, Grid root, HwndSource source)
    {
        var gallery = new Border
        {
            Width = 530, Height = 300, Padding = new Thickness(18), CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromRgb(24, 26, 33)),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom
        };
        var grid = new Grid(); gallery.Child = grid;
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0) }; grid.Children.Add(left);
        left.Children.Add(new TextBlock { Text = "滚动与文件列表", Foreground = Brushes.White, FontSize = 14, Margin = new Thickness(0, 0, 0, 12) });
        var rows = new StackPanel { Width = 460 };
        for (int i = 0; i < 16; i++) rows.Children.Add(new Border
        {
            Height = 42, Margin = new Thickness(0, 0, 0, 5), CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), Padding = new Thickness(10),
            Child = new TextBlock { Text = $"文件 {i + 1:00} · 项目设计与资料", Foreground = Brushes.White, FontSize = 12 }
        });
        var scroll = new ScrollViewer
        {
            Height = 198, Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        left.Children.Add(scroll);
        left.Children.Add(new TextBlock { Text = "纤细外观 · 保留拖动区域", Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), FontSize = 11, Margin = new Thickness(0, 10, 0, 0) });
        var right = new StackPanel(); Grid.SetColumn(right, 1); grid.Children.Add(right);
        right.Children.Add(new TextBlock { Text = "输入与选择", Foreground = Brushes.White, FontSize = 14, Margin = new Thickness(0, 0, 0, 12) });
        var editor = new TextBox { Text = "输入城市或筛选条件", Height = 32, Margin = new Thickness(0, 0, 0, 10) }; right.Children.Add(editor);
        var cities = new ComboBox
        {
            ItemsSource = new[] { new { Display = "北京 · 中国" }, new { Display = "上海 · 中国" } },
            DisplayMemberPath = "Display", SelectedIndex = 0, Height = 32, Margin = new Thickness(0, 0, 0, 10)
        };
        right.Children.Add(cities);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var confirm = new Button { Content = "应用", Margin = new Thickness(0, 0, 8, 0) }; actions.Children.Add(confirm);
        var disabled = new Button { Content = "暂不可用", IsEnabled = false }; actions.Children.Add(disabled); right.Children.Add(actions);
        var multiline = new TextBox { Text = string.Join("\n", Enumerable.Range(1, 18).Select(i => "识别文本与译文 " + i)),
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 106, Margin = new Thickness(0, 12, 0, 0) };
        right.Children.Add(multiline);
        root.Children.Add(gallery);
        try
        {
            root.UpdateLayout();
            var vertical = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
            var horizontal = (ScrollBar)scroll.Template.FindName("PART_HorizontalScrollBar", scroll);
            var verticalTrack = (Track)vertical.Template.FindName("PART_Track", vertical);
            var horizontalTrack = (Track)horizontal.Template.FindName("PART_Track", horizontal);
            Check(vertical.Visibility == Visibility.Visible && horizontal.Visibility == Visibility.Visible
                && vertical.ActualWidth == 12 && horizontal.ActualHeight == 12, "主题滚动区域同时溢出时显示两轴，横竖命中区域稳定");
            ScrollBar.PageDownCommand.Execute(null, vertical); ScrollBar.PageRightCommand.Execute(null, horizontal);
            PumpAnimations(20); root.UpdateLayout();
            Check(scroll.VerticalOffset > 0 && scroll.HorizontalOffset > 0, "透明轨道分页点击仍可向下和向右滚动");
            double down = scroll.VerticalOffset, across = scroll.HorizontalOffset;
            ScrollBar.PageUpCommand.Execute(null, vertical); ScrollBar.PageLeftCommand.Execute(null, horizontal);
            PumpAnimations(20); root.UpdateLayout();
            Check(scroll.VerticalOffset < down && scroll.HorizontalOffset < across, "两轴反向分页命令保持原有滚动行为");
            verticalTrack.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            verticalTrack.Thumb.RaiseEvent(new DragDeltaEventArgs(0, 20) { RoutedEvent = Thumb.DragDeltaEvent });
            verticalTrack.Thumb.RaiseEvent(new DragCompletedEventArgs(0, 20, false) { RoutedEvent = Thumb.DragCompletedEvent });
            horizontalTrack.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            horizontalTrack.Thumb.RaiseEvent(new DragDeltaEventArgs(20, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            horizontalTrack.Thumb.RaiseEvent(new DragCompletedEventArgs(20, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            PumpAnimations(20); root.UpdateLayout();
            Check(scroll.VerticalOffset > 0 && scroll.HorizontalOffset > 0,
                "圆角滑块的原生拖动事件仍能连续改变两轴内容位置");
            scroll.ScrollToEnd(); scroll.ScrollToRightEnd(); PumpAnimations(20); root.UpdateLayout();
            Check(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1 && Math.Abs(scroll.HorizontalOffset - scroll.ScrollableWidth) < 1
                && verticalTrack.IsDirectionReversed && !horizontalTrack.IsDirectionReversed, "两轴滚到末尾后滑块位置和方向与内容一致");
            scroll.ScrollToHome(); scroll.ScrollToLeftEnd();
            editor.Select(0, editor.Text.Length); editor.SelectedText = "北京";
            editor.CaretIndex = 2; root.UpdateLayout();
            Check(editor.Text == "北京" && !editor.GetRectFromCharacterIndex(0).IsEmpty,
                "圆角输入框保留原生选择、替换与文本呈现");
            multiline.ScrollToEnd(); PumpAnimations(20); root.UpdateLayout();
            var editorHost = (ScrollViewer)multiline.Template.FindName("PART_ContentHost", multiline);
            Check(editorHost.VerticalOffset > 0 && editorHost.Template.FindName("PART_VerticalScrollBar", editorHost) is ScrollBar,
                "多行输入框内部也能使用主题滚动条滚动到底部");
            cities.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Down) { RoutedEvent = Keyboard.KeyDownEvent });
            Check(cities.SelectedIndex == 1, "主题下拉框保留键盘方向键选择");
            root.UpdateLayout();
            var selected = (ContentPresenter)cities.Template.FindName("SelectedContent", cities);
            Check(selected.ActualWidth > 0 && selected.Content.ToString()!.Contains("上海"), "天气城市下拉框保留 DisplayMemberPath 和选中项展示");
            var popup = (Popup)cities.Template.FindName("PART_Popup", cities);
            // 验证真实 Popup，但隐藏其子树，避免离屏测试被系统夹到用户桌面。
            popup.Child.Visibility = Visibility.Hidden;
            cities.IsDropDownOpen = true; PumpAnimations(25); popup.Child.UpdateLayout();
            Check(popup.IsOpen && popup.Child.RenderSize.Width >= cities.ActualWidth
                && cities.ItemContainerGenerator.ContainerFromIndex(1) is ComboBoxItem, "主题下拉弹层具备有效呈现源与可选择的项目");
            cities.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            Check(!cities.IsDropDownOpen, "主题下拉框仍可通过 Esc 关闭");
            cities.IsDropDownOpen = false; popup.Child.Visibility = Visibility.Visible;
            SaveThemePreview((FrameworkElement)popup.Child, "docs/screens/theme-dropdown.png");
            bool clicked = false; confirm.Click += (_, _) => clicked = true;
            confirm.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(clicked && disabled.IsEnabled == false, "共用按钮主题保留点击与禁用语义");
            root.UpdateLayout(); SaveThemePreview(gallery, "docs/screens/theme-controls.png");
        }
        finally { cities.IsDropDownOpen = false; root.Children.Remove(gallery); }

        var settings = new alpha_island.Settings.SettingsWindow(persistSettings: false) { ForceClose = true };
        try
        {
            settings.LoadFrom(new IslandOptions());
            typeof(alpha_island.Settings.SettingsWindow).GetMethod("SelectPageByTag", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(settings, new object[] { "system" });
            var settingsRoot = (FrameworkElement)settings.Content;
            settings.Content = null; settingsRoot.Resources = settings.Resources;
            using var settingsSource = new HwndSource(new HwndSourceParameters("Alpha Island theme settings regression")
            { Width = 880, Height = 600, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
            settingsSource.RootVisual = settingsRoot; settingsRoot.UpdateLayout();
            var duration = (Slider)settings.FindName("NotificationDurationSlider");
            double start = duration.Value;
            Slider.IncreaseLarge.Execute(null, duration); PumpAnimations(20);
            Check(duration.Value > start && duration.Template.FindName("PART_Track", duration) is Track,
                "通知设置主题滑杆保留键盘步进与轨道部件");
            var filter = (TextBox)settings.FindName("NotificationBlockedAppsBox");
            filter.Text = "应用名称; 应用 ID"; filter.SelectAll();
            Check(filter.SelectedText == filter.Text && filter.ActualWidth == 180,
                "通知筛选输入框内容可选择且主题没有改变设置布局宽度");
            var settingsScroll = Descendants<ScrollViewer>(settingsRoot).First(x => x.ViewportHeight > 400 && x.ScrollableHeight > 0);
            settingsScroll.ScrollToVerticalOffset(300); PumpAnimations(20); settingsRoot.UpdateLayout();
            Check(settingsScroll.VerticalOffset > 0, "设置页可使用共用滚动模板继续浏览长页面");
            SaveThemePreview(settingsRoot, "docs/screens/theme-settings.png");
        }
        finally { settings.Close(); }

        using var tray = alpha_island.Interop.TrayMenuTheme.Create();
        tray.Items.Add("打开设置"); tray.Items.Add(new System.Windows.Forms.ToolStripMenuItem("整理内存") { Enabled = false });
        tray.Items.Add(new System.Windows.Forms.ToolStripSeparator()); tray.Items.Add("退出程序");
        tray.Size = tray.GetPreferredSize(System.Drawing.Size.Empty); tray.PerformLayout();
        using var trayBitmap = new System.Drawing.Bitmap(tray.Width, tray.Height);
        tray.DrawToBitmap(trayBitmap, new System.Drawing.Rectangle(0, 0, tray.Width, tray.Height));
        trayBitmap.Save(Path.GetFullPath("docs/screens/theme-tray-menu.png"), System.Drawing.Imaging.ImageFormat.Png);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void SaveThemePreview(FrameworkElement view, string file)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, view.ActualWidth, view.ActualHeight);
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(29, 29, 29)), null, bounds);
            context.DrawRectangle(new VisualBrush(view), null, bounds);
        }
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.GetFullPath(file)); encoder.Save(stream);
    }
}
