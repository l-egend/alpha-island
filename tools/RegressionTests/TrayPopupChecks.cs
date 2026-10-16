using System.Drawing;
using System.Windows.Forms;
using alpha_island.Interop;

internal static partial class Program
{
    private static void TrayPopupChecks()
    {
        using var menu = TrayMenuTheme.Create();
        menu.Items.Add("打开设置");
        menu.Items.Add(new ToolStripSeparator());
        var exit = menu.Items.Add("退出程序");
        var screen = Screen.PrimaryScreen!;
        var anchor = new System.Drawing.Point(screen.Bounds.Right - 30, screen.Bounds.Bottom - 20);
        try
        {
            // NotifyIcon uses this path rather than ContextMenuStrip.Show(Point).
            typeof(ContextMenuStrip).GetMethod("ShowInTaskbar", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(menu, new object[] { anchor.X, anchor.Y });
            PumpAnimations(80);
            var itemPoint = menu.PointToScreen(new System.Drawing.Point(exit.Bounds.Left + 8, exit.Bounds.Top + exit.Bounds.Height / 2));
            bool receivesInput = WindowFromPoint(new ComponentProbePoint { X = itemPoint.X, Y = itemPoint.Y }) == menu.Handle;
            Console.WriteLine($"Tray popup: bounds={menu.Bounds}, working={screen.WorkingArea}, exit={itemPoint}, receiver={receivesInput}");
            Check(screen.WorkingArea.Contains(menu.Bounds), "托盘菜单完整位于工作区，底部选项不与任务栏重叠");
            Check(receivesInput, "退出选项在真实窗口中接收鼠标，没有被任务栏覆盖");
            var corners = new[] {
                new ComponentProbePoint { X=menu.Left, Y=menu.Top },
                new ComponentProbePoint { X=menu.Right-1, Y=menu.Top },
                new ComponentProbePoint { X=menu.Left, Y=menu.Bottom-1 },
                new ComponentProbePoint { X=menu.Right-1, Y=menu.Bottom-1 }
            };
            Check(corners.All(point => WindowFromPoint(point) != menu.Handle),
                "托盘菜单四角真实裁切，圆角以外鼠标不命中菜单");
            using (var preview = new Bitmap(menu.Width + 16, menu.Height + 16))
            {
                using (var graphics = Graphics.FromImage(preview))
                    graphics.CopyFromScreen(menu.Left - 8, menu.Top - 8, 0, 0, preview.Size);
                preview.Save(System.IO.Path.GetFullPath("bin/tray-menu-preview.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            var work = new Rectangle(-1920, 40, 1920, 1040);
            foreach (var point in new[] { new System.Drawing.Point(-1930, -10), new System.Drawing.Point(-20, -10), new System.Drawing.Point(-1930, 1060), new System.Drawing.Point(-20, 1060) })
            {
                var fitted = TrayMenuTheme.FitToWorkArea(new Rectangle(point, menu.Size), work, 6);
                Check(work.Contains(fitted), "四边任务栏和负坐标副屏的菜单位置均限制到工作区：" + point);
            }
            bool clicked = false;
            exit.Click += (_, _) => clicked = true;
            exit.PerformClick();
            Check(clicked, "重定位后退出菜单项仍可触发原有操作");
        }
        finally { menu.Close(); }
    }
}
