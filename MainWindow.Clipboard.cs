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

// Clipboard notifications and right-click paste.
// Moved verbatim out of the former single-file MainWindow.xaml.cs; no logic changes.
namespace alpha_island
{
    public partial class MainWindow
    {
        // ================= 剪切板通知与右键粘贴 =================

        private void OnClipboardContentCopied(object? sender, (string Summary, bool IsCut) e)
        {
            if (_exiting || !_options.ClipboardNotifyEnabled) return;

            _clipboardLocked = true;
            _clipboardPastedUntil = DateTime.MinValue;

            ClipboardStatusTitle.Text = e.IsCut ? "已剪切" : "已复制";
            ClipboardModeBadgeText.Text = e.IsCut ? "剪切" : "文本";
            ClipboardPreviewText.Text = string.IsNullOrWhiteSpace(e.Summary) ? "剪切板内容" : e.Summary;

            var cyan = (System.Windows.Media.Brush)FindResource("AccentCyan");
            ClipboardIcon.Foreground = cyan;
            ClipboardIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0xF0, 0xFF));
            ClipboardIconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xF0, 0xFF));

            // 与其它通知共用接管主岛的入口；只要尚未粘贴就常驻，不借用音乐置顶状态。
            ShowPillNotification(IslandNotification.While("clipboard", () => _clipboardLocked,
                deferredInCard: false) with { Title = ClipboardStatusTitle.Text, Detail = ClipboardPreviewText.Text });
        }

        private void OnClipboardContentPasted(object? sender, EventArgs e)
        {
            if (_exiting || !_options.ClipboardNotifyEnabled) return;

            _clipboardLocked = false;
            _clipboardPastedUntil = DateTime.UtcNow.AddSeconds(_options.ClipboardAutoCollapseSeconds);

            ClipboardStatusTitle.Text = "已粘贴";
            ClipboardModeBadgeText.Text = "完成";
            if (string.IsNullOrWhiteSpace(ClipboardPreviewText.Text))
            {
                ClipboardPreviewText.Text = "已成功粘贴";
            }

            var green = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x88));
            ClipboardIcon.Foreground = green;
            ClipboardIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x28, 0x00, 0xFF, 0x88));
            ClipboardIconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0x00, 0xFF, 0x88));

            ShowPillNotification(IslandNotification.Seconds("clipboard", _options.ClipboardAutoCollapseSeconds,
                deferredInCard: false) with { Title = "已粘贴", Detail = ClipboardPreviewText.Text });
        }

        private void OnClipboardCopiedDismissed(object? sender, EventArgs e)
        {
            _clipboardLocked = false;
            _clipboardPastedUntil = DateTime.MinValue;
            _layers.Hide("clipboard");
            if (_state == IslandState.Pill && !HasVisibleMusicPin && !_layers.AnyPinned && !HasActiveNotification
                && !MusicIslandCanShow)
            {
                CollapseToBar();
            }
            else
            {
                SyncClipboardContent();
            }
        }

        private bool OnShouldInterceptRightClick(int x, int y)
        {
            if (!CanUseClipboardQuickPaste()) return false;

            if (IsPointInsideIsland(x, y)) return false;

            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                _clipboard?.ExecuteRightClickPaste(x, y);
            });

            return true;
        }

        private bool CanUseClipboardQuickPaste()
            => _options.ClipboardRightClickPaste && _clipboardLocked && _clipboard?.IsCopiedPending == true;

        private void SyncClipboardContent()
        {
            if (!ClipboardNotifyActive)
            {
                _layers.Hide("clipboard");
                _clipboardLocked = false;
            }
            SyncNotificationLayers();
        }

        /// <summary>幂等。返回 false 表示剪切板服务最终没能建起来（宿主据此把服务标成降级）。</summary>
        private bool EnsureClipboardInitialized()
        {
            if (_clipboard != null) return true;
            if (!_runtimeEnabled) return false;
            try
            {
                if (_hotkey == null)
                {
                    _hotkey = new HotkeyService(Dispatcher);
                    _hotkey.Start();
                }

                _clipboard = new ClipboardService(Dispatcher, _hotkey);
                _clipboard.ContentCopied += OnClipboardContentCopied;
                _clipboard.ContentPasted += OnClipboardContentPasted;
                _clipboard.CopiedDismissed += OnClipboardCopiedDismissed;
                _clipboard.Start();

                if (_mouseHook != null)
                {
                    _mouseHook.ShouldInterceptRightClick = OnShouldInterceptRightClick;
                }
            }
            catch (Exception ex)
            {
                IslandLog.Error("Clipboard", ex);
            }

            return _clipboard != null;
        }

        private void SyncClipboardHistoryList()
        {
            EnsureClipboardInitialized();

            var history = _clipboard?.History;
            int count = history?.Count ?? 0;
            ClipboardCountBadge.Text = $"{count}条";

            if (count > 0 && history != null)
            {
                var items = new System.Collections.Generic.List<ClipboardItemViewModel>();
                foreach (var entry in history)
                {
                    items.Add(new ClipboardItemViewModel(entry));
                }
                ClipboardHistoryList.ItemsSource = items;
                ClipboardHistoryScroll.Visibility = Visibility.Visible;
                ClipboardEmptyState.Visibility = Visibility.Collapsed;
            }
            else
            {
                ClipboardHistoryList.ItemsSource = null;
                ClipboardHistoryScroll.Visibility = Visibility.Collapsed;
                ClipboardEmptyState.Visibility = Visibility.Visible;
            }
        }

        private void ClipboardHistoryItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 如果用户点击的是项内部的具体按钮（例如“粘贴”按钮或“删除”按钮），忽略外层整行点击
            if (e.OriginalSource is DependencyObject dep)
            {
                var parent = dep;
                while (parent != null && parent != sender)
                {
                    if (parent is System.Windows.Controls.Button)
                    {
                        return; // 留给对应按钮的 Click 事件处理
                    }
                    parent = VisualTreeHelper.GetParent(parent);
                }
            }

            if (sender is FrameworkElement elem && elem.DataContext is ClipboardItemViewModel vm)
            {
                e.Handled = true;
                ExecuteClipboardItemPaste(vm.Entry.Text);
            }
        }

        private void PasteClipboardItem_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement elem && elem.DataContext is ClipboardItemViewModel vm)
            {
                ExecuteClipboardItemPaste(vm.Entry.Text);
            }
        }

        private void ExecuteClipboardItemPaste(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                if (_clipboard == null) return;
                _clipboard.SetTextForPaste(text);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Clipboard] SetText 异常: " + ex.Message);
                return;
            }

            _clipboard?.NotifyPasted();
            SetState(IslandState.Pill);

            // 关键：把输入焦点交还给原本正在打字/工作的宿主软件窗口，90~120ms 后模拟 Ctrl+V
            IntPtr targetHwnd = _lastExternalForegroundHwnd;
            if (targetHwnd != IntPtr.Zero)
            {
                NativeMethods.SetForegroundWindow(targetHwnd);
            }

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            timer.Tick += (s, args) =>
            {
                timer.Stop();
                if (targetHwnd != IntPtr.Zero)
                {
                    NativeMethods.SetForegroundWindow(targetHwnd);
                }
                HotkeyService.SimulatePaste();
            };
            timer.Start();
        }

        private void DeleteClipboardItem_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement elem && elem.DataContext is ClipboardItemViewModel vm)
            {
                _clipboard?.RemoveHistory(vm.Entry);
                SyncClipboardHistoryList();
            }
        }

        private void ClearClipboardButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            _clipboard?.ClearHistory();
            SyncClipboardHistoryList();
        }

    }
}
