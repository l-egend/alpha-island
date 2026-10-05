using System;
using System.Collections.Generic;
using System.Text;
using alpha_island.Core;
using alpha_island.Services;

// C 组②：两个新增卡片页（实时网速 / 系统状态）的装配。
//
// 需求来源（用户原话要点）：本批新增功能必须"岛和卡都有"，否则用户既看不到也没法测；
// 网速这类信息要"精简且有效"，不能只是一个图标。于是：
//   · 实时网速页 = 实时上下行大字 + 最近 60 秒曲线 + 峰值/网卡名；细条常驻数字与
//     「下载期间常显」的一级胶囊共用同一份采样（MainWindow.SystemStatus.cs 的 1s 节拍）；
//   · 系统状态页 = 内存 / 功耗 / 飞行模式 / 隐私 四格实时值（用户拍板合并成一页）；
//     对应服务没开时格子写"未开启 · 设置 → 系统状态"，页本身始终可翻到。
namespace alpha_island
{
    public partial class MainWindow
    {
        // ================= 布局参数 =================

        /// <summary>曲线窗口 = 最近 60 拍（1s 一拍 → 60 秒）。</summary>
        private const int NetSpeedHistoryMax = 60;

        /// <summary>「下载期间常显」的门槛：低于这个速率不占用一级胶囊（避免后台心跳把音乐胶囊顶掉）。</summary>
        private const double NetSpeedPillBytes = 128 * 1024;

        /// <summary>连续多少拍没到门槛才收起胶囊（缓冲空档不闪）。</summary>
        private const int NetSpeedPillQuietTicks = 3;

        /// <summary>每拍给胶囊续期的秒数：只要还在下载，ShowSeconds 就一直把它顶在未来。</summary>
        private const double NetSpeedPillSeconds = 4;

        // ================= 状态 =================

        /// <summary>最近 60 拍的下行字节/秒（曲线数据源；也用于算峰值）。</summary>
        private readonly List<double> _netSpeedDownHistory = new(NetSpeedHistoryMax);

        private int _netSpeedPillTicks;
        private int _netSpeedQuietTicks;
        private bool _netSpeedPillShown;

        /// <summary>调试钩子 ALPHA_ISLAND_FAKE_NETSPEED=<KB/s>：插值成假采样，用来在没有真实下载时验证胶囊/曲线。</summary>
        private double? _netSpeedFakeKbps;
        private bool _netSpeedFakeParsed;

        /// <summary>最近一次电池读数（卡片页功耗格）。SamplePower 的两条分支都会写。</summary>
        private int? _lastPowerMilliwatts;
        private bool _lastPowerCharging;
        private bool _lastPowerOnAc;
        private bool _lastPowerHasBattery;
        private bool _lastPowerEstimated;
        private int? _lastPowerRemainingSeconds;
        private bool _powerSampled;

        private string? _privacyMicApp;
        private string? _privacyCamApp;
        private string _wirelessSummary = "等待无线设备状态";

        /// <summary>网速卡是否正显示（翻页 + 卡片态）；只有这时才逐拍重画曲线。</summary>
        internal bool IsNetSpeedCardVisible =>
            _state == IslandState.Card && _pages.ActiveId == IslandPageIds.System;

        internal bool IsSystemCardVisible =>
            _state == IslandState.Card && _pages.ActiveId == IslandPageIds.System;

        internal void SetNetSpeedCardLayerHeight(double height)
        {
            if (NetSpeedCardLayer != null) NetSpeedCardLayer.Height = height;
        }

        internal void SetSystemCardLayerHeight(double height)
        {
            if (SystemCardLayer != null) SystemCardLayer.Height = height;
        }

        // ================= 每拍推进（1s 节拍，见 SampleNetworkSpeed） =================

        /// <summary>
        /// 网速采样的统一落点：推进曲线缓冲 → 刷新卡片页（如果正显示）→ 驱动「下载期间常显」的胶囊。
        /// 必须每拍都调（不管卡片页有没有在显示），否则曲线会出现空洞。
        /// </summary>
        private void UpdateNetSpeedSample(NetworkSpeedSample sample)
        {
            _netSpeedDownHistory.Add(sample.DownBytesPerSecond);
            while (_netSpeedDownHistory.Count > NetSpeedHistoryMax) _netSpeedDownHistory.RemoveAt(0);

            // 实时驱动左侧常驻卫星小岛（若网速作为卫星小岛显示）
            UpdateNetSpeedSatelliteVisual(sample);

            UpdateNetSpeedPrimary(sample);

            double thresholdMbps = Math.Max(1.0, SettingsRegistry.GetDouble(_options, "NetSpeedThresholdMbps", 10.0));
            double thresholdBytes = thresholdMbps * 1024 * 1024;
            bool notifyEnabled = SettingsRegistry.GetBool(_options, "NetSpeedNotifyEnabled", true);

            if (_layers.IsPinned("netspeed") || (notifyEnabled && (sample.DownBytesPerSecond >= thresholdBytes || sample.UpBytesPerSecond >= thresholdBytes)) || _netSpeedPillTicks > 0)
            {
                UpdateNetSpeedPill(sample);
            }

            if (IsNetSpeedCardVisible) WriteNetSpeedCard(sample);
        }

        private void UpdateNetSpeedPrimary(NetworkSpeedSample sample)
        {
            UpdateCombinedStatus(NetSpeedRates, sample);
        }

        /// <summary>页被切到时调用（含"重切同一页"）：用当前采样整体刷新一次，别等下一拍。</summary>
        internal void SyncNetSpeedCard()
        {
            if (NetSpeedCardLayer == null) return;

            NetworkSpeedSample sample = CurrentNetworkSample;
            WriteNetSpeedCard(sample);
        }

        /// <summary>把一次采样的数值写进网速卡片页（不做任何采样/启停判断）。</summary>
        private void WriteNetSpeedCard(NetworkSpeedSample sample)
        {
            if (NetSpeedCardLayer == null) return;

            bool havelService = _netSpeed != null;

            NetSpeedCardStatusText.Text = !havelService
                ? "网速检测未开启"
                : _externalNetwork.Active ? "来源：" + _externalNetwork.Title
                : (string.IsNullOrEmpty(_netSpeed!.DegradedReason) ? "来源：系统" : _netSpeed.DegradedReason);

            NetSpeedCardAdapterText.Text = _externalNetwork.Active ? _externalNetwork.Reading!.Adapter : havelService && _netSpeed!.AdapterNames.Count > 0
                ? _netSpeed.AdapterNames[0]
                : "—";

            double down = sample.DownBytesPerSecond;
            double up = sample.UpBytesPerSecond;

            NetSpeedCardDownText.Text = havelService && down > 0
                ? NetworkSpeedService.FormatSpeedFull(down)
                : (havelService ? "0 B/s" : "—");
            NetSpeedCardUpText.Text = havelService && up > 0
                ? NetworkSpeedService.FormatSpeedFull(up)
                : (havelService ? "0 B/s" : "—");

            double peak = 0;
            for (int i = 0; i < _netSpeedDownHistory.Count; i++)
            {
                if (_netSpeedDownHistory[i] > peak) peak = _netSpeedDownHistory[i];
            }
            NetSpeedCardPeakText.Text = peak > 0
                ? "峰值 " + NetworkSpeedService.FormatSpeedFull(peak)
                : "峰值 —";

            if (NetSpeedSparkline != null)
            {
                NetSpeedSparkline.SetValues(_netSpeedDownHistory);
            }

            NetSpeedCardHintText.Text = "近 60 秒" + BuildTrafficHint();

            IslandLog.Debug("Cards", $"网速卡：下行 {NetworkSpeedService.FormatSpeedFull(down)}" +
                                     $"｜上行 {NetworkSpeedService.FormatSpeedFull(up)}" +
                                     $"｜峰值 {NetworkSpeedService.FormatSpeedFull(peak)}" +
                                     $"｜曲线 {_netSpeedDownHistory.Count} 点" +
                                     $"｜服务 {(_netSpeed == null ? "未启动" : "在跑")}" +
                                     $"｜提示 {NetSpeedCardHintText.Text}");
        }

        /// <summary>
        /// 「下载期间网速常显」：速率过门槛就把网速层顶在未来（每拍续期 = 一直显示），
        /// 连续 3 拍低于门槛才收起。卡片态不抢形态 —— 用户正看着卡片时不要把岛收成胶囊。
        /// </summary>
        private void UpdateNetSpeedPill(NetworkSpeedSample sample)
        {
            if (_state == IslandState.Card) return;

            // 如果网速已被独立置顶，常驻在岛旁以便于观测数据，不需要作为临时弹窗自动收起
            if (_layers.IsPinned("netspeed"))
            {
                return;
            }

            bool notifyEnabled = SettingsRegistry.GetBool(_options, "NetSpeedNotifyEnabled", true);
            if (!notifyEnabled) return;

            double thresholdMbps = Math.Max(1.0, SettingsRegistry.GetDouble(_options, "NetSpeedThresholdMbps", 10.0));
            double thresholdBytes = thresholdMbps * 1024 * 1024;

            bool busy = sample.DownBytesPerSecond >= thresholdBytes ||
                        sample.UpBytesPerSecond >= thresholdBytes;

            if (busy)
            {
                _netSpeedQuietTicks = 0;
                _netSpeedPillTicks++;

                // 刷新同一网速提示层中的速率、趋势和会话信息。
                UpdateNetSpeedPrimary(sample);
                NetSpeedNotifyLayer.ToolTip = sample.DownBytesPerSecond >= thresholdBytes ? "高速下载" : "高速上传";

                // 细条态要先展开成胶囊（PillOnly），卡片态不顶掉用户正开着的卡片。
                ShowPillNotification(IslandNotification.Refreshing("netspeed", NetSpeedPillSeconds));

                if (!_netSpeedPillShown)
                {
                    _netSpeedPillShown = true;
                    IslandLog.Debug("NetSpeed",
                        $"网络速率过阈值：网速胶囊提示（下行 {NetworkSpeedService.FormatSpeedFull(sample.DownBytesPerSecond)}，" +
                        $"上行 {NetworkSpeedService.FormatSpeedFull(sample.UpBytesPerSecond)}，门槛 {thresholdMbps:0} MB/s）");
                }

                return;
            }

            if (!_netSpeedPillShown) return;

            if (++_netSpeedQuietTicks < NetSpeedPillQuietTicks) return;

            _netSpeedQuietTicks = 0;
            _netSpeedPillTicks = 0;
            _netSpeedPillShown = false;
            _layers.Hide("netspeed");
            IslandLog.Debug("NetSpeed", "下载/上传降速：网速胶囊收起");
        }

        // ================= 系统状态页 =================

        /// <summary>页被切到时调用：用四个服务的最新读数整体刷新一次。</summary>
        internal void SyncSystemCard()
        {
            if (SystemCardLayer == null) return;

            // --- 内存 ---
            bool memOn = _options != null && SettingsRegistry.GetBool(_options, "MemoryMonitorEnabled", false);
            MemorySample mem = _memory?.Current ?? default;
            string memValue;
            string memDetail;

            if (_memory == null || !memOn)
            {
                memValue = "未开启";
                memDetail = "";
            }
            else if (!mem.Valid)
            {
                memValue = "—";
                memDetail = "采样中…";
            }
            else
            {
                memValue = $"{mem.UsedPercent:0}%";
                memDetail = $"已用 {MemoryService.FormatSize(mem.UsedBytes)} / {MemoryService.FormatSize((long)mem.TotalBytes)}";
            }

            SysCardMemValue.Text = memValue;
            SysCardMemDetail.Text = memDetail;

            // --- 功耗 ---
            string powerValue;
            string powerDetail;
            if (!_powerSampled)
            {
                powerValue = "—";
                powerDetail = _battery == null ? "本机没有电池" : "等待采样…";
            }
            else if (!_lastPowerHasBattery)
            {
                powerValue = "无电池";
                powerDetail = "功率不可用";
            }
            else if (_lastPowerOnAc)
            {
                powerValue = _lastPowerCharging ? "充电中" : "接通电源";
                powerDetail = "";
            }
            else if (!_lastPowerMilliwatts.HasValue)
            {
                powerValue = "—";
                powerDetail = "电池未上报放电功率";
            }
            else
            {
                powerValue = BatteryService.FormatPower(_lastPowerMilliwatts);
                powerDetail = (_lastPowerEstimated ? "估算功率｜" : "电池放电功率｜") + "预计剩余 " + BatteryService.FormatRemaining(_lastPowerRemainingSeconds);
            }

            SysCardPowerValue.Text = powerValue;
            SysCardPowerDetail.Text = powerDetail;

            // --- 飞行模式 ---
            SysCardAirValue.Text = !_airplaneKnown ? "未知" : (_airplaneOn ? "已开启" : "已关闭");
            SysCardAirDetail.Text = !_airplaneKnown
                ? _wirelessSummary
                : "";

            // --- 隐私 ---
            bool mic = _privacyMicActive;
            bool cam = _privacyCamActive;
            string privacyValue;
            if (_privacy == null)
            {
                privacyValue = "未开启";
            }
            else if (mic && cam)
            {
                privacyValue = "都在使用";
            }
            else if (mic)
            {
                privacyValue = "麦克风使用中";
            }
            else if (cam)
            {
                privacyValue = "摄像头使用中";
            }
            else
            {
                privacyValue = _privacy.DegradedReason != null ? "状态未知" : "未在使用";
            }

            SysCardPrivacyValue.Text = privacyValue;
            SysCardPrivacyDetail.Text = _privacy == null
                ? ""
                : _privacy.DegradedReason ?? BuildPrivacyDetail(mic, cam);

            SystemCardSummaryText.Text =
                $"内存 {memValue}｜功耗 {powerValue}｜飞行 {SysCardAirValue.Text}｜隐私 {privacyValue}";

            IslandLog.Debug("Cards",
                $"系统状态卡：内存 {memValue}（{memDetail}）｜功耗 {powerValue}（{powerDetail}）" +
                $"｜飞行 {SysCardAirValue.Text}｜隐私 {privacyValue}（{SysCardPrivacyDetail.Text}）");
        }

        private string BuildPrivacyDetail(bool mic, bool cam)
        {
            var parts = new List<string>(2);
            if (mic) parts.Add("麦克风 " + (_privacyMicApp ?? "未知应用"));
            if (cam) parts.Add("摄像头 " + (_privacyCamApp ?? "未知应用"));
            if (parts.Count == 0) return "麦克风 / 摄像头都没有被占用";
            return string.Join("｜", parts);
        }

        /// <summary>SamplePower 的两条分支（真采样 / 调试注入）都调这里，卡片页才有值可显示。</summary>
        private void RecordPowerReading(BatteryStatusInfo info)
        {
            _powerSampled = true;
            _lastPowerCharging = info.IsCharging;
            _lastPowerOnAc = info.IsOnAcPower ?? info.IsCharging;
            _lastPowerHasBattery = info.HasBattery;
            _lastPowerEstimated = info.IsPowerEstimated;
            _lastPowerMilliwatts = info.DischargeRateMilliwatts;
            _lastPowerRemainingSeconds = info.RemainingLifeSeconds;

            if (IsSystemCardVisible) SyncSystemCard();
        }

        /// <summary>SamplePrivacy 把本轮占用麦克风/摄像头的应用名记下来（卡片页的详情行要显示是谁）。</summary>
        private void RecordPrivacyApps(string? micApp, string? camApp)
        {
            _privacyMicApp = micApp;
            _privacyCamApp = camApp;
        }
    }
}
