using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using alpha_island.Core;

namespace alpha_island.Controls;

/// <summary>Samples a frozen desktop before drawing the picker, so its overlay never alters the selected color.</summary>
internal sealed class ScreenPixelPicker : Form
{
    private readonly List<(Rectangle Bounds,Bitmap Image)> _screens=[];
    private Point _position;
    private ScreenColor? _current;
    internal ScreenColor? Selected {get;private set;}
    private ScreenPixelPicker()
    {
        AutoScaleMode=AutoScaleMode.None;FormBorderStyle=FormBorderStyle.None;StartPosition=FormStartPosition.Manual;
        Bounds=SystemInformation.VirtualScreen;TopMost=true;ShowInTaskbar=false;Cursor=Cursors.Cross;DoubleBuffered=true;KeyPreview=true;
        long pixels=0;
        try
        {
            foreach(var screen in Screen.AllScreens.Take(16))
            {
                var rectangle=screen.Bounds;pixels+=(long)rectangle.Width*rectangle.Height;
                if(pixels>64_000_000)throw new InvalidOperationException("显示器总分辨率过大，请暂时减少显示器后重试。");
                var image=Services.ScreenTextService.Capture(rectangle);_screens.Add((rectangle,image));
            }
        }
        catch{foreach(var screen in _screens)screen.Image.Dispose();_screens.Clear();throw;}
        KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Escape){DialogResult=DialogResult.Cancel;Close();}};
        MouseMove+=(_,_)=>{_position=Control.MousePosition;ReadColor();Invalidate();};
        MouseDown+=(_,e)=>{if(e.Button==MouseButtons.Left){_position=Control.MousePosition;ReadColor();Selected=_current;DialogResult=DialogResult.OK;Close();}
            else if(e.Button==MouseButtons.Right){DialogResult=DialogResult.Cancel;Close();}};
        Shown+=(_,_)=>{_position=Control.MousePosition;ReadColor();Invalidate();};
    }
    private void ReadColor()
    {
        _current=ReadAt(_screens,_position);
    }
    internal static ScreenColor? ReadAt(IReadOnlyList<(Rectangle Bounds,Bitmap Image)> screens,Point position)
    {
        foreach(var screen in screens)
            if(screen.Bounds.Contains(position))
            {var color=screen.Image.GetPixel(position.X-screen.Bounds.Left,position.Y-screen.Bounds.Top);return new(color.R,color.G,color.B);}
        return null;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        foreach(var screen in _screens)e.Graphics.DrawImageUnscaled(screen.Image,screen.Bounds.Left-Left,screen.Bounds.Top-Top);
        var monitor=Screen.FromPoint(_position).Bounds;monitor.Offset(-Left,-Top);
        int x=Math.Clamp(_position.X-Left+24,monitor.Left+8,Math.Max(monitor.Left+8,monitor.Right-198));
        int y=Math.Clamp(_position.Y-Top+24,monitor.Top+8,Math.Max(monitor.Top+8,monitor.Bottom-184));
        using var background=new SolidBrush(Color.FromArgb(245,24,27,34));
        e.Graphics.FillRectangle(background,x,y,190,176);
        foreach(var screen in _screens)
        {
            if(!screen.Bounds.Contains(_position))continue;
            int px=_position.X-screen.Bounds.Left,py=_position.Y-screen.Bounds.Top;
            var source=Rectangle.Intersect(new Rectangle(px-5,py-5,11,11),new Rectangle(Point.Empty,screen.Image.Size));
            e.Graphics.InterpolationMode=InterpolationMode.NearestNeighbor;
            e.Graphics.PixelOffsetMode=PixelOffsetMode.Half;
            e.Graphics.DrawImage(screen.Image,new Rectangle(x+10+(source.Left-(px-5))*10,y+10+(source.Top-(py-5))*10,source.Width*10,source.Height*10),source,GraphicsUnit.Pixel);
            using var border=new Pen(Color.White,2);e.Graphics.DrawRectangle(border,x+60,y+60,10,10);
        }
        if(_current is {} color)
        {
            using var paint=new SolidBrush(Color.FromArgb(color.Red,color.Green,color.Blue));e.Graphics.FillRectangle(paint,x+133,y+10,42,42);
            e.Graphics.DrawString(color.Hex,SystemFonts.MessageBoxFont??Font,Brushes.White,x+10,y+125);
        }
        e.Graphics.DrawString("单击取色 · Esc / 右键取消",SystemFonts.MessageBoxFont??Font,Brushes.White,x+10,y+150);
    }
    protected override void Dispose(bool disposing)
    {if(disposing){foreach(var screen in _screens)screen.Image.Dispose();_screens.Clear();}base.Dispose(disposing);}
    internal static ScreenColor? Pick(CancellationToken token)
    {
        using var picker=new ScreenPixelPicker();
        using var registration=token.Register(()=>{try{if(picker.IsHandleCreated&&!picker.IsDisposed)picker.BeginInvoke(new Action(picker.Close));}catch(InvalidOperationException){}});
        if(token.IsCancellationRequested)return null;
        picker.ShowDialog();return token.IsCancellationRequested?null:picker.Selected;
    }
}
