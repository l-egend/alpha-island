using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void DeepSeekSessionAvatarChecks(alpha_island.MainWindow window, Grid root)
        => MultiProviderSessionAvatarChecks(window, root, ["deepseek-harness", "dsh", "codex", "antigravity"], "ds-avatar-fixture-", "DS", "ds-four-card.png");

    private static void MultiProviderSessionAvatarChecks(alpha_island.MainWindow window, Grid root,
        string[] sources, string prefix, string label, string previewName)
    {
        var registry = new ExternalAgentRegistry();
        var sessions = Enumerable.Range(0, sources.Length).Select(i => prefix + i).ToArray();
        var owners = sources.Select((source, i) => "external:" + source + "/" + sessions[i]).ToArray();
        var navigation = AgentField<AgentAppNavigationService>(window, "_agentNavigation");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        bool reduce = MotionPolicy.ReduceMotion, save = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        void Report(int index, string state)
        {
            if (!registry.Update(sources[index], sessions[index], state, "Fixture " + index, 300, DateTime.UtcNow, out var error))
                throw new InvalidOperationException(error);
            PrivateCall(window, "UpdateAgentStatusUi");
        }
        try
        {
            SetAgentField(window, "_agentNavigation", new AgentAppNavigationService(new AgentWindowFixture()));
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            foreach (int i in Enumerable.Range(0, sources.Length)) Report(i, "thinking");
            PumpAnimations(100); root.UpdateLayout();
            var keys = owners.Select(owner => (string)PrivateCall(window, "AgentSessionKey", owner)!).ToArray();
            var icons = keys.Select(key => (SemanticIcon)((Grid)PrivateCall(window, "GetNotifyLayerElement", key)!)
                .Children.OfType<Border>().Single().Child).ToArray();
            Check(keys.Distinct().Count() == sources.Length && keys.All(layers.IsActive), $"两个 {label} 会话与其他助手各自拥有独立状态岛");
            AgentAvatarMotion.Advance(0);
            var first = icons.Select(icon => icon.Source).ToArray();
            Check(icons.All(AgentAvatarMotion.IsAssigned) && ReferenceEquals(first[0], first[1])
                && first.Skip(2).All(frame => !ReferenceEquals(first[0], frame)) && first.Skip(2).Distinct().Count() == sources.Length - 2,
                $"实际会话视图使用各自默认角色，两个 {label} 会话共享素材但保持独立身份");
            var since = AgentStatus.Snapshot.Single(a => a.Owner == owners[0]).SinceUtc;
            Report(1, "tool-wait"); AgentAvatarMotion.Advance(0);
            Check((AgentRunState)PrivateCall(window, "AgentStateFor", keys[1])! == AgentRunState.ToolWait
                && ReferenceEquals(icons[0].Source, first[0]) && Enumerable.Range(2, sources.Length - 2).All(i => ReferenceEquals(icons[i].Source, first[i]))
                && !ReferenceEquals(icons[1].Source, first[1])
                && AgentStatus.Snapshot.Single(a => a.Owner == owners[0]).SinceUtc == since,
                $"{label} 授权提醒只更换本会话动作，不覆盖其他会话或重置计时");
            PrivateCall(window, "SetState", IslandState.Card, true, "focus"); PumpAnimations(400); root.UpdateLayout();
            var orbs = AgentField<Dictionary<string, Border>>(window, "_satelliteOrbViews");
            SemanticIcon Glyph(string key) => ((Grid)orbs[key].Child).Children.OfType<SemanticIcon>().Single(icon => icon.Name == "OrbGlyph");
            Check(keys.All(key => orbs.TryGetValue(key, out var orb) && orb.Visibility == Visibility.Visible
                && AgentAvatarMotion.IsAssigned(Glyph(key))), $"展开卡片时 {sources.Length} 个会话小岛均显示对应动态形象");
            var cardBounds = window.IslandBody.TransformToAncestor(root).TransformBounds(new Rect(0, 0,
                window.IslandBody.ActualWidth, window.IslandBody.ActualHeight));
            Check(keys.All(key => AgentAvatarMotion.HasOverflowVisual(Glyph(key)) && !Glyph(key).IsHitTestVisible
                && System.Windows.Documents.AdornerLayer.GetAdornerLayer(Glyph(key))?.GetAdorners(Glyph(key))
                    ?.OfType<AgentAvatarAdorner>().Any(visual => !visual.IsHitTestVisible
                        && System.Windows.Media.VisualTreeHelper.GetDescendantBounds(visual).Height >= 59) == true),
                $"{label} 形象通过 60 像素透明出框层显示，点击仍由固定小岛承担");
            Check(keys.All(key => orbs[key].TransformToAncestor(root).TransformBounds(new Rect(0, 0,
                orbs[key].ActualWidth, orbs[key].ActualHeight)).Right <= cardBounds.Left - 7),
                $"卡片展开变宽后 {sources.Length} 个会话小岛均保持在卡片外侧，不压住标题");
            var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(root);
            int previewLeft = sources.Length > 4 ? 0 : 100;
            var crop = new CroppedBitmap(bitmap, new Int32Rect(previewLeft, 0, Math.Min(1000, (int)root.ActualWidth - previewLeft), Math.Min(100, (int)root.ActualHeight)));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(crop));
            string folder = System.IO.Path.Combine(Environment.CurrentDirectory, "bin", "AgentMultiSessionPreview");
            System.IO.Directory.CreateDirectory(folder);
            using (var output = System.IO.File.Create(System.IO.Path.Combine(folder, previewName))) encoder.Save(output);
            var custom = AgentFixtureWindowSnapshot();
            PrivateCall(window, "SetAgentSessionArtwork", owners[1], custom);
            Check(ReferenceEquals(Glyph(keys[1]).Source, custom) && keys.Where(key => key != keys[1]).All(key => AgentAvatarMotion.IsAssigned(Glyph(key))),
                $"{label} 会话自定义图片仅覆盖本会话，其他软件形象保持正确");
            Report(1, "error"); Report(0, "thinking");
            Check(ReferenceEquals(Glyph(keys[1]).Source, custom)
                && (AgentRunState)PrivateCall(window, "AgentStateFor", keys[0])! == AgentRunState.Thinking,
                $"{label} 后续心跳和出错事件保持会话图片以及另一任务状态");
            foreach (int i in Enumerable.Range(0, sources.Length)) Report(i, "idle");
            Check(!AgentStatus.Snapshot.Any(a => owners.Contains(a.Owner)) && keys.All(key => !layers.IsActive(key))
                && icons.All(icon => !AgentAvatarMotion.IsAssigned(icon) && icon.Source == null)
                && keys.All(key => !orbs.ContainsKey(key)),
                $"{sources.Length} 个会话结束后清理各自形象与视图");
        }
        finally
        {
            foreach (string owner in owners) AgentStatus.Reset(owner: owner);
            PrivateCall(window, "UpdateAgentStatusUi"); PrivateCall(window, "StopAgentStatus");
            SetAgentField(window, "_agentNavigation", navigation);
            MotionPolicy.Apply(reduce, save); MotionPolicy.OverrideRenderTierForDebug(tier);
            layers.HideAll(); PrivateCall(window, "SetState", IslandState.Bar, true);
        }
    }
}
