using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void BatteryConnectorChecks(alpha_island.MainWindow window, Grid root)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var icon = window.BatteryIcon;
        int tier = MotionPolicy.RenderTier;
        bool reduced = MotionPolicy.ReduceMotion, saving = MotionPolicy.PowerSave;
        var progress = (DependencyProperty)typeof(SemanticIcon).GetField("ProgressProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var output = Path.GetFullPath("bin/battery-connector-previews"); Directory.CreateDirectory(output);
        (double Center, int Cable, byte[] Pixels) Frame(double? value, string name)
        {
            // Hold the real production animation at a deterministic frame, retaining its source/target state.
            if(value.HasValue) { icon.BeginAnimation(progress, null); icon.SetValue(progress, value.Value); }
            root.UpdateLayout();
            window.BatteryRipple.Stop(); // Isolate connector geometry from the brighter background effect.
            const int scale = 3;
            var full = new RenderTargetBitmap((int)root.ActualWidth*scale,(int)root.ActualHeight*scale,96*scale,96*scale,PixelFormats.Pbgra32);
            full.Render(root);
            var origin = icon.TransformToAncestor(root).Transform(new System.Windows.Point(0,0));
            int width = (int)Math.Ceiling(icon.ActualWidth*scale), height = (int)Math.Ceiling(icon.ActualHeight*scale);
            var bitmap = new CroppedBitmap(full,new Int32Rect((int)Math.Round(origin.X*scale),(int)Math.Round(origin.Y*scale),width,height));
            var pixels = new byte[width*height*4]; bitmap.CopyPixels(pixels,width*4,0);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var stream=File.Create(Path.Combine(output,name+".png"))) encoder.Save(stream);
            double sum=0; int count=0, cable=0;
            for(int y=0;y<height;y++) for(int x=0;x<width;x++)
            {
                int alpha=pixels[(y*width+x)*4+3];
                int offset=(y*width+x)*4;
                if(alpha<100 || pixels[offset+1]<140 || pixels[offset+1]-pixels[offset+2]<50) continue;
                double px=(x+.5)*32/width, py=(y+.5)*32/height;
                if(py>=8.0 && py<=10.0) {sum+=px;count++;}
                if(px<5 && py>=11 && py<=21) cable++;
            }
            return (count==0?double.NaN:sum/count,cable,pixels);
        }
        void Start(bool charging)
        {
            PrivateCall(window,"PreviewBatteryAnimation",charging?BatteryPowerEvent.PluggedIn:BatteryPowerEvent.Unplugged);
            PumpAnimations(550); root.UpdateLayout();
            Check(ActionIconMotion.GetIsAnimating(icon), "真实预览入口启动电池内部动画");
        }
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false,false);
            options.BatteryNotifySeconds=5;
            layers.HideAll(); PrivateCall(window,"SetState",IslandState.Bar,true);
            Start(true);
            var begin=Frame(0,"connect-0"); var moving=Frame(.2,"connect-1");
            var inserting=Frame(.65,"connect-2"); var end=Frame(1,"connect-3");
            Console.WriteLine($"Battery centers: {begin.Center:0.00} -> {moving.Center:0.00} -> {inserting.Center:0.00} -> {end.Center:0.00}; cable ink: {begin.Cable}/{moving.Cable}/{inserting.Cable}/{end.Cable}");
            Check(Math.Abs(begin.Center-16)<1.6 && end.Center-begin.Center>2.5,
                "接电动画从居中电池开始，电池本体确实向右移动");
            Check(moving.Center>begin.Center+.8 && moving.Center<end.Center-.4 && moving.Cable==0,
                "电池先移动，移动早期没有提前显示插头");
            Check(Math.Abs(inserting.Center-end.Center)<.8 && inserting.Cable>0 && end.Cable>0,
                "电池让出空间后左侧电线与插头才进入");
            ActionIconMotion.Stop(icon);
            Start(true);
            var repeatBegin=Frame(0,"connect-repeat-0"); var repeatEnd=Frame(1,"connect-repeat-1");
            Check(repeatEnd.Center-repeatBegin.Center>2.5 && repeatBegin.Cable==0,
                "重复点击接电测试也完整重播，不被上次充电状态锁成静态");
            ActionIconMotion.Stop(icon);
            Start(false);
            var unplugBegin=Frame(0,"disconnect-0"); var pulling=Frame(.35,"disconnect-1");
            var returning=Frame(.8,"disconnect-2"); var unplugEnd=Frame(1,"disconnect-3");
            Check(unplugBegin.Cable>0 && returning.Cable==0 && unplugEnd.Cable==0,
                "断电先抽出电线与插头，随后才恢复无插头图标");
            Check(Math.Abs(pulling.Center-unplugBegin.Center)<.8 && returning.Center<unplugBegin.Center-.8
                && Math.Abs(unplugEnd.Center-16)<1.6,
                "断电电池先保持右侧位置，拔出后再回到正中间");
            ActionIconMotion.Stop(icon);
            Start(false);
            Check(Frame(0,"disconnect-repeat-0").Cable>0 && Frame(1,"disconnect-repeat-1").Cable==0,
                "重复点击断电测试也能播放反向过渡");
            ActionIconMotion.Stop(icon); window.BatteryRipple.Stop(); layers.Hide("battery");
            PrivateCall(window,"SetState",IslandState.Bar,true);
            PrivateCall(window,"ShowBatteryNotify",new BatteryStatusInfo(68,true,BatteryChargeType.StandardAC,BatteryPowerEvent.PluggedIn,null,IsOnAcPower:true));
            Check(icon.DisplayedState==0, "展开期间保持居中电池的起始状态，避免先闪出已插线图标");
            PumpAnimations(550);
            Check(AgentField<bool>(window.BatteryRipple,"_animating"), "真实接电总会播放背景波纹");
            var liveEarly=Frame(null,"live-early");
            PumpAnimations(600); var liveLate=Frame(null,"live-late");
            Check(liveLate.Center-liveEarly.Center>2 && liveLate.Cable>liveEarly.Cable,
                "真实WPF时钟推进时电池与插头确实移动，不依赖测试手动修改进度");
            Check(!AgentField<bool>(window.BatteryRipple,"_animating"), "停止背景预览后电池连接时钟仍独立完成");
            PumpAnimations(1000);
            Check(!ActionIconMotion.GetIsAnimating(icon) && !SemanticProgressHasClock(icon), "电池动画完成后释放时钟并保持稳定充电图标");
            layers.Hide("battery"); PrivateCall(window,"SetState",IslandState.Bar,true);
            PrivateCall(window,"PreviewBatteryAnimation",BatteryPowerEvent.PluggedIn);
            MotionPolicy.Apply(true,false); PumpAnimations(600);
            Check(icon.DisplayedState==1 && !ActionIconMotion.GetIsAnimating(icon),
                "展开途中开启减弱动效也直接到最终图标，不留在待播放起点");
        }
        finally
        {
            ActionIconMotion.Stop(icon); window.BatteryRipple.Stop(); layers.Hide("battery");
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduced,saving);
        }
    }
}
