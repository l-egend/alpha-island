using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using alpha_island.Animation;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    private FrameworkElement? _selectedSettingsPage;
    private int _navMotionVersion;
    private bool AnimateSettings => !MotionPolicy.InstantMorph && !MotionPolicy.PowerSave;
    private void InitializeSettingsMotion()
    {
        MotionPolicy.Changed += FinishSettingsMotion;
        Closed += (_, _) => { MotionPolicy.Changed -= FinishSettingsMotion; UnbindPluginHost(); };
    }
    private static void ResetPageMotion(FrameworkElement page)
    {
        page.BeginAnimation(OpacityProperty, null); page.Opacity = 1;
        if (page.RenderTransform is TranslateTransform transform)
        { transform.BeginAnimation(TranslateTransform.YProperty, null); transform.Y = 0; }
    }
    private void TransitionSettingsPage(FrameworkElement target, IEnumerable<(string Tag, FrameworkElement? Page)> pages)
    {
        bool changed = !ReferenceEquals(target, _selectedSettingsPage);
        foreach (var (_, page) in pages)
        {
            if (page == null) continue;
            ResetPageMotion(page);
            page.Visibility = ReferenceEquals(page, target) ? Visibility.Visible : Visibility.Collapsed;
        }
        _selectedSettingsPage = target;
        if (!changed) return;
        SettingsContentScroll?.ScrollToTop();
        if (!target.IsLoaded || !AnimateSettings || _suppress) return;
        var transform = new TranslateTransform(); target.RenderTransform = transform;
        var duration = TimeSpan.FromMilliseconds(190);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        target.BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, 1, duration) { FillBehavior = FillBehavior.Stop });
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }
    private void FinishSettingsMotion()
    {
        if (AnimateSettings) return;
        if (_selectedSettingsPage != null) ResetPageMotion(_selectedSettingsPage);
        ++_navMotionVersion;
        if (NavPane != null) { NavPane.BeginAnimation(WidthProperty, null); NavPane.Width = _navExpanded ? NavWidthExpanded : NavWidthCollapsed; }
    }
}
