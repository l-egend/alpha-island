using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Interop;
using alpha_island.Interop;
using alpha_island.Core;
using WpfClipboard = System.Windows.Clipboard;

namespace alpha_island.Services
{
    /// <summary>剪切板历史单条记录。</summary>
    public sealed record ClipboardEntry(string Text, DateTime Timestamp);

    /// <summary>
    /// 全局剪切板监听与历史管理服务。
    ///  1. 监听系统剪切板变化，序号巡检补偿遗漏消息；热键只补充剪切/粘贴意图；
    ///  2. 弹出"已复制"状态，支持持久驻留；
    ///  3. 在已复制驻留状态下，鼠标右键可替换为自动粘贴动作；
    ///  4. 粘贴后触发"已粘贴"提示；
    ///  5. 按用户设置保留文本历史（排除文件类），持久化存储。
    /// </summary>
    public sealed class ClipboardService : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly HotkeyService _hotkeyService;
        private readonly Func<uint> _getSequenceNumber;
        private readonly Func<string?> _readClipboard;
        public const int DefaultHistoryLimit = 10, MaximumHistoryLimit = 100;
        private readonly Action<string> _writeClipboard;
        private int _historyLimit = DefaultHistoryLimit;
        private readonly string _historyPath;
        private readonly object _lock = new();

        private readonly List<ClipboardEntry> _history = new();
        private bool _disposed;
        private bool _isCopiedPending;
        private DateTime _lastCopyTime = DateTime.MinValue;
        private HwndSource? _messageSource;
        private DispatcherTimer? _readTimer;
        private IslandTimer? _sequenceTimer;
        private bool _started;
        private bool _listenerRegistered;
        private bool _pendingCut;
        private int _readAttempts;
        private bool _copyHintIsCut;
        private uint _observedSequence;
        private uint _completedSequence;
        private uint _readSequence;
        private bool _readPending;
        private int _stateVersion;
        private DateTime _retryAfterUtc;

        /// <summary>当前是否有尚未粘贴/取消的已复制驻留状态。</summary>
        public bool IsCopiedPending => _isCopiedPending;
        public Uri? CurrentWebLink { get; private set; }

        internal bool TryGetPendingWebLink(out Uri? link)
        {
            link = null;
            if (_disposed || !_isCopiedPending || _readPending || CurrentWebLink == null) return false;
            uint sequence = _getSequenceNumber();
            if (sequence != 0 && sequence != _completedSequence) return false;
            link = CurrentWebLink;
            return true;
        }

        /// <summary>剪切板历史记录列表（按 HistoryLimit 保留）。</summary>
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
            : this(dispatcher, hotkeyService, NativeMethods.GetClipboardSequenceNumber, ClipboardTextReader.ReadText) { }

        // 隔离验证可注入剪切板快照与序号，不读取或写入用户真实剪切板。
        internal ClipboardService(Dispatcher dispatcher, HotkeyService hotkeyService,
            Func<uint> getSequenceNumber, Func<System.Windows.IDataObject?> readClipboard, string? historyPath = null)
            : this(dispatcher, hotkeyService, getSequenceNumber, () => ExtractText(readClipboard()), historyPath) { }

        internal ClipboardService(Dispatcher dispatcher, HotkeyService hotkeyService,
            Func<uint> getSequenceNumber, Func<string?> readClipboard, string? historyPath = null, Action<string>? writeClipboard = null)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _hotkeyService = hotkeyService ?? throw new ArgumentNullException(nameof(hotkeyService));
            _getSequenceNumber = getSequenceNumber ?? throw new ArgumentNullException(nameof(getSequenceNumber));
            _readClipboard = readClipboard ?? throw new ArgumentNullException(nameof(readClipboard));
            _historyPath = historyPath ?? AppPaths.ClipboardHistoryFile;
            _writeClipboard = writeClipboard ?? (text => WpfClipboard.SetText(text));
        }

        public void Start()
        {
            if (_disposed || _started) return;
            _started = true;
            // 启动时只记基线，避免把早已存在的剪切板内容当作新复制。
            _observedSequence = _completedSequence = _getSequenceNumber();
            LoadHistory();
            _hotkeyService.KeyPressed += OnKeyPressed;
            try
            {
                _messageSource = new HwndSource(new HwndSourceParameters("Alpha Island Clipboard")
                {
                    ParentWindow = new IntPtr(-3), // HWND_MESSAGE：不占桌面、任务栏或焦点。
                    WindowStyle = 0
                });
                _messageSource.AddHook(ClipboardWndProc);
                _listenerRegistered = NativeMethods.AddClipboardFormatListener(_messageSource.Handle);
                if (!_listenerRegistered) IslandLog.Warn("Clipboard", "系统剪切板监听未注册，使用剪切板变化序号巡检");
            }
            catch (Exception ex) { IslandLog.Warn("Clipboard", "注册系统监听失败: " + ex.Message); }
            _sequenceTimer = new IslandTimer(_dispatcher, "clipboard-sequence")
            { Interval = TimeSpan.FromMilliseconds(250) };
            _sequenceTimer.Tick += (_, _) => CheckClipboardSequence();
            _sequenceTimer.Start();
        }

        private IntPtr ClipboardWndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == NativeMethods.WM_CLIPBOARDUPDATE && !_disposed)
            {
                if (_getSequenceNumber() == 0) ScheduleCheckClipboard(IsRecentCut(), 0);
                else CheckClipboardSequence();
            }
            return IntPtr.Zero;
        }

        private void OnKeyPressed(object? sender, Interop.KeyboardHookEventArgs e)
        {
            if (_disposed || !e.IsKeyDown) return;

            // 按键不代表复制成功，只有剪切板真正新增内容才读取并弹出。
            if ((e.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (e.Key == Key.C)
                {
                    _copyHintIsCut = false;
                    _lastCopyTime = DateTime.UtcNow;
                }
                else if (e.Key == Key.X)
                {
                    _copyHintIsCut = true;
                    _lastCopyTime = DateTime.UtcNow;
                }
                else if (e.Key == Key.V)
                {
                    CompletePasteForSequence(_getSequenceNumber());
                }
            }
            else if (e.Key == Key.Insert && (e.Modifiers & ModifierKeys.Shift) != 0)
                CompletePasteForSequence(_getSequenceNumber());
        }

        private bool IsRecentCut() => _copyHintIsCut && DateTime.UtcNow - _lastCopyTime < TimeSpan.FromSeconds(1);

        internal void CheckClipboardSequence()
        {
            if (_disposed || DateTime.UtcNow < _retryAfterUtc) return;
            uint sequence = _getSequenceNumber();
            if (sequence == 0 || sequence == _observedSequence) return;
            _observedSequence = sequence;
            ScheduleCheckClipboard(IsRecentCut(), sequence);
        }

        /// <summary>
        /// 延迟读取剪切板，确保产生复制事件的目标应用程序已将数据写入系统剪切板。
        /// </summary>
        private void ScheduleCheckClipboard(bool isCut, uint sequence)
        {
            _pendingCut = isCut;
            _readSequence = sequence;
            _readPending = true;
            _readAttempts = 0;
            _readTimer ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(40)
            };
            _readTimer.Stop();
            _readTimer.Tick -= ReadTimerTick;
            _readTimer.Tick += ReadTimerTick;
            _readTimer.Start();
        }

        private void ReadTimerTick(object? sender, EventArgs e)
        {
            _readTimer?.Stop();
            if (!_disposed && _readPending) ReadClipboardContent(_pendingCut);
        }

        private void ReadClipboardContent(bool isCut)
        {
            try
            {
                uint expectedSequence = _readSequence;
                int version = _stateVersion;
                // 同一次打开剪切板内判断格式并复制文本；只保留托管字符串，不保留 OLE 对象。
                string? text = _readClipboard();
                uint currentSequence = _getSequenceNumber();
                if (!_readPending || version != _stateVersion) return;
                if (expectedSequence != 0 && currentSequence != 0 && currentSequence != expectedSequence)
                {
                    // 读取期间又复制了一次：丢弃旧快照，下一次只处理新载荷。
                    CheckClipboardSequence();
                    return;
                }
                ProcessClipboardText(text, isCut);
                _completedSequence = expectedSequence;
                if (_readSequence == expectedSequence) _readPending = false;
            }
            catch (Exception ex)
            {
                if (ex is System.Runtime.InteropServices.ExternalException && ++_readAttempts <= 5)
                {
                    _readTimer?.Start();
                    return;
                }
                _readPending = false;
                _observedSequence = _completedSequence;
                _retryAfterUtc = DateTime.UtcNow.AddSeconds(1);
                Debug.WriteLine("[ClipboardService] 读取剪切板异常: " + ex.Message);
            }
        }

        internal void ProcessClipboardData(System.Windows.IDataObject? data, bool isCut)
            => ProcessClipboardText(ExtractText(data), isCut);

        private static string? ExtractText(System.Windows.IDataObject? data)
        {
            if (data != null && !IsFileClipboardData(data))
            {
                foreach (string format in new[] { System.Windows.DataFormats.UnicodeText, System.Windows.DataFormats.Text,
                    System.Windows.DataFormats.StringFormat })
                {
                    if (data.GetDataPresent(format) && data.GetData(format) is string value)
                        return value;
                }
            }
            return null;
        }

        private void ProcessClipboardText(string? text, bool isCut)
        {
            _stateVersion++;
            CurrentWebLink = WebLinkDetector.Detect(text);
            if (string.IsNullOrWhiteSpace(text))
            {
                // 文件/图片取代文本后取消旧的文本驻留与右键代粘贴，不能误贴新的文件载荷。
                bool wasPending = _isCopiedPending;
                _isCopiedPending = false;
                if (wasPending) CopiedDismissed?.Invoke(this, EventArgs.Empty);
                return;
            }
            // 相同文字被重新复制也是新的操作；重复系统消息由序号过滤，不按文本吞掉。
            _isCopiedPending = true;
            AddHistory(text);
            string summary = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (summary.Length > 28) summary = summary.Substring(0, 28) + "...";
            ContentCopied?.Invoke(this, (summary, isCut));
        }

        private static bool IsFileClipboardData(System.Windows.IDataObject data)
            => data.GetDataPresent(System.Windows.DataFormats.FileDrop, autoConvert: false)
                || data.GetDataPresent("FileGroupDescriptorW", autoConvert: false)
                || data.GetDataPresent("FileGroupDescriptor", autoConvert: false)
                || data.GetDataPresent("FileContents", autoConvert: false);

        private void AddHistory(string text)
        {
            lock (_lock)
            {
                // 移除历史中一模一样的旧项
                _history.RemoveAll(x => x.Text == text);

                // 插入头部
                _history.Insert(0, new ClipboardEntry(text, DateTime.Now));

                // Keep the newest entries within the configured retention limit.
                while (_history.Count > _historyLimit)
                {
                    _history.RemoveAt(_history.Count - 1);
                }
            }

            SaveHistory();
        }

        public int HistoryLimit { get { lock (_lock) return _historyLimit; } }
        public void SetHistoryLimit(int limit)
        {
            bool trimmed;
            lock (_lock)
            {
                _historyLimit = Math.Clamp(limit, 0, MaximumHistoryLimit);
                trimmed = _history.Count > _historyLimit;
                if (trimmed) _history.RemoveRange(_historyLimit, _history.Count - _historyLimit);
            }
            // A smaller retention limit also removes the old, larger backup.
            if (trimmed) SaveHistory(keepBackup: false);
        }
        /// <summary>清空所有剪切板历史记录。</summary>
        public void ClearHistory()
        {
            lock (_lock)
            {
                _history.Clear();
            }
            SaveHistory(keepBackup: false);
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
            CurrentWebLink = null;
            int version = ++_stateVersion;
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (!_disposed && version == _stateVersion && !_isCopiedPending)
                    ContentPasted?.Invoke(this, EventArgs.Empty);
            });
        }

        internal void CompletePasteForSequence(uint sequence)
        {
            bool hadCopy = _isCopiedPending || _readPending || (_started && sequence != 0 && sequence != _observedSequence);
            if (!hadCopy) return;
            AcceptOwnClipboardWrite(sequence);
            NotifyPasted();
        }

        /// <summary>Copy a history item without changing focus or injecting paste keys.</summary>
        public void CopyFromHistory(string text)
        {
            if (_disposed || string.IsNullOrWhiteSpace(text)) return;
            _writeClipboard(text);
            AcceptOwnClipboardWrite(_getSequenceNumber());
            ProcessClipboardText(text, isCut: false);
        }

        internal void AcceptOwnClipboardWrite(uint sequence)
        {
            _observedSequence = _completedSequence = sequence;
            _readPending = false;
            _readTimer?.Stop();
            _retryAfterUtc = DateTime.MinValue;
        }

        /// <summary>用户向上滑动或外部手动收回已复制状态。</summary>
        public void DismissCopiedPending()
        {
            if (!_isCopiedPending) return;
            _isCopiedPending = false;
            CurrentWebLink = null;
            int version = ++_stateVersion;
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (!_disposed && version == _stateVersion && !_isCopiedPending)
                    CopiedDismissed?.Invoke(this, EventArgs.Empty);
            });
        }

        // ================= 持久化 =================

        private void LoadHistory()
        {
            foreach (string path in new[] { _historyPath, _historyPath + ".bak" })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var list = AtomicJsonFile.Read<List<ClipboardEntry>>(path);
                    if (list != null)
                    {
                        lock (_lock)
                        {
                            _history.Clear();
                            _history.AddRange(list.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Text)).Take(_historyLimit));
                        }
                        if (list.Count > HistoryLimit) SaveHistory(keepBackup: false);
                        return;
                    }
                }
                catch (Exception ex) { Debug.WriteLine("[ClipboardService] 加载剪切板历史异常: " + ex.Message); }
            }
        }

        private void SaveHistory(bool keepBackup = true)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_historyPath))!);
                string json;
                lock (_lock)
                {
                    json = JsonSerializer.Serialize(_history, new JsonSerializerOptions { WriteIndented = true });
                }
                AtomicJsonFile.Write(_historyPath, json, keepBackup);
                if (!keepBackup && File.Exists(_historyPath + ".bak")) File.Delete(_historyPath + ".bak");
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
                CurrentWebLink = null;
                _hotkeyService.KeyPressed -= OnKeyPressed;
                _readTimer?.Stop();
                _sequenceTimer?.Dispose();
                if (_messageSource != null)
                {
                    if (_listenerRegistered) NativeMethods.RemoveClipboardFormatListener(_messageSource.Handle);
                    _messageSource.RemoveHook(ClipboardWndProc);
                    _messageSource.Dispose();
                    _messageSource = null;
                }
            }
        }
    }
}
