using L = AlphaIsland.PluginSdk.Localization.UiText;
using System;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Services;
using Point = System.Windows.Point;

namespace alpha_island;

public partial class MainWindow
{
    private void MediaSourcesButton_Click(object sender, RoutedEventArgs e)
    {
        var media = _smtc;
        var menu = new ContextMenu();
        var auto = new MenuItem { Header = L.T("自动选择最近播放的媒体"), IsCheckable = true, IsChecked = media?.SelectedSessionId == null };
        auto.Click += (_, _) => SelectMediaSource(null); menu.Items.Add(auto); menu.Items.Add(new Separator());
        if (media == null || media.Sessions.Count == 0)
            menu.Items.Add(new MenuItem { Header = L.T("没有可控制的系统媒体会话"), IsEnabled = false });
        else
            foreach (var session in media.Sessions)
            {
                string state = session.State == PlaybackState.Playing ? L.T("正在播放") : session.State == PlaybackState.Paused ? L.T("已暂停") : L.T("未播放");
                var item = new MenuItem { Header = $"{session.SourceName} · {(session.Title.Length > 0 ? session.Title : "媒体会话")} · {state}",
                    IsCheckable = true, IsChecked = media.ActiveSessionId == session.Id,
                    IsEnabled = session.State is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Changing };
                string id = session.Id; item.Click += (_, _) => SelectMediaSource(id); menu.Items.Add(item);
            }
        menu.PlacementTarget = MediaSourcesButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        MediaSourcesButton.ContextMenu = menu; menu.IsOpen = true; e.Handled = true;
    }
    private void SelectMediaSource(string? id)
    {
        _smtc?.SelectSession(id);
        _componentSelectedTick = Environment.TickCount64;
        _componentCardAwaitingPointer = _state == Core.IslandState.Card;
        _hoverOutTicks = 0;
    }
    private bool IsPointOnMediaSourcesMenu(int x, int y)
    {
        var menu = MediaSourcesButton.ContextMenu;
        if (menu?.IsOpen != true || menu.ActualWidth <= 0 || menu.ActualHeight <= 0) return false;
        try
        {
            var top = menu.PointToScreen(new Point());
            var bottom = menu.PointToScreen(new Point(menu.ActualWidth, menu.ActualHeight));
            return new Rect(top, bottom).Contains(new Point(x, y));
        }
        catch (InvalidOperationException) { return false; }
    }
}
