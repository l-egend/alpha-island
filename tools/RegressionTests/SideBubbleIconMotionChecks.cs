using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void SideBubbleIconMotionUiChecks(alpha_island.MainWindow window, Grid root)
    {
        bool reduced = MotionPolicy.ReduceMotion, saved = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        var previousState = AgentField<IslandState>(window, "_state");
        var pages = AgentField<IslandPageHost>(window, "_pages");
        string previousPage = pages.ActiveId;
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var notificationView = AgentField<DesktopNotificationPill>(window, "_notificationSummary");
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            layers.HideAll(); PrivateCall(window, "ClearDesktopInbox");
            PrivateCall(window, "HideSideBubbleHint", true);
            // Use a different page from the volume card to exercise the side hint.
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.Clipboard);
            window.CardVolumePercentText.Text = "42%";
            var volume = IslandNotification.Seconds("volume", 15) with { Title = "音量提示", Detail = "42%" };
            var iconSlot = (FrameworkElement)VisualTreeHelper.GetParent(window.SideBubbleIcon);
            bool observedMotion = false;
            System.Windows.Threading.DispatcherHookEventHandler observe = (_, _) => observedMotion |= window.SideBubbleIcon.IsVisible
                && ActionIconMotion.GetIsAnimating(iconSlot) && window.SideBubbleIcon.TransitionProgress < 1;
            window.Dispatcher.Hooks.OperationCompleted += observe;
            try
            {
                PrivateCall(window, "ShowPillNotification", volume);
                PumpAnimations(140); root.UpdateLayout();
            }
            finally { window.Dispatcher.Hooks.OperationCompleted -= observe; }
            Check(window.SideBubble.IsVisible && window.SideBubbleIcon.IsVisible && observedMotion,
                "真实卡片态旁侧音量提示在入场期间出现快速声波过渡");
            Check(iconSlot is Grid && ReferenceEquals(VisualTreeHelper.GetParent(window.SideBubbleArtwork), iconSlot),
                "旁侧提示的应用图片与备用字形共用独立图标槽，不缩放提示文字");

            var initialMotion = iconSlot.RenderTransform;
            PrivateCall(window, "ShowPillNotification", volume with { Detail = "43%" });
            PrivateCall(window, "UpdateSideBubbleHint"); PumpAnimations(50);
            Check(ReferenceEquals(initialMotion, iconSlot.RenderTransform),
                "旁侧提示的同一事件内容刷新不重启图标动画");
            PumpAnimations(1950); PrivateCall(window, "UpdateSideBubbleHint"); PumpAnimations(30);
            Check(!ActionIconMotion.GetIsAnimating(iconSlot),
                "旁侧提示短动画结束后反复刷新不重新创建图标时钟");

            window.VolumeIcon.Active = true; window.VolumeIcon.Level = .25;
            string sameTitle = window.SideBubbleText.Text;
            PrivateCall(window, "ShowSideBubbleHint", "\uE767", sameTitle, "volume");
            ActionIconMotion.Stop(iconSlot); root.UpdateLayout(); PumpAnimations(20);
            window.VolumeIcon.Level = .85;
            PrivateCall(window, "ShowSideBubbleHint", "\uE767", sameTitle, "volume"); PumpAnimations(50);
            Check(ActionIconMotion.GetIsAnimating(iconSlot),
                "旁侧音量提示内容相同但音量值变化时也立即过渡");
            PumpAnimations(250);

            PrivateCall(window, "ShowPillNotification", volume with { Title = "新的音量提醒" }); PumpAnimations(75);
            Check(ActionIconMotion.GetIsAnimating(iconSlot), "可见的旁侧提示收到真实新提示时重新播放一次");
            PrivateCall(window, "HideSideBubbleHint", true);
            Check(!ActionIconMotion.GetIsAnimating(iconSlot), "旁侧提示立即隐藏时清理图标动画时钟");

            layers.HideAll(); PrivateCall(window, "ClearDesktopInbox");
            PrivateCall(window, "SetState", IslandState.Pill, true);
            var message = new DesktopNotification("side-logo-probe", "SideMotion.Test", "图标检查", "应用提醒", "旁侧图片检查", DateTimeOffset.Now)
                { Logo = IconMotionTestLogo() };
            PrivateCall(window, "OnSystemToastsReceived", new[] { message }, false);
            PumpAnimations(70);
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.System);
            PrivateCall(window, "UpdateSideBubbleHint"); PumpAnimations(400); root.UpdateLayout();
            Check(!window.SideBubbleArtwork.IsVisible && window.SideBubbleIcon.IsVisible
                && ReferenceEquals(window.SideBubbleArtwork.Source, notificationView.IconImage.Source)
                && ReferenceEquals(window.SideBubbleIcon.Source, window.SideBubbleArtwork.Source),
                "真实应用通知转为卡片旁侧提示时沿用通知图标图片");
            Check(ActionIconMotion.GetIsAnimating(iconSlot)
                && !ActionIconMotion.GetIsAnimating(window.SideBubbleArtwork),
                "旁侧应用图标通过公共图标槽播放，不叠加图片自己的动画时钟");
            var appMotion = iconSlot.RenderTransform;
            PrivateCall(window, "UpdateSideBubbleHint"); PumpAnimations(40);
            Check(ReferenceEquals(appMotion, iconSlot.RenderTransform), "相同应用旁侧通知同步不重启图片动画");
            PrivateCall(window, "HideSideBubbleHint", false);
            Check(!ActionIconMotion.GetIsAnimating(iconSlot), "旁侧提示退场开始时立即清理图标时钟");
            PumpAnimations(250);
            Check(window.SideBubble.Visibility == Visibility.Collapsed && !ActionIconMotion.GetIsAnimating(iconSlot),
                "旁侧提示退场完成后保持隐藏和静态，无残留图标时钟");
        }
        finally
        {
            PrivateCall(window, "HideSideBubbleHint", true);
            PrivateCall(window, "ClearDesktopInbox"); layers.HideAll();
            PrivateCall(window, "SetState", previousState, true, previousPage);
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduced, saved);
        }
    }
}
