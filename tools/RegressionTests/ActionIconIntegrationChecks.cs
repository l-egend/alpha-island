using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void ActionIconIntegrationChecks(alpha_island.MainWindow window, Grid root)
    {
        bool reduced = MotionPolicy.ReduceMotion, saved = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        var state = AgentField<IslandState>(window, "_state");
        var pages = AgentField<IslandPageHost>(window, "_pages");
        string previousPage = pages.ActiveId;
        var options = AgentField<IslandOptions>(window, "_options");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        bool agentEnabled = SettingsRegistry.GetBool(options, "AgentStatusEnabled", true);
        const string owner = "regression-icon-integration";
        var probes = new List<Border>();
        var pill = AgentField<DesktopNotificationPill>(window, "_notificationSummary");
        var message = new DesktopNotification("icon-glyph-probe", "IconMotion.Test", "图标检查", "动作通知", "独立测试内容", DateTimeOffset.Now);
        var originalPillTransform = pill.IconContent.RenderTransform;
        var originalIconTransform = pill.Icon.RenderTransform;
        var originalAgentTransform = window.AgentNotifyGlyph.RenderTransform;
        var originalBluetoothTransform = window.BtIconContainer.RenderTransform;
        var originalDeviceTransform = window.BtDeviceIcon.RenderTransform;
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            layers.HideAll(); PrivateCall(window, "ClearDesktopInbox");
            PrivateCall(window, "SetState", IslandState.Pill, true);
            PrivateCall(window, "OnSystemToastsReceived", new[] { message }, false);
            PumpAnimations(85); root.UpdateLayout();
            Check(pill.Icon.IsVisible && !pill.IconImage.IsVisible && ActionIconMotion.GetIsAnimating(pill.IconContent)
                && pill.Icon.HasAnimatedProperties && pill.Icon.TransitionProgress < 1
                && ReferenceEquals(originalPillTransform, pill.IconContent.RenderTransform)
                && ReferenceEquals(originalIconTransform, pill.Icon.RenderTransform),
                "真实通知提示只在图标内部播放短描画，图标槽和图标原变换保持不动");
            double glyphProgress = pill.Icon.TransitionProgress;
            pill.SetNotification(message with { Created = message.Created.AddSeconds(1) }); PumpAnimations(20);
            Check(pill.Icon.TransitionProgress >= glyphProgress && ReferenceEquals(originalPillTransform, pill.IconContent.RenderTransform),
                "重复通知同步和时间戳变化不重启图标动画");
            PumpAnimations(1450); pill.SetNotification(message); PumpAnimations(20);
            Check(!ActionIconMotion.GetIsAnimating(pill.IconContent) && !pill.Icon.HasAnimatedProperties
                && pill.Icon.TransitionProgress == 1, "同一通知短动画结束后继续同步保持静态");

            var logoMessage = message with { Key = "icon-logo-probe", Logo = IconMotionTestLogo() };
            pill.SetNotification(logoMessage); PumpAnimations(70); root.UpdateLayout();
            Check(!pill.IconImage.IsVisible && pill.Icon.IsVisible && ActionIconMotion.GetIsAnimating(pill.IconContent)
                && ReferenceEquals(pill.Icon.Source, pill.IconImage.Source) && pill.Icon.Source != null && pill.Icon.HasAnimatedProperties
                && ReferenceEquals(VisualTreeHelper.GetParent(pill.Icon), pill.IconContent)
                && ReferenceEquals(VisualTreeHelper.GetParent(pill.IconImage), pill.IconContent)
                && !ActionIconMotion.GetIsAnimating(pill.IconImage) && ReferenceEquals(originalIconTransform, pill.Icon.RenderTransform),
                "Windows 应用图片由语义图标槽绘制，数据镜像隐藏且没有第二套图片时钟");
            double imageProgress = pill.Icon.TransitionProgress;
            pill.SetNotification(logoMessage); PumpAnimations(20);
            Check(pill.Icon.TransitionProgress >= imageProgress && ReferenceEquals(originalPillTransform, pill.IconContent.RenderTransform),
                "同一应用图片通知重复同步不重启内部描画");

            var noticeOrb = (Border)PrivateCall(window, "CreateSatelliteOrb", "desktoptoast")!;
            probes.Add(noticeOrb); noticeOrb.Opacity = 1; noticeOrb.RenderTransform = Transform.Identity;
            noticeOrb.Visibility = Visibility.Visible; root.Children.Add(noticeOrb); root.UpdateLayout();
            PrivateCall(window, "UpdateSatelliteOrbVisual", "desktoptoast", noticeOrb); PumpAnimations(45);
            var logo = ((Grid)noticeOrb.Child).Children.OfType<System.Windows.Controls.Image>().Single(i => i.Name == "OrbAppLogo");
            var noticeIcon = ((Grid)noticeOrb.Child).Children.OfType<SemanticIcon>().Single(i => i.Name == "OrbGlyph");
            var originalNoticeTransform = noticeIcon.RenderTransform;
            Check(!logo.IsVisible && noticeIcon.IsVisible && ReferenceEquals(noticeIcon.Source, logo.Source)
                && logo.Source != null && ActionIconMotion.GetIsAnimating(noticeIcon) && noticeIcon.HasAnimatedProperties,
                "通知卫星首次显示真实应用图片时，仅在可见语义图标内部播放有限动画");
            PumpAnimations(1450); PrivateCall(window, "UpdateSatelliteOrbVisual", "desktoptoast", noticeOrb);
            Check(!ActionIconMotion.GetIsAnimating(noticeIcon) && !noticeIcon.HasAnimatedProperties,
                "持续可见的同一通知卫星不在同步时重新播放");
            PrivateCall(window, "ClearDesktopInbox");
            PrivateCall(window, "OnSystemToastsReceived", new[] { logoMessage with { Key = "icon-next-logo" } }, false);
            PrivateCall(window, "UpdateSatelliteOrbVisual", "desktoptoast", noticeOrb); PumpAnimations(45);
            Check(noticeIcon.IsVisible && ActionIconMotion.GetIsAnimating(noticeIcon) && noticeIcon.HasAnimatedProperties,
                "通知卫星图片持续可见时新的真实通知仍触发一次内部动画");
            PumpAnimations(220); double previousNoticeProgress = noticeIcon.TransitionProgress;
            PrivateCall(window, "ClearDesktopInbox");
            PrivateCall(window, "OnSystemToastsReceived", new[] { logoMessage with { Key = "icon-rapid-logo" } }, false);
            PrivateCall(window, "UpdateSatelliteOrbVisual", "desktoptoast", noticeOrb); PumpAnimations(30);
            Check(ActionIconMotion.GetIsAnimating(noticeIcon) && noticeIcon.TransitionProgress < previousNoticeProgress
                && ReferenceEquals(originalNoticeTransform, noticeIcon.RenderTransform),
                "通知连续到达时新事件替换内部进度时钟，图标原变换仍然保持不动");
            pill.Visibility = Visibility.Collapsed;
            pill.SetNotification(message with { Key = "icon-hidden-probe" });
            Check(!ActionIconMotion.GetIsAnimating(pill.IconContent) && !pill.Icon.HasAnimatedProperties,
                "通知暂未显示时不创建隐藏动画时钟");
            pill.Visibility = Visibility.Visible; root.UpdateLayout(); PumpAnimations(60);
            Check(ActionIconMotion.GetIsAnimating(pill.IconContent), "隐藏期间收到的通知在首次显示时仍有一次动作提示");
            PrivateCall(window, "ClearDesktopInbox"); layers.HideAll();

            SettingsRegistry.Write(options, "AgentStatusEnabled", true);
            SetAgentField(window, "_lastAgentUiState", AgentRunState.Idle);
            AgentStatus.Set(AgentRunState.Thinking, "测试思考", owner);
            PrivateCall(window, "UpdateAgentStatusUi"); PumpAnimations(80); root.UpdateLayout();
            Check(AgentStatus.State == AgentRunState.Thinking && window.AgentNotifyGlyph.IsVisible
                && ActionIconMotion.GetKind(window.AgentNotifyGlyph) == ActionIconAnimation.Spin
                && ActionIconMotion.GetIsAnimating(window.AgentNotifyGlyph) && window.AgentNotifyGlyph.HasAnimatedProperties
                && ReferenceEquals(originalAgentTransform, window.AgentNotifyGlyph.RenderTransform),
                "真实 Agent 状态迁移触发思考图标内部动画，保留原始变换");
            double agentProgress = window.AgentNotifyGlyph.TransitionProgress;
            PrivateCall(window, "UpdateAgentStatusUi"); AgentStatus.Set(AgentRunState.Thinking, "仅更新说明", owner);
            PrivateCall(window, "UpdateAgentStatusUi"); PumpAnimations(35);
            Check(window.AgentNotifyGlyph.TransitionProgress >= agentProgress
                && ReferenceEquals(originalAgentTransform, window.AgentNotifyGlyph.RenderTransform),
                "Agent 已用时长刷新与同状态说明改变不重启短动画");
            PumpAnimations(1650); PrivateCall(window, "UpdateAgentStatusUi");
            Check(!ActionIconMotion.GetIsAnimating(window.AgentNotifyGlyph) && !window.AgentNotifyGlyph.HasAnimatedProperties,
                "Agent 每秒计时在短动画结束后保持静态");
            window.AgentNotifyGlyph.Visibility = Visibility.Collapsed;
            AgentStatus.Set(AgentRunState.ToolWait, "测试等待", owner); PrivateCall(window, "UpdateAgentStatusUi");
            Check(!ActionIconMotion.GetIsAnimating(window.AgentNotifyGlyph), "隐藏的 Agent 状态图标不创建时钟");
            window.AgentNotifyGlyph.Visibility = Visibility.Visible; PumpAnimations(50);
            Check(ActionIconMotion.GetIsAnimating(window.AgentNotifyGlyph)
                && ActionIconMotion.GetKind(window.AgentNotifyGlyph) == ActionIconAnimation.Tick,
                "隐藏期间的新 Agent 状态在再次显示时播放当前动作");
            AgentStatus.Reset(owner: owner); PrivateCall(window, "UpdateAgentStatusUi"); layers.HideAll();

            var earClip = new BluetoothDeviceInfo("icon-audio-earclip", "HUAWEI FreeClip", BluetoothDeviceCategory.Audio, 73, true);
            PrivateCall(window, "ShowBluetoothNotify", earClip, true); PumpAnimations(60); root.UpdateLayout();
            Check(!window.BtDeviceArt.IsVisible && window.BtDeviceIcon.IsVisible && window.BtDeviceIcon.Source != null
                && ReferenceEquals(window.BtDeviceIcon.Source, window.BtDeviceArt.Source) && window.BtDeviceIcon.Active
                && ActionIconMotion.GetKind(window.BtIconContainer) == ActionIconAnimation.Connect
                && ActionIconMotion.GetIsAnimating(window.BtIconContainer) && window.BtDeviceIcon.HasAnimatedProperties,
                "真实耳夹设备由语义图标绘制二维图片与内部连接描画");
            double connectionProgress = window.BtDeviceIcon.TransitionProgress;
            PrivateCall(window, "OnBluetoothBatteryUpdated", null, earClip with { BatteryPercent = 61 }); PumpAnimations(20);
            Check(window.BtDeviceIcon.TransitionProgress >= connectionProgress
                && ReferenceEquals(originalBluetoothTransform, window.BtIconContainer.RenderTransform)
                && ReferenceEquals(originalDeviceTransform, window.BtDeviceIcon.RenderTransform), "单纯耳机电量刷新不重启内部连接动画");
            PrivateCall(window, "ShowBluetoothNotify", earClip with { IsConnected = false }, false); PumpAnimations(50);
            Check(ActionIconMotion.GetKind(window.BtIconContainer) == ActionIconAnimation.Disconnect
                && ActionIconMotion.GetIsAnimating(window.BtIconContainer)
                && !window.BtDeviceIcon.Active && window.BtDeviceIcon.HasAnimatedProperties
                && ReferenceEquals(originalBluetoothTransform, window.BtIconContainer.RenderTransform),
                "断联立即切换内部断开目标，容器原变换保持不动");
            PumpAnimations(1450);
            Check(!ActionIconMotion.GetIsAnimating(window.BtIconContainer) && !window.BtDeviceIcon.HasAnimatedProperties,
                "断联动画有限结束，不持续占用渲染时钟");
            var keyboard = new BluetoothDeviceInfo("icon-keyboard-probe", "测试键盘", BluetoothDeviceCategory.Keyboard, 45, true);
            PrivateCall(window, "ShowBluetoothNotify", keyboard, true); PumpAnimations(60);
            Check(window.BtDeviceIcon.IsVisible && !window.BtDeviceArt.IsVisible
                && ActionIconMotion.GetKind(window.BtIconContainer) == ActionIconAnimation.Connect
                && ActionIconMotion.GetIsAnimating(window.BtIconContainer), "切换到键盘类型后字形仍使用相同短动画槽");

            var bluetoothOrb = (Border)PrivateCall(window, "CreateSatelliteOrb", "bluetooth")!;
            probes.Add(bluetoothOrb); bluetoothOrb.Opacity = 1; bluetoothOrb.RenderTransform = Transform.Identity;
            bluetoothOrb.Visibility = Visibility.Visible; root.Children.Add(bluetoothOrb); root.UpdateLayout();
            PrivateCall(window, "UpdateSatelliteOrbVisual", "bluetooth", bluetoothOrb); PumpAnimations(45);
            var keyboardGlyph = ((Grid)bluetoothOrb.Child).Children.OfType<SemanticIcon>().Single(t => t.Name == "OrbGlyph");
            Check(keyboardGlyph.IsVisible && ActionIconMotion.GetKind(keyboardGlyph) == ActionIconAnimation.Connect,
                "键盘卫星使用字形并同步当前连接动画类别");
            var originalKeyboardTransform = keyboardGlyph.RenderTransform;
            PumpAnimations(220); double previousDeviceProgress = keyboardGlyph.TransitionProgress;
            keyboard = keyboard with { Id = "icon-second-keyboard", Name = "第二个测试键盘" };
            PrivateCall(window, "ShowBluetoothNotify", keyboard, true);
            PrivateCall(window, "UpdateSatelliteOrbVisual", "bluetooth", bluetoothOrb); PumpAnimations(30);
            Check(ActionIconMotion.GetIsAnimating(keyboardGlyph) && keyboardGlyph.HasAnimatedProperties
                && keyboardGlyph.TransitionProgress < previousDeviceProgress
                && ReferenceEquals(originalKeyboardTransform, keyboardGlyph.RenderTransform),
                "同类别蓝牙设备连续连接时新事件替换内部进度钟，图标自身不移动");
            PrivateCall(window, "ShowBluetoothNotify", keyboard with { IsConnected = false }, false);
            PrivateCall(window, "UpdateSatelliteOrbVisual", "bluetooth", bluetoothOrb); PumpAnimations(45);
            Check(ActionIconMotion.GetKind(keyboardGlyph) == ActionIconAnimation.Disconnect
                && ActionIconMotion.GetIsAnimating(keyboardGlyph) && !keyboardGlyph.Active && keyboardGlyph.HasAnimatedProperties,
                "非音频设备卫星断联也切换内部断开目标");
            PumpAnimations(1450); PrivateCall(window, "UpdateSatelliteOrbVisual", "bluetooth", bluetoothOrb);
            Check(!ActionIconMotion.GetIsAnimating(keyboardGlyph) && !keyboardGlyph.HasAnimatedProperties,
                "同一断联卫星反复刷新不启动新时钟");
            layers.Hide("bluetooth"); PrivateCall(window, "SyncNotificationLayers"); PumpAnimations(350);
            Check(!ActionIconMotion.GetIsAnimating(window.BtIconContainer), "蓝牙提示隐藏时立即清除其图标时钟");

            PrivateCall(window, "EnsureCardPinLayer", "focus");
            PrivateCall(window, "ShowPillNotification", IslandNotification.Seconds("card/focus", 3));
            PrivateCall(window, "SwitchMainIsland", "card/focus"); PumpAnimations(65); root.UpdateLayout();
            var pinViews = AgentField<System.Collections.IDictionary>(window, "_cardPinViews");
            var focus = pinViews["card/focus"]!;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var focusIcon = (SemanticIcon)focus.GetType().GetProperty("Icon", flags)!.GetValue(focus)!;
            var focusDetail = (TextBlock)focus.GetType().GetProperty("Detail", flags)!.GetValue(focus)!;
            var noticeDetailField = focus.GetType().GetField("NoticeDetail", flags)!;
            var noticeUntilField = focus.GetType().GetField("NoticeUntilUtc", flags)!;
            object previousNotice = noticeDetailField.GetValue(focus)!, previousUntil = noticeUntilField.GetValue(focus)!;
            try
            {
                noticeDetailField.SetValue(focus, "临时阶段提醒"); noticeUntilField.SetValue(focus, DateTime.UtcNow.AddSeconds(5));
                PrivateCall(window, "UpdateCardPinSummary", "focus"); ActionIconMotion.Stop(focusIcon);
                PrivateCall(window, "UpdateCardPinSummary", "focus"); PumpAnimations(40);
                Check(focusDetail.Text == "临时阶段提醒" && !ActionIconMotion.GetIsAnimating(focusIcon),
                    "番茄钟临时提醒文案覆盖正常阶段时，每秒刷新不会误判阶段变化重播");
                PrivateCall(window, "UpdateCardPinSummary", "focus");
                Check(!ActionIconMotion.GetIsAnimating(focusIcon), "番茄钟持续计时与相同阶段同步保持静态图标");
            }
            finally
            {
                noticeDetailField.SetValue(focus, previousNotice); noticeUntilField.SetValue(focus, previousUntil);
                PrivateCall(window, "UpdateCardPinSummary", "focus"); layers.Hide("card/focus");
            }
        }
        finally
        {
            AgentStatus.Reset(owner: owner);
            foreach (var probe in probes) root.Children.Remove(probe);
            SettingsRegistry.Write(options, "AgentStatusEnabled", agentEnabled);
            PrivateCall(window, "UpdateAgentStatusUi");
            pill.Visibility = Visibility.Visible; window.AgentNotifyGlyph.Visibility = Visibility.Visible;
            PrivateCall(window, "ClearDesktopInbox"); layers.Hide("bluetooth"); layers.Hide("agentstatus");
            PrivateCall(window, "SyncNotificationLayers");
            PrivateCall(window, "SetState", state, true, previousPage);
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduced, saved);
        }
    }

    private static byte[] IconMotionTestLogo()
    {
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 250, 200, 0, 255, 0, 180, 255, 255, 0, 180, 255, 255, 250, 200, 0, 255 }, 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
}
