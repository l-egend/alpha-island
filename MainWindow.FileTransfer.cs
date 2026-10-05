using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;
using alpha_island.Settings;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;

// File transfer station.
// Moved verbatim out of the former single-file MainWindow.xaml.cs; no logic changes.
namespace alpha_island
{
    public partial class MainWindow
    {
        // ================= 文件中转站 =================

        private void OnFileTransferItemsChanged(object? sender, EventArgs e)
        {
            SyncFileTransferContent();
        }

        private void OnFileTransferAllFilesEmptied(object? sender, EventArgs e)
        {
            if (_state == IslandState.Tray)
            {
                if (HasVisibleMusicPin)
                {
                    SetState(IslandState.Pill);
                }
                else
                {
                    CollapseToBar();
                }
            }
        }

        private void SyncFileTransferContent()
        {
            if (_fileTransfer == null) return;
            bool hasFiles = _fileTransfer.Count > 0;
            DropZoneGrid.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
            FileListGrid.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool _fileDropZoneArmed;
        private bool _fileDragActive;
        private bool _exportingTransferItem;
        private System.Windows.IDataObject? _fileDragData;
        private string[] _fileDragPaths = Array.Empty<string>();
        private IslandState? _fileDragRestoreState;
        private string? _fileDragRestorePage;
        private string? _fileDragRestorePinnedPage;
        private DispatcherTimer? _fileDragLeaveTimer;

        private void Window_DragEnter(object sender, DragEventArgs e) => UpdateIncomingFileDrag(e);
        private void Window_DragOver(object sender, DragEventArgs e) => UpdateIncomingFileDrag(e);
        private void Window_DragLeave(object sender, DragEventArgs e) => ScheduleFileDragCancellation();
        private void Window_Drop(object sender, DragEventArgs e) => CompleteIncomingFileDrop(e);

        private bool UpdateIncomingFileDrag(DragEventArgs e, bool? isCut = null)
        {
            e.Handled = true;
            e.Effects = DragDropEffects.None;
            if (_exiting || !_options.FileTransferEnabled || _exportingTransferItem
                || _approvalCurrent != null || ComponentSwitchGestureActive) return false;

            var effect = FileDropPayload.StagingEffect(e.AllowedEffects);
            if (effect == DragDropEffects.None) return false;
            if (!ReferenceEquals(e.Data, _fileDragData))
            {
                if (!FileDropPayload.TryRead(e.Data, out var files)) return false;
                _fileDragData = e.Data;
                _fileDragPaths = files;
            }
            if (_fileDragPaths.Length == 0) return false;

            _fileDragLeaveTimer?.Stop();
            if (!_fileDragActive)
            {
                _fileDragActive = true;
                if (_state != IslandState.Tray)
                {
                    _fileDragRestoreState = _state;
                    _fileDragRestorePage = _pages.ActiveId;
                    _fileDragRestorePinnedPage = _pinnedCardPage;
                    SetState(IslandState.Tray);
                }
                StartFileTransferService();
            }
            e.Effects = effect;
            ApplyDropZoneHighlight(isCut ?? IsCutDropPosition(e));
            return true;
        }

        private bool IsCutDropPosition(DragEventArgs e)
            => e.GetPosition(IslandBody).X >= IslandBody.ActualWidth / 2;

        private void CompleteIncomingFileDrop(DragEventArgs e, bool? isCut = null)
        {
            if (!UpdateIncomingFileDrag(e, isCut)) return;
            // 松手时再核对一次：拖拽途中被删除的文件不能留成失效条目。
            if (!FileDropPayload.TryRead(e.Data, out var files))
            {
                e.Effects = DragDropEffects.None;
                EndIncomingFileDrag(restore: true);
                return;
            }
            _fileTransfer!.AddFiles(files, isCut ?? IsCutDropPosition(e));
            EndIncomingFileDrag(restore: false);
        }

        private void ScheduleFileDragCancellation()
        {
            if (!_fileDragActive) return;
            _fileDragLeaveTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(180) };
            _fileDragLeaveTimer.Stop();
            _fileDragLeaveTimer.Tick -= FileDragLeaveTimer_Tick;
            _fileDragLeaveTimer.Tick += FileDragLeaveTimer_Tick;
            _fileDragLeaveTimer.Start();
        }

        private void FileDragLeaveTimer_Tick(object? sender, EventArgs e)
        {
            EndIncomingFileDrag(restore: true);
        }

        private void EndIncomingFileDrag(bool restore)
        {
            _fileDragLeaveTimer?.Stop();
            var previous = _fileDragRestoreState;
            string? page = _fileDragRestorePage;
            string? pinnedPage = _fileDragRestorePinnedPage;
            _fileDragActive = false;
            _fileDragData = null;
            _fileDragPaths = Array.Empty<string>();
            _fileDragRestoreState = null;
            _fileDragRestorePage = _fileDragRestorePinnedPage = null;
            SetFileDropZoneArmed(false);
            ResetDropZoneHighlight();
            if (restore && previous.HasValue && _state == IslandState.Tray)
            {
                SetState(previous.Value, preferredPage: page);
                if (previous.Value == IslandState.Card) _pinnedCardPage = pinnedPage;
            }
        }

        private void SetFileDropZoneArmed(bool armed)
        {
            _fileDropZoneArmed = armed;
            TopDropZone.IsHitTestVisible = armed;
            // 非零 alpha 让透明分层窗口在这块小区域接收系统 OLE 命中；平时恢复完全穿透。
            TopDropZone.Background = armed ? new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) : null;
        }

        private bool IsPointOnFileDropZone(int x, int y)
            => _fileDropZoneArmed && IsPointNearFileDropZone(x, y);

        private bool IsPointNearFileDropZone(int x, int y)
        {
            try
            {
                var topLeft = TopDropZone.PointToScreen(new Point());
                var bottomRight = TopDropZone.PointToScreen(new Point(TopDropZone.ActualWidth, TopDropZone.ActualHeight));
                return new Rect(topLeft, bottomRight).Contains(new Point(x, y));
            }
            catch (InvalidOperationException) { return false; }
        }

        private void TransferItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(this);
            if (sender is FrameworkElement elem && elem.DataContext is TransferItem item)
            {
                _dragSourceItem = item;
            }
        }

        private void TransferItem_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _dragSourceItem != null)
            {
                Point current = e.GetPosition(this);
                Vector diff = _dragStartPoint - current;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    var item = _dragSourceItem;
                    _dragSourceItem = null;

                    var dataObject = new DataObject(DataFormats.FileDrop, new string[] { item.FullPath });
                    var effects = item.IsCut ? DragDropEffects.Move : DragDropEffects.Copy;
                    using var preferredEffect = new System.IO.MemoryStream(BitConverter.GetBytes((int)effects));
                    dataObject.SetData("Preferred DropEffect", preferredEffect);

                    DragDropEffects applied;
                    _exportingTransferItem = true;
                    try { applied = DragDrop.DoDragDrop((DependencyObject)sender, dataObject, effects); }
                    finally { _exportingTransferItem = false; }

                    // 只有真正投放到目标（效果不为 None）才算“已转出”。
                    // 原实现无条件 RemoveItem：用户中途按 Esc 取消、或把条目丢在空白处时，
                    // 文件虽然没被复制/移动，条目却已经从中转站消失（数据丢失）。
                    if (applied != DragDropEffects.None)
                    {
                        _fileTransfer?.RemoveItem(item);
                    }
                }
            }
        }

        // ---- 拖拽载荷靠近 / 意图识别响应 ----
        private void OnMouseHookDragMove(object? sender, (int X, int Y, int StartX, int StartY) pt)
        {
            if (_exiting) return;
            if (_isDraggingVolumeSlider || ComponentSwitchGestureActive) return;

            // 若正在岛体上手势上滑，通过全局坐标快速响应收起，绝不误触文件中转站
            if (_isSwiping)
            {
                double deltaY = _swipeStartScreenPoint.Y - pt.Y;
                if (deltaY > 12)
                {
                    _isSwiping = false;
                    _dragStartedInsideIsland = false;
                    _isPinned = false;
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                    {
                        HidePinBubble(immediate: true);
                        if (IslandBody.IsMouseCaptured) IslandBody.ReleaseMouseCapture();
                        if (_clipboardLocked)
                        {
                            _clipboard?.DismissCopiedPending();
                        }
                        CollapseToBar();
                    });
                    return;
                }
            }

            if (_dragStartedInsideIsland || _isSwiping || _dragSourceItem != null) return;

            // 鼠标移动仅临时启用岛附近的 OLE 接收区，不改变岛形态。
            // 是否展开完全由 DragEnter/DragOver 中的真实文件载荷决定。
            long dx = (long)pt.X - pt.StartX;
            long dy = (long)pt.Y - pt.StartY;
            bool armed = _options.FileTransferEnabled && !_exportingTransferItem
                && dx * dx + dy * dy >= 35 * 35 && IsPointNearFileDropZone(pt.X, pt.Y);
            if (armed != _fileDropZoneArmed) SetFileDropZoneArmed(armed);
        }

        private void OnMouseHookDragEnd(object? sender, EventArgs e)
        {
            _dragStartedInsideIsland = false;
            _isSwiping = false;

            // OLE Drop 在按钮松开后投递。先留一个短窗口，避免提前拆掉接收区而丢失投放。
            if (_fileDragActive) ScheduleFileDragCancellation();
            else SetFileDropZoneArmed(false);
        }

        // ---- 分区拖放事件 ----
        private void DropZoneCopy_DragEnter(object sender, DragEventArgs e)
        {
            UpdateIncomingFileDrag(e, isCut: false);
        }

        private void DropZoneCopy_DragOver(object sender, DragEventArgs e)
        {
            UpdateIncomingFileDrag(e, isCut: false);
        }

        private void DropZoneCopy_DragLeave(object sender, DragEventArgs e)
        {
            ScheduleFileDragCancellation();
        }

        private void DropZoneCopy_Drop(object sender, DragEventArgs e)
        {
            CompleteIncomingFileDrop(e, isCut: false);
        }

        private void DropZoneCut_DragEnter(object sender, DragEventArgs e)
        {
            UpdateIncomingFileDrag(e, isCut: true);
        }

        private void DropZoneCut_DragOver(object sender, DragEventArgs e)
        {
            UpdateIncomingFileDrag(e, isCut: true);
        }

        private void DropZoneCut_DragLeave(object sender, DragEventArgs e)
        {
            ScheduleFileDragCancellation();
        }

        private void DropZoneCut_Drop(object sender, DragEventArgs e)
        {
            CompleteIncomingFileDrop(e, isCut: true);
        }

        private void TransferItem_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.DataContext is TransferItem item)
            {
                _fileTransfer?.RemoveItem(item);
                e.Handled = true;
            }
        }

        private void CloseFileTransferButton_Click(object sender, RoutedEventArgs e)
        {
            if (HasVisibleMusicPin)
            {
                SetState(IslandState.Pill);
            }
            else
            {
                CollapseToBar();
            }
        }

        private void ClearFileTransferButton_Click(object sender, RoutedEventArgs e)
        {
            _fileTransfer?.Clear();
            if (HasVisibleMusicPin)
            {
                SetState(IslandState.Pill);
            }
            else
            {
                CollapseToBar();
            }
        }

    }
}
