using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AlphaIsland.PluginSdk;
using alpha_island.Animation;
using CommunityToolkit.Mvvm.ComponentModel;

internal static partial class Program
{
    private sealed class OpacityState : ObservableObject
    { private double _value = .6; public double Value { get => _value; set => SetProperty(ref _value, value); } }
    private static void ClockLifetimeChecks()
    {
        using var host = new HwndSource(new HwndSourceParameters("Animation lifetime fixture")
        { Width = 200, Height = 100, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        var translate = new TranslateTransform();
        var element = new Border { Background = System.Windows.Media.Brushes.White, RenderTransform = translate, Opacity = .2 };
        host.RootVisual = element;
        AnimationClockLifetime.Begin(element, UIElement.OpacityProperty, new DoubleAnimation(.2, .7, TimeSpan.FromMilliseconds(35)));
        PumpUntilState(() => !element.HasAnimatedProperties);
        Check(Math.Abs(element.Opacity - .7) < .001, "UIElement 的有限动画提交终值并释放时钟");
        AnimationClockLifetime.Begin(translate, TranslateTransform.XProperty, new DoubleAnimation(0, 10, TimeSpan.FromMilliseconds(35)));
        PumpUntilState(() => !translate.HasAnimatedProperties);
        Check(translate.X == 10 && ReferenceEquals(element.RenderTransform, translate), "Transform 结束后释放时钟并复用原有变换对象");
        AnimationClockLifetime.Begin(element, UIElement.OpacityProperty, new DoubleAnimation(.7, 0, TimeSpan.FromMilliseconds(100)));
        PumpAnimations(20);
        AnimationClockLifetime.Begin(element, UIElement.OpacityProperty, new DoubleAnimation(element.Opacity, .9, TimeSpan.FromMilliseconds(25)));
        PumpAnimations(150);
        Check(!element.HasAnimatedProperties && Math.Abs(element.Opacity - .9) < .001, "被替换动画的完成事件不会覆盖新动画终值");
        AnimationClockLifetime.Begin(translate, TranslateTransform.XProperty, new DoubleAnimation(0, 20, TimeSpan.FromMilliseconds(50)) { RepeatBehavior = RepeatBehavior.Forever });
        PumpAnimations(20); Check(translate.HasAnimatedProperties, "循环时钟在有效生命周期内继续运行");
        AnimationClockLifetime.Begin(translate, TranslateTransform.XProperty, null);
        Check(!translate.HasAnimatedProperties && translate.X == 10, "显式取消循环时钟恢复原来的结构变换值");
        AnimationReset.Once(element, UIElement.OpacityProperty, new DoubleAnimation(.9, .5, TimeSpan.FromMilliseconds(30)), .5);
        PumpUntilState(() => !element.HasAnimatedProperties); Check(element.Opacity == .5, "Once 清理也覆盖 UIElement 而非只覆盖 Animatable");
        var state = new OpacityState(); element.SetBinding(UIElement.OpacityProperty, new System.Windows.Data.Binding(nameof(OpacityState.Value)) { Source = state });
        AnimationClockLifetime.Begin(element, UIElement.OpacityProperty, new DoubleAnimation(.6, 1, TimeSpan.FromMilliseconds(30)));
        PumpUntilState(() => !element.HasAnimatedProperties); state.Value = .4; PumpAnimations(20);
        Check(BindingOperations.IsDataBound(element, UIElement.OpacityProperty) && element.Opacity == .4, "动画完成保留绑定，后续业务状态仍能驱动控件");
        host.RootVisual = null;
    }
}
