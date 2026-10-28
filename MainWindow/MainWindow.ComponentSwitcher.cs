using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using Point = System.Windows.Point;

namespace alpha_island;

public partial class MainWindow
{
    private DispatcherTimer? _componentHoldTimer, _componentPointerTimer;
    private Popup? _componentPopup;
    private ComponentRadialMenu? _componentMenu;
    private bool _componentPressActive, _componentMenuVisible;
    private bool _componentMenuLeft, _componentMenuUp;
    private Point _componentPressScreen;
    private string? _componentHoveredId;
    private bool _componentKeyboardNavigation;
    private bool _componentCardAwaitingPointer;
    private long _componentSelectedTick;
    private Point _componentKeyboardPointer;
    private IntPtr _componentMenuForegroundWindow;
    private readonly WheelAccumulator _componentWheel = new();
    private bool ComponentSwitchGestureActive => _componentPressActive || _componentMenuVisible;
    private bool CanUseComponentSwitcher => !_exiting && !GameDoNotDisturbActive && !_sessionLocked && _approvalCurrent == null && _pages.Count > 1;

    private void InitializeComponentSwitcher()
    {
        ComponentHandle.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (CanUseComponentSwitcher) BeginComponentHold(ComponentHandle.PointToScreen(e.GetPosition(ComponentHandle)), _runtimeEnabled);
        };
        ComponentHandle.MouseMove += (_, e) =>
        {
            if (!_componentPressActive) return;
            if (_componentMenuVisible) PollComponentPointer();
            else if ((ComponentHandle.PointToScreen(e.GetPosition(ComponentHandle)) - _componentPressScreen).Length
                > 22 * VisualTreeHelper.GetDpi(ComponentHandle).DpiScaleX) CloseComponentSwitcher();
        };
        ComponentHandle.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_componentMenuVisible)
            {
                _componentPressActive = false;
                PollComponentPointer(commitImmediately: true);
                if (ComponentHandle.IsMouseCaptured) ComponentHandle.ReleaseMouseCapture();
                return;
            }
            bool next = _componentPressActive && !_componentMenuVisible && ComponentHandle.ContainsHandlePoint(e.GetPosition(ComponentHandle));
            CloseComponentSwitcher();
            if (next) StepComponent(1);
        };
        ComponentHandle.LostMouseCapture += (_, _) =>
        {
            // A Popup creates a separate HWND and can take capture during its first layout.
            // Once visible, pointer polling and outside clicks own the menu lifetime.
            if (_componentPressActive && !_componentMenuVisible) CloseComponentSwitcher();
        };
        ComponentHandle.PreviewMouseWheel += (_, e) => { if (!_componentMenuVisible) TryWheelSwitchComponent(e.Delta); e.Handled = true; };
        ComponentHandle.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CloseComponentSwitcher(); e.Handled = true; }
            else if (e.Key is Key.Enter or Key.Space && CanUseComponentSwitcher)
            {
                if (_componentMenuVisible) SelectComponent(_componentHoveredId ?? CurrentComponentId());
                else { OpenComponentMenu(); BeginComponentKeyboardNavigation(); }
                e.Handled = true;
            }
            else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down && CanUseComponentSwitcher)
            {
                int delta = e.Key is Key.Left or Key.Up ? -1 : 1;
                if (_componentMenuVisible && _componentMenu != null)
                {
                    BeginComponentKeyboardNavigation();
                    int index = _pages.IndexOf(_componentHoveredId ?? CurrentComponentId());
                    _componentHoveredId = _pages.Pages[(index + delta + _pages.Count) % _pages.Count].Id;
                    _componentMenu.Highlight(_componentHoveredId);
                }
                else StepComponent(delta);
                e.Handled = true;
            }
        };
        _pages.PagesChanged += () => { CloseComponentSwitcher(); SyncComponentHandle(); };
        Deactivated += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_componentMenuVisible)
            {
                IntPtr foreground = Interop.NativeMethods.GetForegroundWindow();
                IntPtr menuWindow = (PresentationSource.FromVisual(_componentMenu) as HwndSource)?.Handle ?? IntPtr.Zero;
                if (foreground == _componentMenuForegroundWindow || foreground == menuWindow
                    || foreground == (PresentationSource.FromVisual(ComponentHandle) as HwndSource)?.Handle) return;
            }
            CloseComponentSwitcher();
        }), DispatcherPriority.Background);
        _componentHoldTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(400) };
        _componentHoldTimer.Tick += (_, _) =>
        {
            _componentHoldTimer.Stop();
            if (_componentPressActive && CanUseComponentSwitcher) OpenComponentMenu(_runtimeEnabled);
            else CloseComponentSwitcher();
        };
        _componentPointerTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        _componentPointerTimer.Tick += (_, _) => PollComponentPointer();
        SyncComponentHandle();
        DiagnosePagerRect();
    }

    private void BeginComponentHold(Point screenPoint, bool capture = true)
    {
        if (!CanUseComponentSwitcher) return;
        CloseComponentSwitcher();
        _componentPressScreen = screenPoint; _componentPressActive = true;
        _isSwiping = false; _dragStartedInsideIsland = false; _hoverOutTicks = 0;
        ComponentHandle.Engaged = true;
        if (capture)
        {
            Keyboard.Focus(ComponentHandle);
            if (!ComponentHandle.CaptureMouse()) { CloseComponentSwitcher(); return; }
        }
        _componentHoldTimer?.Start();
    }

    private void OpenComponentMenu(bool showPopup = true)
    {
        if (!CanUseComponentSwitcher) { CloseComponentSwitcher(); return; }
        _componentHoldTimer?.Stop();
        _componentMenu ??= new ComponentRadialMenu(IslandBody.Background, ResolveAccentBrush());
        _componentMenu.Clicked -= SelectComponent; _componentMenu.Clicked += SelectComponent;
        _componentMenu.SetPages(_pages.Pages, CurrentComponentId(), IslandBody.Background);
        _componentMenu.ArrangeArc(false, false, ComponentHandle.ActualHeight, ComponentHandle.ContourCornerRadius.TopRight);
        _componentHoveredId = null; _componentMenuVisible = true;
        _componentMenuForegroundWindow = Interop.NativeMethods.GetForegroundWindow();
        ComponentHandle.Engaged = true; _hoverOutTicks = 0;
        if (!showPopup) { _componentMenu.Reveal(false); return; }
        _componentPopup ??= new Popup { AllowsTransparency = true, StaysOpen = true, Placement = PlacementMode.Custom,
            PlacementTarget = ComponentHandle, Child = _componentMenu };
        _componentPopup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
            new[] { new CustomPopupPlacement(new Point(IslandContourHandle.ContourGap
                + (_componentMenuLeft ? -ComponentMenuLayout.HandleOffset : ComponentMenuLayout.HandleOffset) - _componentMenu.Layout.Anchor.X,
                targetSize.Height / 2 - _componentMenu.Layout.Anchor.Y), PopupPrimaryAxis.None) };
        UpdateComponentMenuPlacement();
        _componentMenu.Reveal();
        _componentPopup.IsOpen = true;
        if (_runtimeEnabled) Keyboard.Focus(ComponentHandle);
        AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(_componentMenu,UIElement.OpacityProperty, null); _componentMenu.Opacity = 1;
        _componentPointerTimer?.Start();
    }

    private void UpdateComponentMenuPlacement()
    {
        if (_componentPopup == null || _componentMenu == null || !ComponentHandle.IsVisible) return;
        Point anchor = ComponentHandle.PointToScreen(new Point(IslandContourHandle.ContourGap, ComponentHandle.ActualHeight / 2));
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)anchor.X, (int)anchor.Y)).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(ComponentHandle);
        var neutral = ComponentMenuLayout.Create(_pages.Count, contourHeight: ComponentHandle.ActualHeight,
            cornerRadius: ComponentHandle.ContourCornerRadius.TopRight);
        double width = neutral.Size.Width - neutral.Anchor.X;
        _componentMenuLeft = area.Right - anchor.X < (width + ComponentMenuLayout.HandleOffset + 8) * dpi.DpiScaleX && anchor.X - area.Left > area.Right - anchor.X;
        double above = Math.Max(0, (anchor.Y - area.Top) / dpi.DpiScaleY);
        double below = Math.Max(0, (area.Bottom - anchor.Y) / dpi.DpiScaleY);
        double bias = _runtimeEnabled ? Math.Max(0, neutral.Anchor.Y - above) : 0;
        if (_runtimeEnabled && neutral.Size.Height - neutral.Anchor.Y + bias > below)
            bias = Math.Min(0, below - (neutral.Size.Height - neutral.Anchor.Y));
        _componentMenuUp = bias < 0;
        _componentMenu.ArrangeArc(_componentMenuLeft, _componentMenuUp, ComponentHandle.ActualHeight,
            ComponentHandle.ContourCornerRadius.TopRight, bias);
        // Popup placement is cached by WPF; changing an offset refreshes it during live island morphs.
        double offset = _componentPopup.HorizontalOffset;
        _componentPopup.HorizontalOffset = offset + .01; _componentPopup.HorizontalOffset = offset;
    }

    private void PollComponentPointer(bool commitImmediately = false)
    {
        if (!_componentMenuVisible || _componentMenu == null) return;
        if (!CanUseComponentSwitcher) { CloseComponentSwitcher(); return; }
        if (_componentPopup?.IsOpen == true) UpdateComponentMenuPlacement();
        if (Interop.NativeMethods.TryGetCursorPos(out var cursor))
        {
            if (_componentKeyboardNavigation)
            {
                if ((new Point(cursor.X, cursor.Y) - _componentKeyboardPointer).Length < 2) return;
                _componentKeyboardNavigation = false;
            }
            if (IsPointOnComponentHandle(cursor.X, cursor.Y))
            {
                UpdateComponentHover(_componentMenu.Layout.Anchor, commitImmediately);
                return;
            }
            if (PresentationSource.FromVisual(_componentMenu) == null || !_componentMenu.IsArrangeValid) return;
            UpdateComponentHover(_componentMenu.PointFromScreen(new Point(cursor.X, cursor.Y)), commitImmediately);
        }
    }

    private void BeginComponentKeyboardNavigation()
    {
        _componentKeyboardNavigation = true;
        if (Interop.NativeMethods.TryGetCursorPos(out var cursor)) _componentKeyboardPointer = new Point(cursor.X, cursor.Y);
    }

    private void UpdateComponentHover(Point menuPoint, bool commitImmediately = false)
    {
        if (!_componentMenuVisible || _componentMenu == null) return;
        if (!_componentMenu.Layout.ContainsGesturePoint(menuPoint)) { CloseComponentSwitcher(); return; }
        string? id = _componentMenu.ItemAt(menuPoint);
        if (id != _componentHoveredId)
        {
            _componentHoveredId = id;
            _componentMenu.Highlight(id);
        }
        // Hover previews; releasing over an icon commits. Releasing at the handle keeps
        // the menu open so a long press also works as a click-to-open menu.
        if (commitImmediately)
        {
            if (id != null) SelectComponent(id);
        }
    }

    private void CloseComponentSwitcher()
    {
        _componentHoldTimer?.Stop(); _componentPointerTimer?.Stop();
        _componentPressActive = false; _componentMenuVisible = false; _componentHoveredId = null;
        _componentKeyboardNavigation = false;
        if (_componentPopup != null) _componentPopup.IsOpen = false;
        if (ComponentHandle != null)
        {
            ComponentHandle.Engaged = false;
            if (ComponentHandle.IsMouseCaptured) ComponentHandle.ReleaseMouseCapture();
        }
        _hoverOutTicks = 0;
    }

    private string CurrentComponentId() => _state == IslandState.Card ? _pages.ActiveId : _activeMainIslandKey switch
    {
        "music" => IslandPageIds.Music, "netspeed" => IslandPageIds.NetSpeed,
        "status" or "charging" or "battery" or ChargingPreviewIslandKey => IslandPageIds.System, "system" => _systemNotifyTargetPage,
        "volume" => IslandPageIds.Volume, "download" => IslandPageIds.Download, "clipboard" => IslandPageIds.Clipboard,
        DesktopToastKey => IslandPageIds.Notifications, "approval" => IslandPageIds.Ai, _ when IsAgentIslandKey(_activeMainIslandKey) => AgentPage(_activeMainIslandKey),
        _ => CardPageFromPinKey(_activeMainIslandKey) ?? _pages.ActiveId
    };

    private void SelectComponent(string id)
    {
        int index = _pages.IndexOf(id);
        if (index < 0 || !CanUseComponentSwitcher) { CloseComponentSwitcher(); return; }
        CloseComponentSwitcher();
        if (_state == IslandState.Card) SwitchCardPage(index);
        // The contour menu replaces card pagination for every page, including pages with a compact notification layer.
        else SetState(IslandState.Card, preferredPage: id);
        _componentCardAwaitingPointer = true;
        _componentSelectedTick = Environment.TickCount64;
        _hoverOutTicks = 0;
    }

    private bool HoldSelectedComponentCard(bool cursorInside)
    {
        if (_state != IslandState.Card || !_componentCardAwaitingPointer) return false;
        long elapsed = Environment.TickCount64 - _componentSelectedTick;
        if (elapsed < 450 || !cursorInside) return true;
        _componentCardAwaitingPointer = false;
        return false;
    }

    private void StepComponent(int delta)
    {
        if (!CanUseComponentSwitcher || delta == 0) return;
        int index = Math.Max(0, _pages.IndexOf(CurrentComponentId()));
        SelectComponent(_pages.Pages[(index + Math.Sign(delta) + _pages.Count) % _pages.Count].Id);
    }

    private bool TryWheelSwitchComponent(int delta)
    {
        if (!CanUseComponentSwitcher) return true;
        int steps = _componentWheel.Consume(delta);
        if (steps != 0) StepComponent(-Math.Sign(steps));
        return true;
    }

    private void SyncComponentHandle()
    {
        if (ComponentHandle == null) return;
        ComponentHandle.Visibility = _state == IslandState.Bar || _pages.Count < 2 ? Visibility.Collapsed : Visibility.Visible;
        ComponentHandle.Stroke = CurrentComponentId() == IslandPageIds.Music ? System.Windows.Media.Brushes.White : ResolveAccentBrush();
        if (_componentMenuVisible && _componentPopup?.IsOpen == true) UpdateComponentMenuPlacement();
    }

    private bool IsPointOnComponentHandle(int screenX, int screenY)
    {
        if (ComponentHandle == null || !ComponentHandle.IsVisible || ComponentHandle.ActualHeight <= 0) return false;
        try { return ComponentHandle.ContainsHandlePoint(ComponentHandle.PointFromScreen(new Point(screenX, screenY))); }
        catch (InvalidOperationException) { return false; }
    }

    private bool HandleComponentMenuGlobalClick(int x, int y)
    {
        if (!_componentMenuVisible || _componentMenu == null) return false;
        if (_componentPopup?.IsOpen == true)
        {
            var position = _componentMenu.PointFromScreen(new Point(x, y));
            if (new Rect(_componentMenu.RenderSize).Contains(position)) return true;
        }
        CloseComponentSwitcher();
        return true;
    }
}
