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

// Battery status notifications.
// Moved verbatim out of the former single-file MainWindow.xaml.cs; no logic changes.
namespace alpha_island
{
    public partial class MainWindow
    {
        // ================= 电池状态通知 =================

        private void OnBatteryStatusChanged(object? sender, BatteryStatusInfo info)
        {
            if (!_exiting) RecordBatteryDesktopEvent(info);
            if (_exiting || !_options.BatteryNotifyEnabled || !info.HasBattery || info.EventType == BatteryPowerEvent.None) return;
            if (_sessionLocked || (SettingsRegistry.GetBool(_options, "NotificationQuietMode", false)
                && (info.EventType is not (BatteryPowerEvent.LowBattery or BatteryPowerEvent.CriticalBattery)
                    || !SettingsRegistry.GetBool(_options, "NotificationQuietAllowPriority", true)))) return;
            ShowBatteryNotify(info);
        }

        private void ShowBatteryNotify(BatteryStatusInfo info)
        {
            if (_greetingShowing || GreetingActive)
            {
                _greetingTimer?.Stop();
                _layers.Hide(IslandLayerHost.GreetingKey);
                GreetingLayer.Opacity = 0;
                GreetingLayer.Visibility = Visibility.Collapsed;
            }

            ShowPillNotification(IslandNotification.Seconds("battery", _options.BatteryNotifySeconds));

            // 更新波纹特效
            if (_options.BatteryRippleEnabled)
            {
                if (info.EventType == BatteryPowerEvent.PluggedIn || (info.EventType == BatteryPowerEvent.None && info.IsCharging))
                {
                    BatteryRipple.Trigger(isCharging: true);
                }
                else if (info.EventType == BatteryPowerEvent.Unplugged || info.EventType == BatteryPowerEvent.LowBattery || info.EventType == BatteryPowerEvent.CriticalBattery)
                {
                    BatteryRipple.Trigger(isCharging: false);
                }
            }

            // 更新电池图标与提示文字
            string statusDesc;
            Color accentColor;

            if (info.EventType == BatteryPowerEvent.PluggedIn)
            {
                if (info.ChargeType == BatteryChargeType.ConservationMode)
                {
                    statusDesc = $"已连接电源 · 电池保养中 ({info.Percent}%)";
                    accentColor = Color.FromRgb(0x00, 0xE5, 0xFF);
                    BatteryBoltIcon.Visibility = Visibility.Visible;
                }
                else
                {
                    statusDesc = info.ChargeType switch
                    {
                        BatteryChargeType.PD => "已连接电源 · PD 快充",
                        BatteryChargeType.StandardAC => "已连接电源 · 电源适配器",
                        _ => "已连接电源"
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

            BatteryStatusText.Text = statusDesc;
            BatteryPercentText.Text = info.Percent >= 0 ? $"{info.Percent}%" : "未知";

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
            BatteryIcon.Foreground = brush;
            BatteryBoltIcon.Foreground = brush;
            BatteryIconContainer.Background = new SolidColorBrush(Color.FromArgb(0x28, accentColor.R, accentColor.G, accentColor.B));
            BatteryIconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, accentColor.R, accentColor.G, accentColor.B));

            if (_state != IslandState.Pill)
            {
                SetState(IslandState.Pill);
            }
            else
            {
                SyncBatteryNotifyContent();
            }
        }

        private void SyncBatteryNotifyContent()
        {
            if (!BatteryNotifyActive) _layers.Hide("battery");
            SyncNotificationLayers();
        }

    }
}
