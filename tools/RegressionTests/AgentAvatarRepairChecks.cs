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
    private static void AgentAvatarRoutingChecks(alpha_island.MainWindow window, Grid root)
    {
        var registry = new ExternalAgentRegistry();
        var navigation = AgentField<AgentAppNavigationService>(window, "_agentNavigation");
        bool reduced = MotionPolicy.ReduceMotion, saved = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        var expected = new SemanticIcon();
        var cases = new[] {
            (Source:"antigravity", Family:"default", Expected:"claude-code"),
            (Source:"antigravity", Family:"claude", Expected:"claude-code"),
            (Source:"antigravity", Family:"gemini", Expected:"antigravity"),
            (Source:"antigravity", Family:"", Expected:"antigravity"),
            (Source:"antigravity", Family:"deepseek", Expected:"deepseek-harness"),
            (Source:"antigravity", Family:"codex", Expected:"codex"),
            (Source:"deepseek-harness", Family:"", Expected:"deepseek-harness"),
            (Source:"dsh", Family:"", Expected:"deepseek-harness"),
            (Source:"claude-code", Family:"", Expected:"claude-code"),
            (Source:"codex", Family:"", Expected:"codex") };
        try
        {
            SetAgentField(window, "_agentNavigation", new AgentAppNavigationService(new AgentWindowFixture()));
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            registry.Update("codex", "avatar-routing-companion", "thinking", "Companion fixture", 300, DateTime.UtcNow, out _);
            PrivateCall(window, "UpdateAgentStatusUi");
            foreach (var item in cases)
            {
                string session = "avatar-route-" + item.Source + "-" + (item.Family.Length == 0 ? "missing" : item.Family);
                string owner = "external:" + item.Source + "/" + session;
                foreach (var state in new[] { AgentRunState.Thinking, AgentRunState.Streaming, AgentRunState.ToolWait, AgentRunState.Error })
                {
                    string wire = state switch { AgentRunState.Thinking => "thinking", AgentRunState.Streaming => "streaming", AgentRunState.ToolWait => "tool-wait", _ => "error" };
                    Check(registry.Update(item.Source, session, wire, "Artwork fixture", 300, DateTime.UtcNow, out _, artworkFamily: item.Family),
                        item.Source + "/" + item.Family + " 的 " + wire + " 桥接事件可接收");
                    PrivateCall(window, "UpdateAgentStatusUi");
                    string key = (string)PrivateCall(window, "AgentSessionKey", owner)!;
                    var layer = (Grid)PrivateCall(window, "GetNotifyLayerElement", key)!;
                    var icon = (SemanticIcon)layer.Children.OfType<Border>().Single().Child;
                    AgentAvatarMotion.Configure(expected, item.Expected, state, null); AgentAvatarMotion.Advance(0);
                    Check(AgentAvatarMotion.IsAssigned(icon) && ReferenceEquals(icon.Source, expected.Source),
                        item.Source + "/" + item.Family + " 的 " + wire + " 实际主岛使用对应人物");
                    PrivateCall(window, "SetState", IslandState.Card, true, "focus"); PumpAnimations(120); root.UpdateLayout();
                    var orbs = AgentField<Dictionary<string, Border>>(window, "_satelliteOrbViews");
                    var glyph = ((Grid)orbs[key].Child).Children.OfType<SemanticIcon>().Single(i => i.Name == "OrbGlyph");
                    AgentAvatarMotion.Advance(0);
                    Check(AgentAvatarMotion.IsAssigned(glyph) && ReferenceEquals(glyph.Source, expected.Source),
                        item.Source + "/" + item.Family + " 的 " + wire + " 缩略小岛使用相同人物");
                }
                AgentStatus.Reset(owner: owner); PrivateCall(window, "UpdateAgentStatusUi");
            }
        }
        finally
        {
            registry.Clear(); AgentAvatarMotion.Release(expected); PrivateCall(window, "UpdateAgentStatusUi");
            SetAgentField(window, "_agentNavigation", navigation);
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduced, saved);
        }
    }

    private static void AgentAvatarLoopChecks(alpha_island.MainWindow window, Grid root)
    {
        var results = new List<object>();
        foreach (string family in new[] { "Gemini", "Codex", "DeepSeek", "Claude" })
        foreach (var state in Enum.GetValues<AgentRunState>())
        {
            object clip = typeof(AgentAvatarMotion).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [family, state])!;
            var frames = (BitmapSource[])clip.GetType().GetProperty("Frames")!.GetValue(clip)!;
            var pixels = frames.Select(frame => {
                var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
                var bytes = new byte[frame.PixelWidth * frame.PixelHeight * 4]; converted.CopyPixels(bytes, frame.PixelWidth * 4, 0); return bytes;
            }).ToArray();
            static double Difference(byte[] a, byte[] b) => a.Zip(b, (x,y) => Math.Abs(x-y)).Average();
            double seam = Difference(pixels[^1], pixels[0]);
            var steps = Enumerable.Range(1, pixels.Length - 1).Select(i => Difference(pixels[i-1], pixels[i])).Order().ToArray();
            double typical = steps[steps.Length / 2];
            results.Add(new { Family=family, State=state.ToString(), Frames=frames.Length, Seam=seam, Typical=typical });
            Check(seam <= Math.Max(1, typical * 1.8), family + " " + state + " 的循环接缝不超过正常帧变化的 1.8 倍");
            Check(frames.All(f => f.IsFrozen) && pixels.All(p => Enumerable.Range(0,p.Length/4).Any(i => p[i*4+3] == 0)),
                family + " " + state + " 的循环帧保持透明并可共享");
        }
        Directory.CreateDirectory("bin/v99-avatar-seams");
        File.WriteAllText("bin/v99-avatar-seams/runtime-metrics.json", System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions { WriteIndented=true }));
        AgentAvatarPlaybackTimingChecks(root);
    }

    private static void AgentAvatarPlaybackTimingChecks(Grid root)
    {
        bool reduced = MotionPolicy.ReduceMotion, saved = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        var clock = (System.Diagnostics.Stopwatch)typeof(AgentAvatarMotion).GetField("Time", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var host = new Grid { Width=40, Height=40 };
        var icon = new SemanticIcon { Width=32, Height=32 }; host.Children.Add(icon); root.Children.Add(host);
        BitmapSource[] Frames(AgentRunState state)
        {
            object clip = typeof(AgentAvatarMotion).GetMethod("Load", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, ["Codex", state])!;
            return (BitmapSource[])clip.GetType().GetProperty("Frames")!.GetValue(clip)!;
        }
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false,false); root.UpdateLayout(); PumpAnimations(30);
            AgentAvatarMotion.Configure(icon,"codex",AgentRunState.Thinking,null);
            double start = clock.Elapsed.TotalSeconds; AgentAvatarMotion.AdvanceAt(start+.11);
            Check(ReferenceEquals(icon.Source,Frames(AgentRunState.Thinking)[1]),"新会话从自身第一帧连续播放，不跳到共享时钟的任意中段");
            AgentAvatarMotion.Configure(icon,"codex",AgentRunState.Thinking,null); AgentAvatarMotion.AdvanceAt(start+.28);
            Check(ReferenceEquals(icon.Source,Frames(AgentRunState.Thinking)[3]),"同状态心跳保留本会话播放位置");
            AgentAvatarMotion.Configure(icon,"codex",AgentRunState.Streaming,null); start=clock.Elapsed.TotalSeconds;
            AgentAvatarMotion.AdvanceAt(start+.11);
            Check(ReferenceEquals(icon.Source,Frames(AgentRunState.Streaming)[1]),"状态切换从新动作开头连续播放");
            host.Opacity=0; host.Opacity=1; start=clock.Elapsed.TotalSeconds; AgentAvatarMotion.AdvanceAt(start+.11);
            Check(ReferenceEquals(icon.Source,Frames(AgentRunState.Streaming)[1]),"隐藏恢复不会突然跳过动画开头");
        }
        finally
        {
            AgentAvatarMotion.Release(icon); root.Children.Remove(host);
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduced,saved);
        }
    }
}
