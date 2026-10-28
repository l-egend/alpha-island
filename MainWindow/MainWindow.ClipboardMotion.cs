using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Services;

namespace alpha_island;

public partial class MainWindow
{
    private bool _clipboardRemovalInProgress;
    private ClipboardEntry[]? _clipboardRenderedEntries;

    private void RemoveClipboardWithFeedback(FrameworkElement? view, Action remove)
    {
        if (_clipboardRemovalInProgress) return;
        _clipboardRemovalInProgress = true;
        ClipboardHistoryList.IsHitTestVisible = false;
        ClearClipboardButton.IsEnabled = false;
        try { remove(); }
        catch
        {
            _clipboardRemovalInProgress = false; ClipboardHistoryList.IsHitTestVisible = true;
            SyncClipboardHistoryList(); throw;
        }
        void Finish()
        {
            _clipboardRemovalInProgress = false; ClipboardHistoryList.IsHitTestVisible = true;
            SyncClipboardHistoryList();
        }
        // Persist immediately; only the old presentation waits for its short dismissal.
        // New clipboard arrivals during the animation are picked up by the final sync.
        if (view == null || _state != Core.IslandState.Card || CardCarouselHost.Opacity <= .01)
            Finish();
        else NotificationDismissMotion.Play(view, Finish);
    }

    private void ShowClipboardCopyFeedback()
    {
        ClipboardHistoryHint.Text = "已复制，可到目标位置粘贴";
        if (!ClipboardHistoryHint.IsVisible || MotionPolicy.InstantMorph || MotionPolicy.PowerSave) return;
        AlphaIsland.PluginSdk.AnimationClockLifetime.Once(ClipboardHistoryHint, UIElement.OpacityProperty,
            new DoubleAnimation(.4, 1, TimeSpan.FromMilliseconds(160)), 1d);
    }
}
