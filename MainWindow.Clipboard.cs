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
namespace alpha_island
{
    public partial class MainWindow
    {
        // ================= 剪切板通知与右键粘贴 =================
        private bool _clipboardIsCut;
        private (Point Start, Uri Link)? _clipboardOpenPointer;
        private Action<Uri> _openClipboardWebLink = DefaultBrowserLauncher.Open;

        private bool IsClipboardLinkOpenSurface(DependencyObject? source)
            => !_exiting && _approvalCurrent == null && _state == IslandState.Pill && _activeMainIslandKey == "clipboard"
                && _clipboardLocked && SettingsRegistry.GetBool(_options, "ClipboardLinkDetectionEnabled", true)
                && _clipboard?.CurrentWebLink != null && !IsDescendantOf(source, ComponentHandle);

        private bool TryOpenClipboardWebLink(Uri expected)
        {
            if (!IsClipboardLinkOpenSurface(IslandBody) || _clipboard?.TryGetPendingWebLink(out var current) != true
                || current?.AbsoluteUri != expected.AbsoluteUri) return false;
            try
            {
                _openClipboardWebLink(current);
                _clipboard.DismissCopiedPending();
                return true;
            }
            catch (Exception ex)
            {
                ClipboardStatusTitle.Text = "打开失败，请重试";
                IslandLog.Warn("Clipboard", "默认浏览器打开链接失败：" + ex.Message);
                return true;
            }
        }

        private void UpdateClipboardLinkAppearance()
        {
            var link = SettingsRegistry.GetBool(_options, "ClipboardLinkDetectionEnabled", true) ? _clipboard?.CurrentWebLink : null;
            ClipboardStatusTitle.Text = _clipboard?.LastSensitiveKind.Length > 0 ? "敏感内容已复制" : link != null ? "点击打开链接" : _clipboardIsCut ? "已剪切" : "已复制";
            ClipboardModeBadgeText.Text = link != null ? "链接" : _clipboardIsCut ? "剪切" : "文本";
            ClipboardIcon.Text = link != null ? "\uE71B" : "\uE8C8";
            ClipboardNotifyLayer.ToolTip = link != null ? "使用默认浏览器打开\n" + link.AbsoluteUri : null;
        }

        private bool _copyingClipboardHistory;
        private void OnClipboardContentCopied(object? sender, (string Summary, bool IsCut) e)
        {
            if (_exiting || GameDoNotDisturbActive || !_options.ClipboardNotifyEnabled) return;

            _clipboardLocked = true;
            _clipboardIsCut = e.IsCut;
            _clipboardPastedUntil = DateTime.MinValue;

            UpdateClipboardLinkAppearance();
            ClipboardPreviewText.Text = string.IsNullOrWhiteSpace(e.Summary) ? "剪贴板内容" : e.Summary;

            var cyan = (System.Windows.Media.Brush)FindResource("AccentCyan");
            ClipboardIcon.Foreground = cyan;
            ClipboardIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0xF0, 0xFF));
            ClipboardIconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xF0, 0xFF));

            // 与其它通知共用接管主岛的入口；只要尚未粘贴就常驻，不借用音乐置顶状态。
            ShowPillNotification(IslandNotification.While("clipboard", () => _clipboardLocked,
                deferredInCard: _copyingClipboardHistory) with { Title = ClipboardStatusTitle.Text, Detail = ClipboardPreviewText.Text });
        }

        private void OnClipboardContentPasted(object? sender, EventArgs e)
        {
            if (_exiting || GameDoNotDisturbActive || !_options.ClipboardNotifyEnabled) return;

            _clipboardLocked = false;
            _clipboardOpenPointer = null;
            ClipboardNotifyLayer.ToolTip = null;
            _clipboardPastedUntil = DateTime.UtcNow.AddSeconds(2);

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

            ShowPillNotification(IslandNotification.Seconds("clipboard", 2,
                deferredInCard: false) with { Title = "已粘贴", Detail = ClipboardPreviewText.Text });
        }

        private void OnClipboardCopiedDismissed(object? sender, EventArgs e)
        {
            _clipboardLocked = false;
            _clipboardOpenPointer = null;
            ClipboardNotifyLayer.ToolTip = null;
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
            => !GameDoNotDisturbActive && _options.ClipboardRightClickPaste && _clipboardLocked && _clipboard?.IsCopiedPending == true;

        private void SyncClipboardContent()
        {
            if (_clipboardLocked) UpdateClipboardLinkAppearance();
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
                    _hotkey.PreviewKeyPressed += OnVolumeKeyPreview;
                    _hotkey.KeyPressed += OnVolumeKeyPressed;
                    _hotkey.Start();
                }

                _clipboard = new ClipboardService(Dispatcher, _hotkey);
                _clipboard.SetSensitiveGuard(SettingsRegistry.GetBool(_options, "ClipboardSensitiveGuardEnabled", true));
                _clipboard.SetHistoryLimit(SettingsRegistry.GetInt(_options, "ClipboardHistoryLimit", ClipboardService.DefaultHistoryLimit));
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

        private void OnClipboardOptionsChanged()
        {
            _clipboard?.SetSensitiveGuard(SettingsRegistry.GetBool(_options, "ClipboardSensitiveGuardEnabled", true));
            if (_clipboard == null) return;
            _clipboard.SetHistoryLimit(SettingsRegistry.GetInt(_options, "ClipboardHistoryLimit", ClipboardService.DefaultHistoryLimit));
            if (_state == IslandState.Card && _pages.ActiveId == "clipboard") SyncClipboardHistoryList();
        }
        private void SyncClipboardHistoryList()
        {
            if (_clipboardRemovalInProgress) return;
            EnsureClipboardInitialized();
            _clipboard?.SetSensitiveGuard(SettingsRegistry.GetBool(_options, "ClipboardSensitiveGuardEnabled", true));


            var history = _clipboard?.History;
            int count = history?.Count ?? 0;
            ClipboardCountBadge.Text = $"{count}条";
            ClearClipboardButton.IsEnabled = count > 0;
            var snapshot = history?.ToArray() ?? Array.Empty<ClipboardEntry>();
            if (_clipboardRenderedEntries != null && _clipboardRenderedEntries.SequenceEqual(snapshot)) return;
            _clipboardRenderedEntries = snapshot;
            ClipboardHistoryHint.Text = "点击复制，再到目标位置粘贴";

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
            // 如果用户点击的是项内部的具体按钮（例如“复制”按钮或“删除”按钮），忽略外层整行点击
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
                ExecuteClipboardItemCopy(vm.Entry.Text);
            }
        }

        private void CopyClipboardItem_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement elem && elem.DataContext is ClipboardItemViewModel vm)
            {
                ExecuteClipboardItemCopy(vm.Entry.Text);
            }
        }

        private void ExecuteClipboardItemCopy(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || !EnsureClipboardInitialized()) return;
            _copyingClipboardHistory = true;
            try
            {
                _clipboard!.CopyFromHistory(text);
                SyncClipboardHistoryList();
                ShowClipboardCopyFeedback();
            }
            catch (Exception ex)
            {
                ClipboardHistoryHint.Text = "复制失败，请重试";
                IslandLog.Warn("Clipboard", "历史条目复制失败：" + ex.GetType().Name);
            }
            finally { _copyingClipboardHistory = false; }
        }
        private void DeleteClipboardItem_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement elem && elem.DataContext is ClipboardItemViewModel vm)
            {
                var row = ClipboardHistoryList.ItemContainerGenerator.ContainerFromItem(vm) as FrameworkElement;
                RemoveClipboardWithFeedback(row, () => _clipboard?.RemoveHistory(vm.Entry));
            }
        }

        private void ClearClipboardButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            RemoveClipboardWithFeedback(ClipboardHistoryScroll, () => _clipboard?.ClearHistory());
        }

    }
}
