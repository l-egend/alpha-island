using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
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
        public BitmapSource? Artwork { get; init; }
        public PlaybackState State { get; init; }
        public TimeSpan Position { get; init; }
        public TimeSpan Duration { get; init; }
        public bool CanNext { get; init; }
        public bool CanPrevious { get; init; }
        public bool CanSeek { get; init; }

        public bool HasMedia => !string.IsNullOrWhiteSpace(Title) || Artwork != null;
        public bool IsPlaying => State == PlaybackState.Playing;

        public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "未知曲目" : Title;
        public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "未知艺术家" : Artist;
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

        private bool _refreshing;
        private bool _refreshPending;
        private bool _disposed;

        private NowPlayingInfo? _current;
        private string _artworkKey = "";
        private BitmapSource? _artwork;
        private DateTime _positionStampUtc = DateTime.UtcNow;
        private double _playbackRate = 1.0;

        /// <summary>播放器原样上报的状态（"真相"），与经音频仲裁后对外呈现的 <see cref="Current"/> 区分开。</summary>
        private PlaybackState _reportedState = PlaybackState.Closed;

        /// <summary>已因"持续采不到音频"把上报的 Playing 降级为 Paused（锁存，直到真的重新采到声音）。</summary>
        private volatile bool _audioVetoLatched;
        private volatile bool _audioActiveLatched;

        /// <summary>参数为 null 表示当前没有任何媒体会话。</summary>
        public event EventHandler<NowPlayingInfo?>? NowPlayingChanged;
        /// <summary>SMTC 初始化失败时触发，参数为错误描述。</summary>
        public event EventHandler<string>? Failed;

        public NowPlayingInfo? Current => _current;
        public bool IsAvailable => _manager != null;

        /// <summary>
        /// 连续多久采不到任何音频电平，就把播放器上报的 Playing 视为不可信、对外降级为 Paused。
        /// 1.5s 的取舍：既不会把间奏误判成暂停，又能让异常未上报暂停的播放器在极短时间内被修正。
        /// </summary>
        public TimeSpan AudioSilenceTimeout { get; set; } = TimeSpan.FromSeconds(1.5);

        /// <summary>
        /// 播放器会话连续多久有电平，才把它上报的 Paused 升级为 Playing。1.2s 的取舍：
        /// 会话电平表在停声后还会衰减一阵（百毫秒级），要求持续有声才不会把衰减尾巴当成播放；
        /// 而 1.2s 已经短到用户按下播放后几乎立刻就能看到岛里恢复播放态。
        /// </summary>
        public TimeSpan AudioActiveConfirmTime { get; set; } = TimeSpan.FromSeconds(1.2);

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
        /// 由 UI 侧按固定节奏（250ms）回报音频侧观测结果。这里有两类证据，可信度不同：
        /// <list type="bullet">
        /// <item><b>会话级</b>（<paramref name="playerEvidenceAvailable"/> = true）：播放器**自己那个**
        /// 音频会话的电平。它既能证明"没出声"（暂停），也能证明"在出声"（用户按了播放但播放器没上报），
        /// 而且不受别的应用出声、系统音量、系统静音影响 —— 所以只有它能做双向纠正。</item>
        /// <item><b>端点级</b>（<paramref name="endpointEvidenceUsable"/>）：整块声卡的环回电平。
        /// 只能证明"整个系统都没出声"（→ 降级），不能证明"是它在出声"（别的应用出声会误升级），
        /// 所以只用于降级。</item>
        /// </list>
        /// 为什么锁存而不是每拍重算：一旦降级（或升格），<c>_current.State</c> 就变了，
        /// 若判据再依赖它就会与 250ms 的检测周期形成来回抖动；锁存后唯一解除条件是相反的观测出现。
        /// </summary>
        public void ReportAudioEvidence(double playerSilentSeconds, double playerActiveSeconds,
                                        bool playerEvidenceAvailable,
                                        double endpointSilentSeconds, bool endpointEvidenceUsable)
        {
            if (_disposed || !AudioProbeEnabled) return;

            bool silent = playerEvidenceAvailable
                ? playerSilentSeconds >= AudioSilenceTimeout.TotalSeconds
                : (endpointEvidenceUsable && endpointSilentSeconds >= AudioSilenceTimeout.TotalSeconds);
            bool active = playerEvidenceAvailable
                          && playerActiveSeconds >= AudioActiveConfirmTime.TotalSeconds;

            // 同一份会话证据不可能同时得出两个结论；真出现就按"在出声"处理（宁可显示播放）。
            if (active && silent) silent = false;

            if (silent == _audioVetoLatched && active == _audioActiveLatched) return;

            _audioVetoLatched = silent;
            _audioActiveLatched = active;
            Debug.WriteLine($"[SMTC] 音频仲裁: veto={silent}(silent={playerSilentSeconds:F1}s/{endpointSilentSeconds:F1}s) active={active}({playerActiveSeconds:F1}s) session={playerEvidenceAvailable}");
            CheckFastPlaybackState();
        }

        /// <summary>
        /// 把"播放器上报状态"折算成"对外呈现状态"的唯一出口。
        /// 播放器权威上报状态优先：显式上报的 Paused / Stopped / Closed 绝不被音频锁存反向覆盖为 Playing；
        /// 仅当播放器谎报 Playing 但持续采不到任何音频时才降级为 Paused。
        /// </summary>
        private PlaybackState ResolveState(PlaybackState reported)
        {
            if (reported != PlaybackState.Playing) return reported;
            if (_audioVetoLatched) return PlaybackState.Paused;
            return PlaybackState.Playing;
        }

        public SmtcService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public async Task InitializeAsync()
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
                _manager.CurrentSessionChanged += OnCurrentSessionChanged;
                _manager.SessionsChanged += OnSessionsChanged;
                Attach();
                await RefreshCoreAsync();
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
                                              MediaPropertiesChangedEventArgs args) => QueueRefresh();

        private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender,
                                                 TimelinePropertiesChangedEventArgs args)
        {
            CheckFastPlaybackState();
            QueueRefresh();
        }

        private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender,
                                           PlaybackInfoChangedEventArgs args)
        {
            CheckFastPlaybackState();
            QueueRefresh();
        }

        /// <summary>
        /// 极速同步检测当前会话播放状态。
        /// 在主界面定时器（每 200~250ms）或媒体事件触发时调用，开销 &lt;0.01ms。
        /// 彻底防止部分播放器（网易云、QQ音乐等）丢发 WinRT PlaybackInfoChanged 事件导致状态不同步。
        /// </summary>
        public void CheckFastPlaybackState()
        {
            if (_disposed) return;
            var session = _session;
            if (session == null || _current == null)
            {
                return;
            }

            try
            {
                var playback = session.GetPlaybackInfo();
                if (playback != null)
                {
                    var reported = MapState(playback.PlaybackStatus);
                    _reportedState = reported;
                    if (reported != PlaybackState.Playing)
                    {
                        _audioActiveLatched = false;
                        _audioVetoLatched = false;
                    }
                    var newState = ResolveState(reported);
                    if (newState != _current.State)
                    {
                        var controls = playback.Controls;
                        var updated = _current with
                        {
                            State = newState,
                            CanNext = controls?.IsNextEnabled ?? _current.CanNext,
                            CanPrevious = controls?.IsPreviousEnabled ?? _current.CanPrevious
                        };
                        _current = updated;
                        // 状态跃迁的同时校准外推基准：暂停→播放若沿用旧的 _positionStampUtc，
                        // 会把整段暂停时长当成"已播放过的时间"加回位置（位置瞬间前跳）。
                        _positionStampUtc = DateTime.UtcNow;
                        Publish(updated);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SMTC] CheckFastPlaybackState 异常: " + ex.Message);
                QueueRefresh();
            }
        }

        private GlobalSystemMediaTransportControlsSession? GetBestSession()
        {
            if (_manager == null) return null;
            try
            {
                var cur = _manager.GetCurrentSession();
                if (cur != null)
                {
                    var pb = cur.GetPlaybackInfo();
                    if (pb != null && pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        return cur;
                    }
                }

                // GetCurrentSession 没在播放或为 null 时，遍历系统所有媒体会话：
                // 1. 优先拾取正在播放的会话
                var sessions = _manager.GetSessions();
                GlobalSystemMediaTransportControlsSession? candidate = null;
                GlobalSystemMediaTransportControlsSession? currentSessionInList = null;

                foreach (var s in sessions)
                {
                    try
                    {
                        var pb = s.GetPlaybackInfo();
                        if (pb != null && pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        {
                            return s;
                        }

                        if (_session != null && IsSameSession(s, _session))
                        {
                            currentSessionInList = s;
                        }

                        if (candidate == null)
                        {
                            candidate = s;
                        }
                    }
                    catch { }
                }

                // 2. 没有任何会话在播放时：若当前绑定的会话仍存在（即刚暂停的曲目），坚守该会话！
                // 避免错误漂移到后台其他无媒体或休眠的浏览器进程，导致暂停提示被吞并误报无媒体。
                if (currentSessionInList != null)
                {
                    return currentSessionInList;
                }

                return cur ?? candidate;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SMTC] GetBestSession 异常: " + ex.Message);
                return null;
            }
        }

        private void Attach()
        {
            _session = GetBestSession();
            if (_session == null) return;
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }

        private void Detach()
        {
            if (_session == null) return;
            try
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
            _session = null;
        }

        // ---------------- 刷新（合并高频事件） ----------------

        private void QueueRefresh()
        {
            if (_disposed) return;
            if (_dispatcher.CheckAccess()) _ = RefreshSafeAsync();
            else _dispatcher.BeginInvoke(new Action(() => _ = RefreshSafeAsync()));
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
                    await RefreshCoreAsync();
                }
                while (_refreshPending && !_disposed);
            }
            catch (Exception ex) { Debug.WriteLine("[SMTC] 刷新异常: " + ex); }
            finally { _refreshing = false; }
        }

        /// <summary>强制刷新一次（供外部在需要时调用）。</summary>
        public Task RefreshAsync() => RefreshSafeAsync();

        private static bool IsSameSession(GlobalSystemMediaTransportControlsSession? a, GlobalSystemMediaTransportControlsSession? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            try
            {
                return string.Equals(a.SourceAppUserModelId, b.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private async Task RefreshCoreAsync()
        {
            var session = GetBestSession();

            if (!IsSameSession(session, _session))
            {
                Detach();
                Attach();
            }

            if (_session == null)
            {
                Publish(null);
                return;
            }

            var props = await _session.TryGetMediaPropertiesAsync().AsTask();
            var playback = _session.GetPlaybackInfo();
            var timeline = _session.GetTimelineProperties();

            string title = props?.Title ?? "";
            string artist = props?.Artist ?? "";
            string album = props?.AlbumTitle ?? "";
            string source = "";
            try { source = _session.SourceAppUserModelId ?? ""; } catch { }

            // 某些播放器（网易云、QQ音乐等）切歌瞬间属性异步就绪有极短延迟，重试一次
            if (string.IsNullOrEmpty(title) && playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                await Task.Delay(100).ConfigureAwait(false);
                if (_session != null)
                {
                    props = await _session.TryGetMediaPropertiesAsync().AsTask();
                    title = props?.Title ?? "";
                    artist = props?.Artist ?? "";
                    album = props?.AlbumTitle ?? "";
                }
            }

            // 封面：按「曲目身份」缓存，避免每次事件都重新解码
            string key = title + "\u0001" + artist + "\u0001" + album;
            if (!string.Equals(key, _artworkKey, StringComparison.Ordinal))
            {
                _artworkKey = key;
                _artwork = await LoadArtworkAsync(props?.Thumbnail);
            }

            var state = MapState(playback?.PlaybackStatus);
            _reportedState = state;
            if (state != PlaybackState.Playing)
            {
                _audioActiveLatched = false;
                _audioVetoLatched = false;
            }
            // 对外只呈现经音频仲裁后的状态：播放器谎报 Playing 而环回持续无音频时降级为 Paused
            state = ResolveState(state);
            var controls = playback?.Controls;
            _playbackRate = playback?.PlaybackRate ?? 1.0;
            if (_playbackRate <= 0) _playbackRate = 1.0;

            TimeSpan duration = TimeSpan.Zero;
            TimeSpan position = TimeSpan.Zero;
            if (timeline != null)
            {
                duration = timeline.EndTime - timeline.StartTime;
                if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
                position = timeline.Position - timeline.StartTime;
                if (position < TimeSpan.Zero) position = TimeSpan.Zero;
                if (duration > TimeSpan.Zero && position > duration) position = duration;

                DateTimeOffset lastUpdated = timeline.LastUpdatedTime;
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
            }
            else
            {
                _positionStampUtc = DateTime.UtcNow;
            }

            Publish(new NowPlayingInfo
            {
                Title = title,
                Artist = artist,
                Album = album,
                SourceApp = source,
                Artwork = _artwork,
                State = state,
                Position = position,
                Duration = duration,
                CanNext = controls?.IsNextEnabled ?? false,
                CanPrevious = controls?.IsPreviousEnabled ?? false,
                CanSeek = (controls?.IsPlaybackPositionEnabled ?? false) && duration > TimeSpan.Zero
            });
        }

        private void Publish(NowPlayingInfo? info)
        {
            _current = info;
            if (_dispatcher.CheckAccess())
            {
                NowPlayingChanged?.Invoke(this, info);
            }
            else
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    NowPlayingChanged?.Invoke(this, info);
                }));
            }
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

        private static async Task<BitmapSource?> LoadArtworkAsync(Windows.Storage.Streams.IRandomAccessStreamReference? reference)
        {
            if (reference == null) return null;
            try
            {
                using var winrtStream = await reference.OpenReadAsync().AsTask();
                using var netStream = winrtStream.AsStreamForRead();
                using var ms = new MemoryStream();
                await netStream.CopyToAsync(ms);
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

        public async Task TogglePlayPauseAsync()
        {
            var s = _session;
            if (s == null) return;
            try { await s.TryTogglePlayPauseAsync().AsTask(); }
            catch (Exception ex) { Debug.WriteLine(ex); }
            CheckFastPlaybackState();
            QueueRefresh();
        }

        public async Task NextAsync()
        {
            var s = _session;
            if (s == null) return;
            try { await s.TrySkipNextAsync().AsTask(); }
            catch (Exception ex) { Debug.WriteLine(ex); }
            QueueRefresh();
        }

        public async Task PreviousAsync()
        {
            var s = _session;
            if (s == null) return;
            try { await s.TrySkipPreviousAsync().AsTask(); }
            catch (Exception ex) { Debug.WriteLine(ex); }
            QueueRefresh();
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
                await s.TryChangePlaybackPositionAsync(ticks).AsTask();
                // 乐观更新，避免进度条在事件回来之前乱跳
                var cur = _current;
                if (cur != null)
                {
                    _current = new NowPlayingInfo
                    {
                        Title = cur.Title,
                        Artist = cur.Artist,
                        Album = cur.Album,
                        SourceApp = cur.SourceApp,
                        Artwork = cur.Artwork,
                        State = cur.State,
                        Position = position,
                        Duration = cur.Duration,
                        CanNext = cur.CanNext,
                        CanPrevious = cur.CanPrevious,
                        CanSeek = cur.CanSeek
                    };
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

            try
            {
                Detach();
                if (_manager != null)
                {
                    _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
                    _manager.SessionsChanged -= OnSessionsChanged;
                }
            }
            catch (Exception ex) { Debug.WriteLine(ex); }

            _manager = null;
        }
    }
}
