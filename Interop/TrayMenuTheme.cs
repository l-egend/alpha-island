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
        var menu = new ContextMenuStrip
        {
            BackColor = Surface, ForeColor = Text, Font = font,
            ShowImageMargin = false, Padding = new Padding(4), Renderer = new DarkRenderer()
        };
        menu.Disposed += (_, _) => font.Dispose();
        return menu;
    }

    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            => e.Graphics.Clear(Surface);

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Color.FromArgb(66, 69, 79));
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var bounds = new Rectangle(0, 0, e.Item.Width - 1, e.Item.Height - 1);
            const int diameter = 10;
            using var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            var previous = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Color.FromArgb(48, 52, 63));
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
