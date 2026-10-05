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

// Card page carousel.
// Moved verbatim out of the former single-file MainWindow.xaml.cs; no logic changes.
namespace alpha_island
{
    public partial class MainWindow
    {
        // ================= 卡片页轮播 (Win11 风格) =================

        private void SwitchCardPage(int targetPage, bool animated = true, bool updateGeometry = true)
        {
            // 页集合（含顺序与可用性）唯一真源是 _pages；这里只做动画与可视收尾。
            if (targetPage < 0 || targetPage >= _pages.Count) return;

            bool pageChanged = targetPage != _pages.ActiveIndex;
            if (pageChanged) EndVolumeDrag();

            // 翻页时抽屉必须收回：它撑高的岛体不会自己缩回去
            // （用户报"展开过输出设备/音量合成器之后切到别的卡片，岛还是那么大，只有点收起才恢复"）。
            if (pageChanged) ResetCardDrawers();

            // 生命周期与内容刷新交给页自己（剪切板历史 / 音量卡内容）；重切同一页也会再刷一次。
            _pages.SetActiveIndex(targetPage);
            SyncCardViewportHeight();

            // 卡片页级置顶（④）跟着页走：翻到别的页就自动取消 —— 钉的那张卡已经不在眼前了。
            if (_pinnedCardPage != null && !string.Equals(_pinnedCardPage, _pages.ActiveId, StringComparison.Ordinal))
            {
                IslandLog.Debug("Pin", $"卡片置顶：换页（{_pinnedCardPage} → {_pages.ActiveId}）→ 取消[pin=card-off reason=page]");
                _pinnedCardPage = null;
            }

            IslandLog.Debug("Cards", $"卡片页 → {_pages.ActivePage?.Title ?? "?"}（id={_pages.ActiveId}，" +
                                     $"第 {targetPage + 1}/{_pages.Count} 页，动画={animated && !MotionPolicy.ReduceMotion}）");

            // 「减弱动效」：直接走下面的瞬时分支（复用它，不另写一套）。
            if (MotionPolicy.ReduceMotion) animated = false;

            TimeSpan duration = animated ? TimeSpan.FromMilliseconds(260) : TimeSpan.Zero;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            // 卡片高度按页计算，禁用动画时也必须立即同步目标尺寸。
            if (updateGeometry && _state == IslandState.Card && _animator != null)
            {
                // 只在岛高真的需要变时才起形变：同级页同为 214 高，翻页时再来一发弹簧
                // 会和自己那套翻页动画抢同一批渲染帧 —— 那正是"切到第三张卡明显更卡"。
                var target = CardGeometry;
                double current = IslandBody.Height;
                if (double.IsNaN(current) || current <= 0) current = IslandBody.ActualHeight;
                if (Math.Abs(current - target.Height) > 0.5)
                {
                    if (animated)
                        MorphIsland(target, duration, new CubicEase { EasingMode = EasingMode.EaseOut });
                    else
                    {
                        _widthOnlyMorph = false;
                        _animator.SetImmediate(target);
                        _layers.EndMorph();
                    }
                }
            }

            // 动画期间 ProgressTimer_Tick 会让开这一拍（见那里的注释）。
            // 先置 false 再按本次是否真的动画置 true：即使上一次动画被本次 BeginAnimation 顶掉
            // （被替换的动画不会触发 Completed），状态也不会卡在"永远在翻页"。
            _cardAnimating = false;

            // 位移至少覆盖通用页和音乐页的高度；纯音乐视口较矮时，其他完整页面也不会互相重叠。
            double cardStep = Math.Max(194, Math.Max(CardViewport.Height, CardLayer.Height));

            var pages = _pages.Pages;
            int lastIndex = pages.Count - 1;

            // 动画前：让所有参与页面处于 Visible 状态，交互命中只给当前目标页。
            for (int i = 0; i < pages.Count; i++)
            {
                pages[i].View.Visibility = Visibility.Visible;
                pages[i].View.IsHitTestVisible = i == targetPage;
            }

            if (animated)
            {
                _cardAnimating = true;

                for (int i = 0; i < pages.Count; i++)
                {
                    double y = (i - targetPage) * cardStep;      // 目标页在 0，上方的页为负、下方的页为正
                    double opacity = i == targetPage ? 1.0 : 0.0;

                    if (pages[i].View.RenderTransform is TranslateTransform translate)
                    {
                        translate.BeginAnimation(TranslateTransform.YProperty,
                            new DoubleAnimation(y, duration) { EasingFunction = ease });
                    }

                    var anim = new DoubleAnimation(opacity, duration) { EasingFunction = ease };

                    // Completed 必须在 BeginAnimation 之前挂！实测（同一会话内两个探针）：
                    // 先挂 = 260ms 后正常触发；后挂 = 永远不触发（时钟建好后再订阅不会接上）。
                    // 后挂的后果是 _cardAnimating 永远停在 true，ProgressTimer_Tick 每拍都让开，
                    // 展开/收起就此瘫掉。老实现是先挂再 BeginAnimation —— 重写时不能丢这个顺序。
                    if (i == lastIndex)
                    {
                        anim.Completed += (s, e) =>
                        {
                            _cardAnimating = false;
                            // 动画结束时把非当前页的命中测试收干净，杜绝卡片重叠、事件穿透与渲染干扰。
                            ApplyCardLayerVisibilities(_pages.ActiveIndex);
                            IslandLog.Debug("Pages", $"动画结束 → {DescribePageState()}");
                        };
                    }

                    pages[i].View.BeginAnimation(UIElement.OpacityProperty, anim);
                }

            }
            else
            {
                for (int i = 0; i < pages.Count; i++)
                {
                    double y = (i - targetPage) * cardStep;
                    double opacity = i == targetPage ? 1.0 : 0.0;

                    if (pages[i].View.RenderTransform is TranslateTransform translate)
                    {
                        translate.BeginAnimation(TranslateTransform.YProperty, null);
                        translate.Y = y;
                    }

                    pages[i].View.BeginAnimation(UIElement.OpacityProperty, null);
                    pages[i].View.Opacity = opacity;
                }

                ApplyCardLayerVisibilities(targetPage);
            }

            SyncCardPageVisuals();

            // 一行把翻页的可验证事实写清楚（页 Id / 序号 / 是否动画 / 位移步长 / 卡片高 / 抽屉额外高 /
            // 轮廓线显隐 / 波浪底色透明度）—— 核对翻页时靠这一行就能判对错。
            IslandLog.Debug("Pages",
                $"切页 → {DescribePageState()} animated={animated} step={cardStep:0} " +
                $"card={CardGeometry.Height:0} extra={_cardDrawerExtra:0} wave={CardWave?.Opacity:0.##}");
        }

        /// <summary>
        /// 翻页状态的可验证摘要。注意 <c>IsHitTestVisible</c> 的有效值会被祖先胁迫（coerced）：
        /// <c>CardCarouselHost.IsHitTestVisible == false</c> 时三层读出来全是 F，即使本地值已经是 T
        /// （实测 <c>DependencyPropertyHelper.GetValueSource(...).IsCoerced == true</c>）。所以
        /// <c>host=</c> 一并打出来，避免把 SetState 中途那一拍误判成"命中区丢了"。
        /// </summary>
        private string DescribePageState()
        {
            var vis = new System.Text.StringBuilder();
            var hit = new System.Text.StringBuilder();
            var names = new System.Text.StringBuilder();

            for (int i = 0; i < _pages.Count; i++)
            {
                if (i > 0) { vis.Append('/'); hit.Append('/'); names.Append('/'); }
                // 各页一律常驻 Visible（绝不 Collapsed，否则下次翻到该页要重新冷排版）。
                vis.Append(_pages.Pages[i].View.Visibility == Visibility.Visible ? "V" : "H");
                hit.Append(_pages.Pages[i].View.IsHitTestVisible ? "T" : "F");
                names.Append((_pages.Pages[i].View as FrameworkElement)?.Name ?? "?");
            }

            return $"{_pages.ActiveId} index={_pages.ActiveIndex}/{_pages.Count} switcher={ComponentHandle?.Visibility} " +
                   $"names=[{names}] vis=[{vis}] hit=[{hit}] host={(CardCarouselHost?.IsHitTestVisible == true ? "T" : "F")}";
        }

        private void ApplyCardLayerVisibilities(int activePage)
        {
            // 各页一律常驻 Visible：非当前页靠 TranslateTransform 被推到视口外（±一个视口高度），
            // 被 CardViewport.ClipToBounds 裁掉，既看不见也点不到。绝不 Collapsed ——
            // 一旦 Collapsed，下次翻到该页要重新冷排版（实测 10~20ms 的翻页卡顿就来自这里）。
            var pages = _pages.Pages;
            for (int i = 0; i < pages.Count; i++)
            {
                pages[i].View.Visibility = Visibility.Visible;
                pages[i].View.IsHitTestVisible = i == activePage;
            }
        }

        /// <summary>
        /// 卡片正文的翻页逻辑保留；右侧轮廓线由组件切换器独立处理，避免与正文滚动混用。
        /// <para>内层自己有滚动条的页（剪切板历史 / AI 气泡）先把自己的滚动条滚到底，才让翻页接管。</para>
        /// </summary>
        /// <returns>true = 真的换了页（调用方据此决定 Handled，这样内层滚动条还能正常工作）。</returns>
        private readonly WheelAccumulator _pageWheel = new();
        private long _lastWheelPageUtcTick;
        private bool TryWheelSwitchPage(int delta, DependencyObject? wheelSource = null)
        {
            if (_state != IslandState.Card) return false;

            int index = _pages.ActiveIndex;
            if (index < 0) return false;
            if (_pages.Pages[index] is Plugins.PluginIslandPage plugin && plugin.KeepsWheel(wheelSource, delta)) return false;

            if (delta < 0)
            {
                // 向下滚：切到下一页
                if (index >= _pages.Count - 1) return false;

                // 剪切板页自己有滚动条：只有滚到底才让翻页接管，否则滚轮会被"翻页"吃掉。
                if (_pages.ActiveId == IslandPageIds.Clipboard &&
                    ClipboardHistoryScroll.VerticalOffset < ClipboardHistoryScroll.ScrollableHeight - 1.0)
                {
                    return false;
                }

                // AI 页同理：气泡列表还能往下滚就先让它滚。
                if (_pages.ActiveId == IslandPageIds.Ai &&
                    AiScrollViewer.VerticalOffset < AiScrollViewer.ScrollableHeight - 1.0)
                {
                    return false;
                }

                if (_pageWheel.Consume(delta) == 0 || Environment.TickCount64 - _lastWheelPageUtcTick < 250) return true;
                _lastWheelPageUtcTick = Environment.TickCount64;
                SwitchCardPage(index + 1);
                return true;
            }

            if (delta > 0)
            {
                // 向上滚：切到上一页
                if (index <= 0) return false;

                if (_pages.ActiveId == IslandPageIds.Clipboard &&
                    ClipboardHistoryScroll.VerticalOffset > 1.0)
                {
                    return false;
                }

                if (_pages.ActiveId == IslandPageIds.Ai &&
                    AiScrollViewer.VerticalOffset > 1.0)
                {
                    return false;
                }

                if (_pageWheel.Consume(delta) == 0 || Environment.TickCount64 - _lastWheelPageUtcTick < 250) return true;
                _lastWheelPageUtcTick = Environment.TickCount64;
                SwitchCardPage(index - 1);
                return true;
            }

            return false;
        }

        /// <summary>仅在线条附近命中组件切换器，正文与滚动条仍使用自己的交互。</summary>
        private void CardCarousel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_pages.ActiveId is IslandPageIds.Notifications or IslandPageIds.System) return;
            // 宿主这一条保留：它负责卡片正文上的滚轮，兜底那条在 IslandBody_PreviewMouseWheel。
            if (TryWheelSwitchPage(e.Delta, e.OriginalSource as DependencyObject)) e.Handled = true;
        }
    }
}
