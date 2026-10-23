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

// Content layer reveal driven by the main spring progress.
namespace alpha_island
{
    public partial class MainWindow
    {
        // ================= 内容层的显露（挂在主弹簧进度上，没有自己的时间轴） =================

        /// <summary>形变进度 0..1（含过冲），由 IslandAnimator.ProgressHook 每帧回传。</summary>
        private double _reveal = 1.0;
        private IslandState _revealFrom = IslandState.Bar;
        private IslandState _revealTo = IslandState.Bar;

        private void OnMorphProgress(double t)
        {
            // 胶囊内容层跟着岛体宽度走（歌词把胶囊撑开时，内容层必须同步，否则歌词拿不到多出来的空间）。
            UpdatePillLayerWidth();

            // 置顶提示也挂在岛体宽度上：岛被歌词撑宽时气泡要一起往外挪，否则会被压在岛上。
            SyncPinBubbleOffset();
            SyncSideBubbleOffset();
            SyncSatelliteOrbOffsets();
            SyncComponentHandle();

            // 只改宽度的形变（歌词伸缩）不能再跑一遍内容揭示：_reveal 从 0 重跑会让
            // 胶囊内容二次淡入，观感上就是"伸缩一下、闪一下"。
            if (_widthOnlyMorph) return;

            _reveal = t;
            ApplyReveal();
        }

        /// <summary>
        /// 所有"非宽度"形变的统一出口。
        /// 除了转发给动画器，还做两件事：
        /// ① 清掉"只改宽度"标记 —— 动画器被抢占时会直接丢弃上一个形变的完成回调，
        ///    标记只能在入口处显式清掉，否则收回 / 换态动画期间的内容揭示会被误跳过；
        /// ② 形变结束后回头补一次宽度判定 —— 形变进行中被推迟的歌词伸缩在这里落地。
        /// </summary>
        private void MorphIsland(IslandGeometry to, Duration duration, IEasingFunction? ease = null,
            Action? completed = null, IslandGeometry? via = null, double viaAt = 0.5, double followLag = 1.0)
        {
            if (_animator == null) return;

            ReserveIslandHost(to);
            if (via is { } waypoint) ReserveIslandHost(waypoint);
            _widthOnlyMorph = false;
            _animator.MorphTo(to, true, duration, ease, () =>
            {
                // 形变结束：通知图层的淡化窗口关闭，透明度归一化回 1（S3）。
                // 放在这里是因为所有形变都走这个出口 —— 包括蓝牙塌回收细条（见 CollapseToBar）。
                FitIslandHost(to);
                _layers.EndMorph();
                completed?.Invoke();
                FlushPendingPillWidth();
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(TryMorphPillWidth));
            }, via, viaAt, followLag);
        }

        /// <summary>
        /// 让胶囊内容层 (PillLayer) 的宽度跟着岛体走。
        /// XAML 里 PillLayer.Width 写死 328（= PillWidth − PillContentWidth），岛体被歌词撑宽后
        /// 必须逐帧同步，否则内容层还是窄的，多出来的宽度全成了空白。
        /// </summary>
        private void UpdatePillLayerWidth()
        {
            if (PillLayer == null || IslandBody == null) return;

            double bodyWidth = IslandBody.Width;
            if (double.IsNaN(bodyWidth) || bodyWidth <= 1.0) bodyWidth = IslandBody.ActualWidth;
            if (double.IsNaN(bodyWidth) || bodyWidth <= 1.0) return;

            double target = Math.Max(1.0, bodyWidth - (IslandMetrics.PillWidth - IslandMetrics.PillContentWidth));
            if (Math.Abs(PillLayer.Width - target) > 0.05) PillLayer.Width = target;
        }

        /// <summary>
        /// 把内容层透明度和封面球推到"当前形变进度该有的样子"。
        /// 刻意不生成任何 Opacity 动画：一旦有独立时间轴，它就会和形变错相位，
        /// 观感上变成"盖在形变上的淡入淡出"，而不是材质本身在开合。
        /// </summary>
        private void ApplyReveal()
        {
            double t = _reveal;
            double span = IslandMetrics.ContentRevealEnd - IslandMetrics.ContentRevealStart;
            double content = span <= 0 ? 1.0 : Math.Clamp((t - IslandMetrics.ContentRevealStart) / span, 0.0, 1.0);

            // 胶囊→卡片（小岛返回主岛）这一向不能用交叉淡入淡出：两层同时半透明时，
            // 各自的白字/亮块都会掉到 50%~60% 亮度，整块内容一起发灰 —— 那就是用户反复报的
            // "从小岛返回时内容闪一下"（实测：亮像素 480 → 54 → 460，跨 3~4 帧）。
            // 改成一次"硬交接"：闸门用**岛体实时高度**（而不是弹簧进度）——高度通道天生滞后于宽度，
            // 于是观感是"材质先横向摊开 → 长到装得下卡片时，内容才在模糊里化开出现"。
            // 交接前胶囊保持满不透明；交接后整层立刻交给卡片，由卡片那层 13px→0 的模糊把切换吃掉。
            // 反方向（分离）保持原样，用户已确认没问题。
            double fromFade = content;
            double toFade = content;
            double cardSharp = content;

            if (_revealTo == IslandState.Bar)
            {
                // 收回细条阶段：内容层提前在 0 ~ 25% 进度（约 70~80ms）内快速淡出并微缩放融进胶囊材质
                double collapseProgress = Math.Clamp(t / 0.25, 0.0, 1.0);
                fromFade = collapseProgress;
                toFade = 0.0;
                cardSharp = 0.0;
                if (PillLayerScale != null)
                {
                    double s = 1.0 - collapseProgress * 0.05;
                    PillLayerScale.ScaleX = s;
                    PillLayerScale.ScaleY = s;
                }
                CardCarouselHost.Opacity = 1.0 - collapseProgress;
            }
            else
            {
                if (PillLayerScale != null && (PillLayerScale.ScaleX != 1.0 || PillLayerScale.ScaleY != 1.0))
                {
                    PillLayerScale.ScaleX = 1.0;
                    PillLayerScale.ScaleY = 1.0;
                }
            }

            if (_revealFrom == IslandState.Pill && _revealTo == IslandState.Card)
            {
                double cardH = Math.Max(1.0, CardGeometry.Height);
                double bodyH = IslandBody.ActualHeight > 0 ? IslandBody.ActualHeight : IslandBody.Height;

                // 闸门取"岛体几乎长满卡片高度"（94%），不是几何中途：实测中途交接时卡片刚出现
                // 就带着 13px 模糊，整块亮内容被糊成一片灰，再叠上胶囊内容同时消失 —— 一帧之内
                // 亮度从 436 掉到 54（连拍帧 026 → 028 的字符图：清晰的封面+整行歌词变成一片淡雾）。
                // 现在胶囊内容以满不透明一直撑到岛体长满，内容交接基本落在几何形变的末尾，
                // 且交接时只带一点点模糊，观感是"内容在材质里化开"，没有发灰的空窗。
                if (!_cardHandoff && (bodyH >= cardH * 0.94 || t >= 1.0))
                {
                    _cardHandoff = true;
                    _cardHandoffMs = Environment.TickCount64;
                }

                fromFade = _cardHandoff ? 1.0 : 0.0;
                toFade = _cardHandoff ? 1.0 : 0.0;

                // 交接那一帧卡片就是满不透明（半透明会把底下的胶囊白字压成灰），
                // 但只带 ~2px 模糊、再用 ~80ms 化到锐利：模糊再大一点、再久一点，卡片的小字就会被
                // 模糊过强会降低文字亮度；按时间收敛，避免换态闪烁。
                cardSharp = _cardHandoff
                    ? Math.Clamp(0.85 + (Environment.TickCount64 - _cardHandoffMs) / 80.0 * 0.15, 0.85, 1.0)
                    : 0.0;
            }

            // 通知图层（含一级胶囊内容层 PillLayer）的透明度与显隐，全部由图层宿主按**同一个 plan** 落地（S3）：
            // 这里只把"这一拍的形变窗口"交出去（进度 0..1），不再自己维护第二份白名单。
            // 旧实现在这里抄了一份状态（_volumeNotifying && VolumeNotifyActive || _revealTo == Bar && _currentAction == ...），
            // 它认不出 vivo / download / agentstatus / approval 这几个后加的注册图层：
            // 宿主已经按 plan 把胶囊按下去了，这份白名单却在同一拍把它点亮 —— 谁后跑谁赢，
            // 就是"歌词压在通知上"与随机闪烁的根。
            // 旧写法里真正有用的语义（"塌回收细条时，正在收的那一层要跟着淡出"）
            // 改由宿主用 BeginMorph() 的"换态前可见层"快照表达，那 7 行 `_currentAction == Xxx` 特判整段删掉。
            _layers.SetReveal(fromFade, toFade);

            // 中间态托盘层（文件中转站）
            RevealLayer(FileTransferLayer, _revealFrom == IslandState.Tray, _revealTo == IslandState.Tray, fromFade, toFade);
            bool trayVisible = (_revealFrom == IslandState.Tray || _revealTo == IslandState.Tray) && FileTransferLayer.Opacity > 0.001;
            FileTransferLayer.Visibility = trayVisible ? Visibility.Visible : Visibility.Collapsed;

            RevealLayer(CardCarouselHost, _revealFrom == IslandState.Card, _revealTo == IslandState.Card, fromFade, toFade);
            if (_revealTo == IslandState.Card || _revealFrom == IslandState.Card)
            {
                CardCarouselHost.Visibility = Visibility.Visible;
            }
            // 卡片子树常驻 Visible（XAML 里就不再是 Collapsed），显露完全交给 Opacity：
            // 一旦用 Collapsed 隐藏，WPF 会丢弃整棵子树的测量缓存，下次展开又要重新冷排版一次
            // （约 15~25ms 的冷排版会拖慢换态首帧）。点击穿透由岛体裁切 +
            // CardCarouselHost 自身的 IsHitTestVisible 兜住（见 SetState / CollapseToBar）。

            // 换态期间给卡片子树盖一层动态模糊（半径随进度收敛到 0），把内容重排 / 首帧藏在模糊里。
            // 但"胶囊→卡片"的交接前卡片还完全不可见，这时挂模糊等于每帧白跑一次离屏渲染，
            // 所以那一段直接摘掉 Effect，交接发生的那一帧再挂上满强度模糊。
            if (_revealFrom == IslandState.Pill && _revealTo == IslandState.Card && !_cardHandoff)
            {
                ClearCardTransitionBlur();
            }
            else
            {
                ApplyCardTransitionBlur(cardSharp);
            }

            if (_pages.ActiveId == IslandPageIds.Music)
            {
                RevealLayer(CardWave, _revealFrom == IslandState.Card, _revealTo == IslandState.Card, fromFade, toFade);
            }
            else
            {
                CardWave.Opacity = 0;
            }
            if (_revealTo != IslandState.Card)
            {
                Keyboard.ClearFocus();
            }

            // 收回细条途中：在经过正圆阶段显现封面球/状态图标
            if (_revealTo == IslandState.Bar && _revealFrom != IslandState.Bar)
            {
                double riseSpan = IslandMetrics.OrbRevealEnd - IslandMetrics.OrbRevealStart;
                double rise = riseSpan <= 0 ? 1.0
                    : Math.Clamp((t - IslandMetrics.OrbRevealStart) / riseSpan, 0.0, 1.0);
                double fall = Math.Clamp((IslandMetrics.OrbFadeStart - t) / IslandMetrics.OrbFadeSpan, 0.0, 1.0);

                SiriOrb.Opacity = rise * fall;
                SiriOrbTranslate.X = 0;
            }

            // 细条上的常驻指示（网速）也要跟着形变进度走，否则收回途中会提前亮在卡片中间
            SyncBarIndicators();

            // 形变途中的叠层不变量（ApplyReveal 每帧都跑，这里是"零帧"级的取证点）。
            CheckPillLayerOverlapInvariant("ApplyReveal");
        }

        private static void RevealLayer(UIElement element, bool isFrom, bool isTo, double fromFade, double toFade)
        {
            element.Opacity = isTo ? (isFrom ? 1.0 : toFade) : (isFrom ? 1.0 - fromFade : 0.0);
        }

    }
}
