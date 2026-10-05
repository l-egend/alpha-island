using System;
using alpha_island.Animation;
using alpha_island.Core;

namespace alpha_island
{
    /// <summary>
    /// 「减弱动效」「节能模式」两个开关在窗口里的落地。
    ///
    /// 策略本身（开关状态、渲染层级、判定函数）在 <see cref="MotionPolicy"/> 里，
    /// 这里只负责三件事：把设置灌进去、按当前形态重算节能节奏、给逐帧循环一个显式守卫。
    ///
    /// 节能模式真正省电的杠杆只有一个：<c>_progressTimer</c>。它每 100ms 同步问一次
    /// 音频会话（见 <c>ProgressTimer_Tick</c>），是收起态最重的常驻工作；
    /// 逐帧回调（频谱/波形/卡拉OK）本来就已经"只在胶囊/卡片态挂"，节能模式把这件事实
    /// 从隐含写法变成显式契约，防止以后新增循环时漏掉。
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>常态进度轮询间隔（连同胶囊内进度条与音频仲裁一起用）。</summary>
        private static readonly TimeSpan ProgressIntervalNormal = TimeSpan.FromMilliseconds(100);

        /// <summary>节能模式下收起态的进度轮询间隔。收起态只关心"还在不在放"，1s 足够。</summary>
        private static readonly TimeSpan ProgressIntervalPowerSave = TimeSpan.FromSeconds(1);

        /// <summary>
        /// 把设置里的两个开关灌进 <see cref="MotionPolicy"/>，并按当前形态重算节能节奏。
        /// 由 <c>ApplyOptions()</c> 调用：启动一次，之后每次设置变更一次。
        /// </summary>
        private void ApplyMotionPolicy()
        {
            MotionPolicy.ProbeRenderTier();
            MotionPolicy.Apply(
                SettingsRegistry.GetBool(_options, "ReduceMotionEnabled", false),
                SettingsRegistry.GetBool(_options, "PowerSaveEnabled", false));
            if (MotionPolicy.InstantMorph || MotionPolicy.PowerSave)
            {
                ResetLyricPresentation();
                SetMusicProgressHovered(_musicProgressHovered);
                SetVolumeTrackHovered(CardVolumeSliderTrack, CardVolumeSliderTrack.IsMouseOver || ReferenceEquals(_volumeDragTrack, CardVolumeSliderTrack));
                SetVolumeTrackHovered(VolumeTrackContainer, VolumeTrackContainer.IsMouseOver || ReferenceEquals(_volumeDragTrack, VolumeTrackContainer));
                _componentMenu?.Reveal(false);
            }
            ApplyPowerSavePacing();
            IslandLog.Debug("Motion", "动效策略 = " + MotionPolicy.Describe());
        }

        /// <summary>
        /// 节能模式的落点：细条（收起）态把进度轮询从 100ms 降频到 1s。
        ///
        /// 两个细节：
        ///   * 间隔没变就直接返回 —— <c>ApplyOptions()</c> 会被各种设置变更反复调用，
        ///     每次都给 DispatcherTimer 赋同一个值会白白重置它的计时；
        ///   * 从节能档回到常态档时补跑一拍：否则展开后进度条/歌词要等下一拍才动，
        ///     看起来像"展开瞬间卡了一下"。
        /// </summary>
        private void ApplyPowerSavePacing()
        {
            if (_progressTimer == null) return;

            bool slow = MotionPolicy.PowerSave && _state == IslandState.Bar;
            TimeSpan want = slow ? ProgressIntervalPowerSave : ProgressIntervalNormal;
            if (_progressTimer.Interval == want) return;

            _progressTimer.Interval = want;
            IslandLog.Debug("Motion", $"节能模式：进度轮询 = {want.TotalMilliseconds:F0}ms（形态={_state}）");

            if (!slow && _progressTimer.IsEnabled) ProgressTimer_Tick(this, EventArgs.Empty);
        }

        /// <summary>
        /// 现在这个形态下能不能挂逐帧循环。纯装饰性循环（频谱/波形/逐字歌词）在收起态一律不挂：
        /// 细条上没有它们的位置，挂着只是白烧 CPU。
        /// </summary>
        private bool AllowsFrameLoop() => MotionPolicy.AllowsFrameLoop(_state == IslandState.Bar);
    }
}
