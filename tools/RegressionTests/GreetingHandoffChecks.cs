using System.Collections;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void GreetingHandoffChecks(alpha_island.MainWindow window, Grid root)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var failures = new List<Exception>();
        using var media = new SmtcService(window.Dispatcher);
        SetAgentField(window, "_smtc", media);
        MotionPolicy.Apply(true, false);
        options.GreetingEnabled = true; options.GreetingSeconds = 10;
        SettingsRegistry.Write(options, "MediaWindowHandoffEnabled", false);
        void Reset()
        {
            PrivateCall(window, "DismissGreeting");
            PrivateCall(window, "ClearDesktopInbox");
            layers.HideAll();
            SetAgentField(window, "_isPinned", false);
            SetAgentField(window, "_musicIslandDismissed", false);
            SetAgentField(window, "_startupMediaPromptUntil", DateTime.MinValue);
            media.SetDebugNowPlaying(new NowPlayingInfo { State = PlaybackState.Closed });
            PrivateCall(window, "ApplyNoMedia");
            PrivateCall(window, "SetState", IslandState.Bar, true);
            options.GreetingSeconds = 10;
        }
        void Case(string name, Action body)
        {
            try { Reset(); body(); }
            catch (Exception error) { failures.Add(error); Console.WriteLine("FAIL CASE: " + name + " / " + error.Message); }
        }
        void AssertContent(string scenario)
        {
            root.UpdateLayout();
            var state = AgentField<IslandState>(window, "_state");
            bool content = window.PillLayer.IsVisible && window.PillLayer.Opacity > .01;
            foreach (DictionaryEntry entry in AgentField<IDictionary>(layers, "_views"))
            {
                var visual = AgentField<UIElement>(entry.Value!, "Root");
                content |= visual.IsVisible && visual.Opacity > .01;
            }
            Console.WriteLine($"Presentation: {scenario}; State={state}; Content={content}; GreetingShowing={layers.IsShowing("greeting")}; GreetingActive={layers.IsActive("greeting")}");
            Check(state != IslandState.Pill || content, scenario + "后保留有效内容或收回细条，不留下空白胶囊");
        }
        Case("natural expiry", () =>
        {
            options.GreetingSeconds = 1;
            PrivateCall(window, "ShowStartupGreeting");
            PumpAnimations(1250); AssertContent("欢迎计时自然结束");
        });
        Case("hot zone retention", () =>
        {
            PrivateCall(window, "ShowStartupGreeting");
            var menu = new ContextMenu { PlacementTarget = window.MediaSourcesButton, StaysOpen = true };
            menu.Items.Add(new MenuItem { Header = "isolated regression" });
            window.MediaSourcesButton.ContextMenu = menu;
            try
            {
                menu.IsOpen = true;
                Check((bool)PrivateCall(window, "IsCursorInHotZone")!, "使用真实菜单进入与鼠标停留相同的热区保留分支");
                PrivateCall(window, "DismissGreeting"); PumpAnimations(400); AssertContent("欢迎结束时热区保留");
            }
            finally { menu.IsOpen = false; window.MediaSourcesButton.ContextMenu = null; }
        });
        Case("already retired layer", () =>
        {
            PrivateCall(window, "ShowStartupGreeting");
            layers.ShowUntil("greeting", DateTime.UtcNow.AddSeconds(-1));
            PrivateCall(window, "SyncGreetingContent");
            PrivateCall(window, "DismissGreeting"); PumpAnimations(400); AssertContent("欢迎图层被其他入口提前撤下");
        });
        Case("hover after greeting", () =>
        {
            PrivateCall(window, "ShowStartupGreeting");
            PrivateCall(window, "DismissGreeting");
            PrivateCall(window, "ExpandIslandFromBar");
            AssertContent("无媒体时重新移入启动岛");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Card,
                "没有媒体摘要时直接打开可操作卡片，不展开空白播放器");
        });
        Case("early collapse unblocks services", () =>
        {
            var completed = new TaskCompletionSource<bool>();
            SetAgentField(window, "_greetingFinishedTcs", completed);
            PrivateCall(window, "ShowStartupGreeting");
            PrivateCall(window, "CollapseToBar", true);
            Check(completed.Task.IsCompletedSuccessfully,
                "手动提前收起欢迎后立即解除服务初始化等待，不等欢迎超时");
        });
        Case("interrupted by notification", () =>
        {
            PrivateCall(window, "ShowStartupGreeting");
            var notice = new DesktopNotification("greeting-handoff", "fixture", "测试", "启动通知", "测试内容", DateTimeOffset.Now);
            PrivateCall(window, "OnSystemToastsReceived", new[] { notice }, false);
            PrivateCall(window, "DismissGreeting"); PumpAnimations(400); AssertContent("欢迎交接到有效通知");
            Check(AgentField<DesktopNotification?>(window, "_desktopCurrent")?.Key == notice.Key,
                "欢迎结束保留新到的通知而不清空");
        });
        Case("pending playback", () =>
        {
            PrivateCall(window, "ShowStartupGreeting");
            var track = new NowPlayingInfo { Title = "启动交接测试", SourceApp = "fixture.exe", State = PlaybackState.Playing };
            media.SetDebugNowPlaying(track); PrivateCall(window, "ApplyMedia", track);
            PrivateCall(window, "DismissGreeting"); PumpAnimations(400); AssertContent("欢迎交接到播放提示");
        });
        Case("pinned notification", () =>
        {
            PrivateCall(window, "SetLayerPinned", "netspeed", true);
            PrivateCall(window, "ShowStartupGreeting");
            PrivateCall(window, "DismissGreeting"); PumpAnimations(400);
            AssertContent("欢迎交接到已置顶网速");
            Check(layers.IsPinned("netspeed") && layers.IsVisible("netspeed"),
                "欢迎结束保留已置顶通知及其主岛内容");
        });
        Case("repeated completion", () =>
        {
            PrivateCall(window, "ShowStartupGreeting"); PrivateCall(window, "DismissGreeting");
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.System);
            PrivateCall(window, "DismissGreeting");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Card,
                "重复到达的欢迎结束回调不能收起用户刚打开的卡片");
            Check(AgentField<System.Windows.Threading.DispatcherTimer?>(window, "_greetingTimer") == null,
                "欢迎完成后释放计时器引用，不保留旧定时回调");
        });
        Case("normal animation", () =>
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            try
            {
                PrivateCall(window, "ShowStartupGreeting"); PumpAnimations(650);
                PrivateCall(window, "DismissGreeting"); PumpAnimations(650);
                AssertContent("正常展开与收起动效完成");
                Check(AgentField<IslandState>(window, "_state") == IslandState.Bar,
                    "保留正常弹簧动画且最终归位细条");
            }
            finally { MotionPolicy.Apply(true, false); }
        });
        Case("reset unblocks services", () =>
        {
            var completed = new TaskCompletionSource<bool>();
            SetAgentField(window, "_greetingFinishedTcs", completed);
            PrivateCall(window, "ShowStartupGreeting");
            PrivateCall(window, "DismissActiveNotifications");
            Check(completed.Task.IsCompletedSuccessfully,
                "重置提前结束欢迎时也解除服务初始化等待");
        });
        Case("charging interruption", () =>
        {
            var completed = new TaskCompletionSource<bool>();
            SetAgentField(window, "_greetingFinishedTcs", completed);
            PrivateCall(window, "ShowStartupGreeting");
            PrivateCall(window, "ShowBatteryNotify", new BatteryStatusInfo(68, true,
                BatteryChargeType.StandardAC, BatteryPowerEvent.PluggedIn, null, IsOnAcPower: true), false);
            PumpAnimations(400);
            Check(completed.Task.IsCompletedSuccessfully && !layers.IsActive("greeting"),
                "电源提醒打断欢迎后及时完成启动交接");
            AssertContent("欢迎交接到充电提示");
        });
        Reset();
        if (failures.Count > 0) throw new AggregateException(failures);
    }
}
