using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Plugins;
using alpha_island.Plugins.Isolation;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Point = System.Windows.Point;

internal static partial class Program
{
    private static void AiSettingsLayoutChecks(alpha_island.MainWindow window,string root)
    {
        PluginScrollingChecks();
        string target=Path.Combine(root,"api-settings-worker");Directory.CreateDirectory(target);
        string source=Path.GetFullPath($"examples/ApiChatPlugin/bin/{TestBuildConfiguration}/net8.0-windows");
        foreach(string file in Directory.GetFiles(source).Where(f=>f.EndsWith(".dll")||f.EndsWith(".deps.json"))) File.Copy(file,Path.Combine(target,Path.GetFileName(file)));
        File.Copy(Path.GetFullPath("examples/ApiChatPlugin/plugin.json"),Path.Combine(target,"plugin.json"));
        var manifest=PluginManifest.Read(Path.Combine(target,"plugin.json"));
        using var session=new PluginProcessSession(window.Dispatcher);
        AwaitPlugin(session.StartAsync(new(target,manifest.Assembly,manifest.EntryType,manifest.Id,Path.Combine(root,"api-data"),
            new Dictionary<string,JsonElement>()),CancellationToken.None));
        try
        {
        foreach(int width in new[]{480,560,720})
        {
            var frame=AwaitPlugin(session.Request<RemoteFrame>("frame",new RemoteViewRequest("$settings",width,650)));
            var remote=new RemotePluginView("$settings",_=>Task.CompletedTask,error=>throw error);remote.Apply(frame);
            var panel=new Border {Background=new SolidColorBrush(System.Windows.Media.Color.FromRgb(28,28,28)),Child=remote};
            SaveConversationPreview(panel,$"docs/screens/v113-ai-settings-{width}.png",width,(int)Math.Ceiling(frame.Height));
            var buttons=frame.Elements.Where(e=>e.Kind=="button").ToArray();
            RemoteVisual Control(string name)=>frame.Elements.Single(e=>e.Name==name);
            var save=Control("SaveAiKeyButton");var clear=Control("ClearAiKeyButton");
            var connect=Control("ConnectAiModelsButton");var test=Control("TestAiModelButton");
            Check(Math.Abs(save.Y-clear.Y)<1 && clear.X-save.X-save.Width>=7, $"{width}px：保存和删除密钥并排且保留间距");
            Check(Math.Abs(connect.Y-test.Y)<1 && test.X-connect.X-connect.Width>=7, $"{width}px：连接和测试模型并排且保留间距");
            Check(save.Y>=Control("AiApiKeyBox").Y+Control("AiApiKeyBox").Height+7, $"{width}px：密钥输入和操作按钮不挤在一起");
            Check(connect.Y>=Control("AiModelBox").Y+Control("AiModelBox").Height+7, $"{width}px：模型选择和操作按钮有足够留白");
            Check(buttons.All(e=>e.Height>=32 && e.X>=0 && e.X+e.Width<=width), $"{width}px：按钮尺寸统一且不会超出设置页");
            var importPersona=Control("AiPersonaImportButton"); var openPersonas=Control("AiPersonaFolderButton");
            Check(Math.Abs(importPersona.Y-openPersonas.Y)<1 && openPersonas.X-importPersona.X-importPersona.Width>=7, $"{width}px：人格导入与文件夹按钮并排且有间距");
            Check(importPersona.ClipHeight>=importPersona.Height-1 && openPersonas.ClipHeight>=openPersonas.Height-1, $"{width}px：人格操作按钮完整可见");
            var prompt=Control("AiSystemPromptBox");
            Console.WriteLine($"AI settings {width}px: height={frame.Height:F1}, promptBottom={prompt.Y+prompt.Height:F1}, promptClip={prompt.ClipHeight:F1}/{prompt.Height:F1}");
            Check(prompt.ClipHeight>=prompt.Height-1 && prompt.Y+prompt.Height+12<=frame.Height, $"{width}px：人格提示词和卡片底部完整显示");
            Check(frame.Height<=650, $"{width}px：扩展设置不超过宿主可滚动视口上限");
            AwaitPlugin(session.Request<bool>("input",new RemoteCommand("$settings","wheel",Number:-3000,X:width/2d,Y:frame.Height-20)));
            var bottom=AwaitPlugin(session.Request<RemoteFrame>("frame",new RemoteViewRequest("$settings",width,650)));
            var guard=bottom.Elements.Single(e=>e.Name=="AiSensitiveSendGuardEnabledToggle");
            Check(guard.ClipHeight>=guard.Height-1 && guard.Y>=0 && guard.Y+guard.Height<=bottom.Height, $"{width}px：滚轮可到达最后一个新增保护开关，未被截断");
            AwaitPlugin(session.Request<bool>("input",new RemoteCommand("$settings","wheel",Number:3000,X:width/2d,Y:bottom.Height-20)));
            var toggle=(CheckBox)remote.FindName("AiEnabledToggle");
            var icon=new DrawingVisual(); using(var draw=icon.RenderOpen()) draw.DrawRectangle(new VisualBrush(toggle){AutoLayoutContent=false,Stretch=Stretch.Fill},null,new Rect(0,0,toggle.ActualWidth,toggle.ActualHeight));
            int w=(int)Math.Ceiling(toggle.ActualWidth),h=(int)Math.Ceiling(toggle.ActualHeight);
            var bitmap=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);bitmap.Render(icon);var pixels=new byte[w*h*4];bitmap.CopyPixels(pixels,w*4,0);
            int min=w,max=-1;
            for(int y=0;y<h;y++)for(int x=0;x<w;x++)if(pixels[(y*w+x)*4+3]>12){min=Math.Min(min,x);max=Math.Max(max,x);}
            Check(max-min+1>=32,$"{width}px：隔离设置页保留完整开关外观，不退化为白色复选框");
            Check(frame.Elements.Where(e=>e.Kind=="password").All(e=>e.Text.Length==0), "布局快照不传输明文密钥");
            remote.Close();
        }
        AwaitPlugin(session.Request<RemoteFrame>("frame",new RemoteViewRequest("$settings",500,650,false)));
        }
        finally { session.Dispose(); AwaitPlugin(session.Completion.ContinueWith(_=>true)); }
    }
}
