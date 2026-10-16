using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using alpha_island.Interop;
using alpha_island.Plugins;
using alpha_island.Plugins.Isolation;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;
using Button = System.Windows.Controls.Button;

internal static partial class Program
{
    private static void PluginSettingsSurfaceChecks(alpha_island.MainWindow window, string root)
    {
        string target=Path.Combine(root,"local-agent-worker"); Directory.CreateDirectory(target);
        string source=Path.GetFullPath($"examples/LocalAgentsPlugin/bin/{TestBuildConfiguration}/net8.0-windows");
        foreach(string file in Directory.GetFiles(source).Where(file=>file.EndsWith(".dll")||file.EndsWith(".deps.json")))
            File.Copy(file,Path.Combine(target,Path.GetFileName(file)));
        File.Copy(Path.GetFullPath("examples/LocalAgentsPlugin/plugin.json"),Path.Combine(target,"plugin.json"));
        var manifest=PluginManifest.Read(Path.Combine(target,"plugin.json"));
        using var session=new PluginProcessSession(window.Dispatcher);
        var registry=AwaitPlugin(session.StartAsync(new(target,manifest.Assembly,manifest.EntryType,manifest.Id,
            Path.Combine(root,"worker-data"),new Dictionary<string,JsonElement>()),CancellationToken.None));
        int workerId=session.ProcessId;
        Check(registry.HasSettings,"真实本机 Agent 插件注册设置界面");
        var frame=AwaitPlugin(session.Request<RemoteFrame>("frame",new RemoteViewRequest("$settings",500,650)));
        IntPtr worker=IntPtr.Zero;
        NativeMethods.EnumWindowProc find=(handle,_)=> {
            NativeMethods.GetWindowThreadProcessId(handle,out uint pid);
            if(pid==(uint)session.ProcessId && NativeMethods.IsWindowVisible(handle)) worker=handle;
            return true;
        };
        NativeMethods.EnumWindows(find,IntPtr.Zero);
        Check(worker!=IntPtr.Zero,"测试跟踪真实插件进程的界面宿主 HWND");
        Color backdropColor=Color.FromRgb(42,82,114);
        var failures=new List<Exception>();
        var remote=new RemotePluginView("$settings",async command=> {
            await session.Request<bool>("input",command);
        },error=>failures.Add(error));
        remote.Apply(frame);
        var background=new Window {Left=100,Top=120,Width=500,Height=400,WindowStyle=WindowStyle.None,
            ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,ShowActivated=false,Topmost=true,
            Background=new SolidColorBrush(backdropColor),Content=remote};
        try
        {
            background.Show(); PumpAnimations(130);
            // This is the same click/position queue used by the settings UI, not a direct plugin call.
            var refresh=WalkUi(remote).OfType<Button>().Single(button=>System.Windows.Automation.AutomationProperties.GetName(button)=="重新检测");
            var sample=background.PointToScreen(new Point(460,Math.Min(350,frame.Height-10)));
            Color before=PluginPixel(sample);
            refresh.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            PumpAnimations(200);
            NativeMethods.SetWindowPos(worker,new IntPtr(-1),0,0,0,0,0x13);
            PumpAnimations(160); NativeMethods.DwmFlush();
            Color after=PluginPixel(sample);
            Console.WriteLine($"Plugin surface: before={before}, after={after}, layered={(NativeMethods.GetWindowLong(worker,-20)&0x80000)!=0}, inputFailures={failures.Count}");
            Check(before==backdropColor,"空白区域在插件按钮操作前显示已知背景颜色");
            Check(after==before,"点击本机 Agent 设置按钮后，插件宿主保持透明而不留下大黑块");
            AwaitPlugin(session.Request<RemoteFrame>("frame",new RemoteViewRequest("$settings",500,650,false)));
            PumpAnimations(80);
            Check(PluginPixel(sample)==before,"离开插件设置后没有不透明的残留窗口");
            Check(failures.Count==0,"设置按钮经真实跨进程队列完成操作");
        }
        finally {background.Close();session.Dispose();AwaitPlugin(session.Completion.ContinueWith(_=>true));}
        Check(!ProcessAlive(workerId),"关闭插件回收其界面宿主进程");
        Check(frame.Height < 260, "本机 Agent 简短设置按内容高度布局，不再强制撑到 650px");
    }
    private static Color PluginPixel(Point point)
    {
        using var bitmap=new System.Drawing.Bitmap(1,1);
        using(var graphics=System.Drawing.Graphics.FromImage(bitmap)) graphics.CopyFromScreen((int)point.X,(int)point.Y,0,0,new System.Drawing.Size(1,1));
        var color=bitmap.GetPixel(0,0);return Color.FromRgb(color.R,color.G,color.B);
    }
    private static IEnumerable<DependencyObject> WalkUi(DependencyObject node)
    {
        yield return node;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++) foreach(var child in WalkUi(VisualTreeHelper.GetChild(node,i))) yield return child;
    }
}