using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Interop;

internal static partial class Program
{
    private static void IslandZOrderPeer(string ready)
    {
        var thread = new Thread(() => {
            var app = new System.Windows.Application();
            var peer = new Window { Title="Alpha Island z-order test peer", Left=-30000, Top=0, Width=1200, Height=640,
                ShowActivated=false, ShowInTaskbar=false, WindowStyle=WindowStyle.None, Topmost=true };
            peer.Loaded += (_,_) => File.WriteAllText(ready, new WindowInteropHelper(peer).Handle.ToInt64().ToString());
            var timeout = new DispatcherTimer { Interval=TimeSpan.FromSeconds(15) };
            timeout.Tick += (_,_) => peer.Close(); timeout.Start(); app.Run(peer);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    }
    private static bool Above(IntPtr first, IntPtr second)
    {
        for (IntPtr next=ZOrderGetWindow(first,2); next!=IntPtr.Zero; next=ZOrderGetWindow(next,2)) if(next==second) return true;
        return false;
    }
    private static void IslandZOrderChecks(alpha_island.MainWindow window, string root)
    {
        var previous=NativeMethods.GetForegroundWindow();
        window.Left=-30000; window.Top=0; window.Show(); PumpAnimations(100);
        var hwnd=new WindowInteropHelper(window).Handle;
        string ready=Path.Combine(root,"peer-window.txt");
        var start=new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=false, CreateNoWindow=true };
        start.ArgumentList.Add("--island-zorder-peer"); start.ArgumentList.Add(ready);
        using var peer=Process.Start(start)!;
        try
        {
            for(int i=0;i<60 && !File.Exists(ready);i++) PumpAnimations(50);
            Check(File.Exists(ready), "独立进程创建覆盖主岛的窗口样本");
            IntPtr foreign=new(long.Parse(File.ReadAllText(ready)));
            ZOrderSetWindowPos(foreign,new IntPtr(-1),0,0,0,0,0x13);
            NativeMethods.SetForegroundWindow(foreign); PumpAnimations(300);
            Console.WriteLine($"Native overlay: islandVisible={NativeMethods.IsWindowVisible(hwnd)}, foregroundIsPeer={NativeMethods.GetForegroundWindow()==foreign}, islandAbovePeer={Above(hwnd,foreign)}");
            Check(NativeMethods.IsWindowVisible(hwnd) && Above(hwnd,foreign), "外部窗口覆盖或最大化后，最小态仍位于该窗口上方");
            Check(NativeMethods.GetForegroundWindow()!=hwnd, "恢复岛层级不会夺取外部窗口的焦点");
            int shift=0;
            foreach(var state in new[] { IslandState.Pill, IslandState.Card, IslandState.Bar })
            {
                PrivateCall(window,"SetState",state,true,state==IslandState.Card?"productivity":null); window.UpdateLayout();
                ZOrderSetWindowPos(foreign,new IntPtr(-1),-30000+(++shift),0,1200,640,0x10);
                var foreground=NativeMethods.GetForegroundWindow(); PumpAnimations(200);
                Check(Above(hwnd,foreign) && NativeMethods.IsWindowVisible(hwnd), "窗口尺寸或层级改变时保持可见："+state);
                Check(NativeMethods.GetForegroundWindow()==foreground, "重排期间保留原前台窗口："+state);
            }
            window.Hide(); ZOrderSetWindowPos(foreign,new IntPtr(-1),-30000+(++shift),0,1200,640,0x10); PumpAnimations(200);
            Check(!NativeMethods.IsWindowVisible(hwnd), "主动隐藏的岛不会被层级守护重新弹出");
            window.Show(); PumpAnimations(200);
            ZOrderSetWindowPos(foreign,new IntPtr(-1),-30000+(++shift),0,1200,640,0x10); PumpAnimations(200);
            Check(Above(hwnd,foreign), "隐藏后重新显示仍能恢复窗口层级");
        }
        finally { if(!peer.HasExited) { peer.Kill(); peer.WaitForExit(); } if(previous!=IntPtr.Zero) NativeMethods.SetForegroundWindow(previous); }
    }
    [DllImport("user32.dll", EntryPoint="GetWindow")] private static extern IntPtr ZOrderGetWindow(IntPtr window,uint command);
    [DllImport("user32.dll", EntryPoint="SetWindowPos")] private static extern bool ZOrderSetWindowPos(IntPtr window,IntPtr insert,int x,int y,int width,int height,uint flags);
}