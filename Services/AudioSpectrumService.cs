using System;
using System.Diagnostics;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace alpha_island.Services
{
    /// <summary>
    /// 系统音频频谱采集：WASAPI Loopback 抓默认播放设备的混音输出 → FFT → 对数频段 → 平滑。
    ///
    /// 说明：
    ///  * SMTC 只给「元数据」，拿不到波形；要可视化必须自己抓音频。
    ///  * Loopback 抓的是整机混音（所有正在发声的程序），这与「灵动岛随音乐跳动」的预期一致。
    ///  * SpectrumUpdated 在采集线程/看门狗线程上触发，订阅方需自行保证线程安全。
    ///
    /// 锁纪律（很重要，改之前先读）：
    ///  * _gate      —— 只保护音频数据缓冲（_acc / _smoothed / 分析参数）。
    ///                  采集线程只拿这把锁，且持锁时间极短。
    ///  * _lifecycle —— 串行化采集设备的创建/销毁。
    ///  * WasapiCapture.StopRecording()/Dispose() 内部会 Join 采集线程，
    ///    而采集线程可能正阻塞在 _gate 上，所以**绝对不能在持有 _gate 时调用它们**，
    ///    否则退出程序时随机死锁。锁顺序固定为 _lifecycle → _gate。
    /// </summary>
    public sealed class AudioSpectrumService : IDisposable
    {
        /// <summary>输出的频段数量（可视化控件会自行重采样到实际柱数）。</summary>
        public const int BandCount = 64;

        private const int FftSize = 2048;          // ~43ms @48k，足够稳
        private const int FftLog2 = 11;            // 2^11 == FftSize
        private const int HopSize = 1024;          // 50% 重叠，一跳一次 FFT（~21ms）
        private const int FftBins = FftSize / 2;
        private const int AccCapacity = FftSize * 4;
        private const double PublishIntervalMs = 13.0;

        private readonly object _gate = new();
        private readonly object _lifecycle = new();

        private readonly float[] _acc = new float[AccCapacity];
        private int _accCount;

        private readonly Complex[] _fftData = new Complex[FftSize];
        private readonly float[] _window = new float[FftSize];
        private readonly double[] _bandFreq = new double[BandCount + 1];
        private readonly float[] _tilt = new float[BandCount];
        private readonly float[] _smoothed = new float[BandCount];

        private WasapiLoopbackCapture? _capture;
        private MMDevice? _device;
        private MMDeviceEnumerator? _enumerator;

        // 仅由 _gate 保护
        private int _channels = 2;
        private int _bytesPerSample = 4;
        private int _sampleRate = 48000;
        private long _lastPublishTicks;

        // 仅由 _lifecycle 保护
        private string? _deviceId;
        private long _lastStartAttemptTicks;

        private long _lastDataTicks;
        private volatile bool _running;
        private volatile string? _deviceName;
        private volatile string? _lastError;
        private volatile bool _disposed;

        private readonly System.Threading.Timer _watchdog;

        /// <summary>参数为长度 BandCount 的 float[]（0..1 的新数组，可安全持有）。</summary>
        public event EventHandler<float[]>? SpectrumUpdated;

        /// <summary>灵敏度倍数，1.0 为默认。</summary>
        public float Sensitivity { get; set; } = 1.0f;

        /// <summary>低于该分贝视为静音（噪声门）。</summary>
        public float NoiseFloorDb { get; set; } = -80f;

        /// <summary>达到该分贝即为满格。</summary>
        public float CeilingDb { get; set; } = -14f;

        public bool IsRunning => _running;
        public string? LastError => _lastError;
        public string? DeviceName => _deviceName;

        public AudioSpectrumService()
        {
            // 汉宁窗，抑制频谱泄漏
            for (int i = 0; i < FftSize; i++)
                _window[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1))));

            BuildBandMap(_sampleRate);

            _watchdog = new System.Threading.Timer(WatchdogTick, null, 800, 400);
        }

        // ---------------- 频段映射（必须在 _gate 内调用） ----------------

        private void BuildBandMap(int sampleRate)
        {
            double nyquist = sampleRate * 0.5;
            double fMin = 40.0;
            double fMax = Math.Min(16000.0, nyquist * 0.95);
            if (fMax <= fMin * 1.2) fMax = nyquist * 0.95;

            for (int b = 0; b <= BandCount; b++)
            {
                double t = (double)b / BandCount;
                _bandFreq[b] = fMin * Math.Pow(fMax / fMin, t);
            }

            for (int b = 0; b < BandCount; b++)
            {
                double mid = Math.Sqrt(_bandFreq[b] * _bandFreq[b + 1]);
                // 音乐频谱天然高频衰减，做一点倾斜补偿，视觉才均衡
                _tilt[b] = (float)Math.Min(2.3, Math.Pow(Math.Max(mid, 60.0) / 700.0, 0.26));
            }
        }

        // ---------------- 启停 ----------------

        public void Start()
        {
            if (_disposed) return;
            lock (_lifecycle) StartCoreLocked();
        }

        public void Stop()
        {
            lock (_lifecycle) StopCoreLocked();
            lock (_gate)
            {
                _accCount = 0;
                Array.Clear(_smoothed, 0, _smoothed.Length);
            }
        }

        /// <summary>必须在 _lifecycle 内调用。内部绝不获取 _gate 后再调 NAudio 的停止/释放。</summary>
        private void StartCoreLocked()
        {
            StopCoreLocked();

            WasapiLoopbackCapture? capture = null;
            MMDevice? device = null;
            try
            {
                _enumerator ??= new MMDeviceEnumerator();
                device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                capture = new WasapiLoopbackCapture(device);
                var fmt = capture.WaveFormat;

                lock (_gate)
                {
                    _channels = Math.Max(1, fmt.Channels);
                    _bytesPerSample = Math.Max(2, fmt.BitsPerSample / 8);
                    _sampleRate = fmt.SampleRate > 0 ? fmt.SampleRate : 48000;
                    BuildBandMap(_sampleRate);
                    _accCount = 0;
                    Array.Clear(_smoothed, 0, _smoothed.Length);
                }

                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                capture.StartRecording();

                _capture = capture;
                _device = device;
                _deviceId = device.ID;
                _deviceName = device.FriendlyName;
                Interlocked.Exchange(ref _lastDataTicks, Stopwatch.GetTimestamp());
                _lastError = null;
                _running = true;

                Debug.WriteLine($"[Audio] loopback 已启动: {_deviceName} / {fmt.Encoding} {fmt.SampleRate}Hz {fmt.Channels}ch {fmt.BitsPerSample}bit");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Audio] 启动失败: " + ex);
                _lastError = ex.Message;
                _running = false;
                _deviceName = null;

                if (capture != null)
                {
                    try { capture.DataAvailable -= OnDataAvailable; } catch { }
                    try { capture.RecordingStopped -= OnRecordingStopped; } catch { }
                    try { capture.Dispose(); } catch { }
                }
                try { device?.Dispose(); } catch { }
            }
        }

        /// <summary>必须在 _lifecycle 内调用；不得持有 _gate。</summary>
        private void StopCoreLocked()
        {
            var capture = _capture;
            _capture = null;
            _running = false;

            if (capture != null)
            {
                try
                {
                    capture.DataAvailable -= OnDataAvailable;
                    capture.RecordingStopped -= OnRecordingStopped;
                    // StopRecording/Dispose 会 Join 采集线程 —— 此时不能持有 _gate
                    capture.StopRecording();
                    capture.Dispose();
                }
                catch (Exception ex) { Debug.WriteLine(ex); }
            }

            try { _device?.Dispose(); } catch { }
            _device = null;
            _deviceId = null;
            _deviceName = null;
        }

        // ---------------- 采集 ----------------

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_disposed || e.BytesRecorded <= 0) return;
            Interlocked.Exchange(ref _lastDataTicks, Stopwatch.GetTimestamp());

            float[]? frame = null;
            try
            {
                lock (_gate)
                {
                    AppendMono(e.Buffer, e.BytesRecorded);
                    while (_accCount >= FftSize)
                    {
                        AnalyzeLocked();
                        int remain = _accCount - HopSize;
                        if (remain > 0) Array.Copy(_acc, HopSize, _acc, 0, remain);
                        _accCount = remain;
                    }
                    frame = SnapshotIfDueLocked();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Audio] 处理异常: " + ex.Message);
                return;
            }

            // 事件在锁外触发：订阅方再慢也不会拖住采集线程
            if (frame != null) SpectrumUpdated?.Invoke(this, frame);
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                Debug.WriteLine("[Audio] 采集中断: " + e.Exception.Message);
                _lastError = e.Exception.Message;
            }
        }

        /// <summary>把交错的多声道数据混成单声道，追加进分析缓冲。必须在 _gate 内调用。</summary>
        private void AppendMono(byte[] buffer, int bytes)
        {
            int ch = _channels;
            int bps = _bytesPerSample;
            int frameSize = bps * ch;
            if (frameSize <= 0) return;

            int frames = bytes / frameSize;
            for (int f = 0; f < frames; f++)
            {
                if (_accCount >= AccCapacity) break;
                int off = f * frameSize;
                float sum = 0f;
                for (int c = 0; c < ch; c++)
                {
                    int o = off + c * bps;
                    sum += bps == 4
                        ? BitConverter.ToSingle(buffer, o)
                        : BitConverter.ToInt16(buffer, o) / 32768f;
                }
                _acc[_accCount++] = sum / ch;
            }
        }

        /// <summary>对缓冲里最新的一帧做 FFT 并更新 _smoothed。必须在 _gate 内调用。</summary>
        private void AnalyzeLocked()
        {
            for (int i = 0; i < FftSize; i++)
            {
                _fftData[i].X = _acc[i] * _window[i];
                _fftData[i].Y = 0f;
            }

            FastFourierTransform.FFT(true, FftLog2, _fftData);

            // 重要：NAudio 的 FastFourierTransform 内部已按 1/N 归一化
            // （幅度 1.0 的满幅正弦 + 汉宁窗，这里得到约 0.25），所以只需 ×4 就还原到 0 dBFS。
            // 早期版本这里又除了一次 FftSize，导致所有频段落到噪声门以下、柱状图恒为空。
            const float MagScale = 4f;
            double floor = NoiseFloorDb;
            double range = Math.Max(6.0, CeilingDb - floor);
            float sens = Sensitivity;

            for (int b = 0; b < BandCount; b++)
            {
                double f0 = _bandFreq[b];
                double f1 = _bandFreq[b + 1];

                // 频段内按对数取 5 个采样点取最大值，避免高频宽带"漏采"
                double mag = 0;
                const int Probes = 4;
                for (int s = 0; s <= Probes; s++)
                {
                    double f = f0 * Math.Pow(f1 / f0, (double)s / Probes);
                    double m = InterpolatedMagnitude(f);
                    if (m > mag) mag = m;
                }
                mag *= MagScale;

                double db = 20.0 * Math.Log10(mag + 1e-7);
                double level = (db - floor) / range;
                level *= _tilt[b] * sens;
                if (level < 0) level = 0; else if (level > 1) level = 1;

                float prev = _smoothed[b];
                // 快起慢落，观感更"跟手"又不抖
                float coeff = level > prev ? 0.68f : 0.17f;
                _smoothed[b] = prev + ((float)level - prev) * coeff;
            }
        }

        private double InterpolatedMagnitude(double freq)
        {
            double nyquist = _sampleRate * 0.5;
            if (nyquist <= 0) return 0;

            double binPos = freq / nyquist * FftBins;
            int i0 = (int)binPos;
            if (i0 < 0) i0 = 0;
            if (i0 > FftBins - 2) i0 = FftBins - 2;
            double frac = binPos - i0;
            if (frac < 0) frac = 0; else if (frac > 1) frac = 1;

            double x0 = _fftData[i0].X, y0 = _fftData[i0].Y;
            double x1 = _fftData[i0 + 1].X, y1 = _fftData[i0 + 1].Y;
            double m0 = Math.Sqrt(x0 * x0 + y0 * y0);
            double m1 = Math.Sqrt(x1 * x1 + y1 * y1);
            return m0 + (m1 - m0) * frac;
        }

        /// <summary>按节流间隔取一份可发布的快照；未到时间返回 null。必须在 _gate 内调用。</summary>
        private float[]? SnapshotIfDueLocked()
        {
            long now = Stopwatch.GetTimestamp();
            double sinceMs = (now - _lastPublishTicks) * 1000.0 / Stopwatch.Frequency;
            if (sinceMs < PublishIntervalMs) return null;
            _lastPublishTicks = now;

            var snapshot = new float[BandCount];
            Array.Copy(_smoothed, snapshot, BandCount);
            return snapshot;
        }

        // ---------------- 看门狗：静音衰减 / 设备切换恢复 ----------------

        private void WatchdogTick(object? state)
        {
            if (_disposed) return;
            try
            {
                long now = Stopwatch.GetTimestamp();
                double idleMs = (now - Interlocked.Read(ref _lastDataTicks)) * 1000.0 / Stopwatch.Frequency;

                bool needRestart = false;
                lock (_lifecycle)
                {
                    if (!_running)
                    {
                        double sinceStart = (now - _lastStartAttemptTicks) * 1000.0 / Stopwatch.Frequency;
                        if (sinceStart > 5000)
                        {
                            _lastStartAttemptTicks = now;
                            StartCoreLocked();
                        }
                        return;
                    }

                    // 插拔耳机/切换默认设备后，旧设备的 loopback 只会一直吐静音
                    if (idleMs > 2000 && DefaultDeviceChangedLocked())
                        needRestart = true;
                }

                if (needRestart)
                {
                    Debug.WriteLine("[Audio] 默认播放设备已改变，重启采集");
                    lock (_lifecycle) StartCoreLocked();
                    return;
                }

                if (idleMs > 900)
                {
                    // 没声音了：让柱子自然落下去
                    float[]? frame;
                    lock (_gate)
                    {
                        for (int i = 0; i < BandCount; i++) _smoothed[i] *= 0.80f;
                        frame = SnapshotIfDueLocked();
                    }
                    if (frame != null) SpectrumUpdated?.Invoke(this, frame);
                }
            }
            catch (Exception ex) { Debug.WriteLine("[Audio] watchdog: " + ex.Message); }
        }

        /// <summary>必须在 _lifecycle 内调用。</summary>
        private bool DefaultDeviceChangedLocked()
        {
            if (_deviceId == null) return false;
            try
            {
                _enumerator ??= new MMDeviceEnumerator();
                using var current = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return !string.Equals(current.ID, _deviceId, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _watchdog.Dispose(); } catch { }
            try { Stop(); } catch (Exception ex) { Debug.WriteLine(ex); }

            try { _enumerator?.Dispose(); } catch { }
            _enumerator = null;
        }
    }
}
