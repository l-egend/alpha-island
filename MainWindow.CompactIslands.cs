using System;
using System.Windows;
using alpha_island.Core;

namespace alpha_island;

public partial class MainWindow
{
    private void UpdateCompactIslandContent(string key)
    {
        SideBubble.Width = key == "netspeed" ? StatusSatelliteWidth : 144;
        SideBubble.Height = 40; SideBubble.CornerRadius = new CornerRadius(20);
        SideBubbleArtwork.Source = key == "music" ? _smtc?.Current?.Artwork : null;
        SideBubbleArtwork.Visibility = SideBubbleArtwork.Source != null ? Visibility.Visible : Visibility.Collapsed;
        SideBubbleIcon.Visibility = SideBubbleArtwork.Source == null ? Visibility.Visible : Visibility.Collapsed;
        SideBubbleDetail.FontSize = key == "card/focus" ? 18 : 9;
        SideBubbleDetail.Text = key switch
        {
            "card/focus" => _pomodoro.DisplayRemaining(),
            "music" => _smtc?.Current?.DisplayTitle ?? "",
            "clipboard" => ClipboardPreviewText.Text,
            "volume" => CardVolumePercentText.Text,
            "download" => DownloadNotifyDetail.Text,
            _ => _cardPinViews.TryGetValue(key, out var view) ? view.Detail.Text : _layers.Peek(key)?.Detail ?? ""
        };
        SideBubbleGeneric.Visibility = key == "netspeed" ? Visibility.Collapsed : Visibility.Visible;
        SideBubbleCustomContent.Visibility = key == "netspeed" ? Visibility.Visible : Visibility.Collapsed;
        if (key == "netspeed")
        {
            _sideNetworkView ??= new Controls.NetworkSpeedPill(compact: true) { Height = 40 };
            SideBubbleCustomContent.Content = _sideNetworkView;
            UpdateCombinedStatus(_sideNetworkView, CurrentNetworkSample);
        }
    }
    private void SideBubble_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!TryOpenAgentIsland(_sideBubbleComponentKey)
            && _sideBubbleComponentKey is { } key && PageForCompactIsland(key) is { } page) SelectComponent(page);
        e.Handled = true;
    }

    // External agent status/approvals have their own pill, independent of the AI chat page.
    private bool TryOpenAgentIsland(string? key)
    {
        if (key != ApprovalLayerKey && key != AgentTickerKey) return false;
        if (_approvalCurrent != null) PresentCurrentApproval();
        else if (key == AgentTickerKey && (_layers.IsActive(key) || AgentStatus.IsBusy))
        {
            UpdateAgentStatusUi();
            SwitchMainIsland(key);
            SetState(IslandState.Pill);
        }
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
