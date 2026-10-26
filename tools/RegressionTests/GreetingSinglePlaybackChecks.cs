using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void GreetingSinglePlaybackChecks(alpha_island.MainWindow main, string testRoot)
    {
        var options = AgentField<IslandOptions>(main, "_options");
        bool reduce = MotionPolicy.ReduceMotion, save = MotionPolicy.PowerSave; int tier = MotionPolicy.RenderTier;
        var amiya = GreetingPresetCatalog.LoadFrames(GreetingPresetCatalog.AmiyaMedia);
        string gif = Path.Combine(testRoot, "greeting-once.gif");
        var encoder = new GifBitmapEncoder();
        foreach (var picture in new[] { amiya[0].Image, amiya[^1].Image })
        {
            var metadata = new BitmapMetadata("gif"); metadata.SetQuery("/grctlext/Delay", (ushort)50);
            encoder.Frames.Add(BitmapFrame.Create(picture, null, metadata, null));
        }
        using (var file = File.Create(gif)) encoder.Save(file);
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            options.GreetingEnabled = true; options.GreetingSeconds = 10;
            SettingsRegistry.Write(options, "GreetingSoundEnabled", false);
            foreach (string preset in new[] { GreetingPresetCatalog.Amiya, GreetingPresetCatalog.Nailong, GreetingPresetCatalog.HoshinoSwimsuit, GreetingPresetCatalog.Custom })
            {
                PrivateCall(main, "DismissGreeting");
                if (preset == GreetingPresetCatalog.Custom)
                {
                    SettingsRegistry.Write(options, "GreetingPreset", preset);
                    SettingsRegistry.Write(options, "GreetingImagePath", gif);
                }
                else GreetingPresetCatalog.Select(options, preset);
                PrivateCall(main, "ApplyGreetingMedia");
                PrivateCall(main, "OnGreetingPresentationChanged", true);
                var frames = AgentField<GreetingGifFrame[]>(main, "_greetingFrames");
                Check(frames.Length > 1 && AgentField<DispatcherTimer?>(main, "_greetingImageTimer")?.IsEnabled == true,
                    preset + " 每次欢迎展示开始播放完整动画");
                SetAgentField(main, "_greetingFrameIndex", frames.Length - 2);
                PrivateCall(main, "OnGreetingImageTick", null, EventArgs.Empty);
                for (int i = 0; i < frames.Length + 2; i++) PrivateCall(main, "OnGreetingImageTick", null, EventArgs.Empty);
                Check(ReferenceEquals(main.GreetingCustomImage.Source, frames[^1].Image)
                    && AgentField<int>(main, "_greetingFrameIndex") == frames.Length - 1,
                    preset + " 到最后一帧后不再绕回开头");
                Check(AgentField<DispatcherTimer?>(main, "_greetingImageTimer") == null,
                    preset + " 播放一次后释放播放计时器");
                PrivateCall(main, "ApplyGreetingMedia");
                PrivateCall(main, "OnGreetingPresentationChanged", true);
                Check(ReferenceEquals(main.GreetingCustomImage.Source, frames[^1].Image)
                    && AgentField<DispatcherTimer?>(main, "_greetingImageTimer") == null,
                    preset + " 保存设置或重复可见通知不会重新播放");
                MotionPolicy.Apply(true, false); PrivateCall(main, "ApplyGreetingMedia");
                MotionPolicy.Apply(false, false); PrivateCall(main, "ApplyGreetingMedia");
                Check(ReferenceEquals(main.GreetingCustomImage.Source, frames[^1].Image)
                    && AgentField<DispatcherTimer?>(main, "_greetingImageTimer") == null,
                    preset + " 动效策略改变也不会重播已经结束的动画");
                PrivateCall(main, "OnGreetingPresentationChanged", false);
                Check(AgentField<DispatcherTimer?>(main, "_greetingImageTimer") == null
                    && AgentField<int>(main, "_greetingFrameIndex") == 0
                    && (!GreetingPresetCatalog.IsBuiltInAnimation(GreetingPresetCatalog.ImageKey(options))
                        || AgentField<GreetingGifFrame[]>(main, "_greetingFrames").Length == 1),
                    preset + " 欢迎隐藏后重置位置并保持已有资源回收逻辑");
                PrivateCall(main, "OnGreetingPresentationChanged", true);
                frames = AgentField<GreetingGifFrame[]>(main, "_greetingFrames");
                Check(ReferenceEquals(main.GreetingCustomImage.Source, frames[0].Image)
                    && AgentField<int>(main, "_greetingFrameIndex") == 0
                    && AgentField<DispatcherTimer?>(main, "_greetingImageTimer")?.IsEnabled == true,
                    preset + " 下一次欢迎展示可从头播放一次");
                if (frames.Length > 3)
                {
                    PrivateCall(main, "OnGreetingImageTick", null, EventArgs.Empty);
                    var current = main.GreetingCustomImage.Source;
                    MotionPolicy.Apply(false, true); PrivateCall(main, "SyncGreetingImageAnimation");
                    MotionPolicy.Apply(false, false); PrivateCall(main, "SyncGreetingImageAnimation");
                    Check(ReferenceEquals(main.GreetingCustomImage.Source, current)
                        && AgentField<int>(main, "_greetingFrameIndex") == 1,
                        preset + " 播放中节能暂停后继续当前进度，不从头重复");
                }
                else
                {
                    PrivateCall(main, "ShowStartupGreeting"); PumpAnimations(1300);
                    Check(ReferenceEquals(main.GreetingCustomImage.Source, frames[^1].Image)
                        && AgentField<DispatcherTimer?>(main, "_greetingImageTimer") == null,
                        "真实欢迎窗口中的 GIF 定时播放结束后保持末帧，不再循环");
                }
            }
        }
        finally
        {
            PrivateCall(main, "OnGreetingPresentationChanged", false);
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduce, save);
        }
    }
}
