using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void BatteryRippleChecks(alpha_island.MainWindow window, Grid root)
    {
        var options = AgentField<IslandOptions>(window,"_options");
        var layers = AgentField<IslandLayerHost>(window,"_layers");
        var wave = window.BatteryRipple;
        bool reduced=MotionPolicy.ReduceMotion,saving=MotionPolicy.PowerSave;
        var folder=Path.GetFullPath("bin/battery-ripple-previews");Directory.CreateDirectory(folder);
        byte[] Capture(string name,double phase)
        {
            wave.Stop();SetAgentField(wave,"_progress",phase);wave.InvalidateVisual();PumpAnimations(25);root.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
            var origin=window.IslandBody.TransformToAncestor(root).Transform(new System.Windows.Point(0,0));
            var cropped=new CroppedBitmap(bitmap,new Int32Rect((int)Math.Round(origin.X),(int)Math.Round(origin.Y),
                (int)Math.Ceiling(window.IslandBody.ActualWidth),(int)Math.Ceiling(window.IslandBody.ActualHeight)));
            var pixels=new byte[cropped.PixelWidth*cropped.PixelHeight*4];cropped.CopyPixels(pixels,cropped.PixelWidth*4,0);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(cropped));
            using var stream=File.Create(Path.Combine(folder,name+".png"));encoder.Save(stream);return pixels;
        }
        int BoostedPixels(byte[] bright,byte[] plain,int channel) => Enumerable.Range(0,bright.Length/4)
            .Count(index=>bright[index*4+channel]-plain[index*4+channel]>=25);
        try
        {
            var legacy=JsonSerializer.Deserialize<IslandOptions>("{\"BatteryRippleEnabled\":false,\"BatteryNotifyEnabled\":true,\"GreetingEnabled\":false}")!.Normalized();
            options.CopyFrom(legacy);MotionPolicy.Apply(true,false);layers.HideAll();
            Check(!JsonSerializer.Serialize(options).Contains("BatteryRippleEnabled"),"旧配置中的关闭波纹值被忽略，重新保存也不再写入废弃选项");
            PrivateCall(window,"ShowBatteryNotify",new BatteryStatusInfo(68,true,BatteryChargeType.StandardAC,BatteryPowerEvent.PluggedIn,null,IsOnAcPower:true));
            PumpAnimations(120);
            Check(AgentField<bool>(wave,"_animating") && AgentField<bool>(wave,"_isCharging"),"真实接电在旧关闭配置下仍启动绿色波纹");
            Capture("charging-origin",.10);Capture("charging-spread",.55);
            var charging=Capture("charging",.30);var chargingPlain=Capture("charging-without-wave",1);
            int green=BoostedPixels(charging,chargingPlain,1);
            Check(green>180,$"经过岛体材质叠加后仍有明显绿色亮度变化（{green}像素提升至少25）");
            PrivateCall(window,"ShowBatteryNotify",new BatteryStatusInfo(68,false,BatteryChargeType.Unknown,BatteryPowerEvent.Unplugged,null,IsOnAcPower:false));
            PumpAnimations(120);
            Check(AgentField<bool>(wave,"_animating") && !AgentField<bool>(wave,"_isCharging"),"真实断电同样始终启动橙色波纹");
            Capture("unplugged-origin",.10);Capture("unplugged-spread",.55);
            var unplugged=Capture("unplugged",.30);var unpluggedPlain=Capture("unplugged-without-wave",1);
            int amber=BoostedPixels(unplugged,unpluggedPlain,2);
            Check(amber>180,$"断电橙色波纹经过材质叠加后也明显可见（{amber}像素提升至少25）");
            wave.Trigger(false);PumpAnimations(2200);
            Check(!AgentField<bool>(wave,"_animating") && AgentField<double>(wave,"_progress")==1,"增强波纹约两秒后停止并清空，不增加常驻帧循环");
        }
        finally {wave.Stop();layers.Hide("battery");MotionPolicy.Apply(reduced,saving);}
    }
}
