using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using AlphaIsland.PluginSdk;

namespace AlphaIsland.Plugins.ApiChat;

public partial class ApiChatView
{
    private StackPanel? _aiWaitingDots;
    private bool _aiWaitingForReply;
    private bool _aiWaitingAnimating;
    private bool AiFeedbackAllowed => !_context.ReadConfiguration("ReduceEffectsEnabled", false)
        && SystemParameters.ClientAreaAnimation;

    private void InitializeAiFeedback()
    {
        Loaded += (_, _) => SyncAiWaitingMotion();
        Unloaded += (_, _) => StopAiWaitingClocks();
        IsVisibleChanged += (_, _) => SyncAiWaitingMotion();
    }

    private void BeginAiReply()
    {
        EndAiWaiting();
        _aiLiveBubble = AddAiBubble("正在等待回复", isUser: false, animate: true);
        var bubble = (Border)_aiLiveBubble.Parent;
        bubble.Child = null;
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(_aiLiveBubble);
        _aiWaitingDots = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0), IsHitTestVisible = false
        };
        for (int i = 0; i < 3; i++)
            _aiWaitingDots.Children.Add(new Ellipse
            { Width = 4, Height = 4, Margin = new Thickness(2, 0, 2, 0), Fill = AiTextBrush, Opacity = .45 });
        Grid.SetColumn(_aiWaitingDots, 1); content.Children.Add(_aiWaitingDots); bubble.Child = content;
        _aiWaitingForReply = true;
        SyncAiWaitingMotion();
    }

    private void SyncAiWaitingMotion()
    {
        bool animate = !_disposed && _aiWaitingForReply && IsLoaded && IsVisible && AiFeedbackAllowed;
        if (!animate) { StopAiWaitingClocks(); return; }
        if (_aiWaitingAnimating || _aiWaitingDots == null) return;
        _aiWaitingAnimating = true;
        for (int i = 0; i < _aiWaitingDots.Children.Count; i++)
        {
            var pulse = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(1100), BeginTime = TimeSpan.FromMilliseconds(i * 150),
                RepeatBehavior = RepeatBehavior.Forever
            };
            pulse.KeyFrames.Add(new LinearDoubleKeyFrame(.35, KeyTime.FromPercent(0)));
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(.35), new SineEase()));
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(.35, KeyTime.FromPercent(.7), new SineEase()));
            pulse.KeyFrames.Add(new LinearDoubleKeyFrame(.35, KeyTime.FromPercent(1)));
            AnimationClockLifetime.Begin((Ellipse)_aiWaitingDots.Children[i], OpacityProperty, pulse);
        }
    }

    private void StopAiWaitingClocks()
    {
        if (_aiWaitingDots != null)
            foreach (Ellipse dot in _aiWaitingDots.Children)
            { AnimationClockLifetime.Begin(dot, OpacityProperty, null); dot.Opacity = .45; }
        _aiWaitingAnimating = false;
    }

    private void EndAiWaiting()
    {
        _aiWaitingForReply = false; StopAiWaitingClocks();
        if (_aiWaitingDots != null) _aiWaitingDots.Visibility = Visibility.Collapsed;
        _aiWaitingDots = null;
    }

    private void AnimateAiBubble(Border bubble)
    {
        if (!AiFeedbackAllowed || !IsLoaded || !IsVisible) return;
        var motion = new TranslateTransform(); bubble.RenderTransform = motion;
        var duration = TimeSpan.FromMilliseconds(160);
        AnimationClockLifetime.Once(bubble, OpacityProperty, new DoubleAnimation(0, 1, duration), 1d);
        AnimationClockLifetime.Once(motion, TranslateTransform.YProperty,
            new DoubleAnimation(5, 0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }, 0d);
    }
}
