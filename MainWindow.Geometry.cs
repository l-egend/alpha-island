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

// Island shape/size/geometry and the state machine.
// Moved verbatim out of the former single-file MainWindow.xaml.cs; no logic changes.
namespace alpha_island
{
    public partial class MainWindow
    {
        // ================= 形态与几何 =================

        private static IslandGeometry BarGeometry =>
            new(IslandMetrics.BarWidth, IslandMetrics.BarHeight, IslandMetrics.BarTop, IslandMetrics.BarCorner);

        /// <summary>
        /// 一级胶囊的几何。宽度不是常量：歌词长了要按实测宽度把胶囊撑开（见 UpdatePillLyricScroll），
        /// 所以这里用 CurrentPillWidth（基准宽度 ~ 上限），而"以中心点为准"由窗口的居中布局天然保证。
        /// </summary>
        private IslandGeometry PillGeometry =>
            new(CurrentPillWidth, IslandMetrics.PillHeight, IslandMetrics.PillTop, IslandMetrics.PillCorner);

        private static IslandGeometry TrayGeometry =>
            new(IslandMetrics.TrayWidth, IslandMetrics.TrayHeight, IslandMetrics.TrayTop, IslandMetrics.TrayCorner);

        private IslandGeometry CardGeometry
        {
            get
            {
                // 歌词只撑高音乐卡片；其他详情页由各自 PreferredHeight 决定。
                double baseHeight = _pages.ActiveId == IslandPageIds.Music && LyricsAreaVisible
                    ? IslandMetrics.MusicCardHeightLyrics : IslandMetrics.CardHeight;
                var page = _pages.ActivePage;
                double h = page != null ? Math.Max(baseHeight, page.PreferredHeight) : baseHeight;
                // 音量卡抽屉展开时整卡要长高，见 SetCardDrawerExtra：岛体可见带 ≈ 身高 − 22，
                // 而卡片视口 = 194 + 抽屉高，两边必须同步长，否则多出来的部分会被
                // IslandBody / CardViewport 的 ClipToBounds 裁掉（连命中测试都没有）。
                if (_cardDrawerExtra > 0) h += _cardDrawerExtra + 4;
                return new IslandGeometry(IslandMetrics.CardWidth, h, IslandMetrics.CardTop,
                    new CornerRadius(IslandMetrics.CardCornerRadius));
            }
        }

        /// <summary>歌词区是否真的占位（决定卡片高度）。</summary>
        private bool LyricsAreaVisible => _options.LyricsEnabled && _lyricsDoc != null && _lyricsDoc.HasLyrics;

        private IslandGeometry GeometryFor(IslandState state) => state switch
        {
            IslandState.Pill => PillGeometry,
            IslandState.Tray => TrayGeometry,
            IslandState.Card => CardGeometry,
            _ => BarGeometry
        };

        /// <summary>
        /// 切换到目标形态。这是唯一的入口 —— 尺寸、圆角、内容层透明度、可视化开关、
        /// 卡拉OK 钩子全部在这里同步，避免各条路径各改一半导致状态不一致。
        /// </summary>
        private void SetState(IslandState next, bool force = false, string? preferredPage = null)
        {
            // 审批操作位于胶囊：不能被普通点击或选页入口切到没有审批按钮的卡片。
            if (_approvalCurrent != null && next == IslandState.Card) next = IslandState.Pill;
            // 暂停隐藏只撤下音乐内容；所有自动降级/再展开入口也必须跳过没有载荷的胶囊。
            if (next == IslandState.Pill && (ShouldHidePausedMedia || ShouldHideForegroundMedia) && _approvalCurrent == null
                && !_clipboardLocked && !HasActiveNotification)
                next = IslandState.Bar;
            if (next == IslandState.Card && ShouldHideForegroundMedia
                && (preferredPage ?? _pages.ActiveId) == IslandPageIds.Music) next = IslandState.Bar;
            if (!force && next == _state) return;
            if (_exiting) return;

            if (MediaSourcesButton.ContextMenu is { IsOpen: true } mediaMenu) mediaMenu.IsOpen = false;

            if (next != IslandState.Tray && _fileDragActive) EndIncomingFileDrag(restore: false);

            CloseComponentSwitcher();

            IslandState prev = _state;
            if (next != prev) EndVolumeDrag();
            if (next != IslandState.Card) _componentCardAwaitingPointer = false;
            _state = next;

            // 卡片页级置顶（本轮 ④）只在"停在卡片态"时成立：一旦离开卡片（显式下滑、被更高优先级的
            // 形态顶掉、收回细条），这个 pin 自动释放 —— 否则它会留在字段里，下次进卡片又"悄悄"生效。
            if (prev == IslandState.Card && next != IslandState.Card && _pinnedCardPage != null)
            {
                IslandLog.Debug("Pin", $"卡片置顶：离开卡片态（{prev} → {next}）→ 取消（{_pinnedCardPage}）[pin=card-off reason=state]");
                _pinnedCardPage = null;
            }

            // 开一段"形变窗口"（S3）：宿主记下换态前的可见层，作为本次形变要淡出的源层。
            // 这是通知图层透明度曲线的唯一入口 —— 旧实现把同样的语义写成 ApplyReveal 里的
            // 7 行 `_revealTo == Bar && _currentAction == Xxx` 特判（只认老式通知）。
            _layers.BeginMorph(prev, next);

            // 形态变了就要重算节能节奏：细条态降到 1s，离开细条立刻回到 100ms 并补跑一拍
            // （见 MainWindow.Motion.cs 的 ApplyPowerSavePacing）。
            ApplyPowerSavePacing();

            // 换态的第一件事：掐掉"胶囊宽度形变"留下的那层模糊（AnimateIslandWidthBlur）。
            // 它是 0→峰值→0 的 300ms 关键帧动画，一旦在换态前起飞，就会跨过换态继续跑完，
            // 把正在出场的胶囊内容糊掉 —— 观感正是"从小岛返回时内容闪一下"。
            CancelPillWidthBlur();

            // 新一次换态：内容交接标记复位（见 ApplyReveal 里"小岛返回"的硬交接逻辑）。
            _cardHandoff = false;

            // 离开细条就立刻掐掉收回动画中间态那颗球；若收缩回细条，则装配当前左侧状态的圆形过渡内容
            if (next == IslandState.Bar && prev != IslandState.Bar)
            {
                PrepareSiriOrbContent();
            }
            if (next != IslandState.Bar) CancelSiriOrb();

            bool growing = next > prev || (force && next == prev);
            var duration = growing ? IslandMetrics.ExpandDuration : IslandMetrics.GrowDuration;

            // 卡片页必须在算几何之前定下来：CardGeometry 的高度依赖 _currentCardPage
            // （无歌词 138 / 有歌词或第 1、2 页 214）。以前这段在 MorphIsland 之后执行，
            // 于是"页码决定岛高"永远慢一拍 —— 展开到一半才补一次高度形变，
            // 观感就是卡片内容先闪一下、再突然长高。
            if (next == IslandState.Card)
            {
                // 进卡片前把音量卡抽屉状态清干净：抽屉展开会把整卡和岛体一起撑高（_cardDrawerExtra），
                // 这份额外高度必须随"离开卡片/重新进卡片"归零，否则下次进来岛还是大的。
                ResetCardDrawers();

                // 选页按 Id 而不是序号：页集合唯一真源是 IslandPageHost，将来加/删页不用改这里。
                // 第一优先：**此刻正弹着的通知**对应哪一页就落到哪一页。老实现完全没看通知图层，
                // 下面那条"剪切板有历史就用剪切板"的兜底又永远成立，于是不管弹的是下载/系统/agent，
                // 点开岛都是剪切板页（用户报的就是这个）。没有对应卡片的通知（欢迎/蓝牙/电池）返回 null。
                string? notifyPage = preferredPage != null && _pages.IndexOf(preferredPage) >= 0
                    ? preferredPage : CardPageForNotifyLayer();
                string targetPage = notifyPage ?? IslandPageIds.Music;
                if (notifyPage == null)
                {
                    if (_volumeNotifying && VolumeNotifyActive)
                    {
                        targetPage = IslandPageIds.Volume;
                    }
                    else if (_clipboardLocked || _clipboardNotifying)
                    {
                        targetPage = IslandPageIds.Clipboard;
                    }
                    else if (_smtc?.Current?.HasMedia == true && _smtc.Current.IsPlaying)
                    {
                        targetPage = IslandPageIds.Music;
                    }
                    else if (_pages.ActiveId is { Length: > 0 } currentPage && _pages.IndexOf(currentPage) >= 0)
                    {
                        // 什么都没弹：**留在用户上一张卡片**（点开就是刚才看的那张）。
                        // 这里原来是 `_clipboard?.History.Count > 0 → 剪切板` 的兜底 ——
                        // 剪贴板历史几乎永远非空，于是所有入口都被它抢走（用户报的"展开后都是剪切板"）。
                        targetPage = currentPage;
                    }
                }

                // 调试：ALPHA_ISLAND_CARD_PAGE_LOCK=1 时**不要**按内容重选页。
                // 起因（第二批③ 下载进度实测）：脚本用 CARD_PAGE=download 停到下载页后，
                // 岛体进卡片这一次换态会按内容优先级重选 —— 剪贴板历史非空就把页抢走（实测 0.3 秒内），
                // 于是脚本永远看不到那一页的界面文案。带这个环境变量时保持脚本指定的页。
                if (Environment.GetEnvironmentVariable("ALPHA_ISLAND_CARD_PAGE_LOCK") != "1")
                {
                    // 本次形态切换在后面统一形变，选页阶段只预先排版，避免抢先跳到目标几何。
                    SwitchCardPage(_pages.IndexOfOrDefault(targetPage), animated: false, updateGeometry: false);
                }
            }

            // 内容层不做独立时间轴的淡入淡出：显露度由主弹簧进度驱动（见 ApplyReveal），
            // 材质展开到哪儿内容就露到哪儿，源层在同一个进度上退场。
            _revealFrom = prev;
            _revealTo = next;
            _reveal = 0.0;
            ApplyReveal();
            SyncComponentHandle();

            if (_animator != null)
            {
                // 从小岛回到主岛：只靠两条弹簧的相位差做"横向先摊开、高度后追上"的融合，
                // 不再给路径点。原因（ALPHA_ISLAND_TRACE 实测轨迹）：原来的 via 把宽度先推到
                // 472px（比目标宽 42px）再缩回 430px，而 via 接点处 smoothstep 又把几何速度压到 0，
                // 于是 62~76ms 这段宽度只变了 3px 然后反向 —— 眼睛看到的就是"展开到一半顿一下"。
                // 去掉 via 后宽度单调涨到目标（弹簧自身的过冲只多出 6px），高度仍滞后 1.38 拍。
                bool fromPill = next == IslandState.Card && prev != IslandState.Card;

                // 卡片的测量/排列在形变第一帧之前结清：否则整棵子树的排版会落在弹簧的某一帧上，
                // 那一帧的掉帧就是用户看到的"主岛内容闪一下"。
                if (next == IslandState.Card && CardCarouselHost.Visibility == Visibility.Visible)
                {
                    CardCarouselHost.UpdateLayout();
                }

                MorphIsland(GeometryFor(next), duration, new CubicEase { EasingMode = EasingMode.EaseInOut },
                    () =>
                    {
                        if (_state == IslandState.Card)
                        {
                            CardCarouselHost.Visibility = Visibility.Visible;
                            CardCarouselHost.Opacity = 1.0;
                            CardCarouselHost.IsHitTestVisible = true;
                        }
                        else
                        {
                            CardCarouselHost.BeginAnimation(UIElement.OpacityProperty, null);
                            CardCarouselHost.Opacity = 0;
                            CardCarouselHost.IsHitTestVisible = false;
                        }

                        // 形变收尾必须整拍重算一次通知图层（原来只在 Pill 态做，Card/Bar 收尾会漏）。
                        // 一级胶囊内容层的显隐也挂在这条链上：宿主按 plan + 唯一判据落地 ——
                        // 旧写法在这里无条件点亮 PillLayer（完全不看 _layers 有没有通知独占，
                        // 实测叠层窗口 103~116ms、起始触发点 = morph-complete）。
                        SyncNotificationLayers();

                        ClearCardTransitionBlur();
                        CheckPillLayerOverlapInvariant("morph-complete");
                    },
                    followLag: fromPill ? 1.38 : 1.0);
            }
            else
            {
                _reveal = 1.0;
                ApplyReveal();
            }

            // 各通知图层与一级胶囊内容层的命中测试由图层宿主按 plan 落地（S3），这里只剩
            // 两个不是注册图层的层：文件中转站托盘与卡片轮播宿主。
            FileTransferLayer.IsHitTestVisible = next == IslandState.Tray;
            CardCarouselHost.IsHitTestVisible = next == IslandState.Card;
            if (next == IslandState.Pill)
            {
                _lyricIndex = -2;
                // 进胶囊态时清掉主元素上残留的时间轴（收缩/绽放可能被换态打断，时钟不清则
                // 完成回调不触发、宿主会一直以为"动画在跑"而不再接管显隐）。
                _layers.CancelPrimaryAnimation();
                if (PillLayerTranslate != null)
                {
                    PillLayerTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                    PillLayerTranslate.X = 0;
                }
                SyncMusicPillContent();
                SyncUpNextContent();
            }
            else if (next == IslandState.Card)
            {
                CardCarouselHost.Visibility = Visibility.Visible;
                CardCarouselHost.IsHitTestVisible = true;
                // 源层（胶囊）不在这里直接掐灭：它要按弹簧进度退场（见宿主 BeginMorph/SetReveal）。
                // 立刻 Opacity=0 + Collapsed 会让展开的第一帧"两边都空"，那一下就是闪。
            }
            // next == IslandState.Bar 同理：胶囊内容层由宿主的形变窗口淡出，不再在这里直接掐灭。

            if (next != IslandState.Card && prev != IslandState.Card)
            {
                CardCarouselHost.BeginAnimation(UIElement.OpacityProperty, null);
                CardCarouselHost.Opacity = 0;
                Keyboard.ClearFocus();
            }

            SyncVisualizers();
            SyncUpNextContent();
            SyncBtNotifyContent();
            SyncGreetingContent();
            SyncVolumeContent();
            SyncBatteryNotifyContent();
            SyncClipboardContent();
            SyncFileTransferContent();
            UpdateKaraokeHook();
            SyncBackdrop();
        }

        /// <summary>
        /// 缩回成圆过渡态：根据当前灵动岛左侧实时状态（音量/外设/问候/剪切板/中转站/专辑），在圆内装配对应的专属图标与背景色。
        /// 使形变收缩过渡极其自然连续，避免一律用 Siri 渐变或专辑封面。
        /// </summary>
        private void PrepareSiriOrbContent(IslandActionType? forcedAction = null)
        {
            var action = forcedAction ?? GetCurrentActionType();
            // 取证：收缩过渡期圆心里装配的是哪个动作的图标。这是"通知→收回"连续性的唯一可观测量，
            // 换掉 _currentAction 那套手工记忆时必须靠它做 A/B（同场景跑两遍逐行对比）。
            IslandLog.Debug("SiriOrb", $"缩回过渡图标：{action}（形态={_state}）");
            switch (action)
            {
                case IslandActionType.Volume:
                    OrbCover.Opacity = 0;
                    OrbCoverRing.Opacity = 0;
                    OrbGradient.Opacity = 0;
                    OrbIconContainer.Opacity = 1;
                    OrbIconContainer.Background = VolumeIconContainer.Background ?? new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0xE5, 0xFF));
                    OrbIconContainer.BorderBrush = VolumeIconContainer.BorderBrush ?? new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xE5, 0xFF));
                    OrbIconContainer.BorderThickness = new Thickness(1);
                    OrbIcon.Text = VolumeIcon.Text;
                    OrbIcon.Foreground = VolumeIcon.Foreground ?? new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));
                    break;

                case IslandActionType.Battery:
                    OrbCover.Opacity = 0;
                    OrbCoverRing.Opacity = 0;
                    OrbGradient.Opacity = 0;
                    OrbIconContainer.Opacity = 1;
                    OrbIconContainer.Background = BatteryIconContainer.Background ?? new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0xFF, 0x88));
                    OrbIconContainer.BorderBrush = BatteryIconContainer.BorderBrush ?? new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xFF, 0x88));
                    OrbIconContainer.BorderThickness = new Thickness(1);
                    OrbIcon.Text = BatteryIcon.Text;
                    OrbIcon.Foreground = BatteryIcon.Foreground ?? new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x88));
                    break;

                case IslandActionType.Clipboard:
                    OrbCover.Opacity = 0;
                    OrbCoverRing.Opacity = 0;
                    OrbGradient.Opacity = 0;
                    OrbIconContainer.Opacity = 1;
                    OrbIconContainer.Background = ClipboardIconContainer.Background ?? new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0xF0, 0xFF));
                    OrbIconContainer.BorderBrush = ClipboardIconContainer.BorderBrush ?? new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xF0, 0xFF));
                    OrbIconContainer.BorderThickness = new Thickness(1);
                    OrbIcon.Text = ClipboardIcon.Text;
                    OrbIcon.Foreground = ClipboardIcon.Foreground ?? new SolidColorBrush(Color.FromRgb(0x00, 0xF0, 0xFF));
                    break;

                case IslandActionType.Bluetooth:
                    OrbCover.Opacity = 0;
                    OrbCoverRing.Opacity = 0;
                    OrbGradient.Opacity = 0;
                    OrbIconContainer.Opacity = 1;
                    OrbIconContainer.Background = BtIconContainer.Background ?? new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
                    OrbIconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
                    OrbIconContainer.BorderThickness = new Thickness(1);
                    if (BtDeviceIcon.Visibility == Visibility.Visible && !string.IsNullOrEmpty(BtDeviceIcon.Text))
                    {
                        OrbIcon.Text = BtDeviceIcon.Text;
                        OrbIcon.Foreground = BtDeviceIcon.Foreground ?? new SolidColorBrush(Colors.White);
                    }
                    else
                    {
                        // 耳机 3D 旋转模型在圆球中以经典微光耳机图标呈现
                        OrbIcon.Text = "\uE7F6";
                        OrbIcon.Foreground = new SolidColorBrush(Colors.White);
                    }
                    break;

                case IslandActionType.NetSpeed:
                    OrbCover.Opacity = 0;
                    OrbCoverRing.Opacity = 0;
                    OrbGradient.Opacity = 0;
                    OrbIconContainer.Opacity = 1;
                    OrbIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x22, 0xD3, 0xEE));
                    OrbIconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x22, 0xD3, 0xEE));
                    OrbIconContainer.BorderThickness = new Thickness(1);
                    OrbIcon.Text = "\uE896";   // 下载箭头（与细条提示层同一套图标语言）
                    OrbIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE));
                    break;

                case IslandActionType.SysNotify:
                    // 系统状态通用提示（A 组②③④⑤）：字形跟着提示层走（内存/飞行模式/隐私灯各不同），
                    // 层里没写过字形时退回"内存"字形，别让圆心里空着。
                    OrbCover.Opacity = 0;
                    OrbCoverRing.Opacity = 0;
                    OrbGradient.Opacity = 0;
                    OrbIconContainer.Opacity = 1;
                    OrbIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x22, 0xD3, 0xEE));
                    OrbIconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x22, 0xD3, 0xEE));
                    OrbIconContainer.BorderThickness = new Thickness(1);
                    OrbIcon.Text = string.IsNullOrEmpty(SysNotifyGlyph?.Text) ? "\uE950" : SysNotifyGlyph.Text;
                    OrbIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE));
                    break;

                case IslandActionType.Greeting:
                    OrbCover.Opacity = 0;
                    OrbCoverRing.Opacity = 0;
                    OrbGradient.Opacity = 0;
                    OrbIconContainer.Opacity = 1;
                    OrbIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0xE5, 0xFF));
                    OrbIconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xE5, 0xFF));
                    OrbIconContainer.BorderThickness = new Thickness(1);
                    OrbIcon.Text = "\uE735";
                    OrbIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));
                    break;

                case IslandActionType.FileTransfer:
                    OrbCover.Opacity = 0;
                    OrbCoverRing.Opacity = 0;
                    OrbGradient.Opacity = 0;
                    OrbIconContainer.Opacity = 1;
                    OrbIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0xE5, 0xFF));
                    OrbIconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xE5, 0xFF));
                    OrbIconContainer.BorderThickness = new Thickness(1);
                    OrbIcon.Text = "\uE8B7";
                    OrbIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));
                    break;

                case IslandActionType.Music:
                default:
                    // 音乐媒体状态：优先更新封面笔刷，有封面显示封面，无封面显示优雅音符图标
                    var art = _smtc?.Current?.Artwork ?? _lastArt;
                    if (art != null)
                    {
                        OrbCoverBrush.ImageSource = art;
                    }
                    bool hasCover = OrbCoverBrush.ImageSource != null;
                    if (hasCover)
                    {
                        OrbCover.Opacity = 1;
                        OrbCoverRing.Opacity = 1;
                        OrbGradient.Opacity = 0;
                        OrbIconContainer.Opacity = 0;
                    }
                    else
                    {
                        OrbCover.Opacity = 0;
                        OrbCoverRing.Opacity = 0;
                        OrbGradient.Opacity = 0;
                        OrbIconContainer.Opacity = 1;
                        OrbIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0xE5, 0xFF));
                        OrbIconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xE5, 0xFF));
                        OrbIconContainer.BorderThickness = new Thickness(1);
                        OrbIcon.Text = "\uE767";
                        OrbIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));
                    }
                    break;
            }
        }

        /// <summary>把"收回动画中间态那颗球"复位：停掉两条动画并把不可见状态写实。</summary>
        private void CancelSiriOrb()
        {
            SiriOrb.BeginAnimation(OpacityProperty, null);
            SiriOrb.Opacity = 0;
            OrbIconContainer.Opacity = 0;
            OrbCover.Opacity = 0;
            OrbCoverRing.Opacity = 0;
            OrbGradient.Opacity = 0;
            SiriOrbTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            SiriOrbTranslate.X = -40;
        }

        /// <summary>PillLyric 的唯一写入口 —— 顺手记住"正常文本"，换曲提示结束后好还原。</summary>
        private void SetPillLyric(string text)
        {
            // 调试灌词期间屏蔽真实歌词，否则歌词服务每 ~100ms 覆盖一次会把跑马灯动画一直打回起点。
            if (_pillLyricForce != null) return;

            _pillLyricNormal = text;
            if (!UpNextActive) ApplyPillLyricText(text);
        }

    }
}
