using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using alpha_island.Core;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace alpha_island.Services
{
    /// <summary>播放状态（与 WinRT 的 GlobalSystemMediaTransportControlsSessionPlaybackStatus 对应）。</summary>
    public enum PlaybackState
    {
        Closed = 0,
        Opened = 1,
        Changing = 2,
        Stopped = 3,
        Playing = 4,
        Paused = 5
    }

    /// <summary>一次「当前正在播放」的完整快照，UI 只依赖这个不可变对象。</summary>
    public sealed record NowPlayingInfo
    {
        public string Title { get; init; } = "";
        public string Artist { get; init; } = "";
        public string Album { get; init; } = "";
        /// <summary>来源应用（如 Spotify.exe / CloudMusic.exe）</summary>
        public string SourceApp { get; init; } = "";
        public string SessionId { get; init; } = "";
        public MediaContentKind ContentKind { get; init; }
        public string SourceName => MediaSourceNames.DisplayName(SourceApp);
        public bool SupportsLyrics => ContentKind == MediaContentKind.Music;
        public bool CanToggle { get; init; } = true;
        public BitmapSource? Artwork { get; init; }
        public PlaybackState State { get; init; }
        public TimeSpan Position { get; init; }
        public TimeSpan Duration { get; init; }
        public bool CanNext { get; init; }
        public bool CanPrevious { get; init; }
        public bool CanSeek { get; init; }
        public bool CanShuffle { get; init; }
        public bool CanRepeat { get; init; }
        public MediaPlaybackOrder PlaybackOrder { get; init; }
        public bool PlaybackOrderKnown { get; init; }
        public bool PlaybackStateInferred { get; init; }

        public bool HasMedia => !string.IsNullOrWhiteSpace(Title) || Artwork != null;
        public bool IsPlaying => State == PlaybackState.Playing;

        public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "未知媒体" : Title;
        public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? SourceName : Artist;
    }

    /// <summary>
    /// 全局系统媒体传输控件（SMTC）监听。
    /// 依赖 Windows.Media.Control 的 WinRT 投影 —— 项目 TFM 必须是 net8.0-windows10.0.19041.0 及以上。
    /// 所有事件都统一派发到创建时传入的 Dispatcher，UI 侧不必再关心线程。
    /// </summary>
    public sealed class SmtcService : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _session;
        private readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> _watched = new();
        private readonly Dictionary<string, long> _activated = new();
        private readonly Dictionary<string, string> _titles = new();
        private long _activationOrder;
        private string? _selectedSession;
        public IReadOnlyList<MediaSessionInfo> Sessions { get; private set; } = Array.Empty<MediaSessionInfo>();
        public string? SelectedSessionId => _selectedSession;
        public string? ActiveSessionId => _session == null ? null : SessionId(_session);
        public void SelectSession(string? id) { _selectedSession = id; QueueRefresh(); }

        private bool _refreshing;
        private bool _refreshPending;
        private bool _disposed;
        private long _metadataVersion;
        private Task _refreshTask = Task.CompletedTask;
        private CancellationTokenSource? _metadataCancellation;
        private long _artworkVersion;
        private CancellationTokenSource? _artworkCancellation;
        private PlaybackState? _commandState;
        private long _commandUntilMs;
        private long _commandVersion;
        private bool _commandAcknowledged;
        private long _pendingCommandVersion;
        private string? _pendingCommandSession;
        private PlaybackState _pendingCommandState;
        private TimeSpan _pausePosition;
        private DateTime _pauseStampUtc;
        private bool _pausedTimelineAdvancing, _playerQuietAfterPause;
        private long _playerAudioUntilMs;
        private TimeSpan _nativeTimelinePosition;
        private DateTimeOffset _nativeTimelineStamp;
        private long _nativeTimelineSampleMs, _timelinePlayingUntilMs;
        private readonly IslandTimer _metadataProbe;
        private readonly DispatcherTimer _metadataEvents;
        private bool _artworkLoading;
        /// <summary>会话、曲目或控制命令改变后，UI 应重新开始累计音频证据。</summary>
        internal long AudioEvidenceEpoch { get; private set; }

        private MediaAudioSessionProbe? _streamProbe;
        private readonly Func<MediaStreamObservation?> _readStreamObservation;
        private DateTimeOffset _appliedTimelineStamp;
        private TimeSpan _appliedNativePosition;
        private NowPlayingInfo? _current;

        /// <summary>
        /// 调试钩子用的合成「当前播放」（ALPHA_ISLAND_FAKE_MEDIA）。非 null 时它优先于真实会话，
        /// 于是不需要真的开着播放器，就能走通音乐胶囊 / 音乐圆球 / 封面 / 歌词这些路径。
        /// 传 null 恢复真实会话。
        /// </summary>
        private NowPlayingInfo? _debugOverride;

        private string _artworkKey = "";
        private BitmapSource? _artwork;
        private DateTime _positionStampUtc = DateTime.UtcNow;
        private double _playbackRate = 1.0;

        /// <summary>播放器原样上报的状态（"真相"），与经音频仲裁后对外呈现的 <see cref="Current"/> 区分开。</summary>
        private PlaybackState _reportedState = PlaybackState.Closed;

        /// <summary>已因"持续采不到音频"把上报的 Playing 降级为 Paused（锁存，直到真的重新采到声音）。</summary>
        private volatile bool _audioVetoLatched;
        private volatile bool _audioActiveLatched;

        /// <summary>
        /// 音频仲裁诊断日志的节流闸门：只有"结论与上次打印的不同"且距上次打印 ≥
        /// <see cref="AudioLogMinIntervalMs"/> 才写一行。仲裁正常时本来就只在锁存翻转那一下打印，
        /// 这道闸是为了让"锁存被别的路径每拍清零"这类抖动最多也只留下 2 秒一行的痕迹。
        /// </summary>
        private bool _audioLogVeto;
        private bool _audioLogActive;
        private long _audioLogStampMs = long.MinValue / 2;

        /// <summary>音频仲裁诊断日志的最小间隔（毫秒）。</summary>
        private const int AudioLogMinIntervalMs = 2000;

        /// <summary>参数为 null 表示当前没有任何媒体会话。</summary>
        public event EventHandler<NowPlayingInfo?>? NowPlayingChanged;
        /// <summary>SMTC 初始化失败时触发，参数为错误描述。</summary>
        public event EventHandler<string>? Failed;

        public NowPlayingInfo? Current => _debugOverride ?? _current;
        public bool IsAvailable => _manager != null;

        /// <summary>
        /// 连续多久采不到任何音频电平，就把播放器上报的 Playing 视为不可信、对外降级为 Paused。
        /// 1.5s 的取舍：既不会把间奏误判成暂停，又能让异常未上报暂停的播放器在极短时间内被修正。
        /// </summary>
        public TimeSpan AudioSilenceTimeout { get; set; } = TimeSpan.FromSeconds(1.5);

        /// <summary>
        /// 播放器自身会话连续 400ms 有声，才纠正迟到的 Paused；其他应用出声不能触发升级。
        /// </summary>
        public TimeSpan AudioActiveConfirmTime { get; set; } = TimeSpan.FromSeconds(0.4);

        /// <summary>
        /// 是否启用音频侧仲裁。频谱采集没起来时必须保持 false，
        /// 否则"静音计时"永不刷新，会把真实播放全部误判成暂停。
        /// </summary>
        public bool AudioProbeEnabled { get; set; }

        /// <summary>诊断用：当前是否正因"持续采不到音频"而压制播放器上报的 Playing。</summary>
        public bool IsAudioVetoLatched => _audioVetoLatched;

        /// <summary>诊断用：当前是否正因"播放器会话持续有电平"而把上报的 Paused 升格为 Playing。</summary>
        public bool IsAudioActiveLatched => _audioActiveLatched;

        /// <summary>诊断用：播放器最近一次上报的原始状态（未经音频仲裁折算）。</summary>
        public PlaybackState ReportedState => _reportedState;

        /// <summary>
        /// 由 UI 侧按固定节奏（常态 100ms，见 <c>MainWindow.ProgressIntervalNormal</c>；节能模式收起态 1s）
        /// 回报音频侧观测结果。这里有两类证据，可信度不同：
        /// <list type="bullet">
        /// <item><b>会话级</b>（<paramref name="playerEvidenceAvailable"/> = true）：播放器**自己那个**
        /// 音频会话的电平。它既能证明"没出声"（暂停），也能证明"在出声"（用户按了播放但播放器没上报），
        /// 而且不受别的应用出声、系统音量、系统静音影响 —— 所以只有它能做双向纠正。</item>
        /// <item><b>端点级</b>（<paramref name="endpointEvidenceUsable"/>）：整块声卡的环回电平。
        /// 只能证明"整个系统都没出声"（→ 降级），不能证明"是它在出声"（别的应用出声会误升级），
        /// 所以只用于降级。</item>
        /// </list>
        /// 为什么锁存而不是每拍重算：一旦降级（或升格），<c>_current.State</c> 就变了，
        /// 若判据再依赖它就会与轮询周期形成来回抖动；锁存后唯一解除条件是相反的观测出现。
        /// 只有 Playing / Paused 参与音频仲裁，其他状态保持播放器的明确上报。
        /// </summary>
        public void ReportAudioEvidence(double playerSilentSeconds, double playerActiveSeconds,
                                        bool playerEvidenceAvailable,
                                        double endpointSilentSeconds, bool endpointEvidenceUsable)
        {
            if (_disposed || !AudioProbeEnabled || MediaSourceNames.IsBrowser(Current?.SourceApp ?? "") || Current?.ContentKind == MediaContentKind.Video) return;

            bool silent = playerEvidenceAvailable
                ? playerSilentSeconds >= AudioSilenceTimeout.TotalSeconds
                : (endpointEvidenceUsable && endpointSilentSeconds >= AudioSilenceTimeout.TotalSeconds);
            bool active = playerEvidenceAvailable
                          && playerActiveSeconds >= AudioActiveConfirmTime.TotalSeconds;
            // A native position that genuinely advances also confirms the player's
            // own Playing report when its audio session meter is unavailable.
            if (_reportedState == PlaybackState.Playing && Environment.TickCount64 < _timelinePlayingUntilMs)
                silent = false;
            if (Environment.TickCount64 < _transportEventUntilMs)
            {
                if (_lastTransportEvent == PlaybackState.Paused) active = false;
                else if (_lastTransportEvent == PlaybackState.Playing) silent = false;
            }
            bool confirming = _commandState != null && Environment.TickCount64 < _commandUntilMs;
            if (_reportedState == PlaybackState.Paused && playerEvidenceAvailable && playerSilentSeconds >= .3)
                _playerQuietAfterPause = true;
            // USB streams can stay open while paused. Require a real quiet interval followed
            // by fresh own-app audio, not just an Active stream or a buffered pause tail.
            bool resumedAfterQuiet = _playerQuietAfterPause && ReadCurrentStream() is { Running: true };
            if (_reportedState == PlaybackState.Paused && !_pausedTimelineAdvancing && !resumedAfterQuiet
                && !(confirming && _commandState == PlaybackState.Playing)) active = false;

            // 同一份会话证据不可能同时得出两个结论；真出现就按"在出声"处理（宁可显示播放）。
            if (active && silent) silent = false;
            if (active) _playerAudioUntilMs = Environment.TickCount64 + 500;

            if (_reportedState is not (PlaybackState.Playing or PlaybackState.Paused))
            {
                if (_reportedState == PlaybackState.Changing) return;
                _audioVetoLatched = false;
                _audioActiveLatched = false;
                return;
            }

            // 成功命令后的短暂确认窗口不采用执行前残留的音频样本。
            if (confirming && _commandState == PlaybackState.Playing && _commandAcknowledged
                && _reportedState == PlaybackState.Paused && playerEvidenceAvailable && playerSilentSeconds >= .2)
            {
                _commandState = null; confirming = false;
                _audioActiveLatched = false; _audioVetoLatched = true;
                PublishResolvedState();
            }
            if (confirming)
            {
                if (_commandState == PlaybackState.Paused) active = false;
                else silent = false;
            }

            // An inconclusive sample (a short gap or a missing meter) cannot undo a
            // confirmed correction. Only continuous evidence in the opposite direction can.
            bool veto = _audioVetoLatched, playing = _audioActiveLatched;
            if (silent) { veto = true; playing = false; }
            else if (active) { veto = false; playing = true; }
            else if (playerEvidenceAvailable && _reportedState == PlaybackState.Paused && playerSilentSeconds >= .3
                && !(confirming && _commandState == PlaybackState.Playing))
            { veto = true; playing = false; }
            if (veto == _audioVetoLatched && playing == _audioActiveLatched) return;

            _audioVetoLatched = veto;
            _audioActiveLatched = playing;
            LogAudioArbitration(veto, playing, playerSilentSeconds, endpointSilentSeconds,
                                playerActiveSeconds, playerEvidenceAvailable);

            PublishResolvedState();
        }

        /// <summary>打印一条仲裁诊断（带节流，见 <see cref="AudioLogMinIntervalMs"/>）。</summary>
        private void LogAudioArbitration(bool silent, bool active, double playerSilentSeconds,
                                         double endpointSilentSeconds, double playerActiveSeconds,
                                         bool playerEvidenceAvailable)
        {
            if (silent == _audioLogVeto && active == _audioLogActive) return;

            long now = Environment.TickCount64;
            if (now - _audioLogStampMs < AudioLogMinIntervalMs) return;

            _audioLogStampMs = now;
            _audioLogVeto = silent;
            _audioLogActive = active;
            Debug.WriteLine($"[SMTC] 音频仲裁: veto={silent}(silent={playerSilentSeconds:F1}s/{endpointSilentSeconds:F1}s) active={active}({playerActiveSeconds:F1}s) session={playerEvidenceAvailable} reported={_reportedState}");
        }

        /// <summary>
        /// 把"播放器上报状态"折算成"对外呈现状态"的唯一出口。
        /// 成功控制命令先短暂确认，再结合播放器状态与新鲜的自身会话电平双向纠正。
        /// </summary>
        private PlaybackState ResolveState(PlaybackState reported, string? source = null, MediaContentKind? kind = null)
        {
            if (_pendingCommandVersion != 0 && _pendingCommandVersion == _commandVersion
                && _current is { } pendingCurrent && pendingCurrent.SessionId == _pendingCommandSession && (source == null || source == pendingCurrent.SourceApp))
                return _pendingCommandState;
            if (_commandState is { } requested && Environment.TickCount64 < _commandUntilMs) return requested;
            if (reported == PlaybackState.Changing && (_current?.State is PlaybackState.Playing or PlaybackState.Paused)
                && (source == null || source == _current.SourceApp)) return _current.State;
            if (MediaSourceNames.IsBrowser(source ?? Current?.SourceApp ?? "") || (kind ?? Current?.ContentKind) == MediaContentKind.Video) return reported;
            if (ResolveStreamState(reported, source, kind) is { } streamState) return streamState;
            if (!AudioProbeEnabled) return reported;
            if (reported == PlaybackState.Paused && _audioActiveLatched) return PlaybackState.Playing;
            if (reported == PlaybackState.Playing && _audioVetoLatched) return PlaybackState.Paused;
            return reported;
        }

        internal float ReadPlayerSessionPeak(float mixerPeak)
        {
            var observation = ReadCurrentStream();
            return observation?.Peak is { } peak && float.IsFinite(peak) && peak is >= 0 and <= 1
                && Environment.TickCount64 - observation.SampledAtMs <= 500 ? peak : mixerPeak;
        }

        private MediaStreamObservation? ReadCurrentStream(string? source = null, MediaContentKind? kind = null)
        {
            var observation = _readStreamObservation();
            long now = Environment.TickCount64;
            return observation != null && observation.Source == (source ?? _current?.SourceApp)
                && observation.SessionId == _current?.SessionId && (kind ?? _current?.ContentKind) == MediaContentKind.Music
                && now >= observation.SampledAtMs && now - observation.SampledAtMs <= 1500
                && now - observation.StableSinceMs >= 180 ? observation : null;
        }

        private PlaybackState? ResolveStreamState(PlaybackState reported, string? source, MediaContentKind? kind)
        {
            if (reported is not (PlaybackState.Playing or PlaybackState.Paused)) return null;
            var observation = ReadCurrentStream(source, kind);
            if (observation == null || observation.Running) return null;
            long now = Environment.TickCount64;
            if (reported == PlaybackState.Playing
                && (now < _transportEventUntilMs && _lastTransportEvent == PlaybackState.Playing
                    || now < _timelinePlayingUntilMs && observation.StableSinceMs <= _nativeTimelineSampleMs)) return null;
            // A short device/stream interruption must not collapse an otherwise playing island.
            // Once stopped, latch pause: reopening a USB stream alone cannot prove playback resumed.
            if (now - observation.StableSinceMs < 650 || now < _playerAudioUntilMs) return null;
            _audioVetoLatched = true; _audioActiveLatched = false;
            return PlaybackState.Paused;
        }
        private PlaybackState? _lastTransportEvent;
        private long _transportEventUntilMs;
        internal PlaybackState ObserveTransportState(PlaybackState reported, bool fromEvent)
        {
            if (fromEvent)
            {
                _lastTransportEvent = reported;
                _transportEventUntilMs = Environment.TickCount64 + 650;
                ObservePlaybackEvent(reported);
            }
            else if (_lastTransportEvent is { } latest && Environment.TickCount64 < _transportEventUntilMs
                && reported is PlaybackState.Playing or PlaybackState.Paused && reported != latest)
                ObserveReportedState(latest);
            else ObserveReportedState(reported);
            return _reportedState;
        }

        private void ObserveReportedState(PlaybackState reported)
        {
            if (reported == PlaybackState.Paused && _reportedState != PlaybackState.Paused) ObservePause();
            if (reported is PlaybackState.Closed or PlaybackState.Stopped)
            {
                _audioActiveLatched = _audioVetoLatched = false;
            }
            _reportedState = reported;
            if (_commandState == reported) _commandAcknowledged = true;
            // Keep the full command confirmation window after an acknowledgement:
            // some players deliver another old playback notification immediately after it.
        }

        private void ObservePause()
        {
            ResetNativeTimelineEvidence();
            _pausePosition = EstimatedPosition;
            _pauseStampUtc = DateTime.UtcNow;
            _pausedTimelineAdvancing = _playerQuietAfterPause = false;
            _playerAudioUntilMs = 0;
            _audioActiveLatched = false;
            AudioEvidenceEpoch++;
        }

        private void ObservePlaybackEvent(PlaybackState reported)
        {
            bool repeatedPause = reported == PlaybackState.Paused && _reportedState == PlaybackState.Paused;
            bool newPlaying = reported == PlaybackState.Playing && _reportedState != PlaybackState.Playing;
            ObserveReportedState(reported);
            if (reported == PlaybackState.Paused)
            {
                // Once play was acknowledged, a new explicit pause is a newer action;
                // a stale polling sample still uses the command's confirmation window.
                if (!repeatedPause && _commandState == PlaybackState.Playing && _commandAcknowledged) _commandState = null;
            }
            else if (newPlaying
                && _pendingCommandVersion == 0
                && !(_commandState == PlaybackState.Paused && Environment.TickCount64 < _commandUntilMs))
            {
                _audioVetoLatched = false;
                AudioEvidenceEpoch++;
            }
        }

        internal void ObserveTimelineEvidence(TimeSpan position, DateTimeOffset lastUpdated)
        {
            if (_reportedState == PlaybackState.Paused && lastUpdated.UtcDateTime > _pauseStampUtc
                && lastUpdated <= DateTimeOffset.UtcNow.AddSeconds(1) && position > _pausePosition + TimeSpan.FromMilliseconds(150))
                _pausedTimelineAdvancing = true;
            long now = Environment.TickCount64;
            bool eligible = AudioProbeEnabled && _reportedState == PlaybackState.Playing
                && _current != null && _current.ContentKind != MediaContentKind.Video
                && !MediaSourceNames.IsBrowser(_current.SourceApp) && _pendingCommandVersion == 0
                && !(_commandState == PlaybackState.Paused && now < _commandUntilMs)
                && lastUpdated <= DateTimeOffset.UtcNow.AddSeconds(1)
                && lastUpdated >= DateTimeOffset.UtcNow.AddSeconds(-15);
            if (!eligible) { _nativeTimelineSampleMs = 0; _nativeTimelineStamp = default; return; }
            // A repeated IPC read is not a new timeline sample. Many desktop players
            // update Position/LastUpdatedTime every 3-5 seconds; use their timestamp,
            // not the time of the most recent repeated poll, to measure progression.
            if (lastUpdated <= _nativeTimelineStamp) return;
            double elapsed = (lastUpdated - _nativeTimelineStamp).TotalMilliseconds;
            double advance = (position - _nativeTimelinePosition).TotalMilliseconds;
            if (_nativeTimelineSampleMs != 0 && elapsed is >= 150 and <= 15000
                && now - _nativeTimelineSampleMs <= 16000
                && advance >= 120 && advance <= elapsed * 2 + 100)
            {
                _timelinePlayingUntilMs = now + (long)Math.Clamp(elapsed + 1500, 1500, 8000);
                _audioVetoLatched = false;
            }
            _nativeTimelinePosition = position; _nativeTimelineSampleMs = now; _nativeTimelineStamp = lastUpdated;
        }

        private void ResetNativeTimelineEvidence()
        {
            _nativeTimelineSampleMs = _timelinePlayingUntilMs = 0;
            _nativeTimelinePosition = TimeSpan.Zero; _nativeTimelineStamp = default;
            _playerQuietAfterPause = false; _playerAudioUntilMs = 0;
        }

        private void PublishResolvedState()
        {
            if (_current == null || _disposed) return;
            var state = ResolveState(_reportedState);
            if (state == _current.State) return;
            var position = EstimatedPosition;
            _positionStampUtc = DateTime.UtcNow;
            Publish(_current with { State = state, Position = position, PlaybackStateInferred = true });
        }

        public SmtcService(Dispatcher dispatcher) : this(dispatcher, null) { }

        internal SmtcService(Dispatcher dispatcher, Func<MediaStreamObservation?>? readStreamObservation)
        {
            _dispatcher = dispatcher;
            _readStreamObservation = readStreamObservation ?? (() => _streamProbe?.Current);
            _metadataProbe = new IslandTimer(dispatcher, "media-metadata")
                { Interval = MediaWorkPacing.MetadataInterval(false, false) };
            _metadataProbe.Tick += (_, _) => { if (!_refreshing) QueueRefresh(); };
            _metadataEvents = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
                { Interval = TimeSpan.FromMilliseconds(35) };
            _metadataEvents.Tick += (_, _) => { _metadataEvents.Stop(); QueueRefresh(); };
        }

        public async Task InitializeAsync()
        {
            try
            {
                _streamProbe ??= new MediaAudioSessionProbe();
                _streamProbe.Changed += () => _dispatcher.BeginInvoke(new Action(PublishResolvedState));
                var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
                await _dispatcher.InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    _manager = manager;
                    _manager.CurrentSessionChanged += OnCurrentSessionChanged;
                    _manager.SessionsChanged += OnSessionsChanged;
                    _metadataProbe.Start();
                    QueueRefresh();
                    await _refreshTask;
                }).Task.Unwrap();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SMTC] 初始化失败: " + ex);
                Failed?.Invoke(this, ex.Message);
            }
        }

        // ---------------- 事件接线 ----------------

        private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender,
                                             CurrentSessionChangedEventArgs args) => QueueRefresh();

        private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender,
                                       SessionsChangedEventArgs args) => QueueRefresh();

        private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender,
                                              MediaPropertiesChangedEventArgs args)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed) return;
                // Coalesce a player's title/artist/album burst before replacing a read.
                _metadataEvents.Stop(); _metadataEvents.Start();
            }));
        }

        private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender,
                                                 TimelinePropertiesChangedEventArgs args)
        {
            _dispatcher.BeginInvoke(new Action(() => CheckFastPlaybackState(updateTimeline: true)));
        }

        private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender,
                                           PlaybackInfoChangedEventArgs args)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed) return;
                try { if (sender.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    _activated[SessionId(sender)] = ++_activationOrder; } catch { }
                var best = GetBestSession();
                if (!IsSameSession(best, _session)) QueueRefresh();
                else CheckFastPlaybackState(updateTimeline: true, fromPlaybackEvent: true);
            }));
        }

        /// <summary>
        /// 极速同步检测当前会话播放状态。
        /// 媒体事件直接调用，主界面定时器补查漏发事件；状态同步不等待曲目属性或封面。
        /// </summary>
        public void CheckFastPlaybackState(bool updateTimeline = false, bool fromPlaybackEvent = false)
        {
            if (_disposed) return;
            var session = _session;
            if (session == null || _current == null || _current.SessionId != SessionId(session))
            {
                return;
            }

            try
            {
                var playback = session.GetPlaybackInfo();
                if (playback != null)
                {
                    var reported = MapState(playback.PlaybackStatus);
                    reported = ObserveTransportState(reported, fromPlaybackEvent);
                    var newState = ResolveState(reported);
                    var controls = playback.Controls;
                    var updated = _current with
                    {
                        State = newState,
                        PlaybackStateInferred = newState != reported && !(_commandState == newState && Environment.TickCount64 < _commandUntilMs),
                        CanToggle = controls?.IsPlayPauseToggleEnabled == true || controls?.IsPlayEnabled == true || controls?.IsPauseEnabled == true,
                        CanNext = controls?.IsNextEnabled ?? false,
                        CanPrevious = controls?.IsPreviousEnabled ?? false
                    };
                    updated = WithPlaybackOrder(updated, playback);
                    if (updateTimeline || newState != _current.State)
                        updated = ApplyTimeline(updated, session.GetTimelineProperties());
                    if (updated != _current) Publish(updated);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SMTC] CheckFastPlaybackState 异常: " + ex.Message);
            }
        }

        private GlobalSystemMediaTransportControlsSession? GetBestSession()
        {
            if (_manager == null) return null;
            try
            {
                var cur = _manager.GetCurrentSession();
                var live = _manager.GetSessions().ToArray();
                SyncSessionSubscriptions(live);
                var snapshots = new List<MediaSessionInfo>();
                foreach (var s in live)
                {
                    try
                    {
                        string id = SessionId(s);
                        snapshots.Add(new(id, s.SourceAppUserModelId ?? "", _titles.GetValueOrDefault(id, ""),
                            MapState(s.GetPlaybackInfo()?.PlaybackStatus), _activated.GetValueOrDefault(id), IsSameSession(s, cur)));
                    }
                    catch { }
                }
                Sessions = snapshots;
                if (_selectedSession != null && !snapshots.Any(s => s.Id == _selectedSession)) _selectedSession = null;
                string? best = MediaSourceNames.Select(snapshots, _selectedSession, ActiveSessionId);
                return best != null ? _watched.GetValueOrDefault(best) : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SMTC] GetBestSession 异常: " + ex.Message);
                return null;
            }
        }

        private void Attach(GlobalSystemMediaTransportControlsSession session)
        {
            _session = session;
        }

        private static string SessionId(GlobalSystemMediaTransportControlsSession session) =>
            ((WinRT.IWinRTObject)session).NativeObject.ThisPtr.ToInt64().ToString("X");
        private void SyncSessionSubscriptions(IEnumerable<GlobalSystemMediaTransportControlsSession> sessions)
        {
            var live = sessions.ToDictionary(SessionId);
            foreach (string gone in _watched.Keys.Where(id => !live.ContainsKey(id)).ToArray())
            {
                Unwatch(_watched[gone]); _watched.Remove(gone); _activated.Remove(gone); _titles.Remove(gone);
            }
            foreach (var pair in live)
            {
                if (_watched.ContainsKey(pair.Key)) continue;
                _watched.Add(pair.Key, pair.Value);
                pair.Value.MediaPropertiesChanged += OnMediaPropertiesChanged;
                pair.Value.PlaybackInfoChanged += OnPlaybackInfoChanged;
                pair.Value.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            }
        }
        private void Unwatch(GlobalSystemMediaTransportControlsSession session)
        {
            try { session.MediaPropertiesChanged -= OnMediaPropertiesChanged; session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged; } catch { }
        }

        private void Detach()
        {
            _session = null;
            _lastTransportEvent = null; _transportEventUntilMs = 0;
            _artworkKey = ""; _artwork = null; _audioVetoLatched = _audioActiveLatched = false;
            _artworkLoading = false;
            _artworkVersion++; _artworkCancellation?.Cancel();
            _commandState = null; _commandVersion++; AudioEvidenceEpoch++;
            _pendingCommandVersion = 0; _pendingCommandSession = null;
            ResetNativeTimelineEvidence();
            _commandAcknowledged = false; _pausedTimelineAdvancing = false; _pausePosition = TimeSpan.Zero;
        }

        // ---------------- 刷新（合并高频事件） ----------------

        private void QueueRefresh()
        {
            if (_disposed) return;
            if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(new Action(QueueRefresh)); return; }
            _metadataVersion++;
            var session = GetBestSession();
            if (!IsSameSession(session, _session))
            {
                Detach();
                if (session != null) Attach(session);
                Publish(null);
            }
            CheckFastPlaybackState();
            if (_refreshing) { _refreshPending = true; _metadataCancellation?.Cancel(); }
            else _refreshTask = RefreshSafeAsync();
        }

        private async Task RefreshSafeAsync()
        {
            if (_disposed) return;
            if (_refreshing) { _refreshPending = true; return; }

            _refreshing = true;
            try
            {
                do
                {
                    _refreshPending = false;
                    try { await RefreshCoreAsync(); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { Debug.WriteLine("[SMTC] 刷新异常: " + ex.Message); }
                }
                while (_refreshPending && !_disposed);
            }
            catch (Exception ex) { Debug.WriteLine("[SMTC] 刷新异常: " + ex); }
            finally { _refreshing = false; }
        }

        /// <summary>强制刷新一次（供外部在需要时调用）。</summary>
        public Task RefreshAsync() { QueueRefresh(); return _refreshTask; }

        private static bool IsSameSession(GlobalSystemMediaTransportControlsSession? a, GlobalSystemMediaTransportControlsSession? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            try
            {
                return ReferenceEquals(a, b) ||
                    ((WinRT.IWinRTObject)a).NativeObject.ThisPtr == ((WinRT.IWinRTObject)b).NativeObject.ThisPtr;
            }
            catch
            {
                return false;
            }
        }

        private async Task RefreshCoreAsync()
        {
            if (_session == null)
            {
                Publish(null);
                return;
            }

            var capturedSession = _session;
            long version = _metadataVersion;
            _metadataCancellation?.Dispose();
            _metadataCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var token = _metadataCancellation.Token;
            var readStarted = Stopwatch.GetTimestamp();
            var props = await Task.Run(async () => await capturedSession.TryGetMediaPropertiesAsync().AsTask(token).ConfigureAwait(false), token).WaitAsync(token);
            if (_disposed || version != _metadataVersion || !IsSameSession(capturedSession, _session)) return;

            string title = props?.Title ?? "";
            string artist = props?.Artist ?? "";
            string album = props?.AlbumTitle ?? "";
            string source = "";
            try { source = capturedSession.SourceAppUserModelId ?? ""; } catch { }

            // 某些播放器（网易云、QQ音乐等）切歌瞬间属性异步就绪有极短延迟，重试一次
            if (string.IsNullOrEmpty(title) && capturedSession.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                await Task.Delay(100, token);
                if (_session != null)
                {
                    props = await Task.Run(async () => await capturedSession.TryGetMediaPropertiesAsync().AsTask(token).ConfigureAwait(false), token).WaitAsync(token);
                    if (_disposed || version != _metadataVersion || !IsSameSession(capturedSession, _session)) return;
                    title = props?.Title ?? "";
                    artist = props?.Artist ?? "";
                    album = props?.AlbumTitle ?? "";
                }
            }

            // 封面：按「曲目身份」缓存，避免每次事件都重新解码
            _titles[SessionId(capturedSession)] = title;
            Sessions = Sessions.Select(item => item.Id == SessionId(capturedSession) ? item with { Title = title } : item).ToArray();
            string key = SessionId(capturedSession) + "\u0001" + title + "\u0001" + artist + "\u0001" + album;
            if (key != _artworkKey)
                IslandLog.Debug("SMTC", $"曲目属性更新: source={source} read={Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds:0}ms");
            // 属性请求完成后才取播放状态，不能让异步读取前的旧状态覆盖较新的播放事件。
            var playback = capturedSession.GetPlaybackInfo();
            var state = MapState(playback?.PlaybackStatus);
            state = ObserveTransportState(state, fromEvent: false);
            // 对外只呈现经音频仲裁后的状态：播放器谎报 Playing 而环回持续无音频时降级为 Paused
            var kind = props?.PlaybackType == Windows.Media.MediaPlaybackType.Video ? MediaContentKind.Video
                : props?.PlaybackType == Windows.Media.MediaPlaybackType.Music ? MediaContentKind.Music
                : MediaSourceNames.IsBrowser(source) ? MediaContentKind.Browser : MediaContentKind.Music;
            var controls = playback?.Controls;
            _playbackRate = playback?.PlaybackRate ?? 1.0;
            if (_playbackRate <= 0) _playbackRate = 1.0;

            var info = ApplyTimeline(new NowPlayingInfo
            {
                Title = title,
                Artist = artist,
                Album = album,
                SourceApp = source,
                SessionId = SessionId(capturedSession),
                ContentKind = kind,
                CanToggle = controls?.IsPlayPauseToggleEnabled == true || controls?.IsPlayEnabled == true || controls?.IsPauseEnabled == true,
                State = ResolveState(state, source, kind),
                CanNext = controls?.IsNextEnabled ?? false,
                CanPrevious = controls?.IsPreviousEnabled ?? false,
                CanSeek = controls?.IsPlaybackPositionEnabled ?? false
            }, capturedSession.GetTimelineProperties());
            info = WithPlaybackOrder(info, playback);
            var thumbnail = props?.Thumbnail;
            UpdateTrack(info, key, thumbnail == null ? null : token => Task.Run(() => LoadArtworkAsync(thumbnail, token), token));
        }

        private NowPlayingInfo ApplyTimeline(NowPlayingInfo info, GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline)
        {
            var oldInfo = _current;
            bool sameMedia = oldInfo != null && oldInfo.SessionId == info.SessionId && oldInfo.Title == info.Title && oldInfo.Artist == info.Artist;
            if (timeline != null && sameMedia && _appliedTimelineStamp != default
                && timeline.LastUpdatedTime <= _appliedTimelineStamp && timeline.Position == _appliedNativePosition)
            {
                // A repeated stale timeline is not a seek. Keep the running clock or the
                // frozen pause position instead of resetting lyrics to the old native sample.
                var preservedPosition = EstimatedPosition;
                _positionStampUtc = DateTime.UtcNow;
                var preservedState = ResolveState(_reportedState, info.SourceApp, info.ContentKind);
                return info with { Position = preservedPosition, Duration = timeline.EndTime > timeline.StartTime ? timeline.EndTime - timeline.StartTime : oldInfo!.Duration, State = preservedState,
                    PlaybackStateInferred = preservedState != _reportedState };
            }
            if (timeline != null) { _appliedTimelineStamp = timeline.LastUpdatedTime; _appliedNativePosition = timeline.Position; }
            var previousStamp = _positionStampUtc;
            var estimatedPosition = EstimatedPosition;
            TimeSpan duration = info.Duration;
            TimeSpan position = info.Position;
            if (timeline != null)
            {
                duration = timeline.EndTime - timeline.StartTime;
                if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
                position = timeline.Position - timeline.StartTime;
                if (position < TimeSpan.Zero) position = TimeSpan.Zero;
                if (duration > TimeSpan.Zero && position > duration) position = duration;

                DateTimeOffset lastUpdated = timeline.LastUpdatedTime;
                ObserveTimelineEvidence(position, lastUpdated);
                if (lastUpdated != default)
                {
                    var now = DateTimeOffset.UtcNow;
                    if (lastUpdated > now && lastUpdated <= now.AddSeconds(1))
                    {
                        lastUpdated = now;
                    }
                    if (lastUpdated <= now)
                    {
                        _positionStampUtc = lastUpdated.UtcDateTime;
                    }
                    else
                    {
                        _positionStampUtc = DateTime.UtcNow;
                    }
                }
                else
                {
                    _positionStampUtc = DateTime.UtcNow;
                }
                if (_current is { } old && old.SessionId == info.SessionId && old.State != info.State
                    && (lastUpdated.UtcDateTime <= previousStamp || !old.IsPlaying && info.IsPlaying))
                {
                    if (info.State == PlaybackState.Paused && old.IsPlaying && lastUpdated.UtcDateTime <= previousStamp) position = estimatedPosition;
                    _positionStampUtc = DateTime.UtcNow;
                }
            }
            else
            {
                _positionStampUtc = DateTime.UtcNow;
            }

            var resolved = ResolveState(_reportedState, info.SourceApp, info.ContentKind);
            return info with { Position = position, Duration = duration, CanSeek = info.CanSeek && duration > TimeSpan.Zero,
                State = resolved, PlaybackStateInferred = resolved != _reportedState
                    && !(_commandState == resolved && Environment.TickCount64 < _commandUntilMs) };
        }

        /// <summary>曲目信息先显示，封面独立加载；迟到的旧曲目封面只能被丢弃。</summary>
        internal void UpdateTrack(NowPlayingInfo info, string key, Func<CancellationToken, Task<BitmapSource?>>? readArtwork)
        {
            if (_disposed) return;
            bool changed = key != _artworkKey;
            if (changed)
            {
                ResetNativeTimelineEvidence();
                _artworkCancellation?.Cancel(); _artworkCancellation?.Dispose();
                _artworkCancellation = null;
                _artworkVersion++;
                _artworkKey = key;
                _artwork = null;
                _artworkLoading = false;
                if (_current == null || _current.SessionId != info.SessionId || _current.SourceApp != info.SourceApp)
                    _audioActiveLatched = _audioVetoLatched = false;
                AudioEvidenceEpoch++;
                if (_reportedState == PlaybackState.Paused)
                {
                    _pausePosition = info.Position; _pauseStampUtc = DateTime.UtcNow;
                    _pausedTimelineAdvancing = _playerQuietAfterPause = false;
                    _playerAudioUntilMs = 0;
                }
            }
            // 用当前曲目的来源决定仲裁，不借用上一首曲目的类型。
            var resolved = ResolveState(_reportedState, info.SourceApp, info.ContentKind);
            Publish(info with { Artwork = _artwork, State = resolved,
                PlaybackStateInferred = resolved != _reportedState && !(_commandState == resolved && Environment.TickCount64 < _commandUntilMs) });
            if (_artwork == null && !_artworkLoading && readArtwork != null)
            {
                _artworkCancellation?.Dispose();
                _artworkCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                _artworkLoading = true;
                _ = RefreshArtworkAsync(key, _artworkVersion, info.SessionId, readArtwork, _artworkCancellation.Token);
            }
        }

        private async Task RefreshArtworkAsync(string key, long version, string sessionId,
            Func<CancellationToken, Task<BitmapSource?>> readArtwork, CancellationToken token)
        {
            try
            {
                var art = await readArtwork(token).WaitAsync(token);
                if (_disposed || token.IsCancellationRequested || key != _artworkKey || version != _artworkVersion
                    || _current?.SessionId != sessionId) return;
                _artwork = art;
                // 保留加载期间收到的新状态与进度，只补上封面。
                if (art != _current.Artwork) Publish(_current with { Artwork = art });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.WriteLine("[SMTC] 封面刷新失败: " + ex.Message); }
            finally { if (version == _artworkVersion) _artworkLoading = false; }
        }

        /// <summary>
        /// 调试：注入 / 清除合成的「当前播放」。走的是与真实播放同一条发布通道
        /// （<see cref="Publish"/> + <see cref="NowPlayingChanged"/>），UI 侧完全不需要区分真假；
        /// 注入期间真实会话的每一次 Publish 也会被合成数据顶掉，保证验证过程稳定。
        /// </summary>
        internal void SetDebugNowPlaying(NowPlayingInfo? info)
        {
            _debugOverride = info;
            Publish(info);
        }

        private void Publish(NowPlayingInfo? info)
        {
            if (_debugOverride != null) info = _debugOverride;
            if (_current?.State != info?.State)
                IslandLog.Debug("SMTC", $"状态同步: {_current?.State}->{info?.State} reported={_reportedState} command={(_commandState != null && Environment.TickCount64 < _commandUntilMs ? _commandState : null)} veto={_audioVetoLatched} active={_audioActiveLatched}");
            _streamProbe?.SetTarget(info?.SupportsLyrics == true && !MediaSourceNames.IsBrowser(info.SourceApp) ? info.SourceApp : "", info?.SessionId ?? "");
            _current = info;
            if (_dispatcher.CheckAccess())
            {
                NotifyNowPlayingChanged(info);
            }
            else
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    NotifyNowPlayingChanged(info);
                }));
            }
        }

        private void NotifyNowPlayingChanged(NowPlayingInfo? info)
        {
            var probeInterval = MediaWorkPacing.MetadataInterval(_current?.HasMedia == true, _current?.IsPlaying == true);
            if (_metadataProbe.Interval != probeInterval) _metadataProbe.Interval = probeInterval;
            NowPlayingChanged?.Invoke(this, info);
        }

        private static PlaybackState MapState(Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus? s)
        {
            if (s == null) return PlaybackState.Closed;
            return s.Value switch
            {
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => PlaybackState.Closed,
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => PlaybackState.Opened,
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => PlaybackState.Changing,
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => PlaybackState.Stopped,
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackState.Playing,
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackState.Paused,
                _ => PlaybackState.Closed
            };
        }

        /// <summary>
        /// 估算当前播放位置：以 SMTC 的 TimelineProperties.LastUpdatedTime 为基准，
        /// 结合高精度时钟实时外推，消除媒体事件到达延迟与本地时钟脱节的问题。
        /// </summary>
        public TimeSpan EstimatedPosition
        {
            get
            {
                var info = _current;
                if (info == null) return TimeSpan.Zero;
                if (!info.IsPlaying || info.Duration <= TimeSpan.Zero) return info.Position;

                var elapsed = DateTime.UtcNow - _positionStampUtc;
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                var pos = info.Position + TimeSpan.FromSeconds(elapsed.TotalSeconds * _playbackRate);
                return (info.Duration > TimeSpan.Zero && pos > info.Duration) ? info.Duration : pos;
            }
        }

        // ---------------- 封面解码 ----------------

        private static async Task<BitmapSource?> LoadArtworkAsync(Windows.Storage.Streams.IRandomAccessStreamReference? reference, CancellationToken token)
        {
            if (reference == null) return null;
            try
            {
                using var winrtStream = await reference.OpenReadAsync().AsTask(token);
                using var netStream = winrtStream.AsStreamForRead();
                using var ms = new MemoryStream();
                await netStream.CopyToAsync(ms, token);
                ms.Position = 0;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                bmp.DecodePixelWidth = 160;   // 卡片里最大 64px，够用且省内存
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();                 // 冻结后可跨线程/长期复用
                return bmp;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SMTC] 封面解码失败: " + ex.Message);
                return null;
            }
        }

        // ---------------- 播放控制 ----------------

        private readonly MediaPlaybackOrderTracker _playbackOrders = new();
        private readonly HashSet<string> _shuffleRejected = new(), _repeatRejected = new();
        private NowPlayingInfo WithPlaybackOrder(NowPlayingInfo info,
            GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback) => _playbackOrders.Observe(info,
                playback?.Controls.IsShuffleEnabled == true && !_shuffleRejected.Contains(info.SessionId),
                playback?.Controls.IsRepeatEnabled == true && !_repeatRejected.Contains(info.SessionId),
                playback?.IsShuffleActive, playback?.AutoRepeatMode);

        public async Task<bool> CyclePlaybackOrderAsync()
        {
            var session = _session;
            var info = _current;
            if (session == null || info == null) return false;
            try
            {
                var playback = session.GetPlaybackInfo();
                info = WithPlaybackOrder(info, playback);
                var target = MediaPlaybackOrders.Next(info);
                bool accepted = await MediaPlaybackOrders.ChangeAsync(info, target,
                    async value =>
                    {
                        bool ok = await session.TryChangeShuffleActiveAsync(value).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                        if (!ok) _shuffleRejected.Add(info.SessionId);
                        return ok;
                    },
                    async value =>
                    {
                        bool ok = await session.TryChangeAutoRepeatModeAsync(value).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                        if (!ok) _repeatRejected.Add(info.SessionId);
                        return ok;
                    },
                    () => !_disposed && IsSameSession(_session, session) && _current?.SessionId == info.SessionId);
                if (accepted)
                {
                    try { playback = session.GetPlaybackInfo(); }
                    catch { /* The accepted request remains valid if its readback fails. */ }
                    _playbackOrders.Accept(info.SessionId, target, playback?.IsShuffleActive, playback?.AutoRepeatMode);
                    // Publish the successful request now. Mode changes do not need song
                    // properties, artwork, or the player's delayed playback-info report.
                    if (_current?.SessionId == info.SessionId)
                        Publish(_current with { PlaybackOrder = target, PlaybackOrderKnown = true });
                }
                else CheckFastPlaybackState();
                return accepted;
            }
            catch { return false; }
        }
        public async Task TogglePlayPauseAsync()
        {
            var s = _session;
            if (s == null) return;
            try
            {
                var playback = s.GetPlaybackInfo(); var controls = playback?.Controls;
                // 按岛上按钮所表达的动作执行；音频纠正后的暂停不能被原始 Playing 误当成暂停命令。
                var target = Current?.IsPlaying == true ? PlaybackState.Paused : PlaybackState.Playing;
                Func<Task<bool>>? request = target == PlaybackState.Playing && controls?.IsPlayEnabled == true
                    ? () => s.TryPlayAsync().AsTask()
                    : target == PlaybackState.Paused && controls?.IsPauseEnabled == true
                        ? () => s.TryPauseAsync().AsTask()
                        : controls?.IsPlayPauseToggleEnabled == true ? () => s.TryTogglePlayPauseAsync().AsTask() : null;
                bool explicitRequest = target == PlaybackState.Playing ? controls?.IsPlayEnabled == true : controls?.IsPauseEnabled == true;
                var reported = MapState(playback?.PlaybackStatus);
                bool predictableToggle = target == PlaybackState.Playing ? reported == PlaybackState.Paused : reported == PlaybackState.Playing;
                if (request != null) await ExecutePlaybackCommandAsync(SessionId(s), target, request, explicitRequest || predictableToggle);
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
            CheckFastPlaybackState();
            QueueRefresh();
        }

        internal async Task ExecutePlaybackCommandAsync(string sessionId, PlaybackState target, Func<Task<bool>> request, bool optimistic = true)
        {
            long version = ++_commandVersion;
            if (_current?.SessionId == sessionId)
            {
                _pendingCommandVersion = version;
                _pendingCommandSession = sessionId;
                _pendingCommandState = _current.State;
            }
            bool accepted;
            try { accepted = await request(); }
            finally
            {
                if (_pendingCommandVersion == version) { _pendingCommandVersion = 0; _pendingCommandSession = null; }
            }
            if (!accepted || _disposed || version != _commandVersion || _current?.SessionId != sessionId) return;
            // Toggle-only providers do not describe the resulting state. If the raw
            // state disagrees with the button, wait for feedback instead of guessing "playing".
            if (!optimistic) return;
            var position = EstimatedPosition;
            _commandState = target;
            ResetNativeTimelineEvidence();
            _commandAcknowledged = false;
            _commandUntilMs = Environment.TickCount64 + 2000;
            _audioVetoLatched = _audioActiveLatched = false;
            AudioEvidenceEpoch++;
            _positionStampUtc = DateTime.UtcNow;
            Publish(_current with { State = target, Position = position, PlaybackStateInferred = false });
        }

        public async Task<bool> TryControlAsync(string action)
        {
            var session = _session;
            if (session == null || _disposed) return false;
            try
            {
                var playback = session.GetPlaybackInfo(); var controls = playback?.Controls;
                if (action is "play" or "pause")
                {
                    var target = action == "play" ? PlaybackState.Playing : PlaybackState.Paused;
                    if (Current is { PlaybackStateInferred: false } current && current.State == target) return true;
                    Func<Task<bool>>? request = action == "play" && controls?.IsPlayEnabled == true ? () => session.TryPlayAsync().AsTask()
                        : action == "pause" && controls?.IsPauseEnabled == true ? () => session.TryPauseAsync().AsTask() : null;
                    if (request == null) return false;
                    bool accepted = false;
                    await ExecutePlaybackCommandAsync(SessionId(session), target, async () => accepted = await request());
                    CheckFastPlaybackState(); QueueRefresh(); return accepted;
                }
                bool success = action == "next" && controls?.IsNextEnabled == true ? await session.TrySkipNextAsync().AsTask()
                    : action == "previous" && controls?.IsPreviousEnabled == true && await session.TrySkipPreviousAsync().AsTask();
                if (success) { AudioEvidenceEpoch++; QueueRefresh(); _ = RecheckTrackAsync(SessionId(session), _artworkKey); }
                return success;
            }
            catch (Exception) { return false; }
        }

        public async Task NextAsync()
        {
            var s = _session;
            if (s == null) return;
            string previousKey = _artworkKey;
            try { if (await s.TrySkipNextAsync().AsTask()) _ = RecheckTrackAsync(SessionId(s), previousKey); }
            catch (Exception ex) { Debug.WriteLine(ex); }
            QueueRefresh();
        }

        public async Task PreviousAsync()
        {
            var s = _session;
            if (s == null) return;
            string previousKey = _artworkKey;
            try { if (await s.TrySkipPreviousAsync().AsTask()) _ = RecheckTrackAsync(SessionId(s), previousKey); }
            catch (Exception ex) { Debug.WriteLine(ex); }
            QueueRefresh();
        }

        private async Task RecheckTrackAsync(string sessionId, string previousKey)
        {
            // 一些播放器漏发曲目事件；只在原曲目还未变化时补查，正常事件到达便立即结束。
            foreach (int delay in new[] { 100, 200, 400, 800 })
            {
                await Task.Delay(delay);
                if (_disposed || _session == null || SessionId(_session) != sessionId || previousKey != _artworkKey) return;
                QueueRefresh();
            }
        }

        /// <summary>跳转到指定位置（仅当会话支持 seek 时生效）。</summary>
        public async Task SeekAsync(TimeSpan position)
        {
            var s = _session;
            if (s == null) return;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            try
            {
                long ticks = (long)(position.TotalSeconds * 10_000_000d);
                if (!await s.TryChangePlaybackPositionAsync(ticks).AsTask()) { QueueRefresh(); return; }
                // 乐观更新，避免进度条在事件回来之前乱跳
                var cur = _current;
                if (cur != null)
                {
                    _current = cur with { Position = position };
                    _positionStampUtc = DateTime.UtcNow;
                }
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
            QueueRefresh();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _metadataProbe.Dispose(); _metadataEvents.Stop();
            _metadataCancellation?.Cancel(); _metadataCancellation?.Dispose();
            _artworkCancellation?.Cancel(); _artworkCancellation?.Dispose();
            _artworkCancellation = null;

            try
            {
                Detach();
                foreach (var watched in _watched.Values) Unwatch(watched);
                _watched.Clear(); _activated.Clear(); _titles.Clear(); Sessions = Array.Empty<MediaSessionInfo>();
                if (_manager != null)
                {
                    _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
                    _manager.SessionsChanged -= OnSessionsChanged;
                }
            }
            catch (Exception ex) { Debug.WriteLine(ex); }

            _streamProbe?.Dispose(); _streamProbe = null;
            _manager = null;
        }
    }
}
