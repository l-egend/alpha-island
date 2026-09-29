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

        // —— 采集会话健康检查 ——
        // 故障背景：StartRecording() 成功、_running == true，但设备（尤其是蓝牙 A2DP 端点）
        // 当时还在休眠或被系统重新枚举，于是会话活着却永远收不到数据；旧版本没有任何检测，
        // 结果整轮运行都是"一条直线"，只有重启进程才能恢复。
        //
        // 实测（2026-09-25，本机 OPPO Enco Clip2 蓝牙端点）：会话变聋时，同一个端点上
        // 另起一个进程做回环采集能拿到 2.9MB 音频，而应用的会话 22 秒内只收到 0 字节回调；
        // 同时 MMDevice.AudioMeterInformation.MasterPeakValue 在**放音时依然是 0**，
        // 所以电平表不能作为触发条件，只能当"能读到就提前重建"的加速器。
        // 结论：唯一可靠的恢复手段是重建会话，因此静默超过退避周期就重建，
        // 退避 3s→10s 且只在真正收到音频数据时归零。上限取 10s 而不是更长，是为了保证
        // "用户在放音、频谱却是平线"最多持续一个退避周期；空闲时每次重建只是一次廉价的
        // COM 拆装（不产生任何音频渲染，对蓝牙链路无影响），而一旦真的采到数据，退避立刻
        // 归零、看门狗在整个播放期间都不会再重建。
        /// <summary>连续多少毫秒没有采到真实音频数据算"静默"，进入故障判定。</summary>
        private const double SilentProbeMs = 1000.0;
        /// <summary>重建会话的起始间隔；之后按 1.5 倍退避到 SilentRebindMaxMs。</summary>
        private const int SilentRebindBaseMs = 3000;
        private const int SilentRebindMaxMs = 10000;

        /// <summary>
        /// 原始电平（未做显示倾斜、未乘灵敏度）高出噪声地板这么多分贝，才认为"系统此刻真的在出声"。
        /// 早期阈值是归一化 level &gt; 0.02，换算到 66dB 的量程上只有约 1.3dB —— 等于任何底噪都算"在出声"。
        /// 蓝牙/共享模式 loopback 在暂停时送来的并不是全零缓冲，而是带抖动/本底噪声的非零数据，
        /// 于是静音时长永远涨不到阈值，音频侧仲裁（把播放器谎报的 Playing 降级为 Paused）从不生效，
        /// 表现为"暂停后歌词继续滚、频谱只剩一条线、展开岛仍显示播放"。取 12dB（约 -68dBFS）：
        /// 远高于底噪，又远低于任何真实音乐内容，间奏/清唱照样算"在出声"。
        /// </summary>
        private const double SignalMarginDb = 12.0;

        private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

        private readonly object _gate = new();
        private readonly object _lifecycle = new();

        private readonly float[] _acc = new float[AccCapacity];
        private int _accCount;

        private readonly Complex[] _fftData = new Complex[FftSize];
        private readonly float[] _window = new float[FftSize];
        private readonly double[] _bandFreq = new double[BandCount + 1];
        private readonly float[] _tilt = new float[BandCount];
        private readonly float[] _tiltDb = new float[BandCount];
        private readonly float[] _smoothed = new float[BandCount];

        private WasapiLoopbackCapture? _capture;
        private MMDevice? _device;
        private AudioMeterInformation? _meter;     // 端点电平表（区分“端点空闲”与“会话坏了”）
        private MMDeviceEnumerator? _enumerator;

        // 仅由 _gate 保护
        private int _channels = 2;
        private int _bytesPerSample = 4;
        private int _sampleRate = 48000;
        private bool _isFloat = true;
        private long _lastPublishTicks;

        // 仅由 _lifecycle 保护
        private string? _deviceId;
        private long _lastStartAttemptTicks;

        private long _lastDataTicks;
        private long _lastCallbackTicks;          // 任意 DataAvailable（含 0 字节）都刷新

        // 与 _lastDataTicks 的关键区别：_lastDataTicks 会被看门狗的重建（StartCoreLocked）
        // 反复刷新，所以只能观察到约 1s；_lastSignalTicks 只在公开的 Start()/Stop() 与构造时重置，
        // 看门狗重建绝不碰它，因此可以长期信任地表达"已经静音多久"（见 SilentSeconds）。
        private long _lastSignalTicks;
        private int _rebindBackoffMs = SilentRebindBaseMs;
        private int _sessionRebinds;              // 诊断：自动重建采集会话的次数
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

        /// <summary>
        /// 达到该分贝即为满格（满格点）。
        /// 注意它和 Sensitivity 是**相乘**关系：level = (db - floor) / range * sens，
        /// 所以灵敏度越大，等效满格点越低（越早顶格）。实测 -24f + sens=1.3 时的等效满格点
        /// 只有 -37 dBFS，音乐的中频段（123Hz~4.6kHz，正是能量主体）长期恒为 1.0 被钳死，
        /// 视觉上就是"中间三根顶满、只有两侧在动"。这里提到 -14f 把动态范围从 56dB 拉到 66dB，
        /// 中频段重新有余量，同时仍保留灵敏度滑块"整体抬升"的语义。
        /// </summary>
        public float CeilingDb { get; set; } = -14f;

        public bool IsRunning => _running;
        public string? LastError => _lastError;
        public string? DeviceName => _deviceName;

        /// <summary>
        /// 最近一帧里最高的原始频段分贝（未做显示倾斜、未乘灵敏度），诊断用：
        /// 把它和 <see cref="SignalMarginDb"/> + <see cref="NoiseFloorDb"/> 对照，
        /// 就能判断当前设备空闲时的底噪到底有多高、静音判据有没有作用空间。
        /// </summary>
        public double LastSignalDbPeak { get; private set; } = double.NegativeInfinity;

        /// <summary>
        /// 距最近一次"频谱原始电平高出噪声地板 <see cref="SignalMarginDb"/> 分贝（即系统真的在出声）"的秒数。
        /// 暂停/停止后这个值持续增长（这正是"频谱是一条线"的同一份物理事实），
        /// 看门狗重建采集会话不会把它清零，因此可以长期信任。
        /// 由 UI 侧每 250ms 读一次回灌给 <c>SmtcService</c>，用于交叉校验播放器上报的播放状态。
        /// </summary>
        public double SilentSeconds => TicksToMs(Interlocked.Read(ref _lastSignalTicks)) / 1000.0;

        /// <summary>
        /// 距最近一次任意 DataAvailable 回调（含 0 字节）的毫秒数。
        /// 设备空闲时 WASAPI 仍持续回调，所以它小 = 会话活着（只是没声音）；
        /// 它大 = 采集会话可能已"变聋"（蓝牙/独占设备在暂停后会整个停掉回调），
        /// 此时"回调静默"本身就是没在出声的证据，由 UI 侧折算成等价的静音时长。
        /// </summary>
        public double SinceCallbackMs => TicksToMs(Interlocked.Read(ref _lastCallbackTicks));

        private static double TicksToMs(long ticks)
        {
            if (ticks <= 0) return double.MaxValue;
            return (Stopwatch.GetTimestamp() - ticks) * 1000.0 / Stopwatch.Frequency;
        }

        /// <summary>诊断用：因长时间收不到音频数据而自动重建采集会话的次数。</summary>
        public int SessionRebinds => Volatile.Read(ref _sessionRebinds);

        public AudioSpectrumService()
        {
            // 汉宁窗，抑制频谱泄漏
            for (int i = 0; i < FftSize; i++)
                _window[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1))));

            BuildBandMap(_sampleRate);

            _lastSignalTicks = Stopwatch.GetTimestamp();
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
                // 补偿必须作用在分贝域。level 本身就是按分贝归一化出来的量，
                // 早期版本直接 "level *= _tilt[b] * sens"，等于把一个幅度比当增益去乘分贝等级，
                // 中高音量下多数频段会被顶到 1.0 饱和（实测宽带噪声：64 段里 36 段恒为满格），
                // 频谱只剩一座不会动的小山。换成等效的 dB 偏移即可保留动态。
                _tiltDb[b] = (float)(20.0 * Math.Log10(_tilt[b]));
            }
        }

        // ---------------- 启停 ----------------

        public void Start()
        {
            if (_disposed) return;
            // 只在外部显式启停时重置"静音计时"的观察窗口；
            // 看门狗内部的会话重建（StartCoreLocked）绝不能重置它，否则静音时长永远涨不过阈值。
            Interlocked.Exchange(ref _lastSignalTicks, Stopwatch.GetTimestamp());
            lock (_lifecycle) StartCoreLocked();
        }

        public void Stop()
        {
            Interlocked.Exchange(ref _lastSignalTicks, Stopwatch.GetTimestamp());
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

                // 优先用 Multimedia 端点（默认播放设备）；若失败，退回 Console 端点
                try
                {
                    device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }
                catch
                {
                    device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                }

                capture = new WasapiLoopbackCapture(device);
                var fmt = capture.WaveFormat;

                lock (_gate)
                {
                    _channels = Math.Max(1, fmt.Channels);
                    _bytesPerSample = Math.Max(2, fmt.BitsPerSample / 8);
                    _sampleRate = fmt.SampleRate > 0 ? fmt.SampleRate : 48000;
                    _isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat
                        || (fmt is WaveFormatExtensible we && we.SubFormat == SubtypeIeeeFloat);
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
                // 端点电平表：用来区分“端点本来就空闲”和“系统在出声但我们的会话坏了”。
                // 取不到也照常工作（看门狗会退回到兜底重建）。
                try { _meter = device.AudioMeterInformation; } catch { _meter = null; }
                Interlocked.Exchange(ref _lastDataTicks, Stopwatch.GetTimestamp());
                Interlocked.Exchange(ref _lastCallbackTicks, Stopwatch.GetTimestamp());
                // 注意：这里**不能**把退避重置回 SilentRebindBaseMs。
                // 重置会让"一直采不到数据"的坏会话永远停在 3s 一次的重建节奏上（实测 25 秒重建 8 次）。
                // 退避只在真的拿到音频数据时（OnDataAvailable 里）才归零。
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

        /// <summary>必须在 _lifecycle 内调用；不得持有 _gate。
        ///
        /// 重要：设备被切换/拔除后，WasapiLoopbackCapture.Dispose() 与 MMDevice.Dispose()
        /// 会永久阻塞在 COM 释放上（实测：StopRecording 立即返回，Dispose 再也不返回）。
        /// 旧实现把这些释放直接放在 _lifecycle 锁内，结果看门狗永远拿不到锁、采集会话再也无法
        /// 重建，频谱从此是一条直线，只能重启程序（用户实测现象）。
        /// 因此这里只做"摘除字段 + 退订事件"，所有 COM 释放丢到独立后台线程，锁内绝不阻塞。</summary>
        private void StopCoreLocked()
        {
            var capture = _capture;
            var device = _device;
            _capture = null;
            _device = null;
            _meter = null;
            _deviceId = null;
            _deviceName = null;
            _running = false;

            if (capture == null && device == null) return;

            // 先退订：旧采集线程即使还活着，也不会再回调到我们的逻辑（它可能还持有旧端点）
            if (capture != null)
            {
                try { capture.DataAvailable -= OnDataAvailable; } catch { }
                try { capture.RecordingStopped -= OnRecordingStopped; } catch { }
            }

            // 独立线程（不是线程池：这里可能会永久卡住，不能耗掉线程池线程）
            var cleanup = new Thread(() =>
            {
                try { capture?.StopRecording(); } catch { }
                try { capture?.Dispose(); } catch { }
                try { device?.Dispose(); } catch { }
            })
            { IsBackground = true, Name = "AudioSessionCleanup" };
            cleanup.Start();

        }

        // ---------------- 采集 ----------------

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_disposed) return;

            // 回调本身就是"会话还活着"的证据：设备空闲时 WASAPI 也会持续回调（只是 0 字节），
            // 所以这个时间戳要在字节数判断之前刷新，看门狗才能区分"设备空闲"和"会话已死"。
            Interlocked.Exchange(ref _lastCallbackTicks, Stopwatch.GetTimestamp());
            if (e.BytesRecorded <= 0) return;

            Interlocked.Exchange(ref _lastDataTicks, Stopwatch.GetTimestamp());
            Volatile.Write(ref _rebindBackoffMs, SilentRebindBaseMs);   // 真的拿到数据了，退避重置

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
            _running = false;
            _lastStartAttemptTicks = Stopwatch.GetTimestamp();
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
                    float sample = 0f;
                    if (bps == 4)
                    {
                        // 32-bit WASAPI Loopback 在 Windows 上始终为 IEEE Float
                        sample = BitConverter.ToSingle(buffer, o);
                    }
                    else if (bps == 2)
                    {
                        sample = BitConverter.ToInt16(buffer, o) / 32768f;
                    }
                    else if (bps == 3)
                    {
                        sample = (float)(((sbyte)buffer[o + 2] << 16) | (buffer[o + 1] << 8) | buffer[o]) / 8388608f;
                    }
                    if (float.IsNaN(sample) || float.IsInfinity(sample)) sample = 0f;
                    sum += sample;
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
            double signalDbPeak = double.NegativeInfinity;   // 本帧各频段的原始分贝峰值（未做显示倾斜、未乘灵敏度）

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
                    if (double.IsNaN(m) || double.IsInfinity(m)) m = 0;
                    if (m > mag) mag = m;
                }
                mag *= MagScale;

                double rawDb = 20.0 * Math.Log10(mag + 1e-7);
                if (rawDb > signalDbPeak) signalDbPeak = rawDb;

                double db = rawDb + _tiltDb[b];
                if (double.IsNaN(db) || double.IsInfinity(db)) db = floor;
                double level = (db - floor) / range;
                level *= sens;
                if (level < 0 || double.IsNaN(level)) level = 0; else if (level > 1) level = 1;

                float prev = _smoothed[b];
                if (float.IsNaN(prev)) prev = 0f;
                // 快起慢落，观感更"跟手"又不抖
                float coeff = level > prev ? 0.68f : 0.17f;
                _smoothed[b] = prev + ((float)level - prev) * coeff;
                if (float.IsNaN(_smoothed[b])) _smoothed[b] = 0f;
            }

            // 只要有一根频段高出噪声地板 SignalMarginDb 分贝，就认为系统此刻真的在出声。
            // 判定必须用"未经显示倾斜、未乘灵敏度"的原始分贝：倾斜在高频最多加 7.2dB，
            // 灵敏度又是用户可调的乘数，两者都会让"这是音乐还是底噪"的判断失真。
            LastSignalDbPeak = signalDbPeak;
            if (signalDbPeak > floor + SignalMarginDb)
                Interlocked.Exchange(ref _lastSignalTicks, Stopwatch.GetTimestamp());
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
                double callbackIdleMs = (now - Interlocked.Read(ref _lastCallbackTicks)) * 1000.0 / Stopwatch.Frequency;

                // 电平表必须在 _lifecycle 之外读：个别（蓝牙）设备的 COM 调用可能阻塞上百毫秒，
                // 而 _lifecycle 也是 Stop()/Dispose() 要抢的锁，不能在锁里等 COM。
                bool endpointHasSignal = idleMs > SilentProbeMs && EndpointHasSignal();

                bool needRestart = false;
                bool silentRebind = false;
                lock (_lifecycle)
                {
                    if (!_running)
                    {
                        double sinceStart = (now - _lastStartAttemptTicks) * 1000.0 / Stopwatch.Frequency;
                        if (sinceStart > 1500)
                        {
                            _lastStartAttemptTicks = now;
                            StartCoreLocked();
                        }
                        return;
                    }

                    // 默认音频终结点切换（如连接/断开蓝牙耳机）时立即重启
                    if (DefaultDeviceChangedLocked())
                    {
                        needRestart = true;
                    }
                    else if (idleMs > SilentProbeMs)
                    {
                        // 长时间收不到任何音频数据。两种可能：
                        //   a) 端点本来就是空闲的（蓝牙端点空闲时只有 0 字节回调）——正常，重建也无害；
                        //   b) 我们的采集会话“聋”了：实测在本机蓝牙 A2DP 端点上出现过
                        //      「同端点上别的进程刚采到 2.9MB 音频，我们自己的会话一个字节都没有」。
                        // 已证实 (b) 唯一可靠的恢复手段就是重建会话；电平表在部分（蓝牙）驱动上恒为 0，
                        // 不能当开门条件（实测放音时 MasterPeakValue 仍 ≈0），只能当加速器。
                        // 因此：静默超过退避周期就重建，退避 3s→10s、只在真正收到数据时归零。
                        int backoff = Volatile.Read(ref _rebindBackoffMs);
                        if (endpointHasSignal || idleMs > backoff)
                        {
                            needRestart = true;
                            silentRebind = true;
                            Volatile.Write(ref _rebindBackoffMs,
                                Math.Min(SilentRebindMaxMs, backoff + backoff / 2));
                        }
                    }
                }

                if (needRestart)
                {
                    if (silentRebind)
                    {
                        int n = Interlocked.Increment(ref _sessionRebinds);
                        Debug.WriteLine($"[Audio] 静默 {idleMs:F0}ms（端点{(endpointHasSignal ? "在出声" : "无声")}、回调空闲 {callbackIdleMs:F0}ms），重建采集会话（第 {n} 次）：{_deviceName}");
                    }
                    else
                    {
                        Debug.WriteLine("[Audio] 播放设备改变，立即重启采集");
                    }
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

        /// <summary>
        /// 端点此刻是否正在出声（0..1 的电平峰值）。用来区分“端点本来就空闲”和
        /// “系统在出声、而我们一个字节都采不到”。会访问 COM，必须放在 _lifecycle 之外调用。
        /// </summary>
        private bool EndpointHasSignal()
        {
            try
            {
                var meter = _meter;
                if (meter == null) return false;
                return meter.MasterPeakValue > 0.0005f;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>必须在 _lifecycle 内调用。</summary>
        private bool DefaultDeviceChangedLocked()
        {
            if (_deviceId == null) return false;
            try
            {
                _enumerator ??= new MMDeviceEnumerator();
                // 同时检查 Multimedia 和 Console 两个端点
                // 只要当前采集的设备 ID 与其中任意一个匹配，就认为没有切换
                using var multi = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (string.Equals(multi.ID, _deviceId, StringComparison.OrdinalIgnoreCase))
                    return false; // 设备没变
                try
                {
                    using var cons = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                    if (string.Equals(cons.ID, _deviceId, StringComparison.OrdinalIgnoreCase))
                        return false; // Console 端点也没变
                }
                catch { }
                return true; // 两个都不匹配，设备确实变了
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
