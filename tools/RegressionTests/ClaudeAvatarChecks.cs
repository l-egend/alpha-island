using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;

internal static partial class Program
{
    private static void ClaudeAvatarChecks(alpha_island.MainWindow window, Grid root)
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
                AgentAvatarMotion.Configure(a, "claude-code", state, null);
                var frame = (BitmapSource)a.Source!;
                var pixels = new byte[192 * 192 * 4]; frame.CopyPixels(pixels, 192 * 4, 0);
                int orange = Enumerable.Range(0, 192 * 192).Count(i => pixels[i * 4 + 2] > 190
                    && pixels[i * 4 + 1] is > 60 and < 190 && pixels[i * 4] < 130
                    && pixels[i * 4 + 2] - pixels[i * 4 + 1] > 25 && pixels[i * 4 + 3] > 240);
                int white = Enumerable.Range(0, 192 * 192).Count(i => pixels[i * 4] > 225
                    && pixels[i * 4 + 1] > 225 && pixels[i * 4 + 2] > 225 && pixels[i * 4 + 3] > 240);
                Check(frame.IsFrozen && frame.PixelWidth == 192 && frame.PixelHeight == 192
                    && pixels.Where((_, i) => i % 4 == 3).Any(x => x == 0) && orange > 300 && white > 100,
                    state + " 的 Claude 动画保留橙发、白色服饰与透明背景");
            }
            AgentAvatarMotion.Configure(a, "claude-code", AgentRunState.Thinking, null);
            foreach (string source in new[] { "codex", "antigravity", "deepseek-harness" })
            {
                AgentAvatarMotion.Configure(b, source, AgentRunState.Thinking, null);
                AgentAvatarMotion.Advance(0);
                Check(!ReferenceEquals(a.Source, b.Source), "Claude 与 " + source + " 同状态时使用各自形象");
            }
            AgentAvatarMotion.Configure(b, "claude-code", AgentRunState.Thinking, null);
            AgentAvatarMotion.Advance(0); var first = a.Source;
            Check(ReferenceEquals(a.Source, b.Source), "多个 Claude 会话共享不可变素材");
            AgentAvatarMotion.Advance(.4);
            Check(!ReferenceEquals(a.Source, first) && ReferenceEquals(a.Source, b.Source) && AgentAvatarMotion.ClockRunning,
                "多个 Claude 会话通过唯一共享时钟播放");
            var thinking = a.Source;
            AgentAvatarMotion.Configure(a, "claude-code", AgentRunState.Thinking, null);
            Check(ReferenceEquals(a.Source, thinking), "Claude 同状态心跳不会重置动画");
            AgentAvatarMotion.Configure(b, "claude-code", AgentRunState.ToolWait, null);
            Check(ReferenceEquals(a.Source, thinking) && !ReferenceEquals(a.Source, b.Source), "一个 Claude 会话等待授权不改变另一会话动作");
            var custom = AgentFixtureWindowSnapshot();
            AgentAvatarMotion.Configure(a, "claude-code", AgentRunState.Error, custom);
            Check(ReferenceEquals(a.Source, custom) && !AgentAvatarMotion.IsAssigned(a), "Claude 会话单独指定的图片继续优先使用");
            host.Visibility = Visibility.Collapsed;
            Check(!AgentAvatarMotion.ClockRunning, "所有 Claude 形象隐藏后停止播放时钟");
        }
        finally
        {
            AgentAvatarMotion.Release(a); AgentAvatarMotion.Release(b); root.Children.Remove(host);
            MotionPolicy.Apply(reduce, save); MotionPolicy.OverrideRenderTierForDebug(tier);
        }
        MultiProviderSessionAvatarChecks(window, root, ["claude-code", "claude-code", "codex", "antigravity", "deepseek-harness"],
            "claude-avatar-fixture-", "Claude", "claude-five-card.png");
    }
}
