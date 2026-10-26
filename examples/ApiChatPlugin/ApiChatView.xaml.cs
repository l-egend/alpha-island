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
using alpha_island.Conversations;
using AlphaIsland.PluginSdk;
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

// API chat presentation and request lifecycle; shared transport and storage live in ConversationCore.
namespace AlphaIsland.Plugins.ApiChat
{
    public partial class ApiChatView : UserControl
    {
        /// <summary>发给模型的历史上限（不含 system），太长既费 token 又拖慢首 token。</summary>
        private const int AiMaxContextMessages = 20;
        private const int AiMaxResidentMessages = 100, AiMaxVisibleBubbles = 40;
        private int _aiMessageOffset;

        /// <summary>流式期间的 UI 合并间隔：模型一秒能吐几十段，每段都写 TextBlock 会让界面发飘。</summary>
        private const int AiUiUpdateIntervalMs = 50;

        /// <summary>出错后细条状态点保持红色的时长，超时自动回空闲。</summary>
        private static readonly TimeSpan AiErrorHold = TimeSpan.FromSeconds(15);

        private readonly ChatService _ai = new();
        private Func<IChatSettings, ChatConfig> _aiConfigFactory = options => ChatConfig.From(options);

        /// <summary>当前会话（不含 system）。唯一真源，落盘与发请求都从这里取。</summary>
        private readonly List<ChatMessage> _aiMessages = new();
        private ChatSessionStore _chatSessions = new();
        private string _aiSessionId = ChatHistoryStore.DefaultSessionId;
        private alpha_island.Controls.ChatHistoryWindow? _chatHistoryWindow;

        private CancellationTokenSource? _aiCts;
        private int _aiGeneration;
        private DispatcherTimer? _aiErrorTimer;
        private bool _aiStreaming;
        private bool _aiLoaded;
        private Func<SensitiveTextReport, bool> _confirmSensitiveSend = sensitive => System.Windows.MessageBox.Show(
            "检测到" + sensitive.Kind + "。内容将发送到你配置的接口，是否仅本次继续？历史会保存脱敏版本。", "敏感内容提醒",
            MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) == MessageBoxResult.Yes;
        private string _aiLastUser = "";

        /// <summary>正在流式追加的那个气泡。</summary>
        private TextBlock? _aiLiveBubble;

        // ================= 页生命周期 =================

        /// <summary>页被激活（翻到这一页 / 重切同一页）时调用。</summary>
        private void SyncAiCardContent()
        {
            if (AiBubbleStack == null) return;

            if (!_aiLoaded)
            {
                _aiLoaded = true;
                _aiSessionId = _options.GetText("AiActiveSession", ChatHistoryStore.DefaultSessionId);
                try { if (!_chatSessions.Exists(_aiSessionId)) _aiSessionId = ChatHistoryStore.DefaultSessionId; }
                catch (ArgumentException) { _aiSessionId = ChatHistoryStore.DefaultSessionId; }
                LoadAiHistory();
            }

            UpdateAiStatusText();
            UpdateAiInputState();
        }

        /// <summary>设置页改了接口地址/模型后刷新头部文字。</summary>
        private void OnAiOptionsChanged()
        {
            SyncAiWaitingMotion();
            UpdateAiStatusText();
            UpdateAiInputState();
        }

        // ================= 历史 =================

        private void LoadAiHistory()
        {
            EndAiWaiting();
            _aiMessages.Clear();
            _aiMessageOffset = 0;
            AiBubbleStack.Children.Clear();
            _aiLiveBubble = null;
            _aiLastUser = "";

            try
            {
                var messages = _chatSessions.Load(_aiSessionId);
                _aiMessageOffset = Math.Max(0, messages.Count - AiMaxResidentMessages);
                _aiMessages.AddRange(messages.Skip(_aiMessageOffset));
                _aiLastUser = messages.LastOrDefault(m => m.Role == ChatMessage.RoleUser)?.Content ?? "";
                // Full records remain available in history; the island only renders the latest bubbles.
                foreach (var message in messages.TakeLast(AiMaxVisibleBubbles))
                {
                    AddAiBubble(message.Content, message.Role == ChatMessage.RoleUser);
                }
            }
            catch (Exception ex)
            {
                _context.Log("读取对话历史失败: " + ex.Message);
            }

            if (AiEmptyText != null)
            {
                AiEmptyText.Visibility = _aiMessages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            ScrollAiToEnd(force: true);
        }

        private bool SaveAiHistory()
        {
            try
            {
                _chatSessions.SaveTail(_aiSessionId, _aiMessageOffset,
                    _aiMessages.Select(m => m with { Content = SensitiveTextDetector.Inspect(m.Content).Redacted }).ToArray());
                int excess = _aiMessages.Count - AiMaxResidentMessages;
                if (excess > 0) { _aiMessages.RemoveRange(0, excess); _aiMessageOffset += excess; }
                return true;
            }
            catch (Exception ex) { _context.Log("保存对话历史失败: " + ex.Message); return false; }
        }

        // ================= 气泡 =================

        /// <summary>加一个气泡，返回里面的 TextBlock（流式追加直接写它）。</summary>
        private TextBlock AddAiBubble(string text, bool isUser, bool animate = false)
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
            if (animate) AnimateAiBubble(bubble);
            while (AiBubbleStack.Children.Count > AiMaxVisibleBubbles) AiBubbleStack.Children.RemoveAt(0);
            if (AiEmptyText != null) AiEmptyText.Visibility = Visibility.Collapsed;
            ScrollAiToEnd();
            return content;
        }

        /// <summary>把最后一条助手气泡改写成错误文案（红字），并返回它的文本。</summary>
        private void MarkAiLiveBubbleError(string reason)
        {
            EndAiWaiting();
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
            _ = SendAiPromptAsync(prompt);
        }

        // Event handlers must observe failures before the streaming lifecycle starts,
        // including settings/secret loading and local command preparation.
        private async Task SendAiPromptAsync(string prompt)
        {
            try { await SendAiAsync(prompt); }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (_disposed) return;
                string reason = SensitiveTextDetector.Inspect(ex.GetBaseException().Message).Redacted;
                AddAiBubble("未能发送，输入内容已保留。" + reason, isUser: false);
                _context.Log("发送前失败: " + reason);
            }
            finally { if (!_disposed) UpdateAiInputState(); }
        }

        private async Task SendAiAsync(string prompt, CancellationToken cancellationToken = default)
        {
            if (_aiStreaming) return;
            if (_options == null) return;
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryHandleLocalReminderAsync(prompt, cancellationToken)) return;
            if (!Ready)
            {
                UpdateAiStatusText();
                return;
            }

            var config = _aiConfigFactory(_options);
            if (!config.IsUsable)
            {
                AddAiBubble("还没配置接口或模型。打开设置 →「AI / Agent」，填写接口地址与密钥，点击“连接并获取模型”后选择模型。", isUser: false);
                UpdateAiStatusText();
                
                return;
            }

            if (_context.ReadConfiguration("AiSensitiveSendGuardEnabled", true) && SensitiveTextDetector.Inspect(prompt) is { Sensitive: true } sensitive
                && !_confirmSensitiveSend(sensitive)) return;
            cancellationToken.ThrowIfCancellationRequested();

            // 出错态在下一次提问时清掉（细条状态点不用等自动复位）。
            _activity.Reset(owner: "ai");

            bool personaPending = !_chatSessions.HasStartedApi(_aiSessionId);
            string personaSession = _aiSessionId;
            _aiLastUser = prompt;
            _aiMessages.Add(new ChatMessage(ChatMessage.RoleUser, prompt));
            AddAiBubble(SensitiveTextDetector.Inspect(prompt).Redacted, isUser: true, animate: true);
            SaveAiHistory();

            if (AiInputBox != null) AiInputBox.Text = string.Empty;

            _aiStreaming = true;
            UpdateAiInputState();
            _chatHistoryWindow?.Reload(_aiSessionId);
            if (AiStopButton != null) AiStopButton.Visibility = Visibility.Visible;
            if (AiSendButton != null) AiSendButton.IsEnabled = false;
            _activity.Set(AgentRunState.Thinking, config.Model, owner: "ai");
            UpdateAiStatusText();
            

            BeginAiReply();
            var captured = new StringBuilder();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _context.Lifetime);
            _aiCts = cts;
            int generation = ++_aiGeneration;
            int chunks = 0;
            int lastUiTick = 0;
            long startedTicks = Environment.TickCount64;

            try
            {
                var request = BuildAiRequest(config);

                _aiStateReads.Clear();
                var tools = new ApiToolDispatcher(_context, result => { if (generation == _aiGeneration) RecordAiStateRead(result); });
                var agentConfig = config with { SystemPrompt = ApiAssistantPrompt.DefaultSystemPrompt };
                string? initialPrompt = personaPending ? ApiAssistantPrompt.Compose(config.SystemPrompt) : null;
                void OnFirstRequestAccepted()
                {
                    if (generation != _aiGeneration) return;
                    try { _chatSessions.MarkApiStarted(personaSession); }
                    catch (Exception ex) { _context.Log("保存会话首次发送状态失败: " + ex.Message); }
                }
                await foreach (var agentEvent in _ai.RunAgentAsync(request, agentConfig, tools, _aiSessionId, cts.Token,
                    initialSystemPrompt: initialPrompt, onFirstRequestAccepted: personaPending ? OnFirstRequestAccepted : null))
                {
                    if (generation != _aiGeneration) break;
                    if (agentEvent.Kind != "text")
                    {
                        _activity.Set(AgentRunState.ToolWait, agentEvent.Text);
                        if (_aiLiveBubble != null && _aiWaitingForReply && !string.IsNullOrWhiteSpace(agentEvent.Text)) _aiLiveBubble.Text = agentEvent.Text;
                        continue;
                    }
                    string delta = agentEvent.Text;
                    if (generation != _aiGeneration) break;
                    captured.Append(delta);
                    if (_aiWaitingForReply && string.IsNullOrWhiteSpace(captured.ToString())) continue;
                    bool wasWaiting = _aiWaitingForReply;
                    EndAiWaiting();
                    chunks++;

                    if (_activity.State != AgentRunState.Streaming)
                    {
                        _activity.Set(AgentRunState.Streaming, config.Model, owner: "ai");
                        UpdateAiStatusText();
                    }

                    // 合并 UI 更新：每 50ms 刷一次，而不是每个 delta 都刷。
                    int now = Environment.TickCount;
                    if (wasWaiting || chunks == 1 || unchecked(now - lastUiTick) >= AiUiUpdateIntervalMs)
                    {
                        lastUiTick = now;
                        if (_aiLiveBubble != null) _aiLiveBubble.Text = captured.ToString();
                        ScrollAiToEnd();
                    }
                }

                if (generation != _aiGeneration) return;
                EndAiWaiting();
                if (_aiLiveBubble != null) _aiLiveBubble.Text = captured.ToString();

                string answer = captured.ToString();
                if (string.IsNullOrWhiteSpace(answer))
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
                _context.Log($"回答完成：{chunks} 段 / {answer.Length} 字 / {ms}ms（{config.Model}）");
                _activity.Reset(owner: "ai");
            }
            catch (OperationCanceledException)
            {
                if (generation != _aiGeneration) return;
                string partial = captured.ToString();
                EndAiWaiting();
                if (!string.IsNullOrWhiteSpace(partial))
                {
                    _aiMessages.Add(new ChatMessage(ChatMessage.RoleAssistant, partial));
                    if (_aiLiveBubble != null) _aiLiveBubble.Text = partial;
                }
                else if (_aiLiveBubble != null) _aiLiveBubble.Text = "（已停止）";
                SaveAiHistory();
                _context.Log($"用户停止生成，已保留 {partial.Length} 字");
                _activity.Reset(owner: "ai");
            }
            catch (Exception ex)
            {
                if (generation != _aiGeneration) return;
                string reason = SensitiveTextDetector.Inspect(string.IsNullOrWhiteSpace(_ai.LastError) ? ex.Message : _ai.LastError).Redacted;
                if (!string.IsNullOrEmpty(_ai.LastError)) AiConnectionState.Invalidate(_options);
                _context.Refresh();
                MarkAiLiveBubbleError("⚠ " + reason);
                SaveAiHistory();
                _context.Log("对话失败: " + reason);
                _activity.Set(AgentRunState.Error, TruncateAi(reason, 24), owner: "ai");
                ScheduleAiErrorReset();
            }
            finally
            {
                for (int i = 0; i < _aiMessages.Count; i++) _aiMessages[i] = _aiMessages[i] with { Content = SensitiveTextDetector.Inspect(_aiMessages[i].Content).Redacted };
                cts.Dispose();
                if (generation == _aiGeneration)
                {
                    _aiStreaming = false;
                    EndAiWaiting();
                    _aiLiveBubble = null;
                    _aiCts = null;

                    if (AiStopButton != null) AiStopButton.Visibility = Visibility.Collapsed;
                    if (AiSendButton != null) AiSendButton.IsEnabled = true;

                    UpdateAiStatusText();
                    UpdateAiInputState();
                    _chatHistoryWindow?.Reload(_aiSessionId);
                    
                    ScrollAiToEnd();
                }
            }
        }

        private List<ChatMessage> BuildAiRequest(ChatConfig config)
        {
            var list = new List<ChatMessage>();

            list.AddRange(_aiMessages.Where(message => !message.LocalOnly).TakeLast(AiMaxContextMessages));

            return list;
        }

        private static string TruncateAi(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        private void StopAiStreaming()
        {
            if (_aiWaitingForReply && _aiLiveBubble != null) _aiLiveBubble.Text = "正在停止…";
            EndAiWaiting();
            try { _aiCts?.Cancel(); }
            catch (Exception ex) { _context.Log("停止生成失败: " + ex.Message); }
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
            _activity.Reset(owner: "ai");
            UpdateAiStatusText();
        }

        // ================= 头部按钮 =================

        private void AiStopButton_Click(object sender, RoutedEventArgs e) => StopAiStreaming();

        private void AiClearButton_Click(object sender, RoutedEventArgs e)
        {
            try { _chatSessions.Save(_aiSessionId, Array.Empty<ChatMessage>(), keepBackup: false); }
            catch (Exception ex)
            {
                _context.Log("清空对话历史失败: " + ex.Message);
                AiStatusText.Text = "清空失败，记录已保留；请检查数据目录权限";
                return;
            }
            ++_aiGeneration;
            if (_aiStreaming) StopAiStreaming();
            _aiStreaming = false;
            _aiCts = null;
            _aiErrorTimer?.Stop();
            if (AiStopButton != null) AiStopButton.Visibility = Visibility.Collapsed;

            _aiMessages.Clear();
            _aiMessageOffset = 0;
            _aiLastUser = "";
            _aiLoaded = true;          // 清空就是新的起点，别再被 LoadAiHistory 覆盖回来
            EndAiWaiting();
            AiBubbleStack?.Children.Clear();
            _aiLiveBubble = null;
            if (AiEmptyText != null) AiEmptyText.Visibility = Visibility.Visible;

            _activity.Reset(owner: "ai");
            UpdateAiStatusText();
            UpdateAiInputState();
            
            _chatHistoryWindow?.Reload(_aiSessionId);
            _context.Log("对话已清空");
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
                _context.Log($"已复制回复（{text.Length} 字）");
            }
            catch (Exception ex)
            {
                _context.Log("复制失败: " + ex.Message);
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

            _ = SendAiPromptAsync(prompt);
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
                Window.GetWindow(this)?.Activate();
                if (AiInputBox != null)
                {
                    AiInputBox.Focus();
                    Keyboard.Focus(AiInputBox);
                }
            }
            catch (Exception ex)
            {
                _context.Log("输入框抢焦点失败: " + ex.Message);
            }
        }

        private void AiInputBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            UpdateAiInputState();
            
        }

        private void AiInputBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            UpdateAiInputState();
            
        }

        private void AiInputBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateAiInputState();

        private void UpdateAiInputState()
        {
            bool hasText = !string.IsNullOrEmpty(AiInputBox?.Text);
            if (AiInputHint != null)
            {
                AiInputHint.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
                AiInputHint.Text = _aiStreaming ? "等待回复中，可先输入下一条…" : "输入消息…（Enter 发送）";
            }
            if (AiSendButton != null) AiSendButton.IsEnabled = hasText && !_aiStreaming && (Ready || IsLocalReminderInput(AiInputBox?.Text ?? ""));
            if (AiNewButton != null) AiNewButton.IsEnabled = !_aiStreaming;
        }

        // ================= 头部状态 =================

        private void UpdateAiStatusText()
        {
            if (AiStatusText == null) return;
            SyncModelAvatar();
            if (AiEnableButton != null) AiEnableButton.Visibility = !_aiStreaming && !Ready
                ? Visibility.Visible : Visibility.Collapsed;

            string glyph = "\uE99A";
            string text;

            if (_aiStreaming)
            {
                glyph = "\uE895";
                text = _activity.Describe();
            }
            else if (_activity.State == AgentRunState.Error)
            {
                glyph = "\uE783";
                text = _activity.Describe();
            }
            else if (_options != null)
            {
                if (!Ready)
                {
                    AiStatusText.Text = "AI 对话未连接 · 请先连接接口";
                    if (AiEnableButton != null) AiEnableButton.Visibility = Visibility.Visible;
                    return;
                }
                if (AiEnableButton != null) AiEnableButton.Visibility = Visibility.Collapsed;
                // Displaying a status does not need to decrypt the API credential.
                var config = ChatConfig.From(_options, includeKey: false);
                if (!config.IsUsable)
                {
                    text = "未配置 —— 设置 → AI / Agent";
                }
                else
                {
                    text = AiSecretStore.Exists
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
