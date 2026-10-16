using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using alpha_island.Core;
using alpha_island.Interop;
using Button = System.Windows.Controls.Button;

internal static partial class Program
{
    private static void MessengerTrayInvocationChecks()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var trayButton = new Button { Content = "微信", Width = 44, Height = 36 };
            AutomationProperties.SetName(trayButton, " 微信");
            var root = new StackPanel(); root.Children.Add(trayButton);
            var fixture = new Window { Content = root, Width = 160, Height = 100, Left = -30000, Top = -30000,
                ShowActivated = false, ShowInTaskbar = false };
            try
            {
                fixture.Show(); fixture.UpdateLayout();
                bool ready = false; int activations = 0;
                trayButton.Click += (_, _) => { ready = true; activations++; };
                IntPtr handle = new WindowInteropHelper(fixture).Handle;
                bool Invoke(MessengerClient client)
                {
                    var call = Task.Run(() =>
                    {
                        var automation = AutomationElement.FromHandle(handle);
                        var elements = automation.FindAll(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>();
                        return MessengerWindowReader.TryOpen(client, new(() => client == MessengerClient.WeChat ? throw new Exception("Unexpected direct window restore") : false,
                            () => MessengerWindowReader.TryOpenTray(client, elements), () => false));
                    });
                    var until = DateTime.UtcNow.AddSeconds(4);
                    while (!call.IsCompleted && DateTime.UtcNow < until) PumpAnimations(20);
                    Check(call.IsCompleted, "托盘动作在后台完成，辅助功能调用不会占用岛 UI 线程");
                    bool opened = call.GetAwaiter().GetResult(); PumpAnimations(40); return opened;
                }
                Check(Invoke(MessengerClient.WeChat) && ready && activations == 1,
                    "真实 InvokePattern 执行托盘按钮的应用恢复回调，带空白的微信名称也可识别");
                trayButton.IsEnabled = false;
                Check(!Invoke(MessengerClient.WeChat) && activations == 1, "入口禁用时返回失败，不调用原始 HWND 显示或重复恢复");
                trayButton.IsEnabled = true;
                Check(!Invoke(MessengerClient.QQ) && activations == 1, "微信托盘控件不会用于打开 QQ");
            }
            catch (Exception ex) { error = ex; }
            finally { fixture.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error != null) throw error;
    }
    private static void MessengerNotificationOpeningUiChecks(alpha_island.MainWindow window)
    {
        var original = AgentField<Func<DesktopNotification, bool>>(window, "_notificationAppOpener");
        using var completion = new ManualResetEventSlim();
        int attempts = 0, uiThread = Environment.CurrentManagedThreadId;
        bool worker = false;
        var notice = MessengerTestNotice(MessengerClient.WeChat, "opening-wechat");
        try
        {
            SetAgentField(window, "_notificationAppOpener", new Func<DesktopNotification, bool>(_ =>
            { Interlocked.Increment(ref attempts); worker = Environment.CurrentManagedThreadId != uiThread; return completion.Wait(3000); }));
            PrivateCall(window, "OpenDesktopNotificationApp", notice);
            PrivateCall(window, "OpenDesktopNotificationApp", notice);
            var until = DateTime.UtcNow.AddSeconds(1);
            while (Volatile.Read(ref attempts) == 0 && DateTime.UtcNow < until) PumpAnimations(20);
            Check(AgentField<bool>(window, "_openingDesktopApp") && worker && attempts == 1,
                "真实通知点击入口异步打开应用，连续点击只启动一次恢复");
            completion.Set();
            until = DateTime.UtcNow.AddSeconds(2);
            while (AgentField<bool>(window, "_openingDesktopApp") && DateTime.UtcNow < until) PumpAnimations(20);
            Check(!AgentField<bool>(window, "_openingDesktopApp"), "恢复完成后释放点击状态，可以再次打开应用");
            SetAgentField(window, "_sessionLocked", true);
            PrivateCall(window, "OpenDesktopNotificationApp", notice); PumpAnimations(40);
            Check(attempts == 1, "锁屏时通知点击不会调用微信或其他应用入口");
        }
        finally
        {
            completion.Set(); SetAgentField(window, "_notificationAppOpener", original);
            SetAgentField(window, "_sessionLocked", false);
        }
    }
}
