using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Animation;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;

namespace alpha_island.Controls;

internal sealed partial class DesktopNotificationPill
{
    internal event Action? StackRequested;
    internal Button StackCountButton { get; } = new() { FontSize = 10, Padding = new Thickness(7, 3, 7, 3),
        Margin = new Thickness(3, 0, 0, 0), MinHeight = 23, VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed };
    private readonly Border _stackRear = Plate(20, 6), _stackMiddle = Plate(10, 3);
    private int _stackCount;
    private static Border Plate(double right, double offset) => new()
    {
        CornerRadius = new CornerRadius(11), Margin = new Thickness(right, 0, right, 0), Height = 27,
        VerticalAlignment = VerticalAlignment.Bottom, RenderTransform = new TranslateTransform(0, offset),
        Background = new SolidColorBrush(Color.FromArgb(12, 255, 255, 255)), BorderThickness = new Thickness(1),
        Opacity = .75, IsHitTestVisible = false, Visibility = Visibility.Collapsed
    };
    private void InitializeStack()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foreach (var plate in new[] { _stackRear, _stackMiddle })
        {
            plate.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
            plate.SetResourceReference(Border.BackgroundProperty, "ControlSurfaceBrush");
            if (plate.RenderTransform is TranslateTransform offset)
                plate.SizeChanged += (_, _) => plate.Clip = new RectangleGeometry(new Rect(0,
                    Math.Max(0, plate.ActualHeight - offset.Y), plate.ActualWidth, Math.Min(offset.Y, plate.ActualHeight)));
            Grid.SetColumnSpan(plate, 4); System.Windows.Controls.Panel.SetZIndex(plate, -1); Children.Add(plate);
        }
        StackCountButton.SetResourceReference(StyleProperty, "ThemedButton");
        StackCountButton.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimary");
        StackCountButton.Click += (_, args) => { args.Handled = true; StackRequested?.Invoke(); };
        ActionIconMotion.SetKind(StackCountButton, ActionIconAnimation.Pulse);
        Grid.SetColumn(StackCountButton, 3); Children.Add(StackCountButton);
    }
    internal void SetStackCount(int value)
    {
        value = Math.Max(0, value);
        if (value > 1) Height = 36;
        StackCountButton.Visibility = _stackMiddle.Visibility = value > 1 ? Visibility.Visible : Visibility.Collapsed;
        _stackRear.Visibility = value > 2 ? Visibility.Visible : Visibility.Collapsed;
        StackCountButton.Content = $"共 {(value >= 50 ? "50+" : value.ToString())} 条";
        StackCountButton.ToolTip = "展开这批通知；正文点击仍打开对应应用";
        System.Windows.Automation.AutomationProperties.SetName(StackCountButton, $"展开 {value} 条堆叠通知");
        if (_stackCount != value && value > 1) ActionIconMotion.Play(StackCountButton);
        if (value <= 1) ActionIconMotion.Stop(StackCountButton);
        _stackCount = value;
    }
}
