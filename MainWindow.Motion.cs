using System;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Services;

namespace alpha_island
{
    /// <summary>
    /// 「减弱动效」「节能模式」两个开关在窗口里的落地。
    ///
    /// 策略本身（开关状态、渲染层级、判定函数）在 <see cref="MotionPolicy"/> 里，
    /// 这里只负责三件事：把设置灌进去、按当前形态重算节能节奏、给逐帧循环一个显式守卫。
    ///
    /// 进度展示只读取本地时间估算；音频证据与 SMTC 兜底探测各自调度。
    /// 逐帧回调（频谱/波形/卡拉OK）本来就已经"只在胶囊/卡片态挂"，节能模式把这件事实
    /// 从隐含写法变成显式契约，防止以后新增循环时漏掉。
    /// </summary>
    public partial class MainWindow
    {
        private const string MediaEvidenceTickerKey = "media-evidence";
        private const string IdleHoverTickerKey = "idle-hover";

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
            IslandLog.Debug("Motion", $"动效策略 = {MotionPolicy.Describe()}");
        }

        /// <summary>
        /// 展开播放时保持平滑进度；暂停或收起时降频，无媒体时停表。
        /// 音频证据保持独立的 250ms 周期，避免暂停后漏掉播放器恢复播放。
        /// </summary>
        private void ApplyPowerSavePacing()
        {
            SyncResourceSampling();
            if (_progressTimer == null) return;

            var info = _smtc?.Current;
            bool hasMedia = info?.HasMedia == true
                && info.State is not PlaybackState.Closed and not PlaybackState.Stopped;
            TimeSpan want = MediaWorkPacing.ProgressInterval(info?.IsPlaying == true,
                _state != IslandState.Bar, MotionPolicy.PowerSave);
            bool faster = want < _progressTimer.Interval;
            if (_progressTimer.Interval != want) _progressTimer.Interval = want;

            if (_runtimeEnabled && !_exiting)
            {
                if (hasMedia)
                {
                    if (!_progressTimer.IsEnabled) _progressTimer.Start();
                    if (_ticker != null && !_ticker.IsRegistered(MediaEvidenceTickerKey))
                        _ticker.Register(MediaEvidenceTickerKey, MediaWorkPacing.AudioEvidenceInterval,
                            UpdateMediaAudioEvidence);
                }
                else
                {
                    _progressTimer.Stop();
                    _ticker?.Unregister(MediaEvidenceTickerKey);
                }
                if (_state != IslandState.Bar || HasActiveNotification) SyncHoverPacing();
            }
            if (faster && _progressTimer.IsEnabled) ProgressTimer_Tick(this, EventArgs.Empty);
        }

        // 收起后复用已有低频心跳，保留缝隙热区检测；展开与动画期间立即恢复原节奏。
        private void SyncHoverPacing()
        {
            if (!_runtimeEnabled || !IsLoaded || _exiting || _hoverTimer == null || _ticker == null) return;
            bool idle = _state == IslandState.Bar && !HasActiveNotification && !_fileDragActive
                && !ComponentSwitchGestureActive && _animator?.IsRunning != true && !_widthOnlyMorph;
            if (idle)
            {
                _hoverTimer.Stop();
                if (!_ticker.IsRegistered(IdleHoverTickerKey))
                    _ticker.Register(IdleHoverTickerKey, TimeSpan.FromMilliseconds(500),
                        () => HoverTimer_Tick(this, EventArgs.Empty));
            }
            else
            {
                _ticker.Unregister(IdleHoverTickerKey);
                if (!_hoverTimer.IsEnabled) _hoverTimer.Start();
            }
        }

        /// <summary>
        /// 现在这个形态下能不能挂逐帧循环。纯装饰性循环（频谱/波形/逐字歌词）在收起态一律不挂：
        /// 细条上没有它们的位置，挂着只是白烧 CPU。
        /// </summary>
        private bool AllowsFrameLoop() => MotionPolicy.AllowsFrameLoop(_state == IslandState.Bar);
    }
}
