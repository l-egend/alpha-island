using System.Net;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static int _checks;
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL: " + name);
        _checks++;
        System.Console.WriteLine("PASS: " + name);
    }

    private static async Task Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);
        try { await RunChecks(args); }
        catch (Exception error)
        {
            System.Console.Error.WriteLine(error);
            Environment.ExitCode = 1;
        }
    }

    private static async Task RunChecks(string[] args)
    {
        IslandLog.FileSinkEnabled = false;
        if (args.Contains("--media-lyrics-controls-only"))
        {
            string mediaRoot = Path.Combine(Path.GetTempPath(), "alpha-media-lyrics-" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(mediaRoot); IslandUi(mediaRoot, iconEntryProbe: "media-lyrics-controls"); Console.WriteLine($"All {_checks} media lyrics controls checks passed."); }
            finally { if (Directory.Exists(mediaRoot)) Directory.Delete(mediaRoot, true); }
            return;
        }
        if (args.Contains("--lyric-rest-only"))
        {
            string restRoot = Path.Combine(Path.GetTempPath(), "alpha-lyric-rest-" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(restRoot); LyricRestPolicyChecks(); IslandUi(restRoot, iconEntryProbe: "lyric-rest"); Console.WriteLine($"All {_checks} lyric rest checks passed."); }
            finally { if (Directory.Exists(restRoot)) Directory.Delete(restRoot, true); }
            return;
        }
        if (args.Contains("--settings-lyrics-only"))
        {
            string settingsRoot = Path.Combine(Path.GetTempPath(), "alpha-settings-lyrics-" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(settingsRoot); await LyricsSourcesBehaviorChecks(settingsRoot); IslandUi(settingsRoot, iconEntryProbe: "settings-lyrics"); Console.WriteLine($"All {_checks} settings and lyrics checks passed."); }
            finally { if (Directory.Exists(settingsRoot)) Directory.Delete(settingsRoot, true); }
            return;
        }
        if (args.Contains("--weather-content-layout-only"))
        {
            string layoutRoot = Path.Combine(Path.GetTempPath(), "alpha-weather-content-" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(layoutRoot); IslandUi(layoutRoot, iconEntryProbe: "weather-content-layout"); }
            finally { if (Directory.Exists(layoutRoot)) Directory.Delete(layoutRoot, true); }
            return;
        }
        if (args.Contains("--notification-weather-layout-only"))
        {
            string layoutRoot = Path.Combine(Path.GetTempPath(), "alpha-notification-weather-" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(layoutRoot); IslandUi(layoutRoot, iconEntryProbe: "notification-weather-layout"); }
            finally { if (Directory.Exists(layoutRoot)) Directory.Delete(layoutRoot, true); }
            return;
        }
        if (args.Contains("--ui-refinement-only"))
        {
            string layoutRoot = Path.Combine(Path.GetTempPath(), "alpha-ui-refinement-" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(layoutRoot); IslandUi(layoutRoot, iconEntryProbe: "ui-refinement"); }
            finally { if (Directory.Exists(layoutRoot)) Directory.Delete(layoutRoot, true); }
            return;
        }
        if (args.Contains("--voice-dictation-fixture"))
        {
            string dictationRoot = Path.Combine(Path.GetTempPath(), "alpha-voice-dictation-" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(dictationRoot); IslandUi(dictationRoot, iconEntryProbe: "voice-dictation-fixture"); }
            finally { if (Directory.Exists(dictationRoot)) Directory.Delete(dictationRoot, true); }
            return;
        }
        if (args.Contains("--voice-failure-layout-only"))
        {
            string layoutRoot = Path.Combine(Path.GetTempPath(), "alpha-voice-layout-" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(layoutRoot); IslandUi(layoutRoot, iconEntryProbe: "voice-failure-layout"); }
            finally { if (Directory.Exists(layoutRoot)) Directory.Delete(layoutRoot, true); }
            return;
        }
        if (args.Contains("--voice-capture-live"))
        {
            string captureRoot = Path.Combine(Path.GetTempPath(), "alpha-voice-capture-" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(captureRoot); IslandUi(captureRoot, iconEntryProbe: "voice-capture-live"); }
            finally { if (Directory.Exists(captureRoot)) Directory.Delete(captureRoot, true); }
            return;
        }
        if (args.Contains("--receiver-latency-live")) { await ReceiverLatencyLiveProbe(); return; }
        if (args.Contains("--earbud-identity-live"))
        {
            string identityRoot = Path.Combine(Path.GetTempPath(), "alpha-earbud-identity-" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(identityRoot); IslandUi(identityRoot, iconEntryProbe: "earbud-identity-live"); Console.WriteLine($"All {_checks} live earbud identity checks passed."); }
            finally { if (Directory.Exists(identityRoot)) Directory.Delete(identityRoot, true); }
            return;
        }
        if (args.Contains("--bluetooth-identity-only")) { BluetoothIdentityChecks(); Console.WriteLine($"All {_checks} Bluetooth identity checks passed."); return; }
        if (args.Contains("--speech-audio-live")) { await SpeechAudioSourceLiveChecks(); Console.WriteLine($"All {_checks} live speech audio checks passed."); return; }
        if (args.Contains("--speech-protocol-only")) { await SpeechProtocolChecks(); Console.WriteLine($"All {_checks} speech protocol checks passed."); return; }
        if (args.Contains("--speech-caption-only"))
        {
            string speechRoot = Path.Combine(Path.GetTempPath(), "alpha-speech-caption-" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(speechRoot); IslandUi(speechRoot, iconEntryProbe: "speech-caption"); Console.WriteLine($"All {_checks} speech caption checks passed."); }
            finally { if (Directory.Exists(speechRoot)) Directory.Delete(speechRoot, true); }
            return;
        }
        if (args.Contains("--voice-assistant-only"))
        {
            string voiceRoot = Path.Combine(Path.GetTempPath(), "alpha-voice-" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(voiceRoot); IslandUi(voiceRoot, iconEntryProbe: "voice-assistant"); Console.WriteLine($"All {_checks} voice assistant checks passed."); }
            finally { if (Directory.Exists(voiceRoot)) Directory.Delete(voiceRoot, true); }
            return;
        }
        if (args.Contains("--receiver-latency-only")) { ReceiverLatencyChecks(); return; }
        if (args.Contains("--speech-platform-probe"))
        {
            foreach (var recognizer in System.Speech.Recognition.SpeechRecognitionEngine.InstalledRecognizers())
                Console.WriteLine("Desktop speech recognizer: " + recognizer.Culture.Name + " | " + recognizer.Id);
            Console.WriteLine("Metadata only; --voice-capture-live verifies actual microphone startup.");
            return;
        }
        if (args.Contains("--bluetooth-connection-live"))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var peers = await BluetoothService.ReadConnectionSnapshotAsync(timeout.Token);
            foreach (var peer in peers) Console.WriteLine($"Windows Bluetooth: {peer.Name} | Connected={peer.Connected?.ToString() ?? "unknown"}");
            Check(peers.Any(p => p.Name.Contains("OPPO", StringComparison.OrdinalIgnoreCase) && p.Connected.HasValue),
                "实际 Windows 查询可以读取 OPPO 耳机的明确连接状态");
            return;
        }
        if (args.Contains("--earbuds-connection-live")) { await EarbudConnectionLiveCheck(); return; }
        if (args.Contains("--earbuds-isolated-live"))
        {
            string earbudRoot=Path.Combine(Path.GetTempPath(),"alpha-earbud-live-"+Guid.NewGuid().ToString("N"));
            try{Directory.CreateDirectory(earbudRoot);IslandUi(earbudRoot,iconEntryProbe:"earbuds-isolated-live");}
            finally{if(Directory.Exists(earbudRoot))Directory.Delete(earbudRoot,true);}
            return;
        }
        if (args.Contains("--peripheral-interactions-only"))
        {
            string peripheralRoot = Path.Combine(Path.GetTempPath(), "alpha-peripheral-" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(peripheralRoot); IslandUi(peripheralRoot, iconEntryProbe: "peripheral-interactions"); Console.WriteLine($"All {_checks} peripheral interaction checks passed."); }
            finally { if (Directory.Exists(peripheralRoot)) Directory.Delete(peripheralRoot, true); }
            return;
        }
        if (args.Contains("--personal-reminders-only")) { PersonalReminderChecks(); Console.WriteLine($"All {_checks} personal reminder checks passed."); return; }
        if (args.Contains("--desktop-insights-only")) { DesktopInsightsChecks(); Console.WriteLine($"All {_checks} desktop insight checks passed."); return; }
        if (args.Contains("--screen-tools-open-only"))
        {
            string screenRoot = Path.Combine(Path.GetTempPath(), "alpha-screen-tools-" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(screenRoot); IslandUi(screenRoot, iconEntryProbe: "screen-tools-open"); Console.WriteLine($"All {_checks} screen tools opening checks passed."); }
            finally { if (Directory.Exists(screenRoot)) Directory.Delete(screenRoot, true); }
            return;
        }
        if (args.Contains("--device-batteries-live")) { await DeviceBatteryLiveProbe(); return; }
        if (args.Contains("--device-batteries-only"))
        {
            string batteryRoot = Path.Combine(Path.GetTempPath(), "alpha-device-battery-" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(batteryRoot); DeviceBatteryPolicyChecks(); IslandUi(batteryRoot, iconEntryProbe: "device-batteries"); Console.WriteLine($"All {_checks} device battery checks passed."); }
            finally { if (Directory.Exists(batteryRoot)) Directory.Delete(batteryRoot, true); }
            return;
        }
        if (args.Contains("--island-zorder-peer")) { IslandZOrderPeer(args[1]); return; }
        if (args.Contains("--core-logic-only")) { CoreLogicChecks(); Console.WriteLine($"All {_checks} core logic checks passed."); return; }
        if (args.Contains("--optimization-core-only"))
        {
            string optimizationRoot = Path.Combine(Path.GetTempPath(), "alpha-island-optimization-" + Guid.NewGuid().ToString("N"));
            try { CoreLogicChecks(); OptimizationTickerChecks(); await OptimizationLogChecks(optimizationRoot); Console.WriteLine($"All {_checks} optimization core checks passed."); }
            finally { if (Directory.Exists(optimizationRoot)) Directory.Delete(optimizationRoot, true); }
            return;
        }
        if (args.Contains("--liquid-cell-motion-only")) { string cellRoot = Path.Combine(Path.GetTempPath(), "alpha-liquid-cell-" + Guid.NewGuid().ToString("N")); try { Directory.CreateDirectory(cellRoot); IslandUi(cellRoot, iconEntryProbe: "liquid-cell-motion"); Console.WriteLine($"All {_checks} liquid cell motion checks passed."); } finally { if (Directory.Exists(cellRoot)) Directory.Delete(cellRoot, true); } return; }
        if (args.Contains("--liquid-glass-core-only")) { LiquidGlassCoreChecks(); Console.WriteLine($"All {_checks} liquid glass core checks passed."); return; }
        if (args.Contains("--liquid-glass-native-only")) { LiquidGlassNativeChecks(); Console.WriteLine($"All {_checks} native liquid glass checks passed."); return; }
        if (args.Contains("--vivo-ip-live"))
        {
            var current=new AlphaIsland.Plugins.VivoOffice.VivoConnectionReader().Read();
            Console.WriteLine($"Vivo IP={current.IpAddress}, current device={current.IsCurrent}, current address={current.IsIpCurrent}");
            Check(current.DeviceName.Length>0 && current.IpAddress.Length>0,"本机 vivo 记录的内网地址可由实际插件读取");
            Console.WriteLine($"All {_checks} live vivo IP checks passed."); return;
        }
        if (args.Contains("--bluetooth-media-latency-only")) { await BluetoothMediaLatencyChecks(); Console.WriteLine($"All {_checks} Bluetooth media latency checks passed."); return; }
        if (args.Contains("--smart-state-stability-only")) { await SmartStateStabilityChecks(); Console.WriteLine($"All {_checks} smart state stability checks passed."); return; }
        if (args.Contains("--music-state-stability-only")) { await MusicStateStabilityChecks(); Console.WriteLine($"All {_checks} music state stability checks passed."); return; }
        if (args.Contains("--usb-resume-latency-only")) { await UsbResumeLatencyChecks(); Console.WriteLine($"All {_checks} USB resume latency checks passed."); return; }
        if (args.Contains("--multi-brand-earbuds-only")) { await MultiBrandEarbudsChecks(); Console.WriteLine($"All {_checks} multi brand earbuds checks passed."); return; }
        if (args.Contains("--usb-media-state-only")) { await UsbMediaStateChecks(); Console.WriteLine($"All {_checks} USB media state checks passed."); return; }
        if (args.Contains("--charger-type-live")) { await ChargerTypeLiveCheck(); return; }
        if (args.Contains("--battery-sampling-latency-only")) { await BatterySamplingLatencyChecks(); await BatteryBroadcastCoalescingChecks(); Console.WriteLine($"All {_checks} battery sampling latency checks passed."); return; }
        if (args.Contains("--smart-charging-live")) { await SmartChargingLiveCheck(); return; }
        if (args.Contains("--messenger-activation-live") || args.Contains("--qq-activation-live"))
        {
            var client = args.Contains("--qq-activation-live") ? MessengerClient.QQ : MessengerClient.WeChat;
            bool opened = await Task.Run(() => alpha_island.Interop.MessengerWindowReader.TryOpen(client));
            Check(opened, "实际" + MessengerIdentity.Name(client) + "通过自身托盘动作打开");
            var processes = new alpha_island.Interop.MessengerWindowReader().ReadProcesses().Where(p => p.Client == client).Select(p => p.Id).ToHashSet();
            IntPtr foreground = IntPtr.Zero;
            bool focused = false;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                foreground = alpha_island.Interop.NativeMethods.GetForegroundWindow();
                alpha_island.Interop.NativeMethods.GetWindowThreadProcessId(foreground, out uint process);
                if (processes.Contains((int)process)) { focused = true; break; }
                await Task.Delay(60);
            }
            string type = alpha_island.Interop.MessengerWindowReader.ReadClass(foreground);
            Console.WriteLine("Restored foreground class: " + type);
            Check(focused && !type.Contains("TrayIconMessage", StringComparison.OrdinalIgnoreCase), "托盘动作完成后前台是" + MessengerIdentity.Name(client) + "正常窗口，未出现内部空白宿主");
            Console.WriteLine($"All {_checks} live messenger activation checks passed."); return;
        }
        if (args.Contains("--messenger-activation-only")) { MessengerActivationChecks(); MessengerTrayInvocationChecks(); Console.WriteLine($"All {_checks} messenger activation checks passed."); return; }
        if (args.Contains("--messenger-jump-only")) { MessengerWindowJumpChecks(); Console.WriteLine($"All {_checks} messenger window jump checks passed."); return; }
        if (args.Contains("--external-agent-only")) { await ExternalAgentChecks(); Console.WriteLine($"All {_checks} external agent checks passed."); return; }
        if (args.Contains("--agent-lifetime-only")) { await ExternalAgentLifetimeChecks(); Console.WriteLine($"All {_checks} agent lifetime checks passed."); return; }
        if (args.Contains("--volume-native-only")) { NativeAppVolumeChecks(); Console.WriteLine($"All {_checks} native application volume checks passed."); return; }
        if (args.Contains("--agent-rpc-fixture")) { await AgentRpcFixture(args[1]); return; }
        if (args.Contains("--cold-plugin-resource-check")){ColdPluginResourceChecks();Console.WriteLine($"All {_checks} cold WPF plugin checks passed.");return;}
        if (args.Contains("--traffic-producer")) { NativeTrafficProducer(); return; }
        if (args.Contains("--package-assets")) { PackageAssets(); return; }
        if (args.Contains("--background-window-live-check")) { BackgroundWindowLiveCheck(); return; }
        if (args.Contains("--shortcut-native-check"))
        {
            Exception? error = null;
            var thread = new Thread(() => { try { ShortcutNativeReceiverCheck(); } catch (Exception ex) { error = ex; } });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
            if (error != null) throw error;
            Console.WriteLine($"All {_checks} native shortcut checks passed."); return;
        }
        if (args.Contains("--messenger-jump-probe"))
        {
            var processes = new alpha_island.Interop.MessengerWindowReader().ReadProcesses().Where(p => p.Client == MessengerClient.WeChat);
            IntPtr selected = alpha_island.Interop.MessengerWindowReader.FindPrimaryWindow(MessengerClient.WeChat, processes.Select(p => p.Id).ToHashSet());
            bool found = selected != IntPtr.Zero;
            string type = alpha_island.Interop.MessengerWindowReader.ReadClass(selected);
            Console.WriteLine("Selected WeChat window class: " + type);
            Check(found && selected != IntPtr.Zero && !type.Contains("TrayIconMessageWindow", StringComparison.OrdinalIgnoreCase),
                "微信通知跳转不会选择内部托盘消息窗口");
            Console.WriteLine($"All {_checks} live WeChat target checks passed."); return;
        }
        if (args.Contains("--messenger-api-probe"))
        {
            var snapshot = await Task.Run(() => new alpha_island.Interop.MessengerWindowReader().Read(new HashSet<MessengerClient>(Enum.GetValues<MessengerClient>())));
            foreach (var client in Enum.GetValues<MessengerClient>())
                Console.WriteLine($"{MessengerIdentity.Name(client)}: processes={snapshot.Processes.Count(p => p.Client == client)}, unreadCounter={snapshot.Unread.ContainsKey(client)}, trayIcons={snapshot.TrayIcons.Count(i => i.Client == client)}, notificationPopups={snapshot.Popups.Count(p => p.Client == client)}");
            return;
        }
        if (args.Contains("--toast-api-probe"))
        {
            Console.WriteLine("Package identity: " + SystemToastService.HasPackageIdentity);
            try
            {
                var listener = Windows.UI.Notifications.Management.UserNotificationListener.Current;
                Console.WriteLine("Notification access: " + listener.GetAccessStatus());
                if (listener.GetAccessStatus() == Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed)
                {
                    var notifications = await listener.GetNotificationsAsync(Windows.UI.Notifications.NotificationKinds.Toast);
                    Console.WriteLine("Readable notification count: " + notifications.Count);
                    if (args.Contains("--logo"))
                    {
                        var sample = notifications.LastOrDefault(n => n.AppInfo.AppUserModelId == "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe");
                        if (sample != null)
                        {
                            try
                            {
                                var logo = sample.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(32, 32));
                                if (logo == null) Console.WriteLine("PowerShell AppDisplayInfo logo: unavailable");
                                else
                                {
                                    using var random = await logo.OpenReadAsync();
                                    Console.WriteLine($"PowerShell logo stream: {random.Size} bytes, position {random.Position}");
                                }
                            }
                            catch (Exception logoError) { Console.WriteLine("PowerShell logo: " + logoError.GetType().Name + ": 0x" + logoError.HResult.ToString("X8") + " " + logoError.Message); }
                            var shellLogo = await alpha_island.Interop.NotificationAppIcon.ReadAsync(sample.AppInfo.AppUserModelId);
                            Console.WriteLine($"Registered shell logo bytes: {shellLogo?.Length ?? 0}");
                            if (shellLogo != null)
                            {
                                Directory.CreateDirectory("bin/launch-previews");
                                File.WriteAllBytes("bin/launch-previews/windows-powershell-notification-logo.png", shellLogo);
                            }
                        }
                    }
                }
            }
            catch (Exception error) { Console.WriteLine(error.GetType().Name + ": 0x" + error.HResult.ToString("X8") + " " + error.Message); }
            return;
        }
        if (args.Contains("--notification-assets-live-check")) { await NotificationAssetLiveChecks(); return; }
        if (args.Contains("--toast-live-check")) { RealWindowsNotificationChecks(); return; }
        if (args.Contains("--desktop-hardware-probe")) { var privacy=new PrivacyIndicatorService();var usage=privacy.Sample();var mic=MicrophoneStateReader.Read();Console.WriteLine(JsonSerializer.Serialize(new {PrivacyAvailable=privacy.DegradedReason==null,UsageCount=usage.Count,MicrophoneAvailable=mic.Available,MicrophoneMuted=mic.Available?(bool?)mic.Muted:null}));return; }
        if (args.Contains("--read-browser-downloads")) { Console.WriteLine(JsonSerializer.Serialize(BrowserDownloadProbe.ScanWindows())); return; }
        if (args.Contains("--inspect-browser-partial")) { Console.WriteLine(JsonSerializer.Serialize(new { Owner = BrowserPartialOwner.Read(args[1]), Readings = BrowserDownloadProbe.Read() })); return; }
        if (args.Contains("--toast-popup-race-check")) { await ToastPopupRaceChecks(); return; }
        if (args.Contains("--toast-popup-control-check")) { await ToastPopupRaceChecks(nativeOnly: true); return; }
        if (args.Contains("--local-agent-handshake")) {await LocalAgentHandshakeChecks();Console.WriteLine($"All {_checks} local protocol handshake checks passed.");return;}
        if (args.Contains("--v42-lyrics-album")) { await V42LyricsAlbumChecks(); return; }
        if (args.Contains("--playback-order-only")) { await PlaybackOrderChecks(); Console.WriteLine($"All {_checks} playback order checks passed."); return; }
        if (args.Contains("--playback-order-latency-live")) { await PlaybackOrderLatencyLiveCheck(args.Contains("--long-readback")); return; }
        if (args.Contains("--v42-mode-live")) { await V42ModeLiveCheck(); return; }
        if (args.Contains("--v42-assets-live")) { V42NotificationAssetLiveCheck(); return; }
        if (args.Contains("--v42-media-trace")) { await V42MediaTrace(args.Contains("--exercise-pause")); return; }
        if (args.Contains("--inspect-media")) { await InspectMedia(); return; }
        if (args.Contains("--inspect-agent-windows"))
        {
            var windows = new AgentAppNavigationService.NativeAgentWindowAccess().ReadWindows();
            foreach (string provider in new[] { "codex", "antigravity", "deepseek-harness" })
                Console.WriteLine($"{provider}: matching open windows={windows.Count(w => AgentAppNavigationService.Matches(provider, w))}");
            return;
        }
        if (args.Contains("--inspect-steam"))
        {
            var monitor = new DownloadMonitorService(Path.Combine(AppContext.BaseDirectory, "no-browser-profile"));
            monitor.Sample();
            foreach (var task in monitor.Current.Where(t => t.Kind == DownloadSourceKind.Steam))
                System.Console.WriteLine($"{task.Source} | {task.Name} | {task.StatusText} | reliable progress: {task.HasReliableProgress} | running: {task.IsRunning}");
            if (monitor.DegradedReason != null) System.Console.WriteLine(monitor.DegradedReason);
            return;
        }
        string root = Path.Combine(Path.GetTempPath(), "alpha-island-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            if (args.Contains("--device-battery-enhancements-only")) { IslandUi(root, iconEntryProbe: "device-battery-enhancements"); Console.WriteLine($"All {_checks} battery enhancement checks passed."); return; }
            if (args.Contains("--card-wheel-only")) { IslandUi(root, iconEntryProbe: "card-wheel"); Console.WriteLine($"All {_checks} card wheel checks passed."); return; }
            if (args.Contains("--component-menu-input-only")) { IslandUi(root, iconEntryProbe: "component-menu-input"); Console.WriteLine($"All {_checks} component menu input checks passed."); return; }
            if (args.Contains("--launcher-input-only")) { IslandUi(root, iconEntryProbe: "launcher-input"); Console.WriteLine($"All {_checks} launcher input checks passed."); return; }
            if (args.Contains("--agent-startup-latency-only")) { IslandUi(root, iconEntryProbe: "agent-startup-latency"); Console.WriteLine($"All {_checks} agent startup latency checks passed."); return; }
            if (args.Contains("--agent-window-responsiveness-only")) { IslandUi(root, iconEntryProbe: "agent-window-responsiveness"); Console.WriteLine($"All {_checks} agent window responsiveness checks passed."); return; }
            if (args.Contains("--handoff-responsiveness-only")) { IslandUi(root, iconEntryProbe: "handoff-responsiveness"); Console.WriteLine($"All {_checks} handoff responsiveness checks passed."); return; }
            if (args.Contains("--assistant-reminders-only")) { AssistantReminderChecks(); IslandUi(root, iconEntryProbe: "assistant-reminders"); Console.WriteLine($"All {_checks} assistant reminder checks passed."); return; }
            if (args.Contains("--desktop-assistant-only")) { await DesktopToolChecks(root); DesktopLocalChecks(root); IslandUi(root, iconEntryProbe: "desktop-assistant"); Console.WriteLine($"All {_checks} desktop assistant checks passed."); return; }
            if (args.Contains("--clipboard-history-preferences-only")) { IslandUi(root, iconEntryProbe: "clipboard-history-preferences"); Console.WriteLine($"All {_checks} clipboard history preference checks passed."); return; }
            if (args.Contains("--ai-settings-layout-only")) { IslandUi(root, iconEntryProbe: "ai-settings-layout"); Console.WriteLine($"All {_checks} AI settings layout checks passed."); return; }
            if (args.Contains("--plugin-settings-surface-only")) { IslandUi(root, iconEntryProbe: "plugin-settings-surface"); Console.WriteLine($"All {_checks} plugin settings surface checks passed."); return; }
            if (args.Contains("--codex-status-recovery-only")) { IslandUi(root, iconEntryProbe: "codex-status-recovery"); Console.WriteLine($"All {_checks} Codex state recovery checks passed."); return; }
            if (args.Contains("--codex-terminal-only")) { await CodexTerminalChecks(root); Console.WriteLine($"All {_checks} Codex terminal checks passed."); return; }
            if (args.Contains("--island-zorder-only")) { IslandUi(root, iconEntryProbe: "island-zorder"); Console.WriteLine($"All {_checks} island z-order checks passed."); return; }
            if (args.Contains("--agent-session-identity-only")) { await AgentConversationStateChecks(root); Console.WriteLine($"All {_checks} agent identity checks passed."); return; }
            if (args.Contains("--browser-downloads-only")) { BrowserDownloadChecks(root); Console.WriteLine($"All {_checks} browser download checks passed."); return; }
            if (args.Contains("--notification-visual-stack-only")) { IslandUi(root, iconEntryProbe: "notification-visual-stack"); Console.WriteLine($"All {_checks} notification visual stack checks passed."); return; }
            if (args.Contains("--notification-stack-only")) { IslandUi(root, iconEntryProbe: "notification-stack"); Console.WriteLine($"All {_checks} notification stack checks passed."); return; }
            if (args.Contains("--notification-presentation-only")) { IslandUi(root, iconEntryProbe: "notification-presentation"); Console.WriteLine($"All {_checks} notification presentation checks passed."); return; }
            if (args.Contains("--downloads-regression-only")) { BrowserDownloadChecks(root); Downloads(root); DownloadIdleQueryChecks(root); IslandUi(root, iconEntryProbe: "browser-download"); Console.WriteLine($"All {_checks} download regression checks passed."); return; }
            if (args.Contains("--file-transfer-out-only")) { IslandUi(root, iconEntryProbe: "file-transfer-out"); Console.WriteLine($"All {_checks} file transfer export checks passed."); return; }
            if (args.Contains("--file-transfer-ole-live")) { IslandUi(root, iconEntryProbe: "file-transfer-ole"); Console.WriteLine($"All {_checks} native file transfer export checks passed."); return; }
            if (args.Contains("--volume-flash-baseline")) { IslandUi(root, iconEntryProbe: "volume-flash-baseline"); return; }
            if (args.Contains("--volume-flash-live")) { IslandUi(root, iconEntryProbe: "volume-flash-live"); Console.WriteLine($"All {_checks} volume flash checks passed."); return; }
            if (args.Contains("--volume-game-quiet-live")) { IslandUi(root, iconEntryProbe: "volume-game-quiet-live"); Console.WriteLine($"All {_checks} game volume feedback checks passed."); return; }
            if (args.Contains("--game-quiet-only")) { await GameActivityChecks(); IslandUi(root, iconEntryProbe: "game-quiet"); Console.WriteLine($"All {_checks} game quiet checks passed."); return; }
            if (args.Contains("--daily-traffic-only")) { DailyTrafficChecks(root); Console.WriteLine($"All {_checks} daily traffic checks passed."); return; }
            if (args.Contains("--settings-consolidation-only")) { IslandUi(root, iconEntryProbe: "settings-consolidation"); Console.WriteLine($"All {_checks} consolidated settings checks passed."); return; }
            if (args.Contains("--keyboard-battery-live")) { IslandUi(root, iconEntryProbe: "keyboard-battery-live"); Console.WriteLine($"All {_checks} live keyboard battery checks passed."); return; }
            if (args.Contains("--keyboard-spectrum-only")) { IslandUi(root, iconEntryProbe: "keyboard-spectrum"); Console.WriteLine($"All {_checks} keyboard and spectrum checks passed."); return; }
            if (args.Contains("--resident-memory-only")) { IslandUi(root, iconEntryProbe: "resident-memory"); Console.WriteLine($"All {_checks} resident memory checks passed."); return; }
            if (args.Contains("--settings-icon-media-pin-only")) { IslandUi(root, iconEntryProbe: "settings-icon-media-pin"); Console.WriteLine($"All {_checks} settings icons and media pin checks passed."); return; }
            if (args.Contains("--media-peripheral-lifecycle-only")) { IslandUi(root, iconEntryProbe: "media-peripheral-lifecycle"); Console.WriteLine($"All {_checks} media and peripheral lifecycle checks passed."); return; }
            if (args.Contains("--game-resource-budget-only")) { IslandUi(root, iconEntryProbe: "game-resource-budget"); Console.WriteLine($"All {_checks} game resource budget checks passed."); return; }
            if (args.Contains("--gaming-memory-only")) { IslandUi(root, iconEntryProbe: "gaming-memory"); Console.WriteLine($"All {_checks} game memory checks passed."); return; }
            if (args.Contains("--speech-setup-only")) { await SpeechSetupProtocolChecks(); IslandUi(root, iconEntryProbe: "speech-setup"); Console.WriteLine($"All {_checks} speech setup checks passed."); return; }
            if (args.Contains("--boot-timing-only")) { BootTimingPolicyChecks(); Console.WriteLine($"All {_checks} boot timing checks passed."); return; }
            if (args.Contains("--boot-timing-live")) { BootTimingLiveReadChecks(); Console.WriteLine($"All {_checks} boot timing live checks passed."); return; }
            if (args.Contains("--notification-logo-fit-only")) { IslandUi(root, iconEntryProbe: "notification-logo-fit"); Console.WriteLine($"All {_checks} logo fit checks passed."); return; }
            if (args.Contains("--short-hints-only")) { IslandUi(root, iconEntryProbe: "short-hints"); Console.WriteLine($"All {_checks} short hint checks passed."); return; }
            if (args.Contains("--hint-bounds-only")) { IslandUi(root, iconEntryProbe: "hint-bounds"); Console.WriteLine($"All {_checks} hint bounds checks passed."); return; }
            if (args.Contains("--gaming-notifications-only")) { IslandUi(root, iconEntryProbe: "gaming-notifications"); Console.WriteLine($"All {_checks} game notification checks passed."); return; }
            if (args.Contains("--game-compact-lock-only")) { IslandUi(root, iconEntryProbe: "game-compact-lock"); Console.WriteLine($"All {_checks} game compact lock checks passed."); return; }
            if (args.Contains("--agent-avatar-only")) { IslandUi(root, iconEntryProbe: "agent-avatar"); Console.WriteLine($"All {_checks} agent avatar checks passed."); return; }
            if (args.Contains("--agent-avatar-routing-only")) { IslandUi(root, iconEntryProbe: "agent-avatar-routing"); Console.WriteLine($"All {_checks} agent avatar routing checks passed."); return; }
            if (args.Contains("--agent-avatar-loop-only")) { IslandUi(root, iconEntryProbe: "agent-avatar-loop"); Console.WriteLine($"All {_checks} agent avatar loop checks passed."); return; }
            if (args.Contains("--plugin-button-stability-only")) { IslandUi(root, iconEntryProbe: "plugin-button-stability"); Console.WriteLine($"All {_checks} plugin button stability checks passed."); return; }
            if (args.Contains("--theme-cohesion-only")) { IslandUi(root, iconEntryProbe: "theme-cohesion"); Console.WriteLine($"All {_checks} theme cohesion checks passed."); return; }
            if (args.Contains("--satellite-material-only")) { IslandUi(root, iconEntryProbe: "satellite-material"); Console.WriteLine($"All {_checks} satellite material checks passed."); return; }
            if (args.Contains("--greeting-notification-sound-only")) { IslandUi(root, iconEntryProbe: "greeting-notification-sound"); Console.WriteLine($"All {_checks} greeting portrait and notification sound checks passed."); return; }
            if (args.Contains("--spectrum-visibility-only")) { IslandUi(root, iconEntryProbe: "spectrum-visibility"); Console.WriteLine($"All {_checks} spectrum visibility checks passed."); return; }
            if (args.Contains("--amiya-handle-only")) { IslandUi(root, iconEntryProbe: "amiya-handle"); Console.WriteLine($"All {_checks} Amiya and adaptive handle checks passed."); return; }
            if (args.Contains("--greeting-once-only")) { IslandUi(root, iconEntryProbe: "greeting-once"); Console.WriteLine($"All {_checks} single-play greeting checks passed."); return; }
            if (args.Contains("--caption-modes-only")) { IslandUi(root, iconEntryProbe: "caption-modes"); Console.WriteLine($"All {_checks} independent caption mode checks passed."); return; }
            if (args.Contains("--agent-completion-visual-only")) { IslandUi(root, iconEntryProbe: "agent-completion-visual"); Console.WriteLine($"All {_checks} completion and satellite checks passed."); return; }
            if (args.Contains("--agent-multi-session-only")) { IslandUi(root, iconEntryProbe: "agent-multi-session"); Console.WriteLine($"All {_checks} multi-session agent checks passed."); return; }
            if (args.Contains("--agent-background-resume-only")) { IslandUi(root, iconEntryProbe: "agent-background-resume"); Console.WriteLine($"All {_checks} agent background resume checks passed."); return; }
            if (args.Contains("--background-agent-visibility-only")) { IslandUi(root, iconEntryProbe: "background-agent"); Console.WriteLine($"All {_checks} background agent visibility checks passed."); return; }
            if (args.Contains("--background-music-visibility-only")) { IslandUi(root, iconEntryProbe: "background-music"); Console.WriteLine($"All {_checks} background music visibility checks passed."); return; }
            if (args.Contains("--messenger-direct-open-only")) { IslandUi(root, iconEntryProbe: "messenger-direct-open"); Console.WriteLine($"All {_checks} messenger direct open checks passed."); return; }
            if (args.Contains("--messenger-notifications-only")) { MessengerNotificationStateChecks(); IslandUi(root, iconEntryProbe: "messenger-notifications"); Console.WriteLine($"All {_checks} QQ and WeChat notification checks passed."); return; }
            if (args.Contains("--status-split-only")) { IslandUi(root, iconEntryProbe: "status-split"); Console.WriteLine($"All {_checks} status split checks passed."); return; }
            if (args.Contains("--power-frame-stall-only")) { IslandUi(root, iconEntryProbe: "power-frame-stall"); Console.WriteLine($"All {_checks} power frame stall checks passed."); return; }
            if (args.Contains("--charger-type-only")) { await ChargerTypeChecks(); IslandUi(root, iconEntryProbe: "charger-type"); Console.WriteLine($"All {_checks} charger type checks passed."); return; }
            if (args.Contains("--smart-charging-only")) { await SmartChargingReaderChecks(); IslandUi(root, iconEntryProbe: "smart-charging"); Console.WriteLine($"All {_checks} smart charging checks passed."); return; }
            if (args.Contains("--energy-saver-only")) { IslandUi(root, iconEntryProbe: "energy-saver"); Console.WriteLine($"All {_checks} energy saver checks passed."); return; }
            if (args.Contains("--power-island-lifecycle-only")) { IslandUi(root, iconEntryProbe: "power-island-lifecycle"); Console.WriteLine($"All {_checks} power island lifecycle checks passed."); return; }
            if (args.Contains("--music-layout-preview-only")) { IslandUi(root, iconEntryProbe: "music-layout-preview"); Console.WriteLine("Music layout previews saved."); return; }
            if (args.Contains("--battery-text-layout-preview-only")) { IslandUi(root, iconEntryProbe: "battery-text-layout-preview"); Console.WriteLine("Battery text layout previews saved."); return; }
            if (args.Contains("--bluetooth-transition-only")) { IslandUi(root, iconEntryProbe: "bluetooth-transition"); Console.WriteLine($"All {_checks} Bluetooth transition checks passed."); return; }
            if (args.Contains("--charging-island-preview-only")) { IslandUi(root, iconEntryProbe: "charging-island-preview"); Console.WriteLine($"All {_checks} charging island preview checks passed."); return; }
            if (args.Contains("--charging-corners-only")) { IslandUi(root, iconEntryProbe: "charging-corners"); Console.WriteLine($"All {_checks} charging compact corner checks passed."); return; }
            if (args.Contains("--all-island-corners-only")) { IslandUi(root, iconEntryProbe: "all-island-corners"); Console.WriteLine($"All {_checks} compact island outline checks passed."); return; }
            if (args.Contains("--network-curves-only")) { IslandUi(root, iconEntryProbe: "network-curves"); Console.WriteLine($"All {_checks} network menu and curve checks passed."); return; }
            if (args.Contains("--screen-tools-theme-only")) { IslandUi(root, iconEntryProbe: "screen-tools-theme"); Console.WriteLine($"All {_checks} screen tools theme checks passed."); return; }
            if (args.Contains("--media-traffic-only")) { MediaAndTrafficChecks(root); IslandUi(root, iconEntryProbe: "media-events"); Console.WriteLine($"All {_checks} media, traffic and download checks passed."); return; }
            if (args.Contains("--bundled-plugin-isolation-only")) { IslandUi(root, iconEntryProbe: "bundled-isolation"); Console.WriteLine($"All {_checks} bundled process checks passed."); return; }
            if (args.Contains("--cross-machine-compatibility-only")) { CrossMachineCompatibilityChecks(root); IslandUi(root, iconEntryProbe: "small-screen-compatibility"); Console.WriteLine($"All {_checks} cross-machine compatibility checks passed."); return; }
            if (args.Contains("--api-model-avatar-only")) { IslandUi(root, iconEntryProbe: "api-model-avatar"); Console.WriteLine($"All {_checks} API model avatar checks passed."); return; }
            if (args.Contains("--runtime-reliability-only")) { await RuntimeReliabilityChecks(root); Console.WriteLine($"All {_checks} runtime reliability checks passed."); return; }
            if (args.Contains("--interaction-feedback-only")) { IslandUi(root, iconEntryProbe: "interaction-feedback"); Console.WriteLine($"All {_checks} interaction feedback checks passed."); return; }
            if (args.Contains("--greeting-handoff-only")) { IslandUi(root, iconEntryProbe: "greeting-handoff"); Console.WriteLine($"All {_checks} greeting handoff checks passed."); return; }
            if (args.Contains("--greeting-media-only")) { IslandUi(root, iconEntryProbe: "greeting-media"); Console.WriteLine($"All {_checks} greeting media checks passed."); return; }
            if (args.Contains("--maintenance-only")) { IslandUi(root, iconEntryProbe: "maintenance"); Console.WriteLine($"All {_checks} maintenance checks passed."); return; }
            if (args.Contains("--plugin-isolation-only")) { IslandUi(root, iconEntryProbe: "plugin-isolation"); Console.WriteLine($"All {_checks} process isolation checks passed."); return; }
            if (args.Contains("--plugin-lifecycle-only")) { IslandUi(root, iconEntryProbe: "plugin-lifecycle"); Console.WriteLine($"All {_checks} plugin lifecycle checks passed."); return; }
            if (args.Contains("--optimization-ui-only")) { IslandUi(root, iconEntryProbe: "optimization"); Console.WriteLine($"All {_checks} optimization UI checks passed."); return; }
            if (args.Contains("--liquid-glass-ui-only")) { IslandUi(root, iconEntryProbe: "liquid-glass"); Console.WriteLine($"All {_checks} liquid glass UI checks passed."); return; }
            if (args.Contains("--productivity-corners-only")) { IslandUi(root, iconEntryProbe: "productivity-corners"); Console.WriteLine($"All {_checks} productivity corner checks passed."); return; }
            if (args.Contains("--liquid-glass-live-only")) { IslandUi(root, iconEntryProbe: "liquid-glass-live"); Console.WriteLine($"All {_checks} live liquid glass checks passed."); return; }
            if (args.Contains("--battery-ripple-only")) { IslandUi(root, iconEntryProbe: "battery-ripple"); Console.WriteLine($"All {_checks} battery ripple checks passed."); return; }
            if (args.Contains("--battery-connector-only")) { IslandUi(root, iconEntryProbe: "battery-connector"); Console.WriteLine($"All {_checks} battery connector checks passed."); return; }
            if (args.Contains("--welcome-power-preview-only")) { IslandUi(root, iconEntryProbe: "welcome-power-preview"); Console.WriteLine($"All {_checks} welcome and power preview checks passed."); return; }
            if (args.Contains("--compact-status-alerts-only")) { IslandUi(root, iconEntryProbe: "compact-status-alerts"); Console.WriteLine($"All {_checks} compact status alert checks passed."); return; }
            if (args.Contains("--v41-issues-only"))
            {
                IslandUi(root, iconEntryProbe: "v41-issues");
                Console.WriteLine($"All {_checks} current issue checks passed."); return;
            }
            if (args.Contains("--redesigned-icons-only"))
            {
                IslandUi(root, iconEntryProbe: "redesign");
                Console.WriteLine($"All {_checks} redesigned icon checks passed.");
                return;
            }
            if (args.Contains("--sidebubble-icon-only") || args.Contains("--icon-actions-only"))
            {
                IslandUi(root, iconEntryProbe: args.Contains("--sidebubble-icon-only") ? "sidebubble" : "actions");
                Console.WriteLine($"All {_checks} action entry checks passed.");
                return;
            }
            if (args.Contains("--icon-presentation-only"))
            {
                IslandUi(root, iconPresentationOnly: true);
                Console.WriteLine($"All {_checks} icon presentation checks passed.");
                return;
            }
            if (args.Contains("--short-icons-only") || args.Contains("--semantic-icons-only"))
            {
                IslandUi(root, iconsOnly: true);
                Console.WriteLine($"All {_checks} short icon checks passed."); return;
            }
            if (args.Contains("--page-restore-only"))
            {
                IslandUi(root, pageRestoreOnly: true);
                Console.WriteLine($"All {_checks} page restore checks passed."); return;
            }
            if (args.Contains("--notification-content-only"))
            {
                IslandUi(root, notificationContentOnly: true);
                Console.WriteLine($"All {_checks} notification content checks passed."); return;
            }
            if (args.Contains("--notification-full-only"))
            {
                IslandUi(root, iconEntryProbe: "notification-full");
                Console.WriteLine($"All {_checks} notification full content checks passed."); return;
            }
            if (args.Contains("--theme-controls-only"))
            {
                IslandUi(root, iconEntryProbe: "theme-controls");
                Console.WriteLine($"All {_checks} theme controls checks passed."); return;
            }
            if (args.Contains("--volume-wheel-only"))
            {
                IslandUi(root, iconEntryProbe: "volume-wheel");
                Console.WriteLine($"All {_checks} volume wheel checks passed."); return;
            }
            if (args.Contains("--volume-mixer-only"))
            {
                IslandUi(root, iconEntryProbe: "volume-mixer");
                Console.WriteLine($"All {_checks} volume mixer checks passed."); return;
            }
            if (args.Contains("--volume-growth-only"))
            {
                IslandUi(root, iconEntryProbe: "volume-growth");
                Console.WriteLine($"All {_checks} volume growth checks passed."); return;
            }
            if (args.Contains("--outside-card-only"))
            {
                IslandUi(root, iconEntryProbe: "outside-card");
                Console.WriteLine($"All {_checks} outside card checks passed."); return;
            }
            if (args.Contains("--clipboard-domains-only"))
            {
                ClipboardDomainChecks(); IslandUi(root, iconEntryProbe: "clipboard-domains");
                Console.WriteLine($"All {_checks} clipboard domain checks passed."); return;
            }
            if (args.Contains("--agent-foreground-click-only")) { IslandUi(root, iconEntryProbe: "agent-foreground-click"); Console.WriteLine($"All {_checks} agent foreground click checks passed."); return; }
            if (args.Contains("--agent-window-handoff-only"))
            {
                IslandUi(root, iconEntryProbe: "agent-window-handoff"); Console.WriteLine($"All {_checks} agent window handoff checks passed."); return;
            }
            if (args.Contains("--agent-handoff-only"))
            {
                IslandUi(root, iconEntryProbe: "agent-handoff"); Console.WriteLine($"All {_checks} agent handoff checks passed."); return;
            }
            if (args.Contains("--agent-navigation-only"))
            {
                IslandUi(root, iconEntryProbe: "agent-navigation");
                Console.WriteLine($"All {_checks} agent navigation checks passed."); return;
            }
            if (args.Contains("--startup-morph-only"))
            {
                IslandUi(root, iconEntryProbe: "startup-morph");
                Console.WriteLine($"All {_checks} startup morph checks passed."); return;
            }
            if (args.Contains("--tray-popup-only")) { IslandUi(root, iconEntryProbe: "tray-popup"); Console.WriteLine($"All {_checks} tray popup checks passed."); return; }
            if (args.Contains("--island-corners-only")) { IslandUi(root, iconEntryProbe: "island-corners"); Console.WriteLine($"All {_checks} island corner checks passed."); return; }
            if (args.Contains("--backdrop-opacity-only")) { IslandUi(root, iconEntryProbe: "backdrop-opacity"); Console.WriteLine($"All {_checks} native backdrop opacity checks passed."); return; }
            if (args.Contains("--component-native-hit-only")) { IslandUi(root, iconEntryProbe: "component-native-hit"); Console.WriteLine($"All {_checks} component native hit checks passed."); return; }
            if (args.Contains("--component-hit-area-only"))
            {
                IslandUi(root, componentHitAreaOnly: true);
                Console.WriteLine($"All {_checks} component hit area checks passed."); return;
            }
            if (args.Contains("--local-agents-only"))
            {
                await LocalAgentProtocolChecks(root);
                Console.WriteLine($"All {_checks} local agent checks passed."); return;
            }
            if (args.Contains("--notification-refinement-only"))
            {
                IslandUi(root, iconEntryProbe: "notification-refinement");
                Console.WriteLine($"All {_checks} notification refinement checks passed."); return;
            }
            if (args.Contains("--api-persona-only"))
            {
                await ApiPersonaDeliveryChecks(root);
                IslandUi(root, iconEntryProbe: "api-persona");
                Console.WriteLine($"All {_checks} API persona checks passed."); return;
            }
            if (args.Contains("--island-information-only"))
            {
                await IslandInformationAgentChecks();
                IslandUi(root, iconEntryProbe: "island-information");
                Console.WriteLine($"All {_checks} island information checks passed."); return;
            }
            if (args.Contains("--api-chat-input-only"))
            {
                IslandUi(root, iconEntryProbe: "api-chat-input");
                Console.WriteLine($"All {_checks} API chat input checks passed."); return;
            }
            if (args.Contains("--ai-conversations-only"))
            {
                AiConversationStoreChecks(root);
                IslandUi(root, aiConversationsOnly: true);
                Console.WriteLine($"All {_checks} AI conversation checks passed."); return;
            }
            if (args.Contains("--portable-setup-only"))
            {
                PortableSetupChecks(root);
                Console.WriteLine($"All {_checks} portable setup checks passed."); return;
            }
            if (args.Contains("--performance-only"))
            {
                PerformanceAllocationChecks();
                DownloadIdleQueryChecks(root);
                Downloads(root);
                IslandUi(root, performanceOnly: true);
                System.Console.WriteLine($"All {_checks} performance checks passed.");
                return;
            }
            if (args.Contains("--agent-ui-only"))
            {
                IslandUi(root, agentOnly: true);
                System.Console.WriteLine($"All {_checks} agent interaction checks passed.");
                return;
            }
            if (args.Contains("--external-translation-only"))
            {
                await ShortcutSimulationChecks(root);
                IslandUi(root, externalTranslationOnly: true);
                Console.WriteLine($"All {_checks} external translation checks passed."); return;
            }
            if (args.Contains("--action-icon-probe"))
            {
                ActionIconFeasibilityChecks();
                System.Console.WriteLine($"All {_checks} action icon feasibility checks passed.");
                return;
            }
            if (args.Contains("--earbuds-plugin-only"))
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) throw new PlatformNotSupportedException("耳机插件需要 Windows 10 2004 或以上");
                await EarbudsPluginChecks(root);
                System.Console.WriteLine($"All {_checks} earbud plugin checks passed.");
                return;
            }
            if (args.Contains("--cards-focus-only"))
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) throw new PlatformNotSupportedException();
                PomodoroConfigurationChecks();
                IslandUi(root, focusOnly: true);
                System.Console.WriteLine($"All {_checks} card pin and pomodoro checks passed.");
                return;
            }
            if (args.Contains("--vivo-ip-only"))
            {
                VivoIpChecks(root); Console.WriteLine($"All {_checks} vivo IP checks passed."); return;
            }
            if (args.Contains("--vivo-plugin-only"))
            {
                VivoPluginReaderChecks(root);
                IslandUi(root, vivoOnly: true);
                System.Console.WriteLine($"All {_checks} vivo plugin checks passed.");
                return;
            }
            if (args.Contains("--plugins-uninstall-only"))
            {
                IslandUi(root, uninstallOnly: true);
                System.Console.WriteLine($"All {_checks} plugin uninstall checks passed.");
                return;
            }
            if (args.Contains("--device-battery-only"))
            {
                IslandUi(root, batteryOnly: true);
                System.Console.WriteLine($"All {_checks} device battery checks passed.");
                return;
            }
            if (args.Contains("--cover-clipboard-only"))
            {
                NativeClipboardReadChecks();
                IslandUi(root, coverClipboardOnly: true);
                System.Console.WriteLine($"All {_checks} cover and clipboard checks passed.");
                return;
            }
            if (args.Contains("--launch-links-only"))
            {
                Directory.CreateDirectory("bin/launch-previews");
                LaunchAndLinksPolicyChecks(root);
                IslandUi(root, launchLinksOnly: true);
                Console.WriteLine($"All {_checks} startup and web link checks passed.");
                return;
            }
            if (args.Contains("--notification-startup-only"))
            {
                DesktopNotifications();
                IslandUi(root, notificationStartupOnly: true);
                Console.WriteLine($"All {_checks} notification and boot popup checks passed.");
                return;
            }
            if (args.Contains("--background-window-only"))
            {
                IslandUi(root, backgroundWindowOnly: true);
                Console.WriteLine($"All {_checks} background window checks passed.");
                return;
            }
            if (args.Contains("--weather-only"))
            {
                await Weather(root);
                await WeatherReminderChecks(root);
                IslandUi(root, weatherOnly: true);
                Console.WriteLine($"All {_checks} weather checks passed.");
                return;
            }
            if (args.Contains("--weather-live-check"))
            {
                await WeatherLiveChecks(root);
                Console.WriteLine($"All {_checks} live weather checks passed.");
                return;
            }
            if (args.Contains("--agent-media-ui-only"))
            {
                IslandUi(root, agentOnly: true, pausedMedia: true);
                System.Console.WriteLine($"All {_checks} agent and media interaction checks passed.");
                return;
            }
            if (args.Contains("--paused-media-only"))
            {
                IslandUi(root, pausedOnly: true);
                System.Console.WriteLine($"All {_checks} paused media checks passed.");
                return;
            }
            if (args.Contains("--media-handoff-only"))
            {
                MediaHandoffPolicyChecks();
                IslandUi(root, handoffOnly: true);
                System.Console.WriteLine($"All {_checks} media window handoff checks passed.");
                return;
            }
            if (args.Contains("--media-sync-only"))
            {
                await MediaSynchronizationChecks();
                IslandUi(root, pausedOnly: true);
                System.Console.WriteLine($"All {_checks} media synchronization checks passed.");
                return;
            }
            if (args.Contains("--cpu-only"))
            {
                CpuSamplingPolicyChecks();
                await CpuLiveSamplingChecks();
                IslandUi(root, cpuOnly: true);
                System.Console.WriteLine($"All {_checks} CPU sampling checks passed.");
                return;
            }
            if (args.Contains("--hardware-power-only"))
            {
                StatusDisplayPolicyChecks();
                await HardwarePowerArchitectureChecks();
                Console.WriteLine($"All {_checks} native hardware power checks passed.");
                return;
            }
            if (args.Contains("--status-display-only"))
            {
                StatusDisplayPolicyChecks();
                await HardwarePowerLiveChecks();
                IslandUi(root, statusOnly: true);
                System.Console.WriteLine($"All {_checks} status display and wireless checks passed.");
                return;
            }
            if (args.Contains("--ai-config-only"))
            {
                AiConfigurationChecks(root);
                await AiIntegrationChecks();
                await AiModelConnectionChecks();
                IslandUi(root, aiConfigOnly: true);
                System.Console.WriteLine($"All {_checks} AI and configuration checks passed.");
                return;
            }
            if (args.Contains("--settings-appearance-only"))
            {
                IslandUi(root, settingsAppearanceOnly: true);
                Console.WriteLine($"All {_checks} settings appearance checks passed."); return;
            }
            if (args.Contains("--volume-flyout-only") || args.Contains("--volume-flyout-live"))
            {
                IslandUi(root, iconEntryProbe: args.Contains("--volume-flyout-live") ? "volume-flyout-live" : "volume-flyout");
                Console.WriteLine($"All {_checks} native volume flyout checks passed."); return;
            }
            if (args.Contains("--native-toast-banners-only"))
            {
                IslandUi(root, nativeBannersOnly: true);
                Console.WriteLine($"All {_checks} native notification banner checks passed."); return;
            }
            Notifications();
            SatelliteCycles();
            PinDismissal();
            NetworkTelemetry();
            MediaAndTrafficChecks(root);
            MediaHandoffPolicyChecks();
            await MediaSynchronizationChecks();
            await PlaybackOrderChecks();
            DesktopNotifications();
            CpuSamplingPolicyChecks();
            await ShortcutSimulationChecks(root);
            NativeClipboardReadChecks();
            IslandUi(root);
            Storage(root);
            AiConfigurationChecks(root);
            await LocalAgentProtocolChecks(root);
            await AiIntegrationChecks();
            await AiModelConnectionChecks();
            Conversations();
            InputsAndPrivacy();
            Downloads(root);
            VivoPluginReaderChecks(root);
            await Weather(root);
            FocusAndQr();
            await Ocr();
            RenderTools();
            await Approvals();
            await ExternalAgentChecks();
            await ExternalAgentLifetimeChecks();
            if (args.Contains("--online-weather"))
            {
                using var online = new WeatherService(cachePath: Path.Combine(root, "online-weather.json"));
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var cities = await online.SearchAsync("北京", limit.Token);
                Check(cities.Count > 0, "在线城市搜索返回北京候选");
                var actual = await online.GetAsync(WeatherCity.Beijing, limit.Token);
                Check(double.IsFinite(actual.Temperature), "在线天气接口返回有效温度");
            }
            System.Console.WriteLine($"All {_checks} regression checks passed.");
        }
        finally
        {
            // 可回收插件上下文的 Unload 是协作式的；先回收已结束的 UI 测试引用，释放 Windows DLL 映射。
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Notifications()
    {
        var hub = new NotificationHub();
        hub.Register(new("volume", 80));
        hub.Register(new("approval", 90, RequiresAttention: true));
        hub.Request(IslandNotification.Seconds("volume", 30));
        hub.Request(IslandNotification.Seconds("approval", 30));
        foreach (string preferred in new[] { "volume", "music", "approval" })
        {
            hub.PreferredWinnerKey = preferred;
            Check(hub.ComputePlan(IslandState.Pill, false).WinnerKey == "approval", "审批优先于首选 " + preferred);
        }
        hub.Hide("approval");
        hub.PreferredWinnerKey = "volume";
        Check(hub.ComputePlan(IslandState.Pill, false).WinnerKey == "volume", "审批结束后恢复普通通知");
        AgentStatus.Set(AgentRunState.ToolWait, "工具审批", owner: "approval");
        AgentStatus.Set(AgentRunState.Streaming, "回答", owner: "ai");
        AgentStatus.Reset(owner: "ai");
        Check(AgentStatus.State == AgentRunState.ToolWait, "AI 完成保留审批状态");
        AgentStatus.Set(AgentRunState.Thinking, owner: "ai");
        AgentStatus.Reset(owner: "approval");
        Check(AgentStatus.State == AgentRunState.Thinking, "审批结束恢复进行中的 AI 状态");
        AgentStatus.Reset(owner: "ai");

        hub.Register(new("download", 66));
        hub.Register(new("netspeed", 64));
        var progress = IslandNotification.Refreshing("download", 30);
        Check(hub.ShouldPromoteToMain(progress), "首次下载通知可接管主岛");
        hub.Request(progress);
        foreach (string preferred in new[] { "music", "volume", "download", "music", "volume" })
        {
            hub.PreferredWinnerKey = preferred;
            bool keepSelection = !hub.ShouldPromoteToMain(progress);
            hub.Request(progress);
            Check(keepSelection && hub.ComputePlan(IslandState.Pill, false).WinnerKey == (preferred == "music" ? null : preferred),
                "下载刷新保留双岛切换结果 " + preferred);
        }
        hub.Request(IslandNotification.Refreshing("netspeed", 30));
        Check(!hub.ShouldPromoteToMain(IslandNotification.Refreshing("netspeed", 30)), "持续网速更新不抢回主岛");
        Check(hub.ShouldPromoteToMain(IslandNotification.Seconds("download", 6)), "下载完成事件仍可主动提示");
        hub.Hide("download");
        Check(hub.ShouldPromoteToMain(progress), "下载重新开始时恢复首次通知");
    }

    private static void SatelliteCycles()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var root = new System.Windows.Controls.Grid();
                System.Windows.Controls.Border MakeOrb(int left)
                {
                    var transforms = new System.Windows.Media.TransformGroup();
                    transforms.Children.Add(new System.Windows.Media.ScaleTransform());
                    transforms.Children.Add(new System.Windows.Media.TranslateTransform());
                    var result = new System.Windows.Controls.Border
                    {
                        Width = 36, Height = 36, Background = System.Windows.Media.Brushes.Black,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Left, VerticalAlignment = System.Windows.VerticalAlignment.Top,
                        Margin = new System.Windows.Thickness(left, 20, 0, 0), RenderTransform = transforms
                    };
                    root.Children.Add(result);
                    return result;
                }
                var orb = MakeOrb(300);
                var other = MakeOrb(500);
                using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Alpha Island animation regression")
                { Width = 800, Height = 100, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
                source.RootVisual = root;
                bool Clickable(System.Windows.Controls.Border view)
                {
                    root.UpdateLayout();
                    var point = view.TranslatePoint(new System.Windows.Point(18, 18), root);
                    return view.Visibility == System.Windows.Visibility.Visible && view.Opacity > 0.99 && view.IsHitTestVisible
                        && ReferenceEquals(root.InputHitTest(point), view);
                }

                alpha_island.Animation.SatelliteOrbAnimator.Show(orb);
                alpha_island.Animation.SatelliteOrbAnimator.Hide(other, immediate: true);
                alpha_island.Animation.SatelliteOrbAnimator.Hide(orb);
                PumpAnimations(350);
                Check(orb.Visibility == System.Windows.Visibility.Collapsed && !orb.HasAnimatedProperties, "收起动画结束清除透明度时钟");
                alpha_island.Animation.SatelliteOrbAnimator.Show(orb);
                Check(Clickable(orb), "双岛切换后通知小岛恢复可见并命中点击");

                bool cyclesOk = true;
                for (int i = 0; i < 10; i++)
                {
                    alpha_island.Animation.SatelliteOrbAnimator.Hide(orb);
                    alpha_island.Animation.SatelliteOrbAnimator.Show(other);
                    PumpAnimations(250);
                    cyclesOk &= Clickable(other);
                    alpha_island.Animation.SatelliteOrbAnimator.Hide(other);
                    alpha_island.Animation.SatelliteOrbAnimator.Show(orb);
                    PumpAnimations(250);
                    cyclesOk &= Clickable(orb);
                }
                Check(cyclesOk, "双岛连续往返切换十轮均可点击");

                alpha_island.Animation.SatelliteOrbAnimator.Hide(orb);
                PumpAnimations(60);
                alpha_island.Animation.SatelliteOrbAnimator.Show(orb);
                PumpAnimations(350);
                Check(Clickable(orb), "快速切回取消旧淡出且不会被旧回调隐藏");

                alpha_island.Animation.SatelliteOrbAnimator.Collapse(orb);
                PumpAnimations(60);
                alpha_island.Animation.SatelliteOrbAnimator.Show(orb);
                PumpAnimations(350);
                var group = (System.Windows.Media.TransformGroup)orb.RenderTransform;
                var scale = group.Children.OfType<System.Windows.Media.ScaleTransform>().Single();
                var translate = group.Children.OfType<System.Windows.Media.TranslateTransform>().Single();
                Check(Clickable(orb) && scale.ScaleX == 1 && scale.ScaleY == 1 && translate.Y == 0 && !scale.HasAnimatedProperties && !translate.HasAnimatedProperties,
                    "收回途中重新展开恢复大小位移与点击区域");

                alpha_island.Animation.SatelliteOrbAnimator.Hide(orb);
                PumpAnimations(120);
                alpha_island.Animation.SatelliteOrbAnimator.Hide(orb);
                PumpAnimations(180);
                Check(orb.Visibility == System.Windows.Visibility.Collapsed, "同步刷新不会重启正在收起的动画");
                alpha_island.Animation.SatelliteOrbAnimator.Show(orb);
                alpha_island.Animation.SatelliteOrbAnimator.Hide(orb, immediate: true);
                alpha_island.Animation.SatelliteOrbAnimator.Show(orb);
                Check(Clickable(orb), "减弱动效立即切换后仍可切回");
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void PumpAnimations(int milliseconds)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void PinDismissal()
    {
        var hub = new NotificationHub();
        hub.Register(new("download", 66)); hub.Register(new("netspeed", 64));
        hub.Request(IslandNotification.Refreshing("download", 30));
        hub.SetPinned("download", true);
        hub.Request(IslandNotification.Refreshing("download", 6)); // 置顶期间仍有刷新写入。
        hub.SetPinned("netspeed", true);
        hub.SetPinned("download", false);
        Check(!hub.IsActive("download") && hub.IsPinned("netspeed") && hub.IsActive("netspeed"), "取消一个置顶岛只收起该岛");
        Check(!hub.Request(IslandNotification.Refreshing("download", 6)) && !hub.ShouldPromoteToMain(IslandNotification.Refreshing("download", 6)),
            "取消置顶后后台续期不能重新弹出");
        hub.SetPinned("download", true);
        Check(hub.IsPinned("download") && hub.IsActive("download"), "明确重新置顶恢复已收起岛");
        hub.SetPinned("download", false);
        hub.Hide("download");
        Check(hub.Request(IslandNotification.Refreshing("download", 6)), "状态结束后下一次下载可正常通知");
        hub.SetPinned("download", true); hub.SetPinned("download", false);
        Check(hub.Request(IslandNotification.Seconds("download", 6)), "取消置顶后下载完成等新事件仍能提示");
        hub.Register(new("approval", 90, ForceActive: () => true, RequiresAttention: true));
        hub.Request(IslandNotification.Seconds("approval", 30));
        hub.SetPinned("approval", true); hub.SetPinned("approval", false);
        Check(hub.IsActive("approval") && hub.ComputePlan(IslandState.Pill, false).WinnerKey == "approval", "独立取消置顶不能隐藏正在等待的审批");
    }

    private static void NetworkTelemetry()
    {
        using var service = new NetworkSpeedService();
        service.ApplyByteDeltas(4_000_000, 2_000_000, 2);
        service.ApplyByteDeltas(1_000_000, 1_500_000, 0.5);
        Check(service.SessionDownloadedBytes == 5_000_000 && service.SessionUploadedBytes == 3_500_000
            && service.Current.DownBytesPerSecond == 2_000_000 && service.Current.UpBytesPerSecond == 3_000_000
            && service.SessionPeakDownload == 2_000_000 && service.SessionPeakUpload == 3_000_000,
            "不等间隔采样使用真实字节差分累计并记录双向峰值");
        service.ApplyByteDeltas(-4_000_000, -1_000_000, 1);
        Check(service.Current == default && service.SessionDownloadedBytes == 5_000_000
            && service.SessionUploadedBytes == 3_500_000 && service.SessionPeakUpload == 3_000_000,
            "网卡计数回退不会扣减累计流量或污染峰值");
        service.ApplyByteDeltas(1_000_000, 1_000_000, 0.001);
        service.ApplyByteDeltas(1_000_000, 1_000_000, double.NaN);
        Check(service.Current == default && service.SessionDownloadedBytes == 5_000_000 && service.SessionUploadedBytes == 3_500_000,
            "重复瞬时采样与无效间隔不计入累计流量");
        using var restarted = new NetworkSpeedService();
        restarted.Sample();
        Check(restarted.Current == default && restarted.SessionDownloadedBytes == 0 && restarted.SessionUploadedBytes == 0
            && restarted.SessionPeakDownload == 0 && restarted.SessionPeakUpload == 0,
            "重启监测从零累计且首拍不计入网卡开机历史");
    }

    private static void DesktopNotifications()
    {
        var options = new IslandOptions(); var policy = new DesktopNotificationPolicy(options);
        var now = DateTimeOffset.Now;
        var normal = new DesktopNotification("normal", "Contoso.Chat!App", "聊天", "新消息", "下午开会", now);
        Check(policy.Accepts(normal) && policy.CanPopup(normal, false), "默认接收并展示普通系统通知");
        SettingsRegistry.Write(options, "NotificationBlockedApps", "CHAT；邮件");
        Check(!policy.Accepts(normal), "屏蔽应用按名称或 AppID 忽略大小写匹配");
        SettingsRegistry.Write(options, "NotificationBlockedApps", ""); SettingsRegistry.Write(options, "NotificationAllowedApps", "邮件");
        Check(!policy.Accepts(normal), "仅接收列表排除不匹配应用");
        SettingsRegistry.Write(options, "NotificationAllowedApps", "聊天"); SettingsRegistry.Write(options, "NotificationKeywords", "发票;会议");
        Check(!policy.Accepts(normal) && policy.Accepts(normal with { Body = "会议开始" }), "关键词筛选检查标题和正文中的任意一项");
        SettingsRegistry.Write(options, "NotificationKeywords", ""); SettingsRegistry.Write(options, "NotificationAllowedApps", "");
        SettingsRegistry.Write(options, "NotificationPriorityApps", "Urgent");
        var urgent = normal with { Key = "urgent", AppName = "Urgent", AppId = "Urgent.App" };
        SettingsRegistry.Write(options, "NotificationQuietMode", true);
        Check(!policy.CanPopup(normal, false) && policy.CanPopup(urgent, false), "静默时普通消息只留记录且优先应用可提示");
        var critical = normal with { Kind = DesktopEventKind.CriticalBattery, AppId = "", AppName = "Windows" };
        Check(policy.CanPopup(critical, false) && !policy.CanPopup(critical, true), "低电量遵守静默优先例外且锁屏不展示内容");
        SettingsRegistry.Write(options, "NotificationQuietAllowPriority", false);
        Check(!policy.CanPopup(urgent, false) && !policy.CanPopup(critical, false), "完全静默关闭优先例外");
        SettingsRegistry.Write(options, "NotificationQuietMode", false); SettingsRegistry.Write(options, "NotificationPriorityOnly", true);
        Check(!policy.CanPopup(normal, false) && policy.CanPopup(urgent, false), "只展示高优先级模式保留普通消息记录");
        SettingsRegistry.Write(options, "NotificationPriorityOnly", false);
        Check(!policy.Accepts(normal with { AppId = "AlphaIsland.Desktop!App", AppName = "Alpha Island" }), "通知监听排除本程序避免自循环");
        var inbox = new DesktopNotificationInbox();
        inbox.Add(normal, false); Check(inbox.History.Count == 1 && inbox.PendingCount == 0, "首次同步旧消息只导入通知中心");
        inbox.Add(urgent, true, policy); inbox.Add(normal with { Key = "second" }, true, policy);
        Check(inbox.TakeNext(policy, false, now)?.Key == urgent.Key, "消息队列优先展示优先应用");
        Check(!inbox.Add(normal with { Key = "second", Body = "更新消息" }, true, policy) && inbox.PendingCount == 1, "同一通知更新不重复排队");
        Check(inbox.TakeNext(policy, false, now)?.Body == "更新消息", "排队通知的内容更新生效");
        inbox.Add(normal with { Key = "expired", Created = now.AddMinutes(-3) }, true, policy);
        Check(inbox.TakeNext(policy, false, now) == null, "延迟太久的消息保留记录但不补弹");
        inbox.Add(urgent with { Key = "preserve-priority" }, true, policy);
        for (int i = 0; i < 150; i++) inbox.Add(normal with { Key = "flood-" + i, Created = now.AddMilliseconds(i) }, true, policy);
        Check(inbox.History.Count <= 100 && inbox.PendingCount <= 50 && inbox.TakeNext(policy, false, now)?.Key == "preserve-priority",
            "消息洪峰限制历史与队列并保留高优先级通知");
        inbox.Clear(); Check(inbox.PendingCount == 0 && inbox.History.Count == 0, "清空仅清除岛内队列和记录");
        Check(CpuUsageService.Calculate(40, 70, 30) == 60 && CpuUsageService.Calculate(100, 100, 0) == 0,
            "CPU 使用率正确扣除内核计时中包含的空闲时间");
        Check(CpuUsageService.Calculate(1, 0, 0) == null && CpuUsageService.Calculate(-1, 5, 5) == null,
            "CPU 重置或无采样间隔显示未知而非假数值");
    }

    private static void IslandUi(string testRoot, bool agentOnly = false, bool pausedMedia = false, bool focusOnly = false, bool vivoOnly = false, bool uninstallOnly = false, bool batteryOnly = false, bool coverClipboardOnly = false, bool pausedOnly = false, bool aiConfigOnly = false, bool handoffOnly = false, bool cpuOnly = false, bool statusOnly = false, bool launchLinksOnly = false, bool notificationStartupOnly = false, bool weatherOnly = false, bool backgroundWindowOnly = false, bool performanceOnly = false, bool externalTranslationOnly = false, bool aiConversationsOnly = false, bool settingsAppearanceOnly = false, bool nativeBannersOnly = false, bool notificationContentOnly = false, bool componentHitAreaOnly = false, bool pageRestoreOnly = false, bool iconsOnly = false, bool iconPresentationOnly = false, string? iconEntryProbe = null)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            System.Windows.Application? app = null;
            alpha_island.MainWindow? window = null;
            bool reduceMotion = alpha_island.Animation.MotionPolicy.ReduceMotion;
            bool powerSave = alpha_island.Animation.MotionPolicy.PowerSave;
            try
            {
                app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                app.Resources["BoolToVisConverter"] = new System.Windows.Controls.BooleanToVisibilityConverter();
                app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                { Source = new Uri("pack://application:,,,/alpha-island;component/Themes/LiquidGlass.xaml") });
                app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                { Source = new Uri("pack://application:,,,/alpha-island;component/Themes/Win11Settings.xaml") });
                alpha_island.Animation.MotionPolicy.Apply(true, false);
                if (settingsAppearanceOnly)
                {
                    var appearanceSettings = new alpha_island.Settings.SettingsWindow(false);
                    try { SettingsForegroundChecks(appearanceSettings); SettingsAppearanceMotionChecks(appearanceSettings); SettingsCategoryChecks(); }
                    finally { appearanceSettings.ForceClose = true; appearanceSettings.Close(); }
                    return;
                }
                if (aiConfigOnly) { AiSettingsConfigurationUiChecks(testRoot); AiModelPickerUiChecks(testRoot); return; }
                if (iconEntryProbe == "theme-cohesion") { ThemeCohesionChecks(testRoot); return; }
                if (iconEntryProbe == "settings-lyrics") { SettingsLyricsLayoutChecks(testRoot); return; }
                if (iconEntryProbe == "plugin-button-stability") { PluginButtonStabilityChecks(testRoot); return; }
                if (iconEntryProbe == "settings-consolidation") { SettingsConsolidationChecks(testRoot); return; }
                var options = new IslandOptions { LyricsEnabled = true, GlassOpacity = 0.08, GreetingEnabled = false, FileTransferEnabled = true };
                window = new alpha_island.MainWindow(options);
                object? Call(string name, params object?[] values)
                {
                    var method = typeof(alpha_island.MainWindow).GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    var parameters = method.GetParameters();
                    var args = parameters.Select((p, i) => i < values.Length ? values[i] : p.DefaultValue).ToArray();
                    return method.Invoke(window, args);
                }
                T Field<T>(string name) => (T)ReadWindowMember(window, name)!;
                void SetField(string name, object? value) => WriteWindowMember(window, name, value);
                var root = (System.Windows.Controls.Grid)window.Content;
                if (iconEntryProbe == "notification-refinement") { NotificationRefinementChecks(window); return; }
                if (iconEntryProbe == "api-persona") { ApiPersonaUiChecks(testRoot); return; }
                if (iconEntryProbe == "island-information") { IslandInformationUiChecks(window, testRoot); return; }
                if (iconEntryProbe == "desktop-assistant") { DesktopAssistantUiChecks(window, testRoot); return; }
                if (iconEntryProbe == "assistant-reminders") { AssistantReminderUiChecks(window, testRoot); return; }
                if (iconEntryProbe == "clipboard-history-preferences") { ClipboardHistoryPreferencesChecks(window, testRoot); return; }
                if (iconEntryProbe == "ai-settings-layout") { AiSettingsLayoutChecks(window, testRoot); return; }
                if (iconEntryProbe == "plugin-settings-surface") { PluginSettingsSurfaceChecks(window, testRoot); return; }
                if (iconEntryProbe == "island-zorder") { IslandZOrderChecks(window, testRoot); return; }
                if (iconEntryProbe == "productivity-corners") { ProductivityCornersChecks(window); return; }
                if (iconEntryProbe == "liquid-glass-live") { LiquidGlassLiveSceneChecks(window); return; }
                if (iconEntryProbe == "file-transfer-ole") { FileTransferOleChecks(window, root, testRoot); return; }
                window.Content = null;
                using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Alpha Island pin and geometry regression")
                { Width = 1200, Height = 340, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
                source.RootVisual = root;
                root.UpdateLayout();
                if (iconEntryProbe == "small-screen-compatibility") { SmallScreenCompatibilityChecks(root); return; }
                if (iconEntryProbe == "api-model-avatar") { ApiModelAvatarChecks(window, root, testRoot); return; }
                if (iconEntryProbe == "interaction-feedback") { InteractionFeedbackChecks(window, root, testRoot); return; }
                if (iconEntryProbe == "media-lyrics-controls") { V41MediaLyricChecks(window, root); return; }
                if (iconEntryProbe == "lyric-rest") { LyricRestUiChecks(window, root); return; }
                if (iconEntryProbe == "ui-refinement") { UiRefinementChecks(window, root, testRoot); return; }
                if (iconEntryProbe == "notification-weather-layout") { NotificationWeatherLayoutChecks(window, root); return; }
                if (iconEntryProbe == "weather-content-layout") { WeatherContentLayoutChecks(window, root); return; }
                if (iconEntryProbe == "greeting-handoff") { GreetingHandoffChecks(window, root); return; }
                if (iconEntryProbe == "greeting-media") { GreetingMediaChecks(window, root, testRoot); return; }
                if (iconEntryProbe == "speech-setup") { SpeechSetupUiChecks(testRoot); return; }
                if (iconEntryProbe == "notification-logo-fit") { NotificationLogoFitChecks(); return; }
                if (iconEntryProbe == "short-hints") { ShortHintChecks(window); return; }
                if (iconEntryProbe == "hint-bounds") { HintBoundsChecks(window, root); return; }
                if (iconEntryProbe == "earbud-identity-live") { EarbudBatteryIdentityLiveChecks(window, root); return; }
                if (iconEntryProbe == "speech-caption") { SpeechCaptionUiChecks(window, testRoot); return; }
                if (iconEntryProbe == "voice-assistant") { VoiceAssistantUiChecks(window, root, testRoot); return; }
                if (iconEntryProbe == "voice-capture-live") { VoiceCaptureLiveChecks(); return; }
                if (iconEntryProbe == "voice-dictation-fixture") { VoiceDictationFixtureChecks(); return; }
                if (iconEntryProbe == "voice-failure-layout") { VoiceFailureUiChecks(root); return; }
                if (iconEntryProbe == "screen-tools-open") { ScreenToolsOpenChecks(window, root); return; }
                if (iconEntryProbe == "keyboard-battery-live") { KeyboardBatteryLiveChecks(window); return; }
                if (iconEntryProbe == "keyboard-spectrum") { KeyboardSpectrumChecks(window, root); return; }
                if (iconEntryProbe == "resident-memory") { ResidentMemoryChecks(window, root, testRoot); return; }
                if (iconEntryProbe == "settings-icon-media-pin") { SettingsIconMediaPinChecks(window, root); return; }
                if (iconEntryProbe == "media-peripheral-lifecycle") { MediaPeripheralLifecycleChecks(window, root); return; }
                if (iconEntryProbe == "game-resource-budget") { GameResourceBudgetChecks(window, root, testRoot); return; }
                if (iconEntryProbe == "gaming-memory") { GamingMemoryChecks(window, root, testRoot); return; }
                if (iconEntryProbe == "gaming-notifications") { GamingNotificationChecks(window, root); return; }
                if (iconEntryProbe == "peripheral-interactions") { PeripheralInteractionChecks(window, root); return; }
                if (iconEntryProbe == "earbuds-isolated-live") { EarbudIsolatedLiveChecks(window, testRoot); return; }
                if (iconEntryProbe == "device-battery-enhancements") { DeviceBatteryEnhancementChecks(window, root); return; }
                if (iconEntryProbe == "card-wheel") { CardWheelInputChecks(window, root); return; }
                if (iconEntryProbe == "component-menu-input") { ComponentMenuInputChecks(window, root); return; }
                if (iconEntryProbe == "launcher-input") { LauncherInputChecks(window, root); return; }
                if (iconEntryProbe == "device-batteries") { DeviceBatteryCardChecks(window, root, testRoot); return; }
                if (iconEntryProbe == "liquid-cell-motion") { LiquidCellMotionChecks(window, root); return; }
                if (iconEntryProbe == "codex-status-recovery") { CodexStatusRecoveryChecks(window, root, testRoot); return; }
                if (iconEntryProbe == "agent-startup-latency") { AgentStartupLatencyChecks(window, root); return; }
                if (iconEntryProbe == "notification-presentation") { NotificationPresentationChecks(window, root); return; }
                if (iconEntryProbe == "browser-download") { BrowserDownloadUiChecks(window, root, testRoot); return; }
                if (iconEntryProbe == "file-transfer-out") { FileTransferExportChecks(window, root, testRoot); return; }
                if (iconEntryProbe == "volume-flash-baseline") { VolumeFlashLiveChecks(window, root, false); return; }
                if (iconEntryProbe == "volume-flash-live") { VolumeFlashLiveChecks(window, root); return; }
                if (iconEntryProbe == "volume-game-quiet-live") { VolumeFlashLiveChecks(window, root, gameQuiet: true); return; }
                if (iconEntryProbe == "game-quiet") { GameQuietUiChecks(window, root); return; }
                if (iconEntryProbe == "game-compact-lock") { GameCompactLockChecks(window, root, testRoot); return; }
                if (iconEntryProbe == "status-split") { StatusSplitUiChecks(window, root); return; }
                if (iconEntryProbe == "power-frame-stall") { PowerFrameStallChecks(window, root); return; }
                if (iconEntryProbe == "charger-type") { ChargerTypeUiChecks(window, root); return; }
                if (iconEntryProbe == "smart-charging") { SmartChargingUiChecks(window, root); return; }
                if (iconEntryProbe == "energy-saver") { EnergySaverUiChecks(window, root); return; }
                if (iconEntryProbe == "power-island-lifecycle") { PowerIslandLifecycleChecks(window, root); return; }
                if (iconEntryProbe == "music-layout-preview") { MusicLayoutPreviews(window, root); return; }
                if (iconEntryProbe == "battery-text-layout-preview") { BatteryTextLayoutPreviews(window, root); return; }
                if (iconEntryProbe == "bluetooth-transition") { BluetoothTransitionChecks(window, root); BluetoothArtworkUiChecks(window, root); return; }
                if (iconEntryProbe == "tray-popup") { TrayPopupChecks(); return; }
                if (iconEntryProbe == "island-corners") { IslandCornerChecks(window, root); return; }
                if (iconEntryProbe == "backdrop-opacity") { BackdropOpacityChecks(); return; }
                if (iconEntryProbe == "component-native-hit") { ComponentNativeHitChecks(); ComponentNativeHitChecks(includeBody: true); ComponentNativeHitChecks(includeBody: true, contourHeight: 214); return; }
                if (iconEntryProbe == "charging-island-preview") { ChargingIslandPreviewChecks(window, root); return; }
                if (iconEntryProbe == "charging-corners") { ChargingCompactCornerChecks(window, root); return; }
                if (iconEntryProbe == "all-island-corners") { AllCompactIslandCornerChecks(window, root); return; }
                if (iconEntryProbe == "network-curves") { NetworkMenuAndCurveChecks(window, root); return; }
                if (iconEntryProbe == "screen-tools-theme") { ScreenToolsThemeChecks(window, root); return; }
                if (iconEntryProbe == "media-events") { DownloadAndMediaEventUiChecks(window, root, testRoot); return; }
                if (iconEntryProbe == "bundled-isolation") { BundledPluginIsolationChecks(window, testRoot); return; }
                if (iconEntryProbe == "api-chat-input") { ApiChatInputRoutingChecks(testRoot); return; }
                if (iconEntryProbe == "maintenance") { MaintenanceChecks(window, testRoot); return; }
                if (iconEntryProbe == "plugin-isolation") { PluginIsolationChecks(window, testRoot); return; }
                if (iconEntryProbe == "plugin-lifecycle") { PluginAndSettingsChecks(window, testRoot); return; }
                if (iconEntryProbe == "optimization") { OptimizationUiChecks(window, testRoot); return; }
                if (iconEntryProbe == "liquid-glass") { LiquidGlassUiChecks(window, root); return; }
                if (iconEntryProbe == "messenger-direct-open") { MessengerDirectOpenChecks(window, root); return; }
                if (iconEntryProbe == "messenger-notifications") { MessengerNotificationUiChecks(window, root); return; }
                if (iconEntryProbe == "battery-ripple") { BatteryRippleChecks(window, root); return; }
                if (iconEntryProbe == "battery-connector") { BatteryConnectorChecks(window, root); return; }
                if (iconEntryProbe == "welcome-power-preview") { WelcomePowerPreviewChecks(window, root); return; }
                if (iconEntryProbe == "compact-status-alerts") { CompactStatusAlertChecks(window, root); return; }
                if (iconEntryProbe == "v41-issues") { V41IssueChecks(window, root); return; }
                if (iconEntryProbe == "sidebubble") { SideBubbleIconMotionUiChecks(window, root); return; }
                if (iconEntryProbe == "actions") { IconActionEntryUiChecks(window, root); return; }
                if (iconEntryProbe == "redesign") { RedesignedIconChecks(); return; }
                if (iconEntryProbe == "notification-visual-stack") { NotificationVisualStackChecks(window, root); return; }
                if (iconEntryProbe == "notification-stack") { NotificationStackChecks(window, root); return; }
                if (iconEntryProbe == "notification-full") { NotificationFullContentUiChecks(window, root); return; }
                if (iconEntryProbe == "startup-morph") { StartupMorphUiChecks(window, root); return; }
                if (iconEntryProbe == "volume-growth") { VolumeGrowthUiChecks(window, root); return; }
                if (iconEntryProbe == "theme-controls") { ThemeControls(window, root, source); return; }
                if (iconEntryProbe == "volume-flyout") { VolumeFlyoutUiChecks(window, root); return; }
                if (iconEntryProbe == "volume-flyout-live") { VolumeFlyoutLiveChecks(window, root); return; }
                if (iconEntryProbe == "volume-wheel") { VolumeWheelUiChecks(window, root); return; }
                if (iconEntryProbe == "volume-mixer") { VolumeMixerUiChecks(window, root); return; }
                if (iconEntryProbe == "outside-card") { OutsideCardClickUiChecks(window, root); return; }
                if (iconEntryProbe == "agent-navigation") { AgentAppNavigationUiChecks(window, root); return; }
                if (iconEntryProbe == "agent-window-responsiveness") { AgentWindowResponsivenessChecks(window, root); return; }
                if (iconEntryProbe == "handoff-responsiveness") { HandoffResponsivenessChecks(window, root); return; }
                if (iconEntryProbe == "agent-handoff") { AgentHandoffUiChecks(window, root); return; }
                if (iconEntryProbe == "agent-avatar") { AgentAvatarChecks(window, root); return; }
                if (iconEntryProbe == "agent-avatar-routing") { AgentAvatarRoutingChecks(window, root); return; }
                if (iconEntryProbe == "agent-avatar-loop") { AgentAvatarLoopChecks(window, root); return; }
                if (iconEntryProbe == "greeting-notification-sound") { GreetingNotificationSoundChecks(window, root, testRoot); return; }
                if (iconEntryProbe == "spectrum-visibility") { SpectrumVisibilityChecks(window, root); return; }
                if (iconEntryProbe == "amiya-handle") { AmiyaHandleChecks(window, root, testRoot); return; }
                if (iconEntryProbe == "greeting-once") { GreetingSinglePlaybackChecks(window, testRoot); return; }
                if (iconEntryProbe == "caption-modes") { CaptionModesChecks(window, testRoot); return; }
                if (iconEntryProbe == "satellite-material") { SatelliteMaterialChecks(window, root); return; }
                if (iconEntryProbe == "agent-completion-visual") { AgentCompletionVisualChecks(window, root, testRoot); return; }
                if (iconEntryProbe == "agent-multi-session") { AgentMultiSessionChecks(window, root); return; }
                if (iconEntryProbe == "agent-background-resume") { AgentBackgroundResumeChecks(window, root); return; }
                if (iconEntryProbe == "background-agent") { BackgroundVisibilityChecks(window, root, false); return; }
                if (iconEntryProbe == "background-music") { BackgroundVisibilityChecks(window, root, true); return; }
                if (iconEntryProbe == "agent-foreground-click") { AgentForegroundClickChecks(window, root); return; }
                if (iconEntryProbe == "agent-window-handoff") { AgentForegroundVisibilityChecks(window, root); return; }
                if (iconEntryProbe == "clipboard-domains") { ClipboardDomainUiChecks(window, root, testRoot); return; }
                if (iconPresentationOnly) { IconPresentationUiChecks(window, root); return; }
                if (iconsOnly) { SemanticIconControlChecks(); ShortIconMotionChecks(window, root); IconPresentationUiChecks(window, root); BluetoothArtworkUiChecks(window, root); ActionIconIntegrationChecks(window, root); SideBubbleIconMotionUiChecks(window, root); IconActionEntryUiChecks(window, root); SemanticIconEntryUiChecks(window, root); SemanticNotificationUiChecks(window, root); SaveBluetoothArtworkGallery(); SaveSemanticIconPreview(Environment.CurrentDirectory); return; }
                if (pageRestoreOnly) { PageRestoreUiChecks(window, root); return; }
                if (notificationContentOnly) { NotificationContentUiChecks(window, root); return; }
                if (componentHitAreaOnly) { ComponentHitAreaUiChecks(window, root); return; }
                if (nativeBannersOnly) { NativeToastBannerUiChecks(window, root); return; }
                if (performanceOnly) { PerformanceUiChecks(window); return; }
                if (aiConversationsOnly) { AiConversationUiChecks(window, root, testRoot); ConversationPluginLifecycleChecks(window,testRoot); return; }
                if (externalTranslationOnly) { ExternalTranslationUiChecks(window, root, testRoot); return; }
                if (agentOnly)
                {
                    if (pausedMedia) PausedMediaVisibilityChecks(window, root);
                    AgentApprovalInteractionChecks(window, root); return;
                }
                if (focusOnly) { FocusCardUiChecks(window, root); return; }
                if (vivoOnly) { VivoPluginUiChecks(window, root, testRoot); return; }
                if (uninstallOnly) { PluginUninstallChecks(window, testRoot); return; }
                if (batteryOnly) { DeviceBatteryUiChecks(window, root); return; }
                if (pausedOnly) { PausedMediaVisibilityChecks(window, root); return; }
                if (cpuOnly) { CpuSamplingUiChecks(window); return; }
                if (statusOnly) { StatusDisplayUiChecks(window, root); return; }
                if (launchLinksOnly) { LaunchAndLinksUiChecks(window, root, testRoot); return; }
                if (notificationStartupOnly) { NotificationStartupUiChecks(window, root); return; }
                if (weatherOnly) { WeatherUiChecks(window, root); WeatherIconUiChecks(window, root); return; }
                if (backgroundWindowOnly) { BackgroundWindowChecks(window); return; }
                if (handoffOnly) { SettingsCategoryChecks(); MediaHandoffUiChecks(window, root); PausedMediaVisibilityChecks(window, root); return; }
                if (coverClipboardOnly) { RoundedCoverChecks(window, root); ClipboardMusicChecks(window, root, testRoot); return; }
                ThemeControls(window, root, source);
                ClipboardMusicChecks(window, root, testRoot);
                MediaAndCompactUiChecks(window, root);
                DownloadAndMediaEventUiChecks(window, root, testRoot);
                PluginAndSettingsChecks(window, testRoot);
                ConversationPluginLifecycleChecks(window,testRoot);
                SettingsCategoryChecks();
                var appearanceCheck = new alpha_island.Settings.SettingsWindow(false);
                try { SettingsForegroundChecks(appearanceCheck); SettingsAppearanceMotionChecks(appearanceCheck); }
                finally { appearanceCheck.ForceClose = true; appearanceCheck.Close(); }
                CpuSamplingUiChecks(window);
                AiSettingsConfigurationUiChecks(testRoot);
                AiModelPickerUiChecks(testRoot);
                ExternalTranslationUiChecks(window, root, testRoot);
                PluginUninstallChecks(window, testRoot);
                DeviceBatteryUiChecks(window, root);
                RoundedCoverChecks(window, root);
                NativeToastBannerUiChecks(window, root);
                NotificationContentUiChecks(window, root);
                NotificationFullContentUiChecks(window, root);
                StartupMorphUiChecks(window, root);
                OutsideCardClickUiChecks(window, root);
                AgentAppNavigationUiChecks(window, root);
                AgentHandoffUiChecks(window, root);
                VolumeMixerUiChecks(window, root);
                ComponentHitAreaUiChecks(window, root);
                PageRestoreUiChecks(window, root);
                ShortIconMotionChecks(window, root);
                BluetoothArtworkUiChecks(window, root);
                ActionIconIntegrationChecks(window, root);
                IconPresentationUiChecks(window, root);
                SideBubbleIconMotionUiChecks(window, root);
                IconActionEntryUiChecks(window, root);
                SemanticIconControlChecks();
                VolumeGrowthUiChecks(window, root);
                SemanticIconEntryUiChecks(window, root);
                SemanticNotificationUiChecks(window, root);
                WeatherIconUiChecks(window, root);
                // 多岛测试使用真实的媒体快照，空播放器不应再凭默认主岛键制造卫星。
                using var pinMedia = new SmtcService(System.Windows.Threading.Dispatcher.CurrentDispatcher);
                pinMedia.SetDebugNowPlaying(new NowPlayingInfo { Title = "双岛回归示例", State = PlaybackState.Playing });
                SetField("_smtc", pinMedia);
                System.Windows.DragEventArgs DragArgs(System.Windows.IDataObject data,
                    System.Windows.DragDropEffects allowed = System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move,
                    bool right = false)
                {
                    var constructor = typeof(System.Windows.DragEventArgs).GetConstructors(
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
                    var result = (System.Windows.DragEventArgs)constructor.Invoke(new object[] { data,
                        System.Windows.DragDropKeyStates.LeftMouseButton, allowed, window.IslandBody,
                        new System.Windows.Point(right ? window.IslandBody.ActualWidth - 5 : 5, 5) });
                    result.RoutedEvent = System.Windows.DragDrop.PreviewDragEnterEvent;
                    return result;
                }
                string filePath = Path.Combine(testRoot, "拖拽样本.txt");
                string directoryPath = Path.Combine(testRoot, "拖拽文件夹");
                File.WriteAllText(filePath, "file drop regression"); Directory.CreateDirectory(directoryPath);
                var realFiles = new System.Windows.DataObject();
                realFiles.SetData(System.Windows.DataFormats.FileDrop, new[] { filePath, filePath.ToUpperInvariant(), directoryPath,
                    Path.Combine(testRoot, "missing.txt"), "relative.txt", "https://example.com/file.txt" });
                Check(FileDropPayload.TryRead(realFiles, out var validPaths) && validPaths.Length == 2
                    && validPaths.Contains(filePath) && validPaths.Contains(directoryPath), "文件载荷识别真实文件和文件夹、规范化去重并排除无效路径");
                var textDrop = new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText, filePath);
                Check(!FileDropPayload.TryRead(textDrop, out _) && !FileDropPayload.TryRead(new System.Windows.DataObject(
                    System.Windows.DataFormats.FileDrop, "不是文件路径数组"), out _), "路径文本与伪造文件格式不能冒充文件拖拽");
                Call("SetState", IslandState.Pill);
                var textArgs = DragArgs(textDrop); Call("Window_DragEnter", window, textArgs);
                Check(textArgs.Effects == System.Windows.DragDropEffects.None && Field<IslandState>("_state") == IslandState.Pill,
                    "拖入文本不展开中转站也不接收文件效果");
                var zonePoint = window.TopDropZone.PointToScreen(new System.Windows.Point(280, 24));
                for (int i = 0; i < 8; i++) Call("OnMouseHookDragMove", null,
                    ((int)zonePoint.X + (i % 2 == 0 ? 22 : -22), (int)zonePoint.Y, (int)zonePoint.X - 100, (int)zonePoint.Y + 200));
                Check(Field<IslandState>("_state") == IslandState.Pill && window.TopDropZone.IsHitTestVisible
                    && (bool)Call("IsPointInsideInteractiveZone", (int)zonePoint.X, (int)zonePoint.Y)!,
                    "连续晃动只临时启用顶部 OLE 感应区，不直接展开且原生命中包含接收区");
                Call("OnMouseHookDragEnd", null, EventArgs.Empty);
                Check(!window.TopDropZone.IsHitTestVisible && window.TopDropZone.Background == null,
                    "普通拖动松手后顶部感应区恢复完全穿透");
                Call("SetState", IslandState.Card, false, IslandPageIds.System); root.UpdateLayout();
                SetField("_pinnedCardPage", IslandPageIds.System);
                var enterFiles = DragArgs(realFiles); Call("Window_DragEnter", window, enterFiles); root.UpdateLayout();
                Check(enterFiles.Effects == System.Windows.DragDropEffects.Copy && Field<IslandState>("_state") == IslandState.Tray
                    && Field<FileTransferService>("_fileTransfer").Count == 0, "真实文件拖入自动展开，但松手前不加入暂存列表");
                Call("Window_DragLeave", window, enterFiles); PumpAnimations(80);
                Call("Window_DragOver", window, DragArgs(realFiles)); PumpAnimations(220);
                Check(Field<IslandState>("_state") == IslandState.Tray && Field<bool>("_fileDragActive"),
                    "拖过中转站内部控件时取消退场计时，不因子控件切换误收回");
                Call("Window_DragLeave", window, enterFiles); PumpAnimations(250);
                Check(Field<IslandState>("_state") == IslandState.Card && Field<IslandPageHost>("_pages").ActiveId == IslandPageIds.System
                    && Field<string?>("_pinnedCardPage") == IslandPageIds.System && !Field<bool>("_fileDragActive"),
                    "拖拽取消恢复原卡片、原置顶页并清理拖拽状态");
                SetField("_pinnedCardPage", null);
                var moveOnly = DragArgs(realFiles, System.Windows.DragDropEffects.Move);
                Call("Window_DragEnter", window, moveOnly);
                Check(moveOnly.Effects == System.Windows.DragDropEffects.None && Field<IslandState>("_state") == IslandState.Card,
                    "只允许立即移动的来源不被路径中转站误确认移动");
                Call("Window_DragEnter", window, DragArgs(realFiles));
                Call("OnMouseHookDragEnd", null, EventArgs.Empty);
                var cutDrop = DragArgs(realFiles, right: true); Call("DropZoneCut_Drop", window.DropZoneCut, cutDrop); PumpAnimations(230);
                var transfer = Field<FileTransferService>("_fileTransfer");
                Check(transfer.Count == 2 && transfer.Items.All(x => x.IsCut) && File.Exists(filePath)
                    && cutDrop.Effects == System.Windows.DragDropEffects.Copy && !Field<bool>("_fileDragActive")
                    && Field<IslandState>("_state") == IslandState.Tray, "鼠标松手不抢先拆掉 OLE 目标，剪切分区只记录后续意图且保留原文件");
                Call("DropZoneCopy_Drop", window.DropZoneCopy, DragArgs(realFiles));
                Check(transfer.Count == 2 && transfer.Items.All(x => !x.IsCut), "重复拖入更新复制意图而不重复添加");
                SetField("_exportingTransferItem", true);
                var selfDrop = DragArgs(realFiles); Call("Window_DragEnter", window, selfDrop);
                Check(selfDrop.Effects == System.Windows.DragDropEffects.None && !Field<bool>("_fileDragActive"),
                    "中转站条目拖出又落回自身时不误确认转出");
                SetField("_exportingTransferItem", false);
                transfer.Clear();
                string disappearingPath = Path.Combine(testRoot, "disappearing.txt"); File.WriteAllText(disappearingPath, "temporary");
                var disappearing = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new[] { disappearingPath });
                Call("SetFileDropZoneArmed", true);
                Call("SetState", IslandState.Pill); Call("Window_DragEnter", window, DragArgs(disappearing));
                File.Delete(disappearingPath);
                var missingDrop = DragArgs(disappearing); Call("Window_Drop", window, missingDrop);
                Check(missingDrop.Effects == System.Windows.DragDropEffects.None && transfer.Count == 0
                    && Field<IslandState>("_state") == IslandState.Pill && !window.TopDropZone.IsHitTestVisible,
                    "文件在拖拽途中消失时拒绝投放、恢复原岛并恢复顶部穿透");
                options.FileTransferEnabled = false;
                var disabledDrop = DragArgs(realFiles); Call("Window_DragEnter", window, disabledDrop);
                Check(disabledDrop.Effects == System.Windows.DragDropEffects.None && Field<IslandState>("_state") == IslandState.Pill,
                    "关闭文件中转站时真实文件也不能触发展开");
                options.FileTransferEnabled = true;
                using (var hotkeys = new HotkeyService(System.Windows.Threading.Dispatcher.CurrentDispatcher))
                using (var clipboard = new ClipboardService(System.Windows.Threading.Dispatcher.CurrentDispatcher, hotkeys))
                {
                    int dismissed = 0; clipboard.CopiedDismissed += (_, _) => dismissed++;
                    foreach (string format in new[] { System.Windows.DataFormats.FileDrop, "FileGroupDescriptorW", System.Windows.DataFormats.Bitmap })
                    {
                        typeof(ClipboardService).GetField("_isCopiedPending", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(clipboard, true);
                        var nonText = new System.Windows.DataObject();
                        nonText.SetData(format, format == System.Windows.DataFormats.FileDrop ? new[] { filePath } : new object());
                        if (format != System.Windows.DataFormats.Bitmap) nonText.SetData(System.Windows.DataFormats.UnicodeText, "附带的文件名称文本");
                        clipboard.ProcessClipboardData(nonText, false);
                        Check(!clipboard.IsCopiedPending && clipboard.History.Count == 0,
                            "剪切板非文本载荷清理旧驻留而不将文件名误记为文本 " + format);
                    }
                    Check(dismissed == 3, "剪切板类型切换只发出一次旧文本取消事件");
                }
                void DrawIslandPreview(System.Windows.Media.DrawingContext draw, double x, double y)
                {
                    var rootBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),
                        (int)Math.Ceiling(root.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    // Component previews include the island and its external handle, without unrelated satellite overlays.
                    var islandContainer = System.Windows.Media.VisualTreeHelper.GetParent(window.IslandBody);
                    var handleContainer = System.Windows.Media.VisualTreeHelper.GetParent(window.ComponentHandle);
                    var overlays = root.Children.OfType<System.Windows.UIElement>().Where(x => !ReferenceEquals(x, islandContainer) && !ReferenceEquals(x, handleContainer))
                        .Select(x => (View: x, Before: x.Visibility)).ToArray();
                    try
                    {
                        foreach (var overlay in overlays) overlay.View.Visibility = System.Windows.Visibility.Hidden;
                        root.UpdateLayout(); rootBitmap.Render(root);
                    }
                    finally
                    {
                        foreach (var overlay in overlays) overlay.View.Visibility = overlay.Before;
                        root.UpdateLayout();
                    }
                    var bodyOrigin = window.IslandBody.TranslatePoint(new System.Windows.Point(), root);
                    draw.PushClip(new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(x - 20, y - 16,
                        window.IslandBody.Width + 64, window.IslandBody.Height + 32)));
                    draw.DrawImage(rootBitmap, new System.Windows.Rect(x - bodyOrigin.X, y - bodyOrigin.Y, root.ActualWidth, root.ActualHeight));
                    draw.Pop();
                }
                Call("SetState", IslandState.Pill);
                Call("SetMusicPinned", true);
                Call("SetLayerPinned", "netspeed", true);
                PumpAnimations(450);
                var layers = Field<IslandLayerHost>("_layers");
                var satellites = Field<Dictionary<string, System.Windows.Controls.Border>>("_satelliteOrbViews");
                Check(Field<bool>("_isPinned") && layers.IsPinned("netspeed") && window.MusicOrbBubble.Visibility == System.Windows.Visibility.Visible,
                    "实际主窗口同时置顶音乐和网速");
                System.Windows.Rect ViewBounds(System.Windows.FrameworkElement view)
                    => view.TransformToAncestor(root).TransformBounds(new System.Windows.Rect(new System.Windows.Point(), view.RenderSize));
                bool HintOutsideCard()
                    => window.SideBubble.Visibility == System.Windows.Visibility.Visible
                        && ViewBounds(window.SideBubble).Right <= ViewBounds(window.IslandBody).Left - 4;
                Call("SwitchMainIsland", "music"); Call("SetState", IslandState.Card, false, IslandPageIds.Music);
                PumpAnimations(250); root.UpdateLayout();
                Check(HintOutsideCard() && satellites["netspeed"].Visibility == System.Windows.Visibility.Collapsed,
                    "双岛展开主岛时网速提示保持外侧且旧卫星不覆盖卡片");
                for (int i = 0; i < 3; i++)
                {
                    Call("SetState", IslandState.Pill); Call("SyncSatelliteOrbs");
                    Call("SetState", IslandState.Card, false, IslandPageIds.Music); PumpAnimations(250); root.UpdateLayout();
                    Check(HintOutsideCard(), "反复重排双岛后旁路提示保留正确位置 " + i);
                }
                alpha_island.Animation.MotionPolicy.Apply(false, false);
                Call("SetState", IslandState.Pill); PumpAnimations(470);
                Call("SetState", IslandState.Card, false, IslandPageIds.Music);
                bool clearThroughoutMorph = true;
                for (int i = 0; i < 9; i++)
                {
                    PumpAnimations(45); root.UpdateLayout();
                    if (window.SideBubble.Opacity > .1) clearThroughoutMorph &= HintOutsideCard();
                    clearThroughoutMorph &= satellites["netspeed"].Visibility == System.Windows.Visibility.Collapsed;
                }
                Check(clearThroughoutMorph && HintOutsideCard(), "主岛展开动画的每个采样帧都不与旁路提示或旧卫星重叠");
                var fullPreview = new System.Windows.Media.Imaging.RenderTargetBitmap(1200, 340, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                fullPreview.Render(root);
                var previewBounds = System.Windows.Rect.Union(ViewBounds(window.IslandBody), ViewBounds(window.SideBubble));
                previewBounds.Inflate(30, 18);
                var crop = new System.Windows.Media.Imaging.CroppedBitmap(fullPreview, new System.Windows.Int32Rect(
                    (int)previewBounds.Left, Math.Max(0, (int)previewBounds.Top), (int)previewBounds.Width,
                    Math.Min(340 - Math.Max(0, (int)previewBounds.Top), (int)previewBounds.Height)));
                var previewDrawing = new System.Windows.Media.DrawingVisual();
                using (var draw = previewDrawing.RenderOpen())
                {
                    var bounds = new System.Windows.Rect(0, 0, crop.PixelWidth, crop.PixelHeight);
                    draw.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0x74, 0x85)), null, bounds);
                    draw.DrawImage(crop, bounds);
                }
                var cardPreview = new System.Windows.Media.Imaging.RenderTargetBitmap(crop.PixelWidth, crop.PixelHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                cardPreview.Render(previewDrawing);
                var cardPreviewEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                cardPreviewEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(cardPreview));
                using (var output = File.Create(Path.GetFullPath("docs/screens/dual-island-card.png"))) cardPreviewEncoder.Save(output);
                alpha_island.Animation.MotionPolicy.Apply(true, false);
                Call("SetState", IslandState.Pill); Call("SwitchMainIsland", "netspeed"); PumpAnimations(450);
                Call("ToggleSatellitePin", "music"); PumpAnimations(400);
                Check(!Field<bool>("_isPinned") && layers.IsPinned("netspeed") && Field<string>("_activeMainIslandKey") == "netspeed"
                    && window.MusicOrbBubble.Visibility == System.Windows.Visibility.Collapsed,
                    "取消音乐卫星置顶时音乐收起网速保留");
                Call("SetMusicPinned", true); Call("SwitchMainIsland", "music"); PumpAnimations(400);
                Call("ToggleSatellitePin", "netspeed");
                Check(Field<bool>("_isPinned") && !layers.IsPinned("netspeed") && satellites["netspeed"].Visibility == System.Windows.Visibility.Collapsed,
                    "取消网速卫星置顶时网速收起音乐保留");
                Call("SetLayerPinned", "netspeed", true); Call("TogglePin");
                Check(Field<string>("_activeMainIslandKey") == "music" && Field<bool>("_isPinned") && !layers.IsActive("netspeed"),
                    "取消网速主岛置顶后音乐接管");
                Call("SetLayerPinned", "netspeed", true); Call("SwitchMainIsland", "music"); Call("TogglePin"); PumpAnimations(400);
                Check(Field<string>("_activeMainIslandKey") == "netspeed" && layers.IsPinned("netspeed") && !Field<bool>("_isPinned")
                    && window.MusicOrbBubble.Visibility == System.Windows.Visibility.Collapsed,
                    "取消音乐主岛置顶后网速接管");
                Call("TogglePin");
                Check(Field<IslandState>("_state") == IslandState.Bar && !layers.AnyPinned, "取消最后一个置顶岛独立收回细条");

                Call("SetMusicPinned", true, true); Call("SetState", IslandState.Pill);
                SetField("_pillLyricWidth", IslandMetrics.MaxPillWidth); Call("TryMorphPillWidth");
                Check(Math.Abs(window.IslandBody.Width - IslandMetrics.MaxPillWidth) < 0.5, "音乐主岛仍能按长歌词展开");
                Call("SetLayerPinned", "netspeed", true);
                bool fixedWidths = true;
                foreach (string key in new[] { "netspeed", "download", "system", "volume", "battery", "clipboard", "agentstatus" })
                {
                    Call("SwitchMainIsland", key);
                    foreach (double lyricWidth in new[] { 344.0, 450.0, 560.0 })
                    {
                        SetField("_pillLyricWidth", lyricWidth); Call("TryMorphPillWidth");
                        double expected = key == "netspeed" ? StatusDisplayOptions.Width(StatusDisplayOptions.NetworkSelected(options).Length) : IslandMetrics.PillWidth;
                        fixedWidths &= Math.Abs(window.IslandBody.Width - expected) < 0.5;
                    }
                }
                Check(fixedWidths, "八类非音乐主岛不随后台歌词长度改变背景");
                Call("SwitchMainIsland", "music"); PumpAnimations(400);
                foreach (string key in layers.ActiveKeys.Where(key => key != "netspeed").ToArray()) layers.Hide(key);
                Call("SyncNotificationLayers");
                var netOrb = satellites["netspeed"];
                var netRates = ((System.Windows.Controls.Grid)netOrb.Child).Children.OfType<alpha_island.Controls.NetworkSpeedPill>().Single();
                netRates.SetRates(263900, 50400);
                Check(netRates.UploadValue.Text == "257.7KB/s" && netRates.DownloadValue.Text == "49.2KB/s"
                    && netRates.UploadValue.FontSize >= 10 && netRates.UploadValue.FontWeight == System.Windows.FontWeights.Bold
                    && netRates.ToolTip.ToString()!.Contains("257.7 KB/s") && netRates.ToolTip.ToString()!.Contains("49.2 KB/s"),
                    "状态卫星显示完整网速单位与加粗字体");
                Check(netOrb.Width <= IslandSizing.NetworkSatelliteWidth && netOrb.Height <= IslandSizing.NetworkSatelliteHeight,
                    "包含功耗的系统状态卫星保持既定紧凑尺寸");
                Check(ReferenceEquals(netOrb.Background, window.IslandBody.Background), "网速卫星共用主岛透明度画刷");
                var fill = (System.Windows.Media.LinearGradientBrush)netOrb.Background;
                byte lowAlpha = fill.GradientStops[0].Color.A;
                options.GlassOpacity = 0.8; Call("ApplyGlassAppearance");
                Check(fill.GradientStops[0].Color.A > lowAlpha && ReferenceEquals(netOrb.Background, window.IslandBody.Background),
                    "修改透明度即时更新已置顶网速卫星");
                options.GlassOpacity = 0.3; Call("ApplyGlassAppearance");
                netRates.SetRates(99_900_000_000, 88_800_000_000);
                root.UpdateLayout();
                var uploadPoint = netRates.UploadValue.TranslatePoint(new System.Windows.Point(), netRates);
                var downloadPoint = netRates.DownloadValue.TranslatePoint(new System.Windows.Point(), netRates);
                Check(uploadPoint.Y < downloadPoint.Y && Math.Abs(uploadPoint.X - downloadPoint.X) < 10,
                    "大速率时上传与下载仍按参考图上下排列");
                Check(netRates.DisplayedItems.SequenceEqual(new[] { "upload", "download" }) && !netRates.ToolTip.ToString()!.Contains("CPU"),
                    "独立网速卫星仅显示上传下载，不混入 CPU 或内存");
                netRates.SetRates(263900, 50400);
                root.UpdateLayout();
                var preview = new System.Windows.Media.Imaging.RenderTargetBitmap(1200, 110, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                var background = new System.Windows.Media.DrawingVisual();
                using (var draw = background.RenderOpen()) draw.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0x74, 0x85)), null, new System.Windows.Rect(0, 0, 1200, 110));
                preview.Render(background); preview.Render(root);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(preview));
                string path = Path.GetFullPath("docs/screens/two-islands.png"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var stream = File.Create(path)) encoder.Save(stream);
                Check(new FileInfo(path).Length > 1000, "双岛与单行网速实际主窗口预览生成");
                var detail = new System.Windows.Media.DrawingVisual();
                using (var draw = detail.RenderOpen())
                {
                    draw.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0x74, 0x85)), null, new System.Windows.Rect(0, 0, netOrb.Width + 40, netOrb.Height + 24));
                    draw.DrawRectangle(new System.Windows.Media.VisualBrush(netOrb), null, new System.Windows.Rect(20, 12, netOrb.Width, netOrb.Height));
                }
                var detailBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)netOrb.Width + 40, (int)netOrb.Height + 24, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                detailBitmap.Render(detail);
                var detailEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                detailEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(detailBitmap));
                using (var stream = File.Create(Path.GetFullPath("docs/screens/network-speed-pill.png"))) detailEncoder.Save(stream);

                using var telemetry = new NetworkSpeedService();
                SetField("_netSpeed", telemetry);
                telemetry.ApplyByteDeltas(4_000_000, 2_000_000, 2);
                Call("UpdateNetSpeedSample", telemetry.Current);
                Call("SwitchMainIsland", "netspeed"); PumpAnimations(400);
                Check(window.NetSpeedRates.UploadValue.Text == "976.6KB/s" && window.NetSpeedRates.DownloadValue.Text == "1.9MB/s"
                    && window.NetSpeedRates.SessionDescription.Contains("上传 1.9MB、下载 3.8MB") && window.NetSpeedRates.SessionDescription.Contains("下载峰值 1.9 MB/s")
                    && window.NetSpeedNotifyLayer.Visibility == System.Windows.Visibility.Visible
                    && window.IslandBody.Width == StatusDisplayOptions.Width(StatusDisplayOptions.NetworkSelected(options).Length),
                    "合并状态主岛以紧凑宽度显示速率且悬停保留真实累计峰值");
                var history = Field<List<double>>("_netSpeedDownHistory");
                for (int i = 0; i < 70; i++) Call("UpdateNetSpeedSample", telemetry.Current);
                string tip = window.NetSpeedRates.ToolTip.ToString()!;
                window.NetSpeedRates.SetSessionInfo(2_000_000, 4_000_000, 1_000_000, 2_000_000, history);
                Check(window.NetSpeedRates.ToolTip.ToString() == tip && history.Count == 60,
                    "持续刷新悬停详情不重复追加且网速曲线缓冲有界");

                telemetry.ApplyByteDeltas(82_800_000, 10_400_000, 80);
                telemetry.ApplyByteDeltas(50_400, 263_900, 1);
                history.Clear();
                for (int i = 0; i < 60; i++) history.Add(50_400 + 500_000 * Math.Pow(Math.Sin(i * 0.21), 2));
                Call("UpdateNetSpeedPrimary", telemetry.Current);
                root.UpdateLayout();
                var mainDetail = new System.Windows.Media.DrawingVisual();
                using (var draw = mainDetail.RenderOpen())
                {
                    draw.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0x74, 0x85)), null,
                        new System.Windows.Rect(0, 0, StatusDisplayOptions.Width(StatusDisplayOptions.NetworkSelected(options).Length) + 40, IslandMetrics.PillHeight + 24));
                    draw.DrawRectangle(new System.Windows.Media.VisualBrush(window.IslandBody), null,
                        new System.Windows.Rect(20, 12, StatusDisplayOptions.Width(StatusDisplayOptions.NetworkSelected(options).Length), IslandMetrics.PillHeight));
                }
                var mainBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)StatusDisplayOptions.Width(StatusDisplayOptions.NetworkSelected(options).Length) + 40, (int)IslandMetrics.PillHeight + 24,
                    96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                mainBitmap.Render(mainDetail);
                var mainEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                mainEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(mainBitmap));
                using (var stream = File.Create(Path.GetFullPath("docs/screens/network-speed-main.png"))) mainEncoder.Save(stream);
                Check(window.NetSpeedRates.UploadValue.ActualWidth > 0 && window.NetSpeedRates.DownloadValue.ActualWidth > 0,
                    "独立网速主岛上传与下载实际排版可见");
                Call("StopNetworkSpeedService");
                Check(window.NetSpeedRates.DownloadValue.Text == "—" && window.NetSpeedRates.SessionDescription == "" && history.Count == 0,
                    "关闭网速监测移除旧速率和会话数据");
                var pages = Field<IslandPageHost>("_pages");
                Check(pages.Pages.Count(x => x.Id == IslandPageIds.System) == 1 && pages.IndexOf(IslandPageIds.NetSpeed) < 0,
                    "系统状态与网速合并成一个卡片页");
                var message = new DesktopNotification("ui-message", "Contoso.Chat!App", "聊天", "项目更新",
                    "通知模块已完成更新。系统状态与网速现在可以在同一张卡片中查看。", DateTimeOffset.Now);
                Call("OnSystemToastsReceived", new[] { message with { Key = "ui-old-message", Title = "会议提醒", Body = "明天下午 3 点讨论项目进度。" } }, true);
                Check(Field<DesktopNotification?>("_desktopCurrent") == null && Field<IslandState>("_state") == IslandState.Pill,
                    "实际窗口首轮同步通知不补弹旧消息");
                Call("OnSystemToastsReceived", new[] { message }, false); PumpAnimations(400); root.UpdateLayout();
                Check(Field<IslandState>("_state") == IslandState.Pill
                    && Field<DesktopNotification?>("_desktopCurrent")?.Key == message.Key,
                    "新系统消息自动展示紧凑小岛，完整卡片由点击打开");
                Call("IslandBody_MouseLeftButtonDown", window.IslandBody,
                    new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left));
                PumpAnimations(400); root.UpdateLayout();
                var notificationView = Field<System.Windows.Controls.Grid>("_notificationsView");
                var notificationList = Field<System.Windows.Controls.StackPanel>("_notificationList");
                var messageCard = notificationList.Children.OfType<alpha_island.Controls.DesktopNotificationCard>().First();
                var buttonBottom = messageCard.OpenButton!.TranslatePoint(new System.Windows.Point(0, messageCard.OpenButton.ActualHeight), notificationView);
                Check(Field<IslandState>("_state") == IslandState.Card && pages.ActiveId == IslandPageIds.Notifications
                    && Field<DesktopNotification?>("_desktopCurrent")?.Key == message.Key && buttonBottom.Y <= notificationView.ActualHeight,
                    "点击消息小岛后打开通知卡片且打开应用按钮在首屏可见");
                Check(messageCard.TitleText.Text == message.Title && messageCard.BodyText.Text == message.Body && messageCard.OpenButton.IsHitTestVisible,
                    "消息卡片保留应用标题正文并可点击打开应用");
                Field<System.Windows.Controls.TextBlock>("_notificationAccessText").Text = "界面预览 · 使用示例消息";
                var notificationPreview = new System.Windows.Media.DrawingVisual();
                using (var draw = notificationPreview.RenderOpen())
                {
                    draw.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0x74, 0x85)), null, new System.Windows.Rect(0, 0, 470, 324));
                    DrawIslandPreview(draw, 20, 12);
                }
                var notificationBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(470, 324, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                notificationBitmap.Render(notificationPreview);
                var notificationEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); notificationEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(notificationBitmap));
                using (var stream = File.Create(Path.GetFullPath("docs/screens/windows-notification-card.png"))) notificationEncoder.Save(stream);
                var isolatedCard = new alpha_island.Controls.DesktopNotificationCard(message); bool opened = false;
                isolatedCard.OpenRequested += item => opened = item.AppId == message.AppId;
                isolatedCard.OpenButton!.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Check(opened, "打开应用交互传递系统提供的 AppID 而不执行消息正文");
                int pageBeforeWheel = pages.ActiveIndex;
                messageCard.BodyText.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
                    { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent });
                Check(pages.ActiveIndex == pageBeforeWheel, "消息正文滚轮用于阅读且不触发岛内翻页");
                SetField("_desktopExpires", DateTimeOffset.Now.AddSeconds(-1)); Call("TickDesktopNotifications");
                Check(Field<DesktopNotification?>("_desktopCurrent") == null && Field<IslandState>("_state") == IslandState.Card,
                    "消息到期不会自动关闭手动打开的通知卡片");
                Call("SetState", IslandState.Pill);
                SettingsRegistry.Write(options, "NotificationQuietMode", true); Call("OnDesktopNotificationOptionsChanged");
                Call("OnSystemToastsReceived", new[] { message with { Key = "ui-quiet-message" } }, false);
                Check(Field<DesktopNotification?>("_desktopCurrent") == null && Field<DesktopNotificationInbox>("_desktopInbox").History.Any(x => x.Key == "ui-quiet-message"),
                    "实际静默消息保留在通知中心且不自动展开");
                SettingsRegistry.Write(options, "NotificationQuietMode", false); Call("ClearDesktopInbox");
                SetField("_approvalCurrent", new ApprovalRequest("ui-approval", "test", "{}", "等待用户", 30, DateTime.UtcNow));
                layers.ShowSeconds("approval", 30); Call("SyncNotificationLayers");
                Call("OnSystemToastsReceived", new[] { message with { Key = "ui-during-approval" } }, false);
                Check(Field<DesktopNotification?>("_desktopCurrent") == null && Field<DesktopNotificationInbox>("_desktopInbox").PendingCount == 1
                    && Field<string>("_activeMainIslandKey") == "approval", "系统消息在工具审批期间排队且不抢走主岛");
                SetField("_approvalCurrent", null); layers.Hide("approval"); Call("TickDesktopNotifications");
                Check(Field<DesktopNotification?>("_desktopCurrent")?.Key == "ui-during-approval", "审批结束后展示仍有效的排队消息");
                Call("ClearDesktopInbox"); window.ApplySessionNotification(true);
                Call("OnSystemToastsReceived", new[] { message with { Key = "ui-locked-message" } }, false);
                Check(root.Visibility == System.Windows.Visibility.Hidden && Field<DesktopNotification?>("_desktopCurrent") == null,
                    "锁屏隐藏整个岛且收到消息不展示内容");
                window.ApplySessionNotification(false);
                Check(root.Visibility == System.Windows.Visibility.Visible && Field<DesktopNotificationInbox>("_desktopInbox").History.Any(x => x.Kind == DesktopEventKind.Unlocked),
                    "解锁恢复岛并记录独立系统事件");
                Call("ClearDesktopInbox");
                using var toast = new SystemToastService(System.Windows.Threading.Dispatcher.CurrentDispatcher); toast.Start();
                Check(!toast.Status.Contains("MSIX"), "普通 exe 按 Windows 实际权限监听，不以包身份提前拒绝");
                var settings = new alpha_island.Settings.SettingsWindow(persistSettings: false); settings.LoadFrom(options);
                Check(settings.FindName("SystemToastToggle") is System.Windows.Controls.CheckBox
                    && settings.FindName("NotificationBlockedAppsBox") is System.Windows.Controls.TextBox
                    && settings.FindName("NotificationDurationSlider") is System.Windows.Controls.Slider,
                    "通知设置中的开关筛选和展示时长控件实际绑定");
                settings.ForceClose = true; settings.Close();
                Call("SetMusicPinned", false); layers.SetPinned("netspeed", false); layers.HideAll(); Call("SetState", IslandState.Bar);
                Call("OnSystemToastsReceived", new[] { message with { Key = "ui-idle-message" } }, false);
                Check(Field<IslandState>("_state") == IslandState.Pill, "细条待机收到新消息时只展开小岛");
                SetField("_desktopExpires", DateTimeOffset.Now.AddSeconds(-1)); Call("TickDesktopNotifications");
                Check(Field<IslandState>("_state") == IslandState.Bar && Field<string>("_activeMainIslandKey") == "music",
                    "待机消息到期缩回细条并清理已结束的消息主岛键");
                Call("SetState", IslandState.Card); Call("SwitchCardPage", pages.IndexOf(IslandPageIds.Music), false);
                Call("OnSystemToastsReceived", new[] { message with { Key = "ui-deferred-in-card" } }, false);
                Check(pages.ActiveId == IslandPageIds.Music && Field<DesktopNotification?>("_desktopCurrent") == null
                    && Field<DesktopNotificationInbox>("_desktopInbox").PendingCount == 1,
                    "用户正在查看其他卡片时消息排队且不打断页面");
                Call("SetState", IslandState.Pill); Call("TickDesktopNotifications");
                Check(Field<DesktopNotification?>("_desktopCurrent")?.Key == "ui-deferred-in-card",
                    "离开正在阅读的卡片后展示仍有效的排队消息");
                Call("ClearDesktopInbox");

                var pillContour = alpha_island.Controls.IslandContourHandle.BuildContour(new System.Windows.Size(alpha_island.Controls.IslandContourHandle.TrackWidth, 48), new System.Windows.CornerRadius(24));
                var cardContour = alpha_island.Controls.IslandContourHandle.BuildContour(new System.Windows.Size(alpha_island.Controls.IslandContourHandle.TrackWidth, 214), new System.Windows.CornerRadius(28));
                Check(pillContour.Bounds.Width > 3 && pillContour.Bounds.Left > 1.4 && pillContour.Bounds.Bottom < 48,
                    "小岛切换线沿圆角弯曲且整个可见笔画位于岛外");
                Check(cardContour.Bounds.Width < .1 && cardContour.Bounds.Height <= 44,
                    "完整卡片的切换线变成固定长度竖线");
                bool fits = true, separated = true, gestureRangeFits = true;
                foreach (int count in new[] { 1, 2, 6, 7, 11, 18, 24 })
                foreach (bool left in new[] { false, true })
                foreach (bool up in new[] { false, true })
                {
                    var layout = ComponentMenuLayout.Create(count, left, up);
                    gestureRangeFits &= layout.ContainsGesturePoint(layout.Anchor) && layout.Centers.All(layout.ContainsGesturePoint)
                        && !layout.ContainsGesturePoint(layout.Anchor + new System.Windows.Vector(left ? 600 : -600, up ? 600 : -600));
                    for (int i = 0; i < count; i++)
                    {
                        double half = ComponentMenuLayout.Diameter / 2;
                        fits &= new System.Windows.Rect(layout.Size).Contains(new System.Windows.Rect(layout.Centers[i].X - half, layout.Centers[i].Y - half, half * 2, half * 2));
                        for (int j = 0; j < i; j++) separated &= (layout.Centers[i] - layout.Centers[j]).Length > ComponentMenuLayout.Diameter + 3;
                    }
                }
                Check(fits && separated, "组件数量增加与菜单四向展开时所有圆形图标不重叠不越界");
                Check(gestureRangeFits, "所有展开方向的取消边界均包含线条和图标且拒绝远处鼠标");
                var compactLayout = ComponentMenuLayout.Create(11);
                Check(compactLayout.Centers.Min(x => (x - compactLayout.Anchor).Length) < 56
                    && compactLayout.Centers.Max(x => (x - compactLayout.Anchor).Length) < 148,
                    "全部十一张卡片以紧凑分层弧线展开且最近图标靠近切换线");
                Call("SetState", IslandState.Pill); Call("SwitchMainIsland", "music"); root.UpdateLayout();
                var handlePoint = new System.Windows.Point(alpha_island.Controls.IslandContourHandle.ContourGap, window.ComponentHandle.ActualHeight / 2);
                var rootPoint = window.ComponentHandle.TranslatePoint(handlePoint, root);
                Check(System.Windows.Media.VisualTreeHelper.HitTest(root, rootPoint)?.VisualHit == window.ComponentHandle
                    && window.ComponentHandle.TranslatePoint(handlePoint, window.IslandBody).X > window.IslandBody.ActualWidth,
                    "岛外轮廓线能命中且不受岛体裁切");
                var enlargedHitPoint = new System.Windows.Point(21, window.ComponentHandle.ActualHeight / 2);
                var enlargedHit = System.Windows.Media.VisualTreeHelper.HitTest(root, window.ComponentHandle.TranslatePoint(enlargedHitPoint, root))?.VisualHit;
                Check(window.ComponentHandle.ContainsHandlePoint(enlargedHitPoint)
                    && enlargedHit == window.ComponentHandle,
                    "细线外扩的判定范围也能接收鼠标而可见笔画保持纤细");
                var handleScreen = window.ComponentHandle.PointToScreen(handlePoint);
                int hx = (int)Math.Round(handleScreen.X), hy = (int)Math.Round(handleScreen.Y);
                var packedPoint = new IntPtr(unchecked((hy & 0xffff) << 16 | (hx & 0xffff)));
                Check(!(bool)Call("IsPointInsideIsland", hx, hy)! && (bool)Call("IsPointInsideInteractiveZone", hx, hy)!
                    && (IntPtr)Call("WndProc", IntPtr.Zero, 0x0084, IntPtr.Zero, packedPoint, false)! != new IntPtr(-1),
                    "岛外线条属于原生交互热区而不被判为桌面穿透或岛外点击");
                Check(!window.ComponentHandle.ContainsHandlePoint(new System.Windows.Point(80, 2))
                    && !(bool)Call("IsPointOnComponentHandle", hx + 40, hy + 100)!,
                    "外置切换层的空白区域保持鼠标穿透");
                window.ComponentHandle.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
                    { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent });
                Check(Field<IslandState>("_state") == IslandState.Card && pages.ActiveId == IslandPageIds.Clipboard,
                    "岛外线条直接接收滚轮并切换到目标卡片");
                Call("SetState", IslandState.Pill); Call("SwitchMainIsland", "music"); root.UpdateLayout();
                Call("BeginComponentHold", new System.Windows.Point(), false); PumpAnimations(180);
                Check(!Field<bool>("_componentMenuVisible") && !Field<bool>("_isSwiping"), "短按阶段不弹菜单也不启动上滑收回");
                Call("CloseComponentSwitcher"); PumpAnimations(300);
                Check(!Field<bool>("_componentMenuVisible"), "提前松开会停止长按定时器");
                Call("BeginComponentHold", new System.Windows.Point(), false); PumpAnimations(450);
                var radial = Field<alpha_island.Controls.ComponentRadialMenu>("_componentMenu");
                Check(Field<bool>("_componentMenuVisible") && radial.ItemIds.SequenceEqual(pages.Pages.Select(x => x.Id)),
                    "长按四百毫秒展开全部可用卡片而非仅包含溢出组件的圆形菜单");
                Check(window.FindName("PageDotStack") == null && window.FindName("PageIndicatorBorder") == null,
                    "卡片界面已彻底移除旧圆点分页入口");
                Call("HoverTimer_Tick", null, EventArgs.Empty);
                Check(Field<IslandState>("_state") == IslandState.Pill && Field<int>("_hoverOutTicks") == 0,
                    "长按菜单期间鼠标离开岛体不会导致自动收回");
                var systemCenter = radial.Layout.Centers[pages.IndexOf(IslandPageIds.System)];
                string beforeMenuSelection = pages.ActiveId;
                Call("UpdateComponentHover", systemCenter, false); PumpAnimations(500); Call("UpdateComponentHover", systemCenter, false);
                Check(Field<bool>("_componentMenuVisible") && Field<IslandState>("_state") == IslandState.Pill
                    && pages.ActiveId == beforeMenuSelection && radial.CaptionText == pages.Pages[pages.IndexOf(IslandPageIds.System)].Title,
                    "鼠标停留图标仅高亮并持续显示完整说明而不提前打开卡片");
                window.ComponentHandle.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
                    { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent });
                Check(Field<bool>("_componentMenuVisible") && pages.ActiveId == beforeMenuSelection,
                    "按住选择菜单期间滚轮不会提前打开其他卡片");
                Call("UpdateComponentHover", systemCenter, true);
                root.UpdateLayout();
                Check(!Field<bool>("_componentMenuVisible") && pages.ActiveId == IslandPageIds.System
                    && Field<IslandState>("_state") == IslandState.Card && pages.ActivePage!.View.IsHitTestVisible
                    && window.CardCarouselHost.IsHitTestVisible,
                    "小岛长按移向系统图标并松手后才打开系统卡片并关闭菜单");
                Call("BeginComponentHold", new System.Windows.Point(), false); Call("OpenComponentMenu", false);
                var clipboardCenter = radial.Layout.Centers[pages.IndexOf(IslandPageIds.Clipboard)];
                Call("UpdateComponentHover", clipboardCenter, false);
                Call("UpdateComponentHover", radial.Layout.Anchor + new System.Windows.Vector(12, 12), true);
                Check(!Field<bool>("_componentMenuVisible") && !Field<bool>("_componentPressActive") && pages.ActiveId == IslandPageIds.System,
                    "预选图标后在菜单空白处松手会取消且不会提交上一候选");
                Call("BeginComponentHold", new System.Windows.Point(), false); Call("OpenComponentMenu", false);
                Call("UpdateComponentHover", clipboardCenter, false);
                Call("UpdateComponentHover", radial.Layout.Anchor + new System.Windows.Vector(400, 0), false);
                Call("UpdateComponentHover", clipboardCenter, true);
                Check(!Field<bool>("_componentMenuVisible") && !Field<bool>("_componentPressActive") && pages.ActiveId == IslandPageIds.System
                    && !Field<System.Windows.Threading.DispatcherTimer>("_componentPointerTimer").IsEnabled,
                    "鼠标移出缓冲边界立即取消且移回松手也不会误触提交");
                Check(Math.Abs(window.ComponentHandle.TranslatePoint(new System.Windows.Point(), window.IslandBody).X - window.IslandBody.Width) < .5
                    && Math.Abs(window.ComponentHandle.ActualHeight - window.IslandBody.Height) < .5,
                    "从小岛进入完整卡片后外置轮廓线实时跟随右边缘和高度");
                var paintedRoot = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32); paintedRoot.Render(root);
                var paintedLinePoint = window.ComponentHandle.TranslatePoint(new System.Windows.Point(alpha_island.Controls.IslandContourHandle.ContourGap,
                    window.ComponentHandle.ActualHeight / 2), root);
                byte[] linePixel = new byte[4];
                paintedRoot.CopyPixels(new System.Windows.Int32Rect((int)Math.Round(paintedLinePoint.X), (int)Math.Round(paintedLinePoint.Y), 1, 1), linePixel, 4, 0);
                Check(linePixel[3] > 100 && linePixel[0] > linePixel[2] + 40 && linePixel[1] > linePixel[2] + 40,
                    "实际窗口渲染中岛外线条可见且不被父容器裁切");
                bool followsMorph = true;
                alpha_island.Animation.MotionPolicy.Apply(false, false);
                try
                {
                    Call("SetState", IslandState.Pill); PumpAnimations(500);
                    Call("SetState", IslandState.Card, false, IslandPageIds.System);
                    for (int i = 0; i < 8; i++)
                    {
                        PumpAnimations(40); root.UpdateLayout();
                        var linePoint = window.ComponentHandle.TranslatePoint(new System.Windows.Point(alpha_island.Controls.IslandContourHandle.ContourGap,
                            window.ComponentHandle.ActualHeight / 2), window.IslandBody);
                        followsMorph &= linePoint.X > window.IslandBody.ActualWidth
                            && Math.Abs(linePoint.Y - window.IslandBody.ActualHeight / 2) < .5;
                    }
                    Check(followsMorph, "真实展开动画中外置线条同步挤压位移且始终位于岛外");
                }
                finally { alpha_island.Animation.MotionPolicy.Apply(true, false); }
                PumpAnimations(500);

                var allCardPages = pages.Pages.ToArray();
                bool allOpenFromPill = true, allSwitchInCard = true;
                // Exercise every page without invoking weather location or network requests in offline UI checks.
                bool weatherBusy = Field<bool>("_weatherBusy"); SetField("_weatherBusy", true);
                try
                {
                    foreach (var page in allCardPages)
                    {
                        Call("SetState", IslandState.Pill); Call("SelectComponent", page.Id); root.UpdateLayout();
                        allOpenFromPill &= Field<IslandState>("_state") == IslandState.Card && pages.ActiveId == page.Id
                            && page.View.IsHitTestVisible && page.View.Visibility == System.Windows.Visibility.Visible
                            && window.CardCarouselHost.IsHitTestVisible;
                    }
                    foreach (var page in allCardPages.Reverse())
                    {
                        Call("SelectComponent", page.Id); root.UpdateLayout();
                        allSwitchInCard &= Field<IslandState>("_state") == IslandState.Card && pages.ActiveId == page.Id
                            && page.View.IsHitTestVisible && pages.Pages.Count(x => x.View.IsHitTestVisible) == 1;
                    }
                    Check(allOpenFromPill, "全部原有卡片从小岛菜单选择后统一展开对应卡片");
                    Check(allSwitchInCard, "完整卡片内全部页面可往返切换且只命中当前卡片");
                    Call("SetState", IslandState.Pill); Call("SwitchMainIsland", "music"); Call("StepComponent", 1);
                    Check(Field<IslandState>("_state") == IslandState.Card && pages.ActiveId == allCardPages[1].Id,
                        "小岛单击线条的下一页操作也进入卡片分页");
                    Call("SelectComponent", allCardPages[^1].Id); Call("StepComponent", 1);
                    bool wrappedForward = pages.ActiveId == allCardPages[0].Id;
                    Call("StepComponent", -1);
                    Check(wrappedForward && pages.ActiveId == allCardPages[^1].Id && Field<IslandState>("_state") == IslandState.Card,
                        "线条切换支持首尾双向循环且始终保持卡片形态");
                }
                finally { SetField("_weatherBusy", weatherBusy); }

                void SaveSwitcherPreview(string file)
                {
                    PumpAnimations(300);
                    Call("OpenComponentMenu", false);
                    var menu = Field<alpha_island.Controls.ComponentRadialMenu>("_componentMenu");
                    double previewBodyHeight = window.IslandBody.Height;
                    double bias = Math.Max(0, menu.Layout.Anchor.Y - (previewBodyHeight / 2 + 16));
                    menu.ArrangeArc(false, false, window.ComponentHandle.ActualHeight, window.ComponentHandle.ContourCornerRadius.TopRight, bias);
                    menu.Reveal(false);
                    menu.Measure(menu.Layout.Size); menu.Arrange(new System.Windows.Rect(menu.Layout.Size)); menu.UpdateLayout(); root.UpdateLayout();
                    double w = window.IslandBody.Width, h = window.IslandBody.Height;
                    double menuX = 20 + w + alpha_island.Controls.IslandContourHandle.ContourGap - menu.Layout.Anchor.X,
                        menuY = h / 2 + 16 - menu.Layout.Anchor.Y;
                    menuX += ComponentMenuLayout.HandleOffset;
                    int imageWidth = (int)Math.Ceiling(menuX + menu.Width + 16), imageHeight = (int)Math.Ceiling(Math.Max(h + 32, menuY + menu.Height + 16));
                    var visual = new System.Windows.Media.DrawingVisual();
                    using (var draw = visual.RenderOpen())
                    {
                        draw.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0x74, 0x85)), null, new System.Windows.Rect(0, 0, imageWidth, imageHeight));
                        DrawIslandPreview(draw, 20, 16);
                        draw.DrawRectangle(new System.Windows.Media.VisualBrush(menu), null, new System.Windows.Rect(menuX, menuY, menu.Width, menu.Height));
                    }
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(imageWidth, imageHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.GetFullPath(file)); png.Save(output);
                    Call("CloseComponentSwitcher");
                }
                Call("SetState", IslandState.Pill); Call("SwitchMainIsland", "netspeed"); root.UpdateLayout();
                window.NetSpeedRates.SetRates(263900, 50400);
                SaveSwitcherPreview("docs/screens/component-switcher-pill.png");
                Call("SelectComponent", IslandPageIds.Clipboard); root.UpdateLayout();
                Check(Field<IslandState>("_state") == IslandState.Card && pages.ActiveId == IslandPageIds.Clipboard,
                    "圆形菜单直达指定卡片而不被活跃通知选页覆盖");
                var actualCardLine = alpha_island.Controls.IslandContourHandle.BuildContour(window.ComponentHandle.RenderSize, window.ComponentHandle.ContourCornerRadius);
                Check(actualCardLine.Bounds.Width < .1 && Math.Abs(window.ComponentHandle.ContourCornerRadius.TopRight - window.IslandBody.CornerRadius.TopRight) < .1,
                    "实际卡片轮廓线绑定岛体圆角且展开为竖线");
                Call("SelectComponent", IslandPageIds.System); root.UpdateLayout();
                SaveSwitcherPreview("docs/screens/component-switcher-card.png");
                Call("OpenComponentMenu", false);
                window.ComponentHandle.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, Environment.TickCount, System.Windows.Input.Key.Escape)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                Check(!Field<bool>("_componentMenuVisible") && Field<IslandState>("_state") == IslandState.Card,
                    "Esc 取消圆形菜单并保留当前组件");
                Call("TryWheelSwitchComponent", -60);
                string beforeWheel = pages.ActiveId;
                Call("TryWheelSwitchComponent", -60);
                Check(pages.ActiveId != beforeWheel, "轮廓线滚轮支持高精度增量累计切换");
                SetField("_approvalCurrent", new ApprovalRequest("switcher-approval", "test", "{}", "等待用户", 30, DateTime.UtcNow));
                Call("BeginComponentHold", new System.Windows.Point(), false); Call("SelectComponent", IslandPageIds.Clipboard);
                Check(!Field<bool>("_componentPressActive") && pages.ActiveId != IslandPageIds.Clipboard,
                    "待审批期间组件手势不会隐藏审批或切走页面");
                SetField("_approvalCurrent", null);
                Call("OpenComponentMenu", false); window.ApplySessionNotification(true);
                Check(!Field<bool>("_componentMenuVisible") && !Field<bool>("_componentPressActive"), "锁屏立即关闭圆形菜单并清理长按状态");
                window.ApplySessionNotification(false); Call("ClearDesktopInbox");
                pages.Register(new DelegateIslandPage("regression.dynamic", "动态测试页", new System.Windows.Controls.Grid(), 99,
                    IslandMetrics.CardHeightLyrics, available: o => SettingsRegistry.GetBool(o, "AiEnabled", false)));
                pages.Sync(options);
                Call("OpenComponentMenu", false); int previousCount = pages.Count;
                SettingsRegistry.Write(options, "AiEnabled", true); pages.Sync(options);
                Check(!Field<bool>("_componentMenuVisible") && pages.Count == previousCount + 1,
                    "组件可用性改变时关闭旧菜单避免图标索引串页");
                Call("OpenComponentMenu", false);
                Check(Field<alpha_island.Controls.ComponentRadialMenu>("_componentMenu").ItemIds.SequenceEqual(pages.Pages.Select(x => x.Id))
                    && Field<alpha_island.Controls.ComponentRadialMenu>("_componentMenu").ItemIds.Contains("regression.dynamic"),
                    "重新展开圆形菜单保留全部可用卡片并包含刚启用的动态组件");
                pages.Unregister("regression.dynamic");
                Call("CloseComponentSwitcher");
                var nativeMenu = Field<alpha_island.Controls.ComponentRadialMenu>("_componentMenu");
                nativeMenu.Visibility = System.Windows.Visibility.Hidden; nativeMenu.IsHitTestVisible = false;
                Call("OpenComponentMenu", true); Call("BeginComponentKeyboardNavigation");
                // 离屏 Popup 的呈现测试不依赖用户此时在桌面上的真实鼠标位置。
                Field<System.Windows.Threading.DispatcherTimer>("_componentPointerTimer").Stop(); PumpAnimations(80);
                var nativePopup = Field<System.Windows.Controls.Primitives.Popup>("_componentPopup");
                Check(nativePopup.IsOpen && ReferenceEquals(nativePopup.PlacementTarget, window.ComponentHandle)
                    && System.Windows.PresentationSource.FromVisual(nativeMenu) != null && nativeMenu.ActualWidth > 0,
                    "真实弹出窗口具备独立呈现源并锚定轮廓线布局");
                window.ComponentHandle.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, Environment.TickCount, System.Windows.Input.Key.Right)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                string keyboardTarget = Field<string>("_componentHoveredId"); Call("PollComponentPointer", false);
                Check(Field<string>("_componentHoveredId") == keyboardTarget, "键盘选中的组件不会被静止鼠标的轮询清除");
                Call("SetState", IslandState.Pill); PumpAnimations(120);
                Check(!nativePopup.IsOpen && !Field<bool>("_componentMenuVisible"), "外部形态变化正确关闭真实弹出窗口");
                nativeMenu.Visibility = System.Windows.Visibility.Visible; nativeMenu.IsHitTestVisible = true;
                Call("CloseComponentSwitcher"); Call("SetState", IslandState.Bar); root.UpdateLayout();
                Check(window.ComponentHandle.Visibility == System.Windows.Visibility.Collapsed, "待机细条隐藏切换线不影响原有收回外观");

                var lyricLines = new[]
                {
                    new LyricLine { Time = TimeSpan.Zero, Text = "窗外的云慢慢走远" },
                    new LyricLine { Time = TimeSpan.FromSeconds(4), Text = "把这一刻轻轻留在心里", Translation = "Keep this moment close to your heart" },
                    new LyricLine { Time = TimeSpan.FromSeconds(8), Text = "让音乐陪我们继续前行" },
                    new LyricLine { Time = TimeSpan.FromSeconds(12), Text = "走向远方，迎接新的风景" }
                };
                Call("ApplyLyrics", new LyricsDocument(lyricLines, true, true, "离线示例"));
                Call("SelectComponent", IslandPageIds.Music);
                options.ShowTranslation = false;
                Call("ApplyLyricTexts", lyricLines, 1); root.UpdateLayout();
                window.TitleText.Text = "夜色之间 · 示例曲目";
                window.ArtistText.Text = "Alpha Island"; window.AlbumText.Text = "音乐卡片预览";
                window.PositionText.Text = "1:28"; window.DurationText.Text = "3:42";
                Call("SetMusicProgress", .58); root.UpdateLayout();
                System.Windows.Rect Bounds(System.Windows.FrameworkElement element) =>
                    new(element.TranslatePoint(new System.Windows.Point(), window.IslandBody), element.RenderSize);
                Check(pages.ActiveId == IslandPageIds.Music && window.IslandBody.Height == IslandMetrics.MusicCardHeightLyrics
                    && Bounds(window.LyricPrev).Bottom + 6 < Bounds(window.LyricCurrentHost).Top
                    && Bounds(window.LyricCurrentHost).Bottom + 6 < Bounds(window.LyricNext).Top,
                    "歌词三行留有清晰间距且仅音乐卡片使用新增高度");
                Check(Bounds(window.ProgressTrack).Bottom <= window.IslandBody.Height - 8
                    && Bounds(window.TitleText).Top >= 8 && window.LyricTranslation.Visibility == System.Windows.Visibility.Collapsed,
                    "歌词与进度条完整位于岛内，无翻译不留空白行");
                Check(window.LyricCurrentHost.Effect == null && window.LyricCurrentBright.Text == window.LyricCurrentDim.Text
                    && window.LyricCurrentBright.TranslatePoint(new System.Windows.Point(), window.LyricCurrentHost)
                        == window.LyricCurrentDim.TranslatePoint(new System.Windows.Point(), window.LyricCurrentHost),
                    "当前歌词清晰且逐字高亮与底层文字准确重合");
                double progressY = Bounds(window.ProgressTrack).Top;
                SetField("_lyricIndex", 1);
                options.ShowTranslation = true; Call("SyncCurrentLyricTranslation"); root.UpdateLayout();
                Check(window.LyricTranslation.Visibility == System.Windows.Visibility.Visible
                    && Bounds(window.LyricTranslation).Bottom < Bounds(window.LyricNext).Top
                    && Math.Abs(Bounds(window.ProgressTrack).Top - progressY) < .1,
                    "当前句不变时翻译开关即时更新，不挤压相邻歌词或改变进度条位置");

                void SaveMusicPreview(string file)
                {
                    root.UpdateLayout();
                    int width = (int)window.IslandBody.Width + 80, height = (int)window.IslandBody.Height + 32;
                    var visual = new System.Windows.Media.DrawingVisual();
                    using (var draw = visual.RenderOpen())
                    {
                        draw.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x50, 0x60, 0x78)), null,
                            new System.Windows.Rect(0, 0, width, height));
                        DrawIslandPreview(draw, 20, 16);
                    }
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.GetFullPath(file)); png.Save(output);
                }
                options.ShowTranslation = false; Call("ApplyLyricTexts", lyricLines, 1); root.UpdateLayout();
                Field<System.Windows.Media.RectangleGeometry>("_lyricWipe").Rect = new System.Windows.Rect(0, 0,
                    window.LyricCurrentBright.ActualWidth * .58, window.LyricCurrentBright.ActualHeight);
                SaveMusicPreview("docs/screens/music-lyrics.png");
                double hitHeight = window.ProgressTrack.ActualHeight;
                double fillWidth = window.ProgressFill.ActualWidth;
                window.ProgressTrack.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent }); root.UpdateLayout();
                Check(window.ProgressCapsule.ActualHeight > 2 * 5 && window.ProgressTrack.ActualHeight == hitHeight
                    && Math.Abs(window.ProgressFill.ActualWidth - fillWidth) < .1,
                    "悬停进度条变粗但不改变命中区、时间位置或播放比例");
                SaveMusicPreview("docs/screens/music-progress-hover.png");
                window.ProgressTrack.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent }); root.UpdateLayout();
                Check(window.ProgressCapsule.ActualHeight == 5 && hitHeight >= 24, "离开后进度条恢复纤细且点击区域易操作");
                Call("SetMusicProgress", 0); root.UpdateLayout(); bool emptyProgress = window.ProgressFill.ActualWidth == 0;
                Call("SetMusicProgress", 1); root.UpdateLayout();
                var progressClip = (System.Windows.Media.RectangleGeometry)window.ProgressCapsule.Clip;
                Check(emptyProgress && Math.Abs(window.ProgressFill.ActualWidth - window.ProgressCapsule.ActualWidth) < .1
                    && progressClip.RadiusX == window.ProgressCapsule.ActualHeight / 2
                    && window.ProgressFill.CornerRadius.TopRight == 0,
                    "播放首尾正确填充圆角轨道，中间进度分界保持平直");

                int renderTier = alpha_island.Animation.MotionPolicy.RenderTier;
                alpha_island.Animation.MotionPolicy.OverrideRenderTierForDebug(2);
                alpha_island.Animation.MotionPolicy.Apply(false, false);
                Call("ApplyLyricTexts", lyricLines, 2); PumpAnimations(100);
                Check(window.LyricCurrentHost.Effect is System.Windows.Media.Effects.BlurEffect animatedBlur && animatedBlur.Radius > 0
                    && ((System.Windows.Media.TranslateTransform)window.LyricCurrentHost.RenderTransform).Y > 0,
                    "换句过程中歌词产生短暂动态模糊和向上过渡");
                Call("ApplyLyricTexts", lyricLines, 1); Call("ApplyLyricTexts", lyricLines, 3); PumpAnimations(450);
                Check(window.LyricCurrentDim.Text == lyricLines[3].Text && window.LyricCurrentHost.Effect == null
                    && ((System.Windows.Media.TranslateTransform)window.LyricCurrentHost.RenderTransform).Y == 0,
                    "快速连续换句后保持最新歌词并恢复清晰位置");
                Call("SetMusicProgress", .58); Call("SetMusicProgressHovered", true); PumpAnimations(60);
                Check(window.ProgressCapsule.ActualHeight > 5 && window.ProgressCapsule.ActualHeight < 12
                    && window.ProgressTrack.ActualHeight == hitHeight, "进度条悬停在稳定命中区域内平滑变粗");
                Call("SetMusicProgressHovered", false); PumpAnimations(30); Call("SetMusicProgressHovered", true); PumpAnimations(220);
                Check(window.ProgressCapsule.ActualHeight == 12
                    && Math.Abs(window.ProgressFill.ActualWidth / window.ProgressCapsule.ActualWidth - .58) < .001,
                    "快速移入移出进度条后厚度与播放比例正确，不残留旧动画");
                Call("SetMusicProgressHovered", false); PumpAnimations(200);
                Call("SelectComponent", IslandPageIds.Clipboard); PumpAnimations(450); root.UpdateLayout();
                Check(Math.Abs(window.IslandBody.Height - IslandMetrics.CardHeightLyrics) < .5 && window.CardViewport.Height == 194,
                    "正常动画切出加高歌词卡片后恢复通用页面尺寸");
                Call("SelectComponent", IslandPageIds.Music); Call("SelectComponent", IslandPageIds.Clipboard);
                Call("SelectComponent", IslandPageIds.Music); PumpAnimations(450); root.UpdateLayout();
                Check(pages.ActiveId == IslandPageIds.Music && !Field<bool>("_cardAnimating")
                    && Math.Abs(window.IslandBody.Height - IslandMetrics.MusicCardHeightLyrics) < .5
                    && Bounds(window.ProgressTrack).Bottom <= window.IslandBody.Height - 8,
                    "不同高度卡片快速往返后音乐内容与进度条完整显示且动画正确结束");
                alpha_island.Animation.MotionPolicy.Apply(false, true); Call("ApplyLyricTexts", lyricLines, 1);
                Check(window.LyricCurrentHost.Effect == null && !((System.Windows.Media.TranslateTransform)window.LyricCurrentHost.RenderTransform).HasAnimatedProperties,
                    "节能模式停用歌词装饰动画");
                alpha_island.Animation.MotionPolicy.Apply(true, false);
                alpha_island.Animation.MotionPolicy.OverrideRenderTierForDebug(renderTier);
                Call("SelectComponent", IslandPageIds.Clipboard); root.UpdateLayout();
                Check(window.IslandBody.Height == IslandMetrics.CardHeightLyrics && window.CardViewport.Height == 194,
                    "后台歌词不会撑高其他详情卡片");
                Call("ApplyLyrics", (object?)null); root.UpdateLayout();
                Check(pages.ActiveId == IslandPageIds.Clipboard && window.IslandBody.Height == IslandMetrics.CardHeightLyrics,
                    "后台歌词有无变化不抢走用户当前卡片");
                Call("ApplyLyrics", new LyricsDocument(lyricLines, true, true, "离线示例"));
                Call("SelectComponent", IslandPageIds.Music); Call("ApplyLyrics", (object?)null); root.UpdateLayout();
                Check(window.IslandBody.Height == IslandMetrics.MusicCardHeight && Bounds(window.TitleText).Top >= 8
                    && Bounds(window.ProgressTrack).Bottom <= window.IslandBody.Height - 8,
                    "纯音乐恢复紧凑卡片且封面信息与进度条不被裁切");

                Call("SetMusicPinned", false);
                foreach (string key in layers.PinnedKeys.ToArray()) layers.SetPinned(key, false);
                layers.HideAll(); Call("ClearDesktopInbox");
                Call("SelectComponent", IslandPageIds.Volume);
                SetField("_componentSelectedTick", Environment.TickCount64 - 1000);
                for (int i = 0; i < 8; i++) Call("HoverTimer_Tick", null, EventArgs.Empty);
                Check(Field<IslandState>("_state") == IslandState.Card && pages.ActiveId == IslandPageIds.Volume,
                    "在线条或菜单手动选页后鼠标仍在岛外也等待进入，不立即缩回");
                Check(!(bool)Call("HoldSelectedComponentCard", true)! && !Field<bool>("_componentCardAwaitingPointer"),
                    "进入卡片操作区后解除等待并恢复普通悬停策略");
                var oldOutsideClick = new alpha_island.Interop.MouseButtonDownEventArgs(0, 800, 0x201);
                Call("OnGlobalButtonDown", null, oldOutsideClick);
                Call("SelectComponent", IslandPageIds.Clipboard); PumpAnimations(80);
                Check(Field<IslandState>("_state") == IslandState.Card && pages.ActiveId == IslandPageIds.Clipboard,
                    "选页前入队的全局点击不会按变化后的几何误收起卡片");
                PumpAnimations(30);
                Call("OnGlobalButtonDown", null, new alpha_island.Interop.MouseButtonDownEventArgs(0, 800, 0x201)); PumpAnimations(120);
                Check(Field<IslandState>("_state") != IslandState.Card, "手动选页后新的明确岛外点击仍可收起卡片");

                using (var testVolume = new VolumeService(System.Windows.Threading.Dispatcher.CurrentDispatcher))
                {
                    // 不启动端点线程，验证目标写入与 WPF 输入，不改变用户的真实系统音量。
                    SetField("_volumeService", testVolume);
                    try
                    {
                        testVolume.SetVolume(20); Call("SelectComponent", IslandPageIds.Volume); root.UpdateLayout();
                        var volumeDown = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                            Environment.TickCount, System.Windows.Input.MouseButton.Left)
                            { RoutedEvent = System.Windows.UIElement.PreviewMouseLeftButtonDownEvent, Source = window.CardVolumeSliderTrack };
                        // WPF 的特定按键预览事件由原生输入在每层直接重发；显式复现父级、子级顺序。
                        Call("IslandBody_PreviewMouseLeftButtonDown", window.IslandBody, volumeDown);
                        window.CardVolumeSliderTrack.RaiseEvent(volumeDown);
                        Check(Field<bool>("_isDraggingVolumeSlider") && !Field<bool>("_isSwiping") && window.CardVolumeSliderTrack.IsMouseCaptured,
                            "真实 WPF 音量条按下获得捕获且父级收起手势让出拖动");
                        Call("EndVolumeDrag");
                        double volumeWidth = window.CardVolumeSliderTrack.ActualWidth;
                        Call("BeginVolumeDrag", window.CardVolumeSliderTrack, volumeWidth * .25, false);
                        SetField("_volumeWriteStamp", System.Diagnostics.Stopwatch.GetTimestamp());
                        Call("UpdateVolumeFromSliderPosition", volumeWidth * .8); Call("SyncVolumeCardContent"); root.UpdateLayout();
                        Check(window.CardVolumePercentText.Text == "80%" && Math.Abs(window.CardVolumeSliderFill.ActualWidth / volumeWidth - .8) < .001,
                            "连续拖动即时更新音量百分比与填充，旧服务读数不会拉回画面");
                        PumpAnimations(70);
                        Check(testVolume.CurrentPercent == 80 && Field<bool>("_isDraggingVolumeSlider") && Field<int>("_pendingVolumePercent") == -1,
                            "按住暂停移动时也提交最新音量，不必等待下一次移动或松手");
                        Call("UpdateVolumeFromSliderPosition", volumeWidth * .6); Call("EndVolumeDrag");
                        Check(testVolume.CurrentPercent == 60 && !Field<bool>("_isDraggingVolumeSlider")
                            && !window.CardVolumeSliderTrack.IsMouseCaptured, "松手写入最终音量并清理捕获与拖动状态");
                        Call("BeginVolumeDrag", window.CardVolumeSliderTrack, -20.0, false); Call("EndVolumeDrag");
                        bool clampedLow = testVolume.CurrentPercent == 0;
                        Call("BeginVolumeDrag", window.CardVolumeSliderTrack, volumeWidth + 40, false); Call("EndVolumeDrag");
                        Check(clampedLow && testVolume.CurrentPercent == 100, "拖出音量条两端时正确限制到零和百分之百");
                        Call("BeginVolumeDrag", window.CardVolumeSliderTrack, volumeWidth * .42, false);
                        window.CardVolumeSliderTrack.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                        { RoutedEvent = System.Windows.Input.Mouse.LostMouseCaptureEvent });
                        Check(testVolume.CurrentPercent == 42 && !Field<bool>("_isDraggingVolumeSlider"), "音量条丢失捕获时补写最后位置且不留下拖动状态");
                        Call("SetState", IslandState.Pill); Call("ShowVolumeNotify", 42, false); root.UpdateLayout();
                        double compactWidth = window.VolumeTrackContainer.ActualWidth;
                        Call("BeginVolumeDrag", window.VolumeTrackContainer, compactWidth * .3, false);
                        Call("UpdateVolumeFromSliderPosition", compactWidth * .72); Call("EndVolumeDrag"); root.UpdateLayout();
                        Check(testVolume.CurrentPercent == 72 && window.VolumeText.Text == "72%"
                            && Math.Abs(window.VolumeProgressFill.ActualWidth / compactWidth - .72) < .001,
                            "小岛音量条也支持连续拖动并同步完整卡片读数");
                        window.VolumeTrackContainer.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                        { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent }); root.UpdateLayout();
                        Check(window.VolumeCapsule.ActualHeight == 12 && window.VolumeTrackContainer.ActualHeight == 24
                            && ((System.Windows.Media.RectangleGeometry)window.VolumeCapsule.Clip).RadiusX == 6,
                            "小岛音量条采用与音乐一致的圆角双色轨道和悬停加粗效果");
                        window.VolumeTrackContainer.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                        { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
                        Call("SelectComponent", IslandPageIds.Volume); root.UpdateLayout();
                        int previousTier = alpha_island.Animation.MotionPolicy.RenderTier;
                        alpha_island.Animation.MotionPolicy.OverrideRenderTierForDebug(2);
                        alpha_island.Animation.MotionPolicy.Apply(false, false);
                        Call("SetVolumeTrackHovered", window.CardVolumeSliderTrack, true); PumpAnimations(60);
                        Check(window.CardVolumeCapsule.ActualHeight > 5 && window.CardVolumeCapsule.ActualHeight < 12
                            && window.CardVolumeSliderTrack.ActualHeight == 28, "完整音量条悬停平滑变粗且拖动区域保持稳定");
                        Call("SetVolumeTrackHovered", window.CardVolumeSliderTrack, false); PumpAnimations(200);
                        Call("OpenComponentMenu", false);
                        var contourMenu = Field<alpha_island.Controls.ComponentRadialMenu>("_componentMenu");
                        contourMenu.Measure(contourMenu.Layout.Size); contourMenu.Arrange(new System.Windows.Rect(contourMenu.Layout.Size)); contourMenu.UpdateLayout();
                        var cardOffsets = contourMenu.Layout.Centers.Take(6).Select(p => p.X - contourMenu.Layout.Anchor.X).ToArray();
                        Check(cardOffsets.Max() - cardOffsets.Min() < .1
                            && contourMenu.Layout.Centers.Any(p => p.Y < contourMenu.Layout.Anchor.Y)
                            && contourMenu.Layout.Centers.Any(p => p.Y > contourMenu.Layout.Anchor.Y),
                            "完整卡片的图标紧靠竖线排列在上下两侧，而非集中在远处下方");
                        var originalHeight = window.IslandBody.Height; var originalCorner = window.IslandBody.CornerRadius;
                        window.IslandBody.Height = 80; window.IslandBody.CornerRadius = new System.Windows.CornerRadius(32); root.UpdateLayout();
                        Call("UpdateComponentMenuPlacement");
                        var curvedOffsets = contourMenu.Layout.Centers.Take(6).Select(p => p.X - contourMenu.Layout.Anchor.X).ToArray();
                        Check(curvedOffsets.Max() - curvedOffsets.Min() > 1, "菜单图标的位置随实际岛体高度和圆角变化并沿轮廓弯曲");
                        window.IslandBody.Height = originalHeight; window.IslandBody.CornerRadius = originalCorner; root.UpdateLayout();
                        Call("UpdateComponentMenuPlacement");
                        // 关闭的 Popup 不排列子控件；把菜单接到同一离屏呈现根，验证实际动画坐标。
                        var animationPopup = Field<System.Windows.Controls.Primitives.Popup>("_componentPopup");
                        animationPopup.Child = null;
                        root.Children.Add(contourMenu);
                        contourMenu.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
                        contourMenu.VerticalAlignment = System.Windows.VerticalAlignment.Top;
                        contourMenu.Measure(contourMenu.Layout.Size); contourMenu.Arrange(new System.Windows.Rect(contourMenu.Layout.Size)); contourMenu.UpdateLayout();
                        root.UpdateLayout();
                        contourMenu.Reveal(); PumpAnimations(65);
                        var circles = contourMenu.Children.OfType<System.Windows.Controls.Border>().Where(b => b.Child is AlphaIsland.PluginSdk.GlyphIcon t && t.FontSize == 17).ToArray();
                        var firstCircle = circles[0];
                        double scale = ((System.Windows.Media.ScaleTransform)((System.Windows.Media.TransformGroup)firstCircle.RenderTransform).Children[0]).ScaleX;
                        Check(scale > .65 && scale < 1 && circles[^1].Opacity < firstCircle.Opacity,
                            "图标从线条旁逐个淡入、放大和展开，尚未出现的图标不会闪现");
                        var visibleCenter = firstCircle.TranslatePoint(new System.Windows.Point(17, 17), contourMenu);
                        Check(contourMenu.ItemAt(visibleCenter) == contourMenu.ItemIds[0], "出现动画期间按实际图标位置命中而非提前使用终点位置");
                        PumpAnimations(350);
                        Check(circles.All(b => b.Opacity == 1 && ((System.Windows.Media.ScaleTransform)((System.Windows.Media.TransformGroup)b.RenderTransform).Children[0]).ScaleX == 1),
                            "图标出现动画结束后全部恢复完整大小和透明度");
                        root.Children.Remove(contourMenu); animationPopup.Child = contourMenu;
                        Call("CloseComponentSwitcher");
                        alpha_island.Animation.MotionPolicy.Apply(true, false);
                        alpha_island.Animation.MotionPolicy.OverrideRenderTierForDebug(previousTier);
                        Call("SetVolumeTrackHovered", window.CardVolumeSliderTrack, false); root.UpdateLayout();
                        SaveMusicPreview("docs/screens/volume-card.png");
                        Call("SetVolumeTrackHovered", window.CardVolumeSliderTrack, true); root.UpdateLayout();
                        SaveMusicPreview("docs/screens/volume-card-hover.png");
                    }
                    finally { Call("EndVolumeDrag"); SetField("_volumeService", null); }
                }
                MediaHandoffUiChecks(window, root);
                PausedMediaVisibilityChecks(window, root);
                AgentApprovalInteractionChecks(window, root);
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                window?.Close();
                alpha_island.Animation.MotionPolicy.Apply(reduceMotion, powerSave);
                app?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void PackageAssets()
    {
        string assets = Path.GetFullPath("bin/MsixStage/Assets"); Directory.CreateDirectory(assets);
        using var icon = new System.Drawing.Icon(Path.GetFullPath("Assets/alpha-island.ico"), new System.Drawing.Size(256, 256));
        using var bitmap = icon.ToBitmap();
        foreach (int size in new[] { 44, 50, 150 })
        {
            using var logo = new System.Drawing.Bitmap(size, size);
            using var drawing = System.Drawing.Graphics.FromImage(logo);
            drawing.Clear(System.Drawing.Color.Transparent); drawing.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            drawing.DrawImage(bitmap, new System.Drawing.Rectangle(0, 0, size, size));
            logo.Save(Path.Combine(assets, size == 50 ? "StoreLogo.png" : $"Square{size}Logo.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        string signing = Path.GetFullPath("bin/PackageSigning"); Directory.CreateDirectory(signing);
        string pfx = Path.Combine(signing, "alpha-island-development.pfx");
        if (!File.Exists(pfx))
        {
            using var rsa = System.Security.Cryptography.RSA.Create(3072);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Alpha Island Development", rsa,
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
                new System.Security.Cryptography.OidCollection { new("1.3.6.1.5.5.7.3.3") }, true));
            request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
            File.WriteAllBytes(pfx, certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, ""));
            File.WriteAllBytes(Path.Combine(signing, "alpha-island-development.cer"), certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert));
        }
        System.Console.WriteLine("Package logos and local development signing files prepared; no certificate was installed.");
    }

    private static void Storage(string root)
    {
        string path = Path.Combine(root, "options.json");
        AtomicJsonFile.Write(path, "{\"GreetingSeconds\":3}");
        AtomicJsonFile.Write(path, "{\"GreetingSeconds\":4}");
        Check(File.ReadAllText(path + ".bak").Contains("\"GreetingSeconds\":3"), "原子保存保留前一版备份");
        File.WriteAllText(path, "{broken");
        Check(IslandOptions.Load(path).GreetingSeconds == 3, "损坏设置从有效备份恢复");
        Check(JsonDocument.Parse(File.ReadAllText(path)).RootElement.ValueKind == JsonValueKind.Object, "恢复文件保持有效 JSON");
        File.Delete(path + ".bak");
        File.WriteAllText(path, "{broken");
        var defaults = IslandOptions.Load(path);
        defaults.Save(path);
        Check(File.ReadAllText(path) == "{broken", "未修改设置时保护损坏原文件");
        defaults.GreetingSeconds = 6;
        defaults.Save(path, allowRecovery: true);
        Check(IslandOptions.Load(path).GreetingSeconds == 6, "明确修改后允许恢复保存");
        string before = File.ReadAllText(path);
        try { AtomicJsonFile.Write(path, "{"); } catch (JsonException) { }
        Check(File.ReadAllText(path) == before, "无效 JSON 写入不破坏旧文件");
        Check(!Directory.EnumerateFiles(root, "*.tmp").Any(), "保存后没有临时残留");
        AtomicJsonFile.Write(path, "{\"Extras\":{\"MemoryTrimEnabled\":true,\"WeatherAutoRefresh\":false}}");
        var migrated = IslandOptions.Load(path);
        Check(!migrated.Extras.ContainsKey("MemoryTrimEnabled") && migrated.Extras["WeatherAutoRefresh"].ValueKind == JsonValueKind.False
            && !File.ReadAllText(path).Contains("MemoryTrimEnabled", StringComparison.Ordinal),
            "升级移除旧整理内存配置并持久化，其他用户设置保持原值");
    }

    private static void Conversations()
    {
        var messages = new List<ChatMessage> { new("user", "旧问题"), new("assistant", "旧回答"), new("user", "失败的问题") };
        Check(ChatHistoryOperations.RemoveLastTurn(messages) == "失败的问题" && messages.Count == 2 && messages[1].Content == "旧回答",
            "失败后重试保留上一轮成功回答");
        messages.Add(new("user", "新问题")); messages.Add(new("assistant", "新回答"));
        Check(ChatHistoryOperations.RemoveLastTurn(messages) == "新问题" && messages.Count == 2, "成功后重试仅移除最后回合");
        Check(LyricsService.CacheKey("歌曲", "歌手", TimeSpan.FromSeconds(100)) !=
            LyricsService.CacheKey("歌曲", "歌手", TimeSpan.FromSeconds(200)), "不同版本时长不共用歌词缓存");
    }

    private static void InputsAndPrivacy()
    {
        var wheel = new WheelAccumulator();
        Check(wheel.Consume(30, 1000) == 0 && wheel.Consume(30, 1050) == 0 &&
            wheel.Consume(30, 1100) == 0 && wheel.Consume(30, 1150) == 1, "高精度滚轮累积到一格");
        Check(wheel.Consume(-120, 1200) == -1, "滚轮方向改变正确处理");
        Check(PrivacyIndicatorService.IsActive(DateTime.UtcNow.AddDays(-1).ToFileTimeUtc(), 0), "超过六小时的设备占用保持有效");
        Check(!PrivacyIndicatorService.IsActive(100, 101) && !PrivacyIndicatorService.IsActive(0, 0), "结束占用和未开始记录不亮灯");
    }

    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr db);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr arg, out IntPtr error);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void sqlite3_free(IntPtr value);

    private static void Sql(string path, string sql)
    {
        if (sqlite3_open(path, out var db) != 0) throw new Exception("fixture SQLite open failed");
        try
        {
            int status = sqlite3_exec(db, sql, IntPtr.Zero, IntPtr.Zero, out var error);
            if (error != IntPtr.Zero) sqlite3_free(error);
            if (status != 0) throw new Exception("fixture SQLite exec failed: " + status);
        }
        finally { sqlite3_close(db); }
    }

    private static void Downloads(string root)
    {
        string profile = Path.Combine(root, "Browser"); Directory.CreateDirectory(profile);
        string history = Path.Combine(profile, "History");
        Sql(history, "create table downloads(target_path text,current_path text,total_bytes integer,received_bytes integer,state integer,start_time integer,end_time integer);");
        var monitor = new DownloadMonitorService(profile);
        monitor.Sample();
        Check(!monitor.Current.Any(t => t.Kind == DownloadSourceKind.Browser), "空的首次下载采样");
        var epoch = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        long start = (DateTime.UtcNow.AddHours(-1) - epoch).Ticks / 10;
        long end = (DateTime.UtcNow.AddSeconds(1) - epoch).Ticks / 10;
        Sql(history, $"insert into downloads values('long.zip','',100,100,1,{start},{end}); insert into downloads values('cancel.zip','',100,10,2,{start},{end}); insert into downloads values('old.zip','',100,100,1,{start},{start});");
        monitor.Sample();
        Check(monitor.Current.Any(t => t.Name == "long.zip" && !t.IsActive), "长时间下载按结束时间识别完成");
        Check(!monitor.Current.Any(t => t.Name is "cancel.zip" or "old.zip"), "取消和启动前历史不报完成");
        SteamDownloadChecks(root);
    }

    private static async Task Approvals()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start();
        int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        var requests = new System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>>();
        using var bridge = new AgentBridgeService(port, request =>
        {
            requests.GetOrAdd(request.Id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(true);
            return true;
        });
        bridge.Start();
        using var client = new HttpClient { BaseAddress = new Uri(bridge.Endpoint), Timeout = TimeSpan.FromSeconds(12) };
        async Task<HttpResponseMessage> Post(string id) => await client.PostAsync("approval",
            new StringContent(JsonSerializer.Serialize(new { id, tool = "regression_tool", timeoutSec = 5 }), Encoding.UTF8, "application/json"));
        var pending = Post("race");
        await requests.GetOrAdd("race", _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var bypass = await client.PostAsync("approval/race", new StringContent("{\"approved\":true}", Encoding.UTF8, "application/json"));
        Check(bypass.StatusCode == HttpStatusCode.Forbidden && bridge.IsPending("race"), "客户端不能通过 API 绕过用户审批");
        var racers = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => bridge.Resolve("race", i % 2 == 0, "test", "race"))));
        Check(racers.Count(x => x) == 1, "并发审批只产生一次有效裁决");
        using var result = await pending;
        Check(bridge.TryGetDecision("race", out var decision) && decision != null && !bridge.IsPending("race"), "实际裁决可回查");
        using var duplicate = await Post("race");
        Check(duplicate.StatusCode == HttpStatusCode.BadRequest, "拒绝重复使用已裁决的请求 ID");
        using var timedResponse = await Post("timeout");
        Check(bridge.TryGetDecision("timeout", out var timed) && timed is { Approved: false, TimedOut: true }, "超时按拒绝裁决并退出待审批队列");
    }

    private const string WeatherJson = "{\"current\":{\"temperature_2m\":18.2,\"apparent_temperature\":17,\"weather_code\":2},\"daily\":{\"temperature_2m_min\":[12],\"temperature_2m_max\":[21]}}";
    private sealed class FakeWeatherHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(WeatherJson) });
    }
    private static async Task Weather(string root)
    {
        var parsed = WeatherService.Parse(WeatherJson, WeatherCity.Beijing, DateTime.UtcNow);
        Check(parsed.Temperature == 18.2 && parsed.Description == "多云" && parsed.Low == 12 && parsed.High == 21, "天气数据与温度范围正确解析");
        using var http = new HttpClient(new FakeWeatherHandler());
        using var weather = new WeatherService(http, Path.Combine(root, "weather.json"));
        await weather.GetAsync(WeatherCity.Beijing, CancellationToken.None);
        Check(weather.Cached(WeatherCity.Beijing)?.Temperature == 18.2, "天气离线缓存可读取");
        Check(weather.Cached(new WeatherCity("上海", 31.23, 121.47)) == null, "切换城市不使用上一城市的天气缓存");
        Check(!WeatherService.ValidCity(new WeatherCity("坏数据", double.NaN, 200)), "天气拒绝无效坐标");
    }
    private static void FocusAndQr()
    {
        var timer = new PomodoroTimer(); var now = DateTime.UtcNow;
        timer.Start(now); timer.Pause(now.AddMinutes(10));
        Check(timer.Remaining == TimeSpan.FromMinutes(15) && !timer.Running, "番茄钟暂停保留剩余时间");
        timer.Start(now.AddHours(1));
        Check(timer.Tick(now.AddHours(1).AddMinutes(16)) && timer.IsBreak && timer.CompletedFocus == 1, "番茄钟恢复后按剩余时长完成");
        Check(!timer.Tick(now.AddHours(2)) && timer.Remaining == TimeSpan.FromMinutes(5), "番茄钟结束只提示一次且等待手动开始休息");
        var writer = new ZXing.BarcodeWriterPixelData { Format = ZXing.BarcodeFormat.QR_CODE,
            Options = new ZXing.Common.EncodingOptions { Width = 240, Height = 240, Margin = 4 } };
        var pixels = writer.Write("https://example.com/alpha-island");
        using var bitmap = new System.Drawing.Bitmap(pixels.Width, pixels.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var locked = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try { Marshal.Copy(pixels.Pixels, 0, locked.Scan0, pixels.Pixels.Length); } finally { bitmap.UnlockBits(locked); }
        Check(ScreenTextService.DecodeQr(bitmap) == "https://example.com/alpha-island", "本地二维码识别完整网址");
    }
    private static async Task Ocr()
    {
        if (Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Count == 0)
        { System.Console.WriteLine("SKIP: 本机没有 OCR 语言包"); return; }
        using var bitmap = new System.Drawing.Bitmap(650, 110);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        using (var font = new System.Drawing.Font("Arial", 36))
        {
            graphics.Clear(System.Drawing.Color.White);
            graphics.DrawString("ALPHA ISLAND", font, System.Drawing.Brushes.Black, 20, 15);
        }
        string text = await new ScreenTextService().ReadBitmapAsync(bitmap, CancellationToken.None);
        Check(text.Contains("ALPHA", StringComparison.OrdinalIgnoreCase) && text.Contains("ISLAND", StringComparison.OrdinalIgnoreCase),
            "系统 OCR 识别测试图片中的文字");
    }
    private static void RenderTools()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new alpha_island.Controls.ScreenToolsWindow(() => new ChatConfig("", "", "", "", 0.1, 1024));
                var content = (System.Windows.FrameworkElement)window.Content;
                var size = new System.Windows.Size(564, 415); content.Measure(size); content.Arrange(new System.Windows.Rect(size)); content.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(600, 450, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(content);
                string target = Path.GetFullPath("docs/screens/screen-tools.png"); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var stream = File.Create(target); var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); png.Save(stream); window.Close();
                Check(content.ActualWidth > 500 && content.ActualHeight > 350 && new FileInfo(target).Length > 1000,
                    "屏幕工具窗口离屏布局和预览生成");
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error != null) throw error;
    }
}
