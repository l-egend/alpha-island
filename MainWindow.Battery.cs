using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;
using alpha_island.Settings;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;

// Battery status notifications and temporary animation previews.
namespace alpha_island
{
    public partial class MainWindow
    {
        // ================= 电池状态通知 =================

        private void OnBatteryStatusChanged(object? sender, BatteryStatusInfo info)
        {
            if (!_exiting)
            {
                RecordBatteryDesktopEvent(info);
            }
            if (_exiting) return;
            if (!_options.BatteryNotifyEnabled || !info.HasBattery || info.EventType == BatteryPowerEvent.None)
            { UpdateChargingIsland(info); return; }
            if (_sessionLocked || GameDoNotDisturbActive || (SettingsRegistry.GetBool(_options, "NotificationQuietMode", false)
                && (info.EventType is not (BatteryPowerEvent.LowBattery or BatteryPowerEvent.CriticalBattery)
                    || !SettingsRegistry.GetBool(_options, "NotificationQuietAllowPriority", true))))
            { UpdateChargingIsland(info); return; }
            ShowBatteryNotify(info);
        }

        private DispatcherTimer? _batteryAnimationTimer;

        private void PreviewBatteryAnimation(BatteryPowerEvent powerEvent)
        {
            if (_exiting || _sessionLocked || powerEvent is not (BatteryPowerEvent.PluggedIn or BatteryPowerEvent.Unplugged)) return;
            int percent = _battery?.Current?.Percent is >= 0 and <= 100 ? _battery.Current.Percent : 68;
            bool charging = powerEvent == BatteryPowerEvent.PluggedIn;
            ShowBatteryNotify(new BatteryStatusInfo(percent, charging, charging ? BatteryChargeType.StandardAC : BatteryChargeType.Unknown,
                powerEvent, null, IsOnAcPower: charging), preview: true);
        }

        private void ShowBatteryNotify(BatteryStatusInfo info, bool preview = false)
        {
            BeginPowerIslandTransition(info, preview);
            if (_greetingShowing || GreetingActive)
            {
                CompleteGreetingLifetime();
                _layers.Hide(IslandLayerHost.GreetingKey);
                GreetingLayer.Opacity = 0;
                GreetingLayer.Visibility = Visibility.Collapsed;
            }

            // 更新电池图标与提示文字
            string statusDesc;
            Color accentColor;

            if (info.EventType == BatteryPowerEvent.PluggedIn)
            {
                if (info.SmartCharging == SmartChargingState.Active || info.ChargeType == BatteryChargeType.ConservationMode)
                {
                    statusDesc = info.SmartCharging == SmartChargingState.Active ? "已连接电源 · 智能充电" : $"已连接电源 · 电池保养中 ({info.Percent}%)";
                    accentColor = Color.FromRgb(0x00, 0xE5, 0xFF);
                    BatteryBoltIcon.Visibility = Visibility.Visible;
                }
                else
                {
                    statusDesc = info.ChargeType switch
                    {
                        BatteryChargeType.PD => "已连接电源 · USB-C PD",
                        BatteryChargeType.StandardAC => "已连接电源 · 普通 AC 适配器",
                        _ => "已连接电源 · 充电器类型未知"
                    };
                    accentColor = Color.FromRgb(0x00, 0xFF, 0x88);
                    BatteryBoltIcon.Visibility = Visibility.Visible;
                }
            }
            else if (info.EventType == BatteryPowerEvent.Unplugged)
            {
                statusDesc = "已拔出电源 · 电池供电中";
                accentColor = Color.FromRgb(0xFF, 0x91, 0x00);
                BatteryBoltIcon.Visibility = Visibility.Collapsed;
            }
            else if (info.EventType == BatteryPowerEvent.FullyCharged)
            {
                statusDesc = "电池已充满";
                accentColor = Color.FromRgb(0x00, 0xFF, 0x88);
                BatteryBoltIcon.Visibility = Visibility.Collapsed;
            }
            else if (info.EventType == BatteryPowerEvent.CriticalBattery)
            {
                statusDesc = "⚠ 电池电量严重不足";
                accentColor = Color.FromRgb(0xFF, 0x33, 0x33);
                BatteryBoltIcon.Visibility = Visibility.Collapsed;
            }
            else if (info.EventType == BatteryPowerEvent.LowBattery)
            {
                statusDesc = "电池电量不足";
                accentColor = Color.FromRgb(0xFF, 0x91, 0x00);
                BatteryBoltIcon.Visibility = Visibility.Collapsed;
            }
            else
            {
                if (info.ChargeType == BatteryChargeType.ConservationMode)
                {
                    statusDesc = $"已连接电源 · 电池保养中 ({info.Percent}%)";
                    accentColor = Color.FromRgb(0x00, 0xE5, 0xFF);
                    BatteryBoltIcon.Visibility = Visibility.Visible;
                }
                else
                {
                    statusDesc = info.IsCharging ? "正在充电" : "电池供电中";
                    accentColor = info.IsCharging ? Color.FromRgb(0x00, 0xFF, 0x88) : Color.FromRgb(0xE0, 0xE0, 0xE0);
                    BatteryBoltIcon.Visibility = info.IsCharging ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            if (!preview && (info.EventType is BatteryPowerEvent.LowBattery or BatteryPowerEvent.CriticalBattery) && BatteryContextDescription() is { Length: > 0 } context)
                statusDesc += " · " + context;
            BatteryStatusText.Text = preview ? statusDesc + " · 测试" : statusDesc;
            BatteryPercentText.Text = info.Percent >= 0 ? $"{info.Percent}%" : "未知";
            ActionIconMotion.Stop(BatteryIcon);
            BatteryIcon.Shape = IconShape.Battery;
            BatteryIcon.Active = info.EventType switch
            {
                BatteryPowerEvent.PluggedIn => true,
                BatteryPowerEvent.Unplugged => false,
                _ => info.IsCharging || info.IsOnAcPower == true
            };
            BatteryIcon.Level = info.Percent < 0 ? 0 : Math.Clamp(info.Percent / 100.0, 0, 1);

            if (info.IsCharging)
            {
                BatteryIcon.Text = "\uEA93";
            }
            else
            {
                if (info.Percent >= 90) BatteryIcon.Text = "\uE83F";
                else if (info.Percent >= 60) BatteryIcon.Text = "\uE855";
                else if (info.Percent >= 40) BatteryIcon.Text = "\uE853";
                else if (info.Percent >= 20) BatteryIcon.Text = "\uE852";
                else BatteryIcon.Text = "\uE850";
            }

            var brush = new SolidColorBrush(accentColor);
            BatteryIcon.Foreground = new SolidColorBrush(info.Percent is >= 0 and <= 10 ? Color.FromRgb(0xFF,0x5C,0x6A)
                : info.Percent is >= 0 and <= 20 ? Color.FromRgb(0xFF,0xAA,0x3B)
                : BatteryIcon.Active ? Color.FromRgb(0x32,0xD7,0x74) : Color.FromRgb(0x22,0xD3,0xEE));
            BatteryBoltIcon.Foreground = brush;
            BatteryIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x28, accentColor.R, accentColor.G, accentColor.B));
            BatteryIconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, accentColor.R, accentColor.G, accentColor.B));

            if (info.EventType is BatteryPowerEvent.PluggedIn or BatteryPowerEvent.Unplugged)
                BatteryIcon.PrepareBatteryConnectionTransition(info.EventType == BatteryPowerEvent.PluggedIn);
            else BatteryIcon.EndTransition();

            _powerTransitionAwaitingClaim = false;
            ShowPillNotification(IslandNotification.Seconds("battery", preview ? Math.Max(4, _options.BatteryNotifySeconds) : _options.BatteryNotifySeconds));

            if (_state != IslandState.Pill)
            {
                SetState(IslandState.Pill);
            }
            else
            {
                SyncBatteryNotifyContent();
            }
            TryMorphPillWidth();

            // Start after the island has opened, so the first part is not clipped by its collapsed geometry.
            ScheduleBatteryNotificationAnimation(info);
        }

        private void ScheduleBatteryNotificationAnimation(BatteryStatusInfo info)
        {
            _batteryAnimationTimer?.Stop();
            BatteryRipple.Stop();
            int waitingTicks = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(25) };
            _batteryAnimationTimer = timer;
            timer.Tick += (_, _) =>
            {
                if (_exiting || !BatteryNotifyActive || ++waitingTicks >= 80)
                {
                    timer.Stop(); _batteryAnimationTimer = null;
                    BatteryIcon.EndTransition();
                    return;
                }
                if (_animator?.IsRunning == true || BatteryNotifyLayer.Visibility != Visibility.Visible) return;
                if (_activeMainIslandKey == "battery" && Math.Abs(IslandBody.Width - CurrentPillWidth) > .5)
                { TryMorphPillWidth(); return; }
                timer.Stop(); _batteryAnimationTimer = null;
                // Measure after expansion so the glow starts behind the battery badge at any DPI.
                var rippleOrigin = BatteryIconContainer.TranslatePoint(
                    new Point(BatteryIconContainer.ActualWidth / 2, BatteryIconContainer.ActualHeight / 2), BatteryRipple);
                if (info.EventType == BatteryPowerEvent.PluggedIn || (info.EventType == BatteryPowerEvent.None && info.IsCharging))
                    BatteryRipple.Trigger(isCharging: true, origin: rippleOrigin);
                else if (info.EventType is BatteryPowerEvent.Unplugged or BatteryPowerEvent.LowBattery or BatteryPowerEvent.CriticalBattery)
                    BatteryRipple.Trigger(isCharging: false, origin: rippleOrigin);
                if (MotionPolicy.InstantMorph || MotionPolicy.PowerSave) { BatteryIcon.EndTransition(); return; }
                ActionIconMotion.Play(BatteryIcon, info.EventType switch
                {
                    BatteryPowerEvent.PluggedIn => ActionIconAnimation.Connect,
                    BatteryPowerEvent.Unplugged => ActionIconAnimation.Disconnect,
                    _ => ActionIconAnimation.Pulse
                });
            };
            timer.Start();
        }

        private void SyncBatteryNotifyContent()
        {
            if (!BatteryNotifyActive) _layers.Hide("battery");
            SyncNotificationLayers();
        }

    }
}
