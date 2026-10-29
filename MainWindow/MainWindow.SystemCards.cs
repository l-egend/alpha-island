using L = AlphaIsland.PluginSdk.Localization.UiText;
using System;
using System.Collections.Generic;
using System.Text;
using alpha_island.Core;
using alpha_island.Services;

// 两个新增卡片页（实时网速 / 系统状态）的装配。
//
// 需求来源（用户原话要点）：本批新增功能必须"岛和卡都有"，否则用户既看不到也没法测；
// 网速这类信息要"精简且有效"，不能只是一个图标。于是：
//   · 实时网速页 = 实时上下行大字 + 最近 60 秒曲线 + 峰值/网卡名；细条常驻数字与
//     手动展开或置顶的一级胶囊共用同一份采样（MainWindow.SystemStatus.cs 的 1s 节拍）；
//   · 系统状态页 = 统一 CPU / 内存 / CPU＋GPU 功耗区域，以及无线状态与隐私；
//     对应服务没开时格子写"未开启 · 设置 → 系统状态"，页本身始终可翻到。
namespace alpha_island
{
    public partial class MainWindow
    {
        // ================= 布局参数 =================

        /// <summary>曲线窗口 = 最近 60 拍（1s 一拍 → 60 秒）。</summary>
        private const int NetSpeedHistoryMax = 60;
        private long _lastNetworkHistoryTick;

        // ================= 状态 =================

        /// <summary>最近 60 拍的下行字节/秒（曲线数据源；也用于算峰值）。</summary>
        private readonly List<double> _netSpeedDownHistory = new(NetSpeedHistoryMax);
        private readonly List<double> _netSpeedUpHistory = new(NetSpeedHistoryMax);


        /// <summary>调试钩子 ALPHA_ISLAND_FAKE_NETSPEED=<KB/s>：插值成假采样，用来在没有真实下载时验证胶囊/曲线。</summary>
        private double? _netSpeedFakeKbps;
        private bool _netSpeedFakeParsed;

        private string? _privacyMicApp;
        private string? _privacyCamApp;
        private string _wirelessSummary = L.T("等待无线设备状态");

        /// <summary>网速卡是否正显示（翻页 + 卡片态）；只有这时才逐拍重画曲线。</summary>
        internal bool IsNetSpeedCardVisible =>
            _state == IslandState.Card && _pages.ActiveId == IslandPageIds.NetSpeed;

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
        /// 网速采样的统一落点：推进曲线缓冲 → 刷新卡片页（如果正显示）→ 刷新手动展开/置顶的状态栏。
        /// 必须每拍都调（不管卡片页有没有在显示），否则曲线会出现空洞。
        /// </summary>
        private void UpdateNetSpeedSample(NetworkSpeedSample sample)
        {
            long now = Environment.TickCount64;
            if (_lastNetworkHistoryTick != 0 && now - _lastNetworkHistoryTick > 1500)
            {
                _netSpeedDownHistory.Clear(); _netSpeedUpHistory.Clear();
            }
            _lastNetworkHistoryTick = now;
            _netSpeedDownHistory.Add(sample.DownBytesPerSecond);
            _netSpeedUpHistory.Add(sample.UpBytesPerSecond);
            while (_netSpeedDownHistory.Count > NetSpeedHistoryMax) _netSpeedDownHistory.RemoveAt(0);
            while (_netSpeedUpHistory.Count > NetSpeedHistoryMax) _netSpeedUpHistory.RemoveAt(0);

            // 实时驱动左侧常驻卫星小岛（若网速作为卫星小岛显示）
            UpdateNetSpeedSatelliteVisual(sample);

            UpdateNetSpeedPrimary(sample);

            if (IsNetSpeedCardVisible) WriteNetSpeedCard(sample);
        }

        private void ClearNetSpeedHistory()
        {
            _lastNetworkHistoryTick = 0;
            _netSpeedDownHistory.Clear(); _netSpeedUpHistory.Clear();
            NetSpeedSparkline.Clear(); NetSpeedUpSparkline.Clear();
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

            bool havelService = _netSpeed != null || _externalNetwork.Active;

            NetworkCardIcon.Active = havelService;
            NetSpeedCardStatusText.Text = !havelService
                ? L.T("网速检测未开启")
                : _externalNetwork.Active ? L.T("来源：") + _externalNetwork.Title
                : (string.IsNullOrEmpty(_netSpeed!.DegradedReason) ? L.T("来源：系统") : _netSpeed.DegradedReason);

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

            // Both directions share a time window and scale. Each line keeps its own measured samples.
            double downPeak = _netSpeedDownHistory.Count > 0 ? _netSpeedDownHistory.Max() : 0;
            double upPeak = _netSpeedUpHistory.Count > 0 ? _netSpeedUpHistory.Max() : 0;
            NetSpeedCardPeakText.Text = havelService ? L.T("↓ 下载 · 峰值 ") + NetworkSpeedService.FormatSpeedFull(downPeak) : L.T("↓ 下载 · 峰值 —");
            NetSpeedCardUpPeakText.Text = havelService ? L.T("↑ 上传 · 峰值 ") + NetworkSpeedService.FormatSpeedFull(upPeak) : L.T("↑ 上传 · 峰值 —");
            double axisMax = Math.Max(1, Math.Max(downPeak, upPeak) * 1.1);
            NetSpeedSparkline.FixedMax = axisMax; NetSpeedUpSparkline.FixedMax = axisMax;
            NetSpeedSparkline.SetValues(_netSpeedDownHistory);
            NetSpeedUpSparkline.SetValues(_netSpeedUpHistory);

            NetSpeedCardHintText.Text = L.T("近 60 秒") + BuildTrafficHint();

            IslandLog.Debug("Cards", $"网速卡：下行 {NetworkSpeedService.FormatSpeedFull(down)}" +
                                     $"｜上行 {NetworkSpeedService.FormatSpeedFull(up)}" +
                                     $"｜峰值 {NetworkSpeedService.FormatSpeedFull(downPeak)}" +
                                     $"｜曲线 {_netSpeedDownHistory.Count} 点" +
                                     $"｜服务 {(_netSpeed == null ? "未启动" : "在跑")}" +
                                     $"｜提示 {NetSpeedCardHintText.Text}");
        }

        // ================= 系统状态页 =================

        /// <summary>页被切到时调用：用四个服务的最新读数整体刷新一次。</summary>
        internal void SyncSystemCard()
        {
            if (SystemCardLayer == null) return;

            // The card and the small status island render the same CPU/RAM/hardware snapshot.
            SysCardBatteryDetail.Text = ChargingBatteryDescription;
            SystemBatterySummary.SetBattery(_chargingStatus ?? _battery?.Current);
            // --- 无线连接 ---
            SysCardWirelessValue.Text = _radioMonitoring ? L.T("Wi-Fi / 蓝牙") : L.T("未开启监测");
            SysCardWirelessDetail.Text = _radioMonitoring ? _wirelessSummary : "";
            // --- 隐私 ---
            bool mic = _privacyMicActive;
            bool cam = _privacyCamActive;
            string privacyValue;
            if (_privacy == null)
            {
                privacyValue = L.T("未开启");
            }
            else if (mic && cam)
            {
                privacyValue = L.T("都在使用");
            }
            else if (mic)
            {
                privacyValue = L.T("麦克风使用中");
            }
            else if (cam)
            {
                privacyValue = L.T("摄像头使用中");
            }
            else
            {
                privacyValue = _privacy.DegradedReason != null ? L.T("状态未知") : L.T("未在使用");
            }

            SysCardPrivacyValue.Text = privacyValue;
            SysCardPrivacyDetail.Text = _privacy == null
                ? ""
                : _privacy.DegradedReason ?? BuildPrivacyDetail(mic, cam);

            SystemCardSummaryText.Text = L.T("CPU · 内存 · CPU＋GPU 总功耗");

            IslandLog.Debug("Cards",
                $"系统状态卡：内存 {SysCardMemValue.Text}（{SysCardMemDetail.Text}）｜功耗 {SysCardPowerValue.Text}（{SysCardPowerDetail.Text}）" +
                $"｜无线 {SysCardWirelessValue.Text}｜隐私 {privacyValue}（{SysCardPrivacyDetail.Text}）");
        }

        private string BuildPrivacyDetail(bool mic, bool cam)
        {
            var parts = new List<string>(2);
            if (mic) parts.Add(L.T("麦克风 ") + (_privacyMicApp ?? L.T("未知应用")));
            if (cam) parts.Add(L.T("摄像头 ") + (_privacyCamApp ?? L.T("未知应用")));
            if (parts.Count == 0) return L.T("麦克风 / 摄像头都没有被占用");
            return string.Join("｜", parts);
        }

        /// <summary>SamplePrivacy 把本轮占用麦克风/摄像头的应用名记下来（卡片页的详情行要显示是谁）。</summary>
        private void RecordPrivacyApps(string? micApp, string? camApp)
        {
            _privacyMicApp = micApp;
            _privacyCamApp = camApp;
        }
    }
}
