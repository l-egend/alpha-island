using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using alpha_island.Animation;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace alpha_island.Controls;

/// <summary>Independent non-activating output for speech recognition or region translation.</summary>
internal sealed class SubtitleIslandWindow : Window
{
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private readonly TextBlock _text = new() { Foreground = Brushes.White, FontSize = 22, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, LineHeight = 30 };
    private readonly ScrollViewer _scroll;
    private readonly Rectangle? _anchor;
    private readonly bool _belowSelection;
    private bool _positioned, _positioning;
    internal string Translation => _text.Text;
    internal SubtitleIslandWindow(Rectangle? anchor, bool belowSelection, Action stop, Action<string> copy,
        string caption = "区域翻译", bool translation = true)
    {
        _anchor = anchor; _belowSelection = belowSelection;
        Title = "Alpha Island · " + caption; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
        Background = Brushes.Transparent; Width = 960; SizeToContent = SizeToContent.Height;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI, Microsoft YaHei");
        _scroll = new ScrollViewer { Content = _text, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 210 };
        var header = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = caption, Foreground = Brushes.White, Opacity = .7, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        string stopLabel = translation ? "停止区域翻译" : "停止语音识别";
        var stopButton = new System.Windows.Controls.Button { Content = "×", Width = 28, Height = 26, Padding = new Thickness(0),
            Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), ToolTip = stopLabel };
        System.Windows.Automation.AutomationProperties.SetName(stopButton, stopLabel);
        stopButton.Click += (_, _) => stop(); Grid.SetColumn(stopButton, 1); header.Children.Add(stopButton);
        var body = new StackPanel(); body.Children.Add(header); body.Children.Add(_scroll);
        Content = new Border { Background = new SolidColorBrush(Color.FromArgb(225, 20, 24, 32)), CornerRadius = new CornerRadius(20),
            Padding = new Thickness(22, 10, 22, 12), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(65, 255, 255, 255)), Child = body };
        var menu = new ContextMenu();
        var copyItem = new MenuItem { Header = translation ? "复制译文" : "复制识别文字" }; copyItem.Click += (_, _) => { try { copy(_text.Text); } catch (ExternalException) { } };
        var stopItem = new MenuItem { Header = stopLabel }; stopItem.Click += (_, _) => stop();
        menu.Items.Add(copyItem); menu.Items.Add(stopItem); ContextMenu = menu;
        MouseLeftButtonDown += (_, _) => { try { DragMove(); _positioned = true; } catch (InvalidOperationException) { } };
        SourceInitialized += (_, _) => SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0x11);
        SizeChanged += (_, _) => { if (!_positioned) PositionAtBottom(); };
    }
    internal void UpdateText(string translation)
    {
        _text.Text = translation;
        if (!IsVisible) { Show(); PositionAtBottom(); }
        if (!MotionPolicy.ReduceMotion)
            _text.BeginAnimation(OpacityProperty, new DoubleAnimation(.55, 1, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.Stop });
        _scroll.ScrollToEnd();
    }
    private void PositionAtBottom()
    {
        if (_positioning) return;
        _positioning = true;
        try
        {
        var screen = _anchor.HasValue ? System.Windows.Forms.Screen.FromRectangle(_anchor.Value) : System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var area = screen.WorkingArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        double dpiX = Math.Max(1, transform.M11), dpiY = Math.Max(1, transform.M22);
        Width = Math.Clamp(area.Width * .78 / dpiX, Math.Min(360, area.Width / dpiX - 24), 1100);
        UpdateLayout();
        int width = (int)Math.Round(ActualWidth * dpiX), height = (int)Math.Round(ActualHeight * dpiY);
        int x = area.Left + (area.Width - width) / 2;
        int y = _belowSelection && _anchor.HasValue ? _anchor.Value.Bottom + 12 : area.Bottom - height - 64;
        if (_belowSelection && _anchor.HasValue && y + height > area.Bottom - 12)
            y = _anchor.Value.Top - height - 12;
        y = Math.Clamp(y, area.Top + 12, Math.Max(area.Top + 12, area.Bottom - height - 12));
        SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), x, y, width, height, 0x10);
        }
        finally { _positioning = false; }
    }
}
