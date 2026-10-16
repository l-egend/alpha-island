using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void AgentMultiSessionChecks(alpha_island.MainWindow window, Grid root)
    {
        using var codexWindow = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Codex multi-session fixture")
        { Width = 640, Height = 420, PositionX = -31000, PositionY = -31800,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        using var otherWindow = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Antigravity multi-session fixture")
        { Width = 640, Height = 420, PositionX = -30000, PositionY = -30800,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        var fixture = new AgentWindowFixture { Windows = [new(codexWindow.Handle, "Codex", "Fixture", false),
            new(otherWindow.Handle, "Antigravity", "Fixture", false)] };
        var navigation = AgentField<AgentAppNavigationService>(window, "_agentNavigation");
        var options = AgentField<IslandOptions>(window, "_options");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var registry = new ExternalAgentRegistry();
        var owners = new[] { "external:codex/session-a", "external:codex/session-b", "external:antigravity/session-c" };
        void Report(int index, string state)
        {
            string source = index == 2 ? "antigravity" : "codex", session = index == 2 ? "session-c" : index == 1 ? "session-b" : "session-a";
            if (!registry.Update(source, session, state, "Fixture " + index, 300, DateTime.UtcNow, out var error, Environment.ProcessId))
                throw new InvalidOperationException(error);
            PrivateCall(window, "UpdateAgentStatusUi"); PumpAnimations(70); root.UpdateLayout();
        }
        try
        {
            SettingsRegistry.Write(options, "AgentStatusEnabled", true);
            SettingsRegistry.Write(options, "MediaWindowHandoffEnabled", true);
            SetAgentField(window, "_agentNavigation", new AgentAppNavigationService(fixture));
            Report(0, "thinking"); Report(1, "thinking"); Report(2, "thinking");
            Check(layers.ActiveKeys.Count(k => k.StartsWith("agentstatus", StringComparison.Ordinal)) == 3,
                "两个 Codex 会话与一个 Antigravity 会话同时拥有三个独立状态岛");
            string Key(int index) => (string)PrivateCall(window, "AgentSessionKey", owners[index])!;
            var keys = Enumerable.Range(0, 3).Select(Key).ToArray();
            var codexLayer = (Grid)PrivateCall(window, "GetNotifyLayerElement", keys[0])!;
            var codexIcon = (alpha_island.Controls.SemanticIcon)codexLayer.Children.OfType<Border>().Single().Child;
            Check(codexIcon.Source != null && alpha_island.Animation.AgentAvatarMotion.IsAssigned(codexIcon),
                "真实 Codex 会话状态显示 GPT 角色，主岛与独立小岛都使用默认形象");
            Check(keys.Distinct().Count() == 3, "同一软件的会话拥有不同显示键，不会被汇总覆盖");
            var orbs = AgentField<Dictionary<string, Border>>(window, "_satelliteOrbViews");
            Check(keys.Count(k => orbs.TryGetValue(k, out var orb) && orb.Visibility == Visibility.Visible) == 2,
                "一个会话显示在主岛，其余两个会话同时显示在独立小岛");
            void SavePreview(string name)
            {
                PumpAnimations(500); root.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(root);
                var crop = new System.Windows.Media.Imaging.CroppedBitmap(bitmap, new Int32Rect(200, 0, Math.Min(800, (int)root.ActualWidth - 200), Math.Min(100, (int)root.ActualHeight)));
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(crop));
                string folder = System.IO.Path.Combine(Environment.CurrentDirectory, "bin", "AgentMultiSessionPreview");
                System.IO.Directory.CreateDirectory(folder);
                using var output = System.IO.File.Create(System.IO.Path.Combine(folder, name)); encoder.Save(output);
            }
            SavePreview("three-thinking.png");
            Report(0, "working");
            Check((AgentRunState)PrivateCall(window, "AgentStateFor", keys[0])! == AgentRunState.Working
                && Descendants<TextBlock>(codexLayer).Any(text => text.Text.Contains("执行")), "真实助手岛的执行状态和标题同步切换");
            var loadClip = typeof(alpha_island.Animation.AgentAvatarMotion).GetMethod("Load", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            Check(ReferenceEquals(loadClip.Invoke(null, new object[] { "Codex", AgentRunState.Working }), loadClip.Invoke(null, new object[] { "Codex", AgentRunState.Streaming })),
                "执行状态复用工作动作素材，保留与思考动作不同的播放轨道");
            Report(0, "thinking");
            var initial = AgentStatus.Snapshot.ToDictionary(a => a.Owner);
            Report(1, "streaming"); Report(2, "tool-wait");
            Check((AgentRunState)PrivateCall(window, "AgentStateFor", keys[0])! == AgentRunState.Thinking
                && (AgentRunState)PrivateCall(window, "AgentStateFor", keys[1])! == AgentRunState.Streaming
                && (AgentRunState)PrivateCall(window, "AgentStateFor", keys[2])! == AgentRunState.ToolWait,
                "三个会话分别保留思考、输出和授权等待状态");
            Check(layers.ActiveKeys.Count(k => k.StartsWith("agentstatus", StringComparison.Ordinal)) == 3,
                "授权等待只提高本会话提醒优先级，不撤下其他任务");
            Check(AgentStatus.Snapshot.Single(a => a.Owner == owners[0]).SinceUtc == initial[owners[0]].SinceUtc,
                "其他会话更新不重置本会话的计时");
            int events = 0;
            EventHandler listener = (_, _) => events++;
            AgentStatus.Changed += listener;
            try { Report(0, "error"); }
            finally { AgentStatus.Changed -= listener; }
            Check(events > 0 && AgentStatus.Owner == owners[2]
                && (AgentRunState)PrivateCall(window, "AgentStateFor", keys[0])! == AgentRunState.Error,
                "未占全局优先状态的会话变化也立即通知显示层");
            var image = AgentFixtureWindowSnapshot();
            PrivateCall(window, "SetAgentSessionArtwork", owners[1], image);
            var bOrb = orbs[keys[1]];
            var bGlyph = ((Grid)bOrb.Child).Children.OfType<alpha_island.Controls.SemanticIcon>().Single(x => x.Name == "OrbGlyph");
            Check(ReferenceEquals(bGlyph.Source, image) && Equals(bGlyph.Tag, owners[1]),
                "每个会话的小岛绑定自己的图片身份");
            var cRoot = (Grid)PrivateCall(window, "GetNotifyLayerElement", keys[2])!;
            Check(cRoot.Children.OfType<Border>().Single().Child is alpha_island.Controls.SemanticIcon cGlyph && cGlyph.Source != null
                && alpha_island.Animation.AgentAvatarMotion.IsAssigned(cGlyph),
                "更换一个会话图片不覆盖另一个助手");
            PrivateCall(window, "SetState", IslandState.Card, true, "focus"); PumpAnimations(250); root.UpdateLayout();
            Check(keys.All(k => orbs.TryGetValue(k, out var orb) && orb.Visibility == Visibility.Visible)
                && window.SideBubble.Visibility != Visibility.Visible,
                "展开其他卡片时多个会话仍各有小岛，不挤成单个旁路提示");
            SavePreview("three-card.png");
            PrivateCall(window, "ShowPillNotification", IslandNotification.Seconds("volume", 3));
            PumpAnimations(100); root.UpdateLayout();
            Check(window.SideBubble.Visibility == Visibility.Visible && AgentField<string?>(window, "_sideBubbleComponentKey") == "volume"
                && keys.All(k => orbs[k].Visibility == Visibility.Visible),
                "多个会话旁岛保留普通音量等高优先提示的卡片旁路显示");
            layers.Hide("volume"); PrivateCall(window, "SyncNotificationLayers");
            PrivateCall(window, "SetState", IslandState.Pill, true); PumpAnimations(100);
            fixture.Foreground = codexWindow.Handle; PrivateCall(window, "UpdateAgentStatusUi"); PumpAnimations(100);
            Check(!layers.IsActive(keys[0]) && !layers.IsActive(keys[1]) && layers.IsActive(keys[2]),
                "Codex 在前台时按现有应用隐藏规则隐藏其会话，其他助手仍独立显示");
            fixture.Foreground = IntPtr.Zero; PrivateCall(window, "UpdateAgentStatusUi"); PumpAnimations(100);
            Check(keys.All(layers.IsActive), "回到后台后各自恢复自己的状态和小岛");
            bool reduce = alpha_island.Animation.MotionPolicy.ReduceMotion, save = alpha_island.Animation.MotionPolicy.PowerSave;
            try
            {
                alpha_island.Animation.MotionPolicy.Apply(true, false);
                fixture.Activated = null;
                PrivateCall(window, "TryOpenAgentIsland", keys[2]);
                Check(fixture.Activated?.Handle == otherWindow.Handle, "点击其他助手会话打开该助手，不使用全局赢家以外的错误目标");
                fixture.Activated = null; PrivateCall(window, "TryOpenAgentIsland", keys[2]);
                Check(fixture.Activated == null, "会话所属软件已经在前台时不重复跳转");
                fixture.Foreground = IntPtr.Zero; PrivateCall(window, "UpdateAgentStatusUi");
            }
            finally { alpha_island.Animation.MotionPolicy.Apply(reduce, save); }
            layers.SetPinned(keys[1], true); Report(1, "idle");
            Check(layers.IsPinned(keys[1]) && layers.IsActive(keys[1])
                && (AgentRunState)PrivateCall(window, "AgentStateFor", keys[1])! == AgentRunState.Idle,
                "结束已置顶会话只将本会话变为空闲，其余会话保持运行");
            layers.SetPinned(keys[1], false); PrivateCall(window, "UpdateAgentStatusUi");
            Check(!layers.IsRegistered(keys[1]) && keys.Where(k => k != keys[1]).All(layers.IsActive),
                "取消已结束会话置顶只清理自己的视图和小岛");
            Report(1, "thinking"); string resumedKey = Key(1);
            Check(resumedKey == keys[1], "同一会话重新开始仍使用稳定身份");
            var resumedRoot = (Grid)PrivateCall(window, "GetNotifyLayerElement", resumedKey)!;
            Check(resumedRoot.Children.OfType<Border>().Single().Child is alpha_island.Controls.SemanticIcon resumedGlyph
                && ReferenceEquals(resumedGlyph.Source, image), "同一会话新一轮任务保留单独绑定的图片");
            Report(0, "idle");
            Check(layers.IsActive(Key(1)) && layers.IsActive(Key(2)) && AgentField<IslandState>(window, "_state") == IslandState.Pill,
                "主身份会话结束不会收起仍在运行的其他会话");
            Report(0, "thinking"); keys[0] = Key(0);
            PrivateCall(window, "CollapseToBar", true);
            Report(0, "thinking"); Report(1, "thinking");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar, "多个会话续期保留用户明确手动收起");
            Report(2, "thinking"); Report(2, "idle"); Report(2, "thinking");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill, "其中一个会话新一轮任务正常重新提醒");
            foreach (int index in Enumerable.Range(0, 3)) Report(index, "idle");
            PrivateCall(window, "HandleOutsideIslandClick"); PumpAnimations(100);
            Check(!AgentStatus.IsBusy && !layers.ActiveKeys.Any(k => k.StartsWith("agentstatus", StringComparison.Ordinal))
                && AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "全部会话结束后统一收起，不残留旧会话或截图追踪");
        }
        finally
        {
            foreach (string owner in owners) AgentStatus.Reset(owner: owner);
            PrivateCall(window, "UpdateAgentStatusUi"); PrivateCall(window, "StopAgentStatus");
            SetAgentField(window, "_agentNavigation", navigation);
            layers.HideAll(); PrivateCall(window, "SetState", IslandState.Bar, true);
        }
    }
}
