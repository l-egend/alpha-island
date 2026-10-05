using System.Drawing;
using System.Windows.Forms;

namespace alpha_island.Controls;

internal sealed class ScreenRegionPicker : Form
{
    private Point? _start;
    private Rectangle _selection;
    public Rectangle? SelectedRegion { get; private set; }

    public ScreenRegionPicker()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        BackColor = Color.Black;
        Opacity = 0.30;
        TopMost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) _start = Control.MousePosition; };
        MouseMove += (_, _) =>
        {
            if (!_start.HasValue) return;
            Point current = Control.MousePosition;
            _selection = Rectangle.FromLTRB(Math.Min(_start.Value.X, current.X), Math.Min(_start.Value.Y, current.Y),
                Math.Max(_start.Value.X, current.X), Math.Max(_start.Value.Y, current.Y));
            Invalidate();
        };
        MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || !_start.HasValue) return;
            if (_selection.Width >= 8 && _selection.Height >= 8) { SelectedRegion = _selection; DialogResult = DialogResult.OK; }
            else DialogResult = DialogResult.Cancel;
            Close();
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.Cyan, 3);
        Rectangle local = _selection; local.Offset(-Left, -Top);
        if (_start.HasValue) e.Graphics.DrawRectangle(pen, local);
        e.Graphics.DrawString("拖动框选文字或二维码，Esc 取消", SystemFonts.MessageBoxFont ?? Font, Brushes.White, 24, 24);
    }

    internal static Rectangle? Pick()
    {
        using var picker = new ScreenRegionPicker();
        picker.ShowDialog();
        return picker.SelectedRegion;
    }
}
