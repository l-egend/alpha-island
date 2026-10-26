using System.Windows;
using System.Windows.Threading;
using alpha_island.Interop;

namespace alpha_island;

public partial class MainWindow
{
    private bool _displayReflowPending;

    private void RequestDisplayReflow()
    {
        if (_exiting || _displayReflowPending) return;
        _displayReflowPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _displayReflowPending = false;
            if (_exiting) return;
            CloseComponentSwitcher();
            PositionWindow();
            _cachedIslandScreenRect = Rect.Empty;
            PublishIslandRect(Rect.Empty);
            _glassGeneration++;
            _liquidHostBounds = Rect.Empty;
            QueueLiquidRefresh();
            _privacyIndicator?.Reposition();
            _overlayZOrder?.RequestCheck();
        }));
    }

    private void PositionWindow() => WindowWorkArea.PlacePrimaryTop(this, 1200);
}
