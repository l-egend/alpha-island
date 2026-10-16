using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace alpha_island.Interop;

/// <summary>托盘菜单与岛内菜单保持深色背景、圆角高亮和相同的文字层级。</summary>
internal static class TrayMenuTheme
{
    private static readonly Color Surface = Color.FromArgb(27, 29, 36);
    private static readonly Color Text = Color.FromArgb(242, 242, 245);

    internal static ContextMenuStrip Create()
    {
        var font = new Font("Segoe UI", 9.5f);
        var menu = new TrayContextMenu
        {
            BackColor = Surface, ForeColor = Text, Font = font,
            ShowImageMargin = false, ShowCheckMargin = false, DropShadowEnabled = true,
            Padding = new Padding(4), Renderer = new DarkRenderer()
        };
        menu.Disposed += (_, _) => font.Dispose();
        return menu;
    }

    internal static Rectangle FitToWorkArea(Rectangle bounds, Rectangle workArea, int gap)
    {
        int left = workArea.Left + gap, top = workArea.Top + gap;
        return new Rectangle(
            Math.Clamp(bounds.X, left, Math.Max(left, workArea.Right - bounds.Width - gap)),
            Math.Clamp(bounds.Y, top, Math.Max(top, workArea.Bottom - bounds.Height - gap)),
            bounds.Width, bounds.Height);
    }

    private sealed class TrayContextMenu : ContextMenuStrip
    {
        protected override void OnItemAdded(ToolStripItemEventArgs e)
        {
            base.OnItemAdded(e);
            if (e.Item is ToolStripMenuItem item)
                item.Padding = new Padding(8, 4, 8, 4);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyRoundedShape();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ApplyRoundedShape();
        }

        private void ApplyRoundedShape()
        {
            if (!IsHandleCreated || Width < 2 || Height < 2) return;
            // Clip the actual HWND, including hit testing, rather than merely
            // drawing curved corners on top of an opaque rectangular popup.
            WindowCorners.ApplyLegacyRegion(Handle, 8, DeviceDpi / 96d);
        }

        protected override void OnOpened(EventArgs e)
        {
            // NotifyIcon.ShowInTaskbar disables WinForms' working-area constraint.
            // Restore it after that path has calculated the popup's final size.
            int gap = Math.Max(4, (int)Math.Round(6d * DeviceDpi / 96));
            Bounds = FitToWorkArea(Bounds, Screen.FromRectangle(Bounds).WorkingArea, gap);
            NativeMethods.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
            ApplyRoundedShape();
            base.OnOpened(e);
        }
    }

    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            => e.Graphics.Clear(Surface);

        // The native region supplies the rounded outer contour. A rectangular
        // outline would leave hard edges across those corners.
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var bounds = new Rectangle(0, 0, e.Item.Width - 1, e.Item.Height - 1);
            if (bounds.Width < 2 || bounds.Height < 2) return;
            int diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), Math.Max(4, (int)Math.Round(8d * (e.ToolStrip?.DeviceDpi ?? 96) / 96)));
            using var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            var previous = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Color.FromArgb(47, 49, 55));
            e.Graphics.FillPath(brush, path);
            e.Graphics.SmoothingMode = previous;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Color.FromArgb(112, 115, 124);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Color.FromArgb(59, 62, 72));
            e.Graphics.DrawLine(pen, 8, e.Item.Height / 2, e.Item.Width - 8, e.Item.Height / 2);
        }
    }
}
