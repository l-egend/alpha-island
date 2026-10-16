using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using RadioButton = System.Windows.Controls.RadioButton;
using Point = System.Windows.Point;
using System.Windows.Media;
using System.Windows.Media.Animation;
using alpha_island.Animation;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    private FrameworkElement? _selectedSettingsPage;
    private RadioButton? _selectedNavItem;
    private bool _navIndicatorPositioned;
    private int _navMotionVersion;
    private int _pageMotionVersion;
    private int _selectionMotionVersion;
    private bool AnimateSettings => !MotionPolicy.InstantMorph && !MotionPolicy.PowerSave && SystemParameters.ClientAreaAnimation;
    private void InitializeSettingsMotion()
    {
        MotionPolicy.Changed += FinishSettingsMotion;
        Loaded += (_, _) => UpdateNavigationSelection(NavPanel.Children.OfType<RadioButton>().FirstOrDefault(i => i.IsChecked == true), false);
        NavItemsSurface.SizeChanged += (_, _) => UpdateNavigationSelection(_selectedNavItem, false);
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) StopSettingsMotion();
            else Dispatcher.BeginInvoke(new Action(() => UpdateNavigationSelection(_selectedNavItem
                ?? NavPanel.Children.OfType<RadioButton>().FirstOrDefault(i => i.IsChecked == true), false)));
        };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) StopSettingsMotion(); };
        Closed += (_, _) => { StopSettingsMotion(); MotionPolicy.Changed -= FinishSettingsMotion; UnbindPluginHost(); };
    }
    private static void ResetPageMotion(FrameworkElement page)
    {
        AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(page,OpacityProperty, null); page.Opacity = 1;
        if (page.RenderTransform is TranslateTransform transform)
        { AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(transform,TranslateTransform.YProperty, null); transform.Y = 0; }
    }
    private void TransitionSettingsPage(FrameworkElement target, IEnumerable<(string Tag, FrameworkElement? Page)> pages)
    {
        int version = ++_pageMotionVersion;
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
        if (!target.IsLoaded || !target.IsVisible || !AnimateSettings || _suppress) return;
        var transform = new TranslateTransform(); target.RenderTransform = transform;
        var duration = TimeSpan.FromMilliseconds(180);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0.2, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => { if (version == _pageMotionVersion) ResetPageMotion(target); };
        AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(target,OpacityProperty, fade);
        AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(transform,TranslateTransform.YProperty, new DoubleAnimation(8, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }
    private void UpdateNavigationSelection(RadioButton? item, bool animate)
    {
        _selectedNavItem = item;
        if (NavSelectionIndicator == null || NavItemsSurface == null) return;
        int version = ++_selectionMotionVersion;
        var transform = (TranslateTransform)NavSelectionIndicator.RenderTransform;
        double from = transform.Y;
        AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(transform,TranslateTransform.YProperty, null);
        if (item == null || !item.IsVisible || item.ActualHeight <= 0)
        {
            NavSelectionIndicator.Visibility = Visibility.Collapsed;
            _navIndicatorPositioned = false;
            return;
        }
        double target = item.TranslatePoint(new Point(0, (item.ActualHeight - NavSelectionIndicator.Height) / 2), NavItemsSurface).Y;
        transform.Y = target;
        NavSelectionIndicator.Visibility = Visibility.Visible;
        bool motion = animate && _navIndicatorPositioned && AnimateSettings && Math.Abs(from - target) > .1;
        _navIndicatorPositioned = true;
        if (!motion) return;
        var slide = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(200))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        slide.Completed += (_, _) => { if (version == _selectionMotionVersion) AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(transform,TranslateTransform.YProperty, null); };
        AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(transform,TranslateTransform.YProperty, slide);
    }
    private void FinishSettingsMotion()
    {
        if (AnimateSettings) return;
        StopSettingsMotion();
    }
    private void StopSettingsMotion()
    {
        ++_pageMotionVersion;
        if (_selectedSettingsPage != null) ResetPageMotion(_selectedSettingsPage);
        ++_navMotionVersion;
        if (NavPane != null) { AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(NavPane,WidthProperty, null); NavPane.Width = _navExpanded ? NavWidthExpanded : NavWidthCollapsed; }
        UpdateNavigationSelection(_selectedNavItem, false);
    }
}
