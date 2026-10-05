using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Core;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;

// Card page host wiring: page registration and page-change bookkeeping.
// 翻页动画本身留在 MainWindow.Cards.cs（要和凝胶弹簧抢同一批渲染帧）。
namespace alpha_island
{
    public partial class MainWindow
    {
        private readonly IslandPageHost _pages = new();

        /// <summary>
        /// 建立卡片页注册表。必须在 ApplyOptions() 之前调用 —— SetState(Card) 会按 Id 选页。
        /// 三张既有页的 View 就是原来那三个具名 Grid：XAML 结构零改动，翻页逻辑不再认识页数。
        /// </summary>
        private void BuildPageHost()
        {
            // 音乐页：歌词区使用独立高度，其他详情页保持原尺寸。
            _pages.Register(new DelegateIslandPage(
                id: IslandPageIds.Music,
                title: "媒体播放器",
                view: CardLayer,
                order: 0,
                preferredHeight: IslandMetrics.CardHeight));

            // 剪切板页 / 音量页：无论有没有歌词都要 214（老代码写的是 `_currentCardPage >= 1`）。
            _pages.Register(new DelegateIslandPage(
                id: IslandPageIds.Clipboard,
                title: "剪切板及历史",
                view: ClipboardCardLayer,
                order: 1,
                preferredHeight: IslandMetrics.CardHeightLyrics,
                activated: SyncClipboardHistoryList));

            _pages.Register(new DelegateIslandPage(
                id: IslandPageIds.Volume,
                title: "系统音量调节",
                view: VolumeCardLayer,
                order: 2,
                preferredHeight: IslandMetrics.CardHeightLyrics,
                // 方法有可选参数，转不成无参 Action，必须包一层（否则 CS1503）。
                activated: () => SyncVolumeCardContent(),
                viewportChanged: SetVolumeCardLayerHeight));

            // AI 对话页（B 组②）：只有设置里开了「AI 对话」才会出现在组件菜单里
            // （可用性由 Sync(options) 每次 ApplyOptions 重算）。
            _pages.Register(new DelegateIslandPage(
                id: IslandPageIds.Ai,
                title: "AI 对话",
                view: AiCardLayer,
                order: 3,
                preferredHeight: IslandMetrics.CardHeightLyrics,
                available: options => SettingsRegistry.GetBool(options, "AiEnabled", false),
                activated: SyncAiCardContent,
                viewportChanged: SetAiCardLayerHeight));

            // 实时网速页（C 组②）：实时上下行 + 最近 60 秒曲线。
            // **不设 available** —— 用户需求是"新增功能必须看得到、测得到"，
            // 所以哪怕设置里没开网速监控，这一页也始终能翻到，页内写"未开启"提示。
            RegisterMergedSystemPage();

            // 下载进度页（第二批③）：浏览器 downloads 表 + Steam appmanifest 清单（只读）。
            // 同样不设 available：没开开关这一页也能翻到，页内写"当前没有下载任务"。
            _pages.Register(new DelegateIslandPage(
                id: IslandPageIds.Download,
                title: "下载状态",
                view: DownloadCardLayer,
                order: 6,
                preferredHeight: IslandMetrics.CardHeightLyrics,
                activated: SyncDownloadCard,
                viewportChanged: SetDownloadCardLayerHeight));

            RegisterDesktopNotificationPage();
            RegisterUtilityPages();
            // 初值与老代码的 _currentCardPage = 0 一致：开机就停在音乐页。
            _pages.SetActiveIndex(_pages.IndexOfOrDefault(IslandPageIds.Music));
            InitializeComponentSwitcher();
        }

        /// <summary>
        /// 「此刻弹着的通知对应哪一页卡片」。点开岛（细条/胶囊 → 卡片）时按它选页，
        /// 让"下载中/系统告警/agent 忙碌"这类弹窗点开就是它自己的卡片，而不是被剪切板兜底抢走。
        ///
        /// 三条规则：
        ///   * 判据用 <see cref="IslandLayerHost.IsActive"/>（够格显示）而不是"已经画在胶囊上"——
        ///     PillOnly 的图层在卡片态本来就画不出来，而这里恰恰是在决定进哪一页；
        ///   * 只映射**真的有对应卡片**的图层：欢迎 / 蓝牙 / 电池没有专属卡片，返回 null 交给
        ///     调用方的原有优先级（音量 → 剪切板 → 正在播放 → 剪切板历史）；
        ///   * 表按通知图层的仲裁优先级降序（与构造函数里的 Priority 一致），先命中者赢。
        /// </summary>
        private string? CardPageForNotifyLayer()
        {
            // 优先匹配当前主岛对应的那一页（若主岛是置顶或当前活跃的通知小岛，点击时直达该卡片页）
            if (!string.IsNullOrEmpty(_activeMainIslandKey) && !string.Equals(_activeMainIslandKey, "music", StringComparison.Ordinal))
            {
                string? pageForMain = _activeMainIslandKey switch
                {
                    "volume" => IslandPageIds.Volume,
                    "download" => IslandPageIds.Download,
                    "netspeed" => IslandPageIds.System,
                    "system" => IslandPageIds.System,
                    DesktopToastKey => IslandPageIds.Notifications,
                    "clipboard" => IslandPageIds.Clipboard,
                    "approval" or "agentstatus" => IslandPageIds.Ai,
                    _ => CardPageFromPinKey(_activeMainIslandKey)
                };
                if (pageForMain != null && _pages.IndexOf(pageForMain) >= 0)
                {
                    return pageForMain;
                }
            }

            (string Key, string Page)[] table =
            {
                ("approval", IslandPageIds.Ai),          // 90
                ("volume", IslandPageIds.Volume),        // 80
                ("system", IslandPageIds.System),        // 68
                ("download", IslandPageIds.Download),    // 66
                ("netspeed", IslandPageIds.System),      // 系统状态与网络共用卡片
                (DesktopToastKey, IslandPageIds.Notifications),
                ("clipboard", IslandPageIds.Clipboard),  // 56
                ("agentstatus", IslandPageIds.Ai),       // 48
            };

            foreach (var (key, page) in table)
            {
                if (!_layers.IsActive(key)) continue;

                // 该页在当前设置下不可用（例如没开「AI 对话」时的 AI 页）：不抢页，继续往下找。
                if (_pages.IndexOf(page) < 0) continue;

                return page;
            }

            return null;
        }

        /// <summary>音量卡抽屉撑高时只长音量页这一层，另两层保持 194 不会互相错位。</summary>
        private void SetVolumeCardLayerHeight(double height)
        {
            if (VolumeCardLayer != null) VolumeCardLayer.Height = height;
        }

        /// <summary>AI 页同理：输入态/带对话时整卡被 SetCardDrawerExtra 撑高，只有这一层跟着长。</summary>
        private void SetAiCardLayerHeight(double height)
        {
            if (AiCardLayer != null) AiCardLayer.Height = height;
        }

        /// <summary>
        /// 调试钩子 <c>ALPHA_ISLAND_DIAG_PAGER=1</c>：把右侧轮廓线的命中区域打进日志。
        /// <para>
        /// 验收脚本要精确把光标/滚轮送到这条切换线上，
        /// 位置随岛体宽度、高度和圆角变化，布局跑完才量得准。
        /// </para>
        /// </summary>
        private bool _pagerDiagWired;

        private void DiagnosePagerRect()
        {
            if (Environment.GetEnvironmentVariable("ALPHA_ISLAND_DIAG_PAGER") != "1") return;
            if (ComponentHandle == null) return;

            if (!_pagerDiagWired)
            {
                _pagerDiagWired = true;
                // 细条态隐藏轮廓线；尺寸变化后重新记录当前形态的命中区域。
                // 挂尺寸变化才能拿到「卡片态下」的那一份矩形。
                ComponentHandle.SizeChanged += (_, _) => LogPagerRect();
            }

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(LogPagerRect));
        }

        private void LogPagerRect()
        {
            if (ComponentHandle == null || ComponentHandle.Visibility != Visibility.Visible) return;
            var bounds = Controls.IslandContourHandle.BuildContour(ComponentHandle.RenderSize, ComponentHandle.ContourCornerRadius).Bounds;
            if (bounds.IsEmpty) return;
            bounds.Inflate(8, 8);
            // 必须全限定：WinForms 被 ImplicitUsings 带进来，Point 会在 System.Drawing / System.Windows 之间歧义（CS0104）。
            System.Windows.Point origin = ComponentHandle.PointToScreen(bounds.TopLeft);
            IslandLog.Debug("Diag",
                $"[pager] x={origin.X:0} y={origin.Y:0} w={bounds.Width:0} h={bounds.Height:0} pages={_pages.Count}");
        }

        private Brush ResolveAccentBrush()
        {
            if (TryFindResource("AccentCyan") is Brush brush) return brush;
            return new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0xE5, 0xFF));
        }

        /// <summary>同步轮廓线与当前卡片的波浪底色。</summary>
        private void SyncCardPageVisuals()
        {
            SyncCardViewportHeight();
            SyncComponentHandle();
            if (_pages.ActiveId != IslandPageIds.Music || _state != IslandState.Card)
            {
                if (MediaSourcesButton.ContextMenu is { IsOpen: true } mediaMenu) mediaMenu.IsOpen = false;
                ResetLyricPresentation();
                if (_musicProgressHovered) SetMusicProgressHovered(false);
            }

            if (CardWave != null)
            {
                CardWave.Opacity =
                    (_pages.ActiveId == IslandPageIds.Music && _state == IslandState.Card && _options.ShowWaveLayer)
                        ? 1.0
                        : 0.0;
            }
            UpdateSideBubbleHint();
        }

        private void SyncCardViewportHeight()
        {
            double musicHeight = LyricsAreaVisible ? IslandMetrics.MusicCardHeightLyrics : IslandMetrics.CardHeight;
            CardLayer.Height = musicHeight - 20;
            double pageHeight = _pages.ActiveId == IslandPageIds.Music
                ? musicHeight : _pages.ActivePage?.PreferredHeight ?? IslandMetrics.CardHeightLyrics;
            double height = (_pages.ActiveId == IslandPageIds.Music ? pageHeight - 20 : Math.Max(194, pageHeight - 20))
                + _cardDrawerExtra;
            if (Math.Abs(CardViewport.Height - height) < 0.5) return;
            CardViewport.Height = height;
            _pages.BroadcastViewportHeight(height);
        }

    }
}
