using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DragDropEffects = System.Windows.DragDropEffects;
using Grid = System.Windows.Controls.Grid;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

internal static partial class Program
{
    [DllImport("user32.dll", EntryPoint = "SetCursorPos")]
    private static extern bool FileDragSetCursor(int x, int y);

    // Live OLE acceptance test: both source and receiver are owned fixture windows;
    // only files beneath testRoot are copied/moved and the system clipboard is untouched.
    private static void FileTransferOleChecks(alpha_island.MainWindow window, Grid root, string testRoot)
    {
        var load = typeof(alpha_island.MainWindow).GetMethod("Window_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!;
        window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, load);
        var work = SystemParameters.WorkArea;
        window.Left = work.Left + 10; window.Top = work.Top + 20;
        window.Width = Math.Min(1200, work.Width - 20);
        window.Show();
        var dropArea = new Border { Background = Brushes.SteelBlue, AllowDrop = true,
            Child = new TextBlock { Text = "中转站拖出验证（临时窗口）", Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var receiver = new Window { Title = "Alpha Island file transfer regression target", Content = dropArea,
            Left = window.Left + (window.Width - 450) / 2, Top = window.Top + 210,
            Width = 450, Height = 110, WindowStyle = WindowStyle.ToolWindow, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true };
        NativeMethods.GetCursorPos(out var originalPointer);
        var originalForeground = NativeMethods.GetForegroundWindow();
        Thread? input = null;
        Exception? inputError = null;
        string? receivedPath = null;
        int? receivedPreferred = null;
        bool entered = false;
        bool isCut = false;
        bool cancel = false;
        string fixturePath = "";
        string targetPath = "";
        DragDropEffects expected = DragDropEffects.Copy;
        void TargetDrag(object sender, System.Windows.DragEventArgs e)
        {
            entered = true; e.Handled = true;
            e.Effects = FileDropPayload.TryRead(e.Data, out var paths) && paths.Length == 1 && paths[0] == fixturePath
                && (e.AllowedEffects & expected) == expected ? expected : DragDropEffects.None;
        }
        dropArea.DragEnter += TargetDrag; dropArea.DragOver += TargetDrag;
        dropArea.Drop += (sender, e) =>
        {
            TargetDrag(sender, e);
            if (e.Effects == DragDropEffects.None) return;
            var paths = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop, false);
            receivedPath = paths.Single();
            var preferred = (MemoryStream)e.Data.GetData("Preferred DropEffect", false);
            receivedPreferred = BitConverter.ToInt32(preferred.ToArray());
            if (Directory.Exists(receivedPath))
            {
                if (isCut) Directory.Move(receivedPath, targetPath);
                else
                {
                    Directory.CreateDirectory(targetPath);
                    File.Copy(Path.Combine(receivedPath, "内容.txt"), Path.Combine(targetPath, "内容.txt"));
                }
            }
            else if (isCut) File.Move(receivedPath, targetPath);
            else File.Copy(receivedPath, targetPath);
        };
        try
        {
            receiver.Show();
            PrivateCall(window, "StartFileTransferService");
            var transfer = AgentField<FileTransferService>(window, "_fileTransfer");
            foreach (bool directory in new[] { false, true })
            foreach (bool cut in new[] { false, true })
            foreach (bool abort in new[] { false, true })
            {
                transfer.Clear();
                isCut = cut; cancel = abort; entered = false; receivedPath = null; receivedPreferred = null; inputError = null;
                expected = isCut ? DragDropEffects.Move : DragDropEffects.Copy;
                string name = $"{(directory ? "文件夹" : "文件")}-{(cut ? "剪切" : "复制")}-{(abort ? "取消" : "完成")}";
                fixturePath = Path.Combine(testRoot, name + (directory ? "" : ".txt"));
                targetPath = Path.Combine(testRoot, "目标-" + Path.GetFileName(fixturePath));
                if (directory) { Directory.CreateDirectory(fixturePath); File.WriteAllText(Path.Combine(fixturePath, "内容.txt"), name); }
                else File.WriteAllText(fixturePath, name);
                transfer.AddFiles(new[] { fixturePath }, isCut);
                PrivateCall(window, "SetState", IslandState.Tray);
                SetAgentField(window, "_clipboardLocked", true);
                PumpAnimations(100); root.UpdateLayout();
                var row = TransferRow(window.TransferItemsList)!;
                Check(row.IsVisible && row.ActualWidth > 0, name + "：真实源条目可见");
                var start = row.PointToScreen(new System.Windows.Point(row.ActualWidth / 2, 20));
                var end = dropArea.PointToScreen(new System.Windows.Point(dropArea.ActualWidth / 2, dropArea.ActualHeight / 2));
                window.Activate();
                PumpAnimations(60);
                input = new Thread(() =>
                {
                    try
                    {
                        if (!FileDragSetCursor((int)start.X, (int)start.Y)) throw new InvalidOperationException("Fixture pointer move failed.");
                        Thread.Sleep(100);
                        if (!NativeMethods.GetCursorPos(out var actual) || actual.X != (int)start.X || actual.Y != (int)start.Y)
                            throw new InvalidOperationException("Fixture pointer displaced by external input.");
                        FileDragMouse(NativeMethods.MOUSEEVENTF_LEFTDOWN); Thread.Sleep(90);
                        if (NativeMethods.GetKeyState(1) >= 0) throw new InvalidOperationException("Fixture mouse button was not pressed.");
                        FileDragSetCursor((int)start.X + 24, (int)start.Y); Thread.Sleep(120);
                        FileDragSetCursor((int)end.X, (int)end.Y); Thread.Sleep(200);
                        if (cancel) { FileDragKey(0x1B, false); FileDragKey(0x1B, true); Thread.Sleep(90); }
                    }
                    catch (Exception e) { inputError = e; }
                    finally { FileDragMouse(NativeMethods.MOUSEEVENTF_LEFTUP); }
                }) { IsBackground = true };
                input.Start();
                while (input.IsAlive) PumpAnimations(30);
                PumpAnimations(160);
                if (inputError != null) throw inputError;
                Check(entered, name + "：实际鼠标拖出进入系统 OLE 目标");
                Check(!AgentField<bool>(window, "_exportingTransferItem"), name + "：OLE 结束后释放转出状态");
                if (abort)
                {
                    Check(receivedPath == null && transfer.Count == 1 && (File.Exists(fixturePath) || Directory.Exists(fixturePath))
                        && !File.Exists(targetPath) && !Directory.Exists(targetPath), name + "：Esc 取消保留条目、源文件并且不投放");
                }
                else
                {
                    Check(receivedPath == fixturePath && receivedPreferred == (int)expected, name + "：目标收到真实路径和对应复制／移动标记");
                    Check(transfer.Count == 0 && (File.Exists(targetPath) || Directory.Exists(targetPath))
                        && (File.Exists(fixturePath) || Directory.Exists(fixturePath)) == !cut, name + "：成功投放后移除条目且复制／剪切语义正确");
                }
            }
        }
        finally
        {
            if (input?.IsAlive == true) { FileDragKey(0x1B, false); FileDragKey(0x1B, true); input.Join(2000); }
            FileDragMouse(NativeMethods.MOUSEEVENTF_LEFTUP);
            SetAgentField(window, "_clipboardLocked", false);
            receiver.Close(); window.Hide();
            FileDragSetCursor(originalPointer.X, originalPointer.Y);
            if (originalForeground != IntPtr.Zero) NativeMethods.SetForegroundWindow(originalForeground);
        }
    }

    private static void FileDragMouse(uint flags)
    {
        var inputs = new[] { new NativeMethods.INPUT { type = NativeMethods.INPUT_MOUSE,
            u = new NativeMethods.InputUnion { mi = new NativeMethods.MOUSEINPUT { dwFlags = flags } } } };
        if (NativeMethods.SendInput(1, inputs, Marshal.SizeOf<NativeMethods.INPUT>()) != 1)
            throw new InvalidOperationException("Fixture mouse injection failed.");
    }
    private static void FileDragKey(ushort key, bool up)
    {
        var inputs = new[] { new NativeMethods.INPUT { type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = key, dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0 } } } };
        if (NativeMethods.SendInput(1, inputs, Marshal.SizeOf<NativeMethods.INPUT>()) != 1)
            throw new InvalidOperationException("Fixture key injection failed.");
    }
}
