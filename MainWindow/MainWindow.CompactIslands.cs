using System;
using System.Windows;
using alpha_island.Core;

namespace alpha_island;

public partial class MainWindow
{
    private Services.AgentAppNavigationService _agentNavigation = new();
    private void UpdateCompactIslandContent(string key)
    {
        SideBubble.Width = key switch { "netspeed" => StatusSatelliteWidth, "status" => ResourceSatelliteWidth, "charging" or ChargingPreviewIslandKey => 100, _ when AgentHasDefaultAvatar(key) => 176, _ => 144 };
        SideBubble.Height = 40; SideBubble.CornerRadius = new CornerRadius(20);
        SideBubbleArtwork.Source = key == "music" ? _smtc?.Current?.Artwork : null;
        SideBubbleArtwork.Visibility = Visibility.Collapsed;
        SideBubbleIcon.Visibility = Visibility.Visible;
        SideBubbleDetail.FontSize = key == "card/focus" ? 18 : 9;
        SideBubbleDetail.Text = key switch
        {
            "card/focus" => _pomodoro.DisplayRemaining(),
            "music" => _smtc?.Current?.DisplayTitle ?? "",
            "clipboard" => ClipboardPreviewText.Text,
            "volume" => CardVolumePercentText.Text,
            "download" => DownloadNotifyDetail.Text,
            "system" => string.Join(" · ", new[] { _layers.Peek(key)?.Value, _layers.Peek(key)?.Detail }
                .Where(text => !string.IsNullOrWhiteSpace(text))),
            _ => _cardPinViews.TryGetValue(key, out var view) ? view.Detail.Text : _layers.Peek(key)?.Detail ?? ""
        };
        if (key == "system") SideBubble.ToolTip = SysNotifyTitle.Text + "\n" + SideBubbleDetail.Text;
        SideBubbleGeneric.Visibility = key is "netspeed" or "status" or "charging" or ChargingPreviewIslandKey ? Visibility.Collapsed : Visibility.Visible;
        SideBubbleCustomContent.Visibility = key is "netspeed" or "status" or "charging" or ChargingPreviewIslandKey ? Visibility.Visible : Visibility.Collapsed;
        if (key == "status")
        {
            _sideStatusView ??= new Controls.SystemStatusPill(compact: true);
            SideBubbleCustomContent.Content = _sideStatusView; UpdateResourceStatus(_sideStatusView);
        }
        else if (key is ChargingIslandKey or ChargingPreviewIslandKey)
        {
            _sideChargingView ??= new Controls.BatteryChargePill(compact: true);
            SideBubbleCustomContent.Content = _sideChargingView; SetChargingIslandContent(_sideChargingView, key);
        }
        else if (key == "netspeed")
        {
            _sideNetworkView ??= new Controls.NetworkSpeedPill(compact: true) { Height = 40 };
            SideBubbleCustomContent.Content = _sideNetworkView;
            UpdateCombinedStatus(_sideNetworkView, CurrentNetworkSample);
        }
    }
    private void SideBubble_PreviewMouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (GameDoNotDisturbActive || VoiceIslandVisible || _sideBubbleComponentKey is not { } key) return;
        if (key == "music") SetMusicPinned(!_isPinned, preserveCard: true);
        else SetLayerPinned(key, !_layers.IsPinned(key), promoteToMain: false, preserveCard: true);
    }

    private void UpdateCompactIslandPinState()
    {
        if (_sideBubbleComponentKey is not { } key) return;
        bool pinned = key == "music" ? _isPinned : _layers.IsPinned(key);
        SideBubble.BorderBrush = (System.Windows.Media.Brush)FindResource(pinned ? "AccentCyan" : "GlassEdgeBrush");
        string title = key == "system" ? SysNotifyTitle.Text + "\n" + SideBubbleDetail.Text : GetSatelliteDisplayName(key);
        SideBubble.ToolTip = $"{title}（{(pinned ? "已独立置顶 · " : "")}左键展开，右键切换置顶）";
    }

    // External agent status/approvals have their own pill, independent of the AI chat page.
    private bool TryOpenAgentIsland(string? key)
    {
        if (key != ApprovalLayerKey && !IsAgentIslandKey(key)) return false;
        if (IsAgentIslandKey(key) && !SettingsRegistry.GetBool(_options, "AgentStatusEnabled", true)) return true;
        if (_approvalCurrent != null) PresentCurrentApproval();
        else if (AgentSession(key) is { } view && view.Owner.StartsWith("external:", StringComparison.Ordinal))
        {
            _agentWindowKey = view.Key;
            string message;
            if (_agentNavigation.TryResolve(view.Owner, out var target, out message)) _ = OpenAgentWindowAsync(target!);
            view.Root.ToolTip = string.IsNullOrEmpty(message) ? AgentLabel(view.Key) + " · 打开对应助手" : message;
        }
        else if (AgentSession(key) is { } plugin && _plugins?.PageForActivity(plugin.Owner) is { } activityPage)
            SelectComponent(activityPage);
        else if (key != null && _layers.IsActive(key))
        { UpdateAgentStatusUi(); SwitchMainIsland(key); SetState(IslandState.Pill); }
        return true;
    }
    private bool IsPointOnCompactIsland(int x, int y)
    {
        if (SideBubble.Visibility != Visibility.Visible || SideBubble.Opacity <= .1 || SideBubble.ActualWidth <= 0) return false;
        try
        {
            var top = SideBubble.PointToScreen(new System.Windows.Point());
            var bottom = SideBubble.PointToScreen(new System.Windows.Point(SideBubble.ActualWidth, SideBubble.ActualHeight));
            return new Rect(top, bottom).Contains(new System.Windows.Point(x, y));
        }
        catch (InvalidOperationException) { return false; }
    }
}
