using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Core;
using Button=System.Windows.Controls.Button;
using Color=System.Windows.Media.Color;
using Brushes=System.Windows.Media.Brushes;

namespace alpha_island.Controls;

internal sealed record ScreenToolOutput(string Kind,string Text);

internal sealed partial class ScreenToolsWindow
{
    private readonly Action<string> _copyText;
    private readonly Action<ScreenToolOutput>? _completed;
    private readonly WrapPanel _palette=new(){Margin=new Thickness(0,4,0,0)};
    private readonly List<ScreenColor> _colors=[];
    private ScreenColor? _color;
    internal async Task RecognizeAsync(bool copy,bool returnToWindow=true)
    {
        if(_busy||_closed)return;
        StopSubtitles();
        bool restore=returnToWindow||IsVisible;
        try
        {
            await RunAsync(async token=>
            {
                Hide();await Task.Delay(150,token);
                var selected=_pickRegion(token);
                if(selected==null){_status.Text=L.T("已取消识别，剪贴板未改变。");return;}
                _region=selected;_lastText="";
                string text=await _screen.ReadAsync(selected.Value,token);
                if(!_closed)ApplyRecognizedText(text,copy);
            });
        }
        finally{if(!_closed&&restore&&_canUseTools())Interop.UtilityWindowPresenter.Show(this);}
    }
    internal void ApplyRecognizedText(string text,bool copy)
    {
        _original.Text=text;_result.Clear();
        if(text.Length==0){_status.Text=L.T("没有识别到文字，请放大文字或重新框选。");return;}
        if(copy)CopyOriginal();else _status.Text=L.F($"已在本机识别{text.Length}个字符，可复制原文或选择翻译。");
    }
    private void CopyOriginal()
    {
        if(_original.Text.Length==0){_status.Text=L.T("请先识别屏幕文字。");return;}
        try{_copyText(_original.Text);}
        catch(System.Runtime.InteropServices.ExternalException){_status.Text=L.T("剪贴板正被其他程序占用，请稍后点“复制原文”。");NotifyCompleted(new("error",_status.Text));return;}
        _status.Text=L.F($"已复制原文（{_original.Text.Length}个字符）");NotifyCompleted(new("ocr",_original.Text));
    }
    internal async Task PickColorAsync(bool returnToWindow=true)
    {
        if(_busy||_closed)return;
        StopSubtitles();bool restore=returnToWindow||IsVisible;
        try
        {
            await RunAsync(async token=>
            {
                Hide();await Task.Delay(150,token);
                var color=ScreenPixelPicker.Pick(token);
                if(color==null){_status.Text=L.T("已取消取色，剪贴板未改变。");return;}
                if(!_closed)ApplyPickedColor(color.Value);
            });
        }
        finally{if(!_closed&&restore&&_canUseTools())Interop.UtilityWindowPresenter.Show(this);}
    }
    internal void ApplyPickedColor(ScreenColor color)
    {
        _color=color;_result.Text=$"{color.Hex}\n{color.Rgb}\n{color.Hsl}";
        _colors.Remove(color);_colors.Insert(0,color);if(_colors.Count>8)_colors.RemoveAt(8);
        _palette.Children.Clear();
        foreach(var value in _colors)
        {
            var button=new Button{Content=value.Hex,ToolTip=value.Rgb+"\n"+value.Hsl,Padding=new Thickness(8,4,8,4),Margin=new Thickness(0,0,5,3),
                Background=new SolidColorBrush(Color.FromRgb(value.Red,value.Green,value.Blue)),
                Foreground=value.Red*.299+value.Green*.587+value.Blue*.114>160?Brushes.Black:Brushes.White};
            button.Click+=(_,_)=>{_color=value;_result.Text=$"{value.Hex}\n{value.Rgb}\n{value.Hsl}";CopyColor("hex");};_palette.Children.Add(button);
        }
        CopyColor("hex");
    }
    private void CopyColor(string format)
    {
        if(_color is not {} color){_status.Text=L.T("请先使用颜色吸管。");return;}
        string text=format=="rgb"?color.Rgb:format=="hsl"?color.Hsl:color.Hex;
        try{_copyText(text);}
        catch(System.Runtime.InteropServices.ExternalException){_status.Text=L.T("剪贴板正被占用，可稍后重新复制颜色。");NotifyCompleted(new("error",_status.Text));return;}
        _status.Text=L.T("已复制 ")+text;NotifyCompleted(new("color",text));
    }
    private void NotifyCompleted(ScreenToolOutput output)
    {
        try{_completed?.Invoke(output);}
        catch(Exception error) when(error is not OutOfMemoryException)
        {IslandLog.Warn("ScreenTools","结果通知暂不可用："+error.GetType().Name+" / 0x"+error.HResult.ToString("X8"));}
    }
}
