using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using TextBox = System.Windows.Controls.TextBox;

// B 组②：AI 对话卡片页（第 4 页）。
// 这一层只负责「看得见的部分」——气泡、输入框、停止/重试/复制；
// 网络、SSE、密钥、历史文件全部在 Services\ChatService.cs 与 Services\AiSecretStore.cs 里。
namespace alpha_island
{
    public partial class MainWindow
    {
        /// <summary>输入态/有对话时整卡长高多少。岛体可见带 ≈ 身高 − 22，
        /// 卡片视口与岛体必须同步长（见 SetCardDrawerExtra 的注释），314 仍在窗口 340 高之内。</summary>
        private const double AiCardDrawerExtra = 96;

        /// <summary>发给模型的历史上限（不含 system），太长既费 token 又拖慢首 token。</summary>
        private const int AiMaxContextMessages = 20;

        /// <summary>流式期间的 UI 合并间隔：模型一秒能吐几十段，每段都写 TextBlock 会让界面发飘。</summary>
        private const int AiUiUpdateIntervalMs = 50;

        /// <summary>出错后细条状态点保持红色的时长，超时自动回空闲。</summary>
        private static readonly TimeSpan AiErrorHold = TimeSpan.FromSeconds(15);

        private readonly ChatService _ai = new();

        /// <summary>当前会话（不含 system）。唯一真源，落盘与发请求都从这里取。</summary>
        private readonly List<ChatMessage> _aiMessages = new();

        private CancellationTokenSource? _aiCts;
        private int _aiGeneration;
        private DispatcherTimer? _aiErrorTimer;
        private bool _aiStreaming;
        private bool _aiLoaded;
        private bool _aiStatusHooked;
        private string _aiLastUser = "";

        /// <summary>上一次写进日志的抽屉高度：只在真的变化时打一行，免得每拍都刷。</summary>
        private double _aiDrawerLogged = -1;

        /// <summary>正在流式追加的那个气泡。</summary>
        private TextBlock? _aiLiveBubble;

        // ================= 页生命周期 =================

        /// <summary>页被激活（翻到这一页 / 重切同一页）时调用。</summary>
        private void SyncAiCardContent()
        {
            if (AiBubbleStack == null) return;

            HookAiStatus();

            if (!_aiLoaded)
            {
                _aiLoaded = true;
                LoadAiHistory();
            }

            UpdateAiStatusText();
            UpdateAiInputState();
            ApplyAiDrawer();
        }

        /// <summary>AI 页当前是不是活着的那一页。</summary>
        private bool AiPageActive => _pages.ActiveId == IslandPageIds.Ai;

        /// <summary>撑高/收回卡片：只有 AI 页在台上时才动 _cardDrawerExtra（音量页也共用这一个量）。</summary>
        private void ApplyAiDrawer()
        {
            if (!AiPageActive) return;

            bool grow = _aiStreaming
                        || (AiInputBox != null && AiInputBox.IsKeyboardFocused)
                        || _aiMessages.Count > 0;

            double extra = grow ? AiCardDrawerExtra : 0;
            if (Math.Abs(extra - _aiDrawerLogged) > 0.5)
            {
                _aiDrawerLogged = extra;
                IslandLog.Debug("Ai",
                    $"卡片抽屉 extra={extra:0}（消息 {_aiMessages.Count} 条，流式={_aiStreaming}，" +
                    $"输入焦点={(AiInputBox?.IsKeyboardFocused ?? false)}）");
            }

            SetCardDrawerExtra(extra);
        }

        private void HookAiStatus()
        {
            if (_aiStatusHooked) return;
            _aiStatusHooked = true;
            // 桥接（B 组④）以后也会写这个状态，卡片头部的文字跟着它走。
            AgentStatus.Changed += (_, __) =>
            {
                try { Dispatcher.BeginInvoke(new Action(UpdateAiStatusText), DispatcherPriority.Background); }
                catch { }
            };
        }

        /// <summary>设置页改了接口地址/模型后刷新头部文字。</summary>
        private void OnAiOptionsChanged()
        {
            UpdateAiStatusText();
            UpdateAiInputState();
        }

        // ================= 历史 =================

        private void LoadAiHistory()
        {
            _aiMessages.Clear();
            AiBubbleStack.Children.Clear();
            _aiLiveBubble = null;
            _aiLastUser = "";

            try
            {
                foreach (var message in ChatHistoryStore.Load())
                {
                    if (message.Role != ChatMessage.RoleUser && message.Role != ChatMessage.RoleAssistant) continue;
                    if (string.IsNullOrWhiteSpace(message.Content)) continue;

                    _aiMessages.Add(message);
                    AddAiBubble(message.Content, message.Role == ChatMessage.RoleUser);
                    if (message.Role == ChatMessage.RoleUser) _aiLastUser = message.Content;
                }
            }
            catch (Exception ex)
            {
                IslandLog.Warn("Ai", "读取对话历史失败: " + ex.Message);
            }

            if (AiEmptyText != null)
            {
                AiEmptyText.Visibility = _aiMessages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            ScrollAiToEnd(force: true);
        }

        private void SaveAiHistory()
        {
            try { ChatHistoryStore.Save(ChatHistoryStore.DefaultSessionId, _aiMessages); }
            catch (Exception ex) { IslandLog.Warn("Ai", "保存对话历史失败: " + ex.Message); }
        }

        // ================= 气泡 =================

        /// <summary>加一个气泡，返回里面的 TextBlock（流式追加直接写它）。</summary>
        private TextBlock AddAiBubble(string text, bool isUser)
        {
            var content = new TextBlock
            {
                Text = text ?? "",
                FontSize = 11,
                LineHeight = 16,
                TextWrapping = TextWrapping.Wrap,
                Foreground = AiTextBrush
            };

            var bubble = new Border
            {
                MaxWidth = 300,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(isUser ? 40 : 0, 3, isUser ? 0 : 40, 3),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                // 用户气泡走强调青、助手气泡走中性玻璃，和岛体的语言一致。
                Background = isUser
                    ? new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0xE5, 0xFF))
                    : new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
                BorderBrush = isUser
                    ? new SolidColorBrush(Color.FromArgb(0x50, 0x00, 0xE5, 0xFF))
                    : new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
                Child = content
            };

            AiBubbleStack.Children.Add(bubble);
            if (AiEmptyText != null) AiEmptyText.Visibility = Visibility.Collapsed;
            ScrollAiToEnd();
            return content;
        }

        /// <summary>把最后一条助手气泡改写成错误文案（红字），并返回它的文本。</summary>
        private void MarkAiLiveBubbleError(string reason)
        {
            if (_aiLiveBubble == null) return;
            _aiLiveBubble.Text = reason;
            _aiLiveBubble.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x8A, 0x8A));
        }

        private Brush AiTextBrush =>
            (TryFindResource("TextPrimary") as Brush) ?? new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));

        private void ScrollAiToEnd(bool force = false)
        {
            if (AiScrollViewer == null) return;
            // 排版还没跑完时 ScrollableHeight 是旧值，所以等一拍再滚（Background 优先级排在布局之后）。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { AiScrollViewer.ScrollToEnd(); }
                catch { }
            }), force ? DispatcherPriority.Loaded : DispatcherPriority.Background);
        }

        // ================= 发送 =================

        private void AiSendButton_Click(object sender, RoutedEventArgs e) => SendAiFromInput();

        private void AiInputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                SendAiFromInput();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (_aiStreaming) StopAiStreaming();
                else Keyboard.ClearFocus();
            }
        }

        private void SendAiFromInput()
        {
            if (AiInputBox == null) return;
            string prompt = AiInputBox.Text?.Trim() ?? "";
            if (prompt.Length == 0) return;
            _ = SendAiAsync(prompt);
        }

        private async Task SendAiAsync(string prompt)
        {
            if (_aiStreaming) return;
            if (_options == null) return;

            var config = ChatConfig.From(_options);
            if (!config.IsUsable)
            {
                AddAiBubble("还没配置接口或模型。打开设置 →「AI / Agent」，填写接口地址与密钥，点击“连接并获取模型”后选择模型。", isUser: false);
                UpdateAiStatusText();
                ApplyAiDrawer();
                return;
            }

            // 出错态在下一次提问时清掉（细条状态点不用等自动复位）。
            AgentStatus.Reset(owner: "ai");

            _aiLastUser = prompt;
            _aiMessages.Add(new ChatMessage(ChatMessage.RoleUser, prompt));
            AddAiBubble(prompt, isUser: true);
            SaveAiHistory();

            if (AiInputBox != null) AiInputBox.Text = string.Empty;

            _aiStreaming = true;
            if (AiStopButton != null) AiStopButton.Visibility = Visibility.Visible;
            if (AiSendButton != null) AiSendButton.IsEnabled = false;
            AgentStatus.Set(AgentRunState.Thinking, config.Model, owner: "ai");
            UpdateAiStatusText();
            ApplyAiDrawer();

            _aiLiveBubble = AddAiBubble("", isUser: false);
            var captured = new StringBuilder();
            var cts = new CancellationTokenSource();
            _aiCts = cts;
            int generation = ++_aiGeneration;
            int chunks = 0;
            int lastUiTick = 0;
            long startedTicks = Environment.TickCount64;

            try
            {
                var request = BuildAiRequest(config);

                await foreach (string delta in _ai.StreamAsync(request, config, cts.Token))
                {
                    if (generation != _aiGeneration) break;
                    captured.Append(delta);
                    chunks++;

                    if (chunks == 1)
                    {
                        AgentStatus.Set(AgentRunState.Streaming, config.Model, owner: "ai");
                        UpdateAiStatusText();
                    }

                    // 合并 UI 更新：每 50ms 刷一次，而不是每个 delta 都刷。
                    int now = Environment.TickCount;
                    if (chunks == 1 || unchecked(now - lastUiTick) >= AiUiUpdateIntervalMs)
                    {
                        lastUiTick = now;
                        if (_aiLiveBubble != null) _aiLiveBubble.Text = captured.ToString();
                        ScrollAiToEnd();
                    }
                }

                if (generation != _aiGeneration) return;
                if (_aiLiveBubble != null) _aiLiveBubble.Text = captured.ToString();

                string answer = captured.ToString();
                if (answer.Length == 0)
                {
                    answer = "（模型没有返回内容）";
                    MarkAiLiveBubbleError(answer);
                }
                else
                {
                    _aiMessages.Add(new ChatMessage(ChatMessage.RoleAssistant, answer));
                }

                SaveAiHistory();

                long ms = Environment.TickCount64 - startedTicks;
                IslandLog.Debug("Ai", $"回答完成：{chunks} 段 / {answer.Length} 字 / {ms}ms（{config.Model}）");
                AgentStatus.Reset(owner: "ai");
            }
            catch (OperationCanceledException)
            {
                if (generation != _aiGeneration) return;
                string partial = captured.ToString();
                if (partial.Length > 0) _aiMessages.Add(new ChatMessage(ChatMessage.RoleAssistant, partial));
                else if (_aiLiveBubble != null) _aiLiveBubble.Text = "（已停止）";
                SaveAiHistory();
                IslandLog.Debug("Ai", $"用户停止生成，已保留 {partial.Length} 字");
                AgentStatus.Reset(owner: "ai");
            }
            catch (Exception ex)
            {
                if (generation != _aiGeneration) return;
                string reason = string.IsNullOrWhiteSpace(_ai.LastError) ? ex.Message : _ai.LastError;
                MarkAiLiveBubbleError("⚠ " + reason);
                SaveAiHistory();
                IslandLog.Warn("Ai", "对话失败: " + reason);
                AgentStatus.Set(AgentRunState.Error, TruncateAi(reason, 24), owner: "ai");
                ScheduleAiErrorReset();
            }
            finally
            {
                cts.Dispose();
                if (generation == _aiGeneration)
                {
                    _aiStreaming = false;
                    _aiLiveBubble = null;
                    _aiCts = null;

                    if (AiStopButton != null) AiStopButton.Visibility = Visibility.Collapsed;
                    if (AiSendButton != null) AiSendButton.IsEnabled = true;

                    UpdateAiStatusText();
                    UpdateAiInputState();
                    ApplyAiDrawer();
                    ScrollAiToEnd();
                }
            }
        }

        private List<ChatMessage> BuildAiRequest(ChatConfig config)
        {
            var list = new List<ChatMessage>();

            int from = Math.Max(0, _aiMessages.Count - AiMaxContextMessages);
            for (int i = from; i < _aiMessages.Count; i++) list.Add(_aiMessages[i]);

            return list;
        }

        private static string TruncateAi(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        private void StopAiStreaming()
        {
            try { _aiCts?.Cancel(); }
            catch (Exception ex) { IslandLog.Warn("Ai", "停止生成失败: " + ex.Message); }
        }

        private void ScheduleAiErrorReset()
        {
            _aiErrorTimer ??= new DispatcherTimer { Interval = AiErrorHold };
            _aiErrorTimer.Stop();
            _aiErrorTimer.Tick -= AiErrorTimer_Tick;
            _aiErrorTimer.Tick += AiErrorTimer_Tick;
            _aiErrorTimer.Start();
        }

        private void AiErrorTimer_Tick(object? sender, EventArgs e)
        {
            _aiErrorTimer?.Stop();
            AgentStatus.Reset(owner: "ai");
            UpdateAiStatusText();
        }

        // ================= 头部按钮 =================

        private void AiStopButton_Click(object sender, RoutedEventArgs e) => StopAiStreaming();

        private void AiClearButton_Click(object sender, RoutedEventArgs e)
        {
            ++_aiGeneration;
            if (_aiStreaming) StopAiStreaming();
            _aiStreaming = false;
            _aiCts = null;
            _aiErrorTimer?.Stop();
            if (AiStopButton != null) AiStopButton.Visibility = Visibility.Collapsed;

            _aiMessages.Clear();
            _aiLastUser = "";
            _aiLoaded = true;          // 清空就是新的起点，别再被 LoadAiHistory 覆盖回来
            AiBubbleStack?.Children.Clear();
            _aiLiveBubble = null;
            if (AiEmptyText != null) AiEmptyText.Visibility = Visibility.Visible;

            try { ChatHistoryStore.Clear(); }
            catch (Exception ex) { IslandLog.Warn("Ai", "清空对话历史失败: " + ex.Message); }

            AgentStatus.Reset(owner: "ai");
            UpdateAiStatusText();
            UpdateAiInputState();
            ApplyAiDrawer();
            IslandLog.Debug("Ai", "对话已清空");
        }

        private void AiCopyButton_Click(object sender, RoutedEventArgs e)
        {
            string text = LastAiAssistantText();
            if (text.Length == 0)
            {
                AddAiBubble("（还没有可复制的回复）", isUser: false);
                return;
            }

            try
            {
                // 全限定：UseWindowsForms 下 Clipboard 会撞名。
                System.Windows.Clipboard.SetText(text);
                IslandLog.Debug("Ai", $"已复制回复（{text.Length} 字）");
            }
            catch (Exception ex)
            {
                IslandLog.Warn("Ai", "复制失败: " + ex.Message);
            }
        }

        private void AiRetryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_aiStreaming) return;

            string prompt = _aiLastUser;
            if (prompt.Length == 0)
            {
                AddAiBubble("（没有可重试的提问）", isUser: false);
                return;
            }

            prompt = ChatHistoryOperations.RemoveLastTurn(_aiMessages);
            if (prompt.Length == 0) return;
            AiBubbleStack.Children.Clear();
            foreach (var message in _aiMessages)
                AddAiBubble(message.Content, message.Role == ChatMessage.RoleUser);
            SaveAiHistory();

            _ = SendAiAsync(prompt);
        }

        private void RemoveLastAiBubble()
        {
            if (AiBubbleStack == null) return;
            int count = AiBubbleStack.Children.Count;
            if (count == 0) return;
            AiBubbleStack.Children.RemoveAt(count - 1);
            if (count - 1 == 0 && AiEmptyText != null) AiEmptyText.Visibility = Visibility.Visible;
        }

        private string LastAiAssistantText()
        {
            for (int i = _aiMessages.Count - 1; i >= 0; i--)
            {
                if (_aiMessages[i].Role == ChatMessage.RoleAssistant) return _aiMessages[i].Content ?? "";
            }
            return "";
        }

        // ================= 输入框 =================

        private void AiInputBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 无边框 + ShowInTaskbar=False 的窗口，点进来时窗口可能还没被激活，
            // 那样键盘焦点仍在原来的前台窗口上——表现为"点了输入框打不出字"。
            try
            {
                if (!IsActive) Activate();
                if (AiInputBox != null)
                {
                    AiInputBox.Focus();
                    Keyboard.Focus(AiInputBox);
                }
            }
            catch (Exception ex)
            {
                IslandLog.Warn("Ai", "输入框抢焦点失败: " + ex.Message);
            }
        }

        private void AiInputBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            UpdateAiInputState();
            ApplyAiDrawer();
        }

        private void AiInputBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            UpdateAiInputState();
            ApplyAiDrawer();
        }

        private void AiInputBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateAiInputState();

        private void UpdateAiInputState()
        {
            bool hasText = !string.IsNullOrEmpty(AiInputBox?.Text);
            if (AiInputHint != null) AiInputHint.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
            if (AiSendButton != null) AiSendButton.IsEnabled = hasText && !_aiStreaming;
        }

        // ================= 头部状态 =================

        private void UpdateAiStatusText()
        {
            if (AiStatusText == null) return;

            string glyph = "\uE99A";
            string text;

            if (_aiStreaming)
            {
                glyph = "\uE895";
                text = AgentStatus.Describe();
            }
            else if (AgentStatus.State == AgentRunState.Error)
            {
                glyph = "\uE783";
                text = AgentStatus.Describe();
            }
            else if (_options != null)
            {
                var config = ChatConfig.From(_options);
                if (!config.IsUsable)
                {
                    text = "未配置 —— 设置 → AI / Agent";
                }
                else
                {
                    text = config.HasKey
                        ? $"{config.Model} · 密钥已保存"
                        : $"{config.Model} · 无密钥（本地模型可留空）";
                }
            }
            else
            {
                text = "就绪";
            }

            AiStatusText.Text = text;
            if (AiStatusGlyph != null) AiStatusGlyph.Text = glyph;
        }
    }
}
