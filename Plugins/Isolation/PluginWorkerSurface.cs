using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using C = System.Windows.Controls;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;

namespace alpha_island.Plugins.Isolation;

/// <summary>Plugin-owned visuals stay in its process; only pixels and typed control values cross the pipe.</summary>
internal sealed class PluginWorkerSurface
{
    private sealed class Identity(int value) { internal readonly int Value = value; }
    private readonly ConditionalWeakTable<FrameworkElement, Identity> _identities = new();
    private readonly Dictionary<int, FrameworkElement> _elements = new();
    private int _nextId;
    private long _revision;
    internal FrameworkElement View { get; }
    internal PluginWorkerSurface(FrameworkElement view) => View = view;
    internal bool Prefill(string text)
    {
        View.Measure(new Size(414, 294)); View.Arrange(new Rect(0, 0, 414, 294)); View.UpdateLayout();
        C.TextBox? Find(DependencyObject node)
        {
            if (node is C.TextBox box && box.Name is "AiInputBox" or "LocalAgentInput") return box;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                if (Find(VisualTreeHelper.GetChild(node, i)) is { } result) return result;
            return null;
        }
        if (Find(View) is not { } input) return false;
        input.SetCurrentValue(C.TextBox.TextProperty, text[..Math.Min(text.Length, input.MaxLength > 0 ? input.MaxLength : 12000)]);
        input.GetBindingExpression(C.TextBox.TextProperty)?.UpdateSource(); return true;
    }
    internal RemoteFrame Capture(double width, double height, double scale = 1)
    {
        width = Math.Clamp(double.IsFinite(width) ? width : 414, 160, 1024);
        height = Math.Clamp(double.IsFinite(height) ? height : 294, 80, 1024);
        scale = Math.Clamp(double.IsFinite(scale) ? scale : 1, 1, 3);
        scale = Math.Min(scale, Math.Sqrt((PluginWire.MaximumImagePixels - 8192d) / (width * height)));
        View.Measure(new Size(width, height)); View.Arrange(new Rect(0, 0, width, height)); View.UpdateLayout();
        var controls = new List<RemoteVisual>(); var hidden = new List<(FrameworkElement View, double Opacity)>();
        _elements.Clear();
        void Walk(DependencyObject node)
        {
            if (controls.Count >= 1500) throw new InvalidDataException("插件控件数量超过限制");
            if (node is FrameworkElement element && element.Visibility == Visibility.Visible
                && element.ActualWidth > 0 && element.ActualHeight > 0)
            {
                string kind = element switch
                {
                    C.PasswordBox => "password", C.TextBox => "input", C.ComboBox => "choice",
                    C.CheckBox => "check", C.Slider => "slider", ScrollBar => "scrollbar", C.Button => "button", C.TextBlock => "text", _ => ""
                };
                if (kind.Length != 0)
                {
                    var position = element.TransformToAncestor(View).Transform(new Point());
                    var box = new Rect(position, new Size(element.ActualWidth, element.ActualHeight));
                    Rect clip = Rect.Intersect(box, new Rect(0, 0, width, height));
                    for (DependencyObject? parent = VisualTreeHelper.GetParent(element); parent != null && !ReferenceEquals(parent, View); parent = VisualTreeHelper.GetParent(parent))
                    {
                        if (parent is C.ScrollContentPresenter viewport)
                        {
                            var origin = viewport.TransformToAncestor(View).Transform(new Point());
                            clip.Intersect(new Rect(origin, viewport.RenderSize));
                        }
                    }
                    if (clip.IsEmpty || clip.Width < 1 || clip.Height < 1) return;
                    int id = _identities.GetValue(element, _ => new(++_nextId)).Value;
                    _elements[id] = element;
                    string text = element switch
                    {
                        C.TextBlock block => block.Text, C.TextBox input => input.Text, C.ComboBox choice => choice.Text,
                        C.ContentControl content => content.Content switch { string label => label, C.TextBlock label => label.Text, _ => "" }, _ => ""
                    };
                    if (text.Length > 128_000) text = text[^128_000..];
                    var control = element as C.Control; var blockElement = element as C.TextBlock;
                    var choices = element is C.ComboBox combo ? combo.Items.Cast<object>().Take(256).Select(item => ChoiceText(combo, item)).ToArray() : null;
                    controls.Add(new(id, kind, element.Name, box.X, box.Y, box.Width, box.Height,
                        text, Color(control?.Foreground ?? blockElement?.Foreground), Color(control?.Background ?? blockElement?.Background),
                        control?.FontSize ?? blockElement?.FontSize ?? 12, (control?.FontFamily ?? blockElement?.FontFamily)?.Source ?? "Segoe UI", element.IsEnabled,
                        element is C.TextBox read && read.IsReadOnly, element is C.TextBox multi && (multi.AcceptsReturn || multi.TextWrapping == TextWrapping.Wrap),
                        choices, (element as C.ComboBox)?.SelectedIndex ?? -1,
                        (element as RangeBase)?.Minimum ?? 0, (element as RangeBase)?.Maximum ?? 100, (element as RangeBase)?.Value ?? 0,
                        element is C.PasswordBox password ? password.Password.Length > 0 : (element as C.CheckBox)?.IsChecked, clip.X - box.X, clip.Y - box.Y, clip.Width, clip.Height,
                        (control?.FontWeight ?? blockElement?.FontWeight ?? FontWeights.Normal).ToOpenTypeWeight() >= 600,
                        blockElement?.TextWrapping == TextWrapping.Wrap, element is ScrollBar bar ? bar.Orientation.ToString() : blockElement?.TextAlignment.ToString() ?? "Left",
                        element is C.Button or C.CheckBox ? Snapshot(element, box.Width, box.Height, scale) : "", element.ToolTip as string ?? "", (element as ScrollBar)?.ViewportSize ?? 0, element is C.ComboBox { IsEditable: true },
                        blockElement != null && double.IsFinite(blockElement.LineHeight) ? blockElement.LineHeight : 0));
                    hidden.Add((element, element.Opacity));
                    return; // A control's template is rendered by the host, not duplicated as child controls.
                }
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(View);
        try
        {
            foreach (var item in hidden) item.View.SetCurrentValue(UIElement.OpacityProperty, 0d);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(View); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var bytes = new MemoryStream(); encoder.Save(bytes);
            return new(width, height, Convert.ToBase64String(bytes.ToArray()), controls.ToArray(), ++_revision);
        }
        finally { foreach (var item in hidden) item.View.SetCurrentValue(UIElement.OpacityProperty, item.Opacity); }
    }
    private static string ChoiceText(C.ComboBox combo, object item)
    {
        object? value = item;
        if (!string.IsNullOrEmpty(combo.DisplayMemberPath)) value = item.GetType().GetProperty(combo.DisplayMemberPath)?.GetValue(item);
        if (value is C.ContentControl content) value = content.Content;
        return value?.ToString() ?? "";
    }
    private static string Color(Brush? brush) => brush is SolidColorBrush color ? color.Color.ToString() : "";
    private static string Snapshot(FrameworkElement element, double width, double height, double scale)
    {
        if (width > 1024 || height > 1024) return "";
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(width * scale)), Math.Max(1, (int)Math.Ceiling(height * scale)), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        // Render in local coordinates. Rendering an arranged child directly also
        // applies its layout offset, which can put all pixels outside this bitmap.
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(element) { AutoLayoutContent = false,
                Stretch = Stretch.Fill },
                null, new Rect(0, 0, width, height));
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var bytes = new MemoryStream(); encoder.Save(bytes); return Convert.ToBase64String(bytes.ToArray());
    }
    internal bool Input(RemoteCommand command)
    {
        if (command.Action == "wheel")
        {
            if (!double.IsFinite(command.Number) || !double.IsFinite(command.X) || !double.IsFinite(command.Y)) return false;
            var hit = View.InputHitTest(new Point(command.X, command.Y)) as DependencyObject;
            for (var node = hit; node != null; node = ScrollParent(node))
                if (node is C.ScrollViewer scroll)
                {
                    double target = Math.Clamp(scroll.VerticalOffset - command.Number / 3, 0, scroll.ScrollableHeight);
                    if (Math.Abs(target - scroll.VerticalOffset) < .01) continue;
                    scroll.ScrollToVerticalOffset(target); return true;
                }
            return false;
        }
        if (!_elements.TryGetValue(command.Element, out var element) || !element.IsEnabled) return false;
        if (command.Action == "key" && Enum.TryParse<Key>(command.Text, out var key) && key is Key.Enter or Key.Escape)
        {
            var source = PresentationSource.FromVisual(View);
            if (source == null) return false;
            var args = new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            element.RaiseEvent(args);
            if (!args.Handled) { args.RoutedEvent = Keyboard.KeyDownEvent; element.RaiseEvent(args); }
            return args.Handled;
        }
        if (command.Action is "focus" or "blur")
        {
            element.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount,
                command.Action == "focus" ? null : element, command.Action == "focus" ? element : null)
                { RoutedEvent = command.Action == "focus" ? Keyboard.GotKeyboardFocusEvent : Keyboard.LostKeyboardFocusEvent });
            return true;
        }
        switch (element)
        {
            case C.TextBox input when command.Action == "text":
                input.SetCurrentValue(C.TextBox.TextProperty, (command.Text ?? "")[..Math.Min(command.Text?.Length ?? 0, 128_000)]);
                input.GetBindingExpression(C.TextBox.TextProperty)?.UpdateSource(); return true;
            case C.PasswordBox password when command.Action == "text": password.Password = command.Text ?? ""; return true;
            case C.ComboBox { IsEditable: true } combo when command.Action == "choice-text":
                combo.SetCurrentValue(C.ComboBox.TextProperty, (command.Text ?? "")[..Math.Min(command.Text?.Length ?? 0, 256)]); return true;
            case C.ComboBox combo when command.Action == "choice": combo.SetCurrentValue(Selector.SelectedIndexProperty, Math.Clamp((int)command.Number, -1, combo.Items.Count - 1)); return true;
            case C.CheckBox check when command.Action == "check": check.SetCurrentValue(ToggleButton.IsCheckedProperty, command.Checked); check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); return true;
            case C.Slider slider when command.Action == "value" && double.IsFinite(command.Number): slider.SetCurrentValue(RangeBase.ValueProperty, Math.Clamp(command.Number, slider.Minimum, slider.Maximum)); return true;
            case ScrollBar bar when command.Action == "scroll" && double.IsFinite(command.Number):
                double offset = Math.Clamp(command.Number, bar.Minimum, bar.Maximum);
                for (var node = ScrollParent(bar); node != null; node = ScrollParent(node))
                    if (node is C.ScrollViewer owner)
                    {
                        if (bar.Orientation == C.Orientation.Vertical) owner.ScrollToVerticalOffset(offset);
                        else owner.ScrollToHorizontalOffset(offset);
                        return true;
                    }
                return false;
            case C.Button button when command.Action == "click":
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                if (button.Command?.CanExecute(button.CommandParameter) == true) button.Command.Execute(button.CommandParameter);
                return true;
            default: return false;
        }
    }
    private static DependencyObject? ScrollParent(DependencyObject node) => node is Visual
        ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
