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

        /// <summary>参数为 null 表示当前没有任何媒体会话。</summary>
        public event EventHandler<NowPlayingInfo?>? NowPlayingChanged;
        /// <summary>SMTC 初始化失败时触发，参数为错误描述。</summary>
        public event EventHandler<string>? Failed;

        public NowPlayingInfo? Current => _current;
        public bool IsAvailable => _manager != null;

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
            if (_manager != null)
            {
                var best = GetBestSession();
                if (best != null && !IsSameSession(best, _session))
                {
                    QueueRefresh();
                    return;
                }
            }

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
                    var newState = MapState(playback.PlaybackStatus);
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
                        Publish(updated);
                    }
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
                if (cur != null)
                {
                    var pb = cur.GetPlaybackInfo();
                    if (pb != null && pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        return cur;
                    }
                }

                // GetCurrentSession 没在播放或为 null 时，遍历系统所有媒体会话，优先拾取正在播放的会话
                var sessions = _manager.GetSessions();
                GlobalSystemMediaTransportControlsSession? candidate = null;
                foreach (var s in sessions)
                {
                    try
                    {
                        var pb = s.GetPlaybackInfo();
                        if (pb != null && pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        {
                            return s;
                        }
                        if (candidate == null)
                        {
                            candidate = s;
                        }
                    }
                    catch { }
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

                var session = _session;
                if (session != null)
                {
                    try
                    {
                        var tl = session.GetTimelineProperties();
                        if (tl != null)
                        {
                            var lastUpdated = tl.LastUpdatedTime;
                            if (lastUpdated != default)
                            {
                                var now = DateTimeOffset.UtcNow;
                                var sinceUpdate = now - lastUpdated;
                                // 允许系统时钟与媒体时间戳之间 1 秒以内的微小超前抖动
                                if (sinceUpdate < TimeSpan.Zero && sinceUpdate >= TimeSpan.FromSeconds(-1))
                                {
                                    sinceUpdate = TimeSpan.Zero;
                                }

                                if (sinceUpdate >= TimeSpan.Zero)
                                {
                                    var realPos = (tl.Position - tl.StartTime) + TimeSpan.FromSeconds(sinceUpdate.TotalSeconds * _playbackRate);
                                    if (realPos < TimeSpan.Zero) realPos = TimeSpan.Zero;
                                    if (info.Duration > TimeSpan.Zero && realPos > info.Duration) realPos = info.Duration;
                                    return realPos;
                                }
                            }
                        }
                    }
                    catch { }
                }

                var elapsed = DateTime.UtcNow - _positionStampUtc;
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                var pos = info.Position + TimeSpan.FromSeconds(elapsed.TotalSeconds * _playbackRate);
                return pos > info.Duration ? info.Duration : pos;
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
