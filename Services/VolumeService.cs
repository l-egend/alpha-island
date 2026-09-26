using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace alpha_island.Services
{
    /// <summary>系统音量变更事件数据。</summary>
    public sealed record VolumeInfo(int Percent, bool IsMuted);

    /// <summary>
    /// Windows 系统主音量监听服务。
    ///
    /// 核心职责：
    ///  1. 基于 NAudio CoreAudioApi 挂钩当前默认音频输出端点的 <see cref="AudioEndpointVolume"/>；
    ///  2. 监听 <c>OnVolumeNotification</c>，在系统音量或静音状态发生变化时即刻派发事件；
    ///  3. 监听 <see cref="IMMNotificationClient.OnDefaultDeviceChanged"/>，默认音频设备热插拔切换时自动重新绑定。
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public sealed class VolumeService : IDisposable, IMMNotificationClient
    {
        private readonly Dispatcher _dispatcher;
        private readonly object _lock = new();

        private MMDeviceEnumerator? _enumerator;
        private MMDevice? _currentDevice;
        private AudioEndpointVolume? _volume;
        private bool _disposed;
        private bool _hasInitialValue;
        private int _lastPercent = -1;
        private bool _lastMuted;

        /// <summary>音量或静音状态变更时触发。</summary>
        public event EventHandler<VolumeInfo>? VolumeChanged;

        /// <summary>当前主音量百分比 (0~100)。</summary>
        public int CurrentPercent => _lastPercent >= 0 ? _lastPercent : 0;

        /// <summary>当前是否处于静音状态。</summary>
        public bool IsMuted => _lastMuted;

        /// <summary>当前默认音频设备名称。</summary>
        public string CurrentDeviceName => _currentDevice?.FriendlyName ?? "系统扬声器";

        public VolumeService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        /// <summary>调节系统音量增减（如 delta = +2 或 -2）。</summary>
        public void AdjustVolume(int delta)
        {
            lock (_lock)
            {
                if (_volume == null) return;
                try
                {
                    int target = Math.Clamp(CurrentPercent + delta, 0, 100);
                    _volume.MasterVolumeLevelScalar = target / 100f;
                    _lastPercent = target;
                    if (_volume.Mute && target > 0)
                    {
                        _volume.Mute = false;
                        _lastMuted = false;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[VolumeService] 调节音量失败: " + ex.Message);
                }
            }
        }

        /// <summary>直接设置系统主音量百分比 (0~100)。</summary>
        public void SetVolume(int percent)
        {
            lock (_lock)
            {
                if (_volume == null) return;
                try
                {
                    percent = Math.Clamp(percent, 0, 100);
                    _volume.MasterVolumeLevelScalar = percent / 100f;
                    _lastPercent = percent;
                    if (_volume.Mute && percent > 0)
                    {
                        _volume.Mute = false;
                        _lastMuted = false;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[VolumeService] 设置音量失败: " + ex.Message);
                }
            }
        }

        /// <summary>切换系统静音状态。</summary>
        public void ToggleMute()
        {
            lock (_lock)
            {
                if (_volume == null) return;
                try
                {
                    bool next = !_volume.Mute;
                    _volume.Mute = next;
                    _lastMuted = next;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[VolumeService] 切换静音失败: " + ex.Message);
                }
            }
        }

        /// <summary>启动音量监听并获取初始状态。</summary>
        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VolumeService));

            lock (_lock)
            {
                try
                {
                    _enumerator = new MMDeviceEnumerator();
                    _enumerator.RegisterEndpointNotificationCallback(this);
                    BindCurrentEndpoint();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[VolumeService] 启动失败: " + ex.Message);
                }
            }
        }

        private void BindCurrentEndpoint()
        {
            try
            {
                UnbindCurrentEndpoint();

                if (_enumerator == null) return;
                try
                {
                    _currentDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }
                catch
                {
                    try
                    {
                        _currentDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                    }
                    catch { }
                }

                if (_currentDevice == null) return;

                _volume = _currentDevice.AudioEndpointVolume;
                if (_volume == null) return;

                float master = _volume.MasterVolumeLevelScalar;
                int percent = (int)Math.Round(master * 100f);
                bool muted = _volume.Mute;

                _lastPercent = Math.Clamp(percent, 0, 100);
                _lastMuted = muted;
                _hasInitialValue = true;

                _volume.OnVolumeNotification += HandleVolumeNotification;
                Debug.WriteLine($"[VolumeService] 已绑定音频终结点: {_currentDevice.FriendlyName}, 音量: {_lastPercent}%, 静音: {_lastMuted}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[VolumeService] 绑定终结点异常: " + ex.Message);
            }
        }

        private void UnbindCurrentEndpoint()
        {
            if (_volume != null)
            {
                try { _volume.OnVolumeNotification -= HandleVolumeNotification; } catch { }
                _volume = null;
            }
            _currentDevice?.Dispose();
            _currentDevice = null;
        }

        private void HandleVolumeNotification(AudioVolumeNotificationData data)
        {
            if (_disposed || !_hasInitialValue) return;

            int percent = Math.Clamp((int)Math.Round(data.MasterVolume * 100f), 0, 100);
            bool muted = data.Muted;

            // 仅在真实发生数值或静音状态变动时触发
            if (percent == _lastPercent && muted == _lastMuted) return;

            _lastPercent = percent;
            _lastMuted = muted;

            var info = new VolumeInfo(percent, muted);
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (!_disposed)
                {
                    VolumeChanged?.Invoke(this, info);
                }
            });
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                try
                {
                    if (_enumerator != null)
                    {
                        _enumerator.UnregisterEndpointNotificationCallback(this);
                        _enumerator.Dispose();
                        _enumerator = null;
                    }
                }
                catch { }

                UnbindCurrentEndpoint();
            }
        }

        // ===================== IMMNotificationClient =====================

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (_disposed) return;
            if (flow == DataFlow.Render && (role == Role.Multimedia || role == Role.Console))
            {
                lock (_lock)
                {
                    if (!_disposed)
                    {
                        Debug.WriteLine("[VolumeService] 默认输出设备切换，重新绑定...");
                        BindCurrentEndpoint();
                    }
                }
            }
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
