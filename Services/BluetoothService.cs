using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace alpha_island.Services
{
    /// <summary>蓝牙设备分类。</summary>
    public enum BluetoothDeviceCategory
    {
        /// <summary>音频设备（无线耳机、头戴式、音箱等）。</summary>
        Audio,
        /// <summary>键盘设备。</summary>
        Keyboard,
        /// <summary>鼠标与指针设备。</summary>
        Mouse,
        /// <summary>游戏手柄与控制器。</summary>
        Gamepad,
        /// <summary>其它输入设备。</summary>
        Input,
        /// <summary>其它蓝牙设备。</summary>
        Other
    }

    /// <summary>蓝牙设备信息快照。</summary>
    /// <param name="Id">设备实例 ID。</param>
    /// <param name="Name">设备显示名称。</param>
    /// <param name="Category">设备分类。</param>
    /// <param name="BatteryPercent">电池电量百分比，经典蓝牙或未就绪时为 null。</param>
    /// <param name="IsConnected">当前是否已连接。</param>
    public record BluetoothDeviceInfo(
        string Id,
        string Name,
        BluetoothDeviceCategory Category,
        int? BatteryPercent,
        bool IsConnected);

    /// <summary>
    /// 蓝牙设备连接/断开监控服务。
    /// 采用双轨可靠检测机制：
    ///  1. WinRT 蓝牙层：通过 <see cref="BluetoothDevice"/> 与 <see cref="BluetoothLEDevice"/> 的
    ///     <c>ConnectionStatusChanged</c> 事件实时感知蓝牙链路通断；
    ///  2. Windows CoreAudio 终结点层：通过 <see cref="IMMNotificationClient"/> 监听音频输出设备
    ///     的上线与激活状态，确保无线耳机（如 AirPods、vivo TWS、OPPO Enco 等）插拔/开盖即连 100% 触发。
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public sealed class BluetoothService : IDisposable
    {
        private static readonly Guid BatteryServiceUuid =
            new("0000180f-0000-1000-8000-00805f9b34fb");

        private static readonly Guid BatteryLevelCharacteristicUuid =
            new("00002a19-0000-1000-8000-00805f9b34fb");

        private const string PnpBatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

        private readonly Dispatcher _dispatcher;
        private DeviceWatcher? _classicWatcher;
        private DeviceWatcher? _bleWatcher;
        private MMDeviceEnumerator? _audioEnumerator;
        private AudioNotificationClient? _audioNotificationClient;
        private bool _disposed;
        private volatile bool _enumerated;

        private readonly DateTime _startupTime = DateTime.UtcNow;
        /// <summary>开机/启动静默保护期（启动后前 6 秒），绝不弹已有连接设备。</summary>
        private bool IsInStartupGracePeriod => (DateTime.UtcNow - _startupTime).TotalSeconds < 6.0;

        private readonly ConcurrentDictionary<string, BluetoothDevice> _classicDevices = new();
        private readonly ConcurrentDictionary<string, BluetoothLEDevice> _bleDevices = new();
        private readonly ConcurrentDictionary<string, string> _knownDeviceNames = new();
        private readonly ConcurrentDictionary<string, long> _lastNotifiedTicks = new();
        private readonly ConcurrentDictionary<string, bool> _preExistingConnected = new();

        /// <summary>设备连接时触发，参数为设备信息快照。</summary>
        public event EventHandler<BluetoothDeviceInfo>? DeviceConnected;

        /// <summary>设备断开时触发，参数为设备信息快照。</summary>
        public event EventHandler<BluetoothDeviceInfo>? DeviceDisconnected;

        /// <summary>电量后续异步读取成功时触发，用于实时回填电量。</summary>
        public event EventHandler<BluetoothDeviceInfo>? BatteryUpdated;

        /// <param name="dispatcher">用于将事件派发到 UI 线程的 <see cref="Dispatcher"/>。</param>
        public BluetoothService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        // ===================== 生命周期 =====================

        /// <summary>启动蓝牙与音频设备监听。</summary>
        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BluetoothService));
            if (_classicWatcher != null || _bleWatcher != null) return;

            try
            {
                // 1. CoreAudio 音频终结点通知监听
                _audioEnumerator = new MMDeviceEnumerator();
                _audioNotificationClient = new AudioNotificationClient(this);
                _audioEnumerator.RegisterEndpointNotificationCallback(_audioNotificationClient);
                Debug.WriteLine("[Bluetooth] CoreAudio 终结点监听器已注册");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Bluetooth] CoreAudio 初始化异常: " + ex.Message);
            }

            try
            {
                // 2. WinRT 经典蓝牙配对设备监听
                string classicSelector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
                _classicWatcher = DeviceInformation.CreateWatcher(classicSelector);
                _classicWatcher.Added += OnClassicDeviceAdded;
                _classicWatcher.Updated += OnClassicDeviceUpdated;
                _classicWatcher.EnumerationCompleted += (_, _) => CheckAllEnumerated();
                _classicWatcher.Start();

                // 3. WinRT BLE 配对设备监听
                string bleSelector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
                _bleWatcher = DeviceInformation.CreateWatcher(bleSelector);
                _bleWatcher.Added += OnBleDeviceAdded;
                _bleWatcher.Updated += OnBleDeviceUpdated;
                _bleWatcher.EnumerationCompleted += (_, _) => CheckAllEnumerated();
                _bleWatcher.Start();

                Debug.WriteLine("[Bluetooth] WinRT 蓝牙 DeviceWatchers 已启动");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Bluetooth] DeviceWatcher 启动失败: " + ex.Message);
            }

            // 兜底保护：某些蓝牙驱动或无 BLE 场景下可能延迟或不触发 EnumerationCompleted，3秒后强制进入就绪监听模式
            Task.Delay(3000).ContinueWith(_ =>
            {
                if (!_enumerated && !_disposed)
                {
                    _enumerated = true;
                    Debug.WriteLine("[Bluetooth] 初始枚举超时兜底触发，进入就绪模式");
                }
            });
        }

        private int _enumeratedCount;
        private void CheckAllEnumerated()
        {
            if (System.Threading.Interlocked.Increment(ref _enumeratedCount) >= 2)
            {
                _enumerated = true;
                Debug.WriteLine("[Bluetooth] 初始枚举全部完成，进入监听模式");
            }
        }

        /// <summary>停止监听并释放资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_audioEnumerator != null && _audioNotificationClient != null)
                {
                    _audioEnumerator.UnregisterEndpointNotificationCallback(_audioNotificationClient);
                }
                _audioEnumerator?.Dispose();
                _audioEnumerator = null;
                _audioNotificationClient = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Bluetooth] CoreAudio 注销异常: " + ex.Message);
            }

            try
            {
                if (_classicWatcher != null)
                {
                    if (_classicWatcher.Status == DeviceWatcherStatus.Started ||
                        _classicWatcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                    {
                        _classicWatcher.Stop();
                    }
                    _classicWatcher = null;
                }
                if (_bleWatcher != null)
                {
                    if (_bleWatcher.Status == DeviceWatcherStatus.Started ||
                        _bleWatcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                    {
                        _bleWatcher.Stop();
                    }
                    _bleWatcher = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Bluetooth] Watcher 停止异常: " + ex.Message);
            }

            foreach (var d in _classicDevices.Values)
            {
                try { d.Dispose(); } catch { }
            }
            _classicDevices.Clear();

            foreach (var d in _bleDevices.Values)
            {
                try { d.Dispose(); } catch { }
            }
            _bleDevices.Clear();
            _knownDeviceNames.Clear();
            _preExistingConnected.Clear();
        }

        // ===================== WinRT 设备挂钩 =====================

        private async void OnClassicDeviceAdded(DeviceWatcher sender, DeviceInformation info)
        {
            try
            {
                var device = await BluetoothDevice.FromIdAsync(info.Id);
                if (device == null) return;

                _classicDevices[info.Id] = device;
                string cleanName = CleanDeviceName(device.Name);
                _knownDeviceNames[info.Id] = cleanName;
                var category = ResolveClassicCategory(device);

                // 启动期间若是已连接设备，记录状态并绝对静默
                if (device.ConnectionStatus == BluetoothConnectionStatus.Connected)
                {
                    _preExistingConnected[device.DeviceId] = true;
                }

                device.ConnectionStatusChanged += (dev, _) =>
                {
                    bool isConnected = dev.ConnectionStatus == BluetoothConnectionStatus.Connected;
                    bool wasConnected = _preExistingConnected.TryGetValue(dev.DeviceId, out bool prev) && prev;

                    Debug.WriteLine($"[Bluetooth] Classic 设备 {dev.Name} 状态跳变: {dev.ConnectionStatus} (wasConnected={wasConnected})");

                    if (isConnected)
                    {
                        _preExistingConnected[dev.DeviceId] = true;
                        if (!wasConnected && !IsInStartupGracePeriod && _enumerated)
                        {
                            _ = HandleConnectedAsync(dev.DeviceId, cleanName, category);
                        }
                    }
                    else
                    {
                        _preExistingConnected[dev.DeviceId] = false;
                        if (wasConnected && !IsInStartupGracePeriod && _enumerated)
                        {
                            HandleDisconnected(dev.DeviceId, cleanName, category);
                        }
                    }
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Bluetooth] Classic 设备初始化异常 ({info.Name}): {ex.Message}");
            }
        }

        private async void OnBleDeviceAdded(DeviceWatcher sender, DeviceInformation info)
        {
            try
            {
                var device = await BluetoothLEDevice.FromIdAsync(info.Id);
                if (device == null) return;

                _bleDevices[info.Id] = device;
                string cleanName = CleanDeviceName(device.Name);
                _knownDeviceNames[info.Id] = cleanName;
                var category = ResolveBleCategory(device);

                // 启动期间若是已连接设备，记录状态并绝对静默（杜绝键盘/鼠标开机弹出）
                if (device.ConnectionStatus == BluetoothConnectionStatus.Connected)
                {
                    _preExistingConnected[device.DeviceId] = true;
                }

                device.ConnectionStatusChanged += (dev, _) =>
                {
                    bool isConnected = dev.ConnectionStatus == BluetoothConnectionStatus.Connected;
                    bool wasConnected = _preExistingConnected.TryGetValue(dev.DeviceId, out bool prev) && prev;

                    Debug.WriteLine($"[Bluetooth] BLE 设备 {dev.Name} 状态跳变: {dev.ConnectionStatus} (wasConnected={wasConnected})");

                    if (isConnected)
                    {
                        _preExistingConnected[dev.DeviceId] = true;
                        if (!wasConnected && !IsInStartupGracePeriod && _enumerated)
                        {
                            _ = HandleConnectedAsync(dev.DeviceId, cleanName, category);
                        }
                    }
                    else
                    {
                        _preExistingConnected[dev.DeviceId] = false;
                        if (wasConnected && !IsInStartupGracePeriod && _enumerated)
                        {
                            HandleDisconnected(dev.DeviceId, cleanName, category);
                        }
                    }
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Bluetooth] BLE 设备初始化异常 ({info.Name}): {ex.Message}");
            }
        }

        private void OnClassicDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            if (_disposed || !_enumerated || IsInStartupGracePeriod) return;

            if (_classicDevices.TryGetValue(update.Id, out var device))
            {
                bool isConnected = device.ConnectionStatus == BluetoothConnectionStatus.Connected;
                bool wasConnected = _preExistingConnected.TryGetValue(device.DeviceId, out bool prev) && prev;

                if (isConnected && !wasConnected)
                {
                    _preExistingConnected[device.DeviceId] = true;
                    string name = _knownDeviceNames.GetValueOrDefault(update.Id, device.Name);
                    var category = ResolveClassicCategory(device);
                    _ = HandleConnectedAsync(device.DeviceId, name, category);
                }
                else if (!isConnected && wasConnected)
                {
                    _preExistingConnected[device.DeviceId] = false;
                    string name = _knownDeviceNames.GetValueOrDefault(update.Id, device.Name);
                    var category = ResolveClassicCategory(device);
                    HandleDisconnected(device.DeviceId, name, category);
                }
            }
        }

        private void OnBleDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            if (_disposed || !_enumerated || IsInStartupGracePeriod) return;

            if (_bleDevices.TryGetValue(update.Id, out var device))
            {
                bool isConnected = device.ConnectionStatus == BluetoothConnectionStatus.Connected;
                bool wasConnected = _preExistingConnected.TryGetValue(device.DeviceId, out bool prev) && prev;

                if (isConnected && !wasConnected)
                {
                    _preExistingConnected[device.DeviceId] = true;
                    string name = _knownDeviceNames.GetValueOrDefault(update.Id, device.Name);
                    var category = ResolveBleCategory(device);
                    _ = HandleConnectedAsync(device.DeviceId, name, category);
                }
                else if (!isConnected && wasConnected)
                {
                    _preExistingConnected[device.DeviceId] = false;
                    string name = _knownDeviceNames.GetValueOrDefault(update.Id, device.Name);
                    var category = ResolveBleCategory(device);
                    HandleDisconnected(device.DeviceId, name, category);
                }
            }
        }

        // ===================== CoreAudio 音频设备检测 =====================

        internal void CheckAudioEndpoint(string deviceId)
        {
            if (_disposed || !_enumerated || IsInStartupGracePeriod) return;

            try
            {
                var enumerator = _audioEnumerator;
                if (enumerator == null) return;

                MMDevice? dev = null;
                try { dev = enumerator.GetDevice(deviceId); } catch { }

                // 若初次获取状态未处于 Active，允许延迟 350ms 重试（应对耳机插拔握手瞬态）
                if (dev == null || dev.State != DeviceState.Active)
                {
                    Task.Delay(350).ContinueWith(_ =>
                    {
                        if (!_disposed && _enumerated && !IsInStartupGracePeriod)
                        {
                            try
                            {
                                var d = _audioEnumerator?.GetDevice(deviceId);
                                if (d != null && d.State == DeviceState.Active)
                                {
                                    ProcessAudioEndpoint(d, deviceId);
                                }
                            }
                            catch { }
                        }
                    });
                    return;
                }

                ProcessAudioEndpoint(dev, deviceId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Bluetooth] CheckAudioEndpoint 异常: " + ex.Message);
            }
        }

        private void ProcessAudioEndpoint(MMDevice dev, string deviceId)
        {
            string rawName = dev.FriendlyName ?? "";
            if (string.IsNullOrWhiteSpace(rawName)) return;

            bool isBluetooth = false;
            string extractedMac = "";
            string containerId = "";

            try
            {
                for (int i = 0; i < dev.Properties.Count; i++)
                {
                    var prop = dev.Properties[i];
                    string val = prop.Value?.ToString() ?? "";
                    if (val.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
                        val.Contains("BTHLE", StringComparison.OrdinalIgnoreCase) ||
                        val.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
                    {
                        isBluetooth = true;
                    }

                    if (string.IsNullOrEmpty(extractedMac))
                    {
                        string m = ExtractMacAddress(val);
                        if (!string.IsNullOrEmpty(m)) extractedMac = m;
                    }

                    // 检查是否为 ContainerId
                    if (prop.Key.formatId == new Guid("8c7fc266-4824-43a9-8a2b-9b2194e9f775"))
                    {
                        containerId = val;
                    }
                }
            }
            catch { }

            if (!isBluetooth)
            {
                foreach (var pair in _knownDeviceNames)
                {
                    if (rawName.Contains(pair.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        isBluetooth = true;
                        string m = ExtractMacAddress(pair.Key);
                        if (!string.IsNullOrEmpty(m) && string.IsNullOrEmpty(extractedMac)) extractedMac = m;
                        break;
                    }
                }
            }

            if (isBluetooth)
            {
                string cleanName = CleanDeviceName(rawName);
                Debug.WriteLine($"[Bluetooth] CoreAudio 检测到蓝牙音频设备激活: {cleanName}, mac={extractedMac}, container={containerId}");
                _ = HandleConnectedAsync(deviceId, cleanName, BluetoothDeviceCategory.Audio, extractedMac, containerId);
            }
        }

        // ===================== 事件触发与去重 =====================

        private async Task HandleConnectedAsync(string deviceId, string name, BluetoothDeviceCategory category,
            string? mac = null, string? containerId = null)
        {
            if (_disposed || IsInStartupGracePeriod) return;

            long now = Stopwatch.GetTimestamp();
            long last = _lastNotifiedTicks.GetValueOrDefault(name, 0);
            double elapsedSec = (now - last) / (double)Stopwatch.Frequency;

            // 4 秒内同一设备去重防抖，避免 WinRT 与 CoreAudio 重复弹窗
            if (elapsedSec < 4.0) return;
            _lastNotifiedTicks[name] = now;

            if (string.IsNullOrEmpty(mac)) mac = ExtractMacAddress(deviceId);

            Debug.WriteLine($"[Bluetooth] 触发连接通知: {name} ({deviceId}, mac={mac}, container={containerId})");
            int? battery = await TryReadBatteryAsync(deviceId, name, mac, containerId);
            var info = new BluetoothDeviceInfo(deviceId, name, category, battery, IsConnected: true);

            _ = _dispatcher.BeginInvoke(() => DeviceConnected?.Invoke(this, info));

            // 若初次尝试未读到电量，开启后台重试轮询（解决耳机连接初秒 HFP/GATT 未建立导致显示--的问题）
            if (!battery.HasValue)
            {
                _ = PollBatteryRetryAsync(deviceId, name, category, mac, containerId);
            }
        }

        private void HandleDisconnected(string deviceId, string name, BluetoothDeviceCategory category)
        {
            if (_disposed || IsInStartupGracePeriod) return;

            Debug.WriteLine($"[Bluetooth] 触发断开通知: {name} ({deviceId})");
            var info = new BluetoothDeviceInfo(deviceId, name, category, BatteryPercent: null, IsConnected: false);
            _ = _dispatcher.BeginInvoke(() => DeviceDisconnected?.Invoke(this, info));
        }

        // ===================== 电量读取与多轮重试 =====================

        private async Task PollBatteryRetryAsync(string deviceId, string name, BluetoothDeviceCategory category,
            string? mac, string? containerId)
        {
            // 蓝牙耳机在 A2DP 音频链路建立后，HFP / GATT 握手通常有 0.5s~3s 的延迟
            int[] delays = { 500, 1000, 1800, 3000, 5000 };
            foreach (int delay in delays)
            {
                await Task.Delay(delay);
                if (_disposed) return;

                int? b = await TryReadBatteryAsync(deviceId, name, mac, containerId);
                if (b.HasValue)
                {
                    Debug.WriteLine($"[Bluetooth] 后台轮询成功读取电量: {name} => {b.Value}%");
                    var updated = new BluetoothDeviceInfo(deviceId, name, category, b.Value, IsConnected: true);
                    _ = _dispatcher.BeginInvoke(() => BatteryUpdated?.Invoke(this, updated));
                    return;
                }
            }
        }

        private async Task<int?> TryReadBatteryAsync(string deviceId, string name, string? mac = null, string? containerId = null)
        {
            try
            {
                string targetMac = string.IsNullOrEmpty(mac) ? ExtractMacAddress(deviceId) : mac;

                // 1. Windows PnP 原生电量属性尝试（支持 ContainerId、MAC 和名称匹配，速度极快 ~40-100ms）
                int? pnpBattery = await ReadBatteryFromPnpAsync(targetMac, name, containerId);
                if (pnpBattery.HasValue) return pnpBattery;

                // 2. 若本身是 BLE ID
                if (deviceId.StartsWith("BluetoothLE#", StringComparison.OrdinalIgnoreCase))
                {
                    int? b = await ReadBatteryFromBleIdAsync(deviceId);
                    if (b.HasValue) return b;
                }

                // 3. 根据 MAC 地址精准定位对应的 BLE 实体（解决双模耳机经典蓝牙与 BLE MAC 相同的特征）
                if (!string.IsNullOrEmpty(targetMac))
                {
                    foreach (var pair in _bleDevices)
                    {
                        string bleMac = ExtractMacAddress(pair.Key);
                        if (bleMac.Equals(targetMac, StringComparison.OrdinalIgnoreCase))
                        {
                            int? b = await ReadBatteryFromBleIdAsync(pair.Key);
                            if (b.HasValue) return b;
                        }
                    }
                }

                // 4. 按名称模糊匹配 BLE 实体
                foreach (var pair in _bleDevices)
                {
                    if (pair.Value.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                        name.Contains(pair.Value.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        int? b = await ReadBatteryFromBleIdAsync(pair.Key);
                        if (b.HasValue) return b;
                    }
                }
            }
            catch { }
            return null;
        }

        private static async Task<int?> ReadBatteryFromBleIdAsync(string bleId)
        {
            try
            {
                var ble = await BluetoothLEDevice.FromIdAsync(bleId);
                if (ble == null) return null;

                var servicesResult = await ble.GetGattServicesForUuidAsync(BatteryServiceUuid);
                if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
                    return null;

                var service = servicesResult.Services[0];
                var charResult = await service.GetCharacteristicsForUuidAsync(BatteryLevelCharacteristicUuid);
                if (charResult.Status != GattCommunicationStatus.Success || charResult.Characteristics.Count == 0)
                    return null;

                var characteristic = charResult.Characteristics[0];
                var readResult = await characteristic.ReadValueAsync();
                if (readResult.Status != GattCommunicationStatus.Success) return null;

                using var reader = Windows.Storage.Streams.DataReader.FromBuffer(readResult.Value);
                if (reader.UnconsumedBufferLength >= 1)
                {
                    return (int)reader.ReadByte();
                }
            }
            catch { }
            return null;
        }

        private static async Task<int?> ReadBatteryFromPnpAsync(string? mac, string name, string? containerId = null)
        {
            try
            {
                string[] requestedProps = {
                    PnpBatteryKey,
                    "System.Devices.ContainerId",
                    "{2BD67D8B-8BEB-48D5-87E0-6CDA3428040A} 1"
                };
                // 同时查询 Bluetooth 类设备以及承载 Hands-Free AG 电量的 System 类设备，速度极快（~40-100ms）
                string aqs = "System.Devices.ClassGuid:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" OR System.Devices.ClassGuid:=\"{4d36e97d-e325-11ce-bfc1-08002be10318}\"";
                var devices = await DeviceInformation.FindAllAsync(aqs, requestedProps, DeviceInformationKind.Device);

                // 1. 优先按 ContainerId 匹配（最精确，音频终结点与 Hands-Free AG 共享同一容器）
                if (!string.IsNullOrEmpty(containerId))
                {
                    foreach (var dev in devices)
                    {
                        if (dev.Properties.TryGetValue("System.Devices.ContainerId", out object? cid) && cid != null)
                        {
                            if (string.Equals(cid.ToString(), containerId, StringComparison.OrdinalIgnoreCase) ||
                                cid.ToString()!.Contains(containerId, StringComparison.OrdinalIgnoreCase))
                            {
                                if (dev.Properties.TryGetValue(PnpBatteryKey, out object? val) && val != null)
                                {
                                    if (val is byte b) return b;
                                    if (val is int i) return i;
                                    if (int.TryParse(val.ToString(), out int parsed)) return parsed;
                                }
                            }
                        }
                    }
                }

                // 2. 按 MAC 地址匹配（dev.Id 或 DEVPKEY_Bluetooth_DeviceAddress 属性）
                if (!string.IsNullOrEmpty(mac) && mac.Length >= 6)
                {
                    foreach (var dev in devices)
                    {
                        bool macMatch = dev.Id.Contains(mac, StringComparison.OrdinalIgnoreCase);
                        if (!macMatch && dev.Properties.TryGetValue("{2BD67D8B-8BEB-48D5-87E0-6CDA3428040A} 1", out object? addr) && addr != null)
                        {
                            macMatch = addr.ToString()!.Replace(":", "").Replace("-", "").Equals(mac, StringComparison.OrdinalIgnoreCase);
                        }

                        if (macMatch && dev.Properties.TryGetValue(PnpBatteryKey, out object? val) && val != null)
                        {
                            if (val is byte b) return b;
                            if (val is int i) return i;
                            if (int.TryParse(val.ToString(), out int parsed)) return parsed;
                        }
                    }
                }

                // 3. 其次按设备名称模糊匹配（如 "OPPO Enco Clip2" 匹配 "OPPO Enco Clip2 Hands-Free AG"）
                if (!string.IsNullOrEmpty(name))
                {
                    foreach (var dev in devices)
                    {
                        bool match = dev.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                                     name.Contains(dev.Name, StringComparison.OrdinalIgnoreCase);
                        if (match && dev.Properties.TryGetValue(PnpBatteryKey, out object? val) && val != null)
                        {
                            if (val is byte b) return b;
                            if (val is int i) return i;
                            if (int.TryParse(val.ToString(), out int parsed)) return parsed;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Bluetooth] ReadBatteryFromPnpAsync 异常: " + ex.Message);
            }
            return null;
        }

        // ===================== 设备类别解析 =====================

        public static BluetoothDeviceCategory ResolveClassicCategory(BluetoothDevice device)
        {
            try
            {
                var cod = device.ClassOfDevice;
                if (cod != null)
                {
                    var major = cod.MajorClass;
                    var minor = cod.MinorClass;

                    if (major == BluetoothMajorClass.AudioVideo)
                        return BluetoothDeviceCategory.Audio;

                    if (major == BluetoothMajorClass.Peripheral)
                    {
                        uint raw = cod.RawValue;
                        // CoD minor field: bits 6 & 7 indicate keyboard and pointing device
                        bool isKeyboard = (raw & 0x40) != 0;
                        bool isPointer = (raw & 0x80) != 0;

                        if (isKeyboard)
                            return BluetoothDeviceCategory.Keyboard;

                        if (isPointer)
                            return BluetoothDeviceCategory.Mouse;

                        if (minor == BluetoothMinorClass.PeripheralJoystick ||
                            minor == BluetoothMinorClass.PeripheralGamepad)
                            return BluetoothDeviceCategory.Gamepad;

                        return BluetoothDeviceCategory.Input;
                    }
                }
            }
            catch { }

            return ResolveCategoryFromName(device.Name);
        }

        public static BluetoothDeviceCategory ResolveBleCategory(BluetoothLEDevice device)
        {
            try
            {
                var app = device.Appearance;
                if (app != null)
                {
                    ushort cat = app.Category;
                    ushort sub = app.SubCategory;

                    // 0x03C0 = HumanInterfaceDevice (960)
                    if (cat == BluetoothLEAppearanceCategories.HumanInterfaceDevice)
                    {
                        if (sub == BluetoothLEAppearanceSubcategories.Keyboard)
                            return BluetoothDeviceCategory.Keyboard;
                        if (sub == BluetoothLEAppearanceSubcategories.Mouse)
                            return BluetoothDeviceCategory.Mouse;
                        if (sub == BluetoothLEAppearanceSubcategories.Joystick ||
                            sub == BluetoothLEAppearanceSubcategories.Gamepad)
                            return BluetoothDeviceCategory.Gamepad;

                        return BluetoothDeviceCategory.Input;
                    }

                    // 音频外观分类
                    if (cat == 13 || cat == 14 || cat == 15 || cat == 16)
                        return BluetoothDeviceCategory.Audio;
                }
            }
            catch { }

            return ResolveCategoryFromName(device.Name);
        }

        public static BluetoothDeviceCategory ResolveCategoryFromName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return BluetoothDeviceCategory.Other;
            string s = name.ToLowerInvariant();

            if (s.Contains("keyboard") || s.Contains("键盘") || s.Contains("keychron") ||
                s.Contains("ikbc") || s.Contains("filco") || s.Contains("nuphy") ||
                s.Contains("hhkb") || s.Contains("rk") || s.Contains("akko") ||
                s.Contains("lofree") || s.Contains("cherry") || s.Contains("mx keys") ||
                s.Contains("k380") || s.Contains("k580") || s.Contains("qinglong") ||
                s.Contains("青龙"))
            {
                return BluetoothDeviceCategory.Keyboard;
            }

            if (s.Contains("mouse") || s.Contains("鼠标") || s.Contains("trackball") ||
                s.Contains("touchpad") || s.Contains("g304") || s.Contains("g502") ||
                s.Contains("master") || s.Contains("anywhere") || s.Contains("superlight") ||
                s.Contains("viper") || s.Contains("pulsar") || s.Contains("deathadder"))
            {
                return BluetoothDeviceCategory.Mouse;
            }

            if (s.Contains("controller") || s.Contains("gamepad") || s.Contains("手柄") ||
                s.Contains("xbox") || s.Contains("dualsense") || s.Contains("dualshock") ||
                s.Contains("joy-con") || s.Contains("switch pro"))
            {
                return BluetoothDeviceCategory.Gamepad;
            }

            if (s.Contains("tws") || s.Contains("ear") || s.Contains("buds") ||
                s.Contains("headphone") || s.Contains("headset") || s.Contains("audio") ||
                s.Contains("airpods") || s.Contains("freebuds") || s.Contains("enco") ||
                s.Contains("sound") || s.Contains("speaker") || s.Contains("wh-1000") ||
                s.Contains("wf-1000") || s.Contains("bose") || s.Contains("beats") ||
                s.Contains("shokz") || s.Contains("vivo tws") || s.Contains("oppo enco"))
            {
                return BluetoothDeviceCategory.Audio;
            }

            return BluetoothDeviceCategory.Other;
        }

        // ===================== 辅助函数 =====================

        private static string ExtractMacAddress(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            var m = Regex.Match(input, @"([0-9a-fA-F]{2}[:-]){5}[0-9a-fA-F]{2}");
            if (m.Success)
            {
                return m.Value.Replace(":", "").Replace("-", "").ToUpperInvariant();
            }
            m = Regex.Match(input, @"DEV_([0-9a-fA-F]{12})", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                return m.Groups[1].Value.ToUpperInvariant();
            }
            m = Regex.Match(input, @"([0-9a-fA-F]{12})");
            if (m.Success)
            {
                return m.Groups[1].Value.ToUpperInvariant();
            }
            return "";
        }

        private static string CleanDeviceName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "蓝牙设备";
            string s = name.Trim();

            // 提取 "耳机 (vivo TWS 4 Hi-Fi)" 中的括号内容
            var match = Regex.Match(s, @"\(([^)]+)\)");
            if (match.Success && !match.Groups[1].Value.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase))
            {
                string inner = match.Groups[1].Value.Trim();
                if (inner.Length > 2) s = inner;
            }

            s = Regex.Replace(s, @"\s*(Hands-Free AG Audio|Stereo|Bluetooth|\(Bluetooth\))\s*", " ", RegexOptions.IgnoreCase).Trim();
            return string.IsNullOrWhiteSpace(s) ? name : s;
        }

        private sealed class AudioNotificationClient : IMMNotificationClient
        {
            private readonly BluetoothService _owner;
            public AudioNotificationClient(BluetoothService owner) => _owner = owner;

            public void OnDeviceStateChanged(string pwstrDeviceId, DeviceState dwNewState)
            {
                if (dwNewState == DeviceState.Active)
                {
                    _owner.CheckAudioEndpoint(pwstrDeviceId);
                }
            }

            public void OnDeviceAdded(string pwstrDeviceId)
            {
                _owner.CheckAudioEndpoint(pwstrDeviceId);
            }

            public void OnDeviceRemoved(string pwstrDeviceId) { }

            public void OnDefaultDeviceChanged(DataFlow flow, Role role, string pwstrDefaultDeviceId)
            {
                if (flow == DataFlow.Render && (role == Role.Multimedia || role == Role.Console))
                {
                    _owner.CheckAudioEndpoint(pwstrDefaultDeviceId);
                }
            }

            public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
        }
    }
}
