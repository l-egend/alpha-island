using System.IO;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using C = System.Windows.Controls;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using FontFamily = System.Windows.Media.FontFamily;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace alpha_island.Plugins.Isolation;

internal sealed class RemotePluginView : C.Grid
{
    private readonly C.Canvas _canvas = new();
    private readonly C.Image _background = new() { IsHitTestVisible = false };
    private readonly Dictionary<int, FrameworkElement> _controls = new();
    private readonly HashSet<int> _editing = new();
    private readonly Queue<RemoteCommand> _commands = new();
    private readonly Func<RemoteCommand, Task> _send;
    private readonly Action<Exception> _failed;
    private bool _applying, _sending, _closed;
    private string _lastBackground = "";
    private static readonly DependencyPropertyDescriptor AncestorOpacity = DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(UIElement));
    private readonly List<UIElement> _presentationAncestors = new();
    private bool _presented;
    internal bool IsPresented => !_closed && IsLoaded && IsVisible && _presentationAncestors.Count > 0
        && _presentationAncestors.All(element => element.Opacity > .01);
    internal event Action? PresentationChanged;
    internal string Key { get; }
    internal RemotePluginView(string key, Func<RemoteCommand, Task> send, Action<Exception> failed)
    {
        if (key == "$settings") Height = 650;
        Key = key; _send = send; _failed = failed; NameScope.SetNameScope(this, new NameScope());
        ClipToBounds = true; _canvas.ClipToBounds = true; _canvas.Children.Add(_background);
        Children.Add(new C.Viewbox { Stretch = Stretch.Fill, Child = _canvas });
        Loaded += (_, _) => ObservePresentation();
        Unloaded += (_, _) => { UnobservePresentation(); RefreshPresentation(); };
        IsVisibleChanged += (_, _) => RefreshPresentation();
        PreviewMouseWheel += (_, args) =>
        {
            if (args.OriginalSource is C.TextBox text && text.IsReadOnly) return;
            // At the remote viewport's edge the containing settings page must
            // receive the wheel; swallowing it makes the lower options unreachable.
            if (!_controls.Values.OfType<ScrollBar>().Any(bar => bar.Orientation == C.Orientation.Vertical
                && (args.Delta < 0 ? bar.Value < bar.Maximum - .01 : bar.Value > bar.Minimum + .01))) return;
            Point at = args.GetPosition(_canvas);
            Queue(new(Key, "wheel", Number: args.Delta, X: at.X, Y: at.Y)); args.Handled = true;
        };
    }
    private void ObservePresentation()
    {
        UnobservePresentation();
        for (DependencyObject? node = this; node != null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement element) { _presentationAncestors.Add(element); AncestorOpacity.AddValueChanged(element, OnPresentationChanged); }
        RefreshPresentation();
    }
    private void UnobservePresentation()
    {
        foreach (var element in _presentationAncestors) AncestorOpacity.RemoveValueChanged(element, OnPresentationChanged);
        _presentationAncestors.Clear();
    }
    private void OnPresentationChanged(object? sender, EventArgs args) => RefreshPresentation();
    private void RefreshPresentation()
    {
        bool presented = IsPresented;
        if (_presented == presented) return;
        _presented = presented;
        if (!presented) { _background.Source = null; _lastBackground = ""; }
        PresentationChanged?.Invoke();
    }
    private void Queue(RemoteCommand command)
    {
        if (_applying || _closed) return;
        if (command.Action == "scroll")
        {
            var pending = _commands.Where(item => item.Action != "scroll" || item.Element != command.Element).ToArray();
            _commands.Clear(); foreach (var item in pending) _commands.Enqueue(item);
        }
        if (_commands.Count >= 256) { _failed(new IOException("插件操作队列已满")); return; }
        if (command.Action is "text" or "value" or "choice" or "scroll") _editing.Add(command.Element);
        _commands.Enqueue(command);
        if (!_sending) { _sending = true; _ = Dispatcher.InvokeAsync(Send).Task.Unwrap(); }
    }
    private async Task Send()
    {
        _sending = true;
        try
        {
            while (_commands.TryDequeue(out var command))
            {
                await _send(command);
                if (!_commands.Any(next => next.Element == command.Element && next.Action == command.Action)) _editing.Remove(command.Element);
            }
        }
        catch (Exception ex) { _commands.Clear(); _failed(ex); }
        finally { _sending = false; }
    }
    internal void Close()
    {
        UnobservePresentation();
        _closed = true; IsEnabled = false; _commands.Clear(); _editing.Clear(); _controls.Clear();
        _canvas.Children.Clear(); _background.Source = null; NameScope.SetNameScope(this, new NameScope());
    }
    internal void Apply(RemoteFrame frame)
    {
        if (_closed) return;
        if (!double.IsFinite(frame.Width) || !double.IsFinite(frame.Height) || frame.Width is < 1 or > 1024
            || frame.Height is < 1 or > 1024 || frame.Elements.Length > 1500) throw new InvalidDataException("插件视图尺寸无效");
        _applying = true;
        try
        {
            if (Key == "$settings") Height = frame.Height;
            _canvas.Width = frame.Width; _canvas.Height = frame.Height;
            _background.Width = frame.Width; _background.Height = frame.Height;
            if (frame.Background != _lastBackground && frame.Background.Length != 0)
            { _background.Source = Decode(frame.Background); _lastBackground = frame.Background; }
            var current = frame.Elements.Select(item => item.Id).ToHashSet();
            foreach (int removed in _controls.Keys.Where(id => !current.Contains(id)).ToArray())
            { var old = _controls[removed]; if (old.Name.Length > 0 && ReferenceEquals(FindName(old.Name), old)) UnregisterName(old.Name);
                _canvas.Children.Remove(old); _controls.Remove(removed); _editing.Remove(removed); }
            foreach (var item in frame.Elements)
            {
                if (!new[] { item.X, item.Y, item.Width, item.Height, item.FontSize, item.ClipX, item.ClipY, item.ClipWidth, item.ClipHeight }.All(double.IsFinite)
                    || item.Width is < 0 or > 100000 || item.Height is < 0 or > 100000 || item.Text.Length > 128000
                    || item.FontFamily.Length > 256 || item.Choices?.Length > 256) throw new InvalidDataException("插件控件数据无效");
                if (!_controls.TryGetValue(item.Id, out var element))
                { element = Create(item); _controls.Add(item.Id, element); _canvas.Children.Add(element);
                    if (element.Name.Length > 0 && FindName(element.Name) == null) RegisterName(element.Name, element); }
                C.Canvas.SetLeft(element, item.X); C.Canvas.SetTop(element, item.Y);
                if (item.Width > 8192 || item.Height > 8192) throw new InvalidDataException("插件控件尺寸过大");
                element.Width = item.Width; element.Height = item.Height; element.IsEnabled = item.Enabled;
                element.Clip = new RectangleGeometry(new Rect(item.ClipX, item.ClipY, Math.Max(0, item.ClipWidth), Math.Max(0, item.ClipHeight)));
                var foreground = ParseBrush(item.Foreground, Brushes.White); var background = ParseBrush(item.Background, Brushes.Transparent);
                if (element is C.Control control)
                {
                    control.Foreground = foreground; control.Background = background;
                    control.FontSize = Math.Clamp(item.FontSize, 1, 120); control.FontFamily = SafeFont(item.FontFamily);
                    control.FontWeight = item.Bold ? FontWeights.SemiBold : FontWeights.Normal;
                }
                switch (element)
                {
                    case C.TextBlock text:
                        text.Text = item.Text; text.Foreground = foreground; text.Background = background;
                        text.FontSize = Math.Clamp(item.FontSize, 1, 120); text.FontFamily = SafeFont(item.FontFamily);
                        text.FontWeight = item.Bold ? FontWeights.SemiBold : FontWeights.Normal;
                        text.TextWrapping = item.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
                        text.TextAlignment = Enum.TryParse<TextAlignment>(item.Alignment, out var alignment) ? alignment : TextAlignment.Left;
                        break;
                    case C.TextBox input when !_editing.Contains(item.Id):
                        if (input.Text != item.Text) { int caret = input.CaretIndex; input.Text = item.Text; input.CaretIndex = Math.Min(caret, input.Text.Length); }
                        input.IsReadOnly = item.ReadOnly; input.AcceptsReturn = item.Multiline;
                        input.TextWrapping = item.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap;
                        break;
                    case C.PasswordBox password when !_editing.Contains(item.Id) && item.Checked == false: password.Clear(); break;
                    case C.ComboBox combo when !_editing.Contains(item.Id):
                        var choices = item.Choices ?? [];
                        if (!combo.Items.Cast<string>().SequenceEqual(choices)) combo.ItemsSource = choices;
                        combo.SelectedIndex = Math.Clamp(item.Selected, -1, choices.Length - 1); break;
                    case C.CheckBox check:
                        check.IsChecked = item.Checked;
                        System.Windows.Automation.AutomationProperties.SetName(check, item.ToolTip.Length > 0 ? item.ToolTip : item.Text);
                        if (item.Artwork.Length != 0)
                        {
                            if (check.Tag as string != item.Artwork)
                            {
                                check.Template = SnapshotTemplate;
                                check.Content = new C.Image { Source = Decode(item.Artwork), Stretch = Stretch.Fill, IsHitTestVisible = false };
                                check.Tag = item.Artwork;
                            }
                        }
                        else { check.ClearValue(C.Control.TemplateProperty); check.Tag = null; check.Content = item.Text; }
                        break;
                    case C.Button button:
                        button.ToolTip = item.ToolTip;
                        System.Windows.Automation.AutomationProperties.SetName(button, item.ToolTip.Length > 0 ? item.ToolTip : item.Text);
                        if (item.Artwork.Length != 0)
                        {
                            if (button.Tag as string != item.Artwork)
                            { button.Content = new C.Image { Source = Decode(item.Artwork), Stretch = Stretch.Fill }; button.Tag = item.Artwork; }
                            button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0);
                        }
                        else button.Content = item.Text;
                        break;
                    case C.Slider slider when !_editing.Contains(item.Id):
                        if (!double.IsFinite(item.Minimum) || !double.IsFinite(item.Maximum) || !double.IsFinite(item.Value) || item.Maximum < item.Minimum) break;
                        slider.Minimum = item.Minimum; slider.Maximum = item.Maximum; slider.Value = Math.Clamp(item.Value, item.Minimum, item.Maximum); break;
                    case ScrollBar bar when !_editing.Contains(item.Id):
                        if (!double.IsFinite(item.Minimum) || !double.IsFinite(item.Maximum) || !double.IsFinite(item.Value)
                            || !double.IsFinite(item.Viewport) || item.Maximum < item.Minimum || item.Viewport < 0) break;
                        bar.Orientation = item.Alignment == "Horizontal" ? C.Orientation.Horizontal : C.Orientation.Vertical;
                        bar.Minimum = item.Minimum; bar.Maximum = item.Maximum;
                        bar.ViewportSize = item.Viewport;
                        bar.Value = Math.Clamp(item.Value, item.Minimum, item.Maximum);
                        bar.LargeChange = Math.Max(1, item.Viewport); bar.SmallChange = 16;
                        break;
                }
            }
        }
        finally { _applying = false; }
    }
    private FrameworkElement Create(RemoteVisual item)
    {
        FrameworkElement view;
        switch (item.Kind)
        {
            case "input":
                var text = new C.TextBox { Padding = new Thickness(5, 2, 5, 2), VerticalScrollBarVisibility = C.ScrollBarVisibility.Auto };
                text.TextChanged += (_, _) => Queue(new(Key, "text", item.Id, text.Text)); view = text; break;
            case "password":
                var password = new C.PasswordBox { Padding = new Thickness(10, 7, 10, 7), Template = PasswordTemplate };
                password.PasswordChanged += (_, _) => Queue(new(Key, "text", item.Id, password.Password)); view = password; break;
            case "choice":
                var combo = new C.ComboBox { Padding = new Thickness(4, 1, 4, 1) };
                combo.SelectionChanged += (_, _) => Queue(new(Key, "choice", item.Id, Number: combo.SelectedIndex)); view = combo; break;
            case "check":
                var check = new C.CheckBox(); check.Click += (_, _) => Queue(new(Key, "check", item.Id, Checked: check.IsChecked)); view = check; break;
            case "slider":
                var slider = new C.Slider(); slider.ValueChanged += (_, _) => Queue(new(Key, "value", item.Id, Number: slider.Value)); view = slider; break;
            case "scrollbar":
                var bar = new ScrollBar();
                bar.ValueChanged += (_, _) => Queue(new(Key, "scroll", item.Id, Number: bar.Value));
                System.Windows.Automation.AutomationProperties.SetName(bar, "插件滚动条");
                view = bar; break;
            case "button":
                var button = new C.Button { Padding = new Thickness(0) }; button.Click += (_, _) =>
                {
                    if (IsVisible)
                    {
                        try { var at = PointToScreen(new Point()); Queue(new(Key, "position", X: at.X, Y: at.Y)); }
                        catch (InvalidOperationException) { }
                    }
                    Queue(new(Key, "click", item.Id));
                };
                view = button; break;
            case "text": view = new C.TextBlock { IsHitTestVisible = false }; break;
            default: throw new InvalidDataException("不支持的插件控件");
        }
        if (Regex.IsMatch(item.Name, "^[A-Za-z_][A-Za-z0-9_]{0,127}$")) view.Name = item.Name;
        if (view is C.TextBox or C.PasswordBox)
        {
            view.PreviewKeyDown += (_, args) =>
            { if (args.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Escape) Queue(new(Key, "key", item.Id, args.Key.ToString())); };
            view.GotKeyboardFocus += (_, _) => Queue(new(Key, "focus", item.Id));
            view.LostKeyboardFocus += (_, _) => Queue(new(Key, "blur", item.Id));
        }
        return view;
    }
    private static readonly C.ControlTemplate PasswordTemplate = CreatePasswordTemplate();
    private static C.ControlTemplate CreatePasswordTemplate()
    {
        var border = new FrameworkElementFactory(typeof(C.Border));
        border.SetValue(C.Border.CornerRadiusProperty, new CornerRadius(7));
        foreach (string property in new[] { "Background", "BorderBrush", "BorderThickness" })
        {
            var target = property == "Background" ? C.Border.BackgroundProperty : property == "BorderBrush" ? C.Border.BorderBrushProperty : C.Border.BorderThicknessProperty;
            border.SetBinding(target, new System.Windows.Data.Binding(property)
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        }
        var content = new FrameworkElementFactory(typeof(C.ScrollViewer)) { Name = "PART_ContentHost" };
        content.SetValue(UIElement.FocusableProperty, false);
        content.SetBinding(FrameworkElement.MarginProperty, new System.Windows.Data.Binding("Padding")
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.AppendChild(content);
        return new C.ControlTemplate(typeof(C.PasswordBox)) { VisualTree = border };
    }
    private static readonly C.ControlTemplate SnapshotTemplate = CreateSnapshotTemplate();
    private static C.ControlTemplate CreateSnapshotTemplate()
    {
        var border = new FrameworkElementFactory(typeof(C.Border));
        border.SetValue(C.Border.BackgroundProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(C.ContentPresenter));
        content.SetBinding(C.ContentPresenter.ContentProperty, new System.Windows.Data.Binding("Content")
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.AppendChild(content);
        return new C.ControlTemplate(typeof(C.ContentControl)) { VisualTree = border };
    }
    private static Brush ParseBrush(string value, Brush fallback)
    {
        if (!Regex.IsMatch(value, "^#[A-Fa-f0-9]{6}([A-Fa-f0-9]{2})?$")) return fallback;
        var color = (Color)ColorConverter.ConvertFromString(value); var brush = new SolidColorBrush(color); brush.Freeze(); return brush;
    }
    private static FontFamily SafeFont(string value) => new(value is "Segoe UI" or "Segoe UI Variable" or "Segoe MDL2 Assets"
        or "Segoe Fluent Icons" or "Microsoft YaHei" or "Microsoft YaHei UI" or "Consolas" or "Arial" ? value : "Segoe UI");
    private static BitmapImage Decode(string value)
    {
        if (value.Length > PluginWire.MaximumPacketBytes) throw new InvalidDataException("插件图像过大");
        byte[] bytes = Convert.FromBase64String(value);
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)) is < 1 or > 1024
            || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)) is < 1 or > 1024)
            throw new InvalidDataException("插件 PNG 尺寸无效");
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage(); image.BeginInit(); image.StreamSource = stream; image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        if (image.PixelWidth > 1024 || image.PixelHeight > 1024) throw new InvalidDataException("插件图像尺寸过大");
        image.Freeze(); return image;
    }
}
