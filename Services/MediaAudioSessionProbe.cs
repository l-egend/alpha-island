using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace alpha_island.Services;

internal sealed record MediaStreamObservation(string Source, string SessionId, bool Running, long StableSinceMs, long SampledAtMs, float? Peak = null);

/// <summary>Read running streams, independently of slow mixer/app-label enumeration.
/// Silence and a stopped stream are different: quiet passages and muted music remain active.</summary>
internal sealed class MediaAudioSessionProbe : IDisposable, IMMNotificationClient
{
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _worker;
    private volatile bool _disposed;
    private string _source = "", _sessionId = "";
    private int _devicesDirty = 1, _sessionsDirty = 1;
    private MediaStreamObservation? _current;
    internal MediaStreamObservation? Current => Volatile.Read(ref _current);
    internal event Action? Changed;

    internal MediaAudioSessionProbe()
    {
        _worker = new Thread(Run, 256 * 1024) { IsBackground = true, Name = "MediaStreamState" };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    internal void SetTarget(string source, string sessionId)
    {
        if (_disposed || source == Volatile.Read(ref _source) && sessionId == Volatile.Read(ref _sessionId)) return;
        Volatile.Write(ref _current, null);
        Volatile.Write(ref _sessionId, sessionId);
        Volatile.Write(ref _source, source);
        Interlocked.Exchange(ref _sessionsDirty, 1); Wake();
    }

    private void Run()
    {
        MMDeviceEnumerator? enumerator = null;
        var devices = new List<MMDevice>();
        var controls = new List<AudioSessionControl>();
        string boundSource = "", boundSession = "";
        long nextProcessRefresh = 0;
        try
        {
            enumerator = new MMDeviceEnumerator();
            enumerator.RegisterEndpointNotificationCallback(this);
            while (!_disposed)
            {
                try
                {
                    string source = Volatile.Read(ref _source), session = Volatile.Read(ref _sessionId);
                    if (string.IsNullOrEmpty(source)) { _wake.WaitOne(1000); continue; }
                    if (Interlocked.Exchange(ref _devicesDirty, 0) != 0)
                    {
                        controls.Clear();
                        foreach (var device in devices) { try { device.AudioSessionManager.OnSessionCreated -= OnSessionCreated; device.Dispose(); } catch { } }
                        devices.Clear();
                        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                        {
                            devices.Add(device); device.AudioSessionManager.OnSessionCreated += OnSessionCreated;
                        }
                        Interlocked.Exchange(ref _sessionsDirty, 1);
                    }
                    bool sessionsDirty = Interlocked.Exchange(ref _sessionsDirty, 0) != 0;
                    if (source != boundSource || session != boundSession || sessionsDirty
                        || Environment.TickCount64 >= nextProcessRefresh)
                    {
                        boundSource = source; boundSession = session; nextProcessRefresh = Environment.TickCount64 + 10000;
                        controls.Clear();
                        string processName = System.IO.Path.GetFileNameWithoutExtension(source);
                        if (!source.Contains('!'))
                        {
                            var ids = Process.GetProcessesByName(processName).Select(p => { using (p) return (uint)p.Id; }).ToHashSet();
                            foreach (var device in devices)
                            {
                                var manager = device.AudioSessionManager; manager.RefreshSessions();
                                for (int i = 0; i < manager.Sessions.Count; i++)
                                {
                                    var control = manager.Sessions[i];
                                    if (ids.Contains(control.GetProcessID)) controls.Add(control);
                                }
                            }
                        }
                    }
                    bool found = false, running = false;
                    float? peak = null;
                    bool unreadableActiveMeter = false;
                    foreach (var control in controls)
                    {
                        try
                        {
                            var state = control.State;
                            if (state == AudioSessionState.AudioSessionStateExpired) continue;
                            bool active = state == AudioSessionState.AudioSessionStateActive;
                            found = true; running |= active;
                            // This worker already follows every render endpoint and new session.
                            // Read own-app audio here rather than waiting for mixer list enumeration.
                            float value = 0;
                            if (active)
                            {
                                try { value = control.AudioMeterInformation.MasterPeakValue; }
                                catch { unreadableActiveMeter = true; continue; }
                                if (!float.IsFinite(value) || value is < 0 or > 1)
                                { unreadableActiveMeter = true; continue; }
                            }
                            if (float.IsFinite(value) && value is >= 0 and <= 1)
                                peak = Math.Max(peak ?? 0, value);
                        }
                        catch { Interlocked.Exchange(ref _sessionsDirty, 1); }
                    }
                    if (unreadableActiveMeter && (peak ?? 0) == 0) peak = null;
                    if (source != Volatile.Read(ref _source) || session != Volatile.Read(ref _sessionId)) continue;
                    long now = Environment.TickCount64;
                    var old = Current;
                    var next = found ? new MediaStreamObservation(source, session, running,
                        old != null && old.Source == source && old.SessionId == session && old.Running == running ? old.StableSinceMs : now, now, peak) : null;
                    Volatile.Write(ref _current, next);
                    // Publish a stable transition once, never once per sample.
                    if (next != null && now - next.StableSinceMs >= 180 && (old == null || old.SampledAtMs - old.StableSinceMs < 180)) Changed?.Invoke();
                    _wake.WaitOne(100);
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref _current, null);
                    Interlocked.Exchange(ref _devicesDirty, 1);
                    alpha_island.Core.IslandLog.Debug("MediaStream", "音频设备刷新稍后重试：" + ex.GetType().Name);
                    _wake.WaitOne(1000);
                }
            }
        }
        catch (Exception ex) { alpha_island.Core.IslandLog.Debug("MediaStream", "音频流探测不可用：" + ex.GetType().Name); }
        finally
        {
            foreach (var device in devices) { try { device.AudioSessionManager.OnSessionCreated -= OnSessionCreated; device.Dispose(); } catch { } }
            _disposed = true;
            try { enumerator?.UnregisterEndpointNotificationCallback(this); enumerator?.Dispose(); } catch { }
            _wake.Dispose();
        }
    }

    private void OnSessionCreated(object sender, IAudioSessionControl session) { Interlocked.Exchange(ref _sessionsDirty, 1); Wake(); }
    private void Wake() { if (!_disposed) { try { _wake.Set(); } catch (ObjectDisposedException) { } } }
    public void OnDeviceStateChanged(string id, DeviceState state) { Interlocked.Exchange(ref _devicesDirty, 1); Wake(); }
    public void OnDeviceAdded(string id) => OnDeviceStateChanged(id, DeviceState.Active);
    public void OnDeviceRemoved(string id) => OnDeviceStateChanged(id, DeviceState.NotPresent);
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string id) => OnDeviceStateChanged(id, DeviceState.Active);
    public void OnPropertyValueChanged(string id, PropertyKey key) { }
    public void Dispose() { if (_disposed) return; _disposed = true; try { _wake.Set(); } catch (ObjectDisposedException) { } }
}
