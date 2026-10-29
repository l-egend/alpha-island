using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Plugins;
using alpha_island.Plugins.Isolation;

internal static partial class Program
{
    private static void EarbudIsolatedLiveChecks(alpha_island.MainWindow window, string root)
    {
        string source=ExternalPluginSource($"OppoPodsPlugin/bin/{TestBuildConfiguration}/net8.0-windows10.0.19041.0");
        string target=Path.Combine(root,"oppo.pods");Directory.CreateDirectory(target);
        foreach(string file in Directory.GetFiles(source).Where(path=>path.EndsWith(".dll")||path.EndsWith(".deps.json")||path.EndsWith("plugin.json")))
            File.Copy(file,Path.Combine(target,Path.GetFileName(file)));
        var manifest=PluginManifest.Read(Path.Combine(target,"plugin.json"));
        using var session=new PluginProcessSession(window.Dispatcher);
        session.Event+=message=>{if(message.Name=="log")Console.WriteLine("PLUGIN: "+System.Text.RegularExpressions.Regex.Replace(message.Text,@"(?i)\b[0-9a-f]{12}\b","<device>"));};
        try
        {
            var settings=new Dictionary<string,JsonElement>();
            var registry=AwaitPlugin(session.StartAsync(new(target,manifest.Assembly,manifest.EntryType,manifest.Id,Path.Combine(root,"data"),settings),CancellationToken.None));
            string page=registry.Pages.Single().Key;
            RemoteFrame Frame()=>AwaitPlugin(session.Request<RemoteFrame>("frame",new RemoteViewRequest(page,414,294)));
            void Click(RemoteFrame frame,string text)
            {
                var button=frame.Elements.Single(item=>item.Kind=="button"&&item.Text==text);
                Check(AwaitPlugin(session.Request<bool>("input",new RemoteCommand(page,"click",button.Id))),"独立进程接受真实耳机按钮："+text);
            }
            var current=Frame();Click(current,"刷新");
            var deadline=DateTime.UtcNow.AddSeconds(15);
            do{PumpAnimations(150);current=Frame();}while(!current.Elements.Any(item=>item.Kind=="choice"&&item.Choices?.Any(name=>name.Contains("Clip2"))==true)&&DateTime.UtcNow<deadline);
            var device=current.Elements.FirstOrDefault(item=>item.Kind=="choice"&&item.Choices?.Any(name=>name.Contains("Clip2"))==true);
            Check(device!=null,"独立进程枚举到已连接的 Enco Clip2");
            int index=Array.FindIndex(device!.Choices!,name=>name.Contains("Clip2"));
            Check(AwaitPlugin(session.Request<bool>("input",new RemoteCommand(page,"choice",device.Id,Number:index))),"选择实际 Enco Clip2 条目");
            current=Frame();Click(current,"连接");
            bool Connected(RemoteFrame frame)=>frame.Elements.Any(item=>item.Kind=="text"&&item.Text=="已连接");
            deadline=DateTime.UtcNow.AddSeconds(20);
            do{PumpAnimations(150);current=Frame();}while(!Connected(current)&&DateTime.UtcNow<deadline);
            Console.WriteLine("ISOLATED: "+string.Join(" | ",current.Elements.Where(item=>item.Kind=="text").Select(item=>item.Text).Take(10)));
            Check(Connected(current),"通过实际隔离按钮建立控制连接");
            for(int i=0;i<18;i++){PumpAnimations(1000);current=Frame();Check(Connected(current),"隔离控制通道保持连接，第"+(i+1)+"秒");}
            Check(current.Elements.Count(item=>item.Kind=="text"&&item.Text.EndsWith("%"))>=2,"隔离卡片持续显示双耳电量");
            int process=session.ProcessId;
            Check(!current.Elements.Single(item=>item.Kind=="button"&&item.Text=="连接").Enabled,"已连接目标的连接按钮不会重复创建通道");
            Click(current,"断开");
            deadline=DateTime.UtcNow.AddSeconds(5);
            do{PumpAnimations(100);current=Frame();}while(Connected(current)&&DateTime.UtcNow<deadline);
            Check(!Connected(current),"主动关闭控制连接后卡片立即撤下旧电量");
            Click(current,"连接");
            deadline=DateTime.UtcNow.AddSeconds(20);
            do{PumpAnimations(150);current=Frame();}while(!Connected(current)&&DateTime.UtcNow<deadline);
            Check(Connected(current)&&session.ProcessId==process,"同一插件进程释放旧流后能重新连接，不需要重启进程");
            PumpAnimations(2000);current=Frame();
            Check(current.Elements.Count(item=>item.Kind=="text"&&item.Text.EndsWith("%"))>=2,"重新连接后恢复双耳实时电量");
            var remote=new RemotePluginView(page,_=>Task.CompletedTask,_=>{});remote.Apply(current);
            SaveConversationPreview(new Border{Child=remote,Background=new SolidColorBrush(System.Windows.Media.Color.FromRgb(24,30,42))},"bin/peripheral-previews127/earbuds-isolated-live.png",414,294);
        }
        finally{session.Dispose();AwaitPlugin(session.Completion.ContinueWith(_=>true));}
    }
}
