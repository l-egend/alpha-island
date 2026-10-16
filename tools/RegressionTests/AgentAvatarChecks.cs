using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;

internal static partial class Program
{
    private static void AgentAvatarChecks(alpha_island.MainWindow window, Grid root)
    {
        bool reduce = MotionPolicy.ReduceMotion, save = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        var host = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        var a = new SemanticIcon { Shape = IconShape.Agent, Width = 32, Height = 32 };
        var b = new SemanticIcon { Shape = IconShape.Agent, Width = 32, Height = 32 };
        host.Children.Add(a); host.Children.Add(b); root.Children.Add(host);
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            root.UpdateLayout(); PumpAnimations(30);
            foreach (var state in Enum.GetValues<AgentRunState>())
            {
                AgentAvatarMotion.Configure(a, "antigravity", state, null);
                var frame = (BitmapSource)a.Source!;
                var pixels = new byte[192 * 192 * 4]; frame.CopyPixels(pixels, 192 * 4, 0);
                Check(frame.IsFrozen && frame.PixelWidth == 192 && pixels.Where((_, i) => i % 4 == 3).Any(x => x == 0)
                    && pixels.Where((_, i) => i % 4 == 3).Any(x => x == 255), state + " 的动画保留透明背景与不透明角色");
            }
            AgentAvatarMotion.Configure(a, "antigravity", AgentRunState.Thinking, null);
            AgentAvatarMotion.Configure(b, "antigravity", AgentRunState.ToolWait, null);
            AgentAvatarMotion.Advance(0);
            var thinking = a.Source; var wait = b.Source;
            Check(!ReferenceEquals(thinking, wait), "不同会话分别显示思考和授权动画");
            AgentAvatarMotion.Advance(.4);
            Check(!ReferenceEquals(a.Source, thinking) && !ReferenceEquals(b.Source, wait) && AgentAvatarMotion.ClockRunning,
                "两个可见会话通过共享时钟持续播放各自状态");
            var progress = a.Source;
            AgentAvatarMotion.Configure(a, "antigravity", AgentRunState.Thinking, null);
            Check(ReferenceEquals(a.Source, progress), "同状态心跳不会重新开始动画");
            AgentAvatarMotion.Configure(b, "antigravity", AgentRunState.Error, null);
            Check(ReferenceEquals(a.Source, progress) && !ReferenceEquals(b.Source, wait), "一个会话出错不会改动另一个会话的动画");
            host.Visibility = Visibility.Collapsed;
            Check(!AgentAvatarMotion.ClockRunning, "所有动画隐藏后停止帧时钟");
            host.Visibility = Visibility.Visible; root.UpdateLayout(); PumpAnimations(10);
            Check(AgentAvatarMotion.ClockRunning, "再次显示后恢复帧时钟");
            host.Opacity = 0;
            Check(!AgentAvatarMotion.ClockRunning, "卡片仅通过透明度隐藏时也停止动画");
            host.Opacity = 1;
            MotionPolicy.Apply(true, false);
            Check(!AgentAvatarMotion.ClockRunning && a.Source != null, "减弱动效时保留形象但停止动画");
            MotionPolicy.Apply(false, true);
            Check(!AgentAvatarMotion.ClockRunning, "节能模式不持续解码或刷新动画");
            MotionPolicy.Apply(false, false);
            var custom = AgentFixtureWindowSnapshot();
            AgentAvatarMotion.Configure(a, "antigravity", AgentRunState.Thinking, custom);
            Check(ReferenceEquals(a.Source, custom) && !AgentAvatarMotion.IsAssigned(a), "手动设置的会话图片优先于默认 Gemini 动画");
            AgentAvatarMotion.Configure(b, "fixture-unknown", AgentRunState.Thinking, null);
            Check(b.Source == null && !AgentAvatarMotion.ClockRunning, "其他助手不套用 Gemini 动画且最后一个退出时停止时钟");
            AgentAvatarMotion.Configure(a, "antigravity", AgentRunState.Streaming, null);
            root.Children.Remove(host); PumpAnimations(10);
            Check(!AgentAvatarMotion.ClockRunning, "视图卸载后无残留动画时钟");
            root.Children.Add(host); root.UpdateLayout(); PumpAnimations(20);
            foreach (var state in Enum.GetValues<AgentRunState>())
            {
                AgentAvatarMotion.Configure(a, "codex", state, null);
                var frame = (BitmapSource)a.Source!;
                var pixels = new byte[192 * 192 * 4]; frame.CopyPixels(pixels, 192 * 4, 0);
                int whiteOpaque = Enumerable.Range(0, 192 * 192).Count(i => pixels[i * 4] > 225
                    && pixels[i * 4 + 1] > 225 && pixels[i * 4 + 2] > 225 && pixels[i * 4 + 3] > 240);
                Check(frame.IsFrozen && frame.PixelWidth == 192 && pixels.Where((_, i) => i % 4 == 3).Any(x => x == 0)
                    && whiteOpaque > 400, state + " 的 Codex 动画保留白发与透明背景");
            }
            AgentAvatarMotion.Configure(a, "codex", AgentRunState.Thinking, null);
            AgentAvatarMotion.Configure(b, "antigravity", AgentRunState.Thinking, null);
            AgentAvatarMotion.Advance(0); var codex = a.Source; var gemini = b.Source;
            Check(!ReferenceEquals(codex, gemini), "Codex 与 Gemini 同时思考时显示各自形象");
            AgentAvatarMotion.Configure(b, "codex", AgentRunState.Thinking, null);
            AgentAvatarMotion.Advance(0);
            Check(ReferenceEquals(a.Source, b.Source), "复用的小岛切换助手而状态相同时，也切换到正确形象并共享素材");
            AgentAvatarMotion.Advance(.4);
            Check(!ReferenceEquals(a.Source, codex) && ReferenceEquals(a.Source, b.Source), "多个 Codex 会话共享播放时钟，不重复解码");
            var source = a.Source;
            AgentAvatarMotion.Configure(b, "codex", AgentRunState.ToolWait, null);
            Check(ReferenceEquals(a.Source, source) && !ReferenceEquals(b.Source, source), "一个 Codex 会话等待授权不会改动另一个会话的思考动作");
            AgentAvatarMotion.Configure(a, "codex", AgentRunState.Thinking, custom);
            Check(ReferenceEquals(a.Source, custom) && !AgentAvatarMotion.IsAssigned(a), "Codex 会话单独设置的图片也优先保留");
            host.Visibility = Visibility.Collapsed;
            Check(!AgentAvatarMotion.ClockRunning, "Codex 动画隐藏后停止唯一共享时钟");
            host.Visibility = Visibility.Visible; root.UpdateLayout(); PumpAnimations(20);
            foreach (var state in Enum.GetValues<AgentRunState>())
            {
                AgentAvatarMotion.Configure(a, "deepseek-harness", state, null);
                var frame = (BitmapSource)a.Source!;
                var pixels = new byte[192 * 192 * 4]; frame.CopyPixels(pixels, 192 * 4, 0);
                int whiteOpaque = Enumerable.Range(0, 192 * 192).Count(i => pixels[i * 4] > 225
                    && pixels[i * 4 + 1] > 225 && pixels[i * 4 + 2] > 225 && pixels[i * 4 + 3] > 240);
                Check(frame.IsFrozen && frame.PixelWidth == 192 && pixels.Where((_, i) => i % 4 == 3).Any(x => x == 0)
                    && whiteOpaque > 200, state + " 的 DS 动画保留透明背景、白色头饰与衣服");
            }
            AgentAvatarMotion.Configure(a, "deepseek-harness", AgentRunState.Thinking, null);
            AgentAvatarMotion.Configure(b, "codex", AgentRunState.Thinking, null);
            AgentAvatarMotion.Advance(0);
            Check(!ReferenceEquals(a.Source, b.Source), "DS 与 Codex 同时思考时不会混用形象");
            AgentAvatarMotion.Configure(b, "antigravity", AgentRunState.Thinking, null);
            AgentAvatarMotion.Advance(0);
            Check(!ReferenceEquals(a.Source, b.Source), "复用同状态图标切换到 Gemini 时也更新形象");
            AgentAvatarMotion.Configure(b, "dsh", AgentRunState.Thinking, null);
            AgentAvatarMotion.Advance(0); var deepseek = a.Source;
            Check(ReferenceEquals(a.Source, b.Source), "DeepSeek Harness 与 DSH 别名使用同一套 DS 素材");
            AgentAvatarMotion.Advance(.4);
            Check(!ReferenceEquals(a.Source, deepseek) && ReferenceEquals(a.Source, b.Source), "多个 DS 会话共享素材与播放时钟");
            var deepseekThinking = a.Source;
            AgentAvatarMotion.Configure(b, "dsh", AgentRunState.ToolWait, null);
            Check(ReferenceEquals(a.Source, deepseekThinking) && !ReferenceEquals(b.Source, a.Source), "一个 DS 会话等待授权不会改动其他 DS 会话");
            AgentAvatarMotion.Configure(a, "deepseek-harness", AgentRunState.Thinking, custom);
            Check(ReferenceEquals(a.Source, custom) && !AgentAvatarMotion.IsAssigned(a), "DS 会话单独设置的图片继续优先使用");
            host.Visibility = Visibility.Collapsed;
            Check(!AgentAvatarMotion.ClockRunning, "最后一个 DS 形象隐藏后停止共享时钟");
        }
        finally
        {
            AgentAvatarMotion.Release(a); AgentAvatarMotion.Release(b); root.Children.Remove(host);
            MotionPolicy.Apply(reduce, save); MotionPolicy.OverrideRenderTierForDebug(tier);
        }
        var registry = new alpha_island.Services.ExternalAgentRegistry();
        const string owner = "external:antigravity/model-family-fixture";
        var navigation = AgentField<alpha_island.Services.AgentAppNavigationService>(window, "_agentNavigation");
        try
        {
            SetAgentField(window, "_agentNavigation", new alpha_island.Services.AgentAppNavigationService(new AgentWindowFixture()));
            registry.Update("antigravity", "model-family-fixture", "thinking", "Fixture", 300, DateTime.UtcNow, out _, artworkFamily: "gemini");
            PrivateCall(window, "UpdateAgentStatusUi");
            string key = (string)PrivateCall(window, "AgentSessionKey", owner)!;
            var layer = (Grid)PrivateCall(window, "GetNotifyLayerElement", key)!;
            var icon = (SemanticIcon)layer.Children.OfType<Border>().Single().Child;
            Check(AgentAvatarMotion.IsAssigned(icon), "标记为 Gemini 的 Antigravity 会话使用对应动画");
            registry.Update("antigravity", "model-family-fixture", "thinking", "Fixture", 300, DateTime.UtcNow, out _);
            PrivateCall(window, "UpdateAgentStatusUi");
            Check(AgentStatus.Snapshot.Single(a => a.Owner == owner).ArtworkFamily == "gemini", "不带模型信息的后续工具事件保留会话形象");
            registry.Update("antigravity", "model-family-fixture", "thinking", "Fixture", 300, DateTime.UtcNow, out _, artworkFamily: "default");
            PrivateCall(window, "UpdateAgentStatusUi");
            var claude = new SemanticIcon();
            AgentAvatarMotion.Configure(claude, "claude-code", AgentRunState.Thinking, null); AgentAvatarMotion.Advance(0);
            Check(AgentAvatarMotion.IsAssigned(icon) && ReferenceEquals(icon.Source, claude.Source), "旧版桥接的 Claude 标记也使用 Claude 动画");
            AgentAvatarMotion.Release(claude);
        }
        finally { AgentStatus.Reset(owner: owner); PrivateCall(window, "UpdateAgentStatusUi"); SetAgentField(window, "_agentNavigation", navigation); }
        DeepSeekSessionAvatarChecks(window, root);
        ClaudeAvatarChecks(window, root);
    }
}
