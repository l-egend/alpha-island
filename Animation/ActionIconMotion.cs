using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using alpha_island.Controls;

namespace alpha_island.Animation;

/// <summary>A brief semantic action cue. The icon's strokes transition into their final state.</summary>
public enum ActionIconAnimation { None, Pulse, Connect, Disconnect, Spin, Tick }

/// <summary>
/// Presentation and lifecycle gate for finite SemanticIcon transitions. Caller transforms remain
/// intact; no frame callbacks, timers or repeating clocks are kept while idle.
/// </summary>
public static class ActionIconMotion
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
        "Kind", typeof(ActionIconAnimation), typeof(ActionIconMotion),
        new PropertyMetadata(ActionIconAnimation.None, OnKindChanged));

    // Values may reference their element: ConditionalWeakTable's ephemeron semantics still allow
    // an otherwise unreachable element, its handlers and its state to be collected together.
    private static readonly ConditionalWeakTable<FrameworkElement, MotionState> States = new();

    static ActionIconMotion() => MotionPolicy.Changed += OnPolicyChanged;

    public static void SetKind(DependencyObject element, ActionIconAnimation kind) => element.SetValue(KindProperty, kind);
    public static ActionIconAnimation GetKind(DependencyObject element) => (ActionIconAnimation)element.GetValue(KindProperty);
    public static bool GetIsAnimating(FrameworkElement element) => States.TryGetValue(element, out var state) && state.IsAnimating;

    /// <summary>Plays once when loaded/visible. Repeated calls during the same cue do not restart it.</summary>
    public static void Play(FrameworkElement element, ActionIconAnimation? kind = null)
    {
        element.VerifyAccess();
        var cue = kind ?? GetKind(element);
        if (cue == ActionIconAnimation.None) { Stop(element); return; }
        States.GetValue(element, static owner => new MotionState(owner)).Play(cue);
    }

    /// <summary>Removes owned clocks immediately and displays the final semantic state.</summary>
    public static void Stop(FrameworkElement element)
    {
        element.VerifyAccess();
        if (States.TryGetValue(element, out var state)) state.Stop();
    }

    private static void OnKindChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not FrameworkElement element) return;
        if ((ActionIconAnimation)args.NewValue == ActionIconAnimation.None)
        {
            if (States.TryGetValue(element, out var existing)) { existing.Detach(); States.Remove(element); }
            return;
        }
        var state = States.GetValue(element, static item => new MotionState(item));
        state.Play((ActionIconAnimation)args.NewValue);
    }

    private static void OnPolicyChanged()
    {
        if (!MotionPolicy.InstantMorph && !MotionPolicy.PowerSave) return;
        foreach (var pair in States) pair.Value.Stop();
    }

    private sealed class MotionState
    {
        private static readonly DependencyPropertyDescriptor OpacityDescriptor =
            DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(UIElement));
        private readonly FrameworkElement _element;
        private readonly List<UIElement> _opacityAncestors = new();
        private bool _presented;
        private ActionIconAnimation? _pendingCue;
        private readonly List<SemanticIcon> _icons = new();
        private AnimationTimeline? _completion;
        private EventHandler? _completionHandler;
        private int _generation;
        public bool IsAnimating { get; private set; }

        public MotionState(FrameworkElement element)
        {
            _element = element;
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
            element.IsVisibleChanged += OnVisibilityChanged;
            if (element.IsLoaded) ObservePresentation();
        }

        public void Detach()
        {
            Stop();
            UnobservePresentation();
            _element.Loaded -= OnLoaded;
            _element.Unloaded -= OnUnloaded;
            _element.IsVisibleChanged -= OnVisibilityChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            ObservePresentation();
            Play(_pendingCue ?? GetKind(_element));
        }
        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            Stop(); UnobservePresentation(); _presented = false;
        }
        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if ((bool)args.NewValue) Play(_pendingCue ?? GetKind(_element)); else Stop();
        }

        // Card pages are kept Visible and hidden through their parent's opacity. IsVisible alone
        // would spend the entire cue before the user opens the page. These event subscriptions
        // cause no idle polling and are removed on unload / disabling this behavior.
        private void ObservePresentation()
        {
            UnobservePresentation();
            for (DependencyObject? current = _element; current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is not UIElement ancestor) continue;
                _opacityAncestors.Add(ancestor);
                OpacityDescriptor.AddValueChanged(ancestor, OnPresentationChanged);
            }
            _presented = IsPresented();
        }

        private void UnobservePresentation()
        {
            foreach (var ancestor in _opacityAncestors)
                OpacityDescriptor.RemoveValueChanged(ancestor, OnPresentationChanged);
            _opacityAncestors.Clear();
        }

        private bool IsPresented()
        {
            if (!_element.IsLoaded || !_element.IsVisible) return false;
            double opacity = 1;
            foreach (var ancestor in _opacityAncestors)
            {
                opacity *= ancestor.Opacity;
                if (opacity < .45) return false;
            }
            return true;
        }

        private void OnPresentationChanged(object? sender, EventArgs args)
        {
            bool presented = IsPresented();
            if (!presented)
            {
                if (_presented || IsAnimating) Stop();
                _presented = false;
            }
            else if (!_presented)
            {
                _presented = true;
                Play(_pendingCue ?? GetKind(_element));
            }
        }

        public void Play(ActionIconAnimation cue)
        {
            if (cue == ActionIconAnimation.None) return;
            if (MotionPolicy.InstantMorph || MotionPolicy.PowerSave) return;
            bool redirect = IsAnimating;
            if (redirect)
            {
                if (!_icons.Any(icon => icon.NeedsRetarget(cue))) return;
                // Preserve the currently drawn geometry as the new starting state. Stop would
                // commit the newly requested final glyph and introduce a jump on rapid toggles.
                ++_generation;
                if (_completion != null) _completion.Completed -= _completionHandler;
                _completion = null; _completionHandler = null;
            }
            _pendingCue = cue;
            if (!IsPresented()) return;
            _pendingCue = null;
            _presented = true;
            if (!redirect) CollectIcons(_element);
            if (_icons.Count == 0) return;
            IsAnimating = true;
            // Volume is continuous input: a long EaseInOut cue repeatedly restarted by
            // pointer moves never leaves its slow start. Retarget from the drawn state
            // with a short EaseOut so held dragging responds on the very next frames.
            bool volumeInput = _icons.All(icon => icon.ResolvedShape == IconShape.Volume);
            bool muteTransition = volumeInput && _icons.Any(icon => icon.VolumeStateChanging);
            bool popEntrance = _icons.All(icon => icon.UsesPopEntrance);
            var animation = new DoubleAnimation
            {
                From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(volumeInput ? (muteTransition ? 320 : 180) : popEntrance ? 700 : 1400),
                EasingFunction = volumeInput
                    ? new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    : popEntrance ? null : new CubicEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            _completion = animation;
            int generation = ++_generation;
            _completionHandler = (_, _) => { if (generation == _generation) Stop(); };
            animation.Completed += _completionHandler;
            foreach (var icon in _icons) icon.BeginTransition(cue, animation);
        }

        private void CollectIcons(DependencyObject parent)
        {
            // A parent's IsVisibleChanged is raised before descendant effective visibility is
            // propagated. Inspect local visibility in the already-presented subtree, otherwise
            // the first reveal can consume the pending cue before the child receives that event.
            if (parent is UIElement element && element.Visibility != Visibility.Visible) return;
            if (parent is SemanticIcon icon)
            {
                _icons.Add(icon);
                return;
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                CollectIcons(VisualTreeHelper.GetChild(parent, i));
        }
        public void Stop()
        {
            _pendingCue = null;
            ++_generation; // A queued completion from an old cue must not stop its replacement.
            if (_completion != null) { _completion.Completed -= _completionHandler; _completion = null; }
            _completionHandler = null;
            foreach (var icon in _icons) icon.EndTransition();
            _icons.Clear();
            IsAnimating = false;
        }
    }
}
