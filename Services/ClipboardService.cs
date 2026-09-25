using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using alpha_island.Core;
using WpfClipboard = System.Windows.Clipboard;

namespace alpha_island.Services
{
    /// <summary>剪切板历史单条记录。</summary>
    public sealed record ClipboardEntry(string Text, DateTime Timestamp);

    /// <summary>
    /// 全局剪切板监听与历史管理服务。
    ///  1. 捕获 Ctrl+C / Ctrl+X 复制与剪切操作；
    ///  2. 弹出"已复制"状态，支持持久驻留；
    ///  3. 在已复制驻留状态下，鼠标右键可替换为自动粘贴动作；
    ///  4. 粘贴后触发"已粘贴"提示；
    ///  5. 记录最近 10 条文本历史（排除文件类），持久化存储。
    /// </summary>
    public sealed class ClipboardService : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly HotkeyService _hotkeyService;
        private readonly object _lock = new();

        private readonly List<ClipboardEntry> _history = new();
        private bool _disposed;
        private bool _isCopiedPending;
        private string _lastCopiedText = string.Empty;
        private DateTime _lastCopyTime = DateTime.MinValue;

        /// <summary>当前是否有尚未粘贴/取消的已复制驻留状态。</summary>
        public bool IsCopiedPending => _isCopiedPending;

        /// <summary>剪切板历史记录列表（最多 10 条）。</summary>
        public IReadOnlyList<ClipboardEntry> History
        {
            get
            {
                lock (_lock) return _history.ToArray();
            }
        }

        /// <summary>检测到新复制内容时触发（参数：文本摘要，是否为剪切）。</summary>
        public event EventHandler<(string Summary, bool IsCut)>? ContentCopied;

        /// <summary>检测到内容粘贴时触发。</summary>
        public event EventHandler? ContentPasted;

        /// <summary>用户手动滑起或取消已复制状态时触发。</summary>
        public event EventHandler? CopiedDismissed;

        public ClipboardService(Dispatcher dispatcher, HotkeyService hotkeyService)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _hotkeyService = hotkeyService ?? throw new ArgumentNullException(nameof(hotkeyService));
        }

        public void Start()
        {
            if (_disposed) return;
            LoadHistory();
            _hotkeyService.KeyPressed += OnKeyPressed;
        }

        private void OnKeyPressed(object? sender, Interop.KeyboardHookEventArgs e)
        {
            if (_disposed || !e.IsKeyDown) return;

            // 监听 Ctrl+C / Ctrl+X (复制 / 剪切)
            if ((e.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (e.Key == Key.C)
                {
                    ScheduleCheckClipboard(isCut: false);
                }
                else if (e.Key == Key.X)
                {
                    ScheduleCheckClipboard(isCut: true);
                }
                else if (e.Key == Key.V)
                {
                    // 用户使用 Ctrl+V 主动粘贴
                    if (_isCopiedPending)
                    {
                        NotifyPasted();
                    }
                }
            }
        }

        /// <summary>
        /// 延迟读取剪切板，确保产生复制事件的目标应用程序已将数据写入系统剪切板。
        /// </summary>
        private void ScheduleCheckClipboard(bool isCut)
        {
            // 防抖
            if ((DateTime.UtcNow - _lastCopyTime).TotalMilliseconds < 250) return;
            _lastCopyTime = DateTime.UtcNow;

            var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(75)
            };
            timer.Tick += (s, args) =>
            {
                timer.Stop();
                if (!_disposed) ReadClipboardContent(isCut);
            };
            timer.Start();
        }

        private void ReadClipboardContent(bool isCut)
        {
            try
            {
                // 不包含文件类 (文件类由文件中转站专门负责)
                if (WpfClipboard.ContainsFileDropList()) return;

                if (!WpfClipboard.ContainsText()) return;

                string text = WpfClipboard.GetText();
                if (string.IsNullOrWhiteSpace(text)) return;

                // 与上一条相同且仍在 pending 则不重复刷
                if (text == _lastCopiedText && _isCopiedPending) return;

                _lastCopiedText = text;
                _isCopiedPending = true;

                // 加入历史并持久化
                AddHistory(text);

                // 提取单行摘要（最多 30 个字符）
                string summary = text.Replace("\r", " ").Replace("\n", " ").Trim();
                if (summary.Length > 28) summary = summary.Substring(0, 28) + "...";

                ContentCopied?.Invoke(this, (summary, isCut));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ClipboardService] 读取剪切板异常: " + ex.Message);
            }
        }

        private void AddHistory(string text)
        {
            lock (_lock)
            {
                // 移除历史中一模一样的旧项
                _history.RemoveAll(x => x.Text == text);

                // 插入头部
                _history.Insert(0, new ClipboardEntry(text, DateTime.Now));

                // 限制最多 10 条
                while (_history.Count > 10)
                {
                    _history.RemoveAt(_history.Count - 1);
                }
            }

            SaveHistory();
        }

        /// <summary>清空所有剪切板历史记录。</summary>
        public void ClearHistory()
        {
            lock (_lock)
            {
                _history.Clear();
            }
            SaveHistory();
        }

        /// <summary>移除指定单条剪切板历史记录。</summary>
        public void RemoveHistory(ClipboardEntry entry)
        {
            if (entry == null) return;
            lock (_lock)
            {
                _history.RemoveAll(x => x.Text == entry.Text);
            }
            SaveHistory();
        }

        /// <summary>在已复制驻留状态下，执行右键替换粘贴或完成粘贴。</summary>
        public void ExecuteRightClickPaste(int x = 0, int y = 0)
        {
            if (!_isCopiedPending) return;
            if (x > 0 || y > 0)
            {
                HotkeyService.SimulateRightClickPasteAt(x, y);
            }
            else
            {
                HotkeyService.SimulatePaste();
            }
            NotifyPasted();
        }

        /// <summary>标记已粘贴完成。</summary>
        public void NotifyPasted()
        {
            _isCopiedPending = false;
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                ContentPasted?.Invoke(this, EventArgs.Empty);
            });
        }

        /// <summary>用户向上滑动或外部手动收回已复制状态。</summary>
        public void DismissCopiedPending()
        {
            if (!_isCopiedPending) return;
            _isCopiedPending = false;
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                CopiedDismissed?.Invoke(this, EventArgs.Empty);
            });
        }

        // ================= 持久化 =================

        private void LoadHistory()
        {
            try
            {
                if (File.Exists(AppPaths.ClipboardHistoryFile))
                {
                    string json = File.ReadAllText(AppPaths.ClipboardHistoryFile);
                    var list = JsonSerializer.Deserialize<List<ClipboardEntry>>(json);
                    if (list != null)
                    {
                        lock (_lock)
                        {
                            _history.Clear();
                            _history.AddRange(list);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ClipboardService] 加载剪切板历史异常: " + ex.Message);
            }
        }

        private void SaveHistory()
        {
            try
            {
                AppPaths.EnsureRoot();
                string json;
                lock (_lock)
                {
                    json = JsonSerializer.Serialize(_history, new JsonSerializerOptions { WriteIndented = true });
                }
                File.WriteAllText(AppPaths.ClipboardHistoryFile, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ClipboardService] 保存剪切板历史异常: " + ex.Message);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _hotkeyService.KeyPressed -= OnKeyPressed;
            }
        }
    }
}
