using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Services;
using Button = System.Windows.Controls.Button;

internal static partial class Program
{
    private static void VolumeMixerUiChecks(alpha_island.MainWindow window, Grid root)
    {
        var oldService = AgentField<VolumeService?>(window, "_volumeService");
        var pages = AgentField<IslandPageHost>(window, "_pages");
        var oldState = AgentField<IslandState>(window, "_state");
        string oldPage = pages.ActiveId;
        using var service = new VolumeService(window.Dispatcher);
        var sessions = new List<AudioAppSessionViewModel> {
            new() { ProcessId = 101, Name = "音乐播放器" }, new() { ProcessId = 202, Name = "游戏" }
        };
        typeof(VolumeService).GetField("_sessionCache", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, sessions);
        try
        {
            SetAgentField(window, "_volumeService", service);
            service.SetVolume(55);
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.Volume);
            PrivateCall(window, "RefreshVolumeAppSessionsList");
            PrivateCall(window, "VolumeMixerHeader_MouseLeftButtonDown", window.VolumeMixerHeader,
                new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            root.UpdateLayout(); PumpAnimations(300);
            var sliders = Descendants<Slider>(window.VolumeAppSessionsList).ToArray();
            Check(sliders.Length == 2, "音量合成器为每个应用提供独立可操作滑块");
            Check(Descendants<TextBlock>(window.VolumeAppSessionsList).Where(t => t.Text == "音乐播放器" || t.Text == "游戏").All(t => t.FontSize >= 12),
                "应用名称字体至少 12，清晰可读");
            var systemButton = Descendants<Button>(window.VolumeMixerPanel).First(b => b.DataContext is not AudioAppSessionViewModel);
            Check(systemButton.ActualWidth >= 200 && systemButton.ActualHeight >= 32,
                "系统合成器入口使用宽圆角文字按钮，不被图标按钮宽度压缩");
            sliders[0].Value = 37; root.UpdateLayout();
            var percentProperty = typeof(AudioAppSessionViewModel).GetProperty("VolumePercent")!;
            Check((int)percentProperty.GetValue(sliders[0].DataContext)! == 37 && service.CurrentPercent == 55,
                "拖动应用音量立即更新百分比，系统主音量不变");
            var sliderBefore = sliders[0];
            PrivateCall(window, "RefreshVolumeAppSessionsList"); root.UpdateLayout();
            Check(ReferenceEquals(sliderBefore, Descendants<Slider>(window.VolumeAppSessionsList).First()),
                "后台应用列表同步复用滑块，不打断正在进行的拖动");
            sliders[1].Value = 62;
            service.ToggleAppMute(101);
            var queue = AgentField<AppAudioCommandQueue>(service, "_appCommands").Drain();
            Check(queue[101].Percent == 37 && queue[101].Muted == true && queue[202].Percent == 62,
                "快速操作多个应用分别保留音量和静音目标，不覆盖其他应用请求");
            var snapshots = service.GetAudioAppSessions();
            Check(snapshots.Single(app => app.ProcessId == 101).VolumePercent == 37
                && snapshots.Single(app => app.ProcessId == 202).VolumePercent == 62,
                "两个应用拥有独立音量，主音量保持 55%");
            SaveThemePreview(window.VolumeCardLayer, "bin/icon-previews/volume-mixer-readable.png");
        }
        finally
        {
            PrivateCall(window, "ResetCardDrawers"); SetAgentField(window, "_volumeService", oldService);
            PrivateCall(window, "SetState", oldState, true, oldPage);
        }
    }

    private static void NativeAppVolumeChecks()
    {
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
        float masterBefore = device.AudioEndpointVolume.MasterVolumeLevelScalar;
        bool masterMuteBefore = device.AudioEndpointVolume.Mute;
        using var output = new NAudio.Wave.WasapiOut(device, NAudio.CoreAudioApi.AudioClientShareMode.Shared, false, 80);
        // A silent stream creates only this test process's session; real apps are untouched.
        var silence = new NAudio.Wave.BufferedWaveProvider(device.AudioClient.MixFormat) { ReadFully = true };
        output.Init(silence); output.Play();
        using var service = new VolumeService(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        service.Start();
        int pid = Environment.ProcessId;
        bool WaitFor(Func<bool> check)
        {
            for (int attempt = 0; attempt < 60; attempt++) { if (check()) return true; Thread.Sleep(50); }
            return false;
        }
        Check(WaitFor(() => service.GetAudioAppSessions().Any(item => item.ProcessId == pid)), "后台枚举能发现测试进程的真实音频会话");
        service.SetAppVolume(pid, 37);
        var manager = device.AudioSessionManager;
        bool SessionIs(int percent, bool muted)
        {
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            var matches = Enumerable.Range(0, sessions.Count).Select(i => sessions[i]).Where(item => item.GetProcessID == pid).ToArray();
            return matches.Length > 0 && matches.All(item => Math.Abs(item.SimpleAudioVolume.Volume * 100 - percent) < 1 && item.SimpleAudioVolume.Mute == muted);
        }
        Check(WaitFor(() => SessionIs(37, false)), "应用滑块通过后台 CoreAudio 写入真实会话音量 37%");
        service.ToggleAppMute(pid);
        Check(WaitFor(() => SessionIs(37, true)), "应用静音通过后台 CoreAudio 生效，保留音量值");
        service.SetAppVolume(pid, 62);
        Check(WaitFor(() => SessionIs(62, false)), "再次调高应用音量实时生效并解除该应用静音");
        Check(Math.Abs(device.AudioEndpointVolume.MasterVolumeLevelScalar - masterBefore) < .001
            && device.AudioEndpointVolume.Mute == masterMuteBefore, "独立调节应用全程不修改系统主音量或系统静音");
        output.Stop();
    }
}
