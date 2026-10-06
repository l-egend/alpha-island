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

// Pill lyric line: line-break transition and width fitting.
// Moved verbatim out of the former single-file MainWindow.xaml.cs; no logic changes.
namespace alpha_island
{
    public partial class MainWindow
    {
        // ===================== 一级胶囊歌词：换行过场 · 宽度自适应 =====================

        /// <summary>歌词基准字号（与 XAML 里 PillLyric 的 FontSize 一致）。</summary>
        private const double PillLyricBaseFontSize = 13.0;

        /// <summary>
        /// 歌词允许缩到的最小字号。整句实在撑不进 <see cref="IslandMetrics.MaxPillWidth"/> 时按比例缩字：
        /// 宁可字小一点，也要"整句完整躺在岛里"，不再横向滚动。
        /// 8px 大约还能容纳 58 个汉字（13px 时约 35 个），再长的句子只能截尾。
        /// </summary>
        private const double PillLyricMinFontSize = 8.0;

        /// <summary>换行过场时歌词的纵向位移（DIP）。约一行 13px 文字的高度，再多就会顶到内容层边界。</summary>
        private const double PillLyricRollDistance = 15.0;

        /// <summary>换行过场时长：旧歌词上移淡出，新歌词从下方上移淡入，两者同时进行。</summary>
        private static readonly Duration PillLyricRollDuration = new(TimeSpan.FromMilliseconds(260));

        /// <summary>胶囊伸缩时内容层的峰值模糊半径（类 iOS 灵动岛那种"化开"的过渡）。</summary>
        private const double PillWidthBlurRadius = 4.0;

        /// <summary>
        /// "宿主宽度还没量出来"时的补测次数预算。见 <see cref="UpdatePillLyricScroll"/>：
        /// 只允许有限次重投递，避免把自己无限排进 Loaded 队列。
        /// </summary>
        private const int PillLyricRetryLimit = 5;
        private int _pillLyricRetry;

        /// <summary>
        /// 当前（或将要）的胶囊宽度。只有正常态的音乐胶囊才会被歌词撑宽；
        /// 问候 / 音量 / 电池 / 剪贴板 / 蓝牙 / 换曲提示这些通知层一律回落到 <see cref="IslandMetrics.PillWidth"/>。
        /// </summary>
        private double _pillLyricWidth = IslandMetrics.PillWidth;

        /// <summary>已实测过宽度的文本，避免形变期间每帧重复 Measure。</summary>
        private string _pillLyricMeasuredText = string.Empty;
        private double _pillLyricMeasuredWidth = -1.0;

        /// <summary>当前歌词字号（整句撑不下时按比例缩小，见 <see cref="UpdatePillLyricScroll"/>）。</summary>
        private double _pillLyricFontSize = PillLyricBaseFontSize;

        /// <summary>
        /// "这句歌词该用多大字号"的缓存键：歌词文本 + 除歌词之外占用的宽度。
        /// 形变期间宿主每帧都会 SizeChanged，没有这个缓存就会每帧都做一次"复位基准字号 → 量 → 缩字号"。
        /// </summary>
        private string _pillLyricFitText = string.Empty;
        private double _pillLyricFitChrome = -1.0;
        private double _pillLyricFitFontSize = PillLyricBaseFontSize;

        /// <summary>
        /// 本次形变是否"只改宽度"。见 <see cref="OnMorphProgress"/>：宽度形变不能再跑一遍内容揭示，
        /// 否则胶囊内容会二次淡入（一闪）。该标记由 <see cref="MorphIsland"/> 在入口处统一清掉。
        /// </summary>
        private bool _widthOnlyMorph;

        /// <summary>
        /// 形变正在飞的时候到来的新宽度目标：先记账，等形变收尾时补做。
        /// 以前被 <see cref="TryMorphPillWidth"/> 的 IsRunning 早退直接吞掉，且宽度形变自己的
        /// 完成回调不回补，于是"置顶 + 长歌词切歌"时岛体会一直停在上一句的长度。
        /// </summary>
        private bool _pillWidthPending;

        /// <summary>正常态（非通知层）音乐胶囊才允许被歌词撑宽。</summary>
        private bool PillLyricWantsWide =>
            IslandSizing.LyricsCanResize(_activeMainIslandKey, _options.LyricsEnabled,
                UpNextActive || _musicIslandDismissed || ShouldHidePausedMedia || (_greetingShowing && GreetingActive) || _approvalCurrent != null);

        /// <summary>当前胶囊形态该用的宽度：通知层在展时回落到基准宽度。</summary>
        private double CurrentPillWidth => _activeMainIslandKey == "netspeed" ? StatusPillWidth
            : IslandSizing.PillWidth(PillLyricWantsWide, _pillLyricWidth);

        /// <summary>写入胶囊歌词：文本变化时做换行过场动画，然后重新量宽度决定伸缩 / 跑马灯。</summary>
        private void ApplyPillLyricText(string text)
        {
            text = text ?? string.Empty;

            // 新的文本 = 新的量宽需求，重新给足补测预算。
            _pillLyricRetry = 0;

            string previous = PillLyric?.Text ?? string.Empty;
            bool changed = PillLyric != null && !string.Equals(previous, text, StringComparison.Ordinal);

            if (!changed)
            {
                // 同一句歌词（歌词服务每 ~100ms 会重复写一次）：只补一次宽度判定，绝不碰动画。
                UpdatePillLyricScroll();
                return;
            }

            // 先落文本再做过场：新歌词必须从一开始就是最终内容。
            if (PillLyric != null) PillLyric.Text = text;
            if (PillLyricEcho != null) PillLyricEcho.Text = text;

            // 过场必须在重算横向位置之前做：幽灵层要接管"上一句"此刻的横向偏移（居中偏移）。
            AnimatePillLyricChange(previous, text);
            UpdatePillLyricScroll();
        }

        /// <summary>
        /// 换行过场：旧歌词留在原处继续上移并淡出，新歌词同时从下方上移并淡入。
        /// 只有胶囊内容层正在显示时才做（细条 / 卡片态没有这个舞台，白跑动画只是浪费帧）。
        /// </summary>
        private void AnimatePillLyricChange(string oldText, string newText)
        {
            if (PillLyric == null || PillLyricStage == null || PillLyricGhost == null) return;

            // 无论走哪条分支，新歌词都必须以"无偏移 + 全不透明"收场，所以先复位舞台。
            PillLyricStage.BeginAnimation(OpacityProperty, null);
            PillLyricRoll?.BeginAnimation(TranslateTransform.YProperty, null);
            if (PillLyricRoll != null) PillLyricRoll.Y = 0;

            bool stageVisible = PillLayer != null && PillLayer.Visibility == Visibility.Visible && PillLayer.Opacity > 0.05;
            if (!stageVisible || !PillLyricWantsWide || string.IsNullOrWhiteSpace(oldText) || oldText == newText)
            {
                ResetPillLyricGhost();
                PillLyricStage.Opacity = 1.0;
                return;
            }

            // 旧歌词：接管舞台此刻的横向位置，再向上淡出（两条独立的 DoubleAnimation）。
            PillLyricGhost.Text = oldText;
            if (PillLyricGhostScroll != null) PillLyricGhostScroll.X = PillLyricScroll?.X ?? 0.0;
            if (PillLyricGhostRoll != null) PillLyricGhostRoll.Y = 0.0;
            PillLyricGhost.BeginAnimation(OpacityProperty, null);
            PillLyricGhost.Opacity = 1.0;

            var rollEase = new CubicEase { EasingMode = EasingMode.EaseOut };
            if (PillLyricGhostRoll != null)
            {
                var ghostRoll = new DoubleAnimation(0.0, -PillLyricRollDistance, PillLyricRollDuration) { EasingFunction = rollEase };
                PillLyricGhostRoll.BeginAnimation(TranslateTransform.YProperty, ghostRoll);
            }
            var ghostFade = new DoubleAnimation(1.0, 0.0, PillLyricRollDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            ghostFade.Completed += (_, _) => ResetPillLyricGhost();
            PillLyricGhost.BeginAnimation(OpacityProperty, ghostFade);

            // 新歌词：从下方 +RollDistance 处上移到原位，同时淡入。
            if (PillLyricRoll != null)
            {
                PillLyricRoll.Y = PillLyricRollDistance;
                var stageRoll = new DoubleAnimation(PillLyricRollDistance, 0.0, PillLyricRollDuration) { EasingFunction = rollEase };
                stageRoll.Completed += (_, _) =>
                {
                    if (PillLyricRoll == null) return;
                    PillLyricRoll.BeginAnimation(TranslateTransform.YProperty, null);
                    PillLyricRoll.Y = 0.0;
                };
                PillLyricRoll.BeginAnimation(TranslateTransform.YProperty, stageRoll);
            }
            PillLyricStage.Opacity = 0.0;
            var stageFade = new DoubleAnimation(0.0, 1.0, PillLyricRollDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            stageFade.Completed += (_, _) =>
            {
                if (PillLyricStage == null) return;
                PillLyricStage.BeginAnimation(OpacityProperty, null);
                PillLyricStage.Opacity = 1.0;
            };
            PillLyricStage.BeginAnimation(OpacityProperty, stageFade);
        }

        /// <summary>收起"上一句歌词"那一层（动画结束后必须调，否则它停在 HoldEnd 的 0 透明度上继续占位）。</summary>
        private void ResetPillLyricGhost()
        {
            if (PillLyricGhost == null) return;

            PillLyricGhost.BeginAnimation(OpacityProperty, null);
            PillLyricGhost.Opacity = 0.0;
            if (PillLyricGhostRoll != null)
            {
                PillLyricGhostRoll.BeginAnimation(TranslateTransform.YProperty, null);
                PillLyricGhostRoll.Y = 0.0;
            }
        }

        /// <summary>把过场用到的两层都复位（离开胶囊态时调，免得下次进来还挂着半截动画）。</summary>
        private void ResetPillLyricRoll()
        {
            ResetPillLyricGhost();

            if (PillLyricStage != null)
            {
                PillLyricStage.BeginAnimation(OpacityProperty, null);
                PillLyricStage.Opacity = 1.0;
            }
            if (PillLyricRoll != null)
            {
                PillLyricRoll.BeginAnimation(TranslateTransform.YProperty, null);
                PillLyricRoll.Y = 0.0;
            }
        }

        private void PillLyricHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 只有宽度真正改变才重测，避免高度变化或微小浮点抖动引发死循环
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 0.5) return;

            // 布局真的变了（包括胶囊正在伸缩）：重置补测预算，让 UpdatePillLyricScroll 重新量宽度。
            _pillLyricRetry = 0;
            UpdatePillLyricScroll();
        }

        /// <summary>
        /// 一句歌词的宽度决策：整句必须完整显示，绝不横向滚动。
        ///   量歌词实际宽度 → 撑不下就按比例缩字号 → 算目标胶囊宽度 → Width 平滑伸缩 → 整句居中静止。
        /// 宽度还没算出来（ActualWidth ≈ 0）时异步排入 Loaded 队列补测，**但只补测有限次**。
        /// </summary>
        private void UpdatePillLyricScroll()
        {
            if (PillLyric == null || PillLyricEcho == null || PillLyricHost == null || PillLyricScroll == null) return;

            double available = PillLyricHost.ActualWidth;
            if (available <= 1.0)
            {
                // 布局还没完成（宿主还是 0 宽，例如所在图层收起 / 尚未 Measure）。
                //
                // ⚠ 这里**绝对不能无上限地重投递自己**：Loaded(6) 的优先级高于
                // Background(4)，一旦被无条件续命，Dispatcher 队列里就永远存在一个 6 级项，
                // Background 队列中的 InitializeServicesDeferred（启动服务加载）与
                // Input(5) 的输入分发会被**永久饿死** —— 实测表现为"启动即卡死"：
                // 岛体画得出来但点不动，歌词/剪贴板/媒体/频谱/蓝牙等模块一个都起不来。
                //
                // 因此只给有限次补测预算；真正的布局变化由 PillLyricHost_SizeChanged 重置预算后再补测。
                if (++_pillLyricRetry <= PillLyricRetryLimit)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdatePillLyricScroll));
                }
                return;
            }

            _pillLyricRetry = 0;

            // 必须重新量一次：刚写完 Text 时 DesiredSize 还是上一段文本的宽度（实测会拿到 73px 这种陈旧值），
            // 直接用它判断会把长歌词误判成"放得下"。用无限宽度量，得到的就是整句话的自然宽度。
            double chrome = PillLyricChrome();
            double maxTextWidth = Math.Max(1.0, IslandMetrics.MaxPillWidth - chrome);

            // ① 整句必须完整显示（用户明确要求：不要因为歌词长就在岛内横向滚动）。
            //    先按基准字号量，装不进宽度上限就按比例缩字号（不低于 PillLyricMinFontSize）。
            //    结果按（文本 + chrome）缓存：形变期间 PillLyricHost_SizeChanged 每帧都会重入本方法，
            //    若每次都"复位到基准字号再量"，就会每帧多量两次文本、多写两次 FontSize。
            string lyricText = PillLyric.Text ?? string.Empty;
            if (!string.Equals(lyricText, _pillLyricFitText, StringComparison.Ordinal)
                || Math.Abs(_pillLyricFitChrome - chrome) > 0.01)
            {
                ApplyPillLyricFontSize(PillLyricBaseFontSize);
                double rawWidth = MeasurePillLyricWidth();
                if (rawWidth <= 1.0) return;

                // +0.5 的容差很关键：否则"缩到刚好装下"后本方法重入时会因浮点误差仍判定为装不下，
                // 在基准字号与缩放字号之间反复横跳。
                _pillLyricFitFontSize = rawWidth > maxTextWidth + 0.5
                    ? Math.Max(PillLyricMinFontSize, PillLyricBaseFontSize * maxTextWidth / rawWidth)
                    : PillLyricBaseFontSize;
                _pillLyricFitText = lyricText;
                _pillLyricFitChrome = chrome;
            }

            ApplyPillLyricFontSize(_pillLyricFitFontSize);
            double textWidth = MeasurePillLyricWidth();
            if (textWidth <= 1.0) return;

            // ② 目标宽度：正好装下整句歌词，但不少于基准宽度、不超过上限。这里只记目标 + 起一次形变。
            SetPillLyricWidth(textWidth + chrome);

            // ③ 排版决策要用"伸缩到位之后"的宽度：形变期间本方法每帧都会被重入，
            //    若按当前中间宽度判断，居中位置会来回抖。
            double hostWidth = Math.Max(1.0, CurrentPillWidth - chrome);

            // 跑马灯已彻底移除：副本层永远收起，横向位移只用来把整句居中。
            if (PillLyricEcho.Visibility != Visibility.Collapsed) PillLyricEcho.Visibility = Visibility.Collapsed;

            PillLyricScroll.BeginAnimation(TranslateTransform.XProperty, null);
            double center = Math.Max(0.0, (hostWidth - textWidth) / 2.0);
            if (Math.Abs(PillLyricScroll.X - center) > 0.5) PillLyricScroll.X = center;
        }

        /// <summary>
        /// 把歌词字号统一落到歌词层、副本层与过场幽灵层上（幽灵层不跟就会比新歌词大一号）。
        /// 字号一变，缓存的实测宽度立刻作废。
        /// </summary>
        private void ApplyPillLyricFontSize(double size)
        {
            if (Math.Abs(_pillLyricFontSize - size) < 0.01) return;

            _pillLyricFontSize = size;
            if (PillLyric != null) PillLyric.FontSize = size;
            if (PillLyricEcho != null) PillLyricEcho.FontSize = size;
            if (PillLyricGhost != null) PillLyricGhost.FontSize = size;

            _pillLyricMeasuredText = string.Empty;
            _pillLyricMeasuredWidth = -1.0;
        }

        /// <summary>量歌词的自然宽度（按文本缓存，避免形变期间每帧 Measure）。</summary>
        private double MeasurePillLyricWidth()
        {
            if (PillLyric == null) return 0.0;

            string text = PillLyric.Text ?? string.Empty;
            if (text == _pillLyricMeasuredText && _pillLyricMeasuredWidth > 0.0) return _pillLyricMeasuredWidth;

            PillLyric.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            _pillLyricMeasuredText = text;
            _pillLyricMeasuredWidth = PillLyric.DesiredSize.Width;
            return _pillLyricMeasuredWidth;
        }

        /// <summary>
        /// 胶囊里除歌词之外占掉的宽度（内容层左右余量 + 歌词宿主两侧 Margin + 右侧频谱列 + 封面）。
        /// 关掉封面时封面列会塌掉，所以这里跟着 Visibility 算，不能写死。
        /// </summary>
        private double PillLyricChrome()
        {
            double chrome = (IslandMetrics.PillWidth - IslandMetrics.PillContentWidth)
                            + (PillLyricHost?.Margin.Left ?? 0.0) + (PillLyricHost?.Margin.Right ?? 0.0)
                            + IslandMetrics.PillSpectrumWidth;

            if (PillCoverHost != null && PillCoverHost.Visibility == Visibility.Visible)
                chrome += IslandMetrics.PillCoverSize;

            return chrome;
        }

        /// <summary>
        /// 记录目标宽度；如果当前正好是胶囊态且没有别的形变在跑，就顺手把宽度伸缩做掉。
        /// 非胶囊态只记目标值（下次进入胶囊态时 GeometryFor(Pill) 会直接给出正确宽度）。
        /// </summary>
        private void SetPillLyricWidth(double target)
        {
            target = Math.Clamp(target, IslandMetrics.PillWidth, IslandMetrics.MaxPillWidth);
            if (Math.Abs(target - _pillLyricWidth) < 0.5)
            {
                // 目标值没变且岛体宽度已经相符，直接返回，杜绝无谓重入
                if (_animator != null && Math.Abs(_animator.CurrentGeometry().Width - target) < 0.5)
                {
                    _pillWidthPending = false;
                    return;
                }
                TryMorphPillWidth();
                return;
            }

            _pillLyricWidth = target;
            TryMorphPillWidth();
        }

        /// <summary>
        /// 把胶囊宽度落到实际的形变上（目标取 CurrentPillWidth：通知层在展 / 歌词关掉时就是基准宽度）。
        /// 形变进行中（例如刚进胶囊态、上一句歌词的伸缩还在飞）先记账，
        /// 由 <see cref="FlushPendingPillWidth"/> 在形变收尾时补做 —— 直接丢掉目标会让岛体永久停在旧长度。
        /// </summary>
        private void TryMorphPillWidth()
        {
            if (_animator == null || _state != IslandState.Pill) return;

            double target = CurrentPillWidth;
            if (Math.Abs(_animator.CurrentGeometry().Width - target) < 0.5)
            {
                _pillWidthPending = false;
                return;
            }

            if (_animator.IsRunning || _widthOnlyMorph)
            {
                _pillWidthPending = true;
                return;
            }

            // 只改宽度的形变不参与内容揭示，先把内容层钉在"已显露"状态，
            // 否则 OnMorphProgress 提前返回会把内容层留在半透明上。
            _reveal = 1.0;
            ApplyReveal();

            _widthOnlyMorph = true;
            _pillWidthPending = false;
            _animator.MorphTo(PillGeometry, _options.GelAnimation, IslandMetrics.PillWidthDuration,
                new CubicEase { EasingMode = EasingMode.EaseInOut },
                completed: () =>
                {
                    _widthOnlyMorph = false;
                    FlushPendingPillWidth();
                });

            // 宽度差异显著且处于有媒体播放时才做材质化开模糊；无媒体播放/默认页面绝不模糊，彻底解决开机默认页面文字闪烁问题
            if (Math.Abs(_animator.CurrentGeometry().Width - target) > 16.0 && _smtc?.Current?.HasMedia == true)
            {
                AnimateIslandWidthBlur();
            }
        }

        /// <summary>
        /// 补做形变期间被吞掉的宽度目标。宽度形变与 <see cref="MorphIsland"/> 两条收尾路径都要走这里。
        /// </summary>
        private void FlushPendingPillWidth()
        {
            if (!_pillWidthPending) return;
            _pillWidthPending = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(TryMorphPillWidth));
        }

        /// <summary>
        /// 胶囊伸缩时给内容层盖一层短促的模糊（0 → 峰值 → 0），做出 iOS 灵动岛那种"材质化开"的过渡。
        /// 用后即摘：闲置时 PillLayer.Effect 必须是 null，否则整层每帧都要走一次离屏渲染。
        /// </summary>
        private void AnimateIslandWidthBlur()
        {
            if (PillLayer == null) return;

            var blur = new BlurEffect { Radius = 0.0, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
            PillLayer.Effect = blur;

            double total = IslandMetrics.PillWidthDuration.TimeSpan.TotalMilliseconds;
            var anim = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(PillWidthBlurRadius,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(total * 0.45)),
                new CubicEase { EasingMode = EasingMode.EaseOut }));
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(0.0,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(total)),
                new CubicEase { EasingMode = EasingMode.EaseIn }));
            anim.Completed += (_, _) =>
            {
                blur.BeginAnimation(BlurEffect.RadiusProperty, null);
                if (ReferenceEquals(PillLayer.Effect, blur)) PillLayer.Effect = null;
            };
            blur.BeginAnimation(BlurEffect.RadiusProperty, anim);
        }

        /// <summary>
        /// 掐掉胶囊宽度形变留下的模糊层（换态 / 离开胶囊时必须调用）。
        /// </summary>
        private void CancelPillWidthBlur()
        {
            if (PillLayer?.Effect == null) return;
            if (PillLayer.Effect is BlurEffect blur)
            {
                blur.BeginAnimation(BlurEffect.RadiusProperty, null);
            }
            PillLayer.Effect = null;
        }

        /// <summary>卡片进出场时整棵子树的最大模糊半径（像素）。</summary>
        private const double CardTransitionBlurRadius = 13.0;

        /// <summary>
        /// 卡片参与形态过渡（胶囊 ⇄ 卡片）时，给整棵卡片子树盖一层"动态模糊"：
        /// 模糊半径从峰值跟着形变进度收敛到 0。
        /// <para>
        /// 这是用户点名要的做法 —— 缩回小岛时主岛内容先糊着退场、展开时糊着浮现，
        /// 把"内容重排/首帧"这段时间藏在模糊里，动画结束时正好锐利，不再出现硬切式的闪一下。
        /// </para>
        /// 用后即摘：闲置时 Effect 必须是 null，否则整层每帧都要多走一次离屏渲染。
        /// </summary>
        private void ApplyCardTransitionBlur(double content)
        {
            if (CardCarouselHost == null) return;

            // 只有真正的换态（from ≠ to）才模糊。翻页 / 抽屉撑高这类"同态形变"也会重跑 _reveal，
            // 但它们本来就不切内容，加模糊只会白掉帧 —— 那正是"第三张卡片卡顿"的来源。
            bool inTransition = _revealFrom != _revealTo
                && (_revealFrom == IslandState.Card || _revealTo == IslandState.Card);
            double radius = inTransition ? (1.0 - Math.Clamp(content, 0.0, 1.0)) * CardTransitionBlurRadius : 0.0;

            if (radius <= 0.35)
            {
                ClearCardTransitionBlur();
                return;
            }

            if (CardCarouselHost.Effect is not BlurEffect blur)
            {
                blur = new BlurEffect
                {
                    Radius = 0,
                    KernelType = KernelType.Gaussian,
                    RenderingBias = RenderingBias.Performance
                };
                CardCarouselHost.Effect = blur;
            }
            blur.Radius = radius;
        }

        /// <summary>摘掉卡片层的模糊（闲置时必须为 null）。</summary>
        private void ClearCardTransitionBlur()
        {
            if (CardCarouselHost?.Effect == null) return;
            if (CardCarouselHost.Effect is BlurEffect blur)
            {
                blur.BeginAnimation(BlurEffect.RadiusProperty, null);
            }
            CardCarouselHost.Effect = null;
        }

        /// <summary>进入/离开胶囊态时起停换行过场，细条状态下不白耗动画帧。</summary>
        private void SetPillLyricScrollRunning(bool running)
        {
            if (running)
            {
                // 重新进入胶囊态：宿主会被重新布局，补测预算重置后再量一次。
                _pillLyricRetry = 0;
                UpdatePillLyricScroll();
                return;
            }

            ResetPillLyricRoll();
        }

        /// <summary>
        /// 换曲 / 暂停提示期间：一级胶囊右侧的频谱整块换成状态指示，中间显示曲名。
        /// 提示结束后原样还原（歌词由 UpdateKaraokeHook / ApplyLyricTexts 重新驱动）。
        /// </summary>
        private void SyncUpNextContent()
        {
            bool up = _upNextShowing && UpNextActive;

            if (up)
            {
                if (_upNextKind == "pause")
                {
                    PillUpNextIcon.Text = "\uE769"; // 暂停图标
                    PillUpNextIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xAA, 0x00)); // 柔和橙黄
                    PillUpNextText.Text = "已暂停";
                }
                else if (_upNextKind == "play")
                {
                    PillUpNextIcon.Text = "\uE768"; // 播放图标
                    PillUpNextIcon.Foreground = (System.Windows.Media.Brush)FindResource("AccentCyan");
                    PillUpNextText.Text = "正在播放";
                }
                else
                {
                    PillUpNextIcon.Text = "\uE893"; // 下一曲图标
                    PillUpNextIcon.Foreground = (System.Windows.Media.Brush)FindResource("AccentCyan");
                    PillUpNextText.Text = "接下来";
                }
                ApplyPillLyricText(_upNextTitle);

                // 平滑淡入淡出及位移动画（与歌词滚动一致）
                PillUpNext.Visibility = Visibility.Visible;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var duration = TimeSpan.FromMilliseconds(220);

                var upFade = new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = ease };
                var upSlide = new DoubleAnimation(8.0, 0.0, duration) { EasingFunction = ease };
                PillUpNext.BeginAnimation(UIElement.OpacityProperty, upFade);
                PillUpNextTranslate?.BeginAnimation(TranslateTransform.YProperty, upSlide);

                var specFade = new DoubleAnimation(PillSpectrum.Opacity, 0.0, duration) { EasingFunction = ease };
                specFade.Completed += (_, __) =>
                {
                    if (_upNextShowing && UpNextActive)
                    {
                        PillSpectrum.Visibility = Visibility.Collapsed;
                    }
                };
                PillSpectrum.BeginAnimation(UIElement.OpacityProperty, specFade);
            }
            else
            {
                PillSpectrum.Visibility = Visibility.Visible;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var duration = TimeSpan.FromMilliseconds(220);

                var specFade = new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = ease };
                PillSpectrum.BeginAnimation(UIElement.OpacityProperty, specFade);

                var upFade = new DoubleAnimation(PillUpNext.Opacity, 0.0, duration) { EasingFunction = ease };
                var upSlide = new DoubleAnimation(0.0, -8.0, duration) { EasingFunction = ease };
                upFade.Completed += (_, __) =>
                {
                    if (!_upNextShowing || !UpNextActive)
                    {
                        PillUpNext.Visibility = Visibility.Collapsed;
                        PillUpNext.Opacity = 0.0;
                    }
                };
                PillUpNext.BeginAnimation(UIElement.OpacityProperty, upFade);
                PillUpNextTranslate?.BeginAnimation(TranslateTransform.YProperty, upSlide);

                if (!string.IsNullOrWhiteSpace(_pillLyricNormal))
                {
                    ApplyPillLyricText(_pillLyricNormal);
                }
            }
            SyncVisualizers();
        }

        /// <summary>
        /// 换曲（上一首 / 下一首）提示的显示时长。
        /// 用户反馈：等新歌都已经唱起来了，胶囊还在提示"接下来 上一首"，所以固定压到 1s。
        /// 暂停 / 恢复播放提示均固定为 1.0s。
        /// </summary>
        private const double UpNextPromptSeconds = 1.0;

        private void ScheduleUpNextDismiss(double seconds)
        {
            _upNextTimer?.Stop();
            seconds = Math.Max(0.6, seconds);
            _upNextTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _upNextTimer.Tick += (_, __) =>
            {
                _upNextTimer.Stop();
                DismissUpNext();
            };
            _upNextTimer.Start();
        }

        private void DismissUpNext()
        {
            _upNextTimer?.Stop();
            _upNextShowing = false;
            _upNextUntil = DateTime.MinValue;
            _lyricIndex = -2;
            SyncUpNextContent();
            SyncPausedMediaVisibility();
            SyncVisualizers();
            SyncMusicPillContent();

            // 若未置顶，且光标不在热区，且未保持播放展开，则收回
            if (!HasVisibleMusicPin && !HasActiveNotification && _state == IslandState.Pill && !IsCursorInHotZone())
            {
                if (!_options.KeepCompactWhilePlaying || _smtc?.Current?.IsPlaying != true)
                {
                    CollapseToBar();
                }
            }
        }

        /// <summary>
        /// 换曲提示：弹出一级胶囊，中间是新曲名、右侧是"接下来"，固定显示 <see cref="UpNextPromptSeconds"/> 秒。
        /// </summary>
        private void BeginUpNext(NowPlayingInfo info)
        {
            if (!info.HasMedia) return;
            if (!_options.UpNextEnabled) return;
            if (_greetingShowing && GreetingActive) return;
            if (!CanPresentMediaStatePrompt) return;

            _upNextTitle = info.DisplayTitle;
            _upNextKind = "upnext";
            _upNextUntil = DateTime.UtcNow.AddSeconds(UpNextPromptSeconds);
            _upNextShowing = true;
            ScheduleUpNextDismiss(UpNextPromptSeconds);
            BringMediaPromptToMain();
            SyncUpNextContent();
        }

        /// <summary>
        /// 暂停提示：音乐暂停时弹出一级胶囊，中间是曲名、右侧是"已暂停"，持续 1 秒后自动还原频谱与歌词。
        /// </summary>
        private void BeginPausePrompt(NowPlayingInfo info)
        {
            if (!info.HasMedia) return;
            if (!_options.PauseNotifyEnabled) return;
            if (_greetingShowing && GreetingActive) return;
            if (!CanPresentMediaStatePrompt) return;

            _upNextTitle = info.DisplayTitle;
            _upNextKind = "pause";
            const double promptSeconds = 1.0;
            _upNextUntil = DateTime.UtcNow.AddSeconds(promptSeconds);
            _upNextShowing = true;
            ScheduleUpNextDismiss(promptSeconds);
            BringMediaPromptToMain();
            SyncUpNextContent();
        }

        /// <summary>
        /// 恢复播放提示：音乐继续播放时弹出一级胶囊，中间是曲名、右侧是"正在播放"，持续 1 秒后自动还原频谱与歌词。
        /// </summary>
        private void BeginPlayPrompt(NowPlayingInfo info)
        {
            if (!info.HasMedia) return;
            // SMTC already requires sustained player audio and, after a reported
            // pause, fresh advancing transport progress. Its resolved transition
            // can notify even when the player's native status arrives late.
            if (!_options.PlayNotifyEnabled) return;
            if (_greetingShowing && GreetingActive) return;
            if (!CanPresentMediaStatePrompt) return;

            _upNextTitle = info.DisplayTitle;
            _upNextKind = "play";
            const double promptSeconds = 1.0;
            _upNextUntil = DateTime.UtcNow.AddSeconds(promptSeconds);
            _upNextShowing = true;
            ScheduleUpNextDismiss(promptSeconds);
            BringMediaPromptToMain();
            SyncUpNextContent();
        }

        private bool CanPresentMediaStatePrompt => !ShouldHideForegroundMedia && !_sessionLocked && !_clipboardLocked
            && !_fileDragActive && !_exportingTransferItem && _approvalCurrent == null && !_layers.IsActive(ApprovalLayerKey);

        private void BringMediaPromptToMain()
        {
            // 状态事件主动提醒；普通的时间轴刷新仍保持用户选中的主岛。
            SwitchMainIsland("music");
            _layers.PreferredWinnerKey = "music";
            if (_state == IslandState.Card)
            {
                CloseComponentSwitcher();
                _notificationAutoCard = false;
                int index = _pages.IndexOf(IslandPageIds.Music);
                if (index >= 0 && _pages.ActiveIndex != index) SwitchCardPage(index);
            }
            else if (_state == IslandState.Bar) SetState(IslandState.Pill);
            SyncNotificationLayers();
            SyncSatelliteOrbs();
            TryMorphPillWidth();
        }

        private void ShowStartupGreeting()
        {
            if (_exiting || !_options.GreetingEnabled) return;

            _activeMainIslandKey = "music";
            _layers.PreferredWinnerKey = null; // 允许问候语正常独占主岛
            _layers.ShowSeconds(IslandLayerHost.GreetingKey, _options.GreetingSeconds);
            GreetingText.Text = string.IsNullOrWhiteSpace(_options.GreetingText) ? "欢迎回来" : _options.GreetingText;

            ScheduleGreetingDismiss();

            if (_state == IslandState.Bar)
            {
                SetState(IslandState.Pill);
            }
            else
            {
                SyncGreetingContent();
            }
        }

        private void ScheduleGreetingDismiss()
        {
            _greetingTimer?.Stop();
            double seconds = Math.Max(1.0, _options.GreetingSeconds);
            _greetingTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromSeconds(seconds)
            };
            _greetingTimer.Tick += (_, __) =>
            {
                _greetingTimer.Stop();
                DismissGreeting();
            };
            _greetingTimer.Start();
        }

        private void DismissGreeting()
        {
            _greetingTimer?.Stop();
            _greetingFinishedTcs?.TrySetResult(true);
            if (!_greetingShowing && !GreetingActive) return;

            _layers.Hide(IslandLayerHost.GreetingKey);
            // 欢迎结束不能抢回刚由复制等事件选中的主岛。
            if (!IsIslandActive(_activeMainIslandKey))
            {
                _activeMainIslandKey = "music";
                _layers.PreferredWinnerKey = "music";
            }

            // 若未置顶，且光标不在热区，且没有正在播放的媒体，自动平滑收回至细条
            if (!HasVisibleMusicPin && !_layers.AnyPinned && _state == IslandState.Pill && !IsCursorInHotZone())
            {
                if (!HasActiveNotification && (!_options.KeepCompactWhilePlaying || _smtc?.Current?.IsPlaying != true))
                {
                    CollapseToBar();
                    return;
                }
            }

            SyncGreetingContent();
            SyncNotificationLayers();
        }

    }
}
