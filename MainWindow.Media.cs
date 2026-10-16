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

// Media data, spectrum push, playback progress and transport controls.
// Moved verbatim out of the former single-file MainWindow.xaml.cs; no logic changes.
namespace alpha_island
{
    public partial class MainWindow
    {
        // ================= 媒体 =================

        /// <summary>调试钩子（ALPHA_ISLAND_FAKE_MEDIA=1）：用合成「正在播放」顶替真实 SMTC 会话。</summary>
        private bool _fakeMediaForDebug;
        
        

        private void SyncPausedMediaVisibility()
        {
            bool hidden = ShouldHidePausedMedia || ShouldHideForegroundMedia || GameDoNotDisturbActive && !_isPinned;
            bool wasHidden = _workspace.Media.Hidden;
            _workspace.Media.Hidden = hidden;
            if (hidden)
            {
                _upNextTimer?.Stop();
                _upNextUntil = DateTime.MinValue;
                _upNextShowing = false;
                SyncUpNextContent();
                HideMusicOrb(immediate: true);
            }
            SyncNotificationLayers();
            if (hidden && _state == IslandState.Pill && !_clipboardLocked
                && !_layers.AnyPinned && !HasActiveNotification && !IsIslandActive(_activeMainIslandKey))
            {
                CollapseToBar();
            }
            else if (wasHidden && !hidden && HasVisibleMusicPin && _state == IslandState.Bar)
            {
                SetState(IslandState.Pill);
            }
        }

        internal void EnableFakeMediaForDebug()
        {
            _fakeMediaForDebug = true;
            ApplyFakeMediaForDebug();
            IslandLog.Debug("Media", "调试钩子：媒体改用合成数据（合成曲目，不需要真的开播放器）");
        }

        /// <summary>
        /// 注入合成曲目。SMTC 服务可能比调试钩子晚创建（服务按开关启动），
        /// 所以 <see cref="StartSmtcService"/> 建完实例后也会再调一次这里。
        /// </summary>
        private void ApplyFakeMediaForDebug()
        {
            if (!_fakeMediaForDebug) return;
            _smtc?.SetDebugNowPlaying(new NowPlayingInfo
            {
                Title = "合成曲目（调试）",
                Artist = "Alpha Island",
                Album = "图层验证用",
                SourceApp = "alpha-island-debug",
                State = PlaybackState.Playing,
                Position = TimeSpan.FromSeconds(42),
                Duration = TimeSpan.FromMinutes(3),
                CanNext = true,
                CanPrevious = true,
                CanSeek = true,
            });
        }

        private void OnNowPlayingChanged(object? sender, NowPlayingInfo? info)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnNowPlayingChanged(sender, info)));
                return;
            }

            ApplyPowerSavePacing();
            if (_runtimeEnabled && info?.HasMedia == true) StartAudioSpectrum();

            if (info == null || !info.HasMedia || info.State is PlaybackState.Closed or PlaybackState.Stopped)
            {
                // SMTC 在换曲瞬间会短暂报"无会话"，立刻清空会闪；延迟 1.2s 再确认。
                if (_mediaLostTimer?.IsEnabled == false) _mediaLostTimer.Start();
                return;
            }

            _mediaLostTimer?.Stop();
            ApplyMedia(info);
        }

        private void ApplyMedia(NowPlayingInfo info)
        {
            var transition = _workspace.Media.Preview(info);
            bool playbackChanged = transition.PlaybackChanged;
            _mediaWindows?.SetSource(info.SourceApp);
            TitleText.Text = info.DisplayTitle;
            ArtistText.Text = string.IsNullOrWhiteSpace(info.Artist) && (info.ContentKind != MediaContentKind.Music || MediaSourceNames.IsBrowser(info.SourceApp))
                ? info.ContentKind == MediaContentKind.Video ? "视频播放" : "网页媒体" : info.DisplayArtist;
            AlbumText.Text = info.SourceName
                + (string.IsNullOrWhiteSpace(info.Album) ? "" : " · " + info.Album);
            MediaSourcesButton.ToolTip = "选择媒体来源 · 当前：" + info.SourceName;
            PlayPauseButton.IsEnabled = info.CanToggle;

            var art = info.Artwork;
            CardCoverBrush.ImageSource = art;
            PillCoverBrush.ImageSource = art;
            ApplyArtwork(art);
            CardMediaPlaceholder.Visibility = art == null ? Visibility.Visible : Visibility.Collapsed;
            CardMediaPlaceholder.Text = info.ContentKind == MediaContentKind.Video ? "\uE714" : "\uE8D6";

            PlayPauseGlyph.Shape = IconShape.PlayPause;
            PlayPauseGlyph.Active = info.IsPlaying;
            PlayPauseGlyph.Text = info.IsPlaying ? "\uE769" : "\uE768";
            PrevButton.IsEnabled = info.CanPrevious;
            NextButton.IsEnabled = info.CanNext;
            PlaybackOrderButton.Visibility = MediaPlaybackOrders.Supported(info).Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            PlaybackOrderButton.Content = MediaPlaybackOrders.Glyph(info.PlaybackOrder);
            PlaybackOrderButton.ToolTip = info.PlaybackOrderKnown ? MediaPlaybackOrders.Name(info.PlaybackOrder) + "（点击切换）" : "切换播放模式";
            ProgressTrack.Cursor = info.CanSeek ? Cursors.Hand : Cursors.Arrow;
            ProgressTrack.Opacity = info.CanSeek ? 1.0 : 0.6;

            if (_upNextShowing && (_upNextKind == "play" && !info.IsPlaying || _upNextKind == "pause" && info.IsPlaying))
            {
                _upNextTimer?.Stop(); _upNextShowing = false; _upNextUntil = DateTime.MinValue;
                SyncUpNextContent();
            }
            if (!info.IsPlaying)
            {
                PillSpectrum.Clear();
                CardWave.Clear();
                StopKaraoke();
            }
            else
            {
                UpdateKaraokeHook();
            }

            if (_lyricsDoc == null) SetPillLyric(info.DisplayTitle);

            bool trackChanged = transition.TrackChanged;
            if (trackChanged || _lastIsPlaying != info.IsPlaying) _mediaWindows?.RefreshSnapshot();

            if (trackChanged)
            {
                _lyricsCts?.Cancel(); ApplyLyrics(null);
                _lastFetchDuration = info.Duration;
                StartLyricsFetch(info);

                // 换曲提示：弹出细条→一级胶囊，中间显示新曲名、右侧"接下来"，过一会儿自己收回
                // _sawMedia 保证只有"真的换了一首"才弹，开机第一首不弹。
                if (info.IsPlaying && _sawMedia) BeginUpNext(info);
            }
            else if (_options.LyricsEnabled && info.SupportsLyrics && LyricsIdentity(info) != _lyricsKey)
            {
                // Corrected duration/album invalidates an earlier request or cached version.
                _lastFetchDuration = info.Duration;
                StartLyricsFetch(info);
            }
            // Playback notifications are independent of metadata/lyrics refreshes;
            // a duration update must not consume the same playback transition.
            if (_sawMedia && _lastIsPlaying == true && !info.IsPlaying && _options.PauseNotifyEnabled)
            {
                // 播放中暂停提示：与换曲提示形态一致，中间为当前曲名、右侧为“已暂停”
                _lyricIndex = -2;
                BeginPausePrompt(info);
            }
            else if (_sawMedia && _lastIsPlaying != true && info.IsPlaying && _options.PlayNotifyEnabled
                && (!trackChanged || !_options.UpNextEnabled))
            {
                // 暂停后恢复播放提示：中间为当前曲名、右侧为“正在播放”
                _lyricIndex = -2;
                BeginPlayPrompt(info);
            }

            _workspace.Media.Commit(info);
            SyncPausedMediaVisibility();
            SyncVisualizers();
            // A visible card does not reload its icon when only playback state changes.
            // Timeline / metadata refreshes keep the same state and must not replay this cue.
            if (playbackChanged) ActionIconMotion.Play(PlayPauseGlyph);
        }

        private void ApplyNoMedia()
        {
            PlaybackOrderButton.Visibility = Visibility.Collapsed;
            if (_runtimeEnabled)
            {
                // This path runs only after the existing 1.2s loss confirmation, not between tracks.
                StopSpectrumService();
                _services.MarkStopped(IslandServiceIds.Spectrum);
            }
            _mediaWindows?.SetSource(null);
            _workspace.Media.Reset();
            _lyricsKey = "";
            _lyricsFetchingKey = "";
            _lastFetchDuration = TimeSpan.Zero;
            ApplyLyrics(null);

            TitleText.Text = "未在播放";
            ArtistText.Text = "";
            AlbumText.Text = "";
            CardCoverBrush.ImageSource = null;
            PillCoverBrush.ImageSource = null;
            ApplyArtwork(null);
            CardMediaPlaceholder.Visibility = Visibility.Visible;
            CardMediaPlaceholder.Text = "\uE8D6";
            _upNextUntil = DateTime.MinValue;
            _upNextShowing = false;
            SetPillLyric("Alpha Island");
            SyncUpNextContent();
            PlayPauseGlyph.Shape = IconShape.PlayPause;
            PlayPauseGlyph.Active = false;
            PlayPauseGlyph.Text = "\uE768";
            PlayPauseButton.IsEnabled = false;
            PrevButton.IsEnabled = false;
            NextButton.IsEnabled = false;
            PositionText.Text = "0:00";
            DurationText.Text = "0:00";
            SetMusicProgress(0);
            HideMusicOrb(immediate: true);
            if (MusicOrbCoverBrush != null) MusicOrbCoverBrush.ImageSource = null;

            // 媒体结束只撤下音乐，不能连带收起正在常驻的剪切板或其它独立岛。
            _isPinned = false;
            _activeSatelliteOrder.Remove("music");
            HidePinBubble(immediate: true);
            SyncNotificationLayers();
            if (_state == IslandState.Pill && !HasActiveNotification && !_layers.AnyPinned)
                CollapseToBar();
        }

        /// <summary>
        /// 封面统一处理：① 回缩成球时球里显示专辑封面（用户要求换掉固定图标）；
        /// ② 从封面提取主色调，给一级胶囊的频谱柱和完全展开的流体波浪上色。
        /// 用引用比较去重 —— SMTC 元数据抖动很频繁，不要每次都重算调色板（要遍历 1024 个像素）。
        /// </summary>
        private void ApplyArtwork(BitmapSource? art)
        {
            if (ReferenceEquals(art, _lastArt)) return;
            _lastArt = art;

            OrbCoverBrush.ImageSource = art;
            double coverOn = art != null ? 1.0 : 0.0;
            OrbCover.Opacity = coverOn;
            OrbCoverRing.Opacity = coverOn;
            OrbGradient.Opacity = 1.0 - coverOn;

            if (MusicOrbCoverBrush != null)
            {
                MusicOrbCoverBrush.ImageSource = art;
                MusicOrbCover.Opacity = coverOn;
                MusicOrbGradient.Opacity = 1.0 - coverOn;
            }

            // 可选的 3D 封面（C 组④）：同一张封面同步进 3D 场景并按需播一次翻转；
            // 2D 画刷照旧更新，关掉开关切回 2D 时不用重新取图。
            SyncCover3DArt(art);

            var palette = CoverPalette.From(art);

            // Copilot 原型的四色阶（暖橙红 / 洋红 / 紫罗兰 / 蓝）按封面主色相旋转生成；
            // 每根柱子/每一层波浪自己的 alpha 由控件内部按原型常量叠加，这里给满不透明原色。
            var ramp = palette.Ramp.Length >= 4 ? palette.Ramp : CoverPalette.DefaultRamp;

            PillSpectrum.ColorLow = ramp[0];
            PillSpectrum.ColorMid = ramp[1];
            PillSpectrum.ColorHigh = ramp[2];
            PillSpectrum.ColorExtra = ramp[3];

            CardWave.ColorLow = ramp[0];
            CardWave.ColorMid = ramp[1];
            CardWave.ColorHigh = ramp[2];
            CardWave.ColorExtra = ramp[3];
        }

        // ================= 频谱 =================

        /// <summary>采集线程回调：PushFrame 内部加锁拷贝，可以安全跨线程。</summary>
        private void OnSpectrumUpdated(ReadOnlySpan<float> bands)
        {
            if (bands.IsEmpty || _exiting) return;

            // 无论当前是 Pill 还是 Card，都直接推入最新频谱帧
            // SpectrumVisualizer 内部由 UI 线程的 IsActive 控制渲染循环，未激活时不消耗任何 CPU
            PillSpectrum.PushFrame(bands);
            CardWave.PushFrame(bands);
        }

        // ================= 进度 =================

        private void UpdateMediaAudioEvidence()
        {
            var media = _smtc;
            if (media != null)
            {
                // 音频侧仲裁：环回连续采不到声音时，把播放器上报的 Playing 降级为 Paused。
                // probeUsable 必须同时满足两条，缺一不可，否则正在放的歌会被误判成暂停：
                //   ① 系统没静音、音量也没拉到 0（此时环回本来就是静音，放的是歌但听不到）；
                //   ② 不在切换输出设备后的采集重建窗口里。
                // 另外"回调长时间完全没有了"同样等于没在出声：蓝牙/独占设备在暂停后会整个停掉
                // 采集回调，此时 SilentSeconds 不再增长（停在暂停那一刻）、SinceCallbackMs 一路涨
                // 上去——这正是"暂停后仍被判为播放、歌词继续滚、频谱只剩一条线"的病根，
                // 所以把回调静默也当作静音证据。两个量反相关（回调活着且安静时 SinceCallbackMs
                // 只有几十毫秒），因此绝不能取两者最小值，只能二选一。
                var audio = _audio;
                // Own-app session evidence also works while Bluetooth loopback capture rebuilds.
                media.AudioProbeEnabled = audio != null || _volumeService != null;
                {
                    // 证据①（首选）：**播放器自己那个音频会话**的电平。暂停时那个会话还在，
                    // 但输出的是数字静音 → 峰值≈0；按了播放而播放器没上报时，峰值又会立刻起来。
                    // 这条证据不受"别的应用在出声""系统音量拉到 0""系统静音""刚切过输出设备"影响，
                    // 所以只有它能做双向纠正（既能把谎报的 Playing 降级，也能把谎报的 Paused 升格）。
                    // 用户反复报的"暂停后歌词继续滚 / 频谱一条线 / 岛里还显示播放"，根子就是只靠端点环回。
                    string? mediaApp = media.Current?.SourceApp;
                    string evidenceSession = mediaApp + "|" + media.Current?.SessionId;
                    if (_audioEvidenceSession != evidenceSession || _audioEvidenceEpoch != media.AudioEvidenceEpoch)
                    {
                        _audioEvidenceSession = evidenceSession;
                        _audioEvidenceEpoch = media.AudioEvidenceEpoch;
                        _appSilentSinceMs = _appActiveSinceMs = 0;
                        _volumeService?.RequestRefresh();
                    }
                    float appPeak = mediaApp != null && _volumeService != null
                        ? _volumeService.GetPlayerSessionPeak(mediaApp)
                        : -1f;
                    appPeak = media.ReadPlayerSessionPeak(appPeak);
                    long nowMs = Environment.TickCount64;
                    bool playerEvidence = appPeak >= 0f;
                    double playerSilentSeconds = 0.0;
                    double playerActiveSeconds = 0.0;
                    if (playerEvidence)
                    {
                        if (appPeak < AudioSessionSilencePeak)
                        {
                            if (_appSilentSinceMs == 0) _appSilentSinceMs = nowMs;
                            _appActiveSinceMs = 0;
                        }
                        else
                        {
                            if (_appActiveSinceMs == 0) _appActiveSinceMs = nowMs;
                            _appSilentSinceMs = 0;
                        }
                        playerSilentSeconds = _appSilentSinceMs == 0 ? 0.0 : (nowMs - _appSilentSinceMs) / 1000.0;
                        playerActiveSeconds = _appActiveSinceMs == 0 ? 0.0 : (nowMs - _appActiveSinceMs) / 1000.0;
                    }
                    else
                    {
                        _appSilentSinceMs = 0;
                        _appActiveSinceMs = 0;
                    }

                    // 证据②（兜底，只在拿不到会话电平时用）：整块声卡的环回电平，只能证明"全系统都没出声"。
                    // 两点实测结论决定了这里的判据：
                    //   · 环回采集**不受端点静音/音量影响**（系统 muted + vol=0 且正在放歌时依然采到 -17dB），
                    //     所以不再用"系统是否静音"去禁用这份证据 —— 那会在 muted 时把 veto 整个关掉；
                    //   · 回调长时间完全没有（蓝牙/独占设备暂停后停掉回调）等同于没在出声，
                    //     而回调活着且安静时 SinceCallbackMs 只有几十毫秒，两者反相关，只能二选一。
                    double endpointSilentSeconds = 0.0;
                    bool endpointUsable = false;
                    if (audio != null)
                    {
                        double sinceCallbackMs = audio.SinceCallbackMs;
                        endpointUsable = DateTime.UtcNow >= _audioDeviceSuppressUntil;
                        endpointSilentSeconds = sinceCallbackMs >= 3000.0
                                                ? sinceCallbackMs / 1000.0
                                                : audio.SilentSeconds;
                    }

                    media.ReportAudioEvidence(playerSilentSeconds, playerActiveSeconds, playerEvidence,
                                              endpointSilentSeconds, endpointUsable);

                    long nowDbg = Environment.TickCount64;
                    if (_smtcDebugEnabled && IslandLog.IsEnabled(IslandLogLevel.Debug) && nowDbg - _dbgLastMs >= 1000)
                    {
                        _dbgLastMs = nowDbg;
                        var cur = media.Current;
                        IslandLog.Debug("SMTC_DBG",
                            $"SMTC state={cur?.State} reported={media.ReportedState} playing={cur?.IsPlaying} hasMedia={cur?.HasMedia} veto={media.IsAudioVetoLatched} active={media.IsAudioActiveLatched} probe={media.AudioProbeEnabled} " +
                            $"src={cur?.SourceApp} title={cur?.Title} " +
                            $"apppeak={(appPeak >= 0f ? appPeak.ToString("F4") : "n/a")} appSilent={(_appSilentSinceMs == 0 ? 0.0 : (Environment.TickCount64 - _appSilentSinceMs) / 1000.0):F2} appActive={(_appActiveSinceMs == 0 ? 0.0 : (Environment.TickCount64 - _appActiveSinceMs) / 1000.0):F2} " +
                            $"silent={audio?.SilentSeconds:F2} sinceCb={audio?.SinceCallbackMs:F0} peakdb={audio?.LastSignalDbPeak:F1} vol={_volumeService?.CurrentPercent} muted={_volumeService?.IsMuted} " +
                            $"suppress={(DateTime.UtcNow < _audioDeviceSuppressUntil ? 1 : 0)} karaoke={_karaokeHooked} island={_state} pos={media.EstimatedPosition.TotalSeconds:F2} rawpos={cur?.Position.TotalSeconds:F2}");
                    }
                }

            }
        }

        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            var media = _smtc;
            var info = media?.Current;
            if (media == null || info == null || !info.HasMedia) return;

            TimeSpan position = media.EstimatedPosition;
            TimeSpan duration = info.Duration;

            PositionText.Text = FormatTime(position);
            DurationText.Text = FormatTime(duration);

            double ratio = duration.TotalSeconds > 0.5
                ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1)
                : 0;
            SetMusicProgress(ratio);

            // 暂停拖动进度、恢复播放或未挂逐帧钩子时，同样同步当前句与逐字高亮。
            if (!_karaokeHooked && (_state == IslandState.Pill || _state == IslandState.Card))
                OnKaraokeTick(this, EventArgs.Empty);
        }

        private static string FormatTime(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        private async void ProgressTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var media = _smtc;
            var info = media?.Current;
            if (media == null || info == null || !info.CanSeek || ProgressTrack.ActualWidth <= 0) return;

            double ratio = Math.Clamp(e.GetPosition(ProgressTrack).X / ProgressTrack.ActualWidth, 0, 1);
            await media.SeekAsync(info.Duration * ratio);
        }

        // ================= 传输控制 =================

        private async void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            if (_smtc != null) await _smtc.PreviousAsync();
        }

        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_smtc != null) await _smtc.TogglePlayPauseAsync();
        }

        private async void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_smtc != null) await _smtc.NextAsync();
        }
        private async void PlaybackOrderButton_Click(object sender, RoutedEventArgs e)
        {
            if (_smtc == null || !PlaybackOrderButton.IsEnabled) return;
            PlaybackOrderButton.IsEnabled = false;
            try
            {
                if (!await _smtc.CyclePlaybackOrderAsync())
                    PlaybackOrderButton.ToolTip = "播放器未接受模式切换，请在播放器中确认是否支持";
            }
            finally { PlaybackOrderButton.IsEnabled = true; }
        }

    }
}
