using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Windows.Controls;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;

namespace alpha_island;

public partial class MainWindow
{
    private const string ChargingIslandKey = "charging";
    private const string ChargingPreviewIslandKey = "charging-preview";
    private static readonly BatteryStatusInfo ChargingPreviewStatus = new(80, true, BatteryChargeType.StandardAC, BatteryPowerEvent.None, null, IsOnAcPower: true);
    private BatteryStatusInfo? _chargingStatus;
    private BatteryStatusInfo _chargingPreviewStatus = ChargingPreviewStatus;
    private string? _chargingPreviewReturnKey;
    private bool _powerTransitionPreparing, _powerTransitionPending, _powerTransitionPreview;
    private bool _powerTransitionAwaitingClaim;
    private string? _powerTransitionReturnKey;
    private string? _powerCompactEmergingKey;
    private BatteryPowerEvent _powerTransitionEvent;
    private DateTime _powerAnimationDeadlineUtc;
    private bool PowerAnimationHolding => _powerTransitionPending && DateTime.UtcNow < _powerAnimationDeadlineUtc
        && (_batteryAnimationTimer != null || BatteryRipple.IsAnimating);
    private string PowerTransitionCompactKey => _powerTransitionPreview ? ChargingPreviewIslandKey : ChargingIslandKey;

    private void BeginPowerIslandTransition(BatteryStatusInfo info, bool preview)
    {
        if (_activeMainIslandKey != "battery") _powerTransitionReturnKey = _activeMainIslandKey;
        _powerTransitionPreview = preview; _powerTransitionPending = true; _powerTransitionPreparing = true;
        _powerTransitionAwaitingClaim = true;
        _powerTransitionEvent = info.EventType;
        _powerAnimationDeadlineUtc = DateTime.UtcNow.AddSeconds(15);
        if (_animator != null) _animator.PreservePowerTransitionMotion = true;
        try
        {
            if (preview)
            {
                _chargingPreviewStatus = info with { EventType = BatteryPowerEvent.None };
                _layers.Request(IslandNotification.Seconds(ChargingPreviewIslandKey,
                    Math.Max(4, _options.BatteryNotifySeconds) + 8, opensIsland: false), requestSync: false);
                SetChargingIslandContent(ChargingPreviewPrimary, ChargingPreviewIslandKey);
            }
            else UpdateChargingIsland(info, expandWhenIdle: false);
        }
        finally { _powerTransitionPreparing = false; }
    }

    private void CompletePowerIslandTransition()
    {
        if (!_powerTransitionPending || _powerTransitionPreparing || _powerTransitionAwaitingClaim || BatteryNotifyActive) return;
        _powerTransitionPending = false;
        if (_animator != null) _animator.PreservePowerTransitionMotion = false;
        _batteryAnimationTimer?.Stop(); _batteryAnimationTimer = null;
        BatteryRipple.Stop(); BatteryIcon.EndTransition();
        string compactKey = PowerTransitionCompactKey;
        if (_powerTransitionPreview && _layers.IsShowing(compactKey))
            _layers.Request(IslandNotification.Seconds(compactKey, 8, opensIsland: false), requestSync: false);
        if (_activeMainIslandKey == "battery")
        {
            string target = _powerTransitionReturnKey is { } previous && IsIslandActive(previous) ? previous
                : IsIslandActive(compactKey) ? compactKey : "music";
            _activeMainIslandKey = target; _layers.PreferredWinnerKey = target;
            _activeSatelliteOrder.Remove(target);
            if (target != compactKey && IsIslandActive(compactKey) && !_activeSatelliteOrder.Contains(compactKey))
            {
                _activeSatelliteOrder.Insert(0, compactKey);
                _powerCompactEmergingKey = compactKey;
            }
            AnimateIslandTransition("battery", target);
        }
        _powerTransitionReturnKey = null;
        if (_state == IslandState.Pill && !IsIslandActive(compactKey)
            && !HasActiveNotification && !_layers.AnyPinned && !MusicIslandCanShow)
            CollapseToBar(force: true);
    }

    private bool TryOpenBatteryIsland(string? key)
    {
        if (key is not (ChargingIslandKey or ChargingPreviewIslandKey or "battery")) return false;
        SelectComponent(IslandPageIds.System);
        return true;
    }

    private void SetChargingIslandContent(BatteryChargePill view, string key)
    {
        view.Tag = key;
        view.SetBattery(key == ChargingPreviewIslandKey ? _chargingPreviewStatus : _chargingStatus);
        if (key == ChargingPreviewIslandKey) { view.StateText.Text = _layers.IsPinned(key) ? L.T("测试已置顶") : L.T("测试预览"); view.ToolTip = view.PercentText.Text + (_layers.IsPinned(key) ? L.T(" · 测试已置顶（右键取消后收回）") : L.T(" · 充电提示测试（默认 8 秒，可右键置顶）")); }
    }
    private void PreviewChargingIsland()
    {
        if (_exiting || _sessionLocked) return;
        _chargingPreviewStatus = ChargingPreviewStatus;
        if (_activeMainIslandKey != ChargingPreviewIslandKey) _chargingPreviewReturnKey = _activeMainIslandKey;
        SetChargingIslandContent(ChargingPreviewPrimary, ChargingPreviewIslandKey);
        ShowPillNotification(IslandNotification.Seconds(ChargingPreviewIslandKey, 8, deferredInCard: false) with
            { Glyph = "\uE83F", Title = L.T("充电提示测试"), Value = "80%" });
        TryMorphPillWidth();
    }
    private void RestoreAfterChargingPreview()
    {
        if (_chargingPreviewReturnKey == null || _layers.IsActive(ChargingPreviewIslandKey)) return;
        string key = _chargingPreviewReturnKey; _chargingPreviewReturnKey = null;
        if (_state == IslandState.Pill && _activeMainIslandKey == ChargingPreviewIslandKey && IsIslandActive(key)) SwitchMainIsland(key);
    }
    private bool ChargingIslandAvailable => _chargingStatus is { HasBattery: true };
    private bool ChargingIslandActive => !_exiting && !_sessionLocked && ChargingIslandAvailable
        && _layers.IsPinned(ChargingIslandKey) && _layers.IsShowing(ChargingIslandKey);
    private string ChargingBatteryDescription => _chargingStatus is { HasBattery: true } info
        ? L.T("电池 ") + (info.Percent >= 0 ? $"{info.Percent}%" : "—") + " · "
            + (info.Percent == 100 && info.IsOnAcPower == true ? L.T("已充满") : info.IsCharging ? L.T("正在充电") : info.IsOnAcPower == true ? L.T("已接通电源") : L.T("电池供电"))
            + (info.IsEnergySaverEnabled ? L.T(" · 节电模式") : "")
            + (info.SmartCharging == SmartChargingState.Active ? L.T(" · 智能充电") : "")
            + (info.IsOnAcPower == true ? " · " + BatteryService.FormatChargeType(info.ChargeType) : "")
        : L.T("等待电池状态");

    private void UpdateChargingIsland(BatteryStatusInfo info, bool expandWhenIdle = true)
    {
        bool previouslyAvailable = ChargingIslandAvailable;
        bool previouslyActive = ChargingIslandActive;
        _chargingStatus = info;
        ChargingPrimary.SetBattery(info);
        if (_powerTransitionPending && !_powerTransitionPreview)
        {
            BatteryPercentText.Text = info.Percent >= 0 ? $"{info.Percent}%" : L.T("未知");
            if (_powerTransitionEvent == BatteryPowerEvent.PluggedIn)
                BatteryStatusText.Text = info.SmartCharging == SmartChargingState.Active ? L.T("已连接电源 · 智能充电")
                    : L.T("已连接电源 · ") + BatteryService.FormatChargeType(info.ChargeType);
            BatteryIcon.Level = info.Percent < 0 ? 0 : Math.Clamp(info.Percent / 100d, 0, 1);
        }
        if (_sideChargingView != null && (_sideChargingView.Tag as string) != ChargingPreviewIslandKey) _sideChargingView.SetBattery(info);
        if (_satelliteOrbViews.TryGetValue(ChargingIslandKey, out var orb) && orb.Child is Grid grid
            && grid.Children.OfType<BatteryChargePill>().FirstOrDefault() is { } view) view.SetBattery(info);
        SysCardBatteryDetail.Text = ChargingBatteryDescription;
        SystemBatterySummary.SetBattery(info);
        SystemBatterySummary.ToolTip += _layers.IsPinned(ChargingIslandKey) ? L.T(" · 右键取消电量岛置顶") : L.T(" · 右键置顶电量岛");
        // Battery data always updates the system card. A compact battery island is
        // requested only by the user's pin, independent of smart-charging support.
        if (ChargingIslandAvailable && _layers.IsPinned(ChargingIslandKey))
        {
            var request = IslandNotification.Refreshing(ChargingIslandKey, 3, opensIsland: false);
            _layers.Request(request with { Glyph = "\uE83F", Title = L.T("电池电量"), Value = ChargingPrimary.PercentText.Text }, requestSync: false);
        }
        else if (!ChargingIslandAvailable)
        {
            _layers.SetPinned(ChargingIslandKey, false);
            if (previouslyAvailable || _layers.IsActive(ChargingIslandKey)) _layers.Hide(ChargingIslandKey);
        }
        else if (!_layers.IsPinned(ChargingIslandKey) && _layers.IsShowing(ChargingIslandKey))
            _layers.Hide(ChargingIslandKey);
        if (_powerTransitionPreparing) return;
        SyncNotificationLayers(); SyncSatelliteOrbs();
        if (ChargingIslandActive && !previouslyActive && expandWhenIdle && _state == IslandState.Bar)
        {
            if (!MusicIslandCanShow && !_layers.AnyPinned)
            { _activeMainIslandKey = ChargingIslandKey; _layers.PreferredWinnerKey = ChargingIslandKey; }
            SetState(IslandState.Pill);
        }
        else if (!ChargingIslandActive && previouslyActive && _state == IslandState.Pill
            && !HasActiveNotification && !_layers.AnyPinned && !MusicIslandCanShow) CollapseToBar(force: true);
        TryMorphPillWidth();
    }
}
